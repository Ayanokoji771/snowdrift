using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Snowdrift
{
    internal static class MotionClock
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        public static double Now => Watch.Elapsed.TotalSeconds;
    }

    /// <summary>Virtual desktops: their order and which one is in front. Uses the documented
    /// IVirtualDesktopManager and the desktop order Windows keeps in the registry.</summary>
    internal static class VirtualDesktops
    {
        [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, out int onCurrent);
            [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid desktop);
            [PreserveSig] int MoveWindowToDesktop(IntPtr window, ref Guid desktop);
        }

        private static IVirtualDesktopManager manager;
        private static bool managerFailed;
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        private static IVirtualDesktopManager Manager
        {
            get
            {
                if (manager == null && !managerFailed)
                {
                    try
                    {
                        var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
                        manager = (IVirtualDesktopManager)Activator.CreateInstance(type);
                    }
                    catch (Exception e)
                    {
                        managerFailed = true;
                        Log.Write("Virtual desktops unavailable: " + e.Message);
                    }
                }
                return manager;
            }
        }

        public static Guid DesktopOf(IntPtr window)
        {
            var m = Manager;
            if (m == null) return Guid.Empty;
            try
            {
                IntPtr root = MotionNative.GetAncestor(window, MotionNative.GA_ROOTOWNER);
                return m.GetWindowDesktopId(root != IntPtr.Zero ? root : window, out Guid g) == 0 ? g : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        /// <summary>All desktops, left to right (empty when Windows has never had more than one).</summary>
        public static List<Guid> Order()
        {
            var list = new List<Guid>();
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                if (k?.GetValue("VirtualDesktopIDs") is byte[] b)
                    for (int i = 0; i + 16 <= b.Length; i += 16) list.Add(new Guid(new ReadOnlySpan<byte>(b, i, 16)));
            }
            catch { }
            return list;
        }

        /// <summary>The desktop in front: the one the topmost visible window is on, or what the registry says.</summary>
        public static Guid Current(List<Guid> order)
        {
            Guid found = Guid.Empty;
            Scene.ForEachAppWindow(h =>
            {
                if (Native.IsCloaked(h) || Native.IsIconic(h)) return true;
                Guid g = DesktopOf(h);
                if (g == Guid.Empty || (order.Count > 0 && !order.Contains(g))) return true;
                found = g;
                return false;
            });
            if (found != Guid.Empty) return found;
            string session = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{Process.GetCurrentProcess().SessionId}\VirtualDesktops";
            foreach (string path in new[] { Key, session })
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(path);
                    if (k?.GetValue("CurrentVirtualDesktop") is byte[] b && b.Length >= 16)
                    {
                        var g = new Guid(new ReadOnlySpan<byte>(b, 0, 16));
                        if (order.Count == 0 || order.Contains(g)) return g;
                    }
                }
                catch { }
            }
            return Guid.Empty;
        }
    }

    internal sealed class SceneWindow
    {
        public IntPtr Handle;
        public Native.RECT Rect;
    }

    /// <summary>Which windows take part in an animation. Only handles and frames — never titles or contents.</summary>
    internal static class Scene
    {
        private static readonly HashSet<string> ShellClasses = new HashSet<string>
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow",
            "MultitaskingViewFrame", "ForegroundStaging", "TaskSwitcherWnd", "Shell_InputSwitchTopLevelWindow",
        };
        private static readonly uint OwnPid = (uint)Environment.ProcessId;

        /// <summary>Windows a person would call windows, front to back: visible (or on another desktop), not ours, not the shell.</summary>
        public static void ForEachAppWindow(Func<IntPtr, bool> visit)
        {
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == OwnPid) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                string cls = Native.ClassName(h);
                if (ShellClasses.Contains(cls)) return true;
                if ((ex & Native.WS_EX_LAYERED) != 0 &&
                    Native.GetLayeredWindowAttributes(h, out _, out byte alpha, out uint flags) &&
                    (flags & Native.LWA_ALPHA) != 0 && alpha < 13)
                    return true;
                if (!Native.GetWindowRect(h, out var r) || r.Width < 40 || r.Height < 40) return true;
                // A suspended Store app leaves an empty, cloaked frame behind.
                if (cls == "ApplicationFrameWindow" && Native.IsCloaked(h) &&
                    Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null) == IntPtr.Zero)
                    return true;
                return visit(h);
            }, IntPtr.Zero);
        }

        private static List<SceneWindow> Collect(Func<IntPtr, bool> pick)
        {
            var list = new List<SceneWindow>();
            ForEachAppWindow(h =>
            {
                if (pick(h) && Native.GetWindowRect(h, out var r)) list.Add(new SceneWindow { Handle = h, Rect = r });
                return true;
            });
            return list;
        }

        public static List<SceneWindow> Here() => Collect(h => !Native.IsCloaked(h) && !Native.IsIconic(h));

        public static List<SceneWindow> OnDesktop(Guid id) =>
            Collect(h => Native.IsCloaked(h) && !Native.IsIconic(h) && VirtualDesktops.DesktopOf(h) == id);

        /// <summary>The desktop itself — wallpaper and icons — back to front.</summary>
        public static List<SceneWindow> Background()
        {
            var list = new List<SceneWindow>();
            Native.EnumWindows((h, l) =>
            {
                string cls = Native.ClassName(h);
                if ((cls == "Progman" || cls == "WorkerW") && Native.IsWindowVisible(h) &&
                    Native.GetWindowRect(h, out var r) && r.Width > 200 && r.Height > 200)
                    list.Add(new SceneWindow { Handle = h, Rect = r });
                return true;
            }, IntPtr.Zero);
            list.Reverse();
            return list;
        }

        public static bool DesktopInFront()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return true;
            string cls = Native.ClassName(h);
            return cls == "Progman" || cls == "WorkerW";
        }
    }

    /// <summary>Presses system shortcuts the way the keyboard would.</summary>
    internal static class Keys
    {
        public const ushort Ctrl = 0xA2, Win = 0x5B, Left = 0x25, Right = 0x27, Tab = 0x09, D = 0x44, Escape = 0x1B;

        public static void Chord(params ushort[] keys)
        {
            int n = keys.Length;
            var inputs = new MotionNative.INPUT[n * 2];
            for (int i = 0; i < n; i++)
            {
                inputs[i] = Key(keys[i], false);
                inputs[n * 2 - 1 - i] = Key(keys[i], true);
            }
            uint sent = MotionNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<MotionNative.INPUT>());
            if (sent != inputs.Length) Log.Write($"Keys: Windows took {sent} of {inputs.Length} key events");
        }

        private static MotionNative.INPUT Key(ushort vk, bool up)
        {
            uint flags = up ? MotionNative.KEYEVENTF_KEYUP : 0;
            if (vk == Win || vk == Left || vk == Right) flags |= MotionNative.KEYEVENTF_EXTENDEDKEY;
            return new MotionNative.INPUT
            {
                type = 1,
                u = new MotionNative.InputUnion
                {
                    ki = new MotionNative.KEYBDINPUT { wVk = vk, wScan = (ushort)MotionNative.MapVirtualKey(vk, 0), dwFlags = flags },
                },
            };
        }
    }

    /// <summary>Windows' own three- and four-finger swipes. Snowdrift's gestures act only when Windows'
    /// swipes for that many fingers are set to "Nothing", so a swipe never does two things.</summary>
    internal static class NativeGestures
    {
        private static double checkedAt = -10;
        private static int checkedFingers;
        private static bool on = true;

        public static bool On(int fingers)
        {
            double now = MotionClock.Now;
            if (fingers == checkedFingers && now - checkedAt < 1.5) return on;
            checkedFingers = fingers;
            checkedAt = now;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
                object v = k?.GetValue(fingers == 3 ? "ThreeFingerSlideEnabled" : "FourFingerSlideEnabled");
                on = !(v is int i && i == 0);
            }
            catch { on = true; }
            return on;
        }

        public static void OpenSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true }); }
            catch (Exception e) { Log.Write("Touchpad settings: " + e.Message); }
            checkedAt = -10;
        }

        /// <summary>Whether Windows animates (Settings → Accessibility → Visual effects → Animation effects).</summary>
        public static bool Animations()
        {
            return !MotionNative.SystemParametersInfo(MotionNative.SPI_GETCLIENTAREAANIMATION, 0, out int v, 0) || v != 0;
        }
    }

    /// <summary>A see-through, click-through window over one monitor, just below the taskbar, that shows
    /// live previews of windows (drawn by Windows itself, as in Alt+Tab) while they move with the fingers.</summary>
    internal sealed class StageWindow : IDisposable
    {
        /// <summary>Paint a solid desktop-coloured base under the previews (otherwise the window is fully clear).</summary>
        public static bool OpaqueBase = true;

        public readonly MonitorInfo Mon;
        public readonly IntPtr Hwnd;
        public readonly bool Opaque;
        public readonly List<Thumb> Thumbs = new List<Thumb>();
        private readonly int w, h;
        private byte alpha = 255;

        public sealed class Thumb
        {
            public IntPtr Id;
            public SceneWindow Win;
            /// <summary>0 the desktop, 1 windows here, 2 the desktop on the left, 3 the desktop on the right.</summary>
            public int Layer;
            public bool Applied, Visible;
            public int X0, Y0, X1, Y1;
            public byte Opacity;
        }

        private static readonly Native.WndProc Proc = WndProc;
        private static readonly HashSet<string> Registered = new HashSet<string>();

        public StageWindow(MonitorInfo mon)
        {
            Mon = mon;
            w = Math.Max(1, mon.Bounds.Width);
            h = Math.Max(1, mon.Bounds.Height);
            bool opaque = Opaque = OpaqueBase;
            string cls = opaque ? "SnowdriftStage" : "SnowdriftStageClear";
            IntPtr instance = Native.GetModuleHandle(null);
            if (Registered.Add(cls))
            {
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                    lpfnWndProc = Proc,
                    hInstance = instance,
                    hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_ARROW)),
                    hbrBackground = opaque ? MotionNative.CreateSolidBrush(MotionNative.GetSysColor(1)) : IntPtr.Zero,
                    lpszClassName = cls,
                };
                Native.RegisterClassEx(ref wc);
            }
            int ex = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED |
                     Native.WS_EX_TRANSPARENT | (opaque ? 0 : Native.WS_EX_NOREDIRECTIONBITMAP);
            Hwnd = Native.CreateWindowEx(ex, cls, "Snowdrift", Native.WS_POPUP, mon.Bounds.Left, mon.Bounds.Top, w, h,
                                         IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Hwnd != IntPtr.Zero) Native.SetLayeredWindowAttributes(Hwnd, 0, 255, Native.LWA_ALPHA);
        }

        public bool Covers(Native.RECT r) => r.Intersects(Mon.Bounds);

        /// <summary>Adds previews; the list is front to back, previews are stacked back to front.</summary>
        public void Add(List<SceneWindow> windows, int layer, bool everywhere = false)
        {
            if (Hwnd == IntPtr.Zero) return;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                var win = windows[i];
                if (!everywhere && !Covers(win.Rect)) continue;
                if (MotionNative.DwmRegisterThumbnail(Hwnd, win.Handle, out IntPtr id) != 0 || id == IntPtr.Zero) continue;
                Thumbs.Add(new Thumb { Id = id, Win = win, Layer = layer });
            }
        }

        /// <summary>Puts a preview at a screen rectangle (physical pixels).</summary>
        public void Place(Thumb t, float x0, float y0, float x1, float y1, float opacity)
        {
            int ax = (int)MathF.Round(x0) - Mon.Bounds.Left, ay = (int)MathF.Round(y0) - Mon.Bounds.Top;
            int bx = (int)MathF.Round(x1) - Mon.Bounds.Left, by = (int)MathF.Round(y1) - Mon.Bounds.Top;
            byte op = (byte)Math.Clamp((int)MathF.Round(opacity * 255), 0, 255);
            bool vis = op > 0 && bx > ax && by > ay && ax < w && bx > 0 && ay < h && by > 0;
            if (t.Applied && t.Visible == vis && (!vis || (t.X0 == ax && t.Y0 == ay && t.X1 == bx && t.Y1 == by && t.Opacity == op))) return;
            var p = new MotionNative.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = MotionNative.DWM_TNP_RECTDESTINATION | MotionNative.DWM_TNP_OPACITY | MotionNative.DWM_TNP_VISIBLE |
                          MotionNative.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT(ax, ay, bx, by),
                opacity = op,
                fVisible = vis,
                fSourceClientAreaOnly = false,
            };
            MotionNative.DwmUpdateThumbnailProperties(t.Id, ref p);
            t.Applied = true;
            t.Visible = vis;
            t.X0 = ax; t.Y0 = ay; t.X1 = bx; t.Y1 = by;
            t.Opacity = op;
        }

        public void Show()
        {
            if (Hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, Mon.Bounds.Left, Mon.Bounds.Top, w, h, Native.SWP_NOACTIVATE);
            // Just below the taskbar: like the Dock on a Mac, the taskbar stays put.
            IntPtr bar = Taskbar();
            if (bar != IntPtr.Zero)
                Native.SetWindowPos(Hwnd, bar, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            SetAlpha(1);
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
        }

        public void SetAlpha(float a)
        {
            byte b = (byte)Math.Clamp((int)MathF.Round(a * 255), 0, 255);
            if (b == alpha) return;
            alpha = b;
            Native.SetLayeredWindowAttributes(Hwnd, 0, b, Native.LWA_ALPHA);
        }

        public void Hide()
        {
            if (Hwnd == IntPtr.Zero) return;
            Native.ShowWindow(Hwnd, Native.SW_HIDE);
            foreach (var t in Thumbs) MotionNative.DwmUnregisterThumbnail(t.Id);
            Thumbs.Clear();
            alpha = 0;
        }

        private IntPtr Taskbar()
        {
            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (Mon.Primary || primary == IntPtr.Zero) return primary;
            IntPtr prev = IntPtr.Zero;
            for (int i = 0; i < 16; i++)
            {
                prev = Native.FindWindowEx(IntPtr.Zero, prev, "Shell_SecondaryTrayWnd", null);
                if (prev == IntPtr.Zero) break;
                if (Native.GetWindowRect(prev, out var r) && Mon.Bounds.Contains(r.Left + r.Width / 2, r.Top + r.Height / 2)) return prev;
            }
            return primary;
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_MOUSEACTIVATE) return new IntPtr(Native.MA_NOACTIVATE);
            if (msg == Native.WM_NCHITTEST) return new IntPtr(-1);
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            Hide();
            if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
        }
    }

    /// <summary>Mac-style touchpad gestures for Windows. Swipe sideways and the desktops slide with the
    /// fingers; swipe down and every window shrinks into the taskbar; swipe up and they fly back out.
    /// A released swipe settles on a soft spring, with momentum, and stretches like rubber where there
    /// is nowhere to go. Snowdrift draws nothing of its own here: Windows renders the window previews.</summary>
    internal sealed class SmoothGestures : IDisposable
    {
        public bool Enabled = true;
        /// <summary>Tests: act even though Windows' own swipes are on.</summary>
        public bool ForceActive;
        public float PadWidthMm = 100, PadHeightMm = 65;
        /// <summary>How visible the snow on windows should be (it steps aside while windows fly around).</summary>
        public float SnowWindowAlpha { get; private set; } = 1;
        public bool Busy => mode != Mode.None;
        /// <summary>What the last finished gesture did (tests and the log).</summary>
        public string LastResult { get; private set; } = "";
        public bool HasTouchpad => pad != null;

        private enum Mode { None, Desktops, ShowDesktop, Restore, TaskView }
        private enum Phase { Tracking, Settling, Waiting, Fading }

        private readonly SwipeRecognizer rec = new SwipeRecognizer();
        private Touchpad pad;
        private readonly List<StageWindow> stages = new List<StageWindow>();
        private List<MonitorInfo> monitors = new List<MonitorInfo>();
        private double lastTick = MotionClock.Now, lastForeground;

        private Mode mode = Mode.None;
        private Phase phase;
        private bool owned, committed, hasLeft, hasRight, animations = true;
        private float raw, shown, goal, fade = 1;
        private Spring spring;
        private double releasedAt, commitAt, doneAt, lastCheck;
        private List<Guid> order = new List<Guid>();
        private Guid leftId, rightId, targetDesktop;
        private List<SceneWindow> scene = new List<SceneWindow>();
        private List<SceneWindow> putAway;
        private bool desktopShownByUs;
        private int logged;

        public int Fingers
        {
            get => rec.Fingers;
            set => rec.Fingers = Math.Clamp(value, 3, 4);
        }

        /// <summary>Whether gestures act right now (on, and Windows' own swipes for this many fingers are off).</summary>
        public bool Active => Enabled && (ForceActive || !NativeGestures.On(Fingers));

        public SmoothGestures()
        {
            rec.Began += OnBegan;
            rec.Moved += OnMoved;
            rec.Ended += OnEnded;
        }

        public void Start()
        {
            if (!Touchpad.Exists()) return;
            pad = new Touchpad();
            pad.Frame += Feed;
            if (!pad.Start())
            {
                pad.Dispose();
                pad = null;
            }
        }

        /// <summary>One touchpad frame (also used by the tests with made-up fingers).</summary>
        public void Feed(List<Contact> fingers, double t)
        {
            if (pad != null)
            {
                PadWidthMm = pad.WidthMm;
                PadHeightMm = pad.HeightMm;
            }
            if (!Enabled) return;
            rec.Feed(fingers, t);
        }

        private float TravelX => MathF.Max(30, PadWidthMm * 0.5f);
        private float TravelY => MathF.Max(20, PadHeightMm * 0.55f);

        // ---- Fingers ----

        private void OnBegan(SwipeAxis axis, float dx, float dy)
        {
            owned = false;
            if (!Active || mode != Mode.None) return;
            if (Native.SHQueryUserNotificationState(out int quns) == 0 && quns == 3) return;   // a full-screen game
            try
            {
                animations = NativeGestures.Animations();
                if (axis == SwipeAxis.Horizontal) BeginDesktops();
                else if (dy > 0)
                {
                    if (!(desktopShownByUs && Scene.DesktopInFront())) BeginShowDesktop();
                }
                else if (desktopShownByUs && Scene.DesktopInFront()) BeginRestore();
                else mode = Mode.TaskView;
                if (mode == Mode.None) return;
                owned = true;
                phase = Phase.Tracking;
                fade = 1;
                committed = false;
                OnMoved(dx, dy);
                if (mode != Mode.TaskView)
                {
                    Apply(Shown());
                    foreach (var s in stages) if (s.Thumbs.Count > 0) s.Show();
                }
            }
            catch (Exception e)
            {
                Log.Write("Gesture start failed: " + e);
                Finish("error");
            }
        }

        private void PrepareStages()
        {
            var now = MonitorInfo.All();
            bool same = now.Count == monitors.Count && stages.TrueForAll(s => s.Opaque == StageWindow.OpaqueBase);
            for (int i = 0; same && i < now.Count; i++)
            {
                var a = now[i].Bounds;
                var b = monitors[i].Bounds;
                same = a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
            }
            if (!same)
            {
                foreach (var s in stages) s.Dispose();
                stages.Clear();
                monitors = now;
                foreach (var m in monitors) stages.Add(new StageWindow(m));
            }
            foreach (var s in stages) s.Hide();
        }

        private void BeginDesktops()
        {
            order = VirtualDesktops.Order();
            Guid current = VirtualDesktops.Current(order);
            int i = order.IndexOf(current);
            hasLeft = i > 0;
            hasRight = i >= 0 && i < order.Count - 1;
            leftId = hasLeft ? order[i - 1] : Guid.Empty;
            rightId = hasRight ? order[i + 1] : Guid.Empty;
            var here = Scene.Here();
            var left = hasLeft ? Scene.OnDesktop(leftId) : new List<SceneWindow>();
            var right = hasRight ? Scene.OnDesktop(rightId) : new List<SceneWindow>();
            var bg = Scene.Background();
            PrepareStages();
            foreach (var s in stages)
            {
                s.Add(bg, 0, true);
                s.Add(left, 2);
                s.Add(right, 3);
                s.Add(here, 1);
            }
            mode = Mode.Desktops;
            if (++logged <= 3) Log.Write($"Gesture: desktop {i + 1} of {order.Count}; windows here {here.Count}, left {left.Count}, right {right.Count}, desktop layers {bg.Count}");
        }

        private void BeginShowDesktop()
        {
            scene = Scene.Here();
            if (scene.Count == 0) return;
            var bg = Scene.Background();
            PrepareStages();
            foreach (var s in stages)
            {
                s.Add(bg, 0, true);
                s.Add(scene, 1);
            }
            mode = Mode.ShowDesktop;
        }

        private void BeginRestore()
        {
            scene = putAway?.FindAll(w => Native.IsWindow(w.Handle) && Native.IsIconic(w.Handle)) ?? new List<SceneWindow>();
            if (scene.Count == 0)
            {
                desktopShownByUs = false;
                mode = Mode.TaskView;
                return;
            }
            var bg = Scene.Background();
            PrepareStages();
            foreach (var s in stages)
            {
                s.Add(bg, 0, true);
                s.Add(scene, 1);
            }
            mode = Mode.Restore;
        }

        private void OnMoved(float dx, float dy)
        {
            if (!owned) return;
            raw = mode switch
            {
                Mode.Desktops => dx / TravelX,
                Mode.ShowDesktop => dy / TravelY,
                Mode.Restore or Mode.TaskView => -dy / TravelY,
                _ => 0,
            };
        }

        private void OnEnded(float vx, float vy)
        {
            if (!owned) return;
            owned = false;
            releasedAt = MotionClock.Now;
            switch (mode)
            {
                case Mode.Desktops:
                {
                    float v = vx / TravelX;
                    int t = GestureMath.SwitchTarget(raw, v, hasLeft, hasRight);
                    Settle(GestureMath.DesktopShown(raw, hasLeft, hasRight), v, t);
                    if (t != 0)
                    {
                        targetDesktop = t < 0 ? rightId : leftId;
                        Keys.Chord(Keys.Ctrl, Keys.Win, t < 0 ? Keys.Right : Keys.Left);
                        Committed();
                    }
                    break;
                }
                case Mode.ShowDesktop:
                {
                    float v = vy / TravelY;
                    bool go = GestureMath.Commit(raw, v);
                    Settle(GestureMath.OneWayShown(raw), v, go ? 1 : 0);
                    if (go)
                    {
                        putAway = scene;
                        desktopShownByUs = true;
                        Keys.Chord(Keys.Win, Keys.D);
                        Committed();
                    }
                    break;
                }
                case Mode.Restore:
                {
                    float v = -vy / TravelY;
                    bool go = GestureMath.Commit(raw, v);
                    Settle(GestureMath.OneWayShown(raw), v, go ? 1 : 0);
                    if (go)
                    {
                        desktopShownByUs = false;
                        Keys.Chord(Keys.Win, Keys.D);
                        Committed();
                    }
                    break;
                }
                case Mode.TaskView:
                {
                    bool go = GestureMath.Commit(raw, -vy / TravelY, 0.3f);
                    if (go) Keys.Chord(Keys.Win, Keys.Tab);
                    Finish(go ? "task view" : "nothing");
                    break;
                }
            }
        }

        private void Settle(float from, float velocity, float to)
        {
            spring.X = from;
            spring.V = Math.Clamp(velocity, -8, 8);
            goal = to;
            phase = Phase.Settling;
        }

        private void Committed()
        {
            committed = true;
            commitAt = MotionClock.Now;
            doneAt = 0;
        }

        // ---- Every frame ----

        public void Tick()
        {
            double now = MotionClock.Now;
            float dt = (float)Math.Clamp(now - lastTick, 0, 0.05);
            lastTick = now;
            rec.Tick(now);

            bool flying = mode == Mode.Desktops || mode == Mode.ShowDesktop || mode == Mode.Restore;
            SnowWindowAlpha += ((flying ? 0 : 1) - SnowWindowAlpha) * MathF.Min(1, dt * 14);
            if (SnowWindowAlpha > 0.995f) SnowWindowAlpha = 1;

            // Our "show desktop" is over once the person works in some window again.
            if (desktopShownByUs && mode == Mode.None && now - lastForeground > 0.3)
            {
                lastForeground = now;
                if (!Scene.DesktopInFront()) desktopShownByUs = false;
            }
            if (!flying) return;
            try
            {
                Step(now, dt);
            }
            catch (Exception e)
            {
                Log.Write("Gesture failed: " + e);
                Finish("error");
            }
            if (mode != Mode.None && phase != Phase.Tracking && now - releasedAt > 3.5) Finish("safety timeout");
        }

        private float Shown() => mode == Mode.Desktops ? GestureMath.DesktopShown(raw, hasLeft, hasRight) : GestureMath.OneWayShown(raw);

        private void Step(double now, float dt)
        {
            switch (phase)
            {
                case Phase.Tracking:
                    shown = Shown();
                    Apply(shown);
                    break;

                case Phase.Settling:
                    spring.Step(goal, dt);
                    shown = spring.X;
                    if (spring.Settled(goal))
                    {
                        shown = goal;
                        phase = committed ? Phase.Waiting : Phase.Fading;
                        if (!committed) { Finish("back"); return; }
                    }
                    Apply(shown);
                    break;

                case Phase.Waiting:
                    if (Done(now))
                    {
                        phase = Phase.Fading;
                    }
                    else if (now - commitAt > 1.6)
                    {
                        if (mode == Mode.Desktops)
                        {
                            // Windows did not switch (an admin window in front blocks shortcuts): slide back.
                            committed = false;
                            Settle(shown, 0, 0);
                            Log.Write("Gesture: the desktop did not switch");
                        }
                        else phase = Phase.Fading;
                    }
                    break;

                case Phase.Fading:
                    fade -= dt / 0.16f;
                    if (fade <= 0)
                    {
                        Finish(mode == Mode.Desktops ? "switched" : mode == Mode.ShowDesktop ? "desktop shown" : "windows back");
                        return;
                    }
                    Apply(shown);
                    foreach (var s in stages) s.SetAlpha(fade);
                    break;
            }
        }

        /// <summary>Whether Windows has caught up with what the animation already shows.</summary>
        private bool Done(double now)
        {
            if (now - commitAt < (animations ? 0.3 : 0.06)) return false;
            if (doneAt == 0 && now - lastCheck > 0.05)
            {
                lastCheck = now;
                bool ok = mode switch
                {
                    Mode.Desktops => VirtualDesktops.Current(order) == targetDesktop,
                    Mode.ShowDesktop => scene.TrueForAll(w => !Native.IsWindow(w.Handle) || Native.IsIconic(w.Handle) || !Native.IsWindowVisible(w.Handle)),
                    Mode.Restore => scene.TrueForAll(w => !Native.IsWindow(w.Handle) || !Native.IsIconic(w.Handle)),
                    _ => true,
                };
                if (ok) doneAt = now;
            }
            // Give Windows' own animation, hidden under ours, time to finish.
            return doneAt > 0 && now - doneAt >= (animations ? 0.22 : 0.02);
        }

        private void Apply(float s)
        {
            foreach (var st in stages)
            {
                float w = st.Mon.Bounds.Width;
                foreach (var t in st.Thumbs)
                {
                    var r = t.Win.Rect;
                    if (t.Layer == 0)
                    {
                        st.Place(t, r.Left, r.Top, r.Right, r.Bottom, fade);
                    }
                    else if (mode == Mode.Desktops)
                    {
                        float o = (t.Layer == 1 ? s : t.Layer == 2 ? s - 1 : s + 1) * w;
                        st.Place(t, r.Left + o, r.Top, r.Right + o, r.Bottom, fade);
                    }
                    else
                    {
                        // Show desktop: 0 in place … 1 tucked into the taskbar. Bringing windows back runs it backwards.
                        float e = mode == Mode.ShowDesktop ? s : 1 - s;
                        e = Math.Clamp(e, -0.05f, 1);
                        var k = Sink(r);
                        float x0 = r.Left + (k.Left - r.Left) * e, x1 = r.Right + (k.Right - r.Right) * e;
                        float y0 = r.Top + (k.Top - r.Top) * e, y1 = r.Bottom + (k.Bottom - r.Bottom) * e;
                        st.Place(t, x0, y0, x1, y1, (1 - GestureMath.SmoothStep(0.55f, 1, e)) * fade);
                    }
                }
            }
        }

        /// <summary>Where a window goes when it is put away: a small spot on the taskbar below it.</summary>
        private Native.RECT Sink(Native.RECT r)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            MonitorInfo mon = monitors.Count > 0 ? monitors[0] : null;
            foreach (var m in monitors) if (m.Bounds.Contains(cx, cy)) { mon = m; break; }
            if (mon == null) return new Native.RECT(cx, cy, cx, cy);
            var work = mon.Work;
            int bottom = work.Bottom < mon.Bounds.Bottom ? work.Bottom : mon.Bounds.Bottom;
            int x = Math.Clamp(cx, work.Left + 40, Math.Max(work.Left + 40, work.Right - 40));
            int sw = Math.Max(18, (int)(r.Width * 0.06f)), sh = Math.Max(12, (int)(r.Height * 0.06f));
            return new Native.RECT(x - sw / 2, bottom - sh, x + sw / 2, bottom);
        }

        private void Finish(string result)
        {
            foreach (var s in stages) s.Hide();
            if (mode != Mode.None) LastResult = result;
            mode = Mode.None;
            phase = Phase.Tracking;
            owned = false;
            committed = false;
            fade = 1;
        }

        public void Dispose()
        {
            Finish("closed");
            foreach (var s in stages) s.Dispose();
            stages.Clear();
            pad?.Dispose();
            pad = null;
        }
    }

    internal static class MotionNative
    {
        public const uint GA_ROOTOWNER = 3;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public Native.RECT rcDestination;
            public Native.RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32")] public static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
        [DllImport("user32")] public static extern uint GetSysColor(int index);
        [DllImport("gdi32")] public static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("dwmapi")] public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
        [DllImport("dwmapi")] public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
        [DllImport("dwmapi")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES props);
    }
}
