#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>
    /// Where a hand ray hits a button's face or an agent sphere. Pure. A button only counts when the ray comes at
    /// its front (from the user's side), within its rectangle plus a margin.
    /// </summary>
    public static class RayPick
    {
        /// <summary>Whether the ray hits the rectangle's front within `maxDistance`; `distance` along the ray.</summary>
        public static bool HitsRect(Vec3 origin, Vec3 direction, in PokeRect rect, float margin, float maxDistance, out float distance)
        {
            distance = 0f;
            var d = direction.Normalized;
            // the face's normal points away from the user (a button's forward): the ray has to travel along it
            var normal = Vec3.Cross(rect.Right, rect.Up);
            var along = Vec3.Dot(d, normal);
            if (along < 1e-4f) return false;
            var t = Vec3.Dot(rect.Center - origin, normal) / along;
            if (t <= 0f || t > maxDistance) return false;
            var hit = origin + d * t - rect.Center;
            if (Math.Abs(Vec3.Dot(hit, rect.Right)) > rect.HalfWidth + margin) return false;
            if (Math.Abs(Vec3.Dot(hit, rect.Up)) > rect.HalfHeight + margin) return false;
            distance = t;
            return true;
        }

        /// <summary>Whether the ray hits the sphere within `maxDistance`; `distance` to where it enters.</summary>
        public static bool HitsSphere(Vec3 origin, Vec3 direction, Vec3 center, float radius, float maxDistance, out float distance)
        {
            distance = 0f;
            var d = direction.Normalized;
            if (d.Length < 0.5f) return false;
            var toCenter = center - origin;
            var closest = Vec3.Dot(toCenter, d); // along the ray to the point nearest the centre
            var miss2 = Vec3.Dot(toCenter, toCenter) - closest * closest;
            if (miss2 > radius * radius) return false;
            var half = (float)Math.Sqrt(radius * radius - miss2);
            var t = closest - half;
            if (t < 0f) t = closest + half; // the hand is inside the sphere
            if (t < 0f || t > maxDistance) return false;
            distance = t;
            return true;
        }
    }
}
