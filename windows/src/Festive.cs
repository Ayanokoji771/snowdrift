using System;
using System.Collections.Generic;

namespace Snowdrift
{
    /// <summary>New Year extras for one screen: fireworks, a Christmas tree and frosty corners.</summary>
    public sealed class Festive
    {
        private readonly float W, H;
        /// <summary>The floor the tree stands on (top of the taskbar).</summary>
        private readonly float G;
        private Rng rng = Rng.Create();
        private float time;

        public Festive(float width, float height, float ground)
        {
            W = width;
            H = height;
            G = ground;
            treeH = MathF.Min(230, MathF.Max(130, height * 0.22f));
            treeW = treeH * 0.74f;
            treeX = width - treeW * 0.5f - 34;
            treeBox = new FRect(treeX - treeW * 0.5f - 34, G - treeH - 18, treeX + treeW * 0.5f + 34, G - 1);
            LayoutTree();
        }

        public void Step(float dt, bool showTree, List<FRect> occluders)
        {
            time += dt;
            StepFireworks(dt);
            bool free = true;
            foreach (var o in occluders)
            {
                if (o.X0 < treeBox.X1 && o.X1 > treeBox.X0 && o.Y0 < treeBox.Y1 && o.Y1 > treeBox.Y0) { free = false; break; }
            }
            float target = showTree && free ? 1 : 0;
            treeAlpha += (target - treeAlpha) * MathF.Min(1, dt * 5);
            if (MathF.Abs(target - treeAlpha) < 0.003f) treeAlpha = target;
        }

        // ---- Fireworks ----

        private struct Rocket { public float X, Y, VX, VY, Trail; public int Color, Kind; }

        private struct Spark
        {
            public float X, Y, VX, VY, Life, MaxLife, R, G, B, Size, Drag, Gravity;
            public bool Twinkle;
        }

        private readonly List<Rocket> rockets = new List<Rocket>();
        private readonly List<Spark> sparks = new List<Spark>();
        private float until;
        private float nextLaunch;

        private static readonly (float, float, float)[] Colors =
        {
            (1.0f, 0.32f, 0.30f), (1.0f, 0.80f, 0.28f), (0.40f, 1.0f, 0.50f), (0.38f, 0.66f, 1.0f),
            (1.0f, 0.45f, 0.88f), (0.74f, 0.52f, 1.0f), (1.0f, 1.0f, 1.0f), (1.0f, 0.58f, 0.18f),
        };

        public bool FireworksActive => time < until || rockets.Count > 0 || sparks.Count > 0;

        public void LaunchFireworks(float seconds)
        {
            until = MathF.Max(until, time + seconds);
            nextLaunch = MathF.Min(nextLaunch, time + 0.1f);
        }

        public void StopFireworks() => until = 0;

        private void StepFireworks(float dt)
        {
            if (time < until && time >= nextLaunch)
            {
                int salvo = rng.Unit() < 0.22f ? 3 : (rng.Unit() < 0.4f ? 2 : 1);
                for (int i = 0; i < salvo; i++)
                {
                    float apex = rng.Range(H * 0.12f, H * 0.42f);
                    const float g = 320;
                    float vy = -MathF.Sqrt(2 * g * (G - apex));
                    rockets.Add(new Rocket
                    {
                        X = rng.Range(W * 0.1f, W * 0.9f), Y = G + 4, VX = rng.Range(-35, 35), VY = vy,
                        Color = rng.Int(Colors.Length), Kind = rng.Int(5),
                    });
                }
                nextLaunch = time + rng.Range(0.45f, 1.3f);
            }

            int ri = 0;
            while (ri < rockets.Count)
            {
                var r = rockets[ri];
                r.VY += 320 * dt;
                r.X += r.VX * dt;
                r.Y += r.VY * dt;
                r.Trail += dt;
                while (r.Trail > 0.014f)
                {
                    r.Trail -= 0.014f;
                    AddSpark(r.X + rng.Range(-1, 1), r.Y + 2, rng.Range(-12, 12) - r.VX * 0.1f, rng.Range(10, 50),
                             rng.Range(0.25f, 0.55f), (1.0f, 0.78f, 0.45f), 1.1f, 2.5f, 60, false);
                }
                if (r.VY > -25)
                {
                    Explode(r);
                    rockets[ri] = rockets[rockets.Count - 1];
                    rockets.RemoveAt(rockets.Count - 1);
                    continue;
                }
                rockets[ri] = r;
                ri++;
            }

            int si = 0;
            while (si < sparks.Count)
            {
                var s = sparks[si];
                s.Life -= dt;
                if (s.Life <= 0)
                {
                    sparks[si] = sparks[sparks.Count - 1];
                    sparks.RemoveAt(sparks.Count - 1);
                    continue;
                }
                float k = MathF.Max(0, 1 - s.Drag * dt);
                s.VX *= k;
                s.VY = s.VY * k + s.Gravity * dt;
                s.X += s.VX * dt;
                s.Y += s.VY * dt;
                sparks[si] = s;
                si++;
            }
        }

