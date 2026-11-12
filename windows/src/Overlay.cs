using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Snowdrift
{
    /// <summary>Snow for one monitor: a transparent, click-through, always-on-top window drawn with
    /// Direct3D through DirectComposition, plus the simulation that lives in it.</summary>
    internal sealed class Overlay : IDisposable
    {
        public readonly MonitorInfo Mon;
        public readonly Sim Sim;
        /// <summary>0 snow stays over full-screen apps, 1 hides, 2 also hides over maximized windows.</summary>
        public int FullScreenMode = 1;
        public bool Ok { get; private set; }

        private readonly Renderer renderer;
        private readonly Batch batch;
        private readonly IntPtr hwnd;
        private readonly int pxW, pxH;
        private readonly float scale;
        private IDXGISwapChain1 swap;
        private ID3D11RenderTargetView rtv;
        private IDCompositionDevice dcomp;
        private IDCompositionTarget target;
        private IDCompositionVisual visual;

        private bool shown;
        private float visibility;
        private bool covered;
        private bool capturing;
        private bool shownEmpty;
        private double lastApply;
        private int lastVersion = -1;
        private double lastCoverCheck = -10;

        // For the debug snapshots.
        public List<WinInfo> LastWindows = new List<WinInfo>();
        public FRect? LastTaskbar;
        public bool Covered => covered;
        public float Visibility => visibility;
        public int Frames;

        private static readonly Native.WndProc Proc = WndProcImpl;
        private static bool classRegistered;
        private static readonly Dictionary<IntPtr, Overlay> ByHandle = new Dictionary<IntPtr, Overlay>();
        private const string ClassName = "SnowdriftOverlay";

        public Overlay(MonitorInfo mon, Renderer renderer)
        {
            Mon = mon;
            this.renderer = renderer;
            batch = renderer.MakeBatch();
            scale = (float)mon.Scale;
            pxW = Math.Max(1, mon.Bounds.Width);
            pxH = Math.Max(1, mon.Bounds.Height);
            float w = pxW / scale, h = pxH / scale;
            float ground = (mon.Work.Bottom - mon.Bounds.Top) / scale;
            Sim = new Sim(w, h, 0, ground);

            IntPtr instance = Native.GetModuleHandle(null);
            if (!classRegistered)
            {
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                    lpfnWndProc = Proc,
                    hInstance = instance,
                    hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_ARROW)),
                    lpszClassName = ClassName,
                };
                Native.RegisterClassEx(ref wc);
                classRegistered = true;
            }
            int ex = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED |
                     Native.WS_EX_TRANSPARENT | Native.WS_EX_NOREDIRECTIONBITMAP;
            hwnd = Native.CreateWindowEx(ex, ClassName, "Snowdrift", Native.WS_POPUP,
                                         mon.Bounds.Left, mon.Bounds.Top, pxW, pxH, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;
            Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
            ByHandle[hwnd] = this;

            try
            {
                var desc = new SwapChainDescription1
                {
                    Width = (uint)pxW,
                    Height = (uint)pxH,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Premultiplied,
                    Flags = SwapChainFlags.None,
                };
                swap = renderer.Factory.CreateSwapChainForComposition(renderer.Device, desc, null);
                using (var back = swap.GetBuffer<ID3D11Texture2D>(0))
                    rtv = renderer.Device.CreateRenderTargetView(back);

                dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(renderer.DxgiDevice);
                dcomp.CreateTargetForHwnd(hwnd, true, out target).CheckError();
                dcomp.CreateVisual(out visual).CheckError();
                visual.SetContent(swap).CheckError();
                target.SetRoot(visual).CheckError();
                dcomp.Commit().CheckError();
                Ok = true;
            }
            catch (Exception e)
            {
                Log.Write("Overlay setup failed on monitor " + mon.Bounds + ": " + e);
            }
        }

        public void Show(bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            if (on)
            {
                if (!shown)
                {
                    visibility = 0;
                    shownEmpty = false;
                }
                shown = true;
                Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, Mon.Bounds.Left, Mon.Bounds.Top, pxW, pxH,
                                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            else
            {
                shown = false;
                Native.ShowWindow(hwnd, Native.SW_HIDE);
            }
        }

        /// <summary>Stay above other always-on-top windows such as the taskbar.</summary>
        public void Reassert()
        {
            if (shown && hwnd != IntPtr.Zero)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private FRect Local(Native.RECT r)
        {
            return new FRect((r.Left - Mon.Bounds.Left) / scale, (r.Top - Mon.Bounds.Top) / scale,
                             (r.Right - Mon.Bounds.Left) / scale, (r.Bottom - Mon.Bounds.Top) / scale);
        }

        private bool CheckCovered(ForegroundInfo fg)
        {
            if (FullScreenMode == 0 || fg == null) return false;
            if (fg.FullScreenOn(Mon)) return true;
            if (FullScreenMode == 2 && fg.Zoomed && fg.Monitor == Mon.Handle && !fg.IsShell && !fg.IsOwn) return true;
            return false;
        }

        public void Frame(double now, float dt, WindowSnapshot snap, ForegroundInfo fg, bool hiddenForApp, Native.POINT cursor)
        {
            if (!Ok || !shown) return;
            Frames++;

            if (snap.Version != lastVersion || Sim.NeedsWindowRefresh)
            {
                lastVersion = snap.Version;
                var wins = new List<WinInfo>(snap.Windows.Count);
                foreach (var w in snap.Windows)
                {
                    if (!w.Rect.Intersects(Mon.Bounds)) continue;
                    wins.Add(new WinInfo(w.Id, Local(w.Rect)));
                }
                FRect? bar = null;
                foreach (var t in snap.Taskbars)
                {
                    if (t.Intersects(Mon.Bounds)) { bar = Local(t); break; }
                }
                LastWindows = wins;
                LastTaskbar = bar;
                // While the snow is hidden, windows that move don't shake their drifts off.
                float elapsed = visibility < 0.5f ? 10 : (float)(now - lastApply);
                lastApply = now;
                Sim.ApplyWindows(wins, bar, elapsed, WindowTracker.Existing);
                lastCoverCheck = -10;
            }
            if (now - lastCoverCheck > 0.1)
            {
                lastCoverCheck = now;
                covered = CheckCovered(fg);
            }

            // Quick fade out over full-screen apps and hidden apps; the snow keeps falling meanwhile.
            float targetVis = covered || hiddenForApp ? 0 : 1;
            visibility += (targetVis - visibility) * MathF.Min(1, dt * 16);
            if (MathF.Abs(targetVis - visibility) < 0.01f) visibility = targetVis;
            bool idle = visibility == 0;

            bool inside = Mon.Bounds.Contains(cursor.X, cursor.Y);
            float cx = (cursor.X - Mon.Bounds.Left) / scale, cy = (cursor.Y - Mon.Bounds.Top) / scale;
            Sim.Step(dt, cx, cy, inside && visibility > 0.5f);
            batch.Clear();
            if (!idle) Sim.Build(batch);

            // Take clicks only while the cursor is on a snowball, so it can be picked up.
            bool want = visibility > 0.9f && (Sim.Hover != null || Sim.GrabbedBall != null);
            if (want != capturing)
            {
                capturing = want;
                if (want) Native.SetExStyle(hwnd, 0, Native.WS_EX_TRANSPARENT);
                else Native.SetExStyle(hwnd, Native.WS_EX_TRANSPARENT, 0);
            }

            // While hidden, one empty frame is enough.
            if (idle && shownEmpty) return;
            shownEmpty = idle;
            float v = visibility;
            renderer.Draw(batch, rtv, pxW, pxH, Sim.W, Sim.H, v * v * (3 - 2 * v));
            swap.Present(0, PresentFlags.None);
        }

        /// <summary>The current picture, rendered off screen (debug snapshots).</summary>
        public byte[] Capture(out int w, out int h)
        {
            w = (int)Sim.W;
            h = (int)Sim.H;
            var b = renderer.MakeBatch();
            Sim.Build(b);
            return renderer.RenderToPixels(b, w, h, Sim.W, Sim.H);
        }

        private static IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (ByHandle.TryGetValue(h, out var o))
            {
                switch (msg)
                {
                    case Native.WM_MOUSEACTIVATE:
                        return new IntPtr(Native.MA_NOACTIVATE);
                    case Native.WM_LBUTTONDOWN:
                        o.Sim.GrabHover();
                        Native.SetCapture(h);
                        return IntPtr.Zero;
                    case Native.WM_LBUTTONUP:
                        o.Sim.ReleaseGrab();
                        Native.ReleaseCapture();
                        return IntPtr.Zero;
                    case Native.WM_SETCURSOR:
                        if (o.capturing)
                        {
                            Native.SetCursor(Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_HAND)));
                            return new IntPtr(1);
                        }
                        break;
                }
            }
            return Native.DefWindowProc(h, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (hwnd != IntPtr.Zero)
            {
                ByHandle.Remove(hwnd);
                Native.DestroyWindow(hwnd);
            }
            visual?.Dispose();
            target?.Dispose();
            dcomp?.Dispose();
            rtv?.Dispose();
            swap?.Dispose();
        }
    }
}
