using System;
using System.Collections.Generic;

namespace Snowdrift
{
    public struct FRect
    {
        public float X0, Y0, X1, Y1;
        public FRect(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public bool Contains(float x, float y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
    }

    public record struct SimConfig
    {
        public int Intensity { get; set; }
        public int Wind { get; set; }
        public int Drifts { get; set; }
        public int Tool { get; set; }
        public int Garland { get; set; }
        public bool Depth { get; set; }
        public bool LowPower { get; set; }
        public int FlakeSize { get; set; }
        public bool Melt { get; set; }
        public bool Tree { get; set; }
        public bool Frost { get; set; }
        public bool Sparkle { get; set; }
        /// <summary>0 winter, 1 spring, 2 summer, 3 autumn.</summary>
        public int Season { get; set; }
        /// <summary>Evening or night (fireflies).</summary>
        public bool Evening { get; set; }
        /// <summary>Butterflies and fireflies.</summary>
        public bool Creatures { get; set; }

        public static SimConfig Default => new SimConfig
        {
            Intensity = 1, Wind = 1, Drifts = 2, Tool = 0, Garland = 2, Depth = true,
            LowPower = false, FlakeSize = 1, Melt = false, Tree = true, Frost = false, Sparkle = true, Creatures = true,
        };
    }

    public struct Flake
    {
        public float X, Y, VX;
        public float Size, Alpha;
        public byte Layer;   // 0 far, 1 middle, 2 near
        public byte Kind;    // 0 soft dot, 1…3 crystal, 4 petal, 5 leaf, 6 maple leaf, 7 dandelion fluff
        public float Phase, SwayAmp, SwayFreq;
        public float Rot, Spin;
        public float Fall;
        public float Vol;
        public byte Tint;
        /// <summary>Tumbling: the item turns edge-on and back.</summary>
        public float Flip, FlipSpeed;
    }

    public struct Debris
    {
        public float X, Y, VX, VY;
        public float Size, Vol;
        public bool CanLand;
        /// <summary>0 snow, 1 petal, 2 leaf.</summary>
        public byte Kind, Tint;
        public float Rot, Spin;
    }

    /// <summary>Colours of the seasons.</summary>
    public static class Palette
    {
        public static readonly (float R, float G, float B)[] Petals = { (1.0f, 0.80f, 0.87f), (1.0f, 0.70f, 0.81f), (0.99f, 0.89f, 0.93f), (0.98f, 0.62f, 0.76f) };
        public static readonly (float R, float G, float B)[] Leaves =
        {
            (0.96f, 0.56f, 0.12f), (0.88f, 0.30f, 0.10f), (0.98f, 0.76f, 0.18f), (0.72f, 0.20f, 0.08f), (0.84f, 0.46f, 0.14f), (0.62f, 0.40f, 0.12f),
        };
    }

    /// <summary>A window as the snow sees it, in this screen's points.</summary>
    public struct WinInfo
    {
        public long Id;
        public FRect Rect;
        public WinInfo(long id, FRect rect) { Id = id; Rect = rect; }
    }

    /// <summary>Something snow can lie on: a window's top edge, the taskbar, or the bottom of the screen.</summary>
    public sealed class Surface
    {
        public enum SKind { Window, Dock, Floor }
        public readonly long Id;
        public readonly SKind Kind;
        public float X0;
        public float Y;
        public float[] H;
        public bool[] Vis;
        public float Cap;
        public bool OnScreen = true;
        public float SpillL, SpillR;

        public Surface(long id, SKind kind, float x0, float y, int n)
        {
            Id = id; Kind = kind; X0 = x0; Y = y;
            H = new float[Math.Max(0, n)];
            Vis = new bool[Math.Max(0, n)];
            Array.Fill(Vis, true);
        }

        public float X1 => X0 + H.Length * Sim.ColW;
    }

    public sealed class Ball
    {
        public float X, Y, VX, VY;
        private float area;
        public float R { get; private set; }
        public float Angle;
        public long? Support;
        public float RelX;
        public bool Dormant, Grabbed, Hidden;
        public uint Style;
        public Ball Below;
        public Ball Above;

        public Ball(float x, float y, float area, uint style)
        {
            X = x; Y = y; Area = area; Style = style;
        }

        public float Area
        {
            get => area;
            set { area = value; R = MathF.Sqrt(MathF.Max(0, value) / MathF.PI); }
        }

        public Ball Root
        {
            get { var b = this; while (b.Below != null) b = b.Below; return b; }
        }

        /// <summary>This ball and everything stacked on it.</summary>
        public List<Ball> Chain
        {
            get
            {
                var list = new List<Ball> { this };
                var b = this;
                while (b.Above != null) { list.Add(b.Above); b = b.Above; }
                return list;
            }
        }
    }

    /// <summary>The snow on one screen. Coordinates are points, origin top-left, y down.</summary>
    public sealed class Sim
    {
        public const float ColW = 2;
        public const float MaxBallRadius = 34;
        private const long FloorID = -1;
        private const long DockID = -2;
        private const float EdgeInset = 12;

        public float W { get; private set; }
        public float H { get; private set; }
        public float TopInset { get; private set; }

        private SimConfig config = SimConfig.Default;
        public SimConfig Config
        {
            get => config;
            set
            {
                var old = config;
                config = value;
                if (value.Season != old.Season)
                {
                    // A new season: what lies around blows away, snowmen melt, the new season starts falling.
                    ClearSnow(true, DebrisKind(old.Season));
                    ClearBalls();
                    NeedsWindowRefresh = true;
                }
                if (value.Drifts != old.Drifts)
                {
                    if (value.Drifts == 0) ClearSnow(false);
                    NeedsWindowRefresh = true;
                }
            }
        }

        public bool NeedsWindowRefresh = true;

        private Rng rng = Rng.Create();
        public float Time { get; private set; }
        private float wind;

        private readonly List<Flake> flakes = new List<Flake>();
        private readonly List<Debris> debris = new List<Debris>();
        private readonly Dictionary<long, Surface> surfaces = new Dictionary<long, Surface>();
        private List<Surface> active = new List<Surface>();
        private List<FRect> occluders = new List<FRect>();
        public readonly List<Ball> Balls = new List<Ball>();

        // Cursor.
        private float cursorX = -1000, cursorY = -1000, cursorVX, cursorVY;
        private bool cursorValid;
        private float carveAcc, pendingBall;
        private Ball activeBall;
        public Ball Hover { get; private set; }
        public Ball GrabbedBall { get; private set; }
        private float grabDX, grabDY;

        private int landed;
        public readonly Festive Festive;

        private struct Bulb { public float X, Y, Phase, Speed; public int Color; }
        private readonly List<Bulb> bulbs = new List<Bulb>();
        private readonly List<(float X, float Y)> wire = new List<(float, float)>();

        /// <param name="ground">Where the floor of the usable screen is (top of the taskbar), for the tree.</param>
        public Sim(float width, float height, float topInset, float ground)
        {
            W = width;
            H = height;
            TopInset = topInset;
            Festive = new Festive(width, height, ground);
            BuildGarland();
            var floor = new Surface(FloorID, Surface.SKind.Floor, 0, H, (int)(W / ColW));
            surfaces[floor.Id] = floor;
            active = new List<Surface> { floor };
        }

        // ---- Settings-derived numbers ----

        public int Season => Math.Clamp(config.Season, 0, 3);
        private bool Winter => Season == 0;

        /// <summary>What the mitten and the wind blow around: snow, petals or leaves.</summary>
        private static byte DebrisKind(int season) => season == 1 ? (byte)1 : (season == 3 ? (byte)2 : (byte)0);

        /// <summary>Snow piles high; petals lie in a thin layer, leaves in heaps; summer fluff doesn't settle.</summary>
        private float DriftCap => new float[] { 0, 13, 22, 36 }[config.Drifts] * new float[] { 1, 0.4f, 0, 0.62f }[Season];

        private int TargetCount
        {
            get
            {
                float b = new float[] { 150, 430, 1150 }[config.Intensity];
                float area = (W * H) / (1512f * 982f);
                float perSeason = new float[] { 1, 0.34f, 0.13f, 0.15f }[Season];
                return Math.Max(4, (int)(b * area * perSeason * (config.LowPower ? 0.6f : 1f)));
            }
        }

        private float SpeedFactor => new float[] { 0.85f, 1.0f, 1.45f }[config.Intensity];
        private float DepositGain => new float[] { 1.35f, 1.0f, 0.8f }[config.Intensity];

        // ---- Windows ----

        /// <summary>Updates the surfaces from the window layout (front to back, in this screen's points).</summary>
        public void ApplyWindows(List<WinInfo> windows, FRect? dockRect, float elapsed, Func<HashSet<long>, HashSet<long>> existing)
        {
            NeedsWindowRefresh = false;
            // Real time since the last update; a big value means "don't shake snow off moved windows".
            float pollDt = MathF.Max(1f / 120, elapsed);

            var wins = new List<WinInfo>(windows.Count);
            foreach (var w in windows)
            {
                var r = w.Rect;
                if (r.X1 <= 0 || r.X0 >= W || r.Y1 <= 0 || r.Y0 >= H) continue;
                wins.Add(w);
            }
            FRect? dock = null;
            if (dockRect is FRect d && d.X1 > 0 && d.X0 < W && d.Y1 > 0 && d.Y0 < H) dock = d;
            occluders = new List<FRect>(wins.Count + 1);
            foreach (var w in wins) occluders.Add(w.Rect);
            if (dock is FRect dk0) occluders.Add(dk0);

            float cap = DriftCap;
            var seen = new HashSet<long>();

            for (int zi = 0; zi < wins.Count; zi++)
            {
                var w = wins[zi];
                float y = w.Rect.Y0;
                float x0 = w.Rect.X0 + EdgeInset, x1 = w.Rect.X1 - EdgeInset;
                if (!(y > TopInset + 3 && y < H - 6 && x1 - x0 >= 40)) continue;
                var s = Upsert(w.Id, Surface.SKind.Window, x0, y, (int)((x1 - x0) / ColW), pollDt);
                seen.Add(w.Id);
                s.Cap = MathF.Max(0, MathF.Min(cap, y - TopInset - 2));
                ResetVisibility(s);
                // Hide the parts of the edge that sit behind a window in front, or behind the taskbar.
                for (int j = 0; j < zi; j++)
                {
                    var f = wins[j].Rect;
                    if (f.Y0 <= y - 2 && f.Y1 > y - 2) Hide(s, f.X0 - 1, f.X1 + 1);
                }
                if (dock is FRect dk && dk.Y0 <= y - 2 && dk.Y1 > y - 2) Hide(s, dk.X0, dk.X1);
            }

            // Windows that vanished: closed ones drop their snow, hidden ones keep it for later.
            var missing = new HashSet<long>();
            var dormant = new HashSet<long>();
            foreach (var kv in surfaces)
            {
                if (kv.Value.Kind != Surface.SKind.Window) continue;
                if (seen.Contains(kv.Key)) continue;
                if (kv.Value.OnScreen) missing.Add(kv.Key); else dormant.Add(kv.Key);
            }
            if (missing.Count > 0)
            {
                var all = new HashSet<long>(missing);
                all.UnionWith(dormant);
                var alive = existing(all);
                foreach (long id in missing)
                {
                    if (!surfaces.TryGetValue(id, out var s)) continue;
                    if (alive.Contains(id))
                    {
                        s.OnScreen = false;
                        foreach (var b in Balls) if (b.Support == id) b.Dormant = true;
                    }
                    else
                    {
                        Avalanche(s, 0, s.H.Length);
                        surfaces.Remove(id);
                        foreach (var b in Balls) if (b.Support == id) b.Support = null;
                    }
                }
                foreach (long id in dormant)
                {
                    if (alive.Contains(id)) continue;
                    surfaces.Remove(id);
                    RemoveBalls(b => b.Root.Support == id && b.Root.Dormant);
                }
            }

            // Taskbar.
            if (dock is FRect dock2 && dock2.X1 - dock2.X0 > 60 && dock2.Y0 > TopInset + 20)
            {
                float x0 = dock2.X0 + 10, x1 = dock2.X1 - 10;
                var s = Upsert(DockID, Surface.SKind.Dock, x0, dock2.Y0 + 1, (int)((x1 - x0) / ColW), pollDt);
                s.Cap = MathF.Min(cap, 18);
                ResetVisibility(s);
            }
            else if (surfaces.TryGetValue(DockID, out var ds) && ds.OnScreen)
            {
                // The taskbar hid (full-screen app, auto-hide): keep its snow for when it comes back.
                ds.OnScreen = false;
                foreach (var b in Balls) if (b.Root.Support == DockID) b.Root.Dormant = true;
            }

            // Floor.
            if (surfaces.TryGetValue(FloorID, out var floor))
            {
                floor.Cap = cap * 1.3f;
                ResetVisibility(floor);
                if (dock is FRect dk3 && dk3.Y1 >= H - 2) Hide(floor, dk3.X0, dk3.X1);
            }

            active = new List<Surface>();
            foreach (var s in surfaces.Values) if (s.OnScreen) active.Add(s);
            active.Sort((a, b) => a.Y.CompareTo(b.Y));
        }

        private void ResetVisibility(Surface s)
        {
            int n = s.H.Length;
            if (s.Vis.Length != n) s.Vis = new bool[n];
            for (int i = 0; i < n; i++)
            {
                float x = s.X0 + (i + 0.5f) * ColW;
                s.Vis[i] = x >= 0 && x < W;
            }
        }

        private static void Hide(Surface s, float xa, float xb)
        {
            int n = s.H.Length;
            int a = Math.Max(0, (int)MathF.Floor((xa - s.X0) / ColW));
            int b = Math.Min(n, (int)MathF.Ceiling((xb - s.X0) / ColW));
            for (int i = a; i < b; i++) s.Vis[i] = false;
        }

        private Surface Upsert(long id, Surface.SKind kind, float x0, float y, int n, float pollDt)
        {
            if (!surfaces.TryGetValue(id, out var s))
            {
                s = new Surface(id, kind, x0, y, n);
                surfaces[id] = s;
                return s;
            }
            bool wasOn = s.OnScreen;
            bool resized = false;
            if (s.H.Length != n)
            {
                resized = true;
                int oldN = s.H.Length;
                bool anchorRight = MathF.Abs((x0 + n * ColW) - s.X1) < 2.5f && MathF.Abs(x0 - s.X0) > 1;
                var nh = new float[n];
                int keep = Math.Min(n, oldN);
                if (anchorRight)
                {
                    for (int i = 0; i < keep; i++) nh[n - 1 - i] = s.H[oldN - 1 - i];
                    if (wasOn && oldN > n) Avalanche(s, 0, oldN - n);
                }
                else
                {
                    for (int i = 0; i < keep; i++) nh[i] = s.H[i];
                    if (wasOn && oldN > n) Avalanche(s, n, oldN);
                }
                s.H = nh;
                s.Vis = new bool[n];
                Array.Fill(s.Vis, true);
            }
            float dx = x0 - s.X0, dy = y - s.Y;
            if (wasOn && !resized && (dx != 0 || dy != 0))
            {
                // A window yanked fast leaves part of its snow hanging in the air.
                float speed = MathF.Sqrt(dx * dx + dy * dy) / pollDt;
                if (speed > 1500)
                {
                    float frac = MathF.Min(0.6f, 0.1f + (speed - 1500) / 5000);
                    float acc = 0;
                    for (int i = 0; i < s.H.Length; i++)
                    {
                        if (!s.Vis[i] || s.H[i] <= 0.5f) continue;
                        float take = s.H[i] * frac;
                        s.H[i] -= take;
                        acc += take * ColW;
                        if (acc >= 8)
                        {
                            Emit(s.X0 + i * ColW, s.Y - s.H[i] - 1, rng.Range(-20, 20), rng.Range(-30, 20), acc);
                            acc = 0;
                        }
                    }
                }
                foreach (var b in Balls)
                {
                    if (b.Support == id && b.Below == null && !b.Grabbed) { b.X += dx; b.Y += dy; }
                }
            }
            s.X0 = x0;
            s.Y = y;
            s.OnScreen = true;
            if (!wasOn)
            {
                foreach (var b in Balls)
                {
                    if (b.Support == id && b.Dormant)
                    {
                        b.Dormant = false;
                        b.X = x0 + b.RelX;
                        b.Y = y - b.R - HeightAt(s, b.X);
                    }
                }
            }
            return s;
        }

        private static float HeightAt(Surface s, float x)
        {
            int c = (int)((x - s.X0) / ColW);
            return c >= 0 && c < s.H.Length ? s.H[c] : 0;
        }

        /// <summary>Turns the snow of some columns into falling clumps.</summary>
        private void Avalanche(Surface s, int from, int to)
        {
            if (!s.OnScreen) return;
            float acc = 0;
            for (int i = from; i < to; i++)
            {
                if (i >= s.Vis.Length || !s.Vis[i] || s.H[i] <= 0.3f) continue;
                acc += s.H[i] * ColW;
                if (acc >= 9)
                {
                    Emit(s.X0 + i * ColW, s.Y - s.H[i] * rng.Unit(), rng.Range(-25, 25), rng.Range(-20, 40), acc);
                    acc = 0;
                }
            }
        }

        // ---- Public actions ----

        public int PeekLanded => landed;

        /// <summary>Snowflakes that settled since the last call.</summary>
        public int TakeLanded()
        {
            int n = landed;
            landed = 0;
            return n;
        }

        /// <summary>A puff of snow breathed out at (x, y); the flakes drift down and settle like any other.</summary>
        public void Puff(float x, float y, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var f = MakeFlake(false);
                if (f.Layer == 0)
                {
                    f.Layer = 1;
                    f.Size = MathF.Max(f.Size, 1.5f);
                    f.Alpha = MathF.Max(f.Alpha, 0.8f);
                    f.Vol = 12;
                }
                f.X = x + rng.Range(-70, 70);
                f.Y = y + rng.Range(-4, 14);
                f.VX = rng.Range(-110, 110);
                flakes.Add(f);
            }
        }

        /// <summary>Thaw: drifts sink, snowballs shrink and finally melt away.</summary>
        private void Melt(float dt)
        {
            foreach (var s in active)
            {
                var h = s.H;
                for (int i = 0; i < h.Length; i++)
                    if (h[i] > 0) h[i] = MathF.Max(0, h[i] - dt * (0.12f + h[i] * 0.012f));
            }
            var gone = new List<Ball>();
            foreach (var b in Balls)
            {
                if (b.Grabbed || b.Dormant) continue;
                b.Area *= MathF.Max(0, 1 - dt * 0.012f);
                if (b.R < 4.5f) gone.Add(b);
            }
            foreach (var b in gone)
            {
                if (b.Above != null) { b.Above.Below = null; b.Above = null; }
                if (b.Below != null) { b.Below.Above = null; b.Below = null; }
                RemoveBalls(x => x == b);
            }
        }

        /// <summary>The shovel: all snow puffs up and is gone.</summary>
        public void ClearSnow(bool puff = true, byte? kind = null)
        {
            foreach (var s in surfaces.Values)
            {
                if (puff && s.OnScreen)
                {
                    float acc = 0;
                    for (int i = 0; i < s.H.Length; i++)
                    {
                        if (!s.Vis[i] || s.H[i] <= 0.3f) continue;
                        acc += s.H[i] * ColW;
                        if (acc >= 14)
                        {
                            Emit(s.X0 + i * ColW, s.Y - s.H[i] * rng.Unit(), rng.Range(-90, 90), rng.Range(-190, -40), 8, false, kind);
                            acc = 0;
                        }
                    }
                }
                Array.Clear(s.H);
            }
        }

        public void ClearBalls()
        {
            foreach (var b in Balls.ToArray())
                if (b.Below == null && !b.Dormant) Burst(b, false);
            Balls.Clear();
            Hover = null;
            GrabbedBall = null;
            activeBall = null;
        }

        public bool HasBalls => Balls.Count > 0;

        private void RemoveBalls(Predicate<Ball> match)
        {
            foreach (var b in Balls)
            {
                if (!match(b)) continue;
                if (b == activeBall) activeBall = null;
                if (b == Hover) Hover = null;
                if (b == GrabbedBall) GrabbedBall = null;
            }
            Balls.RemoveAll(match);
        }

        public void GrabHover()
        {
            var b = Hover;
            if (b == null || GrabbedBall != null) return;
            if (b.Below != null)
            {
                b.Below.Above = null;
                b.Below = null;
            }
            b.Grabbed = true;
            b.Support = null;
            b.VX = 0;
            b.VY = 0;
            grabDX = b.X - cursorX;
            grabDY = b.Y - cursorY;
            GrabbedBall = b;
        }

        public void ReleaseGrab()
        {
            var b = GrabbedBall;
            if (b == null) return;
            b.Grabbed = false;
            GrabbedBall = null;
            float speed = MathF.Sqrt(b.VX * b.VX + b.VY * b.VY);
            if (speed > 1500)
            {
                b.VX *= 1500 / speed;
                b.VY *= 1500 / speed;
            }
            TryStack(b);
        }

        // ---- Step ----

        public void Step(float dt, float cx, float cy, bool cursorInside)
        {
            Time += dt;
            float gust = (25 * MathF.Sin(Time * 0.37f) + 15 * MathF.Sin(Time * 0.91f + 2)) * new float[] { 0.6f, 1.0f, 2.2f }[config.Intensity];
            wind = new float[] { -170, -70, 0, 70, 170 }[config.Wind + 2] * new float[] { 0.8f, 1.0f, 1.5f }[config.Intensity] + gust;

            // Cursor motion.
            bool hadCursor = cursorValid;
            float px = cursorX, py = cursorY;
            cursorValid = cursorInside;
            cursorX = cx;
            cursorY = cy;
            if (hadCursor && cursorInside)
            {
                float k = MathF.Min(1, dt * 18);
                cursorVX += ((cx - px) / dt - cursorVX) * k;
                cursorVY += ((cy - py) / dt - cursorVY) * k;
            }
            else
            {
                cursorVX = 0;
                cursorVY = 0;
            }
            float cursorSpeed = MathF.Sqrt(cursorVX * cursorVX + cursorVY * cursorVY);

            if (hadCursor && cursorInside && config.Tool != 2 && GrabbedBall == null)
            {
                float dist = MathF.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
                if (dist > 0.2f)
                {
                    int steps = Math.Min(24, Math.Max(1, (int)(dist / 6) + 1));
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = (float)i / steps;
                        Carve(px + (cx - px) * t, py + (cy - py) * t, 13);
                    }
                }
            }
            if (cursorSpeed < 5) pendingBall *= MathF.Max(0, 1 - dt * 1.5f);

            StepFlakes(dt, cursorSpeed);
            StepDebris(dt);
            Relax();
            StepBalls(dt, cursorSpeed);
            Festive.Step(dt, Season, config.Tree, config.Evening, config.Creatures, cursorInside ? cx : (float?)null, cy, occluders);
            if (config.Melt) Melt(dt);

            // What is under the cursor.
            Hover = null;
            if (cursorInside && GrabbedBall == null)
            {
                foreach (var b in Balls)
                {
                    var root = b.Root;
                    if (root.Hidden || root.Dormant) continue;
                    float dx = b.X - cx, dy = b.Y - cy;
                    if (dx * dx + dy * dy < (b.R + 2) * (b.R + 2)) Hover = b;
                }
            }
        }

