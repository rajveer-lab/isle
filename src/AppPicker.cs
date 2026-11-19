using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>Searchable list of every installed app (the same list as Start › All apps).</summary>
    sealed class AppPicker : Window
    {
        public KeyValuePair<string, string>? Picked { get; private set; }
        readonly List<KeyValuePair<string, string>> _all;
        readonly StackPanel _list = new StackPanel();
        readonly TextBox _search;

        public AppPicker(Window owner)
        {
            Owner = owner;
            Title = "Add an app";
            Width = 420;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            _all = InstalledApps();

            var frame = new Border
            {
                Margin = new Thickness(12), CornerRadius = new CornerRadius(18), Padding = new Thickness(16),
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.5 },
            };
            var root = new DockPanel();
            var head = new DockPanel { Margin = new Thickness(2, 0, 0, 12) };
            var close = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 5), Cursor = Cursors.Hand, Foreground = Brushes.White,
                                     Background = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)), BorderThickness = new Thickness(0) };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(new TextBlock { Text = "Add an app", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            _search = new TextBox
            {
                FontSize = 14, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)), Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            };
            _search.TextChanged += (s, e) => Fill();
            DockPanel.SetDock(_search, Dock.Top);
            root.Children.Add(_search);

            root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            frame.Child = root;
            Content = frame;
            frame.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == frame) DragMove(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) => { _search.Focus(); Fill(); };
        }

        void Fill()
        {
            _list.Children.Clear();
            string q = _search.Text.Trim();
            foreach (var app in _all.Where(a => q.Length == 0 || a.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(150))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var img = new Image { Width = 26, Height = 26, Margin = new Thickness(0, 0, 12, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                row.Children.Add(img);
                row.Children.Add(new TextBlock { Text = app.Key, Foreground = Brushes.White, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button
                {
                    Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 2),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Foreground = Brushes.White,
                };
                var picked = app;
                b.Click += (s, e) => { Picked = picked; DialogResult = true; };
                _list.Children.Add(b);
                // icons load a moment later so the list appears instantly
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try { img.Source = Native.ShellIcon("shell:AppsFolder\\" + picked.Value, 48); } catch { }
                }));
            }
        }

        /// <summary>Name and AppID of every app in Start › All apps.</summary>
        static List<KeyValuePair<string, string>> InstalledApps()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, id = item.Path;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) list.Add(new KeyValuePair<string, string>(name, id));
                }
            }
            catch (Exception ex) { App.Log(ex); }
            return list.GroupBy(a => a.Key).Select(g => g.First()).OrderBy(a => a.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
