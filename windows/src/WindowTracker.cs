using System;
using System.Collections.Generic;

namespace Snowdrift
{
    /// <summary>The window in front and what kind it is.</summary>
    internal sealed class ForegroundInfo
    {
        public IntPtr Hwnd;
        public uint Pid;
        public IntPtr Monitor;
        public Native.RECT Rect;
        public bool Zoomed;
        public bool IsShell;
        public bool IsOwn;
        /// <summary>Windows itself says a full-screen app or game is running.</summary>
        public bool SystemBusy;

        private static double lastQuns = -10;
        private static bool lastBusy;

        public static ForegroundInfo Read(double now)
        {
            var f = new ForegroundInfo();
            IntPtr h = Native.GetForegroundWindow();
            f.Hwnd = h;
            if (now - lastQuns > 0.5)
            {
                lastQuns = now;
                // 2: busy (full-screen app), 3: Direct3D full screen, 4: presentation mode.
                lastBusy = Native.SHQueryUserNotificationState(out int state) == 0 && (state == 2 || state == 3 || state == 4);
            }
            f.SystemBusy = lastBusy;
            if (h == IntPtr.Zero) return f;
            Native.GetWindowThreadProcessId(h, out f.Pid);
            f.IsOwn = f.Pid == (uint)Environment.ProcessId;
            string cls = Native.ClassName(h);
            f.IsShell = cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
            f.Monitor = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
            Native.GetWindowRect(h, out f.Rect);
            f.Zoomed = Native.IsZoomed(h);
            return f;
        }

        /// <summary>A full-screen app or game covers this monitor.</summary>
        public bool FullScreenOn(MonitorInfo m)
        {
            if (Hwnd == IntPtr.Zero || IsShell || IsOwn || Monitor != m.Handle) return false;
            var b = m.Bounds;
            bool covers = Rect.Left <= b.Left + 1 && Rect.Top <= b.Top + 1 && Rect.Right >= b.Right - 1 && Rect.Bottom >= b.Bottom - 1;
            if (covers && !Zoomed) return true;
            return SystemBusy;
        }
    }

    internal sealed class WindowSnapshot
    {
        public int Version;
        /// <summary>Front to back, physical pixels.</summary>
        public List<(long Id, Native.RECT Rect)> Windows = new List<(long, Native.RECT)>();
        /// <summary>Taskbars that are visible right now, physical pixels.</summary>
        public List<Native.RECT> Taskbars = new List<Native.RECT>();
    }

    /// <summary>Reads where windows are. Only their frames: no titles, no contents, and no special permission.</summary>
    internal sealed class WindowTracker
    {
        public static readonly WindowTracker Shared = new WindowTracker();

        public WindowSnapshot Snapshot { get; private set; } = new WindowSnapshot();
        private double lastPoll = -10, lastChange;
        private readonly uint ownPid = (uint)Environment.ProcessId;

        /// <summary>Polls every frame while something is being dragged, a few times a second otherwise.</summary>
        public WindowSnapshot Poll(double now, ForegroundInfo fg, List<MonitorInfo> monitors)
        {
            bool busy = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 || now - lastChange < 0.7;
            double interval = busy ? 0.008 : 0.25;
            if (now - lastPoll < interval) return Snapshot;
            lastPoll = now;

            var windows = ReadWindows();
            var bars = ReadTaskbars(fg, monitors);
            if (!Same(windows, bars))
            {
                Snapshot = new WindowSnapshot { Version = Snapshot.Version + 1, Windows = windows, Taskbars = bars };
                lastChange = now;
            }
            return Snapshot;
        }

        public void Invalidate() => lastPoll = -10;

        private bool Same(List<(long Id, Native.RECT Rect)> w, List<Native.RECT> bars)
        {
            var s = Snapshot;
            if (s.Windows.Count != w.Count || s.Taskbars.Count != bars.Count) return false;
            for (int i = 0; i < w.Count; i++)
            {
                if (s.Windows[i].Id != w[i].Id || !Eq(s.Windows[i].Rect, w[i].Rect)) return false;
            }
            for (int i = 0; i < bars.Count; i++) if (!Eq(s.Taskbars[i], bars[i])) return false;
            return true;
        }

        private static bool Eq(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        private List<(long, Native.RECT)> ReadWindows()
        {
            var list = new List<(long, Native.RECT)>();
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == ownPid) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                // Click-through overlays and tool windows (tooltips, popups) are not real windows.
                if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                // Windows on other virtual desktops and suspended apps are "cloaked".
                if (Native.IsCloaked(h)) return true;
                string cls = Native.ClassName(h);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                    cls == "Windows.UI.Core.CoreWindow" || cls == "NotifyIconOverflowWindow" ||
                    cls == "TopLevelWindowForOverflowXamlIsland" || cls == "XamlExplorerHostIslandWindow")
                    return true;
                if ((ex & Native.WS_EX_LAYERED) != 0 &&
                    Native.GetLayeredWindowAttributes(h, out _, out byte alpha, out uint flags) &&
                    (flags & Native.LWA_ALPHA) != 0 && alpha < 13)
                    return true;
                var r = Native.FrameBounds(h);
                if (r.Width < 120 || r.Height < 60) return true;
                list.Add((h.ToInt64(), r));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static List<Native.RECT> ReadTaskbars(ForegroundInfo fg, List<MonitorInfo> monitors)
        {
            var bars = new List<IntPtr>();
            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) bars.Add(primary);
            IntPtr prev = IntPtr.Zero;
            for (int i = 0; i < 16; i++)
            {
                prev = Native.FindWindowEx(IntPtr.Zero, prev, "Shell_SecondaryTrayWnd", null);
                if (prev == IntPtr.Zero) break;
                bars.Add(prev);
            }
            var list = new List<Native.RECT>();
            foreach (var h in bars)
            {
                if (!Native.IsWindowVisible(h) || Native.IsCloaked(h)) continue;
                if (!Native.GetWindowRect(h, out var r) || r.Width <= r.Height) continue;
                MonitorInfo mon = null;
                foreach (var m in monitors)
                {
                    if (m.Bounds.Contains(r.Left + r.Width / 2, r.Top + r.Height / 2)) { mon = m; break; }
                }
                if (mon == null) continue;
                // Only a taskbar along the bottom edge, and only while it is actually shown
                // (an auto-hidden taskbar slides almost entirely off the screen).
                int visTop = Math.Max(r.Top, mon.Bounds.Top), visBottom = Math.Min(r.Bottom, mon.Bounds.Bottom);
                if (visBottom - visTop < r.Height * 0.6) continue;
                if (Math.Abs(r.Bottom - mon.Bounds.Bottom) > 4) continue;
                if (fg != null && fg.FullScreenOn(mon)) continue;
                list.Add(r);
            }
            return list;
        }

        /// <summary>Which of these windows still exist (minimized, other desktops), as opposed to closed.</summary>
        public static HashSet<long> Existing(HashSet<long> ids)
        {
            var found = new HashSet<long>();
            foreach (long id in ids) if (Native.IsWindow(new IntPtr(id))) found.Add(id);
            return found;
        }
    }
}
