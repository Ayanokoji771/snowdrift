using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Snowdrift
{
    /// <summary>Windows API calls used by Snowdrift. Everything here only reads window geometry
    /// or manages Snowdrift's own windows.</summary>
    internal static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_EXSTYLE = -20;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint LWA_ALPHA = 0x2;

        public const uint WM_SETCURSOR = 0x0020;
        public const uint WM_MOUSEACTIVATE = 0x0021;
        public const uint WM_NCHITTEST = 0x0084;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_HOTKEY = 0x0312;
        public const int MA_NOACTIVATE = 3;
        public const int IDC_ARROW = 32512;
        public const int IDC_HAND = 32649;
        public const int VK_LBUTTON = 0x01;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint MONITORINFOF_PRIMARY = 1;
        public const uint ABM_GETSTATE = 4;
        public const uint ABS_AUTOHIDE = 1;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint GW_OWNER = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
            public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
            public bool Intersects(RECT o) => o.Left < Right && o.Right > Left && o.Top < Bottom && o.Bottom > Top;
            public override string ToString() => $"({Left},{Top} {Width}x{Height})";
        }

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
        [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32")] public static extern bool ReleaseCapture();
        [DllImport("user32")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32")] public static extern short GetAsyncKeyState(int key);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32")] public static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
        [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
        [DllImport("user32")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("shcore")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32")] public static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        [DllImport("shell32")] public static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr handle);

        public static string ClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static bool IsCloaked(IntPtr hWnd)
        {
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0;
        }

        /// <summary>Visible frame of a window, without the invisible resize borders.</summary>
        public static RECT FrameBounds(IntPtr hWnd)
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
                return r;
            GetWindowRect(hWnd, out r);
            return r;
        }

        public static void SetExStyle(IntPtr hWnd, int add, int remove)
        {
            long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            long next = (ex | (long)add) & ~(long)remove;
            if (next != ex) SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(next));
        }
    }

    /// <summary>One display, in physical pixels.</summary>
    internal sealed class MonitorInfo
    {
        public IntPtr Handle;
        public Native.RECT Bounds;
        public Native.RECT Work;
        public double Scale = 1;
        public bool Primary;

        public static System.Collections.Generic.List<MonitorInfo> All()
        {
            var list = new System.Collections.Generic.List<MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
            {
                var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                if (Native.GetMonitorInfo(h, ref info))
                {
                    double scale = 1;
                    try
                    {
                        if (Native.GetDpiForMonitor(h, 0, out uint dx, out uint _) == 0 && dx > 0) scale = dx / 96.0;
                    }
                    catch { }
                    list.Add(new MonitorInfo
                    {
                        Handle = h, Bounds = info.rcMonitor, Work = info.rcWork, Scale = scale,
                        Primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                    });
                }
                return true;
            }, IntPtr.Zero);
            list.Sort((a, b) => b.Primary.CompareTo(a.Primary));
            return list;
        }
    }
}
