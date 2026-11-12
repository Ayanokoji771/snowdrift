using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snowdrift
{
    /// <summary>Every picture the app draws, painted in code at start-up into one texture.
    /// The app ships no image files.</summary>
    public sealed class Atlas
    {
        public const int Size = 1024;
        public readonly UVRect[] UV = new UVRect[(int)Sprite.Count];
        /// <summary>BGRA, premultiplied, top-down rows of Size * 4 bytes.</summary>
        public readonly byte[] Pixels;

        public Atlas()
        {
            using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                int cursorX = 4, cursorY = 4, rowHeight = 0;
                void Place(Sprite sprite, int w, int h, Action<Graphics, float, float> draw)
                {
                    if (cursorX + w + 4 > Size)
                    {
                        cursorX = 4;
                        cursorY += rowHeight + 8;
                        rowHeight = 0;
                    }
                    var state = g.Save();
                    g.TranslateTransform(cursorX, cursorY);
                    g.SetClip(new RectangleF(0, 0, w, h));
                    draw(g, w, h);
                    g.Restore(state);
                    const float inset = 0.5f;
                    UV[(int)sprite] = new UVRect((cursorX + inset) / Size, (cursorY + inset) / Size,
                                                 (cursorX + w - inset) / Size, (cursorY + h - inset) / Size);
                    cursorX += w + 8;
                    rowHeight = Math.Max(rowHeight, h);
                }

                Place(Sprite.White, 16, 16, (c, w, h) => c.FillRectangle(Brushes.White, 0, 0, w, h));
                // Keep samples of the solid white sprite well inside it.
                var wr = UV[(int)Sprite.White];
                float mu = (wr.U0 + wr.U1) / 2, mv = (wr.V0 + wr.V1) / 2;
                UV[(int)Sprite.White] = new UVRect(mu, mv, mu, mv);

                Place(Sprite.Dot, 64, 64, DrawDot);
                Place(Sprite.Crystal1, 128, 128, (c, w, h) => DrawCrystal(c, w, h, 0));
                Place(Sprite.Crystal2, 128, 128, (c, w, h) => DrawCrystal(c, w, h, 1));
                Place(Sprite.Crystal3, 128, 128, (c, w, h) => DrawCrystal(c, w, h, 2));
                Place(Sprite.Glow, 128, 128, DrawGlow);
                Place(Sprite.Bulb, 64, 96, DrawBulb);
                Place(Sprite.Coal, 48, 48, DrawCoal);
                Place(Sprite.BallBase, 256, 256, DrawBallBase);
                Place(Sprite.BallTex, 256, 256, DrawBallTex);
                Place(Sprite.Carrot, 160, 64, DrawCarrot);
                Place(Sprite.Twig, 192, 96, DrawTwig);
                Place(Sprite.Bucket, 160, 160, DrawBucket);
                Place(Sprite.TopHat, 160, 160, DrawTopHat);
                Place(Sprite.Scarf, 224, 128, DrawScarf);
                Place(Sprite.Frost, 256, 256, DrawFrost);
            }

            Pixels = new byte[Size * Size * 4];
            var data = bmp.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < Size; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, Pixels, y * Size * 4, Size * 4);
            }
            finally { bmp.UnlockBits(data); }
        }

        // ---- Painting helpers ----

        public struct C
        {
            public float R, G, B, A;
            public C(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
            public Color ToColor() => Color.FromArgb(Clamp(A), Clamp(R), Clamp(G), Clamp(B));
            private static int Clamp(float v) => Math.Max(0, Math.Min(255, (int)MathF.Round(v * 255)));
        }

        /// <summary>Radial gradient: centre colour at `from`, last colour at `radius` around `to`.</summary>
        public static void Radial(Graphics g, C[] colors, float[] stops, PointF from, PointF to, float radius)
        {
            if (radius <= 0) return;
            using var path = new GraphicsPath();
            path.AddEllipse(to.X - radius, to.Y - radius, radius * 2, radius * 2);
            using var brush = new PathGradientBrush(path) { CenterPoint = from };
            int n = colors.Length;
            // PathGradientBrush counts positions from the edge (0) to the centre (1).
            var blend = new ColorBlend(n);
            for (int i = 0; i < n; i++)
            {
                blend.Colors[i] = colors[n - 1 - i].ToColor();
                blend.Positions[i] = 1 - stops[n - 1 - i];
            }
            blend.Positions[0] = 0;
            blend.Positions[n - 1] = 1;
            brush.InterpolationColors = blend;
            g.FillPath(brush, path);
        }

        /// <summary>Linear gradient from p0 to p1 that keeps its end colours beyond the ends.</summary>
        public static void Linear(Graphics g, RectangleF area, C[] colors, float[] stops, PointF p0, PointF p1)
        {
            float dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            float len2 = dx * dx + dy * dy;
            if (len2 < 1e-6f) return;
            float tmin = 0, tmax = 1;
            foreach (var pt in new[] { new PointF(area.Left, area.Top), new PointF(area.Right, area.Top),
                                       new PointF(area.Left, area.Bottom), new PointF(area.Right, area.Bottom) })
            {
                float t = ((pt.X - p0.X) * dx + (pt.Y - p0.Y) * dy) / len2;
                tmin = Math.Min(tmin, t);
                tmax = Math.Max(tmax, t);
            }
            tmin -= 0.01f;
            tmax += 0.01f;
            var q0 = new PointF(p0.X + dx * tmin, p0.Y + dy * tmin);
            var q1 = new PointF(p0.X + dx * tmax, p0.Y + dy * tmax);
            var cols = new List<Color> { colors[0].ToColor() };
            var pos = new List<float> { 0 };
            for (int i = 0; i < colors.Length; i++)
            {
                float p = (stops[i] - tmin) / (tmax - tmin);
                if (p <= pos[pos.Count - 1]) p = pos[pos.Count - 1] + 1e-4f;
                cols.Add(colors[i].ToColor());
                pos.Add(Math.Min(p, 0.9999f));
            }
            cols.Add(colors[colors.Length - 1].ToColor());
            pos.Add(1);
            using var brush = new LinearGradientBrush(q0, q1, Color.Black, Color.White)
            {
                InterpolationColors = new ColorBlend { Colors = cols.ToArray(), Positions = pos.ToArray() },
                WrapMode = WrapMode.TileFlipXY,
            };
            g.FillRectangle(brush, area);
        }

        /// <summary>Builds GraphicsPath figures with move/line/quad/cubic like a vector path.</summary>
        public sealed class PathBuilder : IDisposable
        {
            public readonly GraphicsPath Path = new GraphicsPath();
            private PointF cur;

            public PathBuilder Move(float x, float y) { Path.StartFigure(); cur = new PointF(x, y); return this; }
            public PathBuilder Line(float x, float y) { var p = new PointF(x, y); Path.AddLine(cur, p); cur = p; return this; }

            public PathBuilder Quad(float cx, float cy, float x, float y)
            {
                var c1 = new PointF(cur.X + 2f / 3 * (cx - cur.X), cur.Y + 2f / 3 * (cy - cur.Y));
                var c2 = new PointF(x + 2f / 3 * (cx - x), y + 2f / 3 * (cy - y));
                var p = new PointF(x, y);
                Path.AddBezier(cur, c1, c2, p);
                cur = p;
                return this;
            }

            public PathBuilder Cubic(float c1x, float c1y, float c2x, float c2y, float x, float y)
            {
                var p = new PointF(x, y);
                Path.AddBezier(cur, new PointF(c1x, c1y), new PointF(c2x, c2y), p);
                cur = p;
                return this;
            }

            public PathBuilder Close() { Path.CloseFigure(); return this; }
            public void Dispose() => Path.Dispose();
        }

        private static C Cc(float r, float g, float b, float a) => new C(r, g, b, a);

        private static void ClipPath(Graphics g, GraphicsPath p) => g.SetClip(p, CombineMode.Intersect);

        // ---- The pictures ----

        public static void DrawDot(Graphics g, float w, float h)
        {
            var p = new PointF(w / 2, h / 2);
            Radial(g, new[] { Cc(1, 1, 1, 1), Cc(1, 1, 1, 0.95f), Cc(1, 1, 1, 0.35f), Cc(1, 1, 1, 0) },
                   new[] { 0, 0.42f, 0.7f, 1 }, p, p, w / 2 - 1);
        }

        public static void DrawGlow(Graphics g, float w, float h)
        {
            var p = new PointF(w / 2, h / 2);
            Radial(g, new[] { Cc(1, 1, 1, 0.85f), Cc(1, 1, 1, 0.32f), Cc(1, 1, 1, 0.08f), Cc(1, 1, 1, 0) },
                   new[] { 0, 0.3f, 0.65f, 1 }, p, p, w / 2 - 1);
        }

        /// <summary>A six-armed snow crystal. Also used for the icons.</summary>
        public static void DrawCrystal(Graphics g, float w, float h, int variant, float lineScale = 1, float alpha = 1, Color? color = null)
        {
            float cx = w / 2, cy = h / 2;
            float radius = w / 2 * 0.86f;
            (float at, float len)[] branches = variant switch
            {
                0 => new[] { (0.42f, 0.30f), (0.68f, 0.22f) },
                1 => new[] { (0.34f, 0.20f), (0.56f, 0.30f), (0.80f, 0.16f) },
                _ => new[] { (0.55f, 0.36f) },
            };
            var baseColor = color ?? Color.White;
            var col = Color.FromArgb(Math.Clamp((int)(alpha * baseColor.A), 0, 255), baseColor.R, baseColor.G, baseColor.B);
            using var thick = new Pen(col, w * 0.062f * lineScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var thin = new Pen(col, w * 0.05f * lineScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var fill = new SolidBrush(col);
            for (int arm = 0; arm < 6; arm++)
            {
                var state = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(arm * 60);
                g.DrawLine(thick, 0, 0, 0, -radius);
                foreach (var (at, len) in branches)
                {
                    float by = -radius * at;
                    foreach (float side in new[] { -1f, 1f })
                        g.DrawLine(thin, 0, by, side * radius * len * 0.87f, by - radius * len * 0.5f);
                }
                if (variant == 2)
                    g.FillEllipse(fill, -w * 0.045f, -radius - w * 0.02f, w * 0.09f, w * 0.09f);
                g.Restore(state);
            }
        }

        public static void DrawBulb(Graphics g, float w, float h)
        {
            using var pb = new PathBuilder();
            pb.Move(w * 0.5f - w * 0.14f, h * 0.04f)
              .Line(w * 0.5f + w * 0.14f, h * 0.04f)
              .Cubic(w * 1.02f, h * 0.38f, w * 0.86f, h * 0.97f, w * 0.5f, h * 0.97f)
              .Cubic(w * 0.14f, h * 0.97f, -w * 0.02f, h * 0.38f, w * 0.5f - w * 0.14f, h * 0.04f)
              .Close();
            var state = g.Save();
            ClipPath(g, pb.Path);
            Radial(g, new[] { Cc(1, 1, 1, 1), Cc(0.92f, 0.92f, 0.92f, 1), Cc(0.66f, 0.66f, 0.66f, 1) }, new[] { 0, 0.5f, 1 },
                   new PointF(w * 0.4f, h * 0.5f), new PointF(w * 0.5f, h * 0.55f), h * 0.55f);
            g.Restore(state);
        }

        public static void DrawCoal(Graphics g, float w, float h)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(3, 3, w - 6, h - 6);
            var state = g.Save();
            ClipPath(g, path);
            Radial(g, new[] { Cc(0.36f, 0.38f, 0.42f, 1), Cc(0.13f, 0.14f, 0.17f, 1), Cc(0.05f, 0.05f, 0.07f, 1) }, new[] { 0, 0.55f, 1 },
                   new PointF(w * 0.38f, h * 0.34f), new PointF(w * 0.5f, h * 0.5f), w * 0.5f);
            g.Restore(state);
        }

        public static void DrawBallBase(Graphics g, float w, float h)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(3, 3, w - 6, h - 6);
            var state = g.Save();
            ClipPath(g, path);
            Radial(g, new[] { Cc(1, 1, 1, 1), Cc(0.97f, 0.985f, 1, 1), Cc(0.87f, 0.92f, 0.975f, 1), Cc(0.70f, 0.79f, 0.90f, 1) },
                   new[] { 0, 0.45f, 0.8f, 1 }, new PointF(w * 0.37f, h * 0.32f), new PointF(w * 0.47f, h * 0.45f), w * 0.62f);
            g.Restore(state);
            // Thin cool outline so the ball reads against white drifts.
            using var pen = new Pen(Cc(0.62f, 0.72f, 0.86f, 0.55f).ToColor(), 2.5f);
            g.DrawEllipse(pen, 4, 4, w - 8, h - 8);
        }

        /// <summary>Faint dimples that turn with the ball, so rolling is visible.</summary>
        public static void DrawBallTex(Graphics g, float w, float h)
        {
            var rng = new Rng(0xC0FFEE);
            using var path = new GraphicsPath();
            path.AddEllipse(8, 8, w - 16, h - 16);
            var state = g.Save();
            ClipPath(g, path);
            for (int i = 0; i < 26; i++)
            {
                float a = rng.Range(0, 2 * MathF.PI);
                float d = rng.Range(0.1f, 0.84f) * w / 2;
                float r = rng.Range(5, 13);
                var p = new PointF(w / 2 + MathF.Cos(a) * d, h / 2 + MathF.Sin(a) * d);
                Radial(g, new[] { Cc(0.55f, 0.66f, 0.82f, 0.30f), Cc(0.55f, 0.66f, 0.82f, 0) }, new[] { 0f, 1f }, p, p, r);
                var q = new PointF(p.X - r * 0.5f, p.Y - r * 0.5f);
                Radial(g, new[] { Cc(1, 1, 1, 0.5f), Cc(1, 1, 1, 0) }, new[] { 0f, 1f }, q, q, r * 0.7f);
            }
            g.Restore(state);
        }

        /// <summary>Carrot nose pointing right.</summary>
        public static void DrawCarrot(Graphics g, float w, float h)
        {
            using var pb = new PathBuilder();
            pb.Move(w * 0.04f, h * 0.5f)
              .Cubic(w * 0.03f, h * 0.3f, w * 0.06f, h * 0.14f, w * 0.12f, h * 0.12f)
              .Line(w * 0.97f, h * 0.47f)
              .Line(w * 0.97f, h * 0.53f)
              .Line(w * 0.12f, h * 0.88f)
              .Cubic(w * 0.06f, h * 0.86f, w * 0.03f, h * 0.7f, w * 0.04f, h * 0.5f)
              .Close();
            var state = g.Save();
            ClipPath(g, pb.Path);
            Linear(g, new RectangleF(0, 0, w, h), new[] { Cc(1.0f, 0.66f, 0.24f, 1), Cc(0.98f, 0.50f, 0.10f, 1), Cc(0.82f, 0.34f, 0.05f, 1) },
                   new[] { 0, 0.5f, 1 }, new PointF(0, h * 0.1f), new PointF(0, h * 0.9f));
            using var pen = new Pen(Cc(0.72f, 0.28f, 0.03f, 0.55f).ToColor(), 2.5f);
            for (int i = 1; i <= 4; i++)
            {
                float x = w * (0.16f + i * 0.14f);
                using var line = new PathBuilder();
                line.Move(x, h * 0.2f).Quad(x + w * 0.05f, h * 0.4f, x + w * 0.02f, h * 0.62f);
                g.DrawPath(pen, line.Path);
            }
            g.Restore(state);
        }

        /// <summary>Twig arm growing from the left edge towards the upper right.</summary>
        public static void DrawTwig(Graphics g, float w, float h)
        {
            void Stroke(PointF a, PointF b, float width)
            {
                using var dark = new Pen(Cc(0.30f, 0.19f, 0.11f, 1).ToColor(), width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(dark, a, b);
                using var light = new Pen(Cc(0.50f, 0.34f, 0.20f, 1).ToColor(), width * 0.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(light, a.X, a.Y - width * 0.15f, b.X, b.Y - width * 0.15f);
            }
            var root = new PointF(w * 0.04f, h * 0.62f);
            var elbow = new PointF(w * 0.62f, h * 0.46f);
            Stroke(root, elbow, h * 0.11f);
            Stroke(elbow, new PointF(w * 0.94f, h * 0.30f), h * 0.085f);
            Stroke(elbow, new PointF(w * 0.86f, h * 0.66f), h * 0.07f);
            Stroke(new PointF(w * 0.78f, h * 0.38f), new PointF(w * 0.84f, h * 0.10f), h * 0.06f);
            Stroke(new PointF(w * 0.36f, h * 0.53f), new PointF(w * 0.46f, h * 0.26f), h * 0.06f);
        }

        /// <summary>Upside-down bucket, the classic snowman hat.</summary>
        public static void DrawBucket(Graphics g, float w, float h)
        {
            using var pb = new PathBuilder();
            pb.Move(w * 0.30f, h * 0.14f).Line(w * 0.70f, h * 0.14f).Line(w * 0.86f, h * 0.84f)
              .Quad(w * 0.5f, h * 0.96f, w * 0.14f, h * 0.84f).Close();
            var state = g.Save();
            ClipPath(g, pb.Path);
            Linear(g, new RectangleF(0, 0, w, h),
                   new[] { Cc(0.42f, 0.50f, 0.60f, 1), Cc(0.80f, 0.86f, 0.92f, 1), Cc(0.60f, 0.68f, 0.77f, 1), Cc(0.36f, 0.43f, 0.53f, 1) },
                   new[] { 0, 0.3f, 0.62f, 1 }, new PointF(w * 0.14f, 0), new PointF(w * 0.86f, 0));
            using (var band = new Pen(Cc(0.25f, 0.31f, 0.40f, 0.7f).ToColor(), 3))
            {
                foreach (float y in new[] { 0.34f, 0.60f })
                {
                    using var line = new PathBuilder();
                    line.Move(0, h * y).Quad(w * 0.5f, h * (y + 0.07f), w, h * y);
                    g.DrawPath(band, line.Path);
                }
            }
            g.Restore(state);
            // Rim.
            using var rim = new Pen(Cc(0.30f, 0.37f, 0.47f, 1).ToColor(), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var rimPath = new PathBuilder();
            rimPath.Move(w * 0.12f, h * 0.84f).Quad(w * 0.5f, h * 0.97f, w * 0.88f, h * 0.84f);
            g.DrawPath(rim, rimPath.Path);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.01f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void DrawTopHat(Graphics g, float w, float h)
        {
            var crown = new RectangleF(w * 0.27f, h * 0.12f, w * 0.46f, h * 0.64f);
            using (var cp = RoundedRect(crown, 8))
            {
                var state = g.Save();
                ClipPath(g, cp);
                Linear(g, new RectangleF(0, 0, w, h), new[] { Cc(0.10f, 0.11f, 0.14f, 1), Cc(0.30f, 0.32f, 0.38f, 1), Cc(0.12f, 0.13f, 0.16f, 1) },
                       new[] { 0, 0.32f, 1 }, new PointF(crown.Left, 0), new PointF(crown.Right, 0));
                using var band = new SolidBrush(Cc(0.82f, 0.16f, 0.20f, 1).ToColor());
                g.FillRectangle(band, crown.Left, h * 0.60f, crown.Width, h * 0.13f);
                g.Restore(state);
            }
            var brim = new RectangleF(w * 0.08f, h * 0.74f, w * 0.84f, h * 0.13f);
            using var bp = RoundedRect(brim, brim.Height / 2);
            var st2 = g.Save();
            ClipPath(g, bp);
            Linear(g, new RectangleF(0, 0, w, h), new[] { Cc(0.26f, 0.28f, 0.33f, 1), Cc(0.08f, 0.09f, 0.11f, 1) }, new[] { 0f, 1f },
                   new PointF(0, brim.Top), new PointF(0, brim.Bottom));
            g.Restore(st2);
        }

        /// <summary>Drawn in greys so one picture can be tinted any colour.</summary>
        public static void DrawScarf(Graphics g, float w, float h)
        {
            void Striped(GraphicsPath path, bool vertical)
            {
                var state = g.Save();
                ClipPath(g, path);
                g.FillRectangle(Brushes.White, 0, 0, w, h);
                using var stripe = new SolidBrush(Cc(0.72f, 0.72f, 0.72f, 1).ToColor());
                float step = w * 0.09f;
                for (float t = 0; t < Math.Max(w, h); t += step)
                {
                    if (vertical) g.FillRectangle(stripe, 0, t, w, step * 0.5f);
                    else g.FillRectangle(stripe, t, 0, step * 0.5f, h);
                }
                Linear(g, new RectangleF(0, 0, w, h), new[] { Cc(0, 0, 0, 0), Cc(0, 0, 0, 0.22f) }, new[] { 0f, 1f },
                       new PointF(0, 0), new PointF(0, h));
                g.Restore(state);
            }
            using (var tail = new PathBuilder())
            {
                tail.Move(w * 0.60f, h * 0.22f).Line(w * 0.78f, h * 0.22f).Line(w * 0.84f, h * 0.94f).Line(w * 0.66f, h * 0.98f).Close();
                Striped(tail.Path, true);
            }
            using var bandPath = new PathBuilder();
            bandPath.Move(w * 0.04f, h * 0.10f).Quad(w * 0.5f, h * 0.30f, w * 0.96f, h * 0.10f).Line(w * 0.96f, h * 0.34f)
                    .Quad(w * 0.5f, h * 0.56f, w * 0.04f, h * 0.34f).Close();
            Striped(bandPath.Path, false);
        }

        /// <summary>Frost growing from the top-left corner: a cold haze with ice ferns.</summary>
        public static void DrawFrost(Graphics g, float w, float h)
        {
            Radial(g, new[] { Cc(1, 1, 1, 0.5f), Cc(0.92f, 0.96f, 1, 0.22f), Cc(0.92f, 0.96f, 1, 0.06f), Cc(0.92f, 0.96f, 1, 0) },
                   new[] { 0, 0.35f, 0.7f, 1 }, new PointF(0, 0), new PointF(0, 0), w);
            var rng = new Rng(1231);
            void Fern(float x, float y, float angle, float length, float width, int depth)
            {
                if (depth <= 0 || length <= 3) return;
                float x2 = x + MathF.Cos(angle) * length, y2 = y + MathF.Sin(angle) * length;
                float dist = MathF.Sqrt(x2 * x2 + y2 * y2) / w;
                float alpha = MathF.Max(0, 0.75f - dist * 0.75f) * (0.45f + 0.15f * depth);
                using (var pen = new Pen(Cc(1, 1, 1, alpha).ToColor(), width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, x, y, x2, y2);
                int steps = Math.Max(1, (int)(length / 9));
                for (int k = 1; k < Math.Max(2, steps); k++)
                {
                    float t = (float)k / steps;
                    float px = x + MathF.Cos(angle) * length * t, py = y + MathF.Sin(angle) * length * t;
                    float side = k % 2 == 0 ? 1 : -1;
                    Fern(px, py, angle + side * rng.Range(0.8f, 1.15f), length * 0.3f * (1.1f - t * 0.6f), width * 0.6f, depth - 1);
                }
                Fern(x2, y2, angle + rng.Range(-0.4f, 0.4f), length * 0.6f, width * 0.78f, depth - 1);
            }
            for (int i = 0; i < 10; i++)
            {
                float a = rng.Range(0.08f, 1.49f);
                float sx = rng.Range(0, 14), sy = rng.Range(0, 14);
                Fern(sx, sy, a, rng.Range(50, 105) * w / 256, 2.2f, 3);
            }
            // A few loose ice crystals.
            for (int i = 0; i < 26; i++)
            {
                float d = rng.Range(0.1f, 0.75f) * w, a = rng.Range(0, 1.57f);
                float size = rng.Range(5, 12);
                var state = g.Save();
                g.TranslateTransform(MathF.Cos(a) * d - size / 2, MathF.Sin(a) * d - size / 2);
                DrawCrystal(g, size, size, 2, 1.4f, MathF.Max(0, 0.7f - d / w));
                g.Restore(state);
            }
        }

        /// <summary>The app icon: night sky, falling snow, drifts and a glowing crystal.</summary>
        public static Bitmap MakeIcon(int size, bool dim = false)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = size / 1024f;
            g.ScaleTransform(s, s);
            var body = new RectangleF(40, 40, 944, 944);
            using var shape = RoundedRect(body, 210);
            var state = g.Save();
            ClipPath(g, shape);
            C top = dim ? Cc(0.22f, 0.24f, 0.28f, 1) : Cc(0.06f, 0.10f, 0.28f, 1);
            C mid = dim ? Cc(0.32f, 0.34f, 0.38f, 1) : Cc(0.16f, 0.30f, 0.64f, 1);
            C bot = dim ? Cc(0.46f, 0.48f, 0.52f, 1) : Cc(0.42f, 0.62f, 0.93f, 1);
            Linear(g, body, new[] { top, mid, bot }, new[] { 0, 0.6f, 1 }, new PointF(0, body.Top), new PointF(0, body.Bottom));
            var rng = new Rng(42);
            for (int i = 0; i < 60; i++)
            {
                float x = rng.Range(60, 964), y = rng.Range(60, 760), r = rng.Range(5, 12);
                using var b = new SolidBrush(Cc(1, 1, 1, rng.Range(0.35f, 0.9f)).ToColor());
                g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
            }
            using (var back = new PathBuilder())
            {
                back.Move(40, 800).Cubic(330, 690, 660, 840, 984, 750).Line(984, 984).Line(40, 984).Close();
                using var b = new SolidBrush(Cc(0.86f, 0.91f, 0.98f, 1).ToColor());
                g.FillPath(b, back.Path);
            }
            using (var front = new PathBuilder())
            {
                front.Move(40, 860).Cubic(400, 770, 700, 910, 984, 830).Line(984, 984).Line(40, 984).Close();
                g.FillPath(Brushes.White, front.Path);
            }
            g.Restore(state);
            var st2 = g.Save();
            g.TranslateTransform(512 - 300, 430 - 300);
            DrawCrystal(g, 600, 600, 1, 1.15f);
            g.Restore(st2);
            return bmp;
        }
    }
}