        private void AddSpark(float x, float y, float vx, float vy, float life, (float, float, float) color,
                              float size, float drag, float gravity, bool twinkle)
        {
            if (sparks.Count >= 6000) return;
            sparks.Add(new Spark
            {
                X = x, Y = y, VX = vx, VY = vy, Life = life, MaxLife = life,
                R = color.Item1, G = color.Item2, B = color.Item3, Size = size, Drag = drag, Gravity = gravity, Twinkle = twinkle,
            });
        }

        private void Explode(Rocket r)
        {
            var main = Colors[r.Color];
            var second = Colors[(r.Color + 3) % Colors.Length];
            // Flash.
            AddSpark(r.X, r.Y, 0, 0, 0.22f, (1, 1, 1), 22, 0, 0, false);
            switch (r.Kind)
            {
                case 0: // Peony: a full ball of one colour.
                    for (int i = 0; i < 110; i++)
                    {
                        float a = rng.Range(0, 6.283f), sp = rng.Range(140, 230) * MathF.Sqrt(MathF.Sqrt(rng.Unit()));
                        AddSpark(r.X, r.Y, MathF.Cos(a) * sp, MathF.Sin(a) * sp, rng.Range(1.2f, 1.9f), main, rng.Range(1.4f, 2.1f), 1.25f, 70, false);
                    }
                    break;
                case 1: // Willow: slow golden trails that droop.
                    for (int i = 0; i < 80; i++)
                    {
                        float a = rng.Range(0, 6.283f), sp = rng.Range(110, 180);
                        AddSpark(r.X, r.Y, MathF.Cos(a) * sp, MathF.Sin(a) * sp, rng.Range(2.2f, 3.0f), (1.0f, 0.76f, 0.32f), rng.Range(1.2f, 1.7f), 1.7f, 45, true);
                    }
                    break;
                case 2: // Ring with a heart of another colour.
                    for (int k = 0; k < 64; k++)
                    {
                        float a = k / 64f * 6.283f;
                        AddSpark(r.X, r.Y, MathF.Cos(a) * 210, MathF.Sin(a) * 210, rng.Range(1.3f, 1.7f), main, 1.8f, 1.3f, 60, false);
                    }
                    for (int i = 0; i < 36; i++)
                    {
                        float a = rng.Range(0, 6.283f), sp = rng.Range(30, 90);
                        AddSpark(r.X, r.Y, MathF.Cos(a) * sp, MathF.Sin(a) * sp, rng.Range(1.0f, 1.4f), second, 1.6f, 1.4f, 60, false);
                    }
                    break;
                case 3: // Confetti: every spark its own colour, glittering.
                    for (int i = 0; i < 120; i++)
                    {
                        float a = rng.Range(0, 6.283f), sp = rng.Range(60, 220);
                        AddSpark(r.X, r.Y, MathF.Cos(a) * sp, MathF.Sin(a) * sp, rng.Range(1.1f, 1.8f), Colors[rng.Int(Colors.Length)],
                                 rng.Range(1.3f, 1.9f), 1.3f, 75, true);
                    }
                    break;
                default: // Two-tone chrysanthemum.
                    for (int k = 0; k < 120; k++)
                    {
                        float a = rng.Range(0, 6.283f), sp = rng.Range(150, 240);
                        AddSpark(r.X, r.Y, MathF.Cos(a) * sp, MathF.Sin(a) * sp, rng.Range(1.3f, 2.0f), k % 2 == 0 ? main : second,
                                 rng.Range(1.3f, 1.9f), 1.2f, 70, false);
                    }
                    break;
            }
        }

