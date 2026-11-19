using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Isle
{
    /// <summary>
    /// Isle Link: Isle's own connection to the "Isle for Android" app, straight over Bluetooth (RFCOMM).
    /// The laptop advertises a Bluetooth service; the phone (already paired) connects to it by itself whenever it's in range.
    /// Protocol: one line per message, UTF-8, text payloads base64:
    ///   HELLO name version | BATT percent charging(0/1) | CLIP base64 | RING | RINGSTOP | PING | PONG
    /// </summary>
    sealed class IsleLink : IDisposable
    {
        public static readonly Guid ServiceUuid = new Guid("6b1f7c2e-3d4a-4e8b-9c5f-2a7d1e0b8c41");

        readonly Dispatcher _ui;
        readonly DispatcherTimer _retry, _ping;
        readonly object _lock = new object();
        RfcommServiceProvider _provider;
        StreamSocketListener _listener;
        StreamSocket _socket;
        Stream _out;
        int _generation;

        public bool Listening { get; private set; }
        public bool Connected { get; private set; }
        public string DeviceName { get; private set; } = "";
        public int Battery { get; private set; } = -1;
        public bool Charging { get; private set; }
        public string Error { get; private set; } = "";

        public event Action StatusChanged;              // UI thread
        public event Action<bool> ConnectionChanged;    // UI thread
        public event Action<string> ClipboardReceived;  // UI thread
        public event Action<string, int, string, int, int> MirrorOffered;   // UI thread: ip, port, token, width, height
        public event Action<string> MirrorFailed;       // UI thread

        public IsleLink(Dispatcher ui)
        {
            _ui = ui;
            _retry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
            _retry.Tick += async (s, e) => { if (!Listening) await StartAsync(); else _retry.Stop(); };
            _ping = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(25) };
            _ping.Tick += (s, e) => { if (Connected && !Send("PING")) Dropped(_generation); };
        }

        public async Task StartAsync()
        {
            try
            {
                if (_provider == null)
                    _provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceUuid));
                if (_listener == null)
                {
                    _listener = new StreamSocketListener();
                    _listener.ConnectionReceived += OnConnection;
                    await _listener.BindServiceNameAsync(_provider.ServiceId.AsString(), SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                    // SDP service name, so the phone sees "Isle Link"
                    var w = new DataWriter();
                    byte[] name = Encoding.UTF8.GetBytes("Isle Link");
                    w.WriteByte(0x25);
                    w.WriteByte((byte)name.Length);
                    w.WriteBytes(name);
                    if (!_provider.SdpRawAttributes.ContainsKey(0x100)) _provider.SdpRawAttributes.Add(0x100, w.DetachBuffer());
                }
                _provider.StartAdvertising(_listener);
                Listening = true;
                Error = "";
                L("listening for the phone (Bluetooth service advertised)");
                _ping.Start();
            }
            catch (Exception ex)
            {
                // typically: Bluetooth is off. Try again later.
                Listening = false;
                Error = ex.Message;
                App.Log(new Exception("Isle Link not listening yet: " + ex.Message));
                _retry.Start();
            }
            StatusChanged?.Invoke();
        }

        void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args) { L("phone connected to the laptop"); Adopt(args.Socket); }

        static void L(string s) => App.Log(new Exception("IsleLink: " + s));

        // ---------- the other direction: Isle dials the phone app (some PCs don't publish desktop apps' Bluetooth services) ----------

        public static readonly Guid PhoneUuid = new Guid("6b1f7c2e-3d4a-4e8b-9c5f-2a7d1e0b8c42");
        public string PhoneDeviceId;
        public string PhoneName = "";
        public string LastDialError { get; private set; } = "";
        DispatcherTimer _dial;
        bool _dialing;
        string _loggedDialError = "";

        public void StartDialing(string phoneDeviceId)
        {
            PhoneDeviceId = phoneDeviceId;
            if (_dial == null)
            {
                _dial = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
                _dial.Tick += async (s, e) => await DialAsync();
                _dial.Start();
            }
            _ = DialAsync();
        }

        public async Task DialAsync()
        {
            if (Connected || _dialing || string.IsNullOrEmpty(PhoneDeviceId)) return;
            _dialing = true;
            try
            {
                var device = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(PhoneDeviceId);
                if (device == null)
                {
                    // re-paired / renamed / Bluetooth toggled: look the phone up again among paired devices
                    var paired = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                        Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true));
                    var match = paired.FirstOrDefault(d => d.Name.IndexOf("pixel", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || (PhoneName.Length > 0 && d.Name.Equals(PhoneName, StringComparison.OrdinalIgnoreCase)));
                    if (match != null) { PhoneDeviceId = match.Id; device = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(match.Id); }
                }
                if (device == null) { LastDialError = "phone not found (Bluetooth off on the laptop or phone?)"; return; }
                var result = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(PhoneUuid), Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                if (result.Services.Count == 0) { LastDialError = "Isle app on the phone not reachable (" + result.Error + ")"; return; }
                var service = result.Services[0];
                L("found the Isle app on the phone, connecting...");
                var socket = new StreamSocket();
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                if (Connected) { socket.Dispose(); return; }
                LastDialError = "";
                L("laptop connected to the phone");
                Adopt(socket);
            }
            catch (Exception ex) { LastDialError = ex.Message; }
            finally
            {
                _dialing = false;
                if (LastDialError.Length > 0 && LastDialError != _loggedDialError)
                {
                    _loggedDialError = LastDialError;
                    App.Log(new Exception("Isle Link dial: " + LastDialError));
                }
            }
        }

        void Adopt(StreamSocket socket)
        {
            int generation;
            lock (_lock)
            {
                if (_socket != null && Connected) { try { socket.Dispose(); } catch { } return; }
                try { _socket?.Dispose(); } catch { }
                _socket = socket;
                _out = socket.OutputStream.AsStreamForWrite(0);
                generation = ++_generation;
            }
            string remote = "";
            try { remote = socket.Information.RemoteHostName?.DisplayName ?? ""; } catch { }
            _ui.BeginInvoke(new Action(() =>
            {
                Connected = true;
                if (DeviceName.Length == 0) DeviceName = remote;
                ConnectionChanged?.Invoke(true);
                StatusChanged?.Invoke();
            }));
            Send("HELLO " + B64(Environment.MachineName) + " 1");
            Task.Run(() => ReadLoop(socket, generation));
        }

        void ReadLoop(StreamSocket socket, int generation)
        {
            try
            {
                using (var reader = new StreamReader(socket.InputStream.AsStreamForRead(0), Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) Handle(line.Trim());
                }
            }
            catch { }
            Dropped(generation);
        }

        void Dropped(int generation)
        {
            lock (_lock)
            {
                if (generation != _generation || _socket == null) return;
                try { _socket.Dispose(); } catch { }
                _socket = null;
                _out = null;
            }
            L("connection dropped");
            _ui.BeginInvoke(new Action(() =>
            {
                if (!Connected) return;
                Connected = false;
                ConnectionChanged?.Invoke(false);
                StatusChanged?.Invoke();
            }));
        }

        void Handle(string line)
        {
            if (line.Length == 0) return;
            string[] p = line.Split(' ');
            switch (p[0])
            {
                case "HELLO":
                    string name = p.Length > 1 ? Unb64(p[1]) : "";
                    L("hello from " + name);
                    _ui.BeginInvoke(new Action(() => { if (name.Length > 0) DeviceName = name; StatusChanged?.Invoke(); }));
                    break;
                case "BATT":
                    if (p.Length >= 3 && int.TryParse(p[1], out int pct))
                    {
                        bool charging = p[2] == "1";
                        _ui.BeginInvoke(new Action(() => { Battery = pct; Charging = charging; StatusChanged?.Invoke(); }));
                    }
                    break;
                case "CLIP":
                    string text = p.Length > 1 ? Unb64(p[1]) : "";
                    if (text.Length > 0) _ui.BeginInvoke(new Action(() => ClipboardReceived?.Invoke(text)));
                    break;
                case "PING":
                    Send("PONG");
                    break;
                case "MIRROR":
                    // MIRROR ip port token width height: the phone is ready to stream its screen over Wi-Fi
                    if (p.Length >= 6 && int.TryParse(p[2], out int port) && int.TryParse(p[4], out int w) && int.TryParse(p[5], out int h))
                    {
                        L("phone offers its screen at " + p[1] + ":" + port);
                        string ip = p[1], token = p[3];
                        _ui.BeginInvoke(new Action(() => MirrorOffered?.Invoke(ip, port, token, w, h)));
                    }
                    break;
                case "MIRRORERR":
                    string why = p.Length > 1 ? Unb64(p[1]) : "Screen mirroring didn't start";
                    L("mirror error: " + why);
                    _ui.BeginInvoke(new Action(() => MirrorFailed?.Invoke(why)));
                    break;
            }
        }

        public bool Send(string line)
        {
            lock (_lock)
            {
                if (_out == null) return false;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    _out.Write(bytes, 0, bytes.Length);
                    _out.Flush();
                    return true;
                }
                catch { return false; }
            }
        }

        public bool SendClipboard(string text) => !string.IsNullOrEmpty(text) && Send("CLIP " + B64(text));

        /// <summary>Streams a file to the phone (saved in Downloads/Isle there). Runs off the UI thread.</summary>
        public Task<bool> SendFileAsync(string path, Action<int> progress = null) => Task.Run(() =>
        {
            try
            {
                var file = new FileInfo(path);
                L("sending file " + file.Name + " (" + file.Length + " bytes), connected=" + Connected);
                if (!file.Exists || !Connected) return false;
                if (!Send("FILE " + B64(file.Name) + " " + file.Length)) return false;
                using (var stream = file.OpenRead())
                {
                    var buffer = new byte[24 * 1024];
                    int n, lastPct = -1;
                    long done = 0;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        done += n;
                        int pct = file.Length > 0 ? (int)(done * 100 / file.Length) : 100;
                        if (progress != null && pct != lastPct) { lastPct = pct; _ui.BeginInvoke(new Action(() => progress(pct))); }
                        if (!Send("DATA " + Convert.ToBase64String(buffer, 0, n))) { L("file send broke mid-way"); return false; }
                    }
                }
                bool ok = Send("FILEEND");
                L("file " + file.Name + (ok ? " sent" : " failed at the end"));
                return ok;
            }
            catch (Exception ex) { App.Log(ex); return false; }
        });
        public bool Ring() => Send("RING");

        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        static string Unb64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return ""; }
        }

        public void Dispose()
        {
            _retry.Stop();
            _ping.Stop();
            _dial?.Stop();
            try { _provider?.StopAdvertising(); } catch { }
            lock (_lock) { try { _socket?.Dispose(); } catch { } }
            try { _listener?.Dispose(); } catch { }
        }
    }
}
