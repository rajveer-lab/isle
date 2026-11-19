using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Isle
{
    /// <summary>The Isle app window: what opens when you start Isle from the Start menu. Closing it keeps Isle running.</summary>
    sealed class HubWindow : Window
    {
        readonly IslandWindow _island;
        readonly PhoneService _phone;
        readonly FrameworkElement _root;
        bool _loading;

        public HubWindow(IslandWindow island, PhoneService phone)
        {
            _island = island;
            _phone = phone;
            Title = "Isle";
            Native.NativeFrame(this);
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E));
            ResizeMode = ResizeMode.NoResize;
            Width = 472;
            Height = 732;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isle.Hub.xaml"))
                _root = (FrameworkElement)XamlReader.Load(s);
            Content = _root;
            var maxH = SystemParameters.WorkArea.Height - 20;
            if (Height > maxH) Height = maxH;

            var icon = LoadIcon();
            if (icon != null) { Icon = icon; P<Image>("AppIcon").Source = icon; }
            P<TextBlock>("VersionText").Text = "Isle " + FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion
                                               + "  ·  " + System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            P<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            P<Button>("MinButton").Click += (s, e) => WindowState = WindowState.Minimized;
            P<Button>("CloseButton").Click += (s, e) => Hide();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Hide(); };
            Closing += (s, e) => { if (!App.Quitting) { e.Cancel = true; Hide(); } };   // closing the window never quits Isle

            // island
            P<Button>("ShowIsland").Click += (s, e) => _island.ToggleExpanded();
            P<RadioButton>("StyleNotch").Checked += (s, e) => Change("style", "notch");
            P<RadioButton>("StyleFloating").Checked += (s, e) => Change("style", "island");
            P<RadioButton>("IdleClock").Checked += (s, e) => Change("idle", "clock");
            P<RadioButton>("IdleMinimal").Checked += (s, e) => Change("idle", "minimal");
            var auto = P<CheckBox>("AutoStart");
            auto.Checked += (s, e) => { if (!_loading) AutoStart.Set(true); };
            auto.Unchecked += (s, e) => { if (!_loading) AutoStart.Set(false); };
            var edge = P<CheckBox>("EdgeOpen");
            edge.Checked += (s, e) => Change("open_trigger", "edge");
            edge.Unchecked += (s, e) => Change("open_trigger", "hover");
            var fade = P<CheckBox>("FadeHover");
            fade.Checked += (s, e) => Change("fade_on_hover", "true");
            fade.Unchecked += (s, e) => Change("fade_on_hover", "false");
            var glass = P<CheckBox>("GlassLook");
            glass.Checked += (s, e) => Change("glass", "true");
            glass.Unchecked += (s, e) => Change("glass", "false");
            var fs = P<CheckBox>("HideFullscreen");
            fs.Checked += (s, e) => Change("hide_in_fullscreen", "true");
            fs.Unchecked += (s, e) => Change("hide_in_fullscreen", "false");

            // phone
            P<Button>("RingPhone").Click += async (s, e) =>
            {
                if (!_phone.CanAct) { _phone.OpenFindMyDevice(); return; }
                if (!await _phone.Ring()) _phone.OpenFindMyDevice();
            };
            P<Button>("SendClip").Click += async (s, e) =>
            {
                var b = (Button)s;
                bool ok = await _phone.SendClipboard();
                b.ToolTip = ok ? "Sent" : "Phone isn't connected";
                Flash(P<TextBlock>("PhoneSub"), ok ? "Clipboard sent to " + _phone.Name : "Phone isn't connected right now");
            };
            P<Button>("SendFiles").Click += async (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send to " + (_phone.Configured ? _phone.Name : "phone") };
                if (dlg.ShowDialog(this) != true) return;
                var files = dlg.FileNames;
                var sub = P<TextBlock>("PhoneSub");
                if (!_phone.LinkConnected && !(_phone.HasKdeConnect && _phone.Reachable))
                {
                    sub.Text = "Phone not connected — opened Quick Share";
                    _phone.QuickShareFallback(files);
                    return;
                }
                sub.Text = "Sending " + files.Length + " file(s)…";
                int sent = await _phone.SendFiles(files, t => sub.Text = t);
                sub.Text = sent > 0 ? "Sent " + sent + " file(s) — on the phone in Downloads/Isle" : "Couldn't send — opened Quick Share";
                if (sent == 0) _phone.QuickShareFallback(files);
            };
            P<Button>("MirrorPhone").Click += (s, e) =>
            {
                _island.StartMirror();
                P<TextBlock>("PhoneSub").Text = _phone.LinkConnected
                    ? "Tap “Show your screen” on the phone, then Start now"
                    : "Phone not connected — open Isle on the phone (Bluetooth on)";
            };
            P<Button>("Messages").Click += (s, e) => _phone.OpenMessages();
            P<Button>("PhoneLink").Click += (s, e) => _phone.OpenPhoneLink();
            P<Button>("GetPhoneApp").Click += (s, e) => ShowPhoneApp();
            P<Button>("ClearClips").Click += (s, e) => _phone.ClearHistory();

            // quick apps
            P<Button>("AddApp").Click += (s, e) =>
            {
                var picker = new AppPicker(this);
                if (picker.ShowDialog() == true && picker.Picked.HasValue)
                {
                    var apps = _island.Settings.Apps.ToList();
                    if (!apps.Any(a => a.Value.Equals(picker.Picked.Value.Value, StringComparison.OrdinalIgnoreCase))) apps.Add(picker.Picked.Value);
                    SaveApps(apps);
                }
            };

            // more
            P<Button>("EditApps").Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Settings.FilePath + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { App.Log(ex); }
            };
            P<Button>("ReloadSettings").Click += (s, e) => { _island.ReloadSettings(); Refresh(); };
            P<Button>("QuitIsle").Click += (s, e) => App.Quit();

            _phone.StatusChanged += () => { if (IsVisible) Refresh(); };
            _phone.ClipboardFromPhone += t => { if (IsVisible) Refresh(); };
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
        }

        T P<T>(string name) where T : class => (T)_root.FindName(name);

        void Change(string key, string value)
        {
            if (_loading) return;
            _island.ChangeSetting(key, value);
        }

        public void ShowAndActivate()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;   // pop to the front, then behave like a normal window
            Topmost = false;
            Focus();
        }

        public void Refresh()
        {
            _loading = true;
            try
            {
                var cfg = _island.Settings;
                P<RadioButton>("StyleNotch").IsChecked = cfg.IsNotch;
                P<RadioButton>("StyleFloating").IsChecked = !cfg.IsNotch;
                P<RadioButton>("IdleClock").IsChecked = !cfg.MinimalIdle;
                P<RadioButton>("IdleMinimal").IsChecked = cfg.MinimalIdle;
                P<CheckBox>("AutoStart").IsChecked = AutoStart.Enabled;
                P<CheckBox>("HideFullscreen").IsChecked = cfg.HideInFullscreen;
                P<CheckBox>("EdgeOpen").IsChecked = cfg.OpenOnEdge;
                P<CheckBox>("FadeHover").IsChecked = cfg.FadeOnHover;
                P<CheckBox>("GlassLook").IsChecked = cfg.Glass;

                P<TextBlock>("PhoneTitle").Text = _phone.Configured ? _phone.Name : "Your phone";
                P<TextBlock>("PhoneSub").Text = _phone.Configured ? _phone.StatusText : "Pair your phone with this PC over Bluetooth first";
                P<Ellipse>("PhoneDot").Fill = new SolidColorBrush(_phone.Online ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x8E, 0x8E, 0x93));
                int batt = _phone.Battery;
                P<TextBlock>("PhoneBattery").Text = batt >= 0 ? batt + "%" : "";
                P<TextBlock>("PhoneBolt").Visibility = batt >= 0 && _phone.BatteryCharging ? Visibility.Visible : Visibility.Collapsed;
                P<Button>("SendClip").IsEnabled = _phone.CanAct;
                P<TextBlock>("LinkSub").Text = _phone.LinkConnected
                    ? "Connected over Bluetooth. Copy on the phone → tap “Copy to laptop” → Ctrl+V here."
                    : _phone.Link != null && !_phone.Link.Listening
                        ? "Turn Bluetooth on to let the phone app connect."
                        : "Install once on your phone: clipboard, battery and ring over Bluetooth. No other apps.";

                BuildQuickApps();

                var list = P<StackPanel>("ClipList");
                list.Children.Clear();
                foreach (var item in _phone.ClipHistory.Take(6)) list.Children.Add(ClipRow(item.Key, item.Value));
                P<TextBlock>("ClipEmpty").Visibility = _phone.ClipHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _loading = false; }
        }

        void SaveApps(List<KeyValuePair<string, string>> apps)
        {
            Settings.WriteApps(apps);
            _island.ReloadSettings();
            Refresh();
        }

        void BuildQuickApps()
        {
            var panel = P<WrapPanel>("QuickAppsList");
            panel.Children.Clear();
            var apps = _island.Settings.Apps.ToList();
            var ghost = (Style)_root.FindResource("Ghost");
            for (int i = 0; i < apps.Count; i++)
            {
                int index = i;
                var app = apps[i];
                var chip = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(8, 6, 4, 6), Margin = new Thickness(0, 0, 8, 8),
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var img = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                string target = app.Value;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    try { img.Source = Native.ShellIcon(File.Exists(target) ? target : "shell:AppsFolder\\" + target, 48); } catch { }
                }));
                row.Children.Add(img);
                row.Children.Add(new TextBlock { Text = app.Key, Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 1) });
                Button Small(string glyph, string tip, Action act)
                {
                    var b = new Button
                    {
                        Style = ghost, Height = 24, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(2, 0, 0, 0), ToolTip = tip, Background = Brushes.Transparent,
                        Content = new TextBlock { Text = glyph, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)) },
                    };
                    b.Click += (s, e) => act();
                    return b;
                }
                if (index > 0) row.Children.Add(Small("‹", "Move left", () => { var l = apps.ToList(); var x = l[index]; l.RemoveAt(index); l.Insert(index - 1, x); SaveApps(l); }));
                if (index < apps.Count - 1) row.Children.Add(Small("›", "Move right", () => { var l = apps.ToList(); var x = l[index]; l.RemoveAt(index); l.Insert(index + 1, x); SaveApps(l); }));
                row.Children.Add(Small("×", "Remove", () => { var l = apps.ToList(); l.RemoveAt(index); SaveApps(l); }));
                chip.Child = row;
                panel.Children.Add(chip);
            }
            if (apps.Count == 0)
                panel.Children.Add(new TextBlock { Text = "No quick apps yet. Add your favourites.", Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255)), FontSize = 12 });
        }

        UIElement ClipRow(DateTime at, string text)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 8) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = PhoneService.Preview(text, 140), Foreground = Brushes.White, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 36,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock { Text = at.ToString("h:mm tt"), Foreground = new SolidColorBrush(Color.FromArgb(0x73, 255, 255, 255)), FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0) });
            box.Child = stack;
            grid.Children.Add(box);
            var copy = new Button { Style = (Style)_root.FindResource("Ghost"), Content = "Copy", Height = 30, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            copy.Click += (s, e) => copy.Content = PhoneService.CopyText(text) ? "Copied" : "Copy";
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);
            return grid;
        }

        static void Flash(TextBlock target, string text)
        {
            target.Text = text;
        }

        void ShowPhoneApp()
        {
            string dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string apk = System.IO.Path.Combine(dir, "Isle-Phone.apk");
            if (!File.Exists(apk)) apk = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir), "dist", "Isle-Phone.apk");
            if (!File.Exists(apk))
            {
                MessageBox.Show(this, "The phone app (Isle-Phone.apk) wasn't found next to Isle. Reinstall Isle with the latest IsleSetup.exe.", "Isle");
                return;
            }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + apk + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
            MessageBox.Show(this,
                "Isle-Phone.apk is selected in the folder that just opened.\n\n" +
                "1. Send it to your Pixel: right-click it → Send with Quick Share (or USB / email).\n" +
                "2. On the phone, open it and tap Install (allow “install unknown apps” if asked).\n" +
                "3. Open Isle on the phone, allow Bluetooth + notifications, pick this laptop (" + Environment.MachineName + ").\n\n" +
                "From then on it reconnects by itself over Bluetooth. To send what you copied on the phone: tap “Copy to laptop” " +
                "in Isle's notification or Quick Settings tile, or share text to Isle. Then press Ctrl+V on the laptop.",
                "Get Isle on your phone");
        }

        static BitmapSource LoadIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isle.isle.ico"))
                {
                    var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                }
            }
            catch { return null; }
        }
    }
}