        public void DrawFireworks(Batch batch)
        {
            foreach (var r in rockets)
            {
                batch.Spr(Sprite.Glow, r.X, r.Y, 9, 9, 0, 1, 0.8f, 0.5f, 0.7f, true);
                batch.Spr(Sprite.Dot, r.X, r.Y, 2.2f, 2.2f, 0, 1, 0.95f, 0.8f, 1, true);
            }
            foreach (var s in sparks)
            {
                float t = s.Life / s.MaxLife;
                float a = MathF.Min(1, t * 1.8f);
                if (s.Twinkle && t < 0.6f)
                    a *= MathF.Sin(time * 38 + s.X * 0.7f + s.Y) > -0.2f ? 1 : 0.15f;
                if (s.Size > 10)
                {
                    // Explosion flash.
                    batch.Spr(Sprite.Glow, s.X, s.Y, s.Size * 3, s.Size * 3, 0, 1, 1, 1, a * 0.55f, true);
                    continue;
                }
                float sz = s.Size * 1.25f;
                batch.Spr(Sprite.Glow, s.X, s.Y, sz * 4.6f, sz * 4.6f, 0, s.R, s.G, s.B, a * 0.6f, true);
                const float core = 0.45f;
                batch.Spr(Sprite.Dot, s.X, s.Y, sz * 1.5f, sz * 1.5f, 0,
                          s.R + (1 - s.R) * core, s.G + (1 - s.G) * core, s.B + (1 - s.B) * core, a);
            }
        }

        // ---- Christmas tree ----

        private struct Ornament { public float X, Y, R; public int Color; }
        private struct Light { public float X, Y, Phase; public int Color; }
        private struct Tier { public float ApexY, BottomY, HalfW, Sag; }

        private readonly float treeH, treeW, treeX;
        private readonly FRect treeBox;
        private float treeAlpha;
        private readonly List<Tier> tiers = new List<Tier>();
        private readonly List<Ornament> ornaments = new List<Ornament>();
        private readonly List<Light> lights = new List<Light>();

        private static readonly (float, float, float)[] OrnamentColors =
        {
            (0.92f, 0.18f, 0.20f), (1.0f, 0.76f, 0.22f), (0.25f, 0.52f, 1.0f), (0.85f, 0.35f, 0.95f), (0.95f, 0.95f, 1.0f),
        };

        private void LayoutTree()
        {
            var seeded = new Rng(2027);
            float h = treeH, w = treeW;
            for (int i = 0; i < 4; i++)
            {
                float fi = i;
                float bottom = G - h * 0.11f - fi * h * 0.2f;
                float height = h * (0.36f - 0.03f * fi);
                tiers.Add(new Tier { ApexY = bottom - height, BottomY = bottom, HalfW = w * (1 - 0.2f * fi) / 2, Sag = height * 0.07f });
            }
            for (int i = 0; i < tiers.Count; i++)
            {
                var t = tiers[i];
                int count = 4 - i / 2;
                for (int k = 0; k < count; k++)
                {
                    float y = t.BottomY - (t.BottomY - t.ApexY) * seeded.Range(0.12f, 0.4f);
                    float half = t.HalfW * (y - t.ApexY) / (t.BottomY - t.ApexY) * 0.8f;
                    float x = treeX - half + (k + seeded.Range(0.25f, 0.75f)) / count * half * 2;
                    ornaments.Add(new Ornament { X = x, Y = y, R = h * seeded.Range(0.026f, 0.034f), Color = (i + k * 2) % OrnamentColors.Length });
                }
                // A swag of lights across each tier.
                float lineY = t.BottomY - (t.BottomY - t.ApexY) * 0.55f;
                float halfL = t.HalfW * 0.5f;
                for (int k = 0; k < 6; k++)
                {
                    float u = k / 5f;
                    float x = treeX - halfL + u * halfL * 2;
                    float y = lineY + MathF.Sin(u * MathF.PI) * (t.BottomY - t.ApexY) * 0.16f;
                    lights.Add(new Light { X = x, Y = y, Color = (k + i) % 4, Phase = seeded.Range(0, 6.28f) });
                }
            }
        }

