using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Isle")]
[assembly: AssemblyProduct("Isle - personal dynamic island")]
[assembly: AssemblyVersion("1.4.6.0")]
[assembly: AssemblyFileVersion("1.4.6.0")]
[assembly: AssemblyCompany("Isle")]
[assembly: AssemblyDescription("Isle - dynamic island for Windows")]

namespace Isle
{
    static class App
    {
        static EventWaitHandle _quit, _show, _send;
        static string SendQueue => Path.Combine(Settings.Dir, "send-queue.txt");
        public static bool PinExpanded, Welcome, Quitting;

        public static void Quit()
        {
            Quitting = true;
            Application.Current.Shutdown();
        }
        public static string SnapshotDir;   // debug: --snapshot=<folder> renders the island to PNGs, then exits

        [STAThread]
        static void Main(string[] args)
        {
            using var mutex = new Mutex(true, @"Local\Isle.DynamicIsland.Instance", out bool first);
            var files = Array.FindAll(args, a => !a.StartsWith("--") && File.Exists(a));
            if (!first)
            {
                // Isle is already running. Files (Explorer "Send to"): hand them over to send to the phone.
                // Otherwise (opened again from Start): ask it to open the Isle window. Then leave.
                if (files.Length > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(Settings.Dir);
                        File.AppendAllLines(SendQueue, files);
                        if (EventWaitHandle.TryOpenExisting(@"Local\Isle.Send", out var send)) { send.Set(); send.Dispose(); }
                    }
                    catch { }
                    return;
                }
                try { if (EventWaitHandle.TryOpenExisting(@"Local\Isle.Show", out var show)) { show.Set(); show.Dispose(); } }
                catch { }
                return;
            }
            if (files.Length > 0) { Directory.CreateDirectory(Settings.Dir); File.AppendAllLines(SendQueue, files); }

            PinExpanded = Array.Exists(args, a => a.Equals("--pin-expanded", StringComparison.OrdinalIgnoreCase));
            Welcome = Array.Exists(args, a => a.Equals("--welcome", StringComparison.OrdinalIgnoreCase));
            SnapshotDir = Array.Find(args, a => a.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase))?.Substring(11).Trim('"');

            System.Windows.Forms.Application.EnableVisualStyles();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log(e.Exception); e.Handled = true; };

            // the installer asks a running Isle to close cleanly (so its tray icon disappears) before replacing it
            _quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Isle.Quit");
            ThreadPool.RegisterWaitForSingleObject(_quit, (s, timedOut) => app.Dispatcher.BeginInvoke(new Action(Quit)), null, -1, true);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) => { Log(e.Exception); e.SetObserved(); };

            try
            {
                var settings = Settings.Load();
                using var audio = new AudioService(app.Dispatcher);
                var media = new MediaService(app.Dispatcher);
                using var calls = new CallService(app.Dispatcher);
                using var brightness = new BrightnessWatcher();
                var shelf = new Shelf();
                shelf.Load();
                using var phone = new PhoneService(app.Dispatcher);
                var window = new IslandWindow(settings, media, audio, calls, shelf, brightness, phone);
                using var tray = new Tray(window);
                window.Show();

                // a second launch (Start menu, desktop shortcut) signals this to open the island
                _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Isle.Show");
                ThreadPool.RegisterWaitForSingleObject(_show, (s, timedOut) => app.Dispatcher.BeginInvoke(new Action(window.ShowFromLaunch)), null, -1, false);

                // Explorer "Send to > Phone (Isle)": files arrive through a small queue file
                _send = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Isle.Send");
                Action drain = () =>
                {
                    try
                    {
                        if (!File.Exists(SendQueue)) return;
                        var queued = File.ReadAllLines(SendQueue);
                        File.Delete(SendQueue);
                        var list = Array.FindAll(queued, File.Exists);
                        if (list.Length > 0) window.SendFromExplorer(list);
                    }
                    catch (Exception ex) { Log(ex); }
                };
                ThreadPool.RegisterWaitForSingleObject(_send, (s, timedOut) => app.Dispatcher.BeginInvoke(drain), null, -1, false);
                // started with files (Isle wasn't running): send once the phone link has had a moment to connect
                if (File.Exists(SendQueue))
                {
                    var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                    later.Tick += (s, e) => { later.Stop(); drain(); };
                    later.Start();
                }

                app.Run();
            }
            catch (Exception ex)
            {
                // never hang around as a zombie: log and quit
                Log(ex);
                Environment.Exit(1);
            }
        }

        public static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                string path = Path.Combine(Settings.Dir, "isle.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
