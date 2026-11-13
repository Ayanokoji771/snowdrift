using System;
using System.Collections.Generic;

namespace Snowdrift
{
    /// <summary>Extras for one screen: fireworks, a corner decoration for each season (Christmas tree, cherry tree,
    /// sunflowers, pumpkins), frosty corners, and the small living things: butterflies and fireflies.</summary>
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

        public int Season { get; private set; }
        private bool evening;
        private List<FRect> occluders = new List<FRect>();
        private bool creatures = true;

        public void Step(float dt, int season, bool showDecor, bool evening, bool creatures, float? cursorX, float cursorY, List<FRect> occluders)
        {
            time += dt;
            if (season != Season)
            {
                // The old decoration goes at once; the new one fades in.
                Season = season;
                treeAlpha = 0;
                fallingPetals.Clear();
                butterflies.Clear();
            }
            this.evening = evening;
            this.occluders = occluders;
            if (!creatures)
            {
                butterflies.Clear();
                fireflies.Clear();
            }
            this.creatures = creatures;
            StepFireworks(dt);
            bool free = true;
            foreach (var o in occluders)
            {
                if (o.X0 < treeBox.X1 && o.X1 > treeBox.X0 && o.Y0 < treeBox.Y1 && o.Y1 > treeBox.Y0) { free = false; break; }
            }
            float target = showDecor && free ? 1 : 0;
            treeAlpha += (target - treeAlpha) * MathF.Min(1, dt * 5);
            if (MathF.Abs(target - treeAlpha) < 0.003f) treeAlpha = target;
            StepFallingPetals(dt);
            StepButterflies(dt, cursorX, cursorY);
            StepFireflies(dt, cursorX, cursorY);
        }

        /// <summary>The decoration in the corner for this season.</summary>
        public void DrawDecor(Batch batch)
        {
            switch (Season)
            {
                case 1: DrawSakura(batch); break;
                case 2: DrawSunflowers(batch); break;
                case 3: DrawPumpkins(batch); break;
                default: DrawTree(batch); break;
            }
        }

