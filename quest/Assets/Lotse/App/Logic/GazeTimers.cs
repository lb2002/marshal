#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>The angle between where the head points (the centre eye's forward; there is no eye tracking) and a target.</summary>
    public static class HeadAngle
    {
        /// <summary>Degrees between `forward` and the direction from `head` to `target` (180 for a degenerate input).</summary>
        public static float Degrees(Vec3 head, Vec3 forward, Vec3 target)
        {
            var to = target - head;
            var a = forward.Length;
            var b = to.Length;
            if (a < 1e-6f || b < 1e-6f) return 180f;
            var cos = Vec3.Dot(forward, to) / (a * b);
            if (cos > 1f) cos = 1f;
            if (cos < -1f) cos = -1f;
            return (float)(Math.Acos(cos) * 180.0 / Math.PI);
        }
    }

    /// <summary>
    /// When the window folds into the rail by itself (U6): after 20 s in which the head was not pointed at the window
    /// (more than 25° off its centre), unless meanwhile a draft is non-empty, a keyboard is open, a hold is in progress,
    /// or a ray or a poke touched the window: each of these starts the 20 s again. Off when the setting
    /// "Automatisch einklappen" is off, and while the window is not shown open. Pure, time and angles injected.
    /// </summary>
    public sealed class CollapseTimer
    {
        public const double AwayMs = 20000;
        public const float AwayDegrees = 25f;

        double _since = double.NaN;

        /// <summary>How long the head has been away without anything that keeps the window open (ms).</summary>
        public double AwayFor(double nowMs) => double.IsNaN(_since) ? 0 : nowMs - _since;

        /// <param name="active">The setting is on and the window is open and shown.</param>
        /// <param name="headDegrees">The head's angle off the window's centre.</param>
        /// <param name="keepOpen">A non-empty draft, an open keyboard, a hold in progress, or a ray or poke on the window now.</param>
        /// <returns>True once, when the window should collapse now.</returns>
        public bool Step(double nowMs, bool active, float headDegrees, bool keepOpen)
        {
            if (!active || keepOpen || headDegrees <= AwayDegrees)
            {
                _since = active ? nowMs : double.NaN;
                return false;
            }
            if (double.IsNaN(_since)) _since = nowMs;
            if (nowMs - _since < AwayMs) return false;
            _since = double.NaN;
            return true;
        }

        public void Reset() => _since = double.NaN;
    }
}
