#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>
    /// Where the pairing viewfinder stands (U12): in the middle of the view, a metre in front of the head, following it
    /// with a soft lag. Position eases toward the point a metre along the gaze; the yaw eases toward the gaze's yaw; it
    /// is always upright (no pitch, no roll). The first step places it at once.
    /// Frozen, it stays exactly where it is, so a control under a ray or under a pinch does not run away and a question
    /// keeps its place; when the freeze ends it eases on from there (no jump).
    /// Pure.
    /// </summary>
    public sealed class ViewFollow
    {
        /// <summary>Metres in front of the head.</summary>
        public const float Distance = 1.0f;
        /// <summary>The lag's time constant: after this long about two thirds of the way are done.</summary>
        public const float LagSeconds = 0.3f;

        bool _placed;

        public Vec3 Position { get; private set; }
        /// <summary>The yaw in degrees, clockwise from +z seen from above (Unity's y rotation). -180..180.</summary>
        public float YawDegrees { get; private set; }
        public bool Placed => _placed;

        /// <summary>Forget the place: the next step puts it in the middle of the view at once.</summary>
        public void Reset() => _placed = false;

        /// <summary>Once a frame. `forward`: where the head looks. `frozen`: stay.</summary>
        public void Step(float dt, Vec3 head, Vec3 forward, bool frozen)
        {
            if (forward.Length < 1e-6f) return;
            var dir = forward.Normalized;
            var target = head + dir * Distance;
            // looking straight up or down gives no yaw: keep the one it has
            var flat = Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
            var yaw = flat > 1e-3 ? (float)(Math.Atan2(dir.X, dir.Z) * 180.0 / Math.PI) : YawDegrees;
            if (!_placed)
            {
                _placed = true;
                Position = target;
                YawDegrees = yaw;
                return;
            }
            if (frozen || dt <= 0f) return;
            var k = 1f - (float)Math.Exp(-dt / LagSeconds);
            Position = Position + (target - Position) * k;
            YawDegrees = Wrap(YawDegrees + Wrap(yaw - YawDegrees) * k);
        }

        /// <summary>An angle in -180..180.</summary>
        public static float Wrap(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }
    }
}
