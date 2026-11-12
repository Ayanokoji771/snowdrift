using System;
using System.Collections.Generic;

namespace Snowdrift
{
    /// <summary>A finger on the touchpad, in millimetres from the pad's top-left corner.</summary>
    public struct Contact
    {
        public int Id;
        public float X, Y;
        public Contact(int id, float x, float y) { Id = id; X = x; Y = y; }
    }

    public enum SwipeAxis { Horizontal, Vertical }

    /// <summary>Turns touchpad frames into swipes made with a set number of fingers.
    /// Pure logic: the self-test drives it with made-up fingers.</summary>
    public sealed class SwipeRecognizer
    {
        public int Fingers = 4;
        /// <summary>How far the fingers travel (mm) before the swipe picks a direction.</summary>
        public const float LockDistance = 4.5f;

        /// <summary>A swipe started: its axis and the movement so far (mm).</summary>
        public event Action<SwipeAxis, float, float> Began;
        /// <summary>Movement since the fingers landed (mm).</summary>
        public event Action<float, float> Moved;
        /// <summary>The fingers lifted; their speed at that moment (mm/s).</summary>
        public event Action<float, float> Ended;

        private enum State { Idle, Pending, Active, Blocked }
        private State state = State.Idle;
        private float startX, startY, lastX, lastY, vx, vy;
        private double lastT;

        public bool Swiping => state == State.Active;

        public void Feed(IReadOnlyList<Contact> fingers, double t)
        {
            int n = fingers.Count;
            float cx = 0, cy = 0;
            for (int i = 0; i < n; i++) { cx += fingers[i].X; cy += fingers[i].Y; }
            if (n > 0) { cx /= n; cy /= n; }

            switch (state)
            {
                case State.Idle:
                    if (n == Fingers) Start(cx, cy, t);
                    else if (n > Fingers) state = State.Blocked;
                    break;

                case State.Pending:
                    if (n != Fingers)
                    {
                        state = n > Fingers ? State.Blocked : State.Idle;
                        break;
                    }
                    Track(cx, cy, t);
                    float dx = cx - startX, dy = cy - startY;
                    if (dx * dx + dy * dy >= LockDistance * LockDistance)
                    {
                        state = State.Active;
                        Began?.Invoke(MathF.Abs(dx) >= MathF.Abs(dy) ? SwipeAxis.Horizontal : SwipeAxis.Vertical, dx, dy);
                        Moved?.Invoke(dx, dy);
                    }
                    break;

                case State.Active:
                    // The first frame with a different finger count is the release; fingers never
                    // lift at exactly the same moment, and their centre would jump otherwise.
                    if (n != Fingers)
                    {
                        Release(t);
                        state = n == 0 ? State.Idle : State.Blocked;
                        break;
                    }
                    Track(cx, cy, t);
                    Moved?.Invoke(cx - startX, cy - startY);
                    break;

                case State.Blocked:
                    if (n == 0) state = State.Idle;
                    break;
            }
        }

        /// <summary>Some touchpads go quiet instead of reporting lifted fingers.</summary>
        public void Tick(double t)
        {
            if (state == State.Idle || t - lastT < 0.25) return;
            if (state == State.Active) Release(t);
            state = State.Idle;
        }

        private void Start(float x, float y, double t)
        {
            state = State.Pending;
            startX = lastX = x;
            startY = lastY = y;
            lastT = t;
            vx = vy = 0;
        }

        private void Track(float x, float y, double t)
        {
            float dt = (float)(t - lastT);
            if (dt < 0.0005f) return;
            float k = MathF.Min(1, dt / 0.035f);
            vx += ((x - lastX) / dt - vx) * k;
            vy += ((y - lastY) / dt - vy) * k;
            lastX = x;
            lastY = y;
            lastT = t;
        }

        private void Release(double t)
        {
            // Fingers that rested before lifting have no speed left.
            float k = MathF.Exp(-MathF.Max(0, (float)(t - lastT)) / 0.05f);
            Ended?.Invoke(vx * k, vy * k);
        }
    }

    /// <summary>A critically damped spring: the Mac feel — fast, soft landing, no wobble.</summary>
    public struct Spring
    {
        public float X, V;

        public void Step(float target, float dt, float stiffness = 280)
        {
            float damping = 2 * MathF.Sqrt(stiffness);
            int n = Math.Max(1, (int)MathF.Ceiling(dt / 0.004f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                float a = -stiffness * (X - target) - damping * V;
                V += a * h;
                X += V * h;
            }
        }

        public bool Settled(float target) => MathF.Abs(X - target) < 0.002f && MathF.Abs(V) < 0.03f;
    }

    public static class GestureMath
    {
        /// <summary>Mac-style rubber band: follows the fingers less and less and never passes <paramref name="limit"/>.</summary>
        public static float Rubber(float x, float limit = 0.1f, float c = 0.55f)
        {
            float a = MathF.Abs(x);
            return MathF.Sign(x) * limit * (1 - 1 / (a * c / limit + 1));
        }

        /// <summary>What a desktop swipe shows for finger progress <paramref name="p"/>
        /// (negative: the content moves left, the desktop on the right comes in).</summary>
        public static float DesktopShown(float p, bool hasLeft, bool hasRight)
        {
            if (p > 0 && !hasLeft) return Rubber(p);
            if (p < 0 && !hasRight) return Rubber(p);
            if (p > 1) return 1 + Rubber(p - 1);
            if (p < -1) return -1 + Rubber(p + 1);
            return p;
        }

        /// <summary>Where a released desktop swipe settles: -1 the desktop on the right, 1 the one on the left, 0 stay.
        /// A quick flick counts as much as a long drag.</summary>
        public static int SwitchTarget(float p, float v, bool hasLeft, bool hasRight)
        {
            float projected = p + v * 0.18f;
            if (projected <= -0.5f && p < 0 && hasRight) return -1;
            if (projected >= 0.5f && p > 0 && hasLeft) return 1;
            return 0;
        }

        /// <summary>Whether a released one-way swipe (show desktop, bring windows back) goes through.</summary>
        public static bool Commit(float p, float v, float threshold = 0.4f) => p > 0.05f && p + v * 0.18f >= threshold;

        /// <summary>What a one-way swipe shows: 0…1, with a little rubber at both ends.</summary>
        public static float OneWayShown(float p)
        {
            if (p < 0) return Rubber(p, 0.04f);
            if (p > 1) return 1 + Rubber(p - 1, 0.04f);
            return p;
        }

        public static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }
    }
}
