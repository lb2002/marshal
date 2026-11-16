#nullable enable
using System.Collections.Generic;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U8, U10: "Further" / "Closer" / "Larger" / "Smaller" from the Menu (the apparent size constant while moving), their
    /// bounds, the facing, the prefs and their migration; a docked window on the rays from the seat; the side taken at
    /// once and "Other side"; the design system's angles and contrasts at the default distance.
    /// </summary>
    public class WindowPlacementTests
    {
        const float Eps = 1e-3f;
        static readonly Vec3 Head = new Vec3(0f, 1.6f, 0f);

        static WorkspaceDock At(float distance, float height = WorkspaceDock.DefaultHeight) =>
            WorkspaceDock.InFrontOfHead(Head, new Vec3(0f, 0f, 1f), height, distance, 0f);

        sealed class Store : IKeyValueStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => Values[key] = value;
            public void Delete(string key) => Values.Remove(key);
        }

        // ---- free placement ----

        [Test]
        public void TheDefaultIsTwoMetresAwayAndFifteenPercentLargerInAngleThanTheFirstDeviceTest()
        {
            Assert.AreEqual(2.0f, WorkspaceDock.InFrontMeters, Eps);
            Assert.AreEqual(0.91f, WorkspaceDock.DefaultHeight, Eps);
            var tested = 1.06f * WindowPlacement.UnitHeight / 1.40f; // scale 1.06 at 1.40 m
            Assert.AreEqual(1.15f, WindowPlacement.DefaultApparent / tested, 0.01f, "about 15 % larger in apparent size");
            var d = WorkspaceDock.InFrontOfHead(Head, new Vec3(0f, 0f, 1f));
            Assert.AreEqual(WindowPlacement.DefaultApparent, WindowPlacement.Apparent(d, Head), 2e-3f);
            Assert.AreEqual(2.0f, WindowPlacement.Distance(d, Head), 2e-3f);
        }

        [Test]
        public void FurtherAndCloserMoveAQuarterMetreAndKeepTheApparentSize()
        {
            var d = At(2.0f);
            var a0 = WindowPlacement.Apparent(d, Head);
            var further = WindowPlacement.Move(d, Head, WindowPlacement.StepMeters);
            Assert.AreEqual(2.25f, WindowPlacement.Distance(further, Head), Eps);
            Assert.AreEqual(a0, WindowPlacement.Apparent(further, Head), 1e-4f, "scaled with the distance: the same angle");
            Assert.Greater(further.Height, d.Height);
            Assert.AreEqual(d.Height * 2.25f / 2.0f, further.Height, Eps);
            Assert.AreEqual(0f, further.Forward.Y, 1e-5f);
            Assert.Greater(Vec3.Dot(further.Forward, (further.Center - Head).Normalized), 0.999f, "its front faces the head");
            var closer = WindowPlacement.Move(d, Head, -WindowPlacement.StepMeters);
            Assert.AreEqual(1.75f, WindowPlacement.Distance(closer, Head), Eps);
            Assert.AreEqual(a0, WindowPlacement.Apparent(closer, Head), 1e-4f);
        }

        [Test]
        public void TheDistanceStaysBetweenPointEightAndThreeMetres()
        {
            Assert.AreEqual(0.8f, WindowPlacement.MinDistance, Eps, "ray panels are comfortable from 0.8 m (Meta)");
            Assert.AreEqual(3.0f, WindowPlacement.MaxDistance, Eps, "to 3 m");
            var far = At(2.0f);
            var a0 = WindowPlacement.Apparent(far, Head);
            for (var i = 0; i < 30; i++) far = WindowPlacement.Move(far, Head, WindowPlacement.StepMeters);
            Assert.AreEqual(WindowPlacement.MaxDistance, WindowPlacement.Distance(far, Head), Eps);
            Assert.AreEqual(a0, WindowPlacement.Apparent(far, Head), 1e-3f, "still the same angle at the far end");
            var near = At(2.0f);
            for (var i = 0; i < 30; i++) near = WindowPlacement.Move(near, Head, -WindowPlacement.StepMeters);
            Assert.AreEqual(WindowPlacement.MinDistance, WindowPlacement.Distance(near, Head), Eps);
            Assert.AreEqual(a0, WindowPlacement.Apparent(near, Head), 1e-3f);
        }

        [Test]
        public void LargerAndSmallerChangeTheApparentSizeOnlyByTenPercentWithinBounds()
        {
            var d = At(2.0f);
            var a0 = WindowPlacement.Apparent(d, Head);
            var larger = WindowPlacement.Resize(d, Head, WindowPlacement.SizeStep);
            Assert.AreEqual(d.Height * 1.10f, larger.Height, Eps);
            Assert.Less(Vec3.Distance(d.TopCenter, larger.TopCenter), 1e-4f, "its top centre stays");
            Assert.AreEqual(WindowPlacement.Distance(d, Head), WindowPlacement.Distance(larger, Head), 0.06f, "the distance is not what changes");
            var big = d;
            for (var i = 0; i < 30; i++) big = WindowPlacement.Resize(big, Head, WindowPlacement.SizeStep);
            Assert.AreEqual(WindowPlacement.MaxApparent, WindowPlacement.Apparent(big, Head), 0.08f, "at most 0.75 (the top stays, so the centre sinks a little)");
            var small = d;
            for (var i = 0; i < 30; i++) small = WindowPlacement.Resize(small, Head, 1f / WindowPlacement.SizeStep);
            Assert.AreEqual(WindowPlacement.MinApparent, WindowPlacement.Apparent(small, Head), 0.02f, "at least 0.25");
            Assert.Less(a0, WindowPlacement.MaxApparent);
            Assert.Greater(a0, WindowPlacement.MinApparent);
        }

        [Test]
        public void ASlotLeftByTheMenuComesIntoTheBounds()
        {
            var slot = new WorkspaceDock(new Vec3(0.5f, 1.4f, 0.5f), new Vec3(0.3f, 0.2f, 0.93f).Normalized, new Vec3(0f, 0.97f, -0.2f).Normalized, 0.12f, DockSide.Left);
            var free = WindowPlacement.Fit(slot, Head);
            Assert.GreaterOrEqual(WindowPlacement.Distance(free, Head), WindowPlacement.MinDistance - Eps);
            Assert.GreaterOrEqual(WindowPlacement.Apparent(free, Head), WindowPlacement.MinApparent - 0.02f);
            Assert.AreEqual(DockSide.Right, WindowPlacement.Move(slot, Head, 0.25f).Side, "a moved window is free");
            Assert.AreEqual(0f, WindowPlacement.Move(slot, Head, 0.25f).Forward.Y, 1e-5f, "upright again");
        }

        // ---- prefs ----

        [Test]
        public void TheWindowIsKeptAndRestored()
        {
            var store = new Store();
            var r = new WindowRecord(store);
            Assert.IsFalse(r.TryLoad(out var d0, out _, out var a0), "nothing saved: the default");
            Assert.AreEqual(WindowPlacement.DefaultDistance, d0, Eps);
            Assert.AreEqual(WindowPlacement.DefaultApparent, a0, Eps);
            r.Save(2.5f, 0.12f, 0.5f);
            Assert.IsTrue(new WindowRecord(store).TryLoad(out var d, out var b, out var a));
            Assert.AreEqual(2.5f, d, Eps);
            Assert.AreEqual(0.12f, b, Eps);
            Assert.AreEqual(0.5f, a, Eps);
            foreach (var bad in new[] { "", "x", "1;2", "2;0;9", "2;0;0.01", "9;0;0.4", "NaN;0;0.4", "2;7;0.4" })
            {
                store.Set(WindowRecord.Key, bad);
                Assert.IsFalse(new WindowRecord(store).TryLoad(out _, out _, out _), "\"" + bad + "\" is not a window");
            }
        }

        [Test]
        public void ARecordOfBeforeU10IsTakenOverOnceAndANearOneResets()
        {
            // 0.75 m at scale 1.15, the old default region: "too near my face" -> the new default, once
            var store = new Store();
            store.Set(WindowRecord.OldKey, "0.75;0.05;1.15");
            Assert.IsFalse(new WindowRecord(store).TryLoad(out var d, out _, out var a), "under 1.0 m: reset to the default");
            Assert.AreEqual(WindowPlacement.DefaultDistance, d, Eps);
            Assert.AreEqual(WindowPlacement.DefaultApparent, a, Eps);
            Assert.IsNull(store.Get(WindowRecord.OldKey), "once: the old record is gone");

            // 1.40 m at scale 1.06 (what the device test used): its distance and its apparent size are kept
            store.Set(WindowRecord.OldKey, "1.4;0.05;1.06");
            Assert.IsTrue(new WindowRecord(store).TryLoad(out d, out var b, out a));
            Assert.AreEqual(1.4f, d, Eps);
            Assert.AreEqual(0.05f, b, Eps);
            Assert.AreEqual(1.06f * WindowPlacement.UnitHeight / 1.4f, a, Eps);
            Assert.IsNull(store.Get(WindowRecord.OldKey));
            Assert.IsTrue(new WindowRecord(store).TryLoad(out d, out _, out _), "kept under the new key");
            Assert.AreEqual(1.4f, d, Eps);
        }

        [Test]
        public void ARestoredWindowStandsAtItsSavedDistanceAndSize()
        {
            var d = WorkspaceDock.InFrontOfHead(Head, new Vec3(1f, 0f, 0f), 0.5f * 1.6f, 1.6f, 0.12f);
            Assert.AreEqual(1.6f, WindowPlacement.Distance(d, Head), Eps);
            Assert.AreEqual(Head.Y - 0.12f, d.Center.Y, Eps);
            Assert.AreEqual(0.5f, WindowPlacement.Apparent(d, Head), Eps);
        }

        // ---- docked: on the rays from the seat ----

        static ScreenRect Monitor => new ScreenRect(new Vec3(0f, 1.3f, 0.75f), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f), 0.6f, 0.34f);

        /// <summary>The direction from the seat to a point.</summary>
        static Vec3 Ray(Vec3 p) => (p - Head).Normalized;

        /// <summary>A corner of a window: x, y in -1..1.</summary>
        static Vec3 Corner(WorkspaceDock w, float x, float y) => w.Center + w.Right * (x * w.Width * 0.5f) + w.Up * (y * w.Height * 0.5f);

        [Test]
        public void PushedOutADockedWindowFacesTheSeatUprightAndStillStartsAtTheDisplaysEdge()
        {
            var s = Monitor;
            foreach (var side in DockSlots.Sides)
            {
                var plane = DockSlots.Slot(s, side);
                foreach (var distance in new[] { 0.8f, 1.6f, 2.0f, 3.0f })
                {
                    var w = DockSlots.Projected(s, side, Head, distance);
                    Assert.AreEqual(distance, WindowPlacement.Distance(w, Head), 1e-3f, side + ": its centre at the preferred distance");
                    // upright, and turned (yaw only) to face the seat
                    Assert.AreEqual(1f, w.Up.Y, 1e-5f, side + ": upright");
                    Assert.AreEqual(0f, w.Forward.Y, 1e-5f, side + ": yaw only");
                    var flat = new Vec3(w.Center.X - Head.X, 0f, w.Center.Z - Head.Z).Normalized;
                    Assert.Greater(Vec3.Dot(w.Forward, flat), 1f - 1e-4f, side + " at " + distance + ": it faces the seat");
                    // the corner that lines up with the display stays on its ray: the window still starts at the display's edge
                    var x = side == DockSide.Left ? 1f : -1f;
                    var y = side == DockSide.Above ? -1f : 1f;
                    Assert.Greater(Vec3.Dot(Ray(Corner(plane, x, y)), Ray(Corner(w, x, y))), 1f - 1e-5f, side + " at " + distance + ": the aligned corner left its ray");
                    // beside an upright display the whole inner edge stays on its rays: there the window is exactly as high
                    // as the display looks (so, seen from the seat, it is a little larger in angle than the slanted slot was)
                    if (side == DockSide.Left || side == DockSide.Right)
                        Assert.Greater(Vec3.Dot(Ray(Corner(plane, x, -y)), Ray(Corner(w, x, -y))), 1f - 1e-5f, side + " at " + distance + ": the inner edge left its rays");
                    var apparent = WindowPlacement.Apparent(w, Head) / WindowPlacement.Apparent(plane, Head);
                    Assert.GreaterOrEqual(apparent, 1f - 1e-4f, side + ": never smaller in angle than the slot");
                    Assert.Less(apparent, 1.35f, side + ": and not much larger");
                    Assert.AreEqual(side, w.Side);
                }
            }
            // beside the display it is turned, not parallel
            var right = DockSlots.Projected(s, DockSide.Right, Head, 2.0f);
            Assert.Greater(ScreenDock.Degrees(right.Forward, s.Forward), 20f, "turned towards the seat");
            Assert.Greater(right.Forward.X, 0f);
        }

        [Test]
        public void InTheDisplaysOwnPlaneADockedWindowStaysParallelToIt()
        {
            // a laptop leaning back: the in-plane slot leans with it
            var up = new Vec3(0f, 0.94f, 0.342f).Normalized;
            var forward = new Vec3(0f, -0.342f, 0.94f).Normalized;
            var s = new ScreenRect(new Vec3(0f, 1.0f, 0.6f), up, forward, 0.34f, 0.22f);
            foreach (var side in DockSlots.Sides)
            {
                var plane = DockSlots.Slot(s, side);
                var w = DockSlots.Projected(s, side, Head, (plane.Center - Head).Length);
                if ((plane.Center - Head).Length < WindowPlacement.MinDistance) continue; // nearer than the minimum: pushed out
                Assert.Greater(Vec3.Dot(w.Forward, plane.Forward), 1f - 1e-6f, side + ": parallel to the display");
                Assert.Greater(Vec3.Dot(w.Up, plane.Up), 1f - 1e-6f);
                Assert.Less(Vec3.Distance(w.Center, plane.Center), 1e-5f);
            }
            var m = Monitor;
            var inPlane = DockSlots.Slot(m, DockSide.Right);
            var same = DockSlots.Projected(m, DockSide.Right, Head, (inPlane.Center - Head).Length + 0.01f);
            Assert.Greater(Vec3.Dot(same.Forward, m.Forward), 1f - 1e-6f, "within 2 cm of the plane: parallel");
            var pushed = DockSlots.Projected(m, DockSide.Right, Head, (inPlane.Center - Head).Length + 0.25f);
            Assert.Less(Vec3.Dot(pushed.Forward, m.Forward), 0.99f, "one step out: turned");
        }

        [Test]
        public void AWindowWhoseBottomEdgeWouldSinkBelowTheDeskIsLiftedJustEnoughOnTheSameAzimuth()
        {
            var s = Monitor;
            var free = DockSlots.Projected(s, DockSide.Right, Head, 2.0f);
            var deskY = DockSlots.BottomY(free) + 0.3f; // the desk 30 cm above where the bottom edge would be
            var w = DockSlots.Projected(s, DockSide.Right, Head, 2.0f, deskY);
            Assert.AreEqual(deskY, DockSlots.BottomY(w), 1e-4f, "its bottom edge on the desk surface, no higher");
            Assert.AreEqual(2.0f, WindowPlacement.Distance(w, Head), 1e-3f, "still at its distance");
            var a = new Vec3(free.Center.X - Head.X, 0f, free.Center.Z - Head.Z).Normalized;
            var b = new Vec3(w.Center.X - Head.X, 0f, w.Center.Z - Head.Z).Normalized;
            Assert.Greater(Vec3.Dot(a, b), 1f - 1e-6f, "the azimuth is kept");
            Assert.AreEqual(free.Height, w.Height, 1e-6f);
            Assert.Greater(Vec3.Dot(free.Forward, w.Forward), 1f - 1e-6f);
            // a desk below the bottom edge changes nothing
            var low = DockSlots.Projected(s, DockSide.Right, Head, 2.0f, DockSlots.BottomY(free) - 0.1f);
            Assert.Less(Vec3.Distance(low.Center, free.Center), 1e-6f);
            // judged on the pushed-out pose: beside and above it is never refused, below it is when it would have to be lifted
            var under = DockSlots.BottomY(DockSlots.Slot(s, DockSide.Right)) - 0.01f; // in the plane the slot is clear of this desk
            Assert.IsTrue(DockSlots.Available(DockSlots.Slot(s, DockSide.Right), under));
            Assert.Less(DockSlots.BottomY(DockSlots.Pose(s, DockSide.Right, Head, 2.0f)), under, "pushed out it would sink");
            Assert.IsTrue(DockSlots.Usable(s, DockSide.Right, under, Head, 2.0f), "lifted, not refused");
            Assert.IsTrue(DockSlots.Usable(s, DockSide.Left, under, Head, 2.0f));
            Assert.IsTrue(DockSlots.Usable(s, DockSide.Above, under, Head, 2.0f));
            Assert.IsFalse(DockSlots.Usable(s, DockSide.Below, under, Head, 2.0f), "below: lifting it would cover the display");
            Assert.IsTrue(DockSlots.Usable(s, DockSide.Below, null, Head, 2.0f));
            Assert.AreEqual(DockSide.Left, DockSlots.Next(s, under, DockSide.Right, Head, 2.0f));
            Assert.AreEqual(DockSide.Right, DockSlots.Next(s, under, DockSide.Above, Head, 2.0f), "below is skipped");
        }

        [Test]
        public void ADockedWindowIsNeverNearerThanPointEightMetresAndDefaultsToAtLeastOnePointSix()
        {
            var s = Monitor; // 0.75 m in front of the seat, a little below the eyes
            Assert.AreEqual(1.6f, DockSlots.DefaultDistance(s, Head), Eps, "the display is nearer than 1.6 m: 1.6 m");
            var far = new ScreenRect(new Vec3(0f, 1.3f, 2.4f), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f), 1.2f, 0.7f);
            Assert.AreEqual((far.Center - Head).Length, DockSlots.DefaultDistance(far, Head), Eps, "a display further away: its own distance");
            Assert.AreEqual(WindowPlacement.MinDistance, WindowPlacement.Distance(DockSlots.Projected(s, DockSide.Right, Head, 0.2f), Head), Eps);
            Assert.AreEqual(WindowPlacement.MaxDistance, WindowPlacement.Distance(DockSlots.Projected(s, DockSide.Right, Head, 9f), Head), Eps);
        }

        [Test]
        public void TheSideIsWhereTheWindowStandsAsSeenFromTheSeatRightWhenUnclearNeverUnavailable()
        {
            var s = Monitor;
            // the window 2 m away: what counts is where its ray meets the display's plane
            Assert.AreEqual(DockSide.Right, DockSlots.SideOf(s, null, Head, new Vec3(2.0f, 1.3f, 2.0f)));
            Assert.AreEqual(DockSide.Left, DockSlots.SideOf(s, null, Head, new Vec3(-2.0f, 1.3f, 2.0f)));
            Assert.AreEqual(DockSide.Above, DockSlots.SideOf(s, null, Head, new Vec3(0f, 2.6f, 2.0f)));
            Assert.AreEqual(DockSide.Below, DockSlots.SideOf(s, null, Head, new Vec3(0f, 0.2f, 2.0f)));
            Assert.AreEqual(DockSide.Right, DockSlots.SideOf(s, null, Head, new Vec3(0f, 1.0f, 2.0f)), "behind the display itself: unclear, right");
            Assert.AreEqual(DockSide.Right, DockSlots.SideOf(s, null, Head, Head), "no direction: right");
            // a desk right under the display: "below" is unavailable, so it is never taken
            var deskY = 1.1f;
            Assert.IsFalse(DockSlots.Available(DockSlots.Slot(s, DockSide.Below), deskY));
            Assert.AreNotEqual(DockSide.Below, DockSlots.SideOf(s, deskY, Head, new Vec3(0f, 0.2f, 2.0f)));
        }

        [Test]
        public void OtherSideCyclesThroughTheAvailableSides()
        {
            var s = Monitor;
            var seen = new List<DockSide>();
            var side = DockSide.Right;
            for (var i = 0; i < 4; i++)
            {
                side = DockSlots.Next(s, null, side);
                seen.Add(side);
            }
            CollectionAssert.AreEqual(new[] { DockSide.Left, DockSide.Above, DockSide.Below, DockSide.Right }, seen);
            var deskY = 1.1f; // below is unavailable
            side = DockSide.Above;
            Assert.AreEqual(DockSide.Right, DockSlots.Next(s, deskY, side), "the unavailable side is skipped");
            // only one available: it stays
            var low = new ScreenRect(new Vec3(0f, 0.9f, 0.75f), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f), 0.6f, 0.34f);
            var only = DockSlots.Default(low, 0.95f);
            Assert.IsNotNull(only);
            Assert.AreEqual(only.Value, DockSlots.Next(low, 0.95f, only.Value), "no other side: it stays");
        }

        [Test]
        public void TheDockRecordKeepsTheSeatAndTheDistanceAndStillReadsAnOldOne()
        {
            var store = new Store();
            var r = new ScreenRecord(store);
            r.Save(0.6f, 0.34f, DockSide.Left, new Vec3(0.1f, 0.3f, -0.75f), 1.85f);
            Assert.IsTrue(new ScreenRecord(store).TryLoad(out var w, out var h, out var side, out var seat, out var distance));
            Assert.AreEqual(0.6f, w, Eps);
            Assert.AreEqual(0.34f, h, Eps);
            Assert.AreEqual(DockSide.Left, side);
            Assert.IsTrue(seat.HasValue);
            Assert.Less(Vec3.Distance(new Vec3(0.1f, 0.3f, -0.75f), seat!.Value), 1e-3f);
            Assert.AreEqual(1.85f, distance, Eps);
            store.Set(ScreenRecord.Key, "0.6;0.34;Right"); // before U10
            Assert.IsTrue(new ScreenRecord(store).TryLoad(out w, out h, out side, out seat, out distance));
            Assert.IsFalse(seat.HasValue, "no seat kept: the window stands in the display's plane as before");
            store.Set(ScreenRecord.Key, "0.6;0.34;Right;0;0;x;2");
            Assert.IsTrue(new ScreenRecord(store).TryLoad(out _, out _, out _, out seat, out _));
            Assert.IsFalse(seat.HasValue, "an unreadable seat counts as none");
        }

        // ---- the design system at the default distance (Meta's guidance) ----

        [Test]
        public void EveryRayTargetSubtendsAtLeastThreeDegreesAtTheDefaultDistance()
        {
            foreach (var (name, width, height) in UiMetrics.Controls)
            {
                var degrees = UiMetrics.HitDegrees(width, height);
                Assert.GreaterOrEqual(degrees, UiMetrics.MinHitDegrees, name + ": " + degrees.ToString("0.00") + " degrees");
            }
            Assert.AreEqual(3.11f, UiMetrics.HitDegrees(UiMetrics.ButtonWidth, UiMetrics.H), 0.01f, "43 mm of hit area on a 27 mm control (U13: buttons are about 50 units high)");
            Assert.Less(UiMetrics.Degrees(UiMetrics.H * UiMetrics.DefaultScale, WorkspaceDock.InFrontMeters), 3f, "the drawn control alone would be too small: the padding is needed");
            // a list row and a rail slot have no padding: they are 44 mm themselves
            Assert.GreaterOrEqual(UiMetrics.Degrees(UiMetrics.RowHeight * UiMetrics.DefaultScale, WorkspaceDock.InFrontMeters), UiMetrics.MinHitDegrees);
            // two padded neighbours meet in the middle of their gap: they never overlap
            Assert.AreEqual(UiMetrics.ControlGap, 2f * UiMetrics.HitPad, 1e-6f);
            Assert.GreaterOrEqual(UiMetrics.GapDegrees(UiMetrics.ControlGap), UiMetrics.MinGapDegrees, "a degree between neighbouring drawn edges");
        }

        [Test]
        public void TextIsLargeEnoughInAngleAtTheDefaultDistance()
        {
            Assert.GreaterOrEqual(UiMetrics.CapDegrees(UiMetrics.Small), UiMetrics.MinSmallCapDegrees, "Small: " + UiMetrics.CapDegrees(UiMetrics.Small));
            Assert.GreaterOrEqual(UiMetrics.CapDegrees(UiMetrics.Body), UiMetrics.MinBodyCapDegrees, "Body: " + UiMetrics.CapDegrees(UiMetrics.Body));
            Assert.Greater(UiMetrics.CapDegrees(UiMetrics.Title), UiMetrics.CapDegrees(UiMetrics.Body));
            Assert.AreEqual(0.432f, UiMetrics.CapDegrees(UiMetrics.Small), 0.005f); // U13: 16, 19 and 25 units
            Assert.AreEqual(0.514f, UiMetrics.CapDegrees(UiMetrics.Body), 0.005f);
            Assert.AreEqual(0.674f, UiMetrics.CapDegrees(UiMetrics.Title), 0.005f);
        }

        [Test]
        public void TheContrastsHoldAgainstThePanelAndTheCard()
        {
            double C(Rgb a, Rgb b) => Look.Contrast(a, b);
            Assert.AreEqual(21.0, C(new Rgb(1f, 1f, 1f), new Rgb(0f, 0f, 0f)), 0.01, "the formula: white on black");
            // primary text: at least 15:1 against the panel (and clearly readable on a card)
            Assert.GreaterOrEqual(C(Look.TextPrimary, Look.Graphite), 15.0);
            Assert.GreaterOrEqual(C(Look.TextPrimary, Look.Card), 12.0);
            // muted text and small captions (they are muted, red or green): at least 7:1 on the panel and on a card
            foreach (var (name, text) in new[] { ("muted", Look.TextSecondary), ("mono", Look.TextMono), ("red text", Look.RedText) })
            {
                Assert.GreaterOrEqual(C(text, Look.Graphite), 7.0, name + " on the panel");
                Assert.GreaterOrEqual(C(text, Look.Card), 7.0, name + " on a card");
            }
            Assert.GreaterOrEqual(C(Look.TextMono, Look.Well), 7.0, "a command in its inset");
            // controls: their label on them, a disabled one still at least 3:1
            Assert.GreaterOrEqual(C(Look.TextPrimary, Look.Control), 7.0);
            Assert.GreaterOrEqual(C(Look.TextDisabled, Look.ControlOff), 3.0);
            Assert.GreaterOrEqual(C(Look.TextDisabled, Look.Graphite), 3.0);
            Assert.GreaterOrEqual(C(Look.OnPrimary, Look.TextPrimary), 7.0, "dark on the primary button");
            Assert.GreaterOrEqual(C(Look.OnPrimary, Look.Red), 4.5, "dark on a red chip");
            Assert.GreaterOrEqual(C(Look.OnPrimary, Look.Amber), 4.5, "dark on the stop bar (U13: there is no amber; halt-all uses the stop red)");
            // U13 (the approved palette: no cards, no borders): the raised fill is lighter than the asphalt; the faint
            // marks and dashes (the way ahead) are visible on both; a quiet button is lighter than the asphalt
            Assert.Greater(Look.Luminance(Look.Card), Look.Luminance(Look.Graphite));
            Assert.GreaterOrEqual(C(Look.Hairline, Look.Graphite), 1.6);
            Assert.GreaterOrEqual(C(Look.Hairline, Look.Card), 1.4);
            Assert.GreaterOrEqual(C(Look.Control, Look.Graphite), 1.15);
            Assert.GreaterOrEqual(C(Look.Card, Look.Graphite), 1.1);
        }
    }
}
