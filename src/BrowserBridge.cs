using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Isle
{
    /// <summary>One browser tab (or frame) that is playing, as reported by the Isle browser extension.
    /// The same instance is kept and updated for as long as the tab plays, so it can be compared by reference.</summary>
    sealed class BrowserTab
    {
        public string Id = "", Key = "", Browser = "Chrome";
        public string Title = "", Artist = "", Album = "", Site = "", SiteName = "", ArtUrl = "", IconUrl = "";
        public bool Playing, CanSeek, CanNext, CanPrev, CanLike, Liked;
        public double Pos, Dur, Rate = 1;
        public DateTimeOffset At;
        public long Seq;
        internal BrowserBridge.Client Client;

        /// <summary>Process / app name the browser runs as, for its icon and to hide its own Windows media session.</summary>
        public string BrowserExe => BrowserBridge.ExeFor(Browser);
    }

    /// <summary>
    /// Talks to the Isle extension in Chrome / Edge / Brave. Windows only ever sees the one tab a browser last played;
    /// the extension reports every tab that's playing and carries out play, skip, seek and Like inside the page.
    ///
    /// The browser starts "Isle.exe chrome-extension://…" (native messaging); that copy only relays
    /// (see <see cref="RunHost"/>) to this, the running Isle, over a named pipe only this Windows user can open.
    /// </summary>
    sealed class BrowserBridge : IDisposable
    {
        public const string HostName = "com.isle.browser";
        public const string ExtensionId = "chmaimghninejlggcobgfhgkojkiefhj";
        static string PipeName => "Isle.Browser-" + Process.GetCurrentProcess().SessionId;

        public static BrowserBridge Current { get; private set; }

        readonly Dispatcher _ui;
        readonly List<Client> _clients = new List<Client>();
        readonly Dictionary<string, BrowserTab> _tabs = new Dictionary<string, BrowserTab>();
        readonly Dictionary<string, byte[]> _images = new Dictionary<string, byte[]>();
        readonly Queue<string> _imageOrder = new Queue<string>();
        volatile bool _stop;
        string _lastSummary = "";

        /// <summary>Raised on the UI thread when tabs appear, change or go away.</summary>
        public event Action TabsChanged;
        /// <summary>Raised on the UI thread when a picture (art / site icon) arrives.</summary>
        public event Action<string> ImageArrived;
        public event Action ConnectionChanged;

        public BrowserBridge(Dispatcher ui)
        {
            _ui = ui;
            Current = this;
        }

        public IEnumerable<BrowserTab> Tabs => _tabs.Values.OrderBy(t => t.Seq);
        public int ConnectedBrowsers { get { lock (_clients) return _clients.Count; } }
        public IEnumerable<string> Browsers { get { lock (_clients) return _clients.Select(c => c.Browser).Where(b => b != null).Distinct().ToList(); } }

        public byte[] Image(string url) => url != null && _images.TryGetValue(url, out var b) ? b : null;

        public void Start()
        {
            try { Register(); } catch (Exception ex) { App.Log(ex); }
            try { WriteExtension(); } catch (Exception ex) { App.Log(ex); }
            new Thread(Listen) { IsBackground = true, Name = "Isle browser bridge" }.Start();
        }

        // ------------------------------------------------------------------ server side (running Isle)

        internal sealed class Client
        {
            public NamedPipeServerStream Pipe;
            public StreamWriter Writer;
            public string Browser;
            public readonly object Gate = new object();
        }

        void Listen()
        {
            while (!_stop)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                    pipe.WaitForConnection();
                    var c = new Client { Pipe = pipe, Writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true } };
                    lock (_clients) _clients.Add(c);
                    new Thread(() => Serve(c)) { IsBackground = true, Name = "Isle browser client" }.Start();
                }
                catch (Exception ex)
                {
                    pipe?.Dispose();
                    if (_stop) return;
                    App.Log(ex);
                    Thread.Sleep(2000);
                }
            }
        }

        void Serve(Client c)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            try
            {
                Send(c, new Dictionary<string, object> { { "type", "hello" } });
                App.Note("browser extension connected");
                _ui.BeginInvoke(new Action(() => ConnectionChanged?.Invoke()));
                using (var reader = new StreamReader(c.Pipe, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        Dictionary<string, object> msg;
                        try { msg = json.DeserializeObject(line) as Dictionary<string, object>; }
                        catch { continue; }
                        if (msg != null) _ui.BeginInvoke(new Action(() => Handle(c, msg)));
                    }
                }
            }
            catch (Exception ex) { if (!_stop && !(ex is IOException)) App.Log(ex); }
            finally
            {
                lock (_clients) _clients.Remove(c);
                try { c.Pipe.Dispose(); } catch { }
                _ui.BeginInvoke(new Action(() =>
                {
                    foreach (var id in _tabs.Where(kv => kv.Value.Client == c).Select(kv => kv.Key).ToList()) _tabs.Remove(id);
                    TabsChanged?.Invoke();
                    ConnectionChanged?.Invoke();
                }));
            }
        }

        void Handle(Client c, Dictionary<string, object> msg)
        {
            string type = Str(msg, "type");
            if (type == "image")
            {
                string url = Str(msg, "url");
                try
                {
                    var bytes = Convert.FromBase64String(Str(msg, "data"));
                    if (url.Length > 0 && bytes.Length > 0 && !_images.ContainsKey(url))
                    {
                        _images[url] = bytes;
                        _imageOrder.Enqueue(url);
                        while (_imageOrder.Count > 60) _images.Remove(_imageOrder.Dequeue());
                    }
                    ImageArrived?.Invoke(url);
                }
                catch { }
                return;
            }
            if (type != "tabs") return;

            bool first = c.Browser == null;
            c.Browser = Str(msg, "browser");
            if (first) ConnectionChanged?.Invoke();
            var seen = new HashSet<string>();
            if (msg.TryGetValue("tabs", out var list) && list is object[] arr)
            {
                foreach (var o in arr.OfType<Dictionary<string, object>>())
                {
                    string key = Str(o, "key");
                    string id = c.GetHashCode() + "/" + key;
                    seen.Add(id);
                    if (!_tabs.TryGetValue(id, out var t)) _tabs[id] = t = new BrowserTab { Id = id, Key = key, Client = c };
                    t.Browser = c.Browser;
                    t.Seq = (long)Num(o, "seq");
                    t.Title = Str(o, "title"); t.Artist = Str(o, "artist"); t.Album = Str(o, "album");
                    t.Site = Str(o, "site"); t.SiteName = Str(o, "siteName");
                    t.ArtUrl = Str(o, "art"); t.IconUrl = Str(o, "icon");
                    t.Playing = Bool(o, "playing"); t.CanSeek = Bool(o, "canSeek"); t.CanNext = Bool(o, "canNext");
                    t.CanPrev = Bool(o, "canPrev"); t.CanLike = Bool(o, "canLike"); t.Liked = Bool(o, "liked");
                    t.Pos = Num(o, "pos"); t.Dur = Num(o, "dur"); t.Rate = Num(o, "rate", 1);
                    double at = Num(o, "at");
                    t.At = at > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)at).ToLocalTime() : DateTimeOffset.Now;
                }
            }
            foreach (var id in _tabs.Where(kv => kv.Value.Client == c && !seen.Contains(kv.Key)).Select(kv => kv.Key).ToList()) _tabs.Remove(id);
            // one line in isle.log whenever what the browser reports changes (not on every position update)
            string summary = string.Join("; ", _tabs.Values.OrderBy(t => t.Seq).Select(t => string.Format("{0} {1}{2}{3}{4} \"{5}\"",
                t.SiteName, t.Playing ? "playing" : "paused", t.CanLike ? (t.Liked ? " liked" : " can-like") : " no-like",
                t.CanNext ? " next" : "", t.CanPrev ? " prev" : "", t.Title.Length > 40 ? t.Title.Substring(0, 40) + "…" : t.Title)));
            if (summary != _lastSummary) { _lastSummary = summary; App.Note(c.Browser + " tabs: " + (summary.Length > 0 ? summary : "none")); }
            TabsChanged?.Invoke();
        }

        /// <summary>Asks the page to do something: toggle, next, prev, seek (seconds), like, focus.</summary>
        public void Command(BrowserTab t, string cmd, double value = 0)
        {
            if (t?.Client == null) return;
            Send(t.Client, new Dictionary<string, object> { { "type", "cmd" }, { "key", t.Key }, { "cmd", cmd }, { "value", value } });
        }

        static void Send(Client c, Dictionary<string, object> msg)
        {
            string line = new JavaScriptSerializer().Serialize(msg);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { lock (c.Gate) c.Writer.WriteLine(line); }
                catch { }
            });
        }

        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "";
        static bool Bool(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is bool b && b;
        static double Num(Dictionary<string, object> d, string k, double fallback = 0)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return fallback;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static string ExeFor(string browser)
        {
            browser = browser ?? "";
            if (browser.IndexOf("Edge", StringComparison.OrdinalIgnoreCase) >= 0) return "msedge";
            if (browser.IndexOf("Brave", StringComparison.OrdinalIgnoreCase) >= 0) return "brave";
            if (browser.IndexOf("Opera", StringComparison.OrdinalIgnoreCase) >= 0) return "opera";
            return "chrome";
        }

        public void Dispose()
        {
            _stop = true;
            lock (_clients) foreach (var c in _clients) { try { c.Pipe.Dispose(); } catch { } }
            // unblock WaitForConnection
            try { using (var p = new NamedPipeClientStream(".", PipeName)) p.Connect(200); } catch { }
        }

        // ------------------------------------------------------------------ registration + extension files

        static readonly string[] HostKeys =
        {
            @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
            @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName,
        };

        /// <summary>Tells the browsers where Isle is (per user, no admin), so the extension can reach it.</summary>
        static void Register()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            string manifest = Path.Combine(Settings.Dir, "browser-host.json");
            var ser = new JavaScriptSerializer();
            string json = ser.Serialize(new Dictionary<string, object>
            {
                { "name", HostName },
                { "description", "Isle dynamic island" },
                { "path", exe },
                { "type", "stdio" },
                { "allowed_origins", new[] { "chrome-extension://" + ExtensionId + "/" } },
            });
            Directory.CreateDirectory(Settings.Dir);
            if (!File.Exists(manifest) || File.ReadAllText(manifest) != json) File.WriteAllText(manifest, json);
            foreach (var key in HostKeys)
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                    if (!(k.GetValue("") is string v) || v != manifest) k.SetValue("", manifest);
        }

        /// <summary>The extension's folder, next to Isle.exe: Chrome loads it from here ("Load unpacked").</summary>
        public static string ExtensionDir => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Browser extension");

        static void WriteExtension()
        {
            var asm = Assembly.GetExecutingAssembly();
            const string prefix = "Isle.browser.";
            Directory.CreateDirectory(ExtensionDir);
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix)))
            {
                byte[] data;
                using (var s = asm.GetManifestResourceStream(name))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                string path = Path.Combine(ExtensionDir, name.Substring(prefix.Length));
                if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(data)) continue;
                File.WriteAllBytes(path, data);
            }
        }

        /// <summary>Opens the extension folder and the browser's extensions page, for the one-time setup.</summary>
        public static void OpenSetup()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ExtensionDir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
            foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
            {
                try
                {
                    string path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe, "", null) as string
                                  ?? Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe, "", null) as string;
                    if (path == null || !File.Exists(path)) continue;
                    Process.Start(new ProcessStartInfo(path, exe == "chrome.exe" ? "chrome://extensions/" : "edge://extensions/") { UseShellExecute = false });
                    return;
                }
                catch (Exception ex) { App.Log(ex); }
            }
        }

        // ------------------------------------------------------------------ relay (Isle.exe started by the browser)

        /// <summary>
        /// Native messaging host: the browser writes 4-byte-length-prefixed JSON to stdin and reads the same from stdout.
        /// Relays both ways to the running Isle as one JSON message per line. Waits quietly while Isle isn't running.
        /// </summary>
        public static void RunHost()
        {
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var outGate = new object();
            StreamWriter toIsle = null;
            var isleGate = new object();

            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        // look for the pipe without connecting to it (Connect would spin while Isle isn't running)
                        if (!Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith("\\" + PipeName, StringComparison.OrdinalIgnoreCase)))
                        {
                            Thread.Sleep(2000);
                            continue;
                        }
                        // Asynchronous (overlapped) so this thread can wait for Isle's next line while the other one
                        // writes: on a synchronous handle Windows queues the write behind the pending read, forever
                        using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                        {
                            pipe.Connect(1000);
                            var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                            lock (isleGate) toIsle = w;
                            using (var r = new StreamReader(pipe, Encoding.UTF8))
                            {
                                string line;
                                while ((line = r.ReadLine()) != null)
                                {
                                    var bytes = Encoding.UTF8.GetBytes(line);
                                    lock (outGate)
                                    {
                                        stdout.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
                                        stdout.Write(bytes, 0, bytes.Length);
                                        stdout.Flush();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                    lock (isleGate) toIsle = null;
                    Thread.Sleep(1500);
                }
            }) { IsBackground = true }.Start();

            var len = new byte[4];
            while (true)
            {
                if (!ReadExactly(stdin, len, 4)) break;                 // browser closed the connection
                int n = BitConverter.ToInt32(len, 0);
                if (n <= 0 || n > 64 * 1024 * 1024) break;
                var buf = new byte[n];
                if (!ReadExactly(stdin, buf, n)) break;
                string json = Encoding.UTF8.GetString(buf);
                lock (isleGate)
                {
                    try { toIsle?.WriteLine(json); }
                    catch { toIsle = null; }
                }
            }
            Environment.Exit(0);
        }

        static bool ReadExactly(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }
    }
}