        private Flake MakeFlake(bool anywhere)
        {
            float pick = rng.Unit();
            byte layer = pick < 0.30f ? (byte)0 : (pick < 0.76f ? (byte)1 : (byte)2);
            float scale = new float[] { 0.72f, 1.0f, 1.4f }[config.FlakeSize];
            var f = new Flake
            {
                X = rng.Range(-40, W + 40),
                Y = anywhere ? rng.Range(-20, H) : -rng.Range(6, 60),
                VX = wind * 0.5f,
                Layer = layer,
                Phase = rng.Range(0, 6.28f), SwayAmp = rng.Range(6, 22), SwayFreq = rng.Range(0.6f, 1.8f),
                Rot = rng.Range(0, 6.28f), Spin = rng.Range(-1.2f, 1.2f),
            };
            // Per layer: size, alpha, fall speed ranges and volume — far, middle, near.
            void Pick((float, float)[] sizes, (float, float)[] alphas, (float, float)[] falls, float[] vols)
            {
                int l = layer;
                f.Size = rng.Range(sizes[l].Item1, sizes[l].Item2) * scale;
                f.Alpha = rng.Range(alphas[l].Item1, alphas[l].Item2);
                f.Fall = rng.Range(falls[l].Item1, falls[l].Item2);
                f.Vol = vols[l];
            }
            switch (Season)
            {
                case 1:
                    Pick(new[] { (2.0f, 2.8f), (3.2f, 4.6f), (4.8f, 6.6f) }, new[] { (0.45f, 0.65f), (0.85f, 0.95f), (0.95f, 1f) },
                         new[] { (16f, 24f), (24f, 38f), (34f, 50f) }, new float[] { 0, 5, 6 });
                    f.Kind = 4;
                    f.Tint = (byte)rng.Int(Palette.Petals.Length);
                    f.SwayAmp = rng.Range(14, 38);
                    f.SwayFreq = rng.Range(0.5f, 1.3f);
                    f.Spin = rng.Range(-2.4f, 2.4f);
                    f.FlipSpeed = rng.Range(1.5f, 4);
                    break;
                case 2:
                    Pick(new[] { (3.4f, 4.6f), (5.4f, 7.2f), (8.0f, 10.5f) }, new[] { (0.45f, 0.6f), (0.75f, 0.9f), (0.9f, 1f) },
                         new[] { (8f, 14f), (12f, 20f), (16f, 26f) }, new float[] { 0, 0, 0 });
                    f.Kind = 7;
                    f.SwayAmp = rng.Range(18, 44);
                    f.SwayFreq = rng.Range(0.25f, 0.7f);
                    f.Spin = rng.Range(-0.25f, 0.25f);
                    f.Rot = rng.Range(-0.3f, 0.3f);
                    break;
                case 3:
                    Pick(new[] { (3.6f, 5.0f), (5.6f, 8.0f), (8.6f, 11.5f) }, new[] { (0.5f, 0.7f), (0.9f, 1f), (1f, 1f) },
                         new[] { (22f, 32f), (32f, 50f), (46f, 68f) }, new float[] { 0, 22, 26 });
                    f.Kind = rng.Unit() < 0.5f ? (byte)5 : (byte)6;
                    f.Tint = (byte)rng.Int(Palette.Leaves.Length);
                    f.SwayAmp = rng.Range(30, 70);
                    f.SwayFreq = rng.Range(0.6f, 1.25f);
                    f.Spin = rng.Range(-0.6f, 0.6f);
                    f.FlipSpeed = rng.Range(0.8f, 2.6f);
                    break;
                default:
                    Pick(new[] { (0.7f, 1.25f), (1.3f, 2.3f), (2.3f, 4.4f) }, new[] { (0.35f, 0.6f), (0.7f, 0.95f), (0.85f, 1f) },
                         new[] { (28f, 48f), (50f, 85f), (85f, 135f) }, new float[] { 0, 12, 14 });
                    if (layer == 2 && f.Size > 3.0f * scale) f.Kind = (byte)(1 + rng.Int(3));
                    break;
            }
            if (!anywhere && Season == 2 && rng.Unit() < 0.4f)
            {
                // Fluff also drifts in from the sides.
                f.X = wind >= 0 ? -30 : W + 30;
                f.Y = rng.Range(H * 0.1f, H * 0.8f);
            }
            return f;
        }

