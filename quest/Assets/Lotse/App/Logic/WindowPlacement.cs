#nullable enable
using System;
using System.Globalization;

namespace Lotse.App
{
    /// <summary>
    /// Moving and sizing the window from the Menu (U8; U10 after the first recorded device test: the window stands about
    /// 2 m away, beyond arm's reach, and is used with the ray).
    /// - "Further" / "Closer" move it 0.25 m along the line from the head through its centre, between 0.8 m and 3.0 m (Meta: ray panels are comfortable from 0.8 to 3 m),
    ///   and scale it with the distance, so its apparent (angular) size stays what it was; its front yawed toward the head.
    /// - "Larger" / "Smaller" change the apparent size only, 10 % per press, its top centre fixed. The apparent size is
    ///   the height divided by the distance (0.455: a window 0.91 m high at 2 m), kept between 0.25 and 0.75.
    /// Pure.
    /// </summary>
    public static class WindowPlacement
    {
        public const float StepMeters = 0.25f;
        public const float MinDistance = 0.8f;
        public const float MaxDistance = 3.0f;
        public const float SizeStep = 1.10f;
        public const float MinApparent = 0.25f;
        public const float MaxApparent = 0.75f;
        public const float DefaultDistance = WorkspaceDock.InFrontMeters;
        public const float DefaultApparent = WorkspaceDock.DefaultApparent;

        /// <summary>The window's height at scale 1 (the U5b size, 0.522 m): the unit of the log's `scale`.</summary>
        public const float UnitHeight = WorkspaceDock.TodayHeight;

        /// <summary>The window's scale (1: the U5b size) for a height.</summary>
        public static float ScaleOf(float height) => height / UnitHeight;

        /// <summary>The window's apparent size from `head`: its height over its distance.</summary>
        public static float Apparent(in WorkspaceDock dock, Vec3 head) => dock.Height / Math.Max(1e-3f, Distance(dock, head));

        /// <summary>
        /// `delta` metres further from the head (negative: closer), clamped, scaled with the distance (the apparent size
        /// stays); faces the head by yaw; a free placement.
        /// </summary>
        public static WorkspaceDock Move(in WorkspaceDock dock, Vec3 head, float delta)
        {
            var dir = dock.Center - head;
            if (dir.Length < 1e-4f) dir = dock.Forward;
            var from = Math.Max(1e-3f, dir.Length);
            var distance = Clamp(from + delta, MinDistance, MaxDistance);
            var center = head + dir.Normalized * distance;
            return new WorkspaceDock(center, Facing(center, head, dock.Forward), Up, dock.Height * (distance / from), DockSide.Right);
        }

        /// <summary>The apparent size times `factor`, within its bounds; the top centre stays, it faces the way it did.</summary>
        public static WorkspaceDock Resize(in WorkspaceDock dock, Vec3 head, float factor)
        {
            var distance = Math.Max(1e-3f, Distance(dock, head));
            var height = Clamp(dock.Height / distance * factor, MinApparent, MaxApparent) * distance;
            var top = dock.TopCenter;
            return new WorkspaceDock(top - dock.Up * (height * 0.5f), dock.Forward, dock.Up, height, dock.Side);
        }

        /// <summary>A placement from elsewhere (a display's slot the window leaves) brought into the bounds of distance and size.</summary>
        public static WorkspaceDock Fit(in WorkspaceDock dock, Vec3 head) => Resize(Move(dock, head, 0f), head, 1f);

        /// <summary>The window's distance from the head (flat and vertical together).</summary>
        public static float Distance(in WorkspaceDock dock, Vec3 head) => (dock.Center - head).Length;

        static readonly Vec3 Up = new Vec3(0f, 1f, 0f);

        static Vec3 Facing(Vec3 center, Vec3 head, Vec3 fallback)
        {
            var f = new Vec3(center.X - head.X, 0f, center.Z - head.Z);
            if (f.Length < 1e-4f) f = new Vec3(fallback.X, 0f, fallback.Z);
            return f.Length < 1e-4f ? new Vec3(0f, 0f, 1f) : f.Normalized;
        }

        internal static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// Where the window was put, kept in prefs (U8, U10): its distance from the head, its height relative to the eyes and
    /// its apparent size. World positions do not survive a new app run on the Quest (the tracking origin moves), so a
    /// restored window stands at that distance in front of the head, as the default does. Anything unreadable counts as
    /// nothing. A record of before U10 (distance; below the eyes; scale) is taken over once: a distance under 1.0 m (the
    /// old default, "too near my face") resets to the default, any other keeps its distance and its apparent size.
    /// </summary>
    public sealed class WindowRecord
    {
        /// <summary>U10: distance; below the eyes; apparent size.</summary>
        public const string Key = "lotse.window2";
        /// <summary>Before U10: distance; below the eyes; scale.</summary>
        public const string OldKey = "lotse.window";
        /// <summary>An old record nearer than this resets to the default.</summary>
        public const float OldResetBelow = 1.0f;
        readonly IKeyValueStore _store;

        public WindowRecord(IKeyValueStore store) => _store = store;

        public void Save(float distance, float belowEyes, float apparent) =>
            _store.Set(Key, string.Join(";", F(distance), F(belowEyes), F(apparent)));

        public bool TryLoad(out float distance, out float belowEyes, out float apparent)
        {
            distance = WindowPlacement.DefaultDistance;
            belowEyes = WorkspaceDock.BelowEyesMeters;
            apparent = WindowPlacement.DefaultApparent;
            if (Read(_store.Get(Key), out var d, out var b, out var a))
            {
                if (a < WindowPlacement.MinApparent - 1e-3f || a > WindowPlacement.MaxApparent + 1e-3f) return false;
                distance = d;
                belowEyes = b;
                apparent = a;
                return true;
            }
            // a record of before U10, once
            var old = _store.Get(OldKey);
            if (string.IsNullOrEmpty(old)) return false;
            _store.Delete(OldKey);
            if (!Read(old, out d, out b, out var scale) || d < OldResetBelow || scale <= 0f) return false;
            distance = d;
            belowEyes = b;
            apparent = WindowPlacement.Clamp(scale * WindowPlacement.UnitHeight / d, WindowPlacement.MinApparent, WindowPlacement.MaxApparent);
            Save(distance, belowEyes, apparent);
            return true;
        }

        static bool Read(string? s, out float distance, out float belowEyes, out float third)
        {
            distance = belowEyes = third = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s!.Split(';');
            if (p.Length != 3 || !P(p[0], out distance) || !P(p[1], out belowEyes) || !P(p[2], out third)) return false;
            return distance >= 0.5f - 1e-3f && distance <= WindowPlacement.MaxDistance + 1e-3f && Math.Abs(belowEyes) <= 1.5f;
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static bool P(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
