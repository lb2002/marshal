#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>
    /// Whether a saved desk anchor can be used where the user is now: at most 1.5 m from the head and inside a
    /// 70° cone around the direction the user faces (yaw only, so looking down at the desk does not matter).
    /// One that is out of reach (another table, behind the user) would put cards and buttons where no hand gets;
    /// placement asks for a new tap instead.
    /// </summary>
    public static class AnchorReach
    {
        public const float MaxMeters = 1.5f;
        public const float ConeDegrees = 70f;

        public static bool InReach(Vec3 anchor, Vec3 head, Vec3 headForward)
        {
            var to = anchor - head;
            if (to.Length > MaxMeters) return false;
            var flatTo = new Vec3(to.X, 0f, to.Z);
            if (flatTo.Length < 0.05f) return true; // right below the head
            var facing = new Vec3(headForward.X, 0f, headForward.Z);
            if (facing.Length < 1e-3f) return true; // looking straight up or down: no yaw to judge by
            return Vec3.Dot(facing.Normalized, flatTo.Normalized) >= (float)Math.Cos(ConeDegrees * Math.PI / 180.0);
        }
    }
}