        private void StepFlakes(float dt, float cursorSpeed)
        {
            int target = TargetCount;
            if (flakes.Count == 0)
            {
                for (int i = 0; i < target; i++) flakes.Add(MakeFlake(true));
            }
            else if (flakes.Count < target)
            {
                int add = Math.Min(12, target - flakes.Count);
                for (int i = 0; i < add; i++) flakes.Add(MakeFlake(false));
            }

            float sf = SpeedFactor, gain = DepositGain;
            float kk = MathF.Min(1, dt * 2.5f);
            bool push = cursorValid && cursorSpeed > 200;
            int idx = 0;
            while (idx < flakes.Count)
            {
                var f = flakes[idx];
                float layerWind = f.Layer == 0 ? 0.55f : (f.Layer == 1 ? 0.85f : 1.15f);
                float targetVX = wind * layerWind + MathF.Sin(Time * f.SwayFreq + f.Phase) * f.SwayAmp;
                f.VX += (targetVX - f.VX) * kk;
                if (push)
                {
                    float dx = f.X - cursorX, dy = f.Y - cursorY;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < 3600)
                    {
                        float w = (1 - d2 / 3600) * MathF.Min(1, dt * 6);
                        f.VX += cursorVX * 0.5f * w;
                        f.Y += cursorVY * 0.35f * w * dt;
                    }
                }
                float vy;
                if (f.Kind == 5 || f.Kind == 6)
                {
                    // A leaf swings like a pendulum: it tilts with the swing and slows at either end.
                    float swing = MathF.Sin(Time * f.SwayFreq + f.Phase);
                    vy = f.Fall * sf * (0.45f + 0.55f * MathF.Abs(MathF.Cos(Time * f.SwayFreq + f.Phase)));
                    f.Rot = f.Spin * Time * 0.3f + swing * 0.7f + f.Phase;
                }
                else if (f.Kind == 7)
                {
                    // Fluff rides the air: now and then it even rises.
                    vy = f.Fall * sf * (0.45f + 0.9f * MathF.Sin(Time * 0.23f + f.Phase));
                    f.Rot += f.Spin * dt;
                }
                else
                {
                    vy = f.Fall * sf * (1 + 0.15f * MathF.Sin(Time * 0.9f + f.Phase * 2));
                    f.Rot += f.Spin * dt;
                }
                f.Flip += f.FlipSpeed * dt;
                float prevY = f.Y;
                f.X += f.VX * dt;
                f.Y += vy * dt;
                if (f.X < -40) f.X += W + 80; else if (f.X > W + 40) f.X -= W + 80;

                bool gone = f.Y > H + 12 || f.Y < -80;
                if (!gone && f.Layer > 0 && f.Vol > 0 && TryLand(f.X, f.Y, prevY, f.Vol * gain, f.Layer == 2))
                {
                    gone = true;
                    landed++;
                }
                if (gone)
                {
                    if (flakes.Count > target)
                    {
                        flakes[idx] = flakes[flakes.Count - 1];
                        flakes.RemoveAt(flakes.Count - 1);
                        continue;
                    }
                    f = MakeFlake(false);
                }
                flakes[idx] = f;
                idx++;
            }
        }

