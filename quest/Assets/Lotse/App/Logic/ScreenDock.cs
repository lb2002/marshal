#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>
    /// A real display as the app knows it (U5b): its centre, its up (along its tilt), Forward (from the user through the
    /// display, the way the app's panels face) and its size in metres. Right is the user's right as they face it.
    /// </summary>
    public readonly struct ScreenRect
    {
        public readonly Vec3 Center;
        public readonly Vec3 Up;
        public readonly Vec3 Forward;
        public readonly float Width;
        public readonly float Height;

        public ScreenRect(Vec3 center, Vec3 up, Vec3 forward, float width, float height)
        {
            Center = center;
            Up = up;
            Forward = forward;
            Width = width;
            Height = height;
        }

        public Vec3 Right => Vec3.Cross(Up, Forward).Normalized;
        /// <summary>The normal facing the user (the opposite of Forward).</summary>
        public Vec3 Normal => -Forward;
        public Vec3 TopLeft => Center - Right * (Width * 0.5f) + Up * (Height * 0.5f);
        public Vec3 TopRight => Center + Right * (Width * 0.5f) + Up * (Height * 0.5f);
        public Vec3 BottomRight => Center + Right * (Width * 0.5f) - Up * (Height * 0.5f);
        public Vec3 BottomLeft => Center - Right * (Width * 0.5f) - Up * (Height * 0.5f);

        public override string ToString() => $"display {Width:0.00} x {Height:0.00} m at {Center}, up {Up}, forward {Forward}";
    }

    /// <summary>Why three tapped points are not taken as a display.</summary>
    public enum ScreenRejection { None, TooSmall, TooLarge, NotSquare, TopNotLevel }

    /// <summary>
    /// The display from three tapped corners (U5b): top-left, top-right, bottom-right, in that order. Three corners, not
    /// two: two diagonal corners do not give the tilt, and laptop screens lean back.
    /// - right = normalise(TR - TL); down = (BR - TR) without its part along right, normalised; width = |TR - TL|;
    ///   height = |(BR - TR) . down|; the normal right x up, turned to face the head where it was at setup time.
    /// - Rejected ("Das sieht nicht nach einem Bildschirm aus.") when the width or the height is under 0.15 m or over
    ///   2.0 m, the angle between the top edge and the right edge is outside 75 to 105 degrees, or the top edge is more
    ///   than 20 degrees off horizontal.
    /// Pure.
    /// </summary>
    public static class ScreenDock
    {
        public const float MinSize = 0.15f;
        public const float MaxSize = 2.0f;
        public const float MinCornerDegrees = 75f;
        public const float MaxCornerDegrees = 105f;
        public const float MaxTopTiltDegrees = 20f;

        /// <param name="tl">Top left, tapped first.</param>
        /// <param name="tr">Top right, tapped second.</param>
        /// <param name="br">Bottom right, tapped third.</param>
        /// <param name="head">Where the head was at setup time: the display faces it.</param>
        public static ScreenRejection FromCorners(Vec3 tl, Vec3 tr, Vec3 br, Vec3 head, out ScreenRect screen)
        {
            screen = default;
            var top = tr - tl;
            var side = br - tr;
            var width = top.Length;
            if (width < 1e-4f || side.Length < 1e-4f) return ScreenRejection.TooSmall;
            var right = top * (1f / width);
            var down = side - right * Vec3.Dot(side, right);
            if (down.Length < 1e-4f) return ScreenRejection.TooSmall;
            down = down.Normalized;
            var height = Math.Abs(Vec3.Dot(side, down));
            if (width < MinSize || height < MinSize) return ScreenRejection.TooSmall;
            if (width > MaxSize || height > MaxSize) return ScreenRejection.TooLarge;
            var corner = Degrees(top, side);
            if (corner < MinCornerDegrees || corner > MaxCornerDegrees) return ScreenRejection.NotSquare;
            var flat = new Vec3(top.X, 0f, top.Z);
            var tilt = flat.Length < 1e-6f ? 90f : Degrees(top, flat);
            if (tilt > MaxTopTiltDegrees) return ScreenRejection.TopNotLevel;

            var up = -down;
            var center = tl + right * (width * 0.5f) + down * (height * 0.5f);
            // right x up points away from a user who sees `right` on their right; turned so the normal faces the head
            var away = Vec3.Cross(right, up).Normalized;
            if (Vec3.Dot(head - center, away) > 0f) away = -away;
            screen = new ScreenRect(center, up, away, width, height);
            return ScreenRejection.None;
        }

        /// <summary>The angle between two directions in degrees.</summary>
        public static float Degrees(Vec3 a, Vec3 b)
        {
            var la = a.Length;
            var lb = b.Length;
            if (la < 1e-9f || lb < 1e-9f) return 0f;
            var cos = Vec3.Dot(a, b) / (la * lb);
            if (cos > 1f) cos = 1f;
            if (cos < -1f) cos = -1f;
            return (float)(Math.Acos(cos) * 180.0 / Math.PI);
        }
    }

    /// <summary>
    /// Where the workspace can stand next to a display (U5b), its four slots. The workspace takes the display's rotation.
    /// - Left and right: height = the display's height clamped to 0.24..0.60 m; width = height x 1.72; top edges aligned;
    ///   a 3 cm gap.
    /// - Above and below: width = the display's width clamped to 0.42..1.03 m; height = width / 1.72; left edges aligned;
    ///   a 3 cm gap.
    /// In the display's plane a slot whose bottom edge would be below the desk surface is unavailable; pushed out along
    /// the ray from the seat the window is lifted instead (see Projected, Usable). A window
    /// released within SnapMeters of an available slot's centre snaps into it. Pure.
    /// </summary>
    public static class DockSlots
    {
        public const float Gap = 0.03f;
        public const float SideMinHeight = 0.24f;
        public const float SideMaxHeight = 0.60f;
        public const float StackMinWidth = 0.42f;
        public const float StackMaxWidth = 1.03f;
        public const float SnapMeters = 0.15f;
        /// <summary>The slots' outlines show while a dragged window's centre is this near the display's centre.</summary>
        public const float NearMeters = 1.0f;

        public static readonly DockSide[] Sides = { DockSide.Left, DockSide.Right, DockSide.Above, DockSide.Below };

        public static WorkspaceDock Slot(in ScreenRect s, DockSide side)
        {
            var right = s.Right;
            var up = s.Up;
            if (side == DockSide.Left || side == DockSide.Right)
            {
                var h = Clamp(s.Height, SideMinHeight, SideMaxHeight);
                var w = h * WorkspaceDock.Aspect;
                var sign = side == DockSide.Right ? 1f : -1f;
                var topCenter = s.Center + up * (s.Height * 0.5f);
                var center = topCenter + right * (sign * (s.Width * 0.5f + Gap + w * 0.5f)) - up * (h * 0.5f);
                return new WorkspaceDock(center, s.Forward, up, h, side);
            }
            var width = Clamp(s.Width, StackMinWidth, StackMaxWidth);
            var height = width / WorkspaceDock.Aspect;
            var leftX = s.Center - right * (s.Width * 0.5f);
            var x = leftX + right * (width * 0.5f);
            var c = side == DockSide.Above
                ? x + up * (s.Height * 0.5f + Gap + height * 0.5f)
                : x - up * (s.Height * 0.5f + Gap + height * 0.5f);
            return new WorkspaceDock(c, s.Forward, up, height, side);
        }

        /// <summary>A docked window is never nearer than this unless its display is (U10: 0.75 m was "too near my face").</summary>
        public const float PreferredMinDistance = 1.6f;

        /// <summary>The default distance of a docked window from the seat: the display's own distance, at least 1.6 m.</summary>
        public static float DefaultDistance(in ScreenRect s, Vec3 head) => Math.Max((s.Center - head).Length, PreferredMinDistance);

        /// <summary>Within this of the display's own plane the window stays parallel to the display.</summary>
        public const float InPlaneMeters = 0.02f;

        /// <summary>
        /// The slot as seen from the seat, at another distance (U10, U11): the window no longer has to sit in the
        /// display's plane.
        /// - In the display's plane (its centre within 2 cm of the in-plane slot's distance) it is the slot itself,
        ///   parallel to the display.
        /// - Pushed out along the ray it turns to face `head` (where the head was at setup time): yaw only, upright. The
        ///   corner that lines up with the display (the inner top corner beside it, the lower left corner above it, the
        ///   upper left corner below it) stays on its ray from the seat, so the window still starts at the display's edge
        ///   and never covers it; its size grows with the distance; its centre is `distance` from the head.
        /// - When a desk is known and the bottom edge would sink below its surface, the window is lifted just enough,
        ///   on the same azimuth and at the same distance.
        /// </summary>
        public static WorkspaceDock Projected(in ScreenRect s, DockSide side, Vec3 head, float distance, float? deskY = null)
            => Lifted(Pose(s, side, head, distance), head, deskY);

        /// <summary>The pose before the desk is looked at.</summary>
        public static WorkspaceDock Pose(in ScreenRect s, DockSide side, Vec3 head, float distance)
        {
            var slot = Slot(s, side);
            var from = (slot.Center - head).Length;
            if (from < 1e-3f) return slot;
            var d = WindowPlacement.Clamp(distance, WindowPlacement.MinDistance, WindowPlacement.MaxDistance);
            if (Math.Abs(d - from) <= InPlaneMeters) return slot;
            // the corner that lines up with the display, and where the window's centre lies from it (in widths, heights)
            var sx = side == DockSide.Left ? 1f : -1f; // the corner is the window's right corner when it stands left
            var sy = side == DockSide.Above ? -1f : 1f; // its lower corner when it stands above
            var corner = slot.Center + slot.Right * (sx * slot.Width * 0.5f) + slot.Up * (sy * slot.Height * 0.5f);
            var up = new Vec3(0f, 1f, 0f);
            var toCentre = slot.Center - head;
            var v = toCentre;
            var forward = slot.Forward;
            for (var i = 0; i < 4; i++)
            {
                var flat = new Vec3(v.X, 0f, v.Z);
                if (flat.Length < 1e-4f) return new WorkspaceDock(head + toCentre * (d / from), slot.Forward, slot.Up, slot.Height * (d / from), side);
                forward = flat.Normalized;
                var right = Vec3.Cross(up, forward).Normalized;
                // per unit of scale: from the head to the corner, then half a window to its centre
                v = (corner - head) - right * (sx * slot.Width * 0.5f) - up * (sy * slot.Height * 0.5f);
            }
            var k = d / Math.Max(1e-4f, v.Length);
            return new WorkspaceDock(head + v * k, forward, up, slot.Height * k, side);
        }

        /// <summary>The pose lifted so that its bottom edge is not below the desk (same azimuth, same distance from the seat).</summary>
        public static WorkspaceDock Lifted(in WorkspaceDock w, Vec3 head, float? deskY)
        {
            if (deskY == null) return w;
            var sink = deskY.Value - BottomY(w);
            if (sink <= 1e-4f) return w;
            var rel = w.Center - head;
            var d = rel.Length;
            var y = rel.Y + sink;
            var flat = new Vec3(rel.X, 0f, rel.Z);
            var upright = Math.Abs(w.Up.Y) > 0.999f;
            if (!upright || flat.Length < 1e-4f || Math.Abs(y) >= d)
                return new WorkspaceDock(w.Center + new Vec3(0f, sink, 0f), w.Forward, w.Up, w.Height, w.Side);
            var along = (float)Math.Sqrt(d * d - y * y);
            return new WorkspaceDock(head + flat.Normalized * along + new Vec3(0f, y, 0f), w.Forward, w.Up, w.Height, w.Side);
        }

        /// <summary>
        /// Whether the window can stand on `side` (U11): judged on the pose it would really have, pushed out along the
        /// ray. Beside and above the display it always can (a window whose bottom edge would sink below the desk is
        /// lifted). Below the display it cannot when it would have to be lifted: it would then cover the display.
        /// </summary>
        public static bool Usable(in ScreenRect s, DockSide side, float? deskY, Vec3 head, float distance)
            => side != DockSide.Below || Available(Pose(s, side, head, distance), deskY);

        /// <summary>
        /// The side the window stands on now, as seen from `head`: where the ray from the head through the window's centre
        /// meets the display's plane, relative to the display (U10: the dock is made at once, without a chooser). Right
        /// when it is unclear (in front of the display, no hit); never a slot that is unavailable.
        /// </summary>
        public static DockSide SideOf(in ScreenRect s, float? deskY, Vec3 head, Vec3 window, float? distance = null)
        {
            var dir = window - head;
            var along = Vec3.Dot(dir, s.Forward);
            var t = along > 1e-4f ? Vec3.Dot(s.Center - head, s.Forward) / along : -1f;
            var rel = t > 0f ? head + dir * t - s.Center : new Vec3(0f, 0f, 0f); // no hit: unclear
            var x = Vec3.Dot(rel, s.Right) / Math.Max(1e-3f, s.Width * 0.5f);
            var y = Vec3.Dot(rel, s.Up) / Math.Max(1e-3f, s.Height * 0.5f);
            DockSide? side = null;
            if (Math.Abs(x) > 1f || Math.Abs(y) > 1f) // beside, above or below the display itself
                side = Math.Abs(x) >= Math.Abs(y) ? (x > 0f ? DockSide.Right : DockSide.Left) : (y > 0f ? DockSide.Above : DockSide.Below);
            if (side == null || !Usable(s, side.Value, deskY, head, distance ?? DefaultDistance(s, head))) side = DockSide.Right;
            return side.Value;
        }

        /// <summary>The order "Other side" goes through.</summary>
        public static readonly DockSide[] Cycle = { DockSide.Right, DockSide.Left, DockSide.Above, DockSide.Below };

        /// <summary>
        /// The next side after `current` the window can stand on ("Other side"); `current` itself when there is no other.
        /// With the seat (`head`) and the distance it is judged on the pushed-out pose (Usable); without, in the display's plane.
        /// </summary>
        public static DockSide Next(in ScreenRect s, float? deskY, DockSide current, Vec3? head = null, float distance = 0f)
        {
            var i = Array.IndexOf(Cycle, current);
            for (var n = 1; n <= Cycle.Length; n++)
            {
                var side = Cycle[(i + n + Cycle.Length) % Cycle.Length];
                if (side == current) continue;
                if (head.HasValue ? Usable(s, side, deskY, head.Value, distance) : Available(Slot(s, side), deskY)) return side;
            }
            return current;
        }

        /// <summary>The lowest point of the window's bottom edge (its two bottom corners).</summary>
        public static float BottomY(in WorkspaceDock d)
        {
            var bottom = d.BottomCenter;
            var half = d.Right * (d.Width * 0.5f);
            return Math.Min((bottom - half).Y, (bottom + half).Y);
        }

        /// <summary>A slot is unavailable when a desk is known and the window's bottom edge would be below its surface.</summary>
        public static bool Available(in WorkspaceDock slot, float? deskY) => deskY == null || BottomY(slot) >= deskY.Value - 1e-4f;

        /// <summary>The available slot whose centre lies nearest to the ray (the one the user points at); null when none is.</summary>
        public static DockSide? Pointed(in ScreenRect s, float? deskY, Vec3? rayOrigin, Vec3? rayDirection)
        {
            if (rayOrigin == null || rayDirection == null || rayDirection.Value.Length < 1e-6f) return Default(s, deskY);
            var dir = rayDirection.Value.Normalized;
            DockSide? best = null;
            var bestD = float.MaxValue;
            foreach (var side in Sides)
            {
                var slot = Slot(s, side);
                if (!Available(slot, deskY)) continue;
                var rel = slot.Center - rayOrigin.Value;
                var along = Vec3.Dot(rel, dir);
                if (along < 0f) continue; // behind the hand
                var d = (rel - dir * along).Length;
                if (d < bestD)
                {
                    bestD = d;
                    best = side;
                }
            }
            return best ?? Default(s, deskY);
        }

        /// <summary>The highlight before the user points: right, else the first available slot (null: none is).</summary>
        public static DockSide? Default(in ScreenRect s, float? deskY)
        {
            if (Available(Slot(s, DockSide.Right), deskY)) return DockSide.Right;
            foreach (var side in Sides)
                if (Available(Slot(s, side), deskY)) return side;
            return null;
        }

        /// <summary>The available slot a window released with its centre at `center` snaps into, or null (it stays).</summary>
        public static DockSide? SnapTo(in ScreenRect s, float? deskY, Vec3 center)
        {
            DockSide? best = null;
            var bestD = SnapMeters;
            foreach (var side in Sides)
            {
                var slot = Slot(s, side);
                if (!Available(slot, deskY)) continue;
                var d = Vec3.Distance(slot.Center, center);
                if (d <= bestD)
                {
                    bestD = d;
                    best = side;
                }
            }
            return best;
        }

        /// <summary>A dragged window is near the display: its slots show as faint outlines.</summary>
        public static bool Near(in ScreenRect s, Vec3 center) => Vec3.Distance(s.Center, center) <= NearMeters + s.Width * 0.5f;

        /// <summary>The edge of the window the collapsed rail hugs: the right edge when it stands left of the display.</summary>
        public static bool RailOnRight(DockSide side) => side == DockSide.Left;

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// Which window a hold may act on (U5b, section 3): the one the head points at, within 20 degrees of the direction to
    /// its centre, the nearest one if several are. None: no window is focused, nothing can be approved. Pure.
    /// </summary>
    public static class WindowFocus
    {
        public const float MaxDegrees = 20f;

        /// <summary>The focused window's id, or null. `windows`: (id, centre) of every window that is on screen.</summary>
        public static int? Pick(Vec3 head, Vec3 forward, IReadOnlyList<(int id, Vec3 center)> windows)
        {
            if (forward.Length < 1e-6f) return null;
            int? best = null;
            var bestD = float.MaxValue;
            for (var i = 0; i < windows.Count; i++)
            {
                var (id, center) = windows[i];
                if (HeadAngle.Degrees(head, forward, center) >= MaxDegrees) continue;
                var d = Vec3.Distance(head, center);
                if (d < bestD)
                {
                    bestD = d;
                    best = id;
                }
            }
            return best;
        }
    }
}
