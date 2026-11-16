#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>A horizontal-ish surface at height Y, with the normal facing the user's head.</summary>
    public readonly struct DeskPlane
    {
        public readonly float Y;
        public readonly Vec3 Normal;

        public DeskPlane(float y, Vec3 normal)
        {
            Y = y;
            Normal = normal;
        }
    }

    /// <summary>
    /// Finds the desk under the index tip without looking at the tip itself: the depth sensor sees the finger,
    /// so a ray at the tip hits the finger, not the desk. Eight rays go straight down from 5 cm above the tip:
    /// a ring of six 5 cm around it and two ahead of the fingertip (see RayOrigins).
    /// Hand parts (knuckles, curled fingers, the other hand) must never pass for the desk, so a hit is dropped
    /// when it lies above the tip (+0.5 cm) or within 3 cm of any tracked joint of either hand. Of what is left,
    /// at least three hits must lie within 1.5 cm of each other in height; the plane is their median height and
    /// component-wise median normal (normals facing away from the head flipped first). A stray hit through a
    /// gap is thereby ignored, while desk and keyboard mixed, or hand and desk mixed, give no plane at all.
    /// </summary>
    public static class DeskSurface
    {
        public const float RingRadius = 0.05f;
        public const float AheadNear = 0.03f;
        public const float AheadFar = 0.06f;
        /// <summary>A finger whose flat part is shorter than this (of its length) points straight down.</summary>
        const float MinFlatFinger = 0.1f;
        static readonly float[] RingAngles = { 30f, -30f, 90f, -90f, 150f, -150f };
        public const float RayStartAbove = 0.05f;
        public const float MaxAboveTip = 0.005f;
        public const float JointClearance = 0.03f;
        public const float MaxSpread = 0.015f;
        public const int MinHits = 3;

        /// <summary>
        /// The eight ray origins (rays point straight down): a ring of six 5 cm around the tip at 30, 90 and 150
        /// degrees to either side of the finger direction (none straight along or back down the finger), and two
        /// ahead of the fingertip along the finger, 3 and 6 cm out, where nothing of the hand is. The finger
        /// direction counts flat; a finger pointing straight down, or none known, looks away from the user.
        /// </summary>
        public static IReadOnlyList<Vec3> RayOrigins(Vec3 tip, Vec3 head, Vec3 finger)
        {
            var ahead = new Vec3(finger.X, 0f, finger.Z);
            if (finger.Length < 1e-6f || ahead.Length < MinFlatFinger * finger.Length)
                ahead = new Vec3(tip.X - head.X, 0f, tip.Z - head.Z);
            ahead = ahead.Length < 1e-6f ? new Vec3(0f, 0f, 1f) : ahead.Normalized;
            var right = new Vec3(ahead.Z, 0f, -ahead.X);
            var start = tip + new Vec3(0f, RayStartAbove, 0f);
            var origins = new Vec3[8];
            for (var i = 0; i < RingAngles.Length; i++)
            {
                var a = RingAngles[i] * Math.PI / 180.0;
                origins[i] = start + (ahead * (float)Math.Cos(a) + right * (float)Math.Sin(a)) * RingRadius;
            }
            origins[6] = start + ahead * AheadNear;
            origins[7] = start + ahead * AheadFar;
            return origins;
        }

        /// <summary>The normal turned to face the head (a plane seen from below reports it pointing down).</summary>
        public static Vec3 FacingHead(Vec3 normal, Vec3 point, Vec3 head) =>
            Vec3.Dot(normal, head - point) < 0f ? -normal : normal;

        public static bool TryPlane(Vec3 tip, Vec3 head, IReadOnlyList<DeskHit> hits, IReadOnlyList<Vec3> joints, out DeskPlane plane)
        {
            plane = default;
            var kept = new List<DeskHit>(hits.Count);
            foreach (var h in hits)
            {
                if (h.Point.Y > tip.Y + MaxAboveTip || NearAJoint(h.Point, joints)) continue;
                kept.Add(new DeskHit(h.Point, FacingHead(h.Normal.Normalized, h.Point, head)));
            }
            if (kept.Count < MinHits) return false;

            // the largest group within MaxSpread in height (the tightest one on a tie)
            kept.Sort((a, b) => a.Point.Y.CompareTo(b.Point.Y));
            int bestFrom = 0, bestCount = 0;
            var bestRange = float.MaxValue;
            for (var from = 0; from < kept.Count; from++)
            {
                var to = from;
                while (to + 1 < kept.Count && kept[to + 1].Point.Y - kept[from].Point.Y <= MaxSpread + 1e-6f) to++;
                var count = to - from + 1;
                var range = kept[to].Point.Y - kept[from].Point.Y;
                if (count > bestCount || (count == bestCount && range < bestRange))
                {
                    bestFrom = from;
                    bestCount = count;
                    bestRange = range;
                }
            }
            if (bestCount < MinHits) return false;

            var ys = new List<float>(bestCount);
            var nx = new List<float>(bestCount);
            var ny = new List<float>(bestCount);
            var nz = new List<float>(bestCount);
            for (var i = bestFrom; i < bestFrom + bestCount; i++)
            {
                var h = kept[i];
                ys.Add(h.Point.Y);
                nx.Add(h.Normal.X);
                ny.Add(h.Normal.Y);
                nz.Add(h.Normal.Z);
            }
            plane = new DeskPlane(Median(ys), new Vec3(Median(nx), Median(ny), Median(nz)).Normalized);
            return true;
        }

        static bool NearAJoint(Vec3 point, IReadOnlyList<Vec3> joints)
        {
            foreach (var j in joints)
                if (Vec3.Distance(point, j) < JointClearance) return true;
            return false;
        }

        static float Median(List<float> values)
        {
            values.Sort();
            var mid = values.Count / 2;
            return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5f;
        }
    }
}
