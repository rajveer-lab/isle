using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Isle
{
    /// <summary>
    /// Your Android phone (Pixel), through the apps already on this PC:
    ///  - KDE Connect: is the phone reachable, ring it, send files / clipboard, phone clipboard arriving on the PC
    ///  - Bluetooth: connected / not connected (the link Phone Link uses for calls)
    ///  - Phone Link / Quick Share / KDE Connect SMS: opened from the island
    /// </summary>
    sealed class PhoneService : IDisposable
    {
        const string PhoneLinkApp = "Microsoft.YourPhone_8wekyb3d8bbwe!App";
        const string QuickShareApp = "NearbyShare_21hpf16v5xp10!NearbyShare";

        readonly Dispatcher _ui;
        readonly DispatcherTimer _poll, _offlineGrace;
        string _bin;
        BluetoothDevice _bt;
        bool _polling, _started, _lastOnline;
        DateTime _lastPoll;

        public string Name { get; private set; } = "";
        public string KdeId { get; private set; } = "";
        public bool HasKdeConnect => _bin != null && KdeId.Length > 0;
        public bool Reachable { get; private set; }
        public bool BluetoothConnected { get; private set; }
        public IsleLink Link { get; private set; }
        public bool LinkConnected => Link != null && Link.Connected;
        public bool Online => Reachable || BluetoothConnected || LinkConnected;
        public bool Configured => Name.Length > 0;
        /// <summary>Ring / clipboard actions are possible (Isle Link connected, or KDE Connect set up).</summary>
        public bool CanAct => LinkConnected || HasKdeConnect;
        public int Battery => LinkConnected ? Link.Battery : -1;
        public bool BatteryCharging => LinkConnected && Link.Charging;
        public string LastClip { get; private set; } = "";
        public DateTime LastClipAt { get; private set; }
        public readonly List<KeyValuePair<DateTime, string>> ClipHistory = new List<KeyValuePair<DateTime, string>>();

        /// <summary>UI thread: the phone came online (true) or went away (false). Not raised for the first reading.</summary>
        public event Action<bool> OnlineChanged;
        /// <summary>UI thread: anything shown in the phone row changed.</summary>
        public event Action StatusChanged;
        /// <summary>UI thread: the phone's clipboard just landed on this PC (preview text).</summary>
        public event Action<string> ClipboardFromPhone;

        public PhoneService(Dispatcher ui)
        {
            _ui = ui;
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(40) };
            _poll.Tick += (s, e) => Refresh();
            // don't announce "disconnected" for a blip: wait a little before going offline
            _offlineGrace = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
            _offlineGrace.Tick += (s, e) => { _offlineGrace.Stop(); Evaluate(true); };
        }

        public void Start(Settings cfg)
        {
            if (!cfg.PhoneEnabled) return;
            if (cfg.IsleLink)
            {
                Link = new IsleLink(_ui);
                Link.StatusChanged += () =>
                {
                    if (LinkConnected && Link.DeviceName.Length > 0 && Name.Length == 0) Name = Link.DeviceName;
                    StatusChanged?.Invoke();
                };
                Link.ConnectionChanged += c => Evaluate(false);
                Link.ClipboardReceived += OnLinkClipboard;
                _ = Link.StartAsync();
            }
            try
            {
                if (cfg.UseKdeConnect) FindKdeConnect();
                if (cfg.PhoneName.Length > 0) Name = cfg.PhoneName;
                if (HasKdeConnect && cfg.StartKdeConnect) EnsureDaemon();
            }
            catch (Exception ex) { App.Log(ex); }
            if (HasKdeConnect) { _poll.Start(); Refresh(); }
            _ = WatchBluetoothAsync();
            _started = true;
            StatusChanged?.Invoke();
        }

        // ---------------- discovery ----------------

        void FindKdeConnect()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var dir in new[]
            {
                Path.Combine(local, "Programs", "KDE Connect", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KDE Connect", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "KDE Connect", "bin"),
            })
            {
                if (File.Exists(Path.Combine(dir, "kdeconnect-cli.exe"))) { _bin = dir; break; }
            }

            // paired devices: [id] name=... type=phone
            string trusted = Path.Combine(local, "kdeconnect", "trusted_devices");
            if (!File.Exists(trusted)) return;
            string id = null, name = null, type = null;
            var devices = new List<(string Id, string Name, string Type)>();
            foreach (var raw in File.ReadAllLines(trusted))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (id != null) devices.Add((id, name ?? "", type ?? ""));
                    id = line.Substring(1, line.Length - 2); name = null; type = null;
                }
                else if (line.StartsWith("name=")) name = line.Substring(5);
                else if (line.StartsWith("type=")) type = line.Substring(5);
            }
            if (id != null) devices.Add((id, name ?? "", type ?? ""));
            var phone = devices.FirstOrDefault(d => d.Type == "phone");
            if (phone.Id == null) phone = devices.FirstOrDefault();
            if (phone.Id == null) return;
            KdeId = phone.Id;
            Name = phone.Name;
        }

        void EnsureDaemon()
        {
            if (Process.GetProcessesByName("kdeconnectd").Length > 0) return;
            string daemon = Path.Combine(_bin, "kdeconnectd.exe");
            if (!File.Exists(daemon)) return;
            Process.Start(new ProcessStartInfo(daemon) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _bin });
        }

        // ---------------- status ----------------

        /// <summary>Asks KDE Connect whether the phone is reachable (runs off the UI thread).</summary>
        public void Refresh(bool searchNetwork = false)
        {
            if (!HasKdeConnect || _polling) return;
            _polling = true;
            _lastPoll = DateTime.Now;
            Task.Run(() =>
            {
                if (searchNetwork) Cli("--refresh");
                var r = Cli("-a", "--id-only");
                return r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(l => l.Trim() == KdeId);
            }).ContinueWith(t =>
            {
                _polling = false;
                bool reachable = t.Status == TaskStatus.RanToCompletion && t.Result;
                if (reachable != Reachable) { Reachable = reachable; Evaluate(false); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void RefreshIfStale()
        {
            if ((DateTime.Now - _lastPoll).TotalSeconds > 12) Refresh();
        }

        async Task WatchBluetoothAsync()
        {
            try
            {
                var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
                var info = infos.FirstOrDefault(d => Name.Length > 0 && d.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
                        ?? infos.FirstOrDefault(d => d.Name.IndexOf("Pixel", StringComparison.OrdinalIgnoreCase) >= 0);
                if (info == null) return;
                if (Name.Length == 0) Name = info.Name;
                if (Link != null) { Link.PhoneName = info.Name; Link.StartDialing(info.Id); }
                _bt = await BluetoothDevice.FromIdAsync(info.Id);
                if (_bt == null) return;
                BluetoothConnected = _bt.ConnectionStatus == BluetoothConnectionStatus.Connected;
                _lastOnline = Online;
                StatusChanged?.Invoke();
                _bt.ConnectionStatusChanged += (s, e) => _ui.BeginInvoke(new Action(() =>
                {
                    bool c = s.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    if (c == BluetoothConnected) return;
                    BluetoothConnected = c;
                    Evaluate(false);
                    if (c) { Refresh(); _ = Link?.DialAsync(); }
                }));
            }
            catch (Exception ex) { App.Log(ex); }
        }

        void Evaluate(bool graceOver)
        {
            bool online = Online;
            if (!online && _lastOnline && !graceOver)
            {
                // looked offline: confirm after a short grace period
                if (!_offlineGrace.IsEnabled) _offlineGrace.Start();
                StatusChanged?.Invoke();
                return;
            }
            if (online) _offlineGrace.Stop();
            if (online != _lastOnline)
            {
                _lastOnline = online;
                if (_started) OnlineChanged?.Invoke(online);
            }
            StatusChanged?.Invoke();
        }

        public string StatusText
        {
            get
            {
                if (!Configured) return "";
                if (LastClip.Length > 0 && (DateTime.Now - LastClipAt).TotalMinutes < 10)
                    return "Copied from phone: “" + Preview(LastClip, 40) + "”  ·  click to copy again";
                if (LinkConnected)
                    return "Connected  ·  Isle Link (Bluetooth)" + (Battery >= 0 ? "  ·  " + Battery + "%" + (BatteryCharging ? " charging" : "") : "");
                if (Reachable && BluetoothConnected) return "Connected  ·  Wi-Fi + Bluetooth";
                if (Reachable) return "Connected  ·  KDE Connect";
                if (BluetoothConnected) return "Connected  ·  Bluetooth";
                return HasKdeConnect ? "Not connected  ·  open KDE Connect on the phone" : "Not connected  ·  Bluetooth off or out of range";
            }
        }

        // ---------------- clipboard from the phone ----------------

        /// <summary>Call on WM_CLIPBOARDUPDATE. If KDE Connect put it there, it came from the phone.</summary>
        public void OnClipboardUpdate()
        {
            if (!HasKdeConnect) return;
            IntPtr owner = Native.GetClipboardOwner();
            if (owner == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(owner, out uint pid);
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch { return; }
            if (!proc.StartsWith("kdeconnect", StringComparison.OrdinalIgnoreCase)) return;

            _ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                string text = null;
                for (int i = 0; i < 3 && text == null; i++)
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText();
                        else if (System.Windows.Clipboard.ContainsImage()) text = "Image";
                        else if (System.Windows.Clipboard.ContainsFileDropList()) text = "Files";
                        else text = "";
                    }
                    catch { System.Threading.Thread.Sleep(40); }
                }
                if (string.IsNullOrEmpty(text)) text = "Clipboard";
                Remember(text);
                ClipboardFromPhone?.Invoke(text);
                StatusChanged?.Invoke();
            }));
        }

        /// <summary>Text copied on the phone arrived over Isle Link: put it on this PC's clipboard, ready for Ctrl+V.</summary>
        void OnLinkClipboard(string text)
        {
            for (int i = 0; i < 4; i++)
            {
                try { System.Windows.Clipboard.SetText(text); break; }
                catch { System.Threading.Thread.Sleep(50); }
            }
            Remember(text);
            ClipboardFromPhone?.Invoke(text);
            StatusChanged?.Invoke();
        }

        void Remember(string text)
        {
            LastClip = text;
            LastClipAt = DateTime.Now;
            if (text == "Image" || text == "Files" || text == "Clipboard") return;
            ClipHistory.RemoveAll(c => c.Value == text);
            ClipHistory.Insert(0, new KeyValuePair<DateTime, string>(DateTime.Now, text));
            while (ClipHistory.Count > 10) ClipHistory.RemoveAt(ClipHistory.Count - 1);
        }

        public void ClearHistory()
        {
            ClipHistory.Clear();
            LastClip = "";
            StatusChanged?.Invoke();
        }

        public static bool CopyText(string text)
        {
            try { System.Windows.Clipboard.SetText(text); return true; }
            catch { return false; }
        }

        /// <summary>Puts the last thing copied on the phone back on this PC's clipboard.</summary>
        public bool CopyLastClipAgain()
        {
            if (LastClip.Length == 0 || LastClip == "Image" || LastClip == "Files" || LastClip == "Clipboard") return false;
            try { System.Windows.Clipboard.SetText(LastClip); return true; }
            catch { return false; }
        }

        public static string Preview(string text, int max)
        {
            string t = string.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return t.Length <= max ? t : t.Substring(0, max - 1) + "…";
        }

        // ---------------- actions ----------------

        /// <summary>Asks the phone app to start screen mirroring (it shows a notification to tap).</summary>
        public bool RequestMirror() => LinkConnected && Link.Send("MIRRORREQ");

        public Task<bool> Ring() =>LinkConnected ? Task.FromResult(Link.Ring()) : Act("--ring");

        public Task<bool> SendClipboard()
        {
            if (LinkConnected)
            {
                string text = "";
                try { if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText(); } catch { }
                return Task.FromResult(Link.SendClipboard(text));
            }
            return Act("--send-clipboard");
        }

        /// <summary>Isle Link first (Bluetooth), then KDE Connect (Wi-Fi). Returns how many files arrived; 0 = use Quick Share.</summary>
        public async Task<int> SendFiles(IEnumerable<string> paths, Action<string> progress = null)
        {
            var files = paths.Where(File.Exists).ToList();
            int sent = 0;
            if (LinkConnected)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    string label = Path.GetFileName(files[i]) + (files.Count > 1 ? " (" + (i + 1) + "/" + files.Count + ")" : "");
                    if (await Link.SendFileAsync(files[i], pct => progress?.Invoke(pct + "%  ·  Sending " + label))) sent++;
                }
                return sent;
            }
            if (HasKdeConnect && Reachable)
                foreach (var p in files) if (await Act("--share", p)) sent++;
            return sent;
        }

        /// <summary>No direct link: open Quick Share and show the file(s), ready to drag in.</summary>
        public void QuickShareFallback(IList<string> paths)
        {
            OpenQuickShare();
            var first = paths.FirstOrDefault(File.Exists);
            if (first != null) Start("explorer.exe", "/select,\"" + first + "\"");
        }

        Task<bool> Act(params string[] args)
        {
            if (!HasKdeConnect) return Task.FromResult(false);
            string id = KdeId;
            return Task.Run(() =>
            {
                // make sure it's actually there first (and wake the connection up)
                var check = Cli("-a", "--id-only");
                if (!check.Output.Contains(id))
                {
                    Cli("--refresh");
                    System.Threading.Thread.Sleep(2500);
                    check = Cli("-a", "--id-only");
                    if (!check.Output.Contains(id)) return false;
                }
                var all = new List<string> { "-d", id };
                all.AddRange(args);
                return Cli(all.ToArray()).ExitCode == 0;
            });
        }

        (int ExitCode, string Output) Cli(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(_bin, "kdeconnect-cli.exe"))
                {
                    Arguments = string.Join(" ", args.Select(a => a.Contains(" ") || a.Contains("\\") ? "\"" + a + "\"" : a)),
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _bin,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return (-1, output); }
                    err.Wait(2000);
                    return (p.ExitCode, output);
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return (-1, "");
            }
        }

        public void OpenMessages()
        {
            string sms = _bin != null ? Path.Combine(_bin, "kdeconnect-sms.exe") : null;
            if (sms != null && File.Exists(sms) && Reachable) Start(sms, "");
            else OpenPhoneLink();
        }

        public void OpenPhoneLink() => Start("explorer.exe", "shell:AppsFolder\\" + PhoneLinkApp);
        /// <summary>Google's own Find My Device (rings the phone at full volume, no extra app needed).</summary>
        public void OpenFindMyDevice() => Start("https://www.google.com/android/find", "");
        public void OpenQuickShare() => Start("explorer.exe", "shell:AppsFolder\\" + QuickShareApp);

        static void Start(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
        }

        // ---------- debug: --snapshot renders the phone row without a phone ----------
        public void Demo(string name, string clip)
        {
            Name = name;
            Reachable = true;
            BluetoothConnected = true;
            if (clip != null) { LastClip = clip; LastClipAt = DateTime.Now; }
            StatusChanged?.Invoke();
        }

        public void Dispose()
        {
            _poll.Stop();
            _offlineGrace.Stop();
            Link?.Dispose();
        }
    }
}
