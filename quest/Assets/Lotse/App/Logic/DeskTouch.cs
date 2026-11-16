#nullable enable

namespace Lotse.App
{
    /// <summary>One environment raycast hit: the point and the surface normal, world space.</summary>
    public readonly struct DeskHit
    {
        public readonly Vec3 Point;
        public readonly Vec3 Normal;

        public DeskHit(Vec3 point, Vec3 normal)
        {
            Point = point;
            Normal = normal;
        }
    }

    /// <summary>
    /// Is the index tip touching the desk? The desk is the plane DeskSurface found under the tip (never a ray
    /// hit at the tip itself, which the finger hides from the depth sensor). The tip touches when it is between
    /// 1 cm inside and 3 cm above that plane and the plane is roughly horizontal (normal y above 0.8). The touch
    /// is accepted once it has lasted 0.4 s without a break, at the tip's x/z on the plane.
    /// After Rearm (a re-place pressed with this very finger) nothing counts until the tip has moved more than
    /// 8 cm from where it was first seen, or 0.6 s have passed.
    /// </summary>
    public sealed class DeskTouch
    {
        public const float MinGap = -0.01f;
        public const float MaxGap = 0.03f;
        public const float MinNormalY = 0.8f;
        public const double HoldMs = 400;
        public const float RearmMeters = 0.08f;
        public const double RearmMs = 600;

        double _since = double.NaN;
        bool _rearming;
        double _rearmSince;
        Vec3? _rearmSpot;

        /// <summary>How far the current hold is, 0..1 (for the reticle).</summary>
        public float Progress { get; private set; }

        /// <summary>The desk point (tip x/z on the plane) and normal of the frame that accepted the touch.</summary>
        public DeskHit Accepted { get; private set; }

        public static bool IsTouch(Vec3 tip, DeskPlane plane)
        {
            var gap = tip.Y - plane.Y;
            return gap >= MinGap && gap <= MaxGap && plane.Normal.Normalized.Y > MinNormalY;
        }

        /// <summary>Ignore the finger where it is now (it just pressed "Neu platzieren", right above the desk).</summary>
        public void Rearm(double nowMs)
        {
            Reset();
            _rearming = true;
            _rearmSince = nowMs;
            _rearmSpot = null;
        }

        /// <summary>Feed one frame. `tip` null: no usable tip; `plane` null: no desk found. True on the accepting frame.</summary>
        public bool Update(double nowMs, Vec3? tip, DeskPlane? plane)
        {
            if (_rearming)
            {
                if (_rearmSpot == null && tip != null) _rearmSpot = tip;
                var moved = tip != null && _rearmSpot != null && Vec3.Distance(tip.Value, _rearmSpot.Value) > RearmMeters;
                if (moved || nowMs - _rearmSince >= RearmMs) _rearming = false;
                else
                {
                    Reset();
                    return false;
                }
            }

            if (tip == null || plane == null || !IsTouch(tip.Value, plane.Value))
            {
                Reset();
                return false;
            }
            if (double.IsNaN(_since)) _since = nowMs;
            var held = nowMs - _since;
            Progress = (float)System.Math.Min(1.0, held / HoldMs);
            if (held < HoldMs) return false;
            var t = tip.Value;
            var p = plane.Value;
            Accepted = new DeskHit(new Vec3(t.X, p.Y, t.Z), p.Normal.Normalized);
            _since = double.NaN;
            return true;
        }

        /// <summary>Forgets a hold in progress (not a rearm).</summary>
        public void Reset()
        {
            _since = double.NaN;
            Progress = 0f;
        }
    }
}
