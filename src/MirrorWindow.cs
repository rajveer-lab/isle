using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Isle
{
    /// <summary>
    /// Your phone's screen, live, in a phone-shaped window that drops out of the island.
    /// Picture: JPEG frames over Wi-Fi from the Isle phone app. Click = tap, drag = swipe, wheel = scroll, keys = typing.
    /// </summary>
    sealed class MirrorWindow : Window
    {
        readonly string _ip, _token;
        readonly int _port;
        readonly double _aspect;
        readonly Image _screen = new Image { Stretch = Stretch.Uniform };
        readonly TextBlock _status = new TextBlock();
        readonly Border _hint;
        TcpClient _tcp;
        StreamWriter _cmd;
        readonly object _cmdLock = new object();
        volatile bool _closed;
        Point _down;
        DateTime _downAt;
        bool _pressed;
        int _frames;

        public MirrorWindow(string phoneName, string ip, int port, string token, int width, int height)
        {
            _ip = ip; _port = port; _token = token;
            _aspect = height > 0 ? (double)height / width : 2.2;
            Title = phoneName + " screen";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = true;

            var wa = SystemParameters.WorkArea;
            double screenH = Math.Min(wa.Height - 140, 820);
            double screenW = screenH / _aspect;
            Width = screenW + 20 + 24;
            Height = screenH + 20 + 24 + 44 + 34;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + 44;

            // phone body
            var screenHost = new Grid { Width = screenW, Height = screenH, Background = Brushes.Black, Cursor = Cursors.Hand };
            screenHost.Children.Add(_screen);
            _status.Text = "Connecting to " + phoneName + "…";
            _status.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255));
            _status.FontSize = 13;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.TextAlignment = TextAlignment.Center;
            _status.Margin = new Thickness(24);
            _status.VerticalAlignment = VerticalAlignment.Center;
            screenHost.Children.Add(_status);
            screenHost.Clip = new RectangleGeometry(new Rect(0, 0, screenW, screenH), 26, 26);
            var body = new Border
            {
                CornerRadius = new CornerRadius(36), Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x3A, 255, 255, 255)), BorderThickness = new Thickness(1.5),
                Padding = new Thickness(10), Child = screenHost,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Opacity = 0.55 },
            };

            // top bar (drag to move, close)
            var top = new Grid { Height = 34, Margin = new Thickness(18, 0, 18, 6), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            top.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), Padding = new Thickness(14, 0, 6, 0),
                Child = new DockPanel
                {
                    Children =
                    {
                        Dock(CircleButton("", "Close", Close), System.Windows.Controls.Dock.Right),
                        new TextBlock { Text = phoneName, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            });
            top.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            // navigation bar
            var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            nav.Children.Add(CircleButton("", "Back", () => Send("BACK")));
            nav.Children.Add(CircleButton("", "Home", () => Send("HOME")));
            nav.Children.Add(CircleButton("", "Recent apps", () => Send("RECENTS")));

            _hint = new Border
            {
                CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x20)), Padding = new Thickness(12, 8, 12, 9),
                Margin = new Thickness(26, 0, 26, 70), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = "To tap and type from here: on the phone, open Isle › “Let the laptop tap and type” and turn it on.",
                    Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
            };

            var layout = new DockPanel { Margin = new Thickness(12) };
            System.Windows.Controls.DockPanel.SetDock(top, System.Windows.Controls.Dock.Top);
            System.Windows.Controls.DockPanel.SetDock(nav, System.Windows.Controls.Dock.Bottom);
            layout.Children.Add(top);
            layout.Children.Add(nav);
            layout.Children.Add(body);
            var rootGrid = new Grid();
            rootGrid.Children.Add(layout);
            rootGrid.Children.Add(_hint);
            Content = rootGrid;

            // drop out of the island
            var scale = new ScaleTransform(0.86, 0.6);
            rootGrid.RenderTransform = scale;
            rootGrid.RenderTransformOrigin = new Point(0.5, 0);
            rootGrid.Opacity = 0;
            Loaded += (s, e) =>
            {
                var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                rootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
                Connect();
            };

            // input: click = tap, hold = long press, drag = swipe, wheel = scroll, keys = typing
            screenHost.MouseLeftButtonDown += (s, e) => { _down = e.GetPosition(_screen); _downAt = DateTime.Now; _pressed = true; screenHost.CaptureMouse(); };
            screenHost.MouseLeftButtonUp += (s, e) =>
            {
                if (!_pressed) return;
                _pressed = false;
                screenHost.ReleaseMouseCapture();
                var up = e.GetPosition(_screen);
                double ms = (DateTime.Now - _downAt).TotalMilliseconds;
                if ((up - _down).Length < 8)
                    Send((ms > 500 ? "LONG " : "TAP ") + Norm(_down));
                else
                    Send("SWIPE " + Norm(_down) + " " + Norm(up) + " " + (int)Math.Max(120, Math.Min(900, ms)));
            };
            screenHost.MouseWheel += (s, e) =>
            {
                var p = e.GetPosition(_screen);
                double dy = e.Delta > 0 ? 0.22 : -0.22;
                Send("SWIPE " + Norm(p) + " " + Norm(new Point(p.X, p.Y + dy * _screen.ActualHeight)) + " 260");
            };
            TextInput += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Text) || e.Text == "\r" || e.Text == "\b") return;
                Send("TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Text)));
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Back) { Send("BKSP"); e.Handled = true; }
                else if (e.Key == Key.Enter) { Send("ENTER"); e.Handled = true; }
                else if (e.Key == Key.Escape) { Send("BACK"); e.Handled = true; }
            };
            Closed += (s, e) => Stop();
        }

        static UIElement Dock(UIElement e, System.Windows.Controls.Dock d) { System.Windows.Controls.DockPanel.SetDock(e, d); return e; }

        Button CircleButton(string glyph, string tip, Action act)
        {
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(6, 0, 6, 0), ToolTip = tip, Cursor = Cursors.Hand, Focusable = false,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = Brushes.White },
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                    "<Border x:Name='B' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='15' Background='#FF1C1C20'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#FF34343A'/></Trigger></ControlTemplate.Triggers></ControlTemplate>"),
            };
            b.Click += (s, e) => act();
            return b;
        }

        string Norm(Point p)
        {
            double w = Math.Max(1, _screen.ActualWidth), h = Math.Max(1, _screen.ActualHeight);
            return (Math.Max(0, Math.Min(1, p.X / w))).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " " +
                   (Math.Max(0, Math.Min(1, p.Y / h))).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        }

        void Send(string line)
        {
            lock (_cmdLock)
            {
                try { _cmd?.WriteLine(line); } catch { }
            }
        }

        async void Connect()
        {
            try
            {
                _tcp = new TcpClient { NoDelay = true };
                var connect = _tcp.ConnectAsync(_ip, _port);
                if (await Task.WhenAny(connect, Task.Delay(8000)) != connect) throw new TimeoutException();
                await connect;
                var net = _tcp.GetStream();
                _cmd = new StreamWriter(net, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                Send("AUTH " + _token);
                App.Log(new Exception("Mirror: connected to " + _ip + ":" + _port));
                _ = Task.Run(() => ReadLoop(net));
            }
            catch (Exception ex)
            {
                App.Log(new Exception("Mirror: couldn't reach the phone at " + _ip + ":" + _port + " - " + ex.Message));
                _status.Text = "Couldn't reach the phone over Wi-Fi.\n\nMake sure the phone and the laptop are on the same Wi-Fi network (not a guest network), then try again.";
            }
        }

        void ReadLoop(NetworkStream net)
        {
            try
            {
                var reader = new BinaryReader(net);
                while (!_closed)
                {
                    int len = IPAddress.NetworkToHostOrder(reader.ReadInt32());
                    if (len == -1) { Dispatcher.BeginInvoke(new Action(() => _hint.Visibility = Visibility.Visible)); continue; }
                    if (len <= 0 || len > 16 * 1024 * 1024) break;
                    byte[] data = reader.ReadBytes(len);
                    if (data.Length < len) break;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(data);
                    bmp.EndInit();
                    bmp.Freeze();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _screen.Source = bmp;
                        if (_frames++ == 0) _status.Visibility = Visibility.Collapsed;
                    }));
                }
            }
            catch { }
            if (!_closed) Dispatcher.BeginInvoke(new Action(() =>
            {
                _status.Visibility = Visibility.Visible;
                _status.Text = "Mirroring stopped on the phone.";
                _screen.Opacity = 0.35;
            }));
        }

        void Stop()
        {
            _closed = true;
            try { _tcp?.Close(); } catch { }
        }
    }
}
