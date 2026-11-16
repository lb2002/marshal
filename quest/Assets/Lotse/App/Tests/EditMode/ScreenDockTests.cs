#nullable enable
using System;
using System.Collections.Generic;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U5b, the display's pure parts: the display from three tapped corners (an upright monitor, a laptop leaning back
    /// 25 degrees, a display seen from the side, every rejection), the four slots and their clamps, the unavailable slot,
    /// the highlight, the snap distance and the focus rule (angles, the nearest of two, none).
    /// </summary>
    public class ScreenDockTests
    {
        const float Eps = 1e-3f;
        static readonly Vec3 Head = new Vec3(0f, 1.2f, 0f);

        static void Near(Vec3 expected, Vec3 actual, string what) =>
            Assert.Less(Vec3.Distance(expected, actual), Eps, $"{what}: expected {expected}, was {actual}");

        /// <summary>The three corners a user taps on a display (centre, right, up as they see it).</summary>
        static (Vec3 tl, Vec3 tr, Vec3 br) Corners(Vec3 c, Vec3 right, Vec3 up, float w, float h) =>
            (c - right * (w * 0.5f) + up * (h * 0.5f), c + right * (w * 0.5f) + up * (h * 0.5f), c + right * (w * 0.5f) - up * (h * 0.5f));

        static ScreenRect Monitor()
        {
            var (tl, tr, br) = Corners(new Vec3(0f, 1.2f, 0.8f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 0.6f, 0.34f);
            Assert.AreEqual(ScreenRejection.None, ScreenDock.FromCorners(tl, tr, br, Head, out var s));
            return s;
        }

        // ---- the display from three corners ----

        [Test]
        public void AnUprightMonitorGivesItsCentreSizeAndFacing()
        {
            var s = Monitor();
            Assert.AreEqual(0.6f, s.Width, Eps);
            Assert.AreEqual(0.34f, s.Height, Eps);
            Near(new Vec3(0f, 1.2f, 0.8f), s.Center, "centre");
            Near(new Vec3(0f, 1f, 0f), s.Up, "up");
            Near(new Vec3(0f, 0f, 1f), s.Forward, "forward: from the user through the display");
            Near(new Vec3(1f, 0f, 0f), s.Right, "right as the user sees it");
            Near(new Vec3(0f, 0f, -1f), s.Normal, "the normal faces the head");
            Near(new Vec3(-0.3f, 1.37f, 0.8f), s.TopLeft, "top left");
        }

        [Test]
        public void ALaptopLeaningBackTwentyFiveDegreesKeepsItsTilt()
        {
            var a = 25f * (float)Math.PI / 180f;
            var up = new Vec3(0f, (float)Math.Cos(a), (float)Math.Sin(a)); // the top leans away from the user
            var c = new Vec3(0.1f, 0.95f, 0.55f);
            var (tl, tr, br) = Corners(c, new Vec3(1f, 0f, 0f), up, 0.31f, 0.2f);
            Assert.AreEqual(ScreenRejection.None, ScreenDock.FromCorners(tl, tr, br, Head, out var s));
            Assert.AreEqual(0.31f, s.Width, Eps);
            Assert.AreEqual(0.2f, s.Height, Eps);
            Near(c, s.Center, "centre");
            Near(up, s.Up, "up along the tilt");
            Assert.AreEqual(25f, ScreenDock.Degrees(s.Up, new Vec3(0f, 1f, 0f)), 0.05f, "25 degrees back");
            Assert.Greater(Vec3.Dot(Head - s.Center, s.Normal), 0f, "it faces the head");
            // a slot takes the display's rotation: the window leans back with it
            var slot = DockSlots.Slot(s, DockSide.Right);
            Near(up, slot.Up, "the slot's up");
            Near(s.Forward, slot.Forward, "the slot's facing");
        }

        [Test]
        public void ADisplaySeenFromTheSideFacesTheHeadAndKeepsItsOwnRight()
        {
            var yaw = 60f * (float)Math.PI / 180f;
            var right = new Vec3((float)Math.Cos(yaw), 0f, -(float)Math.Sin(yaw));
            var up = new Vec3(0f, 1f, 0f);
            var forward = Vec3.Cross(right, up);
            var c = new Vec3(0.5f, 1.25f, 0.6f);
            var head = c - forward * 0.5f + right * 0.6f; // well off to the side, still in front
            var (tl, tr, br) = Corners(c, right, up, 0.53f, 0.3f);
            Assert.AreEqual(ScreenRejection.None, ScreenDock.FromCorners(tl, tr, br, head, out var s));
            Assert.AreEqual(0.53f, s.Width, Eps);
            Assert.AreEqual(0.3f, s.Height, Eps);
            Near(forward, s.Forward, "forward");
            Near(right, s.Right, "right");
            Assert.Greater(Vec3.Dot(head - s.Center, s.Normal), 0f);
        }

        [Test]
        public void TheNormalTurnsToTheHeadWhenTheCornersAreTappedFromBehind()
        {
            var (tl, tr, br) = Corners(new Vec3(0f, 1.2f, 0.8f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 0.6f, 0.34f);
            var behind = new Vec3(0f, 1.2f, 1.6f);
            Assert.AreEqual(ScreenRejection.None, ScreenDock.FromCorners(tl, tr, br, behind, out var s));
            Assert.Greater(Vec3.Dot(behind - s.Center, s.Normal), 0f, "the normal faces the head that tapped");
            Near(Vec3.Cross(s.Up, s.Forward).Normalized, s.Right, "right stays the viewer's right");
        }

        [Test]
        public void TooSmallOrTooLargeIsNoDisplay()
        {
            var r = new Vec3(1f, 0f, 0f);
            var u = new Vec3(0f, 1f, 0f);
            var c = new Vec3(0f, 1.2f, 0.8f);
            var (a, b, d) = Corners(c, r, u, 0.14f, 0.3f);
            Assert.AreEqual(ScreenRejection.TooSmall, ScreenDock.FromCorners(a, b, d, Head, out _), "under 15 cm wide");
            (a, b, d) = Corners(c, r, u, 0.5f, 0.12f);
            Assert.AreEqual(ScreenRejection.TooSmall, ScreenDock.FromCorners(a, b, d, Head, out _), "under 15 cm high");
            (a, b, d) = Corners(c, r, u, 2.1f, 0.5f);
            Assert.AreEqual(ScreenRejection.TooLarge, ScreenDock.FromCorners(a, b, d, Head, out _), "over 2 m wide");
            (a, b, d) = Corners(c, r, u, 1.0f, 2.2f);
            Assert.AreEqual(ScreenRejection.TooLarge, ScreenDock.FromCorners(a, b, d, Head, out _), "over 2 m high");
            Assert.AreEqual(ScreenRejection.TooSmall, ScreenDock.FromCorners(c, c, c, Head, out _), "three taps on one spot");
            (a, b, d) = Corners(c, r, u, 0.15f, 0.15f);
            Assert.AreEqual(ScreenRejection.None, ScreenDock.FromCorners(a, b, d, Head, out _), "15 cm is enough");
        }

        [Test]
        public void ASkewedCornerIsNoDisplay()
        {
            var tl = new Vec3(-0.3f, 1.37f, 0.8f);
            var tr = new Vec3(0.3f, 1.37f, 0.8f);
            // the right edge leans 30 degrees inward: the corner is 60 degrees (outside 75..105)
            var a = 30f * (float)Math.PI / 180f;
            var br = tr + new Vec3(-(float)Math.Sin(a), -(float)Math.Cos(a), 0f) * 0.34f;
            Assert.AreEqual(ScreenRejection.NotSquare, ScreenDock.FromCorners(tl, tr, br, Head, out _));
            // 14 degrees off square (76 degrees) still counts
            a = 14f * (float)Math.PI / 180f;
            br = tr + new Vec3(-(float)Math.Sin(a), -(float)Math.Cos(a), 0f) * 0.34f;
            Assert.AreEqual(ScreenRejection.None, ScreenDock.FromCorners(tl, tr, br, Head, out _));
            // and 16 degrees outward (106) does not
            a = 16f * (float)Math.PI / 180f;
            br = tr + new Vec3((float)Math.Sin(a), -(float)Math.Cos(a), 0f) * 0.34f;
            Assert.AreEqual(ScreenRejection.NotSquare, ScreenDock.FromCorners(tl, tr, br, Head, out _));
        }

        [Test]
        public void ATopEdgeMoreThanTwentyDegreesOffHorizontalIsNoDisplay()
        {
            var c = new Vec3(0f, 1.2f, 0.8f);
            foreach (var (deg, ok) in new[] { (19f, true), (25f, false), (-30f, false) })
            {
                var a = deg * (float)Math.PI / 180f;
                var right = new Vec3((float)Math.Cos(a), (float)Math.Sin(a), 0f);
                var up = new Vec3(-(float)Math.Sin(a), (float)Math.Cos(a), 0f);
                var (tl, tr, br) = Corners(c, right, up, 0.6f, 0.34f);
                Assert.AreEqual(ok ? ScreenRejection.None : ScreenRejection.TopNotLevel, ScreenDock.FromCorners(tl, tr, br, Head, out _), deg + " degrees");
            }
        }

        // ---- the slots ----

        [Test]
        public void LeftAndRightSlotsTakeTheDisplaysHeightAlignTheirTopsAndKeepThreeCentimetres()
        {
            var s = Monitor();
            var right = DockSlots.Slot(s, DockSide.Right);
            Assert.AreEqual(0.34f, right.Height, Eps);
            Assert.AreEqual(0.34f * 1.72f, right.Width, Eps);
            Assert.AreEqual(s.TopRight.Y, right.TopCenter.Y, Eps, "top edges aligned");
            var gap = (right.Center.X - right.Width * 0.5f) - s.TopRight.X;
            Assert.AreEqual(0.03f, gap, Eps, "3 cm gap");
            Assert.AreEqual(DockSide.Right, right.Side);
            var left = DockSlots.Slot(s, DockSide.Left);
            Assert.AreEqual(0.03f, s.TopLeft.X - (left.Center.X + left.Width * 0.5f), Eps);
            Assert.AreEqual(s.TopLeft.Y, left.TopCenter.Y, Eps);
            Near(s.Forward, right.Forward, "the display's rotation");
        }

        [Test]
        public void SideSlotsClampTheirHeight()
        {
            var r = new Vec3(1f, 0f, 0f);
            var u = new Vec3(0f, 1f, 0f);
            var (a, b, c) = Corners(new Vec3(0f, 1.2f, 0.8f), r, u, 0.3f, 0.18f);
            ScreenDock.FromCorners(a, b, c, Head, out var small);
            Assert.AreEqual(0.24f, DockSlots.Slot(small, DockSide.Right).Height, Eps, "at least 24 cm");
            Assert.AreEqual(small.TopRight.Y, DockSlots.Slot(small, DockSide.Right).TopCenter.Y, Eps, "still top-aligned");
            (a, b, c) = Corners(new Vec3(0f, 1.4f, 1.2f), r, u, 1.4f, 0.8f);
            ScreenDock.FromCorners(a, b, c, Head, out var big);
            Assert.AreEqual(0.60f, DockSlots.Slot(big, DockSide.Left).Height, Eps, "at most 60 cm");
        }

        [Test]
        public void AboveAndBelowSlotsTakeTheDisplaysWidthAlignTheirLeftEdgesAndClamp()
        {
            var s = Monitor();
            var above = DockSlots.Slot(s, DockSide.Above);
            Assert.AreEqual(0.6f, above.Width, Eps);
            Assert.AreEqual(0.6f / 1.72f, above.Height, Eps);
            Assert.AreEqual(s.TopLeft.X, above.Center.X - above.Width * 0.5f, Eps, "left edges aligned");
            Assert.AreEqual(0.03f, above.BottomCenter.Y - s.TopLeft.Y, Eps, "3 cm above");
            var below = DockSlots.Slot(s, DockSide.Below);
            Assert.AreEqual(0.03f, s.BottomRight.Y - below.TopCenter.Y, Eps, "3 cm below");
            // a laptop (31 cm) is narrower than the minimum: 42 cm, left edges still aligned
            var (a, b, c) = Corners(new Vec3(0.1f, 0.95f, 0.55f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 0.31f, 0.2f);
            ScreenDock.FromCorners(a, b, c, Head, out var laptop);
            var over = DockSlots.Slot(laptop, DockSide.Above);
            Assert.AreEqual(0.42f, over.Width, Eps);
            Assert.AreEqual(laptop.TopLeft.X, over.Center.X - over.Width * 0.5f, Eps);
            (a, b, c) = Corners(new Vec3(0f, 1.4f, 1.2f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 1.4f, 0.8f);
            ScreenDock.FromCorners(a, b, c, Head, out var wide);
            Assert.AreEqual(1.03f, DockSlots.Slot(wide, DockSide.Below).Width, Eps, "at most 1.03 m");
        }

        [Test]
        public void ASlotWhoseBottomWouldBeBelowTheDeskIsUnavailable()
        {
            var s = Monitor(); // bottom edge at 1.03 m
            var below = DockSlots.Slot(s, DockSide.Below);
            Assert.IsTrue(DockSlots.Available(below, null), "no desk known: every slot");
            Assert.IsFalse(DockSlots.Available(below, 0.9f), "below the monitor reaches under a desk at 0.9 m");
            Assert.IsTrue(DockSlots.Available(DockSlots.Slot(s, DockSide.Right), 0.9f));
            Assert.IsNull(DockSlots.SnapTo(s, 0.9f, below.Center), "an unavailable slot is never snapped into");
            Assert.AreNotEqual(DockSide.Below, DockSlots.Pointed(s, 0.9f, Head, below.Center - Head), "nor highlighted");
            // a desk higher than the side slots' bottoms leaves only above
            Assert.AreEqual(DockSide.Above, DockSlots.Default(s, 1.1f));
            Assert.AreEqual(DockSide.Right, DockSlots.Default(s, 0.9f), "right by default");
        }

        [Test]
        public void TheSlotNearestToWhereTheUserPointsIsHighlighted()
        {
            var s = Monitor();
            Assert.AreEqual(DockSide.Right, DockSlots.Pointed(s, null, null, null), "nothing pointed: right");
            foreach (var side in DockSlots.Sides)
            {
                var slot = DockSlots.Slot(s, side);
                Assert.AreEqual(side, DockSlots.Pointed(s, null, Head, slot.Center - Head), side.ToString());
            }
        }

        [Test]
        public void AWindowReleasedWithinFifteenCentimetresSnapsIntoTheSlot()
        {
            var s = Monitor();
            var right = DockSlots.Slot(s, DockSide.Right);
            Assert.AreEqual(DockSide.Right, DockSlots.SnapTo(s, null, right.Center + new Vec3(0.1f, 0.1f, 0f)), "14 cm");
            Assert.IsNull(DockSlots.SnapTo(s, null, right.Center + new Vec3(0.16f, 0f, 0f)), "16 cm: it stays");
            Assert.IsTrue(DockSlots.Near(s, right.Center));
            Assert.IsFalse(DockSlots.Near(s, s.Center + new Vec3(2f, 0f, 0f)));
            Assert.IsTrue(DockSlots.RailOnRight(DockSide.Left), "left of the display: the rail on the right edge");
            Assert.IsFalse(DockSlots.RailOnRight(DockSide.Right));
            Assert.IsFalse(DockSlots.RailOnRight(DockSide.Above));
        }

        // ---- the focus rule ----

        [Test]
        public void TheFocusedWindowIsTheNearestWithinTwentyDegrees()
        {
            var fwd = new Vec3(0f, 0f, 1f);
            var main = (0, new Vec3(0f, 1.2f, 1.0f));
            var left = (1, new Vec3(-0.5f, 1.2f, 0.7f)); // about 35 degrees off
            var windows = new List<(int, Vec3)> { main, left };
            Assert.AreEqual(0, WindowFocus.Pick(Head, fwd, windows), "straight ahead");
            var toLeft = (left.Item2 - Head).Normalized;
            Assert.AreEqual(1, WindowFocus.Pick(Head, toLeft, windows), "looking at the left window");
            // 19 degrees off still counts, 21 does not
            Assert.AreEqual(0, WindowFocus.Pick(Head, Yawed(19f), new List<(int, Vec3)> { main }));
            Assert.IsNull(WindowFocus.Pick(Head, Yawed(21f), new List<(int, Vec3)> { main }));
            // two within 20 degrees: the nearer one
            var near = (2, new Vec3(0.05f, 1.2f, 0.5f));
            Assert.AreEqual(2, WindowFocus.Pick(Head, fwd, new List<(int, Vec3)> { main, near }), "the nearer of two");
            Assert.AreEqual(2, WindowFocus.Pick(Head, fwd, new List<(int, Vec3)> { near, main }), "whatever the order");
            // none
            Assert.IsNull(WindowFocus.Pick(Head, new Vec3(0f, 0f, -1f), windows), "looking away");
            Assert.IsNull(WindowFocus.Pick(Head, new Vec3(0f, 0f, 0f), windows), "no gaze");
            Assert.IsNull(WindowFocus.Pick(Head, fwd, new List<(int, Vec3)>()), "no window");
        }

        static Vec3 Yawed(float degrees)
        {
            var a = degrees * (float)Math.PI / 180f;
            return new Vec3((float)Math.Sin(a), 0f, (float)Math.Cos(a));
        }
    }
}