        /// <summary>Lets a flake settle if it has just crossed the top of a drift.</summary>
        private bool TryLand(float x, float y, float prevY, float vol, bool nearOnly)
        {
            foreach (var s in active)
            {
                if (nearOnly && s.Kind == Surface.SKind.Window) continue;
                if (x < s.X0 || prevY > s.Y + 1 || y < s.Y - s.Cap * 1.2f - 3) continue;
                int col = (int)((x - s.X0) / ColW);
                if (col >= s.H.Length || !s.Vis[col] || s.Cap <= 0.5f) continue;
                float top = s.Y - s.H[col];
                if (y >= top && prevY <= top + 1.5f)
                {
                    float fill = s.H[col] / s.Cap;
                    if (rng.Unit() > 1 - fill * fill) continue;
                    Deposit(s, col, vol);
                    return true;
                }
            }
            return false;
        }

        private static readonly float[] Kernel = MakeKernel();

        private static float[] MakeKernel()
        {
            var raw = new float[13];
            float sum = 0;
            for (int i = 0; i < 13; i++)
            {
                float d = (i - 6) / 3.2f;
                raw[i] = MathF.Exp(-d * d);
                sum += raw[i];
            }
            for (int i = 0; i < 13; i++) raw[i] /= sum;
            return raw;
        }

        private static void Deposit(Surface s, int col, float vol)
        {
            int n = s.H.Length;
            float limit = s.Cap * 1.1f;
            for (int k = 0; k < Kernel.Length; k++)
            {
                int c = col + k - Kernel.Length / 2;
                if (c < 0 || c >= n || !s.Vis[c]) continue;
                s.H[c] = MathF.Min(limit, s.H[c] + vol * Kernel[k] / ColW);
            }
        }

        private void Emit(float x, float y, float vx, float vy, float vol, bool canLand = true, byte? kind = null)
        {
            if (debris.Count >= 5000) return;
            byte k = kind ?? DebrisKind(Season);
            debris.Add(new Debris
            {
                X = x, Y = y, VX = vx, VY = vy,
                Size = MathF.Min(3.2f, 0.8f + MathF.Sqrt(vol) * 0.32f), Vol = vol, CanLand = canLand,
                Kind = k, Tint = (byte)rng.Int(k == 1 ? Palette.Petals.Length : Palette.Leaves.Length),
                Rot = rng.Range(0, 6.28f), Spin = rng.Range(-6, 6),
            });
        }

