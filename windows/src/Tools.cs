using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using W = System.Windows;

namespace Snowdrift
{
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Folder);
                    File.AppendAllText(Path.Combine(Settings.Folder, "snowdrift.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
                    string dir = LiveDebug.Dir;
                    if (dir != null) File.AppendAllText(Path.Combine(dir, "log.txt"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }

    internal static class Png
    {
        /// <summary>BGRA premultiplied pixels to a GDI+ bitmap.</summary>
        public static Bitmap FromPixels(byte[] px, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        /// <summary>Puts premultiplied snow pixels over an opaque picture, the way the screen does:
        /// out = snow + picture * (1 - snow alpha). Glows (colour brighter than alpha) add light.</summary>
        public static void Over(Bitmap dst, byte[] src, int w, int h)
        {
            var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    IntPtr line = data.Scan0 + y * data.Stride;
                    Marshal.Copy(line, row, 0, w * 4);
                    int o = y * w * 4;
                    for (int x = 0; x < w * 4; x += 4)
                    {
                        int a = src[o + x + 3];
                        int k = 255 - a;
                        for (int c = 0; c < 3; c++)
                            row[x + c] = (byte)Math.Min(255, src[o + x + c] + row[x + c] * k / 255);
                        row[x + 3] = (byte)Math.Min(255, a + row[x + 3] * k / 255);
                    }
                    Marshal.Copy(row, 0, line, w * 4);
                }
            }
            finally { dst.UnlockBits(data); }
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>SNOWDRIFT_DEBUG=folder: every 10 seconds, log what each screen sees and save what it draws.</summary>
    internal static class LiveDebug
    {
        public static readonly string Dir = Environment.GetEnvironmentVariable("SNOWDRIFT_DEBUG");
        private static double next = 8;
        private static int shot;

        public static void Tick(double now, List<Overlay> overlays, ForegroundInfo fg, bool hiddenForApp)
        {
            if (Dir == null || now < next) return;
            next = now + 10;
            shot++;
            try
            {
                Directory.CreateDirectory(Dir);
                for (int i = 0; i < overlays.Count; i++)
                {
                    var o = overlays[i];
                    var st = o.Sim.DebugStats;
                    Log.Write($"screen {i}: frames {o.Frames} (~{o.Frames / Math.Max(1, now):0} fps), ok {o.Ok}, windows {o.LastWindows.Count}, " +
                              $"taskbar {(o.LastTaskbar.HasValue ? "yes" : "no")}, flakes {st.flakes}, debris {st.debris}, surfaces {st.surfaces}, " +
                              $"snow {st.snow:0}, maxH {o.Sim.DebugMaxHeight():0.0}, covered {o.Covered}, visibility {o.Visibility:0.00}, " +
                              $"hiddenForApp {hiddenForApp}, fg class {(fg.Hwnd != IntPtr.Zero ? Native.ClassName(fg.Hwnd) : "-")}");
                    byte[] px = o.Capture(out int w, out int h);
                    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(255, 30, 36, 76));
                        using var pen = new Pen(Color.FromArgb(230, 255, 150, 50), 1.5f);
                        foreach (var win in o.LastWindows)
                            g.DrawRectangle(pen, win.Rect.X0, win.Rect.Y0, win.Rect.X1 - win.Rect.X0, win.Rect.Y1 - win.Rect.Y0);
                        if (o.LastTaskbar is FRect t)
                        {
                            using var tp = new Pen(Color.FromArgb(230, 80, 255, 120), 1.5f);
                            g.DrawRectangle(tp, t.X0, t.Y0, t.X1 - t.X0, t.Y1 - t.Y0);
                        }
                    }
                    Png.Over(bmp, px, w, h);
                    bmp.Save(Path.Combine(Dir, $"screen{i}-{shot}.png"), ImageFormat.Png);
                }
            }
            catch (Exception e)
            {
                Log.Write("debug capture failed: " + e.Message);
            }
        }
    }

    /// <summary>--selftest out.txt: checks the cases that matter, without touching the real screen.</summary>
    internal static class SelfTest
    {
        public static bool Run(string outPath)
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(bool cond, string what)
            {
                sb.AppendLine((cond ? "PASS " : "FAIL ") + what);
                if (!cond) ok = false;
            }

            try
            {
                var sim = new Sim(1512, 982, 0, 934);
                var cfg = SimConfig.Default;
                cfg.Intensity = 2; cfg.Wind = 2; cfg.Drifts = 3; cfg.Garland = 0; cfg.Tree = false; cfg.Sparkle = false;
                sim.Config = cfg;
                var dock = new FRect(0, 934, 1512, 982);
                var win = new WinInfo(7, new FRect(200, 300, 800, 700));
                void RunFor(float seconds)
                {
                    for (float t = 0; t < seconds; t += 1f / 60) sim.Step(1f / 60, -500, -500, false);
                }
                HashSet<long> All(HashSet<long> ids) => ids;

                sim.ApplyWindows(new List<WinInfo> { win }, dock, 1, All);
                RunFor(40);
                var before = sim.DebugDock;
                Check(before.active && before.snow > 500, $"snow settles on a visible taskbar ({before.snow:0})");
                var st = sim.DebugStats;
                Check(st.snow > 2000, $"snow settles on the window ({st.snow:0})");

                sim.ApplyWindows(new List<WinInfo> { win }, null, 1, All);
                RunFor(40);
                var hidden = sim.DebugDock;
                Check(!hidden.active, "hidden taskbar is not a surface");
                Check(Math.Abs(hidden.snow - before.snow) < 1, "hidden taskbar keeps its snow and gets no more");

                sim.ApplyWindows(new List<WinInfo> { win }, dock, 1, All);
                var back = sim.DebugDock;
                Check(back.active && back.snow >= before.snow - 1, "taskbar drift comes back with the taskbar");

                int debrisBefore = sim.DebugDebris;
                var moved = new WinInfo(7, new FRect(700, 200, 1300, 600));
                sim.ApplyWindows(new List<WinInfo> { moved }, dock, 10, All);
                Check(sim.DebugDebris - debrisBefore < 5, "no snow shaken off by moves while hidden");

                var yanked = new WinInfo(7, new FRect(200, 260, 800, 660));
                sim.ApplyWindows(new List<WinInfo> { yanked }, dock, 1f / 60, All);
                Check(sim.DebugDebris - debrisBefore > 20, "a fast yank still shakes snow off");

                // Snowman: three balls dropped on each other stack up.
                var s2 = new Sim(1512, 982, 0, 934);
                s2.ApplyWindows(new List<WinInfo>(), dock, 1, All);
                s2.DebugAddBall(300, 860, 30);
                for (float t = 0; t < 3; t += 1f / 60) s2.Step(1f / 60, -500, -500, false);
                s2.DebugAddBall(302, 760, 22);
                for (float t = 0; t < 3; t += 1f / 60) s2.Step(1f / 60, -500, -500, false);
                s2.DebugAddBall(300, 680, 15);
                for (float t = 0; t < 3; t += 1f / 60) s2.Step(1f / 60, -500, -500, false);
                int stacks = 0;
                foreach (var b in s2.Balls) if (b.Below == null) stacks++;
                Check(s2.Balls.Count == 3 && stacks == 1, $"three snowballs make one snowman (balls {s2.Balls.Count}, stacks {stacks})");

                Check(!sim.DebugHasNaN() && !s2.DebugHasNaN(), "no NaN");

                var atlas = new Atlas();
                int lit = 0;
                for (int i = 3; i < atlas.Pixels.Length; i += 4) if (atlas.Pixels[i] > 0) lit++;
                Check(lit > 50000, $"atlas painted ({lit} pixels)");

                using var renderer = Renderer.Create(true);
                var batch = renderer.MakeBatch();
                sim.Build(batch);
                var px = renderer.RenderToPixels(batch, 400, 300, 1512, 982);
                int drawn = 0;
                for (int i = 3; i < px.Length; i += 4) if (px[i] > 0) drawn++;
                Check(drawn > 1000, $"Direct3D draws the snow ({drawn} pixels, {batch.Count} vertices)");
            }
            catch (Exception e)
            {
                sb.AppendLine("FAIL exception: " + e);
                ok = false;
            }
            File.WriteAllText(outPath, sb.ToString());
            return ok;
        }
    }

    /// <summary>--snapshot out.png: a minute of blizzard on a made-up desktop, rendered with Direct3D.</summary>
    internal static class Snapshot
    {
        public static bool Run(string path)
        {
            const float W = 1512, H = 982, ground = 934;
            var sim = new Sim(W, H, 0, ground);
            var cfg = SimConfig.Default;
            cfg.Intensity = 2; cfg.Wind = 1; cfg.Drifts = 3; cfg.Tool = 1; cfg.Garland = 3; cfg.Frost = true;
            sim.Config = cfg;
            var windows = new List<WinInfo>
            {
                new WinInfo(11, new FRect(820, 300, 1380, 680)),
                new WinInfo(10, new FRect(120, 180, 940, 700)),
            };
            var taskbar = new FRect(0, ground, W, H);
            HashSet<long> All(HashSet<long> ids) => ids;
            sim.ApplyWindows(windows, taskbar, 1, All);
            sim.DebugAddBall(300, 860, 30);
            const float dt = 1f / 60, seconds = 75;
            int dropped = 0;
            bool fireworks = false;
            for (float t = 0; t < seconds; t += dt)
            {
                if (dropped == 0 && t > 8) { sim.DebugAddBall(302, 720, 22); dropped = 1; }
                if (dropped == 1 && t > 10) { sim.DebugAddBall(300, 640, 15); dropped = 2; }
                if (dropped == 2 && t > 12) { sim.DebugAddBall(1300, 820, 18); dropped = 3; }
                if (!fireworks && t > seconds - 4.5f) { fireworks = true; sim.Festive.LaunchFireworks(5); }
                sim.Step(dt, -500, -500, false);
            }

            using var renderer = Renderer.Create(true);
            var batch = renderer.MakeBatch();
            sim.Build(batch);
            const int scale = 2;
            int pw = (int)W * scale, ph = (int)H * scale;
            byte[] px = renderer.RenderToPixels(batch, pw, ph, W, H);
            using var bmp = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(scale, scale);
                using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, H), Color.FromArgb(26, 33, 77), Color.FromArgb(84, 77, 140)))
                    g.FillRectangle(bg, 0, 0, W, H);
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    var r = windows[i].Rect;
                    var rect = new RectangleF(r.X0, r.Y0, r.X1 - r.X0, r.Y1 - r.Y0);
                    using var shape = Png.Rounded(rect, 8);
                    using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    {
                        g.TranslateTransform(0, 8);
                        g.FillPath(shadow, shape);
                        g.TranslateTransform(0, -8);
                    }
                    using (var fill = new SolidBrush(Color.FromArgb(243, 243, 243))) g.FillPath(fill, shape);
                    using (var title = new SolidBrush(Color.FromArgb(225, 225, 228)))
                        g.FillRectangle(title, rect.X, rect.Y, rect.Width, 32);
                    using var glyph = new SolidBrush(Color.FromArgb(60, 60, 60));
                    g.FillRectangle(glyph, rect.Right - 30, rect.Y + 15, 10, 1.5f);
                    g.DrawRectangle(new Pen(Color.FromArgb(60, 60, 60), 1.2f), rect.Right - 66, rect.Y + 11, 9, 9);
                }
                using (var bar = new SolidBrush(Color.FromArgb(235, 28, 30, 38))) g.FillRectangle(bar, 0, ground, W, H - ground);
                using (var start = new SolidBrush(Color.FromArgb(70, 140, 255))) g.FillRectangle(start, W / 2 - 120, ground + 14, 20, 20);
            }
            Png.Over(bmp, px, pw, ph);
            bmp.Save(path, ImageFormat.Png);
            var st = sim.DebugStats;
            File.WriteAllText(path + ".txt", $"flakes {st.flakes} debris {st.debris} surfaces {st.surfaces} snow {st.snow:0} maxH {sim.DebugMaxHeight():0.0} balls {sim.Balls.Count} vertices {batch.Count} warp {renderer.IsWarp}");
            return true;
        }
    }

    /// <summary>--island-preview out.png: the music island in its states with a made-up track.</summary>
    internal static class IslandPreview
    {
        public static bool Run(string path)
        {
            // A made-up album cover: dusk sky, moon, hills.
            var dv = new WM.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var sky = new WM.LinearGradientBrush(WM.Color.FromRgb(242, 115, 140), WM.Color.FromRgb(31, 26, 89), 90);
                dc.DrawRectangle(sky, null, new W.Rect(0, 0, 256, 256));
                dc.DrawEllipse(new WM.SolidColorBrush(WM.Color.FromRgb(255, 242, 217)), null, new W.Point(180, 76), 30, 30);
                var hill = new WM.SolidColorBrush(WM.Color.FromRgb(20, 15, 51));
                dc.DrawEllipse(hill, null, new W.Point(70, 266), 130, 110);
                dc.DrawEllipse(hill, null, new W.Point(220, 280), 130, 115);
            }
            var art = new WMI.RenderTargetBitmap(256, 256, 96, 96, WM.PixelFormats.Pbgra32);
            art.Render(dv);
            art.Freeze();

            var playing = new NowPlaying
            {
                Source = "Spotify", TrackId = "x", Title = "Снег идёт всю ночь (Winter Mix)", Artist = "Aurora Lights",
                IsPlaying = true, Duration = 214, BasePosition = 83, BaseTime = DateTime.UtcNow, LastActive = DateTime.UtcNow,
                Artwork = art, Accent = MusicService.AccentOf(art),
            };

            var compact = new IslandView(playing, null);
            compact.ForceState(false);
            var open = new IslandView(playing, null);
            open.ForceState(true);
            var parts = new[] { (compact, 80.0), (open, 270.0) };

            const double pad = 18, w = IslandView.PanelW;
            double total = pad;
            foreach (var p in parts) total += p.Item2 + pad;
            var sheet = new WM.DrawingVisual();
            using (var dc = sheet.RenderOpen())
            {
                dc.DrawRectangle(new WM.SolidColorBrush(WM.Color.FromRgb(26, 26, 26)), null, new W.Rect(0, 0, w + pad * 2, total));
                double y = pad;
                foreach (var (view, height) in parts)
                {
                    view.Measure(new W.Size(IslandView.PanelW, IslandView.PanelH));
                    view.Arrange(new W.Rect(0, 0, IslandView.PanelW, IslandView.PanelH));
                    view.Tick(0.016, new W.Point(-1000, -1000), false);
                    view.ForceState(view == open);
                    view.UpdateLayout();
                    var shot = new WMI.RenderTargetBitmap((int)IslandView.PanelW * 2, (int)IslandView.PanelH * 2, 192, 192, WM.PixelFormats.Pbgra32);
                    shot.Render(view);
                    var frame = new W.Rect(pad, y, w, height);
                    var bg = new WM.LinearGradientBrush(WM.Color.FromRgb(41, 51, 97), WM.Color.FromRgb(97, 82, 140), 90);
                    dc.PushClip(new WM.RectangleGeometry(frame, 12, 12));
                    dc.DrawRectangle(bg, null, frame);
                    dc.DrawImage(shot, new W.Rect(pad, y, IslandView.PanelW, IslandView.PanelH));
                    dc.Pop();
                    y += height + pad;
                }
            }
            var outBmp = new WMI.RenderTargetBitmap((int)((w + pad * 2) * 2), (int)(total * 2), 192, 192, WM.PixelFormats.Pbgra32);
            outBmp.Render(sheet);
            var enc = new WMI.PngBitmapEncoder();
            enc.Frames.Add(WMI.BitmapFrame.Create(outBmp));
            using var fs = File.Create(path);
            enc.Save(fs);
            return true;
        }
    }
}
