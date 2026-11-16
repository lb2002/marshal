#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// When to capture the fallback anchor: once the head pose is tracked and has held still (two frames in a
    /// row each within 2 cm of the previous one), or after 3 s whatever the pose. And when to capture it
    /// again: if it ends up more than 1.5 m from the head (a recenter, a pose that was wrong at start).
    /// </summary>
    public sealed class AnchorWatch
    {
        public const float StableMeters = 0.02f;
        public const int StableFrames = 2;
        public const double TimeoutMs = 3000;
        public const float MaxDistance = 1.5f;

        double _start = double.NaN;
        bool _hasLast;
        Vec3 _last;
        int _stable;

        /// <summary>Feed one frame; true when the anchor may be captured now.</summary>
        public bool Ready(double nowMs, Vec3 head, bool tracked)
        {
            if (double.IsNaN(_start)) _start = nowMs;
            if (!tracked)
            {
                _hasLast = false;
                _stable = 0;
            }
            else
            {
                _stable = _hasLast && Vec3.Distance(head, _last) < StableMeters ? _stable + 1 : 0;
                _last = head;
                _hasLast = true;
            }
            return _stable >= StableFrames || nowMs - _start >= TimeoutMs;
        }

        public static bool NeedsRecapture(Vec3 anchor, Vec3 head) => Vec3.Distance(anchor, head) > MaxDistance;
    }
}
