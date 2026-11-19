using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Isle
{
    /// <summary>
    /// Hand-drawn (vector) earbuds for the "connected" card, so it shows your actual earphones:
    /// Galaxy Buds Live (beans), AirPods (stems), other buds (round), else headphones.
    /// Each bud swings in from the side with a spring.
    /// </summary>
    static class EarbudsArt
    {
        public static UIElement Create(string deviceName, string color)
        {
            string n = (deviceName ?? "").ToLowerInvariant();
            var canvas = new Canvas { Width = 60, Height = 60 };
            if (n.Contains("buds live")) BudsLive(canvas, Shell(color));
            else if (n.Contains("airpods")) AirPods(canvas);
            else if (n.Contains("buds") || n.Contains("earbud") || n.Contains("wf-")) RoundBuds(canvas, Shell(color));
            else
            {
                return new TextBlock
                {
                    Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            return canvas;
        }

        /// <summary>Galaxy Buds Live colours (buds_color in settings).</summary>
        static (Color Light, Color Dark) Shell(string color)
        {
            switch ((color ?? "").ToLowerInvariant())
            {
                case "white": return (Color.FromRgb(0xFA, 0xFA, 0xFA), Color.FromRgb(0xC9, 0xCB, 0xD0));
                case "bronze": return (Color.FromRgb(0xF0, 0xC9, 0xA2), Color.FromRgb(0xB3, 0x7E, 0x52));
                case "blue": return (Color.FromRgb(0x8F, 0xB8, 0xF0), Color.FromRgb(0x3A, 0x63, 0xA8));
                case "red": return (Color.FromRgb(0xF2, 0x6B, 0x6B), Color.FromRgb(0xA3, 0x22, 0x2B));
                case "silver": return (Color.FromRgb(0xE4, 0xE6, 0xEA), Color.FromRgb(0x9A, 0x9E, 0xA6));
                default: return (Color.FromRgb(0x5A, 0x5C, 0x63), Color.FromRgb(0x18, 0x19, 0x1D));   // black
            }
        }

        static Brush Glossy((Color Light, Color Dark) c) => new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(c.Light, 0), new GradientStop(Blend(c.Light, c.Dark, 0.45), 0.55), new GradientStop(c.Dark, 1),
            }, new Point(0.2, 0), new Point(0.8, 1));

        static Color Blend(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        // ---------- Galaxy Buds Live: two kidney beans with a speaker grille ----------
        static void BudsLive(Canvas c, (Color Light, Color Dark) shell)
        {
            // one bean, drawn in a 30x20 box
            const string bean = "M3,10 C3,3.5 10,0.5 16.5,1.5 C23.5,2.6 28.5,7.5 27.5,12.8 C26.6,17.6 21,19.6 16,17.8 " +
                                "C13.3,16.8 12,14.8 9.5,15.6 C6.2,16.7 3,15.5 3,10 Z";
            Add(c, Bud(bean, shell, 30, 20, grilleX: 18, grilleY: 6), x: 1, y: 13, fromX: -16, fromAngle: -40, angle: -18, delay: 0);
            Add(c, Bud(bean, shell, 30, 20, grilleX: 18, grilleY: 6, mirror: true), x: 29, y: 25, fromX: 16, fromAngle: 40, angle: 18, delay: 90);
        }

        // ---------- AirPods: white heads with stems ----------
        static void AirPods(Canvas c)
        {
            const string pod = "M7,1.5 C11.5,1.5 14,4.6 14,8.2 C14,10.6 12.7,12.4 11,13.4 L10.6,27 C10.6,28.6 9.4,29.6 8,29.6 " +
                               "C6.6,29.6 5.4,28.6 5.4,27 L5.2,14.2 C2.6,13.2 0.8,11 0.8,8.2 C0.8,4.6 3,1.5 7,1.5 Z";
            var white = (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xC8, 0xCB, 0xD2));
            Add(c, Bud(pod, white, 15, 31, grilleX: 9.5, grilleY: 5.5), x: 12, y: 13, fromX: -16, fromAngle: -35, angle: 12, delay: 0);
            Add(c, Bud(pod, white, 15, 31, grilleX: 9.5, grilleY: 5.5, mirror: true), x: 33, y: 13, fromX: 16, fromAngle: 35, angle: -12, delay: 90);
        }

        // ---------- other earbuds: rounded buds with a tip ----------
        static void RoundBuds(Canvas c, (Color Light, Color Dark) shell)
        {
            const string bud = "M11,2 C16.5,2 20,6 20,11 C20,16 16.5,20 11,20 C8.5,20 6.6,19 5.2,17.6 L2.2,18.4 C1.2,18.7 0.4,17.8 0.8,16.9 " +
                               "L2.2,13.6 C2,12.8 2,11.9 2,11 C2,6 5.5,2 11,2 Z";
            Add(c, Bud(bud, shell, 21, 21, grilleX: 12, grilleY: 7), x: 5, y: 19, fromX: -16, fromAngle: -40, angle: -8, delay: 0);
            Add(c, Bud(bud, shell, 21, 21, grilleX: 12, grilleY: 7, mirror: true), x: 33, y: 19, fromX: 16, fromAngle: 40, angle: 8, delay: 90);
        }

        static FrameworkElement Bud(string path, (Color Light, Color Dark) shell, double w, double h, double grilleX, double grilleY, bool mirror = false)
        {
            var g = new Grid { Width = w, Height = h };
            g.Children.Add(new Path
            {
                Data = Geometry.Parse(path), Fill = Glossy(shell),
                Stroke = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), StrokeThickness = 0.6,
            });
            // soft highlight
            g.Children.Add(new Ellipse
            {
                Width = w * 0.32, Height = h * 0.18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(w * 0.2, h * 0.12, 0, 0), Fill = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)),
                RenderTransform = new RotateTransform(-14),
            });
            // speaker grille / sensor
            g.Children.Add(new Ellipse
            {
                Width = 4.5, Height = 2.6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(grilleX, grilleY, 0, 0), Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            });
            if (mirror) { g.RenderTransformOrigin = new Point(0.5, 0.5); g.LayoutTransform = new ScaleTransform(-1, 1); }
            return g;
        }

        /// <summary>Places a bud and springs it in from the side.</summary>
        static void Add(Canvas c, FrameworkElement bud, double x, double y, double fromX, double fromAngle, double angle, int delay)
        {
            Canvas.SetLeft(bud, x);
            Canvas.SetTop(bud, y);
            var move = new TranslateTransform(fromX, 0);
            var turn = new RotateTransform(fromAngle);
            var group = new TransformGroup();
            group.Children.Add(turn);
            group.Children.Add(move);
            bud.RenderTransform = group;
            bud.RenderTransformOrigin = new Point(0.5, 0.5);
            bud.Opacity = 0;
            c.Children.Add(bud);

            var begin = TimeSpan.FromMilliseconds(120 + delay);
            var spring = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(750)) { BeginTime = begin, EasingFunction = spring });
            turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(750)) { BeginTime = begin, EasingFunction = spring });
            bud.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin });
        }
    }
}
