#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>One hand's palm in one frame (world space): its centre and the unit normal out of the palm.</summary>
    public readonly struct Palm
    {
        public readonly bool Valid;
        public readonly Vec3 Center;
        public readonly Vec3 Normal;

        public Palm(Vec3 center, Vec3 normal)
        {
            Valid = true;
            Center = center;
            Normal = normal;
        }

        public static readonly Palm None = default;
    }

    public enum MenuHand { None, Left, Right }

    /// <summary>
    /// When the wrist menu shows, and above which hand. Pure, time injected.
    /// A palm turned up toward the head (normal · up above Layout.MenuPalmUpMin, the palm within
    /// Layout.MenuViewDegrees of the head's view) shows the menu above that hand after Layout.MenuShowMs.
    /// It hides once that palm turns away. Rising edge: a palm only counts after it has been seen not up while
    /// the menu was allowed, so a palm that is already up when a card closes or placement ends does not bring the
    /// menu back by itself. Never while not allowed (a card open, placing). Palm up plus a pinch of the same hand
    /// is the system menu's gesture; the menu needs no pinch, and its buttons are pressed with the other hand.
    /// </summary>
    public sealed class WristMenuLogic
    {
        sealed class Side
        {
            public bool SeenDown;
            public double UpSince = double.NaN;

            public void Forget()
            {
                SeenDown = false;
                UpSince = double.NaN;
            }
        }

        readonly Side _left = new Side();
        readonly Side _right = new Side();

        /// <summary>The menu's hand, or None while it is hidden.</summary>
        public MenuHand Shown { get; private set; }

        /// <summary>A palm turned up and held where the head looks.</summary>
        public static bool PalmUp(in Palm palm, Vec3 head, Vec3 headForward)
        {
            if (!palm.Valid) return false;
            if (palm.Normal.Normalized.Y <= Layout.MenuPalmUpMin) return false;
            var view = headForward.Normalized;
            var to = (palm.Center - head).Normalized;
            if (view.Length < 0.5f || to.Length < 0.5f) return false;
            return Vec3.Dot(view, to) >= (float)Math.Cos(Layout.MenuViewDegrees * Math.PI / 180.0);
        }

        /// <summary>One frame. `allowed`: no card open, not placing, something to offer.</summary>
        public MenuHand Update(double nowMs, bool allowed, in Palm left, in Palm right, Vec3 head, Vec3 headForward)
        {
            if (!allowed)
            {
                _left.Forget();
                _right.Forget();
                return Shown = MenuHand.None;
            }
            var leftUp = Track(_left, nowMs, PalmUp(left, head, headForward));
            var rightUp = Track(_right, nowMs, PalmUp(right, head, headForward));

            // the hand that has it keeps it while its palm stays up
            if (Shown == MenuHand.Left && leftUp) return Shown;
            if (Shown == MenuHand.Right && rightUp) return Shown;
            Shown = MenuHand.None;

            var leftReady = Ready(_left, nowMs);
            var rightReady = Ready(_right, nowMs);
            if (leftReady && rightReady) Shown = _left.UpSince <= _right.UpSince ? MenuHand.Left : MenuHand.Right;
            else if (leftReady) Shown = MenuHand.Left;
            else if (rightReady) Shown = MenuHand.Right;
            return Shown;
        }

        /// <summary>Counts how long a palm has been up, from the first frame it comes up after being seen down.</summary>
        static bool Track(Side s, double nowMs, bool up)
        {
            if (!up)
            {
                s.SeenDown = true;
                s.UpSince = double.NaN;
                return false;
            }
            if (s.SeenDown && double.IsNaN(s.UpSince)) s.UpSince = nowMs;
            return true;
        }

        static bool Ready(Side s, double nowMs) => !double.IsNaN(s.UpSince) && nowMs - s.UpSince >= Layout.MenuShowMs - 1e-6;
    }
}
