using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle
{
    struct PowerInfo
    {
        public bool HasBattery, OnAc;
        public int Percent;
    }

    static class Native
    {
        // ---------- window styles ----------
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Never steals focus, never shows in Alt+Tab or the taskbar.</summary>
        public static void MakeOverlay(IntPtr hwnd)
        {
            long ex = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);
            ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)ex);
        }

        /// <summary>WS_EX_TRANSPARENT on/off: when on, every click goes straight to the window underneath (e.g. Chrome tabs).</summary>
        public static void SetClickThrough(IntPtr hwnd, bool on)
        {
            const long WS_EX_TRANSPARENT = 0x20;
            long ex = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);
            long next = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            if (next == ex) return;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(next));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)next);
        }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        public static bool MouseButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

        // low-level keyboard hook (to take over the volume keys so Windows' own volume pop-up never shows)
        public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public static void ReassertTopmost(IntPtr hwnd)
        {
            // HWND_TOPMOST, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        // ---------- fullscreen detection ----------
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        public static bool IsFullscreenAppOnMonitorOf(IntPtr self)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            var sb = new StringBuilder(64);
            GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "WorkerW" || cls == "Progman" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            IntPtr mon = MonitorFromWindow(fg, 2);
            if (mon != MonitorFromWindow(self, 2)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out RECT r)) return false;
            var m = mi.rcMonitor;
            bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            if (!covers) return false;
            // A maximized window (auto-hide taskbar) also covers the screen but keeps its caption and overhangs the edges.
            bool exact = r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom;
            const int WS_CAPTION = 0x00C00000;
            bool captioned = (GetWindowLong32(fg, -16) & WS_CAPTION) == WS_CAPTION;
            return exact || !captioned;
        }

        // ---------- window enumeration (used to spot call windows / notifications) ----------
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc p, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        public static bool WindowSize(IntPtr h, out int width, out int height)
        {
            width = height = 0;
            if (!GetWindowRect(h, out RECT r)) return false;
            width = r.Right - r.Left;
            height = r.Bottom - r.Top;
            return true;
        }

        // clipboard change notifications (used to spot text arriving from the phone)
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();

        public static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string WindowClass(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static System.Collections.Generic.List<IntPtr> VisibleWindows()
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            EnumWindows((h, l) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        /// <summary>The shell window that shows Windows toast notifications (Win10/11), if it is on screen.</summary>
        public static IntPtr FindToastWindow()
        {
            IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Windows.UI.Core.CoreWindow", "New notification");
            return h != IntPtr.Zero && IsWindowVisible(h) ? h : IntPtr.Zero;
        }

        // ---------- bring a window forward ----------
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        public static void BringToFront(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
        }

        // ---------- power ----------
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        public static PowerInfo GetPower()
        {
            if (!GetSystemPowerStatus(out var s)) return new PowerInfo { HasBattery = false, OnAc = true, Percent = 100 };
            return new PowerInfo
            {
                HasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100,
                OnAc = s.ACLineStatus == 1,
                Percent = Math.Min(100, (int)s.BatteryLifePercent),
            };
        }

        // ---------- shell icons (works for Store apps, desktop apps and files) ----------
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

        public static BitmapSource ShellIcon(string parsingName, int size)
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
            try
            {
                // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out IntPtr hbm) != 0 || hbm == IntPtr.Zero) return null;
                try { return HBitmapToSource(hbm); }
                finally { DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        static BitmapSource HBitmapToSource(IntPtr hbm)
        {
            if (GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), out BITMAP bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bmi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            src.Freeze();
            return src;
        }
    }
}
