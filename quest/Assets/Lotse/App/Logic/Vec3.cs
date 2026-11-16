#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>A minimal 3D vector so the pure logic needs no engine types. Unity axes: x right, y up, z forward.</summary>
    public readonly struct Vec3
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>Unit vector, or zero for a (near) zero vector.</summary>
        public Vec3 Normalized
        {
            get
            {
                var l = Length;
                return l < 1e-6f ? new Vec3(0, 0, 0) : this * (1f / l);
            }
        }

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// <summary>Same formula as UnityEngine.Vector3.Cross.</summary>
        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }
}