        /// <summary>Butterflies, fireflies and petals blowing off the cherry tree (in front of everything else).</summary>
        public void DrawCreatures(Batch batch)
        {
            DrawFallingPetals(batch);
            DrawButterflies(batch);
            DrawFireflies(batch);
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

        // ---- Spring: a cherry tree in blossom ----

        private struct Twig { public float X0, Y0, X1, Y1, Width; }
        private struct Blossom { public float X, Y, R, Rot; public int Tint; }

        private List<Twig> twigs;
        private List<Blossom> clouds, flowers;

        private void LayoutSakura()
        {
            if (twigs != null) return;
            var r = new Rng(31415);
            float h = treeH, cx = treeX;
            twigs = new List<Twig>();
            clouds = new List<Blossom>();
            flowers = new List<Blossom>();
            var ends = new List<(float, float)>();
            // A trunk that leans a little, then branches that split twice.
            float tx = cx - h * 0.04f, ty = G - h * 0.52f;
            twigs.Add(new Twig { X0 = cx, Y0 = G, X1 = tx, Y1 = ty, Width = h * 0.075f });
            void Branch(float x, float y, float angle, float length, float width, int depth)
            {
                float x2 = x + MathF.Sin(angle) * length, y2 = y - MathF.Cos(angle) * length;
                twigs.Add(new Twig { X0 = x, Y0 = y, X1 = x2, Y1 = y2, Width = width });
                if (depth == 0) { ends.Add((x2, y2)); return; }
                foreach (float side in new[] { -1f, 1f })
                    Branch(x2, y2, angle + side * r.Range(0.35f, 0.7f), length * r.Range(0.6f, 0.75f), width * 0.62f, depth - 1);
            }
            foreach (float a in new[] { -0.75f, -0.1f, 0.6f })
                Branch(tx, ty, a + r.Range(-0.1f, 0.1f), h * r.Range(0.2f, 0.26f), h * 0.045f, 2);
            foreach (var (x, y) in ends)
            {
                for (int k = 0; k < 4; k++)
                    clouds.Add(new Blossom { X = x + r.Range(-h * 0.08f, h * 0.08f), Y = y + r.Range(-h * 0.06f, h * 0.05f), R = h * r.Range(0.07f, 0.11f), Tint = r.Int(Palette.Petals.Length) });
                for (int k = 0; k < 5; k++)
                    flowers.Add(new Blossom { X = x + r.Range(-h * 0.11f, h * 0.11f), Y = y + r.Range(-h * 0.08f, h * 0.07f), R = h * r.Range(0.022f, 0.03f), Tint = r.Int(Palette.Petals.Length), Rot = r.Range(0, 1.26f) });
            }
        }

        private void DrawSakura(Batch batch)
        {
            float al = treeAlpha;
            if (al <= 0.003f) return;
            LayoutSakura();
            // A carpet of fallen petals under the tree.
            for (int k = 0; k < 22; k++)
            {
                float u = k / 21f * 2 - 1;
                var c = Palette.Petals[k % Palette.Petals.Length];
                batch.Spr(Sprite.Dot, treeX + u * treeW * 0.8f, G - 2.5f, 9, 3.5f, 0, c.R, c.G, c.B, al * 0.8f);
            }
            foreach (var tw in twigs)
            {
                batch.Line(tw.X0, tw.Y0, tw.X1, tw.Y1, tw.Width, 0.30f, 0.18f, 0.14f, al);
                batch.Line(tw.X0 - tw.Width * 0.18f, tw.Y0, tw.X1 - tw.Width * 0.18f, tw.Y1, tw.Width * 0.35f, 0.44f, 0.29f, 0.22f, al);
            }
            foreach (var b in clouds)
            {
                var c = Palette.Petals[b.Tint];
                batch.Spr(Sprite.Dot, b.X, b.Y, b.R, b.R * 0.9f, 0, c.R, c.G, c.B, al * 0.85f);
            }
            foreach (var b in flowers)
            {
                var c = Palette.Petals[b.Tint];
                for (int k = 0; k < 5; k++)
                {
                    float a = b.Rot + k / 5f * 6.283f;
                    batch.Spr(Sprite.Petal, b.X + MathF.Sin(a) * b.R * 0.7f, b.Y - MathF.Cos(a) * b.R * 0.7f, b.R * 0.62f, b.R * 0.78f, a,
                              MathF.Min(1, c.R + 0.05f), MathF.Min(1, c.G + 0.08f), MathF.Min(1, c.B + 0.06f), al);
                }
                batch.Spr(Sprite.Dot, b.X, b.Y, b.R * 0.32f, b.R * 0.32f, 0, 0.95f, 0.45f, 0.55f, al);
            }
        }

        // Petals blowing off the cherry tree.
        private struct FallingPetal { public float X, Y, VX, VY, Rot, Spin, Flip, Life; public int Tint; }
        private readonly List<FallingPetal> fallingPetals = new List<FallingPetal>();
        private float petalClock;

        private void StepFallingPetals(float dt)
        {
            if (Season == 1 && treeAlpha > 0.5f)
            {
                LayoutSakura();
                petalClock += dt;
                while (petalClock > 0.35f && clouds.Count > 0)
                {
                    petalClock -= 0.35f;
                    var b = clouds[rng.Int(clouds.Count)];
                    fallingPetals.Add(new FallingPetal
                    {
                        X = b.X + rng.Range(-b.R, b.R), Y = b.Y + rng.Range(-b.R, b.R) * 0.5f, VX = rng.Range(-30, 10), VY = rng.Range(8, 20),
                        Rot = rng.Range(0, 6.28f), Spin = rng.Range(-3, 3), Flip = rng.Range(0, 6.28f), Tint = rng.Int(Palette.Petals.Length),
                    });
                }
            }
            int i = 0;
            while (i < fallingPetals.Count)
            {
                var p = fallingPetals[i];
                p.Life += dt;
                p.VX += (MathF.Sin(time * 1.3f + p.Flip) * 25 - p.VX) * MathF.Min(1, dt * 1.5f);
                p.X += p.VX * dt;
                p.Y += (p.VY + 10 * MathF.Sin(time * 2 + p.Flip)) * dt;
                p.Rot += p.Spin * dt;
                p.Flip += 3 * dt;
                if (p.Y > H + 10 || p.Life > 30)
                {
                    fallingPetals[i] = fallingPetals[fallingPetals.Count - 1];
                    fallingPetals.RemoveAt(fallingPetals.Count - 1);
                    continue;
                }
                fallingPetals[i] = p;
                i++;
            }
        }

        private void DrawFallingPetals(Batch batch)
        {
            foreach (var p in fallingPetals)
            {
                var c = Palette.Petals[p.Tint];
                batch.Spr(Sprite.Petal, p.X, p.Y, 4 * MathF.Max(0.22f, MathF.Abs(MathF.Cos(p.Flip))), 4.6f, p.Rot, c.R, c.G, c.B, 0.95f);
            }
        }

        // ---- Summer: sunflowers ----

        private void DrawSunflowers(Batch batch)
        {
            float al = treeAlpha;
            if (al <= 0.003f) return;
            float h = treeH, w = treeW;
            // Grass.
            for (int k = 0; k < 40; k++)
            {
                float u = k / 39f * 2 - 1;
                float x = treeX + u * w * 0.85f;
                float len = 8 + (k * 37) % 11;
                float lean = MathF.Sin(time * 1.1f + k) * 1.5f + (k % 3 - 1) * 2;
                batch.Line(x, G, x + lean, G - len, 1.6f, 0.24f, 0.56f, 0.20f, al);
            }
            var spots = new[] { (-0.38f, 0.78f), (0.02f, 1.0f), (0.36f, 0.62f) };
            for (int i = 0; i < spots.Length; i++)
            {
                var (dx, tall) = spots[i];
                float sway = MathF.Sin(time * 0.8f + i * 1.7f) * 0.05f;
                float baseX = treeX + dx * w;
                float len = h * tall;
                float hx = baseX + MathF.Sin(sway) * len, hy = G - MathF.Cos(sway) * len;
                batch.Line(baseX, G, hx, hy, 4, 0.22f, 0.50f, 0.18f, al);
                float[] ats = { 0.35f, 0.62f };
                for (int k = 0; k < 2; k++)
                {
                    float side = (k + i) % 2 == 0 ? 1 : -1;
                    float lx = baseX + (hx - baseX) * ats[k], ly = G + (hy - G) * ats[k];
                    batch.Spr(Sprite.Leaf, lx + side * 11, ly - 4, 9, 13, side * 1.0f + sway, 0.30f, 0.62f, 0.22f, al);
                }
                float r = h * (0.085f + 0.02f * tall);
                for (int ring = 0; ring < 2; ring++)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k / 16f * 6.283f + ring * 0.2f + sway;
                        float rr = r * (ring == 0 ? 1.05f : 0.9f);
                        float shade = ring == 0 ? 0.88f : 1;
                        batch.Spr(Sprite.Petal, hx + MathF.Sin(a) * rr, hy - MathF.Cos(a) * rr, r * 0.26f, r * 0.55f, a, 1.0f * shade, 0.78f * shade, 0.14f * shade, al);
                    }
                }
                batch.Spr(Sprite.BallBase, hx, hy, r * 0.62f, r * 0.62f, 0, 0.42f, 0.25f, 0.09f, al);
                batch.Spr(Sprite.BallTex, hx, hy, r * 0.58f, r * 0.58f, i, 0.2f, 0.1f, 0.03f, al * 0.9f);
            }
        }

        // ---- Autumn: pumpkins with a candle inside ----

        private void DrawPumpkin(Batch batch, float x, float r, bool face, float al)
        {
            float y = G - r * 0.82f;
            var ribs = new[] { (-0.55f, 0.62f, 0.78f), (0.55f, 0.62f, 0.78f), (-0.28f, 0.7f, 0.9f), (0.28f, 0.7f, 0.9f), (0f, 0.66f, 1.0f) };
            foreach (var (dx, wk, shade) in ribs)
                batch.Spr(Sprite.BallBase, x + dx * r, y, r * wk, r * 0.86f, 0, 1.0f * shade, 0.52f * shade, 0.10f * shade, al);
            batch.Rect(x - r * 0.08f, y - r * 1.06f, r * 0.16f, r * 0.26f, 0.36f, 0.30f, 0.12f, al);
            batch.Line(x + r * 0.05f, y - r * 0.95f, x + r * 0.35f, y - r * 1.12f, 1.8f, 0.30f, 0.45f, 0.16f, al);
            if (!face) return;
            float flicker = 0.78f + 0.22f * MathF.Sin(time * 9.0f) * MathF.Sin(time * 5.3f + 1);
            float lr = 1.0f, lg = 0.82f * flicker + 0.1f, lb = 0.28f * flicker;
            void Tri(float ax, float ay, float bx, float by, float cx, float cy)
            {
                batch.Tri(batch.Flat(x + ax * r, y + ay * r, lr, lg, lb, al), batch.Flat(x + bx * r, y + by * r, lr, lg, lb, al),
                          batch.Flat(x + cx * r, y + cy * r, lr, lg, lb, al));
            }
            Tri(-0.42f, -0.08f, -0.16f, -0.08f, -0.29f, -0.34f);
            Tri(0.16f, -0.08f, 0.42f, -0.08f, 0.29f, -0.34f);
            Tri(-0.07f, 0.08f, 0.07f, 0.08f, 0, -0.06f);
            var mouth = new[] { (-0.5f, 0.2f), (-0.32f, 0.36f), (-0.2f, 0.26f), (-0.08f, 0.4f), (0.08f, 0.28f), (0.2f, 0.4f), (0.34f, 0.27f), (0.5f, 0.2f) };
            for (int k = 0; k < mouth.Length - 1; k++)
            {
                var a = mouth[k];
                var b = mouth[k + 1];
                Tri(a.Item1, a.Item2, b.Item1, b.Item2, (a.Item1 + b.Item1) / 2, 0.18f + (k % 2 == 0 ? 0.02f : 0));
            }
            float glow = evening ? 0.6f : 0.35f;
            batch.Spr(Sprite.Glow, x, y + r * 0.05f, r * 2.2f, r * 1.8f, 0, 1, 0.6f, 0.2f, al * glow * flicker, true);
        }

        private void DrawPumpkins(Batch batch)
        {
            float al = treeAlpha;
            if (al <= 0.003f) return;
            float h = treeH, w = treeW;
            for (int k = 0; k < 14; k++)
            {
                float u = k / 13f * 2 - 1;
                var c = Palette.Leaves[k % Palette.Leaves.Length];
                batch.Spr(k % 2 == 0 ? Sprite.Maple : Sprite.Leaf, treeX + u * w * 0.85f, G - 4, 7, 8, k * 1.9f, c.R, c.G, c.B, al);
            }
            DrawPumpkin(batch, treeX + w * 0.22f, h * 0.12f, false, al);
            DrawPumpkin(batch, treeX - w * 0.2f, h * 0.19f, true, al);
        }

        // ---- Butterflies (spring and summer days) ----

        private struct Butterfly { public float X, BaseY, Y, VX, Phase, Size; public int Tint; }
        private static readonly (float R, float G, float B)[] WingColors = { (1.0f, 0.55f, 0.12f), (0.36f, 0.62f, 1.0f), (1.0f, 0.88f, 0.30f), (1.0f, 1.0f, 1.0f), (0.85f, 0.45f, 0.95f) };
        private readonly List<Butterfly> butterflies = new List<Butterfly>();
        private float nextButterfly = 2;

        private void StepButterflies(float dt, float? cursorX, float cursorY)
        {
            bool welcome = creatures && (Season == 1 || Season == 2) && !evening;
            if (welcome && time > nextButterfly && butterflies.Count < 3)
            {
                bool fromLeft = rng.Unit() < 0.5f;
                butterflies.Add(new Butterfly
                {
                    X = fromLeft ? -20 : W + 20, BaseY = rng.Range(H * 0.25f, H * 0.75f), VX = (fromLeft ? 1 : -1) * rng.Range(45, 75),
                    Phase = rng.Range(0, 6.28f), Tint = rng.Int(WingColors.Length), Size = rng.Range(17, 24),
                });
                nextButterfly = time + rng.Range(6, 15);
            }
            int i = 0;
            while (i < butterflies.Count)
            {
                var b = butterflies[i];
                b.X += b.VX * dt;
                b.Y = b.BaseY + 34 * MathF.Sin(time * 1.2f + b.Phase) + 10 * MathF.Sin(time * 3.3f + b.Phase * 2);
                // Shy: it flutters up and away from the cursor.
                if (cursorX is float cx)
                {
                    float dx = b.X - cx, dy = b.Y - cursorY;
                    if (dx * dx + dy * dy < 120 * 120)
                    {
                        b.BaseY -= 140 * dt;
                        b.X += (dx > 0 ? 1 : -1) * 60 * dt;
                    }
                }
                if (b.X < -40 || b.X > W + 40 || b.BaseY < -60)
                {
                    butterflies[i] = butterflies[butterflies.Count - 1];
                    butterflies.RemoveAt(butterflies.Count - 1);
                    continue;
                }
                butterflies[i] = b;
                i++;
            }
        }

        private void DrawButterflies(Batch batch)
        {
            foreach (var b in butterflies)
            {
                var c = WingColors[b.Tint];
                float flap = MathF.Abs(MathF.Sin(time * 13 + b.Phase));
                float hw = b.Size * (0.2f + 0.8f * flap) * 0.5f;
                float tilt = b.VX > 0 ? 0.12f : -0.12f;
                batch.Spr(Sprite.Wing, b.X + hw, b.Y, hw, b.Size * 0.5f, tilt, c.R, c.G, c.B, 1);
                batch.Spr(Sprite.Wing, b.X - hw, b.Y, hw, b.Size * 0.5f, tilt, c.R, c.G, c.B, 1, false, true);
                batch.Spr(Sprite.Coal, b.X, b.Y + 0.5f, b.Size * 0.07f, b.Size * 0.32f);
                batch.Line(b.X, b.Y - b.Size * 0.25f, b.X - 2.5f, b.Y - b.Size * 0.55f, 0.8f, 0.15f, 0.12f, 0.1f, 0.9f);
                batch.Line(b.X, b.Y - b.Size * 0.25f, b.X + 2.5f, b.Y - b.Size * 0.55f, 0.8f, 0.15f, 0.12f, 0.1f, 0.9f);
            }
        }

        // ---- Fireflies (summer evenings) ----

        private struct Firefly { public float X, Y, VX, VY, Phase, Speed; }
        private readonly List<Firefly> fireflies = new List<Firefly>();

        private void StepFireflies(float dt, float? cursorX, float cursorY)
        {
            int wanted = creatures && Season == 2 && evening ? Math.Max(12, (int)(W * H / 45000)) : 0;
            if (fireflies.Count < wanted && rng.Unit() < dt * 3)
                fireflies.Add(new Firefly { X = rng.Range(0, W), Y = rng.Range(H * 0.35f, H * 0.95f), Phase = rng.Range(0, 6.28f), Speed = rng.Range(1.2f, 2.6f) });
            else if (fireflies.Count > wanted && rng.Unit() < dt * 2)
                fireflies.RemoveAt(fireflies.Count - 1);
            for (int i = 0; i < fireflies.Count; i++)
            {
                var f = fireflies[i];
                f.VX += MathF.Sin(time * 0.9f + f.Phase * 3) * 40 * dt;
                f.VY += MathF.Cos(time * 0.7f + f.Phase * 2) * 32 * dt;
                // Curious: they drift towards the cursor and circle it.
                if (cursorX is float cx)
                {
                    float dx = cx - f.X, dy = cursorY - f.Y;
                    float d = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
                    if (d < 280)
                    {
                        float pull = d > 50 ? 26 : -20;
                        f.VX += dx / d * pull * dt;
                        f.VY += dy / d * pull * dt;
                    }
                }
                f.VX *= MathF.Max(0, 1 - dt * 0.8f);
                f.VY *= MathF.Max(0, 1 - dt * 0.8f);
                f.X += f.VX * dt;
                f.Y += f.VY * dt;
                if (f.X < 0) f.VX += 20 * dt; else if (f.X > W) f.VX -= 20 * dt;
                if (f.Y < H * 0.2f) f.VY += 20 * dt; else if (f.Y > H - 10) f.VY -= 20 * dt;
                fireflies[i] = f;
            }
        }

        private void DrawFireflies(Batch batch)
        {
            foreach (var f in fireflies)
            {
                // They fly out in the dark, behind the windows.
                bool behind = false;
                foreach (var o in occluders) if (o.Contains(f.X, f.Y)) { behind = true; break; }
                if (behind) continue;
                float s = MathF.Max(0, MathF.Sin(time * f.Speed + f.Phase));
                float glow = 0.12f + 0.88f * s * s;
                batch.Spr(Sprite.Glow, f.X, f.Y, 26, 26, 0, 0.72f, 1.0f, 0.30f, glow, true);
                batch.Spr(Sprite.Glow, f.X, f.Y, 8, 8, 0, 0.9f, 1.0f, 0.5f, glow, true);
                batch.Spr(Sprite.Dot, f.X, f.Y, 2.4f, 2.4f, 0, 1, 1, 0.75f, 0.3f + 0.7f * glow);
            }
        }
    }
}