        public void DrawTree(Batch batch)
        {
            float al = treeAlpha;
            if (al <= 0.003f) return;
            float h = treeH, w = treeW, cx = treeX;

            // A mound of snow around the foot of the tree.
            for (int k = 0; k < 26; k++)
            {
                float u = k / 25f * 2 - 1;
                float x = cx + u * w * 0.78f;
                float hgt = (1 - u * u) * 7 + 3;
                batch.Spr(Sprite.Dot, x, G - hgt * 0.5f, hgt * 1.1f + 3, hgt * 0.9f + 2, 0, 1, 1, 1, al);
            }

            // Trunk.
            batch.Rect(cx - w * 0.07f, G - h * 0.14f, w * 0.14f, h * 0.14f, 0.36f, 0.22f, 0.12f, al);
            batch.Rect(cx - w * 0.07f, G - h * 0.14f, w * 0.04f, h * 0.14f, 0.25f, 0.15f, 0.08f, al);

            // Branches, bottom tier first so upper tiers overlap it.
            foreach (var t in tiers)
            {
                var apex = batch.Flat(cx, t.ApexY, 0.22f, 0.58f, 0.32f, al);
                var left = batch.Flat(cx - t.HalfW, t.BottomY, 0.05f, 0.27f, 0.14f, al);
                var mid = batch.Flat(cx, t.BottomY + t.Sag, 0.08f, 0.37f, 0.19f, al);
                var right = batch.Flat(cx + t.HalfW, t.BottomY, 0.11f, 0.45f, 0.23f, al);
                batch.Tri(apex, left, mid);
                batch.Tri(apex, mid, right);
                // Snow along the lower edge and on the tip.
                int steps = (int)(t.HalfW * 2 / 5);
                for (int k = 0; k <= steps; k++)
                {
                    float u = (float)k / steps;
                    float x = cx - t.HalfW + u * t.HalfW * 2;
                    float y = t.BottomY + t.Sag * MathF.Sin(u * MathF.PI) - 1.5f;
                    float s = k % 3 == 0 ? 3.6f : 2.8f;
                    batch.Spr(Sprite.Dot, x, y, s, s, 0, 1, 1, 1, al * 0.95f);
                }
                for (int k = 0; k < 4; k++)
                {
                    float u = k / 4f * 0.22f;
                    batch.Spr(Sprite.Dot, cx - t.HalfW * u * 0.9f, t.ApexY + (t.BottomY - t.ApexY) * u + 2, 2.6f, 2.6f, 0, 1, 1, 1, al * 0.9f);
                }
            }

            foreach (var o in ornaments)
            {
                var c = OrnamentColors[o.Color];
                batch.Spr(Sprite.BallBase, o.X, o.Y, o.R, o.R, 0, c.Item1, c.Item2, c.Item3, al);
                batch.Spr(Sprite.Dot, o.X - o.R * 0.32f, o.Y - o.R * 0.35f, o.R * 0.35f, o.R * 0.35f, 0, 1, 1, 1, al * 0.8f);
            }

            int li = 0;
            int[] lightMap = { 1, 0, 3, 2 };
            foreach (var l in lights)
            {
                var c = Colors[lightMap[l.Color]];
                float tw = 0.45f + 0.55f * (0.5f + 0.5f * MathF.Sin(time * 2.6f + l.Phase + li * 0.4f));
                batch.Spr(Sprite.Glow, l.X, l.Y, 9, 9, 0, c.Item1, c.Item2, c.Item3, al * tw * 0.7f, true);
                batch.Spr(Sprite.Dot, l.X, l.Y, 1.9f, 1.9f, 0, 1, 1, 0.9f, al * (0.5f + 0.5f * tw), true);
                li++;
            }

            // Star.
            float sy = tiers.Count > 0 ? tiers[tiers.Count - 1].ApexY : G - h;
            float sr = h * 0.075f;
            float pulse = 0.75f + 0.25f * MathF.Sin(time * 2);
            batch.Spr(Sprite.Glow, cx, sy, sr * 4, sr * 4, 0, 1, 0.85f, 0.4f, al * 0.55f * pulse, true);
            var centre = batch.Flat(cx, sy, 1, 0.95f, 0.6f, al);
            for (int k = 0; k < 10; k++)
            {
                float a0 = k / 10f * 6.283f - MathF.PI / 2;
                float a1 = (k + 1) / 10f * 6.283f - MathF.PI / 2;
                float r0 = k % 2 == 0 ? sr : sr * 0.45f;
                float r1 = k % 2 == 0 ? sr * 0.45f : sr;
                batch.Tri(centre, batch.Flat(cx + MathF.Cos(a0) * r0, sy + MathF.Sin(a0) * r0, 1, 0.78f, 0.2f, al),
                          batch.Flat(cx + MathF.Cos(a1) * r1, sy + MathF.Sin(a1) * r1, 1, 0.78f, 0.2f, al));
            }

            // Presents.
            var boxes = new (float dx, float bw, float bh, (float, float, float) c, (float, float, float) rib)[]
            {
                (-0.46f, 32, 24, (0.86f, 0.17f, 0.2f), (1, 0.82f, 0.3f)),
                (0.30f, 26, 31, (0.2f, 0.42f, 0.9f), (0.95f, 0.95f, 1)),
                (-0.14f, 22, 16, (0.15f, 0.62f, 0.32f), (0.92f, 0.2f, 0.22f)),
            };
            foreach (var bx in boxes)
            {
                float x = cx + bx.dx * w - bx.bw / 2, y = G - bx.bh;
                var c = bx.c;
                var rib = bx.rib;
                batch.Rect(x, y, bx.bw, bx.bh, c.Item1, c.Item2, c.Item3, al);
                batch.Rect(x, y, bx.bw, bx.bh * 0.22f, c.Item1 * 1.12f, c.Item2 * 1.12f, c.Item3 * 1.12f, al);
                batch.Rect(x + bx.bw * 0.42f, y, bx.bw * 0.16f, bx.bh, rib.Item1, rib.Item2, rib.Item3, al);
                batch.Rect(x, y + bx.bh * 0.3f, bx.bw, bx.bh * 0.14f, rib.Item1, rib.Item2, rib.Item3, al);
                batch.Spr(Sprite.Dot, x + bx.bw * 0.38f, y - 2, 4, 3, 0, rib.Item1, rib.Item2, rib.Item3, al);
                batch.Spr(Sprite.Dot, x + bx.bw * 0.62f, y - 2, 4, 3, 0, rib.Item1, rib.Item2, rib.Item3, al);
            }
        }

        // ---- Frost ----

        public void DrawFrost(Batch batch)
        {
            float s = MathF.Min(W, H) * 0.3f;
            float hw = s / 2;
            const float a = 0.8f;
            batch.Spr(Sprite.Frost, hw, hw, hw, hw, 0, 0.92f, 0.96f, 1, a);
            batch.Spr(Sprite.Frost, W - hw, hw, hw, hw, 0, 0.92f, 0.96f, 1, a, false, true);
            batch.Spr(Sprite.Frost, hw, G - hw, hw, hw, 0, 0.92f, 0.96f, 1, a, false, false, true);
            batch.Spr(Sprite.Frost, W - hw, G - hw, hw, hw, 0, 0.92f, 0.96f, 1, a, false, true, true);
        }
    }
}
