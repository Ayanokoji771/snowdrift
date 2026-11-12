using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snowdrift
{
    /// <summary>The music island's window: a transparent strip at the top centre of the main screen.
    /// It takes clicks only while the cursor is over the island.</summary>
    internal sealed class IslandWindow : Window
    {
        public readonly IslandView View;
        private IntPtr hwnd;
        private bool clickThrough = true;

        public IslandWindow(MusicService music)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            Title = "Snowdrift";
            Width = IslandView.PanelW;
            Height = IslandView.PanelH;
            View = new IslandView(music.Now, music);
            Content = View;
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT, Native.WS_EX_APPWINDOW);
            };
            Place();
        }

        public void Place()
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }

        private void SetClickThrough(bool on)
        {
            if (on == clickThrough || hwnd == IntPtr.Zero) return;
            clickThrough = on;
            if (on) Native.SetExStyle(hwnd, Native.WS_EX_TRANSPARENT, 0);
            else Native.SetExStyle(hwnd, 0, Native.WS_EX_TRANSPARENT);
        }

        public void Reassert()
        {
            if (hwnd != IntPtr.Zero && IsVisible)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Tick(double dt, bool fullScreen)
        {
            if (!IsVisible) return;
            Native.GetCursorPos(out var pt);
            Point local;
            try { local = View.PointFromScreen(new Point(pt.X, pt.Y)); }
            catch { local = new Point(-10000, -10000); }
            bool inside = View.Tick(dt, local, fullScreen);
            SetClickThrough(!inside);
        }
    }

    /// <summary>Draws the island and handles its buttons. Everything is drawn by hand each frame,
    /// with springs for the open and close animation.</summary>
    internal sealed class IslandView : FrameworkElement
    {
        public const double PanelW = 640, PanelH = 300;
        private const double BarH = 32, Wing = 46;
        private const double ExpandedW = 440;

        private readonly NowPlaying now;
        private readonly MusicService service;

        private double e, ev, wing, wingV, time;
        private bool expanded;
        private DateTime? hoverSince, leftSince;
        private DateTime lastSync = DateTime.MinValue;
        private Point mouse = new Point(-10000, -10000);
        private string pressed;
        private Rect prevRect, playRect, nextRect, barRect;
        private double bodyW, bodyH, flare, x0;

        private string cachedTitle;
        private double cachedTitleW;

        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily SymbolFont = new FontFamily("Segoe UI Symbol");
        private static readonly Brush BodyBrush = Frozen(new LinearGradientBrush(Colors.Black, Color.FromRgb(5, 10, 23), 90));

        public IslandView(NowPlaying now, MusicService service)
        {
            this.now = now;
            this.service = service;
            Width = PanelW;
            Height = PanelH;
            Focusable = false;
        }

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double SmoothStep(double a, double b, double x)
        {
            double t = Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        private static void Spring(ref double x, ref double v, double target, double dt, double response = 0.42, double damping = 0.78)
        {
            double k = Math.Pow(2 * Math.PI / response, 2), c = 4 * Math.PI * damping / response;
            int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                v += (k * (target - x) - c * v) * h;
                x += v * h;
            }
        }

        public bool Expanded => expanded;

        /// <summary>Bottom edge of the island, in points from the top of the screen.</summary>
        public double Bottom => Lerp(BarH, ExpandedHeight, Clamp01(e));

        private double ExpandedHeight => BarH + 152;

        private bool WingsWanted(bool fullScreen) =>
            now.HasTrack && !fullScreen && (now.IsPlaying || (DateTime.UtcNow - now.LastActive).TotalSeconds < 600);

        private double TitleWidth()
        {
            if (cachedTitle != now.Title)
            {
                cachedTitle = now.Title;
                var ft = Text(now.Title, 12, FontWeights.SemiBold, Brushes.White);
                cachedTitleW = Math.Min(150, ft.WidthIncludingTrailingWhitespace);
            }
            return cachedTitleW;
        }

        private double CompactWidth => Wing * 2 + (now.HasTrack ? TitleWidth() + 8 : 0);

        /// <summary>For previews: jump straight to a state.</summary>
        public void ForceState(bool open)
        {
            expanded = open;
            e = open ? 1 : 0;
            wing = 1;
            Layout();
        }

        private void Layout()
        {
            double we = Clamp01(wing);
            double cw = CompactWidth * we;
            bodyW = Math.Max(0, Lerp(cw, ExpandedW, e));
            bodyH = Lerp(BarH, ExpandedHeight, e);
            flare = Lerp(7, 14, Clamp01(e));
            double total = bodyW + flare * 2;
            x0 = (PanelW - total) / 2;
        }

        /// <summary>One frame: hover, springs, redraw. Returns whether the cursor is over the island.</summary>
        public bool Tick(double dt, Point m, bool fullScreen)
        {
            time += dt;
            mouse = m;
            bool wingsOn = WingsWanted(fullScreen);
            Spring(ref wing, ref wingV, wingsOn ? 1 : 0, dt);
            Layout();

            var body = new Rect(x0 + flare, 0, bodyW, bodyH);
            Rect hit = expanded
                ? new Rect(body.X - 12, 0, body.Width + 24, body.Height + 14)
                : new Rect(body.X - 4, 0, body.Width + 8, body.Height + 2);
            bool active = wingsOn || expanded;
            bool inside = active && bodyW > 1 && hit.Contains(m);
            var t = DateTime.UtcNow;
            bool buttonDown = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
            if (inside)
            {
                leftSince = null;
                hoverSince ??= t;
                if (!expanded && (t - hoverSince.Value).TotalSeconds > 0.12 && !buttonDown) expanded = true;
            }
            else
            {
                hoverSince = null;
                if (expanded)
                {
                    leftSince ??= t;
                    if ((t - leftSince.Value).TotalSeconds > 0.3) expanded = false;
                }
            }
            if (!now.HasTrack) expanded = false;
            Spring(ref e, ref ev, expanded ? 1 : 0, dt);
            Layout();

            if (expanded && (t - lastSync).TotalSeconds > 2)
            {
                lastSync = t;
                service?.SyncTimeline();
            }
            InvalidateVisual();
            return inside;
        }

        // ---- Drawing ----

        private FormattedText Text(string s, double size, FontWeight weight, Brush brush, FontFamily family = null)
        {
            double ppd = 1;
            try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            return new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     new Typeface(family ?? UiFont, FontStyles.Normal, weight, FontStretches.Normal), size, brush, ppd);
        }

        private static SolidColorBrush B(Color c, double a)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

        private Geometry Shape(double left, double width, double height, double bottomR, double f)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double r = Math.Max(0, Math.Min(bottomR, Math.Min((width - 2 * f) / 2, height - f)));
                double l = left, rt = left + width;
                c.BeginFigure(new Point(l, 0), true, true);
                c.QuadraticBezierTo(new Point(l + f, 0), new Point(l + f, f), true, false);
                c.LineTo(new Point(l + f, height - r), true, false);
                c.QuadraticBezierTo(new Point(l + f, height), new Point(l + f + r, height), true, false);
                c.LineTo(new Point(rt - f - r, height), true, false);
                c.QuadraticBezierTo(new Point(rt - f, height), new Point(rt - f, height - r), true, false);
                c.LineTo(new Point(rt - f, f), true, false);
                c.QuadraticBezierTo(new Point(rt - f, 0), new Point(rt, 0), true, false);
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (bodyW < 1.5) return;
            double ec = Clamp01(e), wc = Clamp01(wing);
            double total = bodyW + flare * 2;
            double bottomR = Lerp(11, 30, ec);
            var accent = now.Accent;
            var geo = Shape(x0, total, bodyH, bottomR, flare);
            var body = new Rect(x0 + flare, 0, bodyW, bodyH);

            // Glow and soft shadow under the open island.
            double glow = ec * (now.IsPlaying ? 0.42 : 0);
            if (glow > 0.01)
            {
                var gb = new RadialGradientBrush(Color.FromArgb((byte)(glow * 255), accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
                gb.Freeze();
                dc.DrawEllipse(gb, null, new Point(body.X + body.Width / 2, bodyH * 0.6), total * 0.62, bodyH * 0.85);
            }
            if (ec > 0.01)
            {
                var sb = new RadialGradientBrush(Color.FromArgb((byte)(ec * 70), 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
                sb.Freeze();
                dc.DrawEllipse(sb, null, new Point(body.X + body.Width / 2, bodyH * 0.75), total * 0.56, bodyH * 0.6);
            }

            dc.DrawGeometry(BodyBrush, null, geo);
            dc.PushClip(geo);
            if (ec > 0.02)
            {
                dc.PushOpacity(ec);
                InnerSnow(dc, body);
                dc.Pop();
            }
            double compactA = (1 - SmoothStep(0, 0.35, e)) * wc;
            if (compactA > 0.01 && now.HasTrack)
            {
                dc.PushOpacity(compactA);
                DrawCompact(dc, body, accent);
                dc.Pop();
            }
            double expandedA = SmoothStep(0.55, 0.95, e);
            if (expandedA > 0.01)
            {
                dc.PushOpacity(expandedA);
                DrawExpanded(dc, body, accent);
                dc.Pop();
            }
            dc.Pop();

            // Frosty rim, brighter towards the bottom.
            var rim = new LinearGradientBrush(Color.FromArgb(0, 255, 255, 255), Color.FromArgb((byte)(255 * Lerp(0.1, 0.22, ec)), 255, 255, 255), 90);
            rim.Freeze();
            dc.DrawGeometry(null, new Pen(rim, 1), geo);

            double iceA = Math.Max(ec, wc);
            if (iceA > 0.01)
            {
                double iw = Math.Max(10, bodyW - Lerp(26, 64, ec));
                var area = new Rect(body.X + (bodyW - iw) / 2, bodyH, iw, Lerp(22, 60, ec));
                dc.PushOpacity(iceA);
                Icicles(dc, area, (int)Math.Round(Lerp(7, 16, ec)), Lerp(7, 17, ec), ec > 0.5 && now.IsPlaying);
                dc.Pop();
            }
        }

        private void DrawCompact(DrawingContext dc, Rect body, Color accent)
        {
            double left = body.X;
            var art = new Rect(left + (Wing - 22) / 2, (BarH - 22) / 2, 22, 22);
            DrawArtwork(dc, art, 6, accent, false);
            double tw = TitleWidth();
            var ft = Text(now.Title, 12, FontWeights.SemiBold, B(Colors.White, 0.92));
            ft.MaxTextWidth = Math.Max(1, tw);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(left + Wing, (BarH - ft.Height) / 2));
            Equalizer(dc, new Point(body.Right - Wing / 2, BarH / 2), 4, 14, accent);
        }

        private void DrawExpanded(DrawingContext dc, Rect body, Color accent)
        {
            double L = body.Left, R = body.Right;
            // Top row.
            var flake = Text("❄", 10, FontWeights.Bold, B(accent, 1), SymbolFont);
            dc.DrawText(flake, new Point(L + 20, (BarH - flake.Height) / 2));
            var brand = Text("Snowdrift", 11, FontWeights.SemiBold, B(Colors.White, 0.55));
            dc.DrawText(brand, new Point(L + 20 + flake.Width + 6, (BarH - brand.Height) / 2));
            if (now.Source.Length > 0)
            {
                var src = Text(now.Source, 11, FontWeights.Medium, B(Colors.White, 0.55));
                dc.DrawText(src, new Point(R - 20 - src.Width, (BarH - src.Height) / 2));
            }

            var art = new Rect(L + 22, BarH + 10, 64, 64);
            DrawArtwork(dc, art, 14, accent, true);

            const double eqW = 30;
            double textX = art.Right + 14;
            double textW = Math.Max(20, R - 22 - eqW - 6 - textX);
            Marquee(dc, now.Title, new Rect(textX, art.Top + 11, textW, 22), 15, FontWeights.SemiBold, B(Colors.White, 1));
            var artist = Text(now.Artist, 13, FontWeights.Normal, B(Colors.White, 0.6));
            artist.MaxTextWidth = textW;
            artist.MaxLineCount = 1;
            artist.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(artist, new Point(textX, art.Top + 35));
            Equalizer(dc, new Point(R - 22 - eqW / 2, art.Top + art.Height / 2), 5, 24, accent);

            // Progress.
            double py = art.Bottom + 17;
            double pos = now.Position(DateTime.UtcNow), dur = now.Duration;
            double f = dur > 0 ? pos / dur : 0;
            var elapsed = Text(Format(pos), 10.5, FontWeights.Medium, B(Colors.White, 0.55));
            dc.DrawText(elapsed, new Point(L + 22 + 36 - elapsed.Width, py - elapsed.Height / 2));
            var left = Text("-" + Format(Math.Max(0, dur - pos)), 10.5, FontWeights.Medium, B(Colors.White, 0.55));
            dc.DrawText(left, new Point(R - 22 - 40, py - left.Height / 2));
            barRect = new Rect(L + 22 + 36 + 8, py - 7, Math.Max(10, R - 22 - 40 - 8 - (L + 22 + 36 + 8)), 14);
            dc.DrawRoundedRectangle(B(Colors.White, 0.14), null, new Rect(barRect.X, py - 2, barRect.Width, 4), 2, 2);
            double fw = Math.Max(4, barRect.Width * f);
            var fill = new LinearGradientBrush(Color.FromArgb(204, accent.R, accent.G, accent.B), Colors.White, 0);
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(barRect.X, py - 2, fw, 4), 2, 2);
            double sx = barRect.X + barRect.Width * f;
            var halo = new RadialGradientBrush(Color.FromArgb(150, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
            halo.Freeze();
            dc.DrawEllipse(halo, null, new Point(sx, py), 10, 10);
            var star = Text("❄", 12, FontWeights.Bold, Brushes.White, SymbolFont);
            dc.PushTransform(new RotateTransform(f * 720, sx, py));
            dc.DrawText(star, new Point(sx - star.Width / 2, py - star.Height / 2));
            dc.Pop();

            // Buttons.
            double cy = py + 7 + 4 + 18;
            double cx = (L + R) / 2;
            prevRect = new Rect(cx - 82 - 22, cy - 18, 44, 36);
            playRect = new Rect(cx - 22, cy - 18, 44, 36);
            nextRect = new Rect(cx + 82 - 22, cy - 18, 44, 36);
            Button(dc, prevRect, "prev", 17);
            Button(dc, playRect, now.IsPlaying ? "pause" : "play", 26);
            Button(dc, nextRect, "next", 17);
        }

        private static string Format(double s)
        {
            int t = Math.Max(0, (int)Math.Floor(s));
            return $"{t / 60}:{t % 60:00}";
        }

        private void Button(DrawingContext dc, Rect r, string kind, double size)
        {
            string key = kind == "pause" || kind == "play" ? "play" : kind;
            bool hover = r.Contains(mouse);
            bool down = hover && pressed == key;
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            if (hover) dc.DrawEllipse(B(Colors.White, down ? 0.16 : 0.09), null, new Point(cx, cy), 19, 19);
            double s = size * (down ? 0.86 : 1);
            var white = Brushes.White;
            var pen = new Pen(white, 1.6) { LineJoin = PenLineJoin.Round };
            switch (kind)
            {
                case "play":
                    dc.DrawGeometry(white, pen, Poly(new Point(cx - s * 0.32, cy - s * 0.42), new Point(cx - s * 0.32, cy + s * 0.42), new Point(cx + s * 0.45, cy)));
                    break;
                case "pause":
                    dc.DrawRoundedRectangle(white, null, new Rect(cx - s * 0.33, cy - s * 0.42, s * 0.24, s * 0.84), 2, 2);
                    dc.DrawRoundedRectangle(white, null, new Rect(cx + s * 0.09, cy - s * 0.42, s * 0.24, s * 0.84), 2, 2);
                    break;
                case "prev":
                    dc.DrawGeometry(white, pen, Poly(new Point(cx, cy - s * 0.36), new Point(cx, cy + s * 0.36), new Point(cx - s * 0.55, cy)));
                    dc.DrawGeometry(white, pen, Poly(new Point(cx + s * 0.55, cy - s * 0.36), new Point(cx + s * 0.55, cy + s * 0.36), new Point(cx, cy)));
                    break;
                case "next":
                    dc.DrawGeometry(white, pen, Poly(new Point(cx, cy - s * 0.36), new Point(cx, cy + s * 0.36), new Point(cx + s * 0.55, cy)));
                    dc.DrawGeometry(white, pen, Poly(new Point(cx - s * 0.55, cy - s * 0.36), new Point(cx - s * 0.55, cy + s * 0.36), new Point(cx, cy)));
                    break;
            }
        }

        private static Geometry Poly(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], true, true);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
            }
            g.Freeze();
            return g;
        }

        private void DrawArtwork(DrawingContext dc, Rect r, double radius, Color accent, bool big)
        {
            if (big)
            {
                var halo = new RadialGradientBrush(Color.FromArgb(105, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
                halo.Freeze();
                dc.DrawEllipse(halo, null, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width * 0.78, r.Height * 0.78);
            }
            var clip = new RectangleGeometry(r, radius, radius);
            clip.Freeze();
            dc.PushClip(clip);
            if (now.Artwork != null)
            {
                double iw = now.Artwork.PixelWidth, ih = now.Artwork.PixelHeight;
                double k = Math.Max(r.Width / Math.Max(1, iw), r.Height / Math.Max(1, ih));
                double w = iw * k, h = ih * k;
                dc.DrawImage(now.Artwork, new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
            }
            else
            {
                var bg = new LinearGradientBrush(Color.FromArgb(217, accent.R, accent.G, accent.B), Rgb(0.08, 0.12, 0.3), 45);
                bg.Freeze();
                dc.DrawRectangle(bg, null, r);
                var flake = Text("❄", r.Width * 0.46, FontWeights.Bold, B(Colors.White, 0.9), SymbolFont);
                dc.DrawText(flake, new Point(r.X + (r.Width - flake.Width) / 2, r.Y + (r.Height - flake.Height) / 2));
            }
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(B(Colors.White, 0.2), 1), r, radius, radius);
            if (big) SnowCap(dc, new Rect(r.X - 3, r.Y - r.Height * 0.09, r.Width + 6, r.Height * 0.2));
        }

        /// <summary>A soft drift of snow, wider than what it sits on, with a lumpy top.</summary>
        private static void SnowCap(DrawingContext dc, Rect rc)
        {
            double x = rc.X, y = rc.Y, w = rc.Width, h = rc.Height;
            Point P(double px, double py) => new Point(x + w * px, y + h * py);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(0, 0.78), true, true);
                c.BezierTo(P(0.02, 0.25), P(0.14, 0.18), P(0.28, 0.2), true, true);
                c.BezierTo(P(0.4, 0.22), P(0.48, 0), P(0.6, 0.14), true, true);
                c.BezierTo(P(0.78, 0.26), P(0.98, 0.2), P(1, 0.75), true, true);
                c.BezierTo(P(1, 1.05), P(0.82, 1.1), P(0.7, 0.86), true, true);
                c.BezierTo(P(0.6, 0.7), P(0.42, 1.15), P(0.3, 0.9), true, true);
                c.BezierTo(P(0.18, 0.7), P(0.02, 1.05), P(0, 0.78), true, true);
            }
            g.Freeze();
            var shadow = g.Clone();
            shadow.Transform = new TranslateTransform(0, 1);
            dc.DrawGeometry(B(Colors.Black, 0.22), null, shadow);
            var fill = new LinearGradientBrush(Colors.White, Rgb(0.86, 0.92, 1), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, g);
        }

        private void Equalizer(DrawingContext dc, Point center, int bars, double maxH, Color accent)
        {
            const double bw = 3, gap = 2.5;
            double total = bars * bw + (bars - 1) * gap;
            var brush = new LinearGradientBrush(Colors.White, accent, 90);
            brush.Freeze();
            for (int i = 0; i < bars; i++)
            {
                double d = i;
                double v = now.IsPlaying
                    ? 0.22 + 0.78 * Math.Abs(Math.Sin(time * (2.6 + d * 0.9) + d * 1.7)) * (0.55 + 0.45 * Math.Abs(Math.Sin(time * 1.3 + d * 2.1)))
                    : 0.16;
                double h = Math.Max(3, maxH * v);
                double x = center.X - total / 2 + i * (bw + gap);
                dc.DrawRoundedRectangle(brush, null, new Rect(x, center.Y - h / 2, bw, h), 1.5, 1.5);
            }
        }

        /// <summary>One line of text that slowly scrolls when it doesn't fit.</summary>
        private void Marquee(DrawingContext dc, string s, Rect r, double size, FontWeight weight, Brush brush)
        {
            var ft = Text(s, size, weight, brush);
            double tw = ft.WidthIncludingTrailingWhitespace;
            double y = r.Y + (r.Height - ft.Height) / 2;
            if (tw <= r.Width)
            {
                dc.DrawText(ft, new Point(r.X, y));
                return;
            }
            const double gap = 36;
            double period = (tw + gap) / 28 + 2.5;
            double t = time % period;
            double offset = t < 2.5 ? 0 : (t - 2.5) * 28;
            dc.PushClip(new RectangleGeometry(r));
            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(r.Left, 0),
                EndPoint = new Point(r.Right, 0),
            };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.04));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.9));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            mask.Freeze();
            dc.PushOpacityMask(mask);
            dc.DrawText(ft, new Point(r.X - offset, y));
            dc.DrawText(ft, new Point(r.X - offset + tw + gap, y));
            dc.Pop();
            dc.Pop();
        }

        private static double Frac(double x) => x - Math.Floor(x);
        private static double Hash(int i, double k) => Frac(Math.Sin(i * 12.9898 + k * 78.233) * 43758.5453);

        /// <summary>Slow snowfall inside the open island.</summary>
        private void InnerSnow(DrawingContext dc, Rect area)
        {
            for (int i = 0; i < 36; i++)
            {
                double speed = 7 + Hash(i, 1) * 15;
                double r = 0.6 + Hash(i, 2) * 1.5;
                double span = area.Height + 10;
                double y = area.Y + (Hash(i, 3) * span + time * speed) % span - 5;
                double x = area.X + Hash(i, 4) * area.Width + Math.Sin(time * (0.5 + Hash(i, 5)) + i) * 7;
                double a = 0.1 + Hash(i, 6) * 0.35;
                dc.DrawEllipse(B(Colors.White, a), null, new Point(x, y), r, r);
            }
        }

        /// <summary>Icicles hanging from the island's lower edge, with a moving glint and, while music plays, drips.</summary>
        private void Icicles(DrawingContext dc, Rect area, int count, double maxLength, bool drips)
        {
            var ice = Rgb(0.92, 0.97, 1);
            var tip = Rgb(0.62, 0.82, 1);
            for (int i = 0; i < count; i++)
            {
                double u = (i + 0.3 + Hash(i, 7) * 0.4) / count;
                double x = area.X + u * area.Width;
                double len = maxLength * (0.3 + 0.7 * Hash(i, 8));
                double w = 1.6 + 2.2 * Hash(i, 9) * maxLength / 17;
                double top = area.Y - 1;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - w, top), true, true);
                    c.LineTo(new Point(x + w, top), true, false);
                    c.QuadraticBezierTo(new Point(x + w * 0.5, top + len * 0.55), new Point(x + 0.3, top + len), true, false);
                    c.QuadraticBezierTo(new Point(x - w * 0.4, top + len * 0.5), new Point(x - w, top), true, false);
                }
                g.Freeze();
                var brush = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(x, top),
                    EndPoint = new Point(x, top + len),
                };
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(242, ice.R, ice.G, ice.B), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(64, tip.R, tip.G, tip.B), 1));
                brush.Freeze();
                dc.DrawGeometry(brush, null, g);
                // A glint running along the icicles.
                double glint = Math.Max(0, Math.Sin(time * 1.8 - u * 9));
                if (glint > 0.75)
                    dc.DrawLine(new Pen(B(Colors.White, (glint - 0.75) * 3.2), 0.9), new Point(x - w * 0.35, top + 1.5), new Point(x - w * 0.05, top + len * 0.7));
                // Drops.
                if (drips && Hash(i, 10) > 0.45)
                {
                    double period = 2.2 + Hash(i, 11) * 3;
                    double phase = (time / period + Hash(i, 12)) % 1;
                    if (phase < 0.55)
                    {
                        double r = 1.3 * Math.Min(1, phase / 0.55);
                        dc.DrawEllipse(B(Rgb(0.8, 0.92, 1), 0.9), null, new Point(x + 0.3, top + len + r * 0.7), r, r * 1.1);
                    }
                    else
                    {
                        double fall = (phase - 0.55) / 0.45;
                        double y = top + len + fall * fall * (area.Height - len);
                        dc.DrawEllipse(B(Rgb(0.8, 0.92, 1), 0.9 * (1 - fall)), null, new Point(x, y + 1.3), 1, 1.3);
                    }
                }
            }
        }

        // ---- Clicks ----

        private string HitButton(Point p)
        {
            if (e < 0.8 || !now.HasTrack) return null;
            if (prevRect.Contains(p)) return "prev";
            if (playRect.Contains(p)) return "play";
            if (nextRect.Contains(p)) return "next";
            if (barRect.Contains(p)) return "bar";
            return null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
        {
            pressed = HitButton(args.GetPosition(this));
            args.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
        {
            var p = args.GetPosition(this);
            string hit = HitButton(p);
            if (hit != null && hit == pressed && service != null)
            {
                switch (hit)
                {
                    case "prev": service.Previous(); break;
                    case "play": service.PlayPause(); break;
                    case "next": service.Next(); break;
                    case "bar": service.Seek(Clamp01((p.X - barRect.X) / Math.Max(1, barRect.Width))); break;
                }
            }
            pressed = null;
            args.Handled = true;
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hit)
        {
            // The whole island body counts, including its transparent-looking parts.
            var body = new Rect(x0, 0, bodyW + flare * 2, bodyH + 4);
            return body.Contains(hit.HitPoint) ? new PointHitTestResult(this, hit.HitPoint) : null;
        }
    }
}
