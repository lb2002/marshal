#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>Which side of the thing it docks to the workspace stands on (U5b fills it from a display).</summary>
    public enum DockSide { Right, Left, Below, Above }

    /// <summary>
    /// Where the workspace window stands and how big it is (U5a): its centre, its facing (Forward points from the user
    /// through the window, the way the app's panels face: their content looks along -Forward), its up, its height in
    /// metres and the side it docks on. The width is always the height times Aspect. Pure: the default pose below, or (U5b) a display's slot.
    /// </summary>
    public readonly struct WorkspaceDock
    {
        /// <summary>The size the window is built at (0.62 x 0.36 m); a dock scales it to its own height.</summary>
        public const float BuildHeight = 0.36f;
        /// <summary>The U5b size: the built size times 1.45 (the first device test: text too small in angle), 0.90 x 0.52 m.</summary>
        public const float TodayScale = 1.45f;
        public const float TodayHeight = BuildHeight * TodayScale;
        /// <summary>
        /// The default apparent size, height over distance (U10, from the first recorded device test): about 15 % larger
        /// than what scale 1.06 at 1.40 m gave (0.553 m / 1.40 m = 0.395).
        /// </summary>
        public const float DefaultApparent = 0.455f;
        /// <summary>The default: this far in front of the head where it was at placement time (measured flat; U10: 2.0 m, beyond arm's reach) ...</summary>
        public const float InFrontMeters = 2.0f;
        /// <summary>The default height at the default distance: 0.91 m (1.57 m wide).</summary>
        public const float DefaultHeight = DefaultApparent * InFrontMeters;
        public const float DefaultScale = DefaultHeight / BuildHeight;
        public const float Aspect = 1.72f;
        /// <summary>... with its centre this far below the eyes, its front facing the head exactly.</summary>
        public const float BelowEyesMeters = 0.05f;

        public readonly Vec3 Center;
        public readonly Vec3 Forward;
        public readonly Vec3 Up;
        public readonly float Height;
        public readonly DockSide Side;

        public WorkspaceDock(Vec3 center, Vec3 forward, Vec3 up, float height, DockSide side)
        {
            Center = center;
            Forward = forward;
            Up = up;
            Height = height;
            Side = side;
        }

        public float Width => Height * Aspect;

        /// <summary>The window's right (as the user sees it).</summary>
        public Vec3 Right => Vec3.Cross(Up, Forward).Normalized;

        /// <summary>The middle of the bottom edge.</summary>
        public Vec3 BottomCenter => Center - Up * (Height * 0.5f);

        /// <summary>The middle of the top edge (the notice pill stands on it).</summary>
        public Vec3 TopCenter => Center + Up * (Height * 0.5f);

        /// <summary>
        /// The default (U5b, from the first device test; with or without a desk anchor): InFrontMeters in front of the head
        /// (yaw only), BelowEyesMeters below the eyes, yawed so its front faces the head.
        /// </summary>
        public static WorkspaceDock InFrontOfHead(Vec3 head, Vec3 headForward, float height = DefaultHeight, float distance = InFrontMeters,
            float belowEyes = BelowEyesMeters)
        {
            var forward = Flat(headForward, new Vec3(0f, 0f, 1f));
            var flat = (float)Math.Sqrt(Math.Max(0.0, distance * distance - belowEyes * belowEyes));
            var at = head + forward * (distance == InFrontMeters && belowEyes == BelowEyesMeters ? InFrontMeters : flat);
            var center = new Vec3(at.X, head.Y - belowEyes, at.Z);
            return new WorkspaceDock(center, Facing(center, head, forward), Up0, height, DockSide.Right);
        }

        static readonly Vec3 Up0 = new Vec3(0f, 1f, 0f);

        /// <summary>From the head through the window, flat; `fallback` when the head stands right above or below it.</summary>
        static Vec3 Facing(Vec3 center, Vec3 head, Vec3 fallback) => Flat(center - head, fallback);

        static Vec3 Flat(Vec3 v, Vec3 fallback)
        {
            var f = new Vec3(v.X, 0f, v.Z);
            if (f.Length < 1e-4f) f = new Vec3(fallback.X, 0f, fallback.Z);
            return f.Length < 1e-4f ? new Vec3(0f, 0f, 1f) : f.Normalized;
        }

        public override string ToString() => $"centre {Center}, forward {Forward}, {Width:0.00} x {Height:0.00} m, {Side}";

        /// <summary>The yaw of Forward in degrees (0: +z, 90: +x), for the log.</summary>
        public float YawDegrees => (float)(Math.Atan2(Forward.X, Forward.Z) * 180.0 / Math.PI);
    }
}
