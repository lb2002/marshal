#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>A poke button's face in world space: centre, unit right and up axes, half sizes (metres).</summary>
    public readonly struct PokeRect
    {
        public readonly Vec3 Center;
        public readonly Vec3 Right;
        public readonly Vec3 Up;
        public readonly float HalfWidth;
        public readonly float HalfHeight;

        public PokeRect(Vec3 center, Vec3 right, Vec3 up, float halfWidth, float halfHeight)
        {
            Center = center;
            Right = right;
            Up = up;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
        }
    }

    /// <summary>
    /// Whether two poke buttons are far enough apart, as the user sees them: button b is projected from the eye
    /// onto button a's plane, and must stay at least 2 cm clear of a there. A finger reaching for a button
    /// farther away passes through anything nearer that covers it, so "seen from the eye" is what counts.
    /// </summary>
    public static class PokeClearance
    {
        public const float Margin = 0.02f;

        public static bool Clear(Vec3 eye, in PokeRect a, in PokeRect b, float margin = Margin)
        {
            var normal = Vec3.Cross(a.Right, a.Up);
            var planeDistance = Vec3.Dot(normal, a.Center - eye);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            var behind = 0;
            for (var i = 0; i < 4; i++)
            {
                var sx = i == 0 || i == 3 ? -1f : 1f;
                var sy = i < 2 ? -1f : 1f;
                var corner = b.Center + b.Right * (sx * b.HalfWidth) + b.Up * (sy * b.HalfHeight);
                var ray = corner - eye;
                var along = Vec3.Dot(normal, ray);
                // the plane is crossed only in front of the eye
                if (Math.Abs(along) < 1e-6f || planeDistance / along <= 0f)
                {
                    behind++;
                    continue;
                }
                var hit = eye + ray * (planeDistance / along) - a.Center;
                var x = Vec3.Dot(hit, a.Right);
                var y = Vec3.Dot(hit, a.Up);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (behind == 4) return true;   // all of b is behind the eye
            if (behind > 0) return false;   // partly behind: no sound projection, so not clear
            var w = a.HalfWidth + margin;
            var h = a.HalfHeight + margin;
            return maxX < -w || minX > w || maxY < -h || minY > h;
        }
    }
}