        private void StepDebris(float dt)
        {
            int i = 0;
            while (i < debris.Count)
            {
                var d = debris[i];
                // Snow drops like snow; petals and leaves flutter down.
                float gravity = d.Kind == 0 ? 620 : (d.Kind == 1 ? 140 : 230);
                float drag = MathF.Max(0, 1 - dt * (d.Kind == 0 ? 1.1f : 2.2f));
                d.VY += gravity * dt;
                d.VX += (wind * 0.4f - d.VX) * MathF.Min(1, dt * 0.8f);
                d.VX *= drag;
                d.VY *= drag;
                d.Rot += d.Spin * dt;
                float prevY = d.Y;
                d.X += d.VX * dt;
                d.Y += d.VY * dt;
                bool gone = d.Y > H + 12 || d.X < -60 || d.X > W + 60;
                if (!gone && d.CanLand && d.VY > 0 && TryLand(d.X, d.Y, prevY, d.Vol, false)) gone = true;
                if (gone)
                {
                    debris[i] = debris[debris.Count - 1];
                    debris.RemoveAt(debris.Count - 1);
                    continue;
                }
                debris[i] = d;
                i++;
            }
        }

        /// <summary>Lets steep piles slump into natural mounds; snow slides off the ends of a window.</summary>
        private void Relax()
        {
            const float maxD = 0.6f * ColW;
            const float rate = 0.3f;
            foreach (var s in active)
            {
                var h = s.H;
                int n = h.Length;
                if (n < 2) continue;
                for (int i = 0; i < n - 1; i++)
                {
                    float d = h[i] - h[i + 1];
                    if (d > maxD) { float m = (d - maxD) * rate; h[i] -= m; h[i + 1] += m; }
                    else if (d < -maxD) { float m = (-d - maxD) * rate; h[i] += m; h[i + 1] -= m; }
                }
                for (int i = n - 2; i >= 0; i--)
                {
                    float d = h[i] - h[i + 1];
                    if (d > maxD) { float m = (d - maxD) * rate; h[i] -= m; h[i + 1] += m; }
                    else if (d < -maxD) { float m = (-d - maxD) * rate; h[i] += m; h[i + 1] -= m; }
                }
                if (s.Kind == Surface.SKind.Floor) continue;
                if (h[0] > maxD)
                {
                    float m = (h[0] - maxD) * rate;
                    h[0] -= m;
                    s.SpillL += m * ColW;
                    if (s.SpillL >= 5)
                    {
                        if (s.Vis[0]) Emit(s.X0 - 1, s.Y - h[0] * 0.5f, rng.Range(-35, -8), rng.Range(-10, 20), s.SpillL);
                        s.SpillL = 0;
                    }
                }
                if (h[n - 1] > maxD)
                {
                    float m = (h[n - 1] - maxD) * rate;
                    h[n - 1] -= m;
                    s.SpillR += m * ColW;
                    if (s.SpillR >= 5)
                    {
                        if (s.Vis[n - 1]) Emit(s.X1 + 1, s.Y - h[n - 1] * 0.5f, rng.Range(8, 35), rng.Range(-10, 20), s.SpillR);
                        s.SpillR = 0;
                    }
                }
            }
        }

        // ---- Cursor in the snow ----

        private void Carve(float cx, float cy, float R)
        {
            float removed = 0;
            foreach (var s in active)
            {
                if (cx + R < s.X0 || cx - R > s.X1 || cy + R < s.Y - s.Cap * 1.2f - 1 || cy - R > s.Y + 2) continue;
                int a = Math.Max(0, (int)((cx - R - s.X0) / ColW));
                int b = Math.Min(s.H.Length - 1, (int)((cx + R - s.X0) / ColW));
                for (int c = a; c <= b; c++)
                {
                    if (!s.Vis[c] || s.H[c] <= 0) continue;
                    float dx = s.X0 + (c + 0.5f) * ColW - cx;
                    float q = R * R - dx * dx;
                    if (q <= 0) continue;
                    float reach = cy + MathF.Sqrt(q);
                    float top = s.Y - s.H[c];
                    if (reach <= top) continue;
                    float nh = MathF.Max(0, s.Y - reach);
                    if (nh < s.H[c])
                    {
                        removed += (s.H[c] - nh) * ColW;
                        s.H[c] = nh;
                    }
                }
            }
            if (removed <= 0) return;

            if (config.Tool == 1 && Winter)
            {
                // Snowball mode: the swept snow is packed into a ball.
                float packed = removed * 0.55f;
                var b = activeBall;
                if (b != null && b.Above == null && b.Below == null && !b.Dormant &&
                    (b.X - cx) * (b.X - cx) + (b.Y - cy) * (b.Y - cy) < (b.R + 55) * (b.R + 55))
                {
                    if (b.R < MaxBallRadius) b.Area += packed;
                }
                else
                {
                    pendingBall += packed;
                    if (pendingBall >= 60)
                    {
                        float dir = cursorVX >= 0 ? 1 : -1;
                        var nb = new Ball(MathF.Min(W - 6, MathF.Max(6, cx + dir * (R + 5))), cy, pendingBall, (uint)rng.Next());
                        nb.VX = cursorVX * 0.3f;
                        Balls.Add(nb);
                        activeBall = nb;
                        pendingBall = 0;
                    }
                }
                return;
            }

            carveAcc += removed;
            while (carveAcc >= 5)
            {
                carveAcc -= 5;
                Emit(cx + rng.Range(-R, R) * 0.7f, cy + rng.Range(-4, 4),
                     cursorVX * 0.45f + rng.Range(-70, 70), cursorVY * 0.3f - rng.Range(40, 170), 5);
            }
        }

        // ---- Snowballs and snowmen ----

        private (float, Surface)? Ground(float x, float prevBottom, float r)
        {
            (float, Surface)? best = null;
            foreach (var s in active)
            {
                if (x < s.X0 || x >= s.X1 || s.Y < prevBottom - 10) continue;
                int col = (int)((x - s.X0) / ColW);
                if (col >= s.H.Length || col < 0) continue;
                if (s.Kind != Surface.SKind.Floor && !s.Vis[col]) continue;
                float hh = s.H[col];
                float top = s.Y - hh + MathF.Min(hh * 0.6f, r * 0.3f);
                if (best == null || top < best.Value.Item1) best = (top, s);
            }
            return best;
        }

        private static int ChainLength(Ball root)
        {
            int n = 1;
            var b = root;
            while (b.Above != null) { n++; b = b.Above; }
            return n;
        }

        private bool TryStack(Ball a)
        {
            if (a.Below != null) return false;
            int aLen = ChainLength(a);
            foreach (var t in Balls)
            {
                if (t.Above != null || t == a || t.Root == a) continue;
                var tr = t.Root;
                if (tr.Dormant || tr.Grabbed || tr.Hidden) continue;
                float dx = a.X - t.X, dy = a.Y - t.Y;
                float reach = a.R + t.R + 2;
                if (dx * dx + dy * dy > reach * reach) continue;
                if (dy > -0.3f * t.R || MathF.Abs(dx) > 0.6f * t.R || a.R > t.R * 1.15f) continue;
                if (ChainLength(tr) + aLen > 3) continue;
                a.Below = t;
                t.Above = a;
                a.VX = 0;
                a.VY = 0;
                a.Support = null;
                return true;
            }
            return false;
        }

        private void Burst(Ball root, bool remove = true)
        {
            var members = root.Chain;
            foreach (var b in members)
            {
                int n = Math.Min(110, (int)(b.Area / 9) + 6);
                float vol = b.Area / 0.55f / n;
                for (int i = 0; i < n; i++)
                {
                    float a = rng.Range(0, 6.28f), d = MathF.Sqrt(rng.Unit()) * b.R;
                    float sp = rng.Range(40, 260);
                    Emit(b.X + MathF.Cos(a) * d, b.Y + MathF.Sin(a) * d, MathF.Cos(a) * sp + b.VX * 0.3f, MathF.Sin(a) * sp - 90, vol);
                }
            }
            if (remove) RemoveBalls(m => members.Contains(m));
        }

