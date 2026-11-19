using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Isle Setup")]
[assembly: AssemblyProduct("Isle")]
[assembly: AssemblyCompany("Isle")]
[assembly: AssemblyVersion("1.4.4.0")]
[assembly: AssemblyFileVersion("1.4.4.0")]

namespace IsleSetup
{
    /// <summary>
    /// Single-file installer for Isle (like an .apk): carries Isle.exe inside itself.
    ///   IsleSetup.exe                      -> install / update window
    ///   IsleSetup.exe /quiet               -> silent install
    ///   "Uninstall Isle.exe" /uninstall    -> uninstall window (this is what Settings > Apps runs)
    ///   ... /uninstall /quiet              -> silent uninstall
    /// Installs per-user to %LOCALAPPDATA%\Programs\Isle, so it never needs admin rights.
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            bool uninstall = Has("/uninstall") || Has("--uninstall");
            bool quiet = Has("/quiet") || Has("/S") || Has("--quiet");
            string snapshot = args.FirstOrDefault(a => a.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase))?.Substring(11);

            if (quiet)
            {
                try
                {
                    if (uninstall) { Installer.Uninstall(Has("/purge")); Installer.ScheduleFolderDelete(); }
                    else Installer.Install(autoStart: !Has("/noautostart"), desktopShortcut: Has("/desktop"), launch: !Has("/nolaunch"));
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    return 1;
                }
            }

            var app = new Application();
            var window = new SetupWindow(uninstall, snapshot);
            app.Exit += (s, e) => { if (window.Uninstalled) Installer.ScheduleFolderDelete(); };
            return app.Run(window);
        }
    }

    static class Installer
    {
        public const string Version = "1.4.6";
        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Isle");
        public static string Exe => Path.Combine(Dir, "Isle.exe");
        public static string Uninstaller => Path.Combine(Dir, "Uninstall Isle.exe");
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Isle.lnk");
        static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Phone (Isle).lnk");
        static string DesktopLink =>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Isle.lnk");
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Isle";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string InstalledVersion
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return k?.GetValue("DisplayVersion") as string;
            }
        }

        public static bool AutoStartEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue("Isle") != null;
            }
        }

        public static void Install(bool autoStart, bool desktopShortcut, bool launch)
        {
            StopRunning();
            Directory.CreateDirectory(Dir);
            Extract("payload.Isle.exe", Exe);
            Extract("payload.Isle.exe.config", Exe + ".config");
            if (Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("payload.Isle-Phone.apk"))
                Extract("payload.Isle-Phone.apk", Path.Combine(Dir, "Isle-Phone.apk"));

            // keep a copy of this installer inside the app folder as the uninstaller
            string self = Process.GetCurrentProcess().MainModule.FileName;
            if (!self.Equals(Uninstaller, StringComparison.OrdinalIgnoreCase)) Retry(() => File.Copy(self, Uninstaller, true));

            Shortcut(StartMenuLink, Exe, "Isle - your dynamic island");
            // right-click any file > Send to > Phone (Isle)
            Shortcut(SendToLink, Exe, "Send to your phone with Isle");
            if (desktopShortcut) Shortcut(DesktopLink, Exe, "Isle - your dynamic island");

            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autoStart) k.SetValue("Isle", Quote(Exe));
                else k.DeleteValue("Isle", false);
            }

            long kb = new DirectoryInfo(Dir).GetFiles().Sum(f => f.Length) / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Isle");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Isle");
                k.SetValue("Comments", "Dynamic island for Windows: media, calls, volume, quick apps");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", Quote(Uninstaller) + " /uninstall");
                k.SetValue("QuietUninstallString", Quote(Uninstaller) + " /uninstall /quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, kb), RegistryValueKind.DWord);
            }

            if (launch) Process.Start(new ProcessStartInfo(Exe, "--welcome") { UseShellExecute = true, WorkingDirectory = Dir });
        }

        public static void Uninstall(bool removeSettings)
        {
            StopRunning();
            foreach (var link in new[] { StartMenuLink, DesktopLink, SendToLink })
                try { if (File.Exists(link)) File.Delete(link); } catch { }
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                k?.DeleteValue("Isle", false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            try { File.Delete(Exe); File.Delete(Exe + ".config"); File.Delete(Path.Combine(Dir, "Isle-Phone.apk")); } catch { }
            if (removeSettings)
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle");
                try { if (Directory.Exists(data)) Directory.Delete(data, true); } catch { }
            }
        }

        /// <summary>The uninstaller lives in the folder it deletes, so the folder goes a moment after this process exits.</summary>
        public static void ScheduleFolderDelete()
        {
            if (!Directory.Exists(Dir)) return;
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q " + Quote(Dir))
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        static void StopRunning()
        {
            var running = Process.GetProcessesByName("Isle");
            if (running.Length == 0) return;
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\Isle.Quit", out var quit)) { quit.Set(); quit.Dispose(); }
            }
            catch { }
            foreach (var p in running)
            {
                try
                {
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(4000); }
                }
                catch { }
            }
        }

        static void Extract(string resource, string target)
        {
            Retry(() =>
            {
                using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                using (var dst = File.Create(target))
                {
                    if (src == null) throw new InvalidOperationException("Installer is missing " + resource);
                    src.CopyTo(dst);
                }
            });
        }

        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(300); }
            }
        }

        static void Shortcut(string path, string target, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = description;
            link.Save();
        }

        static string Quote(string s) => "\"" + s + "\"";
    }

    sealed class SetupWindow : Window
    {
        readonly bool _uninstallMode;
        readonly string _snapshot;
        readonly FrameworkElement _root;
        readonly TextBlock _heading, _sub, _status;
        readonly CheckBox _autoStart, _desktop, _removeSettings;
        readonly StackPanel _options;
        readonly Button _primary;
        bool _done;

        public bool Uninstalled { get; private set; }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        public SetupWindow(bool uninstall, string snapshot)
        {
            _uninstallMode = uninstall;
            _snapshot = snapshot;
            Title = uninstall ? "Uninstall Isle" : "Isle Setup";
            // ordinary opaque window, Windows 11 draws the rounded frame + shadow, so the text stays sharp
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
            SourceInitialized += (s, e) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                try { int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4); int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4); } catch { }
            };
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E));
            ResizeMode = ResizeMode.NoResize;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.Setup.xaml"))
                _root = (FrameworkElement)XamlReader.Load(s);
            Content = _root;

            T P<T>(string n) where T : class => (T)_root.FindName(n);
            _heading = P<TextBlock>("Heading"); _sub = P<TextBlock>("Sub"); _status = P<TextBlock>("Status");
            _autoStart = P<CheckBox>("AutoStart"); _desktop = P<CheckBox>("DesktopShortcut"); _removeSettings = P<CheckBox>("RemoveSettings");
            _options = P<StackPanel>("Options"); _primary = P<Button>("Primary");

            var icon = LoadIcon();
            if (icon != null) { P<Image>("AppIcon").Source = icon; Icon = icon; }
            P<Button>("CloseButton").Click += (s, e) => Close();
            P<Border>("Card").MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            _primary.Click += OnPrimary;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            if (uninstall)
            {
                _heading.Text = "Uninstall Isle";
                _sub.Text = "Removes Isle, its shortcuts and auto-start.";
                _autoStart.Visibility = _desktop.Visibility = Visibility.Collapsed;
                _removeSettings.Visibility = Visibility.Visible;
                _primary.Content = "Uninstall";
                _primary.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                _primary.Foreground = Brushes.White;
            }
            else
            {
                string installed = Installer.InstalledVersion;
                if (installed != null)
                {
                    _primary.Content = installed == Installer.Version ? "Reinstall" : "Update";
                    _sub.Text = "Version " + installed + " is installed. " + (installed == Installer.Version ? "This reinstalls it." : "This updates it to " + Installer.Version + ".");
                    _autoStart.IsChecked = Installer.AutoStartEnabled;
                }
                _status.Text = "Installs to " + Installer.Dir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
                               + "\nNo admin rights needed  Â·  version " + Installer.Version;
            }

            if (_snapshot != null) Loaded += async (s, e) => { await Task.Delay(400); SaveSnapshot(); Close(); };
        }

        static BitmapSource LoadIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.isle.ico"))
                {
                    var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                }
            }
            catch { return null; }
        }

        async void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (_done) { Close(); return; }
            _primary.IsEnabled = false;
            _options.IsEnabled = false;
            bool autoStart = _autoStart.IsChecked == true, desktop = _desktop.IsChecked == true, purge = _removeSettings.IsChecked == true;
            _status.Text = _uninstallMode ? "Removingâ€¦" : "Installingâ€¦";
            try
            {
                if (_uninstallMode)
                {
                    await Task.Run(() => Installer.Uninstall(purge));
                    Uninstalled = true;
                    _heading.Text = "Isle was removed";
                    _sub.Text = purge ? "The app and your settings are gone." : "Your settings were kept in case you install it again.";
                }
                else
                {
                    await Task.Run(() => Installer.Install(autoStart, desktop, launch: true));
                    _heading.Text = "Isle is installed";
                    _sub.Text = "It's running at the top of your screen now.\nFind it in Start, and remove it any time from Settings â€º Apps.";
                    _primary.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0xF2, 0xC2));
                }
                _options.Visibility = Visibility.Collapsed;
                _status.Text = "";
                _primary.Content = "Done";
                _primary.Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C));
                _primary.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0xF2, 0xC2));
                _done = true;
            }
            catch (Exception ex)
            {
                _status.Text = "Something went wrong: " + ex.Message;
                _options.IsEnabled = true;
            }
            _primary.IsEnabled = true;
        }

        void SaveSnapshot()
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * s), (int)Math.Ceiling(ActualHeight * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x48)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            rtb.Render(bg);
            rtb.Render(_root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(_snapshot)) enc.Save(f);
        }
    }
}
