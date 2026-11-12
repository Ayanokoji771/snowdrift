using System;
using System.Runtime.InteropServices;

namespace Snowdrift
{
    /// <summary>Every picture the app draws; all painted in code at start-up (see Atlas).</summary>
    public enum Sprite
    {
        White, Dot, Crystal1, Crystal2, Crystal3,
        BallBase, BallTex, Glow, Bulb,
        Coal, Carrot, Twig, Bucket, TopHat, Scarf,
        Frost,
        Count,
    }

    public struct UVRect
    {
        public float U0, V0, U1, V1;
        public UVRect(float u0, float v0, float u1, float v1) { U0 = u0; V0 = v0; U1 = u1; V1 = v1; }
    }

    /// <summary>Position in points (top-left origin), atlas coordinate, premultiplied colour.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public float X, Y, U, V, R, G, B, A;
        public Vertex(float x, float y, float u, float v, float r, float g, float b, float a)
        {
            X = x; Y = y; U = u; V = v; R = r; G = g; B = b; A = a;
        }
    }

    /// <summary>A frame's worth of triangles in painter's order.</summary>
    public sealed class Batch
    {
        public Vertex[] Verts = new Vertex[1 << 16];
        public int Count;
        private readonly UVRect[] uv;
        private readonly float whiteU, whiteV;

        public Batch(UVRect[] uv)
        {
            this.uv = uv;
            whiteU = uv[(int)Sprite.White].U0;
            whiteV = uv[(int)Sprite.White].V0;
        }

        public void Clear() => Count = 0;

        private void Ensure(int n)
        {
            if (Count + n > Verts.Length) Array.Resize(ref Verts, Math.Max(Verts.Length * 2, Count + n));
        }

        /// <summary>Textured quad. Colour is straight alpha; additive makes it glow instead of cover.</summary>
        public void Spr(Sprite s, float x, float y, float hw, float hh, float rot = 0,
                        float r = 1, float g = 1, float b = 1, float a = 1,
                        bool additive = false, bool flipX = false, bool flipY = false)
        {
            UVRect t = uv[(int)s];
            float u0 = flipX ? t.U1 : t.U0, u1 = flipX ? t.U0 : t.U1;
            float tv0 = flipY ? t.V1 : t.V0, tv1 = flipY ? t.V0 : t.V1;
            float pr = r * a, pg = g * a, pb = b * a, pa = additive ? a * 0.45f : a;
            float ax = hw, ay = 0, bx = 0, by = hh;
            if (rot != 0)
            {
                float c = MathF.Cos(rot), sn = MathF.Sin(rot);
                ax = hw * c; ay = hw * sn;
                bx = -hh * sn; by = hh * c;
            }
            Ensure(6);
            var v0 = new Vertex(x - ax - bx, y - ay - by, u0, tv0, pr, pg, pb, pa);
            var v1 = new Vertex(x + ax - bx, y + ay - by, u1, tv0, pr, pg, pb, pa);
            var v2 = new Vertex(x - ax + bx, y - ay + by, u0, tv1, pr, pg, pb, pa);
            var v3 = new Vertex(x + ax + bx, y + ay + by, u1, tv1, pr, pg, pb, pa);
            Verts[Count++] = v0; Verts[Count++] = v1; Verts[Count++] = v2;
            Verts[Count++] = v2; Verts[Count++] = v1; Verts[Count++] = v3;
        }

        /// <summary>Flat-coloured vertex (premultiplied colour).</summary>
        public Vertex Flat(float x, float y, float r, float g, float b, float a)
            => new Vertex(x, y, whiteU, whiteV, r * a, g * a, b * a, a);

        public void Quad(Vertex a, Vertex b, Vertex c, Vertex d)
        {
            Ensure(6);
            Verts[Count++] = a; Verts[Count++] = b; Verts[Count++] = c;
            Verts[Count++] = c; Verts[Count++] = b; Verts[Count++] = d;
        }

        public void Tri(Vertex a, Vertex b, Vertex c)
        {
            Ensure(3);
            Verts[Count++] = a; Verts[Count++] = b; Verts[Count++] = c;
        }

        public void Rect(float x, float y, float w, float h, float r, float g, float b, float a)
        {
            Quad(Flat(x, y, r, g, b, a), Flat(x + w, y, r, g, b, a), Flat(x, y + h, r, g, b, a), Flat(x + w, y + h, r, g, b, a));
        }

        /// <summary>Thick line segment.</summary>
        public void Line(float x0, float y0, float x1, float y1, float width, float r, float g, float b, float a)
        {
            float dx = x1 - x0, dy = y1 - y0;
            float len = MathF.Max(0.0001f, MathF.Sqrt(dx * dx + dy * dy));
            float nx = -dy / len * width / 2, ny = dx / len * width / 2;
            Quad(Flat(x0 + nx, y0 + ny, r, g, b, a), Flat(x1 + nx, y1 + ny, r, g, b, a),
                 Flat(x0 - nx, y0 - ny, r, g, b, a), Flat(x1 - nx, y1 - ny, r, g, b, a));
        }
    }
}