        private void StepBalls(float dt, float cursorSpeed)
        {
            if (Balls.Count == 0) return;
            var burstList = new List<Ball>();

            foreach (var a in Balls.ToArray())
            {
                if (a.Below != null || a.Dormant) continue;
                if (a.Grabbed)
                {
                    float tx = MathF.Min(W - a.R, MathF.Max(a.R, cursorX + grabDX));
                    float ty = MathF.Min(H - a.R * 0.5f, MathF.Max(TopInset + a.R, cursorY + grabDY));
                    float k = MathF.Min(1, dt * 14);
                    a.VX += ((tx - a.X) / dt - a.VX) * k;
                    a.VY += ((ty - a.Y) / dt - a.VY) * k;
                    a.X = tx;
                    a.Y = ty;
                    a.Hidden = false;
                    continue;
                }

                float prevBottom = a.Y + a.R;
                a.VY += 1400 * dt;
                a.X += a.VX * dt;
                a.Y += a.VY * dt;
                if (a.X < a.R) { a.X = a.R; a.VX = MathF.Abs(a.VX) * 0.3f; }
                if (a.X > W - a.R) { a.X = W - a.R; a.VX = -MathF.Abs(a.VX) * 0.3f; }

                // Cursor shoves loose balls.
                if (cursorValid && config.Tool != 2 && cursorSpeed > 150 && a.Above == null)
                {
                    float dx = a.X - cursorX, dy = a.Y - cursorY;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float reach = a.R + 12;
                    if (d < reach && d > a.R * 0.45f)
                    {
                        float nx = dx / d, ny = dy / d;
                        a.X += nx * (reach - d);
                        a.Y += ny * (reach - d) * 0.5f;
                        float vn = cursorVX * nx + cursorVY * ny;
                        float bn = a.VX * nx + a.VY * ny;
                        if (vn > bn)
                        {
                            a.VX += nx * (vn - bn);
                            a.VY += ny * (vn - bn) * 0.5f;
                        }
                        float sp = MathF.Sqrt(a.VX * a.VX + a.VY * a.VY);
                        if (sp > 1100) { a.VX *= 1100 / sp; a.VY *= 1100 / sp; }
                        activeBall = a;
                    }
                }

                // Other balls.
                foreach (var b in Balls)
                {
                    if (b == a || b.Root == a) continue;
                    var br = b.Root;
                    if (br.Dormant || br.Grabbed || br.Hidden) continue;
                    float dx = a.X - b.X, dy = a.Y - b.Y;
                    float reach = a.R + b.R;
                    float d2 = dx * dx + dy * dy;
                    if (d2 >= reach * reach || d2 < 0.0001f) continue;
                    if (b.Above == null && a.VY >= 0 && TryStack(a)) break;
                    float d = MathF.Sqrt(d2);
                    float nx = dx / d, ny = dy / d;
                    float overlap = reach - d;
                    if (b.Below == null && b.Above == null)
                    {
                        a.X += nx * overlap * 0.5f; a.Y += ny * overlap * 0.5f;
                        b.X -= nx * overlap * 0.5f;
                    }
                    else
                    {
                        a.X += nx * overlap; a.Y += ny * overlap;
                    }
                    float vn2 = a.VX * nx + a.VY * ny;
                    if (vn2 < 0)
                    {
                        a.VX -= nx * vn2 * 1.15f;
                        a.VY -= ny * vn2 * 1.15f;
                    }
                }
                if (a.Below != null) continue;

                // Ground.
                var g = Ground(a.X, prevBottom, a.R);
                if (g != null && a.Y + a.R >= g.Value.Item1)
                {
                    float gy = g.Value.Item1;
                    var s = g.Value.Item2;
                    if (a.VY > 1000)
                    {
                        burstList.Add(a);
                        continue;
                    }
                    a.Y = gy - a.R;
                    a.VY = a.VY > 140 ? -a.VY * 0.18f : 0;
                    a.VX *= MathF.Max(0, 1 - dt * 2.4f);
                    if (MathF.Abs(a.VX) < 4) a.VX = 0;
                    a.Support = s.Id;
                    a.RelX = a.X - s.X0;
                    a.Angle += a.VX / MathF.Max(3, a.R) * dt;
                    int col = (int)((a.X - s.X0) / ColW);
                    a.Hidden = s.Kind != Surface.SKind.Floor && col >= 0 && col < s.Vis.Length && !s.Vis[col];
                    // A rolling ball packs the snow it rolls over.
                    if (MathF.Abs(a.VX) > 10 && a.Above == null && a.R < MaxBallRadius && !a.Hidden)
                    {
                        int ca = Math.Max(0, (int)((a.X - a.R * 0.7f - s.X0) / ColW));
                        int cb = Math.Min(s.H.Length - 1, (int)((a.X + a.R * 0.7f - s.X0) / ColW));
                        float f = MathF.Min(1, dt * 12);
                        for (int c = ca; c <= cb; c++)
                        {
                            if (!s.Vis[c] || s.H[c] <= 0.4f) continue;
                            float take = s.H[c] * f;
                            s.H[c] -= take;
                            a.Area += take * ColW * 0.55f;
                        }
                    }
                }
                else
                {
                    a.Support = null;
                    a.Hidden = false;
                    a.Angle += a.VX / MathF.Max(3, a.R) * dt * 0.5f;
                    if (a.Y - a.R > H + 80) burstList.Add(a);
                }
            }

            foreach (var b in burstList) Burst(b);

            // Stacked balls ride on the one below.
            foreach (var root in Balls)
            {
                if (root.Below != null) continue;
                var lower = root;
                while (lower.Above != null)
                {
                    var up = lower.Above;
                    up.X += (lower.X - up.X) * MathF.Min(1, dt * 22);
                    up.Y = lower.Y - (lower.R + up.R) * 0.86f;
                    up.VX = lower.VX;
                    up.VY = lower.VY;
                    up.Angle = 0;
                    lower = up;
                }
            }
        }

        // ---- Garland ----

        private static readonly (float, float, float)[] BulbColors =
        {
            (1.0f, 0.24f, 0.22f), (1.0f, 0.78f, 0.22f), (0.28f, 0.92f, 0.40f), (0.30f, 0.60f, 1.0f), (1.0f, 0.40f, 0.85f),
        };

        private void BuildGarland()
        {
            bulbs.Clear();
            wire.Clear();
            int spans = Math.Max(2, (int)MathF.Round(W / 150));
            float spanW = W / spans;
            float y0 = TopInset + 1.5f;
            const float sag = 13;
            int index = 0;
            for (int si = 0; si < spans; si++)
            {
                float xa = si * spanW;
                for (int k = 0; k <= 12; k++)
                {
                    float t = k / 12f;
                    wire.Add((xa + t * spanW, y0 + sag * 4 * t * (1 - t)));
                }
                for (int k = 0; k < 5; k++)
                {
                    float t = (k + 0.5f) / 5;
                    bulbs.Add(new Bulb
                    {
                        X = xa + t * spanW, Y = y0 + sag * 4 * t * (1 - t), Color = index % BulbColors.Length,
                        Phase = rng.Range(0, 6.28f), Speed = rng.Range(1.2f, 3.4f),
                    });
                    index++;
                }
            }
        }

        private void DrawGarland(Batch batch)
        {
            if (config.Garland == 0 || wire.Count < 2) return;
            for (int i = 0; i < wire.Count - 1; i++)
            {
                var a = wire[i];
                var b = wire[i + 1];
                if (b.X < a.X) continue;
                batch.Line(a.X, a.Y, b.X, b.Y, 1.3f, 0.07f, 0.22f, 0.11f, 0.95f);
            }
            for (int i = 0; i < bulbs.Count; i++)
            {
                var bulb = bulbs[i];
                float br = 1;
                if (config.Garland == 2)
                {
                    br = 0.4f + 0.6f * (0.5f + 0.5f * MathF.Sin(Time * bulb.Speed + bulb.Phase));
                }
                else if (config.Garland == 3)
                {
                    float c = 0.5f + 0.5f * MathF.Cos(2 * MathF.PI * (i / 4f - Time * 0.8f));
                    br = 0.22f + 0.78f * c * c;
                }
                var col = BulbColors[bulb.Color];
                batch.Spr(Sprite.Glow, bulb.X, bulb.Y + 7, 17, 17, 0, col.Item1, col.Item2, col.Item3, 0.5f * br, true);
                batch.Rect(bulb.X - 1.7f, bulb.Y - 1.2f, 3.4f, 3.4f, 0.06f, 0.18f, 0.09f, 1);
                float k = 0.45f + 0.55f * br;
                batch.Spr(Sprite.Bulb, bulb.X, bulb.Y + 7.2f, 3.7f, 5.5f, 0, col.Item1 * k, col.Item2 * k, col.Item3 * k, 1);
                batch.Spr(Sprite.Dot, bulb.X - 0.9f, bulb.Y + 5.4f, 1.5f, 1.5f, 0, 1, 1, 1, 0.75f * br);
            }
        }

        // ---- Drawing ----

