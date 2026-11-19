using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>
    /// Frosted glass for a custom-shaped layered window. Windows has no backdrop blur for those, so this grabs the
    /// strip of screen behind the island (with the island itself excluded from the grab via
    /// WDA_EXCLUDEFROMCAPTURE), shrinks it with area averaging (a cheap blur) and hands the tiny bitmap to WPF,
    /// which stretches it back up with linear filtering plus a light blur shader. Runs only while the island is
    /// open, a few times a second, on a background thread. Nothing is stored.
    /// </summary>
    sealed class GlassBackdrop : IDisposable
    {
        public const int Downscale = 6;

        readonly Window _window;
        readonly Dispatcher _ui;
        readonly Action<BitmapSource> _onFrame;
        IntPtr _hwnd;
        Thread _thread;
        volatile bool _running;
        Int32Rect _rect;                 // physical pixels, read by the capture thread
        readonly object _lock = new object();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public GlassBackdrop(Window window, Action<BitmapSource> onFrame)
        {
            _window = window;
            _ui = window.Dispatcher;
            _onFrame = onFrame;
        }

        /// <summary>Screen rectangle (physical pixels) to show behind the glass.</summary>
        public void SetRect(Int32Rect r) { lock (_lock) _rect = r; }

        public void Start()
        {
            if (_running) { _wake.Set(); return; }
            if (_hwnd == IntPtr.Zero) _hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (_hwnd == IntPtr.Zero) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Isle glass", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop() { _running = false; _wake.Set(); }

        public void Dispose() => Stop();

        void Loop()
        {
            int frames = 0;
            while (_running)
            {
                Int32Rect r;
                lock (_lock) r = _rect;
                if (r.Width > 8 && r.Height > 8)
                {
                    try
                    {
                        var bmp = Grab(r);
                        if (bmp != null && _running) _ui.BeginInvoke(DispatcherPriority.Render, new Action(() => { if (_running) _onFrame(bmp); }));
                    }
                    catch (Exception ex) { if (frames == 0) App.Log(ex); }
                    frames++;
                }
                // the first frames come quickly so the glass is ready as the island opens, then ~7 per second
                _wake.WaitOne(frames < 3 ? 30 : 140);
            }
        }

        BitmapSource Grab(Int32Rect r)
        {
            int w = Math.Max(2, r.Width / Downscale), h = Math.Max(2, r.Height / Downscale);
            bool excluded = SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
            try
            {
                if (excluded) { DwmFlush(); DwmFlush(); }   // let the compositor drop the island from the captured image
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                IntPtr old = SelectObject(mem, dib);
                try
                {
                    SetStretchBltMode(mem, HALFTONE);
                    SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
                    StretchBlt(mem, 0, 0, w, h, screen, r.X, r.Y, r.Width, r.Height, SRCCOPY);
                    GdiFlush();
                    var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bits, w * h * 4, w * 4);
                    src.Freeze();
                    return src;
                }
                finally
                {
                    SelectObject(mem, old);
                    DeleteObject(dib);
                    DeleteDC(mem);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
            finally
            {
                // back to normal right away, so your own screenshots and recordings still show the island
                if (excluded) SetWindowDisplayAffinity(_hwnd, WDA_NONE);
            }
        }

        // ---------- interop ----------
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        const int HALFTONE = 4, SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
    }
}
