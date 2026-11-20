using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Isle
{
    enum CallPhase { None, Incoming, Active }

    sealed class CallInfo
    {
        public CallPhase Phase;
        public string App = "", Caller = "", Key = "";
        public DateTime Started = DateTime.Now;
        public bool Video;
        public BitmapSource Icon;
        public string[] Processes = new string[0];
        public IntPtr SourceWindow;          // toast / call window we found it in
        public object Accept, Decline;       // UI Automation buttons in that window (typed loosely so UIA loads lazily)
    }

    sealed class AppUse
    {
        public string Key = "", Name = "", ExePath = "";
        public bool Packaged, IsCallApp;
        public string[] Processes = new string[0];
        public DateTime Start;
    }

    /// <summary>
    /// Calls and privacy indicators, built on the same data Windows uses for its own mic/camera tray icon
    /// (CapabilityAccessManager): an app that is holding the microphone right now has LastUsedTimeStop = 0.
    /// A call app holding the mic = an ongoing call. Incoming calls are read from call toasts / call windows.
    /// </summary>
    sealed class CallService : IDisposable
    {
        const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        sealed class KnownApp { public string Name; public bool Call; public string[] Processes, Fragments; }
        static KnownApp K(string name, bool call, string[] procs, params string[] frags) =>
            new KnownApp { Name = name, Call = call, Processes = procs, Fragments = frags };

        static readonly KnownApp[] Known =
        {
            K("WhatsApp", true, new[] { "WhatsApp", "WhatsApp.Root" }, "whatsapp"),
            K("Telegram", true, new[] { "Telegram" }, "telegram"),
            K("Discord", true, new[] { "Discord" }, "discord"),
            K("Teams", true, new[] { "ms-teams", "Teams" }, "msteams", "ms-teams", "teams"),
            K("Zoom", true, new[] { "Zoom" }, "zoom"),
            K("Skype", true, new[] { "Skype" }, "skype"),
            K("Phone Link", true, new[] { "PhoneExperienceHost" }, "yourphone", "phoneexperiencehost", "crossdevice"),
            K("Signal", true, new[] { "Signal" }, "signal"),
            K("Slack", true, new[] { "slack" }, "slack"),
            K("Messenger", true, new[] { "Messenger" }, "messenger"),
            K("Webex", true, new[] { "CiscoCollabHost", "webex" }, "webex", "ciscocollab"),
            K("Viber", true, new[] { "Viber" }, "viber"),
            K("Chrome", true, new[] { "chrome" }, "chrome"),
            K("Edge", true, new[] { "msedge" }, "msedge"),
            K("Brave", true, new[] { "brave" }, "brave"),
            K("Firefox", true, new[] { "firefox" }, "firefox"),
            K("Opera", true, new[] { "opera" }, "opera"),
        };

        static KnownApp Match(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.ToLowerInvariant();
            return Known.FirstOrDefault(k => k.Fragments.Any(f => t.Contains(f)));
        }

        readonly Dispatcher _ui;
        readonly DispatcherTimer _poll, _debounce;
        RegWatch _micWatch, _camWatch;
        readonly Dictionary<string, BitmapSource> _icons = new Dictionary<string, BitmapSource>();
        readonly Dictionary<uint, string> _pidNames = new Dictionary<uint, string>();
        readonly HashSet<IntPtr> _seenWindows = new HashSet<IntPtr>();
        bool _demo, _reading;
        DateTime _incomingSince, _lastRead;
        int _ticks;

        public CallInfo Call { get; private set; }
        public bool MicInUse { get; private set; }
        public bool CameraInUse { get; private set; }
        public string MicApp { get; private set; } = "";
        public event Action Changed;

        public CallService(Dispatcher ui)
        {
            _ui = ui;
            _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); ScanUsage(); };
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _poll.Tick += (s, e) => Poll();
        }

        public void Start()
        {
            Action kick = () => _ui.BeginInvoke(new Action(() => { _debounce.Stop(); _debounce.Start(); }));
            _micWatch = new RegWatch(ConsentStore + "microphone", kick);
            _camWatch = new RegWatch(ConsentStore + "webcam", kick);
            SensorUse.StartCameraWatch(kick);
            ScanUsage();
            _poll.Start();
        }

        void Publish() => Changed?.Invoke();

        // =====================================================================================
        //  mic / camera usage  ->  privacy dots + ongoing calls
        // =====================================================================================

        static List<AppUse> Scan(string capability)
        {
            var list = new List<AppUse>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(ConsentStore + capability))
                {
                    if (root == null) return list;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        if (name == "NonPackaged")
                        {
                            using (var np = root.OpenSubKey(name))
                                if (np != null) foreach (var sub in np.GetSubKeyNames()) Check(np, sub, false, list);
                        }
                        else Check(root, name, true, list);
                    }
                }
            }
            catch (Exception ex) { App.Log(ex); }
            return list;
        }

        static void Check(RegistryKey parent, string sub, bool packaged, List<AppUse> list)
        {
            using (var k = parent.OpenSubKey(sub))
            {
                if (k == null) return;
                if (!(k.GetValue("LastUsedTimeStop") is long stop) || stop != 0) return;
                if (!(k.GetValue("LastUsedTimeStart") is long start) || start <= 0) return;
                var use = new AppUse { Key = sub, Packaged = packaged, Start = DateTime.FromFileTimeUtc(start).ToLocalTime() };
                if (packaged)
                {
                    string pkg = sub;
                    int us = pkg.LastIndexOf('_');
                    if (us > 0) pkg = pkg.Substring(0, us);
                    int dot = pkg.LastIndexOf('.');
                    use.Name = dot >= 0 ? pkg.Substring(dot + 1) : pkg;
                }
                else
                {
                    use.ExePath = sub.Replace('#', '\\');
                    use.Name = Path.GetFileNameWithoutExtension(use.ExePath);
                }
                if (use.Name.Equals("Isle", StringComparison.OrdinalIgnoreCase)) return;
                var known = Match(sub);
                if (known != null)
                {
                    use.Name = known.Name;
                    use.IsCallApp = known.Call;
                    use.Processes = known.Processes;
                }
                else if (!packaged) use.Processes = new[] { Path.GetFileNameWithoutExtension(use.ExePath) };
                list.Add(use);
            }
        }

        // when each live (process-based) use was first seen, so "call started" times stay put between scans
        readonly Dictionary<string, DateTime> _liveSince = new Dictionary<string, DateTime>();

        /// <summary>
        /// Registry (works on most builds, but newer Windows stopped writing it) merged with what is live right now:
        /// Core Audio capture sessions for the mic, Media Foundation's sensor monitor for the camera.
        /// </summary>
        List<AppUse> Usage(string capability, HashSet<uint> livePids, HashSet<string> seen)
        {
            var list = Scan(capability);
            foreach (uint pid in livePids)
            {
                string exe = SensorUse.ExePath(pid);
                string name = exe.Length > 0 ? Path.GetFileNameWithoutExtension(exe) : ProcessName(pid);
                if (name.Length == 0 || name.Equals("Isle", StringComparison.OrdinalIgnoreCase)) continue;
                var use = new AppUse { Key = exe.Length > 0 ? exe.Replace('\\', '#') : name, Name = name, ExePath = exe, Processes = new[] { name } };
                var known = Match(exe.Length > 0 ? exe : name);
                if (known != null) { use.Name = known.Name; use.IsCallApp = known.Call; use.Processes = known.Processes; }
                if (list.Any(u => u.Name == use.Name)) continue;   // the registry already has it (older Windows)
                seen.Add(use.Key);
                if (!_liveSince.TryGetValue(use.Key, out var since)) _liveSince[use.Key] = since = DateTime.Now;
                use.Start = since;
                list.Add(use);
            }
            return list;
        }

        static string ProcessName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
        }

        void ScanUsage()
        {
            if (_demo) return;
            var seen = new HashSet<string>();
            var mic = Usage("microphone", SensorUse.MicPids(), seen);
            var cam = Usage("webcam", SensorUse.CameraPids(), seen);
            foreach (var k in _liveSince.Keys.Where(k => !seen.Contains(k)).ToList()) _liveSince.Remove(k);
            bool changed = MicInUse != mic.Count > 0 || CameraInUse != cam.Count > 0;
            MicInUse = mic.Count > 0;
            CameraInUse = cam.Count > 0;
            MicApp = mic.OrderByDescending(u => u.Start).Select(u => u.Name).FirstOrDefault() ?? "";

            var callUse = mic.Where(u => u.IsCallApp).OrderByDescending(u => u.Start).FirstOrDefault();
            if (callUse != null) _lastActive[callUse.Name] = DateTime.Now;
            if (callUse != null)
            {
                if (Call == null || Call.Phase != CallPhase.Active || Call.Key != callUse.Key)
                {
                    string caller = Call != null && Call.Phase == CallPhase.Incoming && Call.App == callUse.Name ? Call.Caller : "";
                    Call = new CallInfo
                    {
                        Phase = CallPhase.Active, App = callUse.Name, Key = callUse.Key, Caller = caller,
                        Started = callUse.Start > DateTime.Now ? DateTime.Now : callUse.Start,
                        Icon = IconFor(callUse), Processes = callUse.Processes,
                    };
                    changed = true;
                }
                bool video = cam.Any(c => c.Name == callUse.Name);
                if (video != Call.Video) { Call.Video = video; changed = true; }
            }
            else if (Call != null && Call.Phase == CallPhase.Active)
            {
                _lastActive[Call.App] = DateTime.Now;
                Call = null;
                changed = true;
            }
            if (changed) Publish();
        }

        BitmapSource IconFor(AppUse use)
        {
            if (_icons.TryGetValue(use.Key, out var icon)) return icon;
            try
            {
                if (!use.Packaged && File.Exists(use.ExePath)) icon = Native.ShellIcon(use.ExePath, 64);
                else if (use.Packaged) icon = Native.ShellIcon("shell:AppsFolder\\" + use.Key + "!App", 64);
                if (icon == null) icon = IconFromProcess(use.Processes);
            }
            catch (Exception ex) { App.Log(ex); }
            _icons[use.Key] = icon;
            return icon;
        }

        static BitmapSource IconFromProcess(IEnumerable<string> names)
        {
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try
                    {
                        string path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path)) return Native.ShellIcon(path, 64);
                    }
                    catch { }
                }
            return null;
        }

        // =====================================================================================
        //  incoming calls: call toasts ("New notification") and windows titled like a call
        // =====================================================================================

        static readonly string[] RingWords = { "incoming call", "incoming voice call", "incoming video call", "incoming audio call", "is calling", "calling you", "wants to call", "ringing" };
        // WhatsApp titles its call window just "Voice call" / "Video call" (seen in this PC's log), both while ringing and in a call
        static readonly string[] CallWords = { "voice call", "video call", "audio call" };

        static bool HasRingWords(string text)
        {
            string t = text.ToLowerInvariant();
            return RingWords.Any(w => t.Contains(w));
        }

        static bool HasCallWords(string text)
        {
            string t = text.ToLowerInvariant();
            return CallWords.Any(w => t.Contains(w));
        }

        static bool IsBrowser(KnownApp a) =>
            a.Name == "Chrome" || a.Name == "Edge" || a.Name == "Brave" || a.Name == "Firefox" || a.Name == "Opera";

        readonly Dictionary<IntPtr, DateTime> _notACall = new Dictionary<IntPtr, DateTime>();
        readonly Dictionary<IntPtr, int> _visibleTicks = new Dictionary<IntPtr, int>();
        readonly Dictionary<string, DateTime> _lastActive = new Dictionary<string, DateTime>();

        void Poll()
        {
            if (_demo) return;
            _ticks++;
            ScanUsage();   // live mic sessions have no change event we listen to, so check every second (~2 ms)

            var windows = Native.VisibleWindows();
            LogNewCallWindows(windows);

            if (Call != null && Call.Phase == CallPhase.Active) return;

            if (Call != null && Call.Phase == CallPhase.Incoming)
            {
                bool alive = Native.IsWindow(Call.SourceWindow) && Native.IsWindowVisible(Call.SourceWindow)
                             && (DateTime.Now - _incomingSince).TotalSeconds < 75;
                if (!alive) { Call = null; Publish(); }
                return;
            }

            // candidates: anything titled like a ringing call, or a call app's "Voice call" window / Phone Link popup
            IntPtr candidate = IntPtr.Zero;
            bool weak = false;
            var stillVisible = new HashSet<IntPtr>();
            foreach (var h in windows)
            {
                string title = Native.WindowText(h);
                if (title.Length == 0 || title.Length > 120) continue;
                if (HasRingWords(title)) { candidate = h; weak = false; break; }
                if (candidate != IntPtr.Zero) continue;
                var app = Match(ProcessOf(h));
                if (app == null || !app.Call || IsBrowser(app)) continue;
                bool popup = HasCallWords(title) || (app.Name == "Phone Link" && Native.WindowSize(h, out int ww, out int wh) && ww < 760 && wh < 560);
                if (!popup) continue;
                stillVisible.Add(h);
                _visibleTicks.TryGetValue(h, out int seen);
                _visibleTicks[h] = seen + 1;
                // a call window must stay up for a moment, and not belong to a call that just ended (or is starting outgoing)
                bool justActive = _lastActive.TryGetValue(app.Name, out var last) && (DateTime.Now - last).TotalSeconds < 8;
                if (seen + 1 >= 2 && !justActive) { candidate = h; weak = true; }
            }
            foreach (var h in _visibleTicks.Keys.ToList()) if (!stillVisible.Contains(h)) _visibleTicks.Remove(h);

            if (candidate == IntPtr.Zero) candidate = Native.FindToastWindow();
            if (candidate == IntPtr.Zero || _reading || (DateTime.Now - _lastRead).TotalSeconds < 1.5) return;
            if (_notACall.TryGetValue(candidate, out var checkedAt) && (DateTime.Now - checkedAt).TotalSeconds < 10) return;

            _reading = true;
            _lastRead = DateTime.Now;
            IntPtr target = candidate;
            bool weakTarget = weak;
            string owner = ProcessOf(target);
            Task.Run(() => CallAutomation.Read(target)).ContinueWith(t =>
            {
                _reading = false;
                var read = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (t.Exception != null) App.Log(t.Exception);
                OnRead(target, owner, read, weakTarget);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OnRead(IntPtr window, string ownerProcess, WindowRead read, bool weak)
        {
            if (read == null || (Call != null && Call.Phase == CallPhase.Active)) return;
            string title = Native.WindowText(window);
            bool ring = HasRingWords(title) || read.Lines.Any(HasRingWords);
            bool anyButton = read.Accept != null || read.Decline != null;
            bool buttons = read.Accept != null && read.Decline != null;
            bool isCall = buttons || (ring && anyButton) || (ring && HasRingWords(title)) || (weak && (anyButton || HasCallWords(title)));
            if (!isCall) { _notACall[window] = DateTime.Now; return; }

            // app name: toast "Notification from X" / known app in the text / the window's process
            KnownApp app = null;
            foreach (var line in read.Lines)
            {
                if (line.StartsWith("Notification from ", StringComparison.OrdinalIgnoreCase)) { app = Match(line.Substring(18)); if (app != null) break; }
            }
            if (app == null) app = Match(ownerProcess) ?? read.Lines.Select(Match).FirstOrDefault(a => a != null);
            string appName = app?.Name ?? (ownerProcess.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ? "Call" : ownerProcess);

            string caller = read.Lines
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && l.Length < 60 && !HasRingWords(l) && !HasCallWords(l)
                                     && !l.Equals(title, StringComparison.OrdinalIgnoreCase)
                                     && !l.StartsWith("Notification from", StringComparison.OrdinalIgnoreCase)
                                     && !l.Equals(appName, StringComparison.OrdinalIgnoreCase)
                                     && !l.Equals("New notification", StringComparison.OrdinalIgnoreCase)
                                     && !CallAutomation.IsAccept(l) && !CallAutomation.IsDecline(l)) ?? "";
            if (caller.Length == 0 && !HasRingWords(title) && !HasCallWords(title)) caller = title;

            App.Log(new Exception("Incoming call detected: app=" + appName + " caller=" + caller + " window='" + title + "' lines=" + string.Join(" | ", read.Lines.Take(8))));
            Call = new CallInfo
            {
                Phase = CallPhase.Incoming, App = appName, Caller = caller.Length > 0 ? caller : "Incoming call",
                SourceWindow = window, Accept = read.Accept, Decline = read.Decline,
                Processes = app?.Processes ?? new[] { ownerProcess },
                Icon = IconFromProcess(app?.Processes ?? new[] { ownerProcess }),
            };
            _incomingSince = DateTime.Now;
            Publish();
        }

        string ProcessOf(IntPtr h)
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (_pidNames.TryGetValue(pid, out var name)) return name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
            _pidNames[pid] = name;
            return name;
        }

        /// <summary>Writes the title/class of new windows from call apps to the log, to help tune incoming-call detection.</summary>
        void LogNewCallWindows(List<IntPtr> windows)
        {
            if (_seenWindows.Count > 4000) _seenWindows.Clear();
            bool first = _ticks == 1;
            foreach (var h in windows)
            {
                if (!_seenWindows.Add(h) || first) continue;
                var app = Match(ProcessOf(h));
                if (app == null || !app.Call || app.Name == "Chrome" || app.Name == "Edge" || app.Name == "Brave" || app.Name == "Firefox" || app.Name == "Opera") continue;
                App.Log(new Exception("call-app window: " + app.Name + " class=" + Native.WindowClass(h) + " title='" + Native.WindowText(h) + "'"));
            }
        }

        // =====================================================================================
        //  actions
        // =====================================================================================

        public void Accept()
        {
            var c = Call;
            if (c == null || c.Phase != CallPhase.Incoming) return;
            Act(c, c.Accept, CallAutomation.IsAccept, false);
        }

        public void Decline()
        {
            var c = Call;
            if (c == null || c.Phase != CallPhase.Incoming) return;
            Call = null;
            Publish();
            Act(c, c.Decline, CallAutomation.IsDecline, false);
        }

        public void End()
        {
            var c = Call;
            if (c == null) return;
            Act(c, null, CallAutomation.IsEnd, true);
        }

        void Act(CallInfo c, object element, Func<string, bool> match, bool searchApp)
        {
            if (_demo) return;
            var procs = c.Processes;
            IntPtr source = c.SourceWindow;
            Task.Run(() =>
            {
                if (element != null && CallAutomation.Invoke(element)) return true;
                if (source != IntPtr.Zero && CallAutomation.InvokeIn(new[] { source }, match)) return true;
                return searchApp && CallAutomation.InvokeIn(WindowsOf(procs), match);
            }).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || !t.Result) OpenApp(c);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static List<IntPtr> WindowsOf(IEnumerable<string> processNames)
        {
            var pids = new HashSet<uint>();
            foreach (var n in processNames)
                foreach (var p in Process.GetProcessesByName(n)) pids.Add((uint)p.Id);
            var list = new List<IntPtr>();
            foreach (var h in Native.VisibleWindows())
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && Native.WindowText(h).Length > 0) list.Add(h);
            }
            return list;
        }

        public void OpenApp() { if (Call != null) OpenApp(Call); }

        static void OpenApp(CallInfo c)
        {
            try
            {
                // the call window itself (e.g. WhatsApp's "Voice call" window) is the most useful thing to show
                if (c.SourceWindow != IntPtr.Zero && Native.IsWindow(c.SourceWindow) && Native.IsWindowVisible(c.SourceWindow)
                    && Native.WindowClass(c.SourceWindow) != "Windows.UI.Core.CoreWindow")
                {
                    Native.BringToFront(c.SourceWindow);
                    return;
                }
                foreach (var n in c.Processes)
                    foreach (var p in Process.GetProcessesByName(n))
                        if (p.MainWindowHandle != IntPtr.Zero) { Native.BringToFront(p.MainWindowHandle); return; }
                if (c.Key.Contains("_") && !c.Key.Contains("\\"))
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + c.Key + "!App") { UseShellExecute = true });
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // ---------- debug: render call states without a real call ----------
        public void Demo(CallInfo c, bool mic, bool cam)
        {
            _demo = true;
            Call = c;
            MicInUse = mic;
            CameraInUse = cam;
            MicApp = c?.App ?? "Voice Recorder";
            Publish();
        }

        public void Dispose()
        {
            _poll.Stop();
            _micWatch?.Dispose();
            _camWatch?.Dispose();
            SensorUse.StopCameraWatch();
        }
    }

    sealed class WindowRead
    {
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Buttons = new List<string>();
        public object Accept, Decline;
    }

    /// <summary>UI Automation helpers (kept in their own class so UIAutomationClient only loads when a call shows up).</summary>
    static class CallAutomation
    {
        public static bool IsAccept(string n)
        {
            string t = n.Trim().ToLowerInvariant();
            return t.StartsWith("accept") || t.StartsWith("answer") || t == "pick up" || t == "join call";
        }

        public static bool IsDecline(string n)
        {
            string t = n.Trim().ToLowerInvariant();
            return t.StartsWith("decline") || t.StartsWith("reject") || t == "ignore" || t == "ignore call";
        }

        public static bool IsEnd(string n)
        {
            string t = n.Trim().ToLowerInvariant();
            return t == "end call" || t == "end" || t.StartsWith("hang up") || t == "hangup" || t == "leave" || t == "leave call"
                   || t == "disconnect" || t.StartsWith("end voice call") || t.StartsWith("end video call");
        }

        public static WindowRead Read(IntPtr hwnd)
        {
            var read = new WindowRead();
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            int n = 0;
            foreach (System.Windows.Automation.AutomationElement el in all)
            {
                if (++n > 400) break;
                string name;
                System.Windows.Automation.ControlType type;
                try { name = el.Current.Name; type = el.Current.ControlType; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (type == System.Windows.Automation.ControlType.Button)
                {
                    if (read.Buttons.Count < 20) read.Buttons.Add(name.Trim());
                    if (read.Accept == null && IsAccept(name)) read.Accept = el;
                    else if (read.Decline == null && IsDecline(name)) read.Decline = el;
                    continue;
                }
                foreach (var line in name.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.Trim().Length > 0 && !read.Lines.Contains(line.Trim())) read.Lines.Add(line.Trim());
            }
            return read;
        }

        public static bool Invoke(object element)
        {
            try
            {
                var el = (System.Windows.Automation.AutomationElement)element;
                if (el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out object p))
                {
                    ((System.Windows.Automation.InvokePattern)p).Invoke();
                    return true;
                }
            }
            catch (Exception ex) { App.Log(ex); }
            return false;
        }

        public static bool InvokeIn(IEnumerable<IntPtr> windows, Func<string, bool> match)
        {
            var buttonCondition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button);
            foreach (var h in windows)
            {
                try
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(h);
                    foreach (System.Windows.Automation.AutomationElement b in root.FindAll(System.Windows.Automation.TreeScope.Descendants, buttonCondition))
                    {
                        string name;
                        try { name = b.Current.Name; } catch { continue; }
                        if (!string.IsNullOrEmpty(name) && match(name) && Invoke(b)) return true;
                    }
                }
                catch (Exception ex) { App.Log(ex); }
            }
            return false;
        }
    }

    /// <summary>Signals when anything under an HKCU key changes (no polling).</summary>
    sealed class RegWatch : IDisposable
    {
        readonly RegistryKey _key;
        readonly AutoResetEvent _event = new AutoResetEvent(false);
        readonly Action _onChange;
        RegisteredWaitHandle _wait;
        volatile bool _disposed;

        [DllImport("advapi32.dll")]
        static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter, SafeWaitHandle evt, bool async);

        public RegWatch(string path, Action onChange)
        {
            _onChange = onChange;
            try
            {
                _key = Registry.CurrentUser.OpenSubKey(path, false);
                if (_key != null) Arm();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        void Arm()
        {
            if (_disposed) return;
            // REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC
            if (RegNotifyChangeKeyValue(_key.Handle, true, 0x1 | 0x4 | 0x10000000, _event.SafeWaitHandle, true) != 0) return;
            _wait = ThreadPool.RegisterWaitForSingleObject(_event, (s, timedOut) =>
            {
                if (_disposed) return;
                try { _onChange(); } catch { }
                Arm();
            }, null, -1, true);
        }

        public void Dispose()
        {
            _disposed = true;
            try { _wait?.Unregister(null); _key?.Dispose(); } catch { }
        }
    }
}