        public void Build(Batch batch)
        {
            // Far flakes first: they are behind the windows.
            bool depth = config.Depth;
            foreach (var f in flakes)
            {
                if (f.Layer != 0) continue;
                if (depth)
                {
                    bool behind = false;
                    foreach (var o in occluders) if (o.Contains(f.X, f.Y)) { behind = true; break; }
                    if (behind) continue;
                }
                DrawFlake(f, batch);
            }

            Festive.DrawFireworks(batch);
            if (Winter) DrawGarland(batch);
            DrawBalls(batch);
            DrawDrifts(batch);
            Festive.DrawDecor(batch);

            foreach (var d in debris)
            {
                if (d.Kind == 1)
                {
                    var c = Palette.Petals[d.Tint % Palette.Petals.Length];
                    float hw = 2.6f + d.Size * 0.6f;
                    batch.Spr(Sprite.Petal, d.X, d.Y, hw * MathF.Max(0.3f, MathF.Abs(MathF.Cos(d.Rot * 0.7f))), hw * 1.15f, d.Rot, c.R, c.G, c.B, 0.95f);
                }
                else if (d.Kind == 2)
                {
                    var c = Palette.Leaves[d.Tint % Palette.Leaves.Length];
                    float hw = 4 + d.Size * 1.2f;
                    batch.Spr(d.Tint % 2 == 0 ? Sprite.Maple : Sprite.Leaf, d.X, d.Y, hw * MathF.Max(0.3f, MathF.Abs(MathF.Cos(d.Rot * 0.6f))), hw,
                              d.Rot, c.R, c.G, c.B, 1);
                }
                else
                {
                    float hw = d.Size * 1.5f;
                    batch.Spr(Sprite.Dot, d.X, d.Y, hw, hw, 0, 1, 1, 1, 0.95f);
                }
            }
            foreach (var f in flakes)
            {
                if (f.Layer == 0) continue;
                DrawFlake(f, batch);
            }
            Festive.DrawCreatures(batch);
            if (config.Frost && Winter) Festive.DrawFrost(batch);
        }

        private static void DrawFlake(in Flake f, Batch batch)
        {
            switch (f.Kind)
            {
                case 0:
                {
                    float hw = f.Size * 1.5f;
                    batch.Spr(Sprite.Dot, f.X, f.Y, hw, hw, 0, 1, 1, 1, f.Alpha);
                    break;
                }
                case 1:
                case 2:
                case 3:
                {
                    float hw = f.Size * 1.9f;
                    var sprite = f.Kind == 1 ? Sprite.Crystal1 : (f.Kind == 2 ? Sprite.Crystal2 : Sprite.Crystal3);
                    batch.Spr(sprite, f.X, f.Y, hw, hw, f.Rot, 1, 1, 1, f.Alpha);
                    break;
                }
                case 4:
                {
                    var c = Palette.Petals[f.Tint % Palette.Petals.Length];
                    batch.Spr(Sprite.Petal, f.X, f.Y, f.Size * MathF.Max(0.22f, MathF.Abs(MathF.Cos(f.Flip))), f.Size * 1.15f, f.Rot, c.R, c.G, c.B, f.Alpha);
                    break;
                }
                case 5:
                case 6:
                {
                    var c = Palette.Leaves[f.Tint % Palette.Leaves.Length];
                    // Far leaves are paler, as if seen through the air.
                    float mist = f.Layer == 0 ? 0.35f : 0;
                    batch.Spr(f.Kind == 6 ? Sprite.Maple : Sprite.Leaf, f.X, f.Y, f.Size * MathF.Max(0.3f, MathF.Abs(MathF.Cos(f.Flip))), f.Size, f.Rot,
                              c.R + (0.9f - c.R) * mist, c.G + (0.85f - c.G) * mist, c.B + (0.8f - c.B) * mist, f.Alpha);
                    break;
                }
                default:
                    batch.Spr(Sprite.Fluff, f.X, f.Y, f.Size, f.Size, f.Rot, 1, 1, 1, f.Alpha);
                    break;
            }
        }

        private static readonly float[] Smooth = { 1, 3, 6, 8, 6, 3, 1 };

        private void DrawDrifts(Batch batch)
        {
            if (Season == 0) DrawSnowDrifts(batch);
            else if (Season != 2) DrawLitter(batch);
        }

        /// <summary>Smoothed, tapered height of a pile at column i (the same shape the snow drifts use).</summary>
        private static float PileHeight(Surface s, int i)
        {
            int n = s.H.Length;
            float acc = 0, wsum = 0;
            for (int k = -3; k <= 3; k++)
            {
                int j = i + k;
                if (j < 0 || j >= n || !s.Vis[j]) continue;
                float w = Smooth[k + 3];
                acc += s.H[j] * w;
                wsum += w;
            }
            int edge = Math.Min(i, n - 1 - i);
            float taper = s.Kind == Surface.SKind.Floor ? 1 : MathF.Min(1, 0.45f + edge * 0.11f);
            return (wsum > 0 ? acc / wsum : s.H[i]) * taper;
        }

        /// <summary>Petals and leaves: a soft coloured bed with the pieces themselves lying on top.</summary>
        private void DrawLitter(Batch batch)
        {
            bool autumn = Season == 3;
            var top = autumn ? (0.78f, 0.42f, 0.14f) : (1.0f, 0.86f, 0.90f);
            var low = autumn ? (0.42f, 0.21f, 0.08f) : (0.93f, 0.70f, 0.78f);
            foreach (var s in active)
            {
                int n = s.H.Length;
                if (n < 3) continue;
                float baseY = s.Kind == Surface.SKind.Floor ? s.Y : s.Y + 1;
                float prevH = 0;
                bool prevOK = false;
                for (int i = 0; i < n; i++)
                {
                    if (!s.Vis[i]) { prevOK = false; continue; }
                    float hh = PileHeight(s, i);
                    if (prevOK && (hh > 0.25f || prevH > 0.25f))
                    {
                        float xb = s.X0 + (i + 0.5f) * ColW, xa = xb - ColW;
                        float aa = MathF.Min(1, prevH / 1.5f) * 0.92f, ab = MathF.Min(1, hh / 1.5f) * 0.92f;
                        batch.Quad(batch.Flat(xa, s.Y - prevH, top.Item1, top.Item2, top.Item3, aa), batch.Flat(xb, s.Y - hh, top.Item1, top.Item2, top.Item3, ab),
                                   batch.Flat(xa, baseY, low.Item1, low.Item2, low.Item3, aa), batch.Flat(xb, baseY, low.Item1, low.Item2, low.Item3, ab));
                    }
                    prevH = hh;
                    prevOK = true;
                }
                // The pieces on top: a few per column, placed by a hash so they stay put from frame to frame.
                int stride = autumn ? 3 : 2;
                for (int i = 1; i < n - 1; i += stride)
                {
                    if (!s.Vis[i]) continue;
                    float hh = PileHeight(s, i);
                    if (hh < 0.9f) continue;
                    int layers = autumn ? Math.Min(4, (int)(hh / 3.2f) + 1) : 1;
                    for (int k = 0; k < layers; k++)
                    {
                        uint hsh = unchecked((uint)(i * 7919) ^ (uint)(s.Id * 104729) ^ (uint)(k * 1299709));
                        float jitter = (hsh % 100) / 100f - 0.5f;
                        float x = s.X0 + (i + 0.5f + jitter * 1.6f) * ColW;
                        float y = s.Y - hh + 1.2f + k * 3.4f;
                        float rot = (hsh % 628) / 100f;
                        if (autumn)
                        {
                            var c = Palette.Leaves[(int)((hsh >> 8) % (uint)Palette.Leaves.Length)];
                            float shade = 1 - k * 0.14f;
                            float size = 4.6f + ((hsh >> 4) % 30) / 10f;
                            batch.Spr(((hsh >> 12) % 2) == 0 ? Sprite.Maple : Sprite.Leaf, x, y, size * 0.9f, size, rot,
                                      c.R * shade, c.G * shade, c.B * shade, 1);
                        }
                        else
                        {
                            var c = Palette.Petals[(int)((hsh >> 8) % (uint)Palette.Petals.Length)];
                            batch.Spr(Sprite.Petal, x, y, 2.4f, 2.8f, rot, c.R, c.G, c.B, 0.95f);
                        }
                    }
                }
            }
        }

        private void DrawSnowDrifts(Batch batch)
        {
            foreach (var s in active)
            {
                int n = s.H.Length;
                if (n < 3) continue;
                float baseY = s.Kind == Surface.SKind.Floor ? s.Y : s.Y + 1;
                float prevH = 0;
                bool prevOK = false;
                for (int i = 0; i < n; i++)
                {
                    if (!s.Vis[i]) { prevOK = false; continue; }
                    float acc = 0, wsum = 0;
                    for (int k = -3; k <= 3; k++)
                    {
                        int j = i + k;
                        if (j < 0 || j >= n || !s.Vis[j]) continue;
                        float w = Smooth[k + 3];
                        acc += s.H[j] * w;
                        wsum += w;
                    }
                    // Keep the very ends rounded instead of chopped.
                    int edge = Math.Min(i, n - 1 - i);
                    float taper = s.Kind == Surface.SKind.Floor ? 1 : MathF.Min(1, 0.45f + edge * 0.11f);
                    float hh = (wsum > 0 ? acc / wsum : s.H[i]) * taper;
                    if (prevOK && (hh > 0.25f || prevH > 0.25f))
                    {
                        float xb = s.X0 + (i + 0.5f) * ColW;
                        float xa = xb - ColW;
                        float ya = s.Y - prevH, yb = s.Y - hh;
                        float aa = MathF.Min(1, prevH / 1.2f) * 0.97f, ab = MathF.Min(1, hh / 1.2f) * 0.97f;
                        float sa = MathF.Min(1, prevH / 16), sb = MathF.Min(1, hh / 16);
                        // Body: white on top, a cool shadow towards the base of deep snow.
                        batch.Quad(batch.Flat(xa, ya, 1, 1, 1, aa), batch.Flat(xb, yb, 1, 1, 1, ab),
                                   batch.Flat(xa, baseY, 1 - 0.19f * sa, 1 - 0.12f * sa, 1 - 0.03f * sa, aa),
                                   batch.Flat(xb, baseY, 1 - 0.19f * sb, 1 - 0.12f * sb, 1 - 0.03f * sb, ab));
                        // Soft top edge.
                        batch.Quad(batch.Flat(xa, ya - 1.2f, 1, 1, 1, 0), batch.Flat(xb, yb - 1.2f, 1, 1, 1, 0),
                                   batch.Flat(xa, ya, 1, 1, 1, aa), batch.Flat(xb, yb, 1, 1, 1, ab));
                    }
                    // Now and then a crystal on the surface catches the light.
                    long sid = s.Id;
                    if (config.Sparkle && hh > 3 && ((i * 7919L + sid * 31L) % 29 + 29) % 29 == 0)
                    {
                        float phase = (float)(((i * 131L + sid * 17L) % 628 + 628) % 628) / 100;
                        float tw = MathF.Sin(Time * 1.1f + phase);
                        if (tw > 0.9f)
                        {
                            float g = (tw - 0.9f) / 0.1f;
                            float xg = s.X0 + (i + 0.5f) * ColW, yg = s.Y - hh + 1.8f;
                            batch.Spr(Sprite.Glow, xg, yg, 6, 6, 0, 0.85f, 0.93f, 1, g * 0.6f, true);
                            batch.Spr(Sprite.Crystal3, xg, yg, 3.4f * g + 0.6f, 3.4f * g + 0.6f, Time * 0.8f + phase, 1, 1, 1, g, true);
                        }
                    }
                    prevH = hh;
                    prevOK = true;
                }
            }
        }

        private static readonly (float, float, float)[] ScarfColors =
        {
            (0.90f, 0.20f, 0.22f), (0.20f, 0.55f, 0.95f), (0.18f, 0.70f, 0.42f), (0.98f, 0.72f, 0.15f), (0.72f, 0.36f, 0.90f),
        };

        private void DrawBalls(Batch batch)
        {
            foreach (var root in Balls)
            {
                if (root.Below != null || root.Dormant || root.Hidden) continue;
                var chain = root.Chain;
                if (chain.Count >= 2) DrawArms(chain, batch);
                foreach (var b in chain)
                {
                    if (b == Hover || b == GrabbedBall)
                        batch.Spr(Sprite.Glow, b.X, b.Y, b.R * 2.1f, b.R * 2.1f, 0, 1, 1, 1, 0.45f, true);
                    float hw = b.R * 1.03f;
                    batch.Spr(Sprite.BallBase, b.X, b.Y, hw, hw);
                    batch.Spr(Sprite.BallTex, b.X, b.Y, hw, hw, b.Angle, 1, 1, 1, 0.9f);
                }
                if (chain.Count >= 2) DrawFace(chain, batch);
            }
        }

        private static void DrawArms(List<Ball> chain, Batch batch)
        {
            var body = chain[chain.Count - 2];
            float br = body.R;
            float hw = br * 0.78f, hh = br * 0.39f;
            float ay = body.Y - br * 0.12f;
            batch.Spr(Sprite.Twig, body.X + br * 0.78f + hw * 0.92f, ay - hh * 0.24f, hw, hh);
            batch.Spr(Sprite.Twig, body.X - br * 0.78f - hw * 0.92f, ay - hh * 0.24f, hw, hh, 0, 1, 1, 1, 1, false, true);
        }

        private static void DrawFace(List<Ball> chain, Batch batch)
        {
            var head = chain[chain.Count - 1];
            var body = chain[chain.Count - 2];
            uint style = chain[0].Style;
            float dir = (style & 1) == 0 ? 1 : -1;
            float hr = head.R, br = body.R;

            // Buttons.
            float[] buttons = chain.Count >= 3 ? new[] { -0.4f, 0f, 0.4f } : new[] { -0.1f, 0.3f };
            foreach (float k in buttons)
                batch.Spr(Sprite.Coal, body.X, body.Y + k * br, br * 0.085f, br * 0.085f);

            // Scarf.
            var sc = ScarfColors[(int)((style >> 1) % (uint)ScarfColors.Length)];
            float shh = hr * 0.62f;
            batch.Spr(Sprite.Scarf, head.X, head.Y + hr * 0.80f + shh * 0.36f, hr * 1.08f, shh, 0, sc.Item1, sc.Item2, sc.Item3, 1, false, dir < 0);

            // Eyes, smile, carrot.
            float fx = head.X + dir * hr * 0.06f;
            foreach (float side in new[] { -1f, 1f })
                batch.Spr(Sprite.Coal, fx + side * hr * 0.31f, head.Y - hr * 0.20f, hr * 0.115f, hr * 0.115f);
            for (int k = -2; k <= 2; k++)
                batch.Spr(Sprite.Coal, fx + k * hr * 0.14f, head.Y + hr * 0.50f - k * k * hr * 0.04f, hr * 0.05f, hr * 0.05f);
            float len = hr * 0.95f;
            batch.Spr(Sprite.Carrot, fx + dir * len * 0.5f, head.Y + hr * 0.10f, len * 0.5f, len * 0.2f, dir * 0.10f, 1, 1, 1, 1, false, dir < 0);

            // Hat.
            var hat = (style >> 5) % 3 == 1 ? Sprite.TopHat : Sprite.Bucket;
            batch.Spr(hat, head.X - dir * hr * 0.14f, head.Y - hr * 1.22f, hr * 0.95f, hr * 0.95f, -dir * 0.16f);
        }

        // ---- Test hooks ----

        public Ball DebugAddBall(float x, float y, float radius)
        {
            var b = new Ball(x, y, MathF.PI * radius * radius, (uint)rng.Next());
            Balls.Add(b);
            return b;
        }

        public (int flakes, int debris, int surfaces, float snow) DebugStats
        {
            get
            {
                float total = 0;
                foreach (var s in surfaces.Values) foreach (float v in s.H) total += v * ColW;
                return (flakes.Count, debris.Count, active.Count, total);
            }
        }

        public (float snow, bool active) DebugDock
        {
            get
            {
                if (!surfaces.TryGetValue(DockID, out var d)) return (0, false);
                float sum = 0;
                foreach (float v in d.H) sum += v;
                return (sum * ColW, d.OnScreen);
            }
        }

        public int DebugDebris => debris.Count;

        public float DebugMaxHeight()
        {
            float m = 0;
            foreach (var s in surfaces.Values) foreach (float v in s.H) m = MathF.Max(m, v);
            return m;
        }

        public bool DebugHasNaN()
        {
            foreach (var f in flakes) if (!(float.IsFinite(f.X) && float.IsFinite(f.Y))) return true;
            foreach (var b in Balls) if (!(float.IsFinite(b.X) && float.IsFinite(b.Y) && float.IsFinite(b.R))) return true;
            foreach (var s in surfaces.Values) foreach (float v in s.H) if (!float.IsFinite(v)) return true;
            return false;
        }
    }
}
