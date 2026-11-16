using System;
using System.Collections;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U5b, docking through AppRoot: the three-tap setup with a fake fingertip (Back, Cancel, a rejected shape), the side
    /// picked by a ray click or a poke, the window standing next to the display like a second monitor (same height, top
    /// edges aligned, 3 cm gap, the display's rotation, the rail at the edge next to the display), the dock saved and
    /// restored, a lost anchor falling back with the banner, and the safety rule: the setup hides the workspace, cancels a
    /// running hold and nothing can be approved meanwhile.
    /// </summary>
    public class ScreenDockPlayTest
    {
        /// <summary>The display anchor, kept across two app runs like the device keeps it.</summary>
        public sealed class FakeScreenAnchors : IDeskAnchors
        {
            public Pose? Saved;
            public bool Lose;
            public int Creates;
            public bool HasSaved => Saved != null;

            public void LoadSaved(Action<Transform> done)
            {
                if (Lose || Saved == null)
                {
                    done(null);
                    return;
                }
                var go = new GameObject("FakeScreenAnchor");
                go.transform.SetPositionAndRotation(Saved.Value.position, Saved.Value.rotation);
                done(go.transform);
            }

            public Transform Create(Vector3 position, Quaternion rotation)
            {
                Creates++;
                Saved = new Pose(position, rotation);
                var go = new GameObject("FakeScreenAnchor");
                go.transform.SetPositionAndRotation(position, rotation);
                return go.transform;
            }
        }

        // a 0.6 x 0.34 m monitor 0.9 m in front of the head, upright
        static readonly Vector3 Centre = new Vector3(0f, 1.45f, 0.9f);
        const float W = 0.6f;
        const float H = 0.34f;
        static Vector3 TL => Centre + new Vector3(-W / 2, H / 2, 0f);
        static Vector3 TR => Centre + new Vector3(W / 2, H / 2, 0f);
        static Vector3 BR => Centre + new Vector3(W / 2, -H / 2, 0f);

        WorkspaceHarness _h;
        FakeScreenAnchors _anchors;

        [SetUp]
        public void SetUp()
        {
            _anchors = new FakeScreenAnchors();
            _h = new WorkspaceHarness { Configure = r => r.ScreenAnchors = _anchors };
            _h.SetUp();
        }

        [TearDown]
        public void TearDown()
        {
            _h.TearDown();
            foreach (var go in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name == "FakeScreenAnchor"))
                UnityEngine.Object.Destroy(go.gameObject);
        }

        IEnumerator OpenMenuAndSetUp()
        {
            // U8: from the header's Menu (the wrist menu is off)
            yield return _h.OpenMenu();
            Assert.AreEqual(Texts.ScreenSetup, _h.Root.Workspace.Menu.ScreenButton.Label);
            yield return _h.Poke(_h.Root.Workspace.Menu.ScreenButton);
            yield return Frames(2);
            Assert.IsTrue(_h.Root.Setup.Active, "the setup runs");
        }

        /// <summary>The right fingertip rests on `at` for a second, then moves 6 cm toward the user.</summary>
        IEnumerator Tap(Vector3 at)
        {
            for (var i = 0; i < 10; i++)
            {
                _h.Hands.Right = Hand(tip: at);
                yield return null;
            }
            _h.Hands.Right = Hand(tip: at + Vector3.back * 0.06f);
            yield return Frames(2);
        }

        IEnumerator ThreeTaps()
        {
            yield return Tap(TL);
            yield return Tap(TR);
            yield return Tap(BR);
            _h.Hands.Right = Hand();
            yield return Frames(2);
        }

        static ScreenRect Monitor => new ScreenRect(Ui.ToVec(Centre), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f), W, H);

        /// <summary>"Other side" on the dock band until the window stands on `side`.</summary>
        IEnumerator DockTo(DockSide side)
        {
            for (var i = 0; i < 4 && _h.Root.ScreenSide != side; i++)
            {
                Assert.IsNotNull(_h.Root.DockBand, "the dock band offers the other sides");
                yield return _h.RayPinch(_h.Root.DockBand.Buttons[0].transform.position);
                yield return Frames(2);
            }
            Assert.AreEqual(side, _h.Root.ScreenSide);
        }

        /// <summary>
        /// U10: the window stands on the rays from the seat through the display's in-plane slot, at the preferred distance
        /// (the display is 0.9 m away: 1.6 m), so from the seat it lines up with the display's top edge and height.
        /// </summary>
        void AssertSecondMonitor(DockSide side, float distance = DockSlots.PreferredMinDistance)
        {
            var dock = _h.Root.Dock;
            Assert.IsTrue(_h.Root.OnScreen);
            Assert.AreEqual(side, _h.Root.ScreenSide);
            var w = _h.Root.Workspace.transform;
            var head = Ui.ToVec(HeadPos);
            var plane = DockSlots.Slot(Monitor, side);
            Assert.AreEqual(H, plane.Height, 1e-3f, "the in-plane slot: the display's height");
            Assert.AreEqual(distance, WindowPlacement.Distance(dock, head), 2e-3f, "at the preferred distance from the seat");
            var apparent = WindowPlacement.Apparent(dock, head) / WindowPlacement.Apparent(plane, head);
            Assert.IsTrue(apparent > 1f - 1e-3f && apparent < 1.35f, "as high as the display looks at its edge: " + apparent);
            // (U11) its inner top corner on the ray through the slot's: it still starts at the display's edge and top
            var x = side == DockSide.Left ? 1f : -1f;
            var y = side == DockSide.Above ? -1f : 1f;
            var corner = (dock.Center + dock.Right * (x * dock.Width * 0.5f) + dock.Up * (y * dock.Height * 0.5f) - head).Normalized;
            var planeCorner = (plane.Center + plane.Right * (x * plane.Width * 0.5f) + plane.Up * (y * plane.Height * 0.5f) - head).Normalized;
            Assert.Greater(Vec3.Dot(corner, planeCorner), 1f - 1e-5f, "the aligned corner as seen from the seat");
            // (U11) pushed out it faces the seat: yaw only, upright
            var toWindow = Vector3.ProjectOnPlane(w.position - HeadPos, Vector3.up);
            Assert.Less(Vector3.Angle(w.forward, toWindow), 0.5f, "it faces the seat");
            Assert.Less(Vector3.Angle(w.up, Vector3.up), 0.5f);
            Assert.AreEqual(dock.Height / WorkspaceView.BaseHeight, w.localScale.y, 1e-3f, "scaled as a whole");
            Assert.AreEqual(side == DockSide.Left, _h.Root.Workspace.RailOnRight, "the rail hugs the display");
        }

        [UnityTest]
        public IEnumerator ThreeTapsDockTheWindowAtOnceAndABandOffersTheOtherSides()
        {
            yield return _h.StartDemo();
            yield return OpenMenuAndSetUp();
            Assert.IsFalse(_h.Root.Workspace.IsShown, "the workspace is hidden during the setup");
            Assert.AreEqual(Texts.ScreenCornerTopLeft, _h.Root.SetupView.PromptShown);
            // the ring fills during the dwell
            for (var i = 0; i < 4; i++)
            {
                _h.Hands.Right = Hand(tip: TL);
                yield return null;
            }
            Assert.Greater(_h.Root.SetupView.RingShown, 0.2f, "the ring fills");
            yield return Tap(TL);
            Assert.AreEqual(1, _h.Root.SetupView.DotsShown, "a dot stays at the corner");
            Assert.AreEqual(Texts.ScreenCornerTopRight, _h.Root.SetupView.PromptShown);
            yield return Tap(TR);
            yield return Tap(BR);
            _h.Hands.Right = Hand();
            yield return Frames(2);
            // U10: no chooser. Docked at once (the window stood behind the display: unclear, so right), a band says where
            Assert.IsFalse(_h.Root.Setup.Active, "done after the third corner");
            Assert.IsFalse(_h.Root.SetupView.SlotsShown, "no four-slot chooser");
            AssertSecondMonitor(DockSide.Right);
            var band = _h.Root.DockBand;
            Assert.IsNotNull(band, "the dock band");
            Assert.AreEqual("Docked right of your display", band.TextShown);
            CollectionAssert.AreEqual(new[] { Texts.OtherSide, Texts.Done }, band.Buttons.Select(x => x.Label).ToArray());
            Assert.IsTrue(_h.Root.Workspace.Asking, "it follows the band rule: nothing can be approved while it shows");
            // "Other side" goes through the available sides, docking at once
            yield return _h.RayPinch(band.Buttons[0].transform.position);
            yield return Frames(2);
            AssertSecondMonitor(DockSide.Left);
            Assert.AreEqual("Docked left of your display", _h.Root.DockBand.TextShown);
            yield return DockTo(DockSide.Right);
            AssertSecondMonitor(DockSide.Right);
            yield return _h.RayPinch(_h.Root.DockBand.Buttons[1].transform.position); // "Done"
            yield return Frames(2);
            Assert.IsNull(_h.Root.DockBand, "Done closes it");
            Assert.IsFalse(_h.Root.Workspace.Asking);
            Assert.IsTrue(_h.Root.Workspace.IsShown, "the workspace is back");
            Assert.AreEqual(1, _anchors.Creates, "one anchor at the display's centre");
            Assert.Less(Vector3.Distance(_anchors.Saved.Value.position, Centre), 1e-3f);
            Assert.IsNotNull(_h.Prefs.Get(ScreenRecord.Key), "size and side kept");
        }

        [UnityTest]
        public IEnumerator DockedLeftTheRailHugsTheDisplayAtTheWindowsRightEdge()
        {
            yield return _h.StartDemo();
            yield return OpenMenuAndSetUp();
            yield return ThreeTaps();
            yield return DockTo(DockSide.Left);
            AssertSecondMonitor(DockSide.Left);
            var view = _h.Root.Workspace;
            yield return _h.Poke(view.Header.CollapseButton);
            yield return Frames(4);
            Assert.IsTrue(view.Folded);
            var railRight = view.transform.TransformPoint(view.Rail.transform.localPosition + new Vector3(RailView.Width, 0f, 0f));
            var windowRight = view.transform.TransformPoint(new Vector3(-WorkspaceView.Left, 0f, 0f));
            Assert.AreEqual(windowRight.x, railRight.x, 1e-3f, "the rail at the window's right edge, next to the display");
        }

        [UnityTest]
        public IEnumerator BackRemovesTheLastCornerARejectedShapeAsksAgainAndCancelKeepsTheDock()
        {
            yield return _h.StartDemo();
            var before = _h.Root.Workspace.transform.position;
            yield return OpenMenuAndSetUp();
            yield return Tap(TL);
            yield return Tap(TL + new Vector3(0.3f, 0.2f, 0f)); // a wrong corner
            Assert.AreEqual(2, _h.Root.SetupView.DotsShown);
            yield return _h.Poke(_h.Root.SetupView.BackButton);
            yield return Frames(2);
            Assert.AreEqual(1, _h.Root.Setup.Points.Count, "Back removes the last point");
            Assert.AreEqual(1, _h.Root.SetupView.DotsShown);
            // a shape that is no display: 6 x 4 cm
            yield return Tap(TL + new Vector3(0.06f, 0f, 0f));
            yield return Tap(TL + new Vector3(0.06f, -0.04f, 0f));
            Assert.AreEqual(0, _h.Root.Setup.Points.Count, "dropped");
            StringAssert.StartsWith(Texts.ScreenRejected, _h.Root.SetupView.PromptShown, "asked again");
            yield return Tap(TL);
            yield return _h.Poke(_h.Root.SetupView.CancelButton);
            yield return Frames(2);
            Assert.IsFalse(_h.Root.Setup.Active, "Cancel leaves");
            Assert.IsFalse(_h.Root.OnScreen);
            Assert.Less(Vector3.Distance(before, _h.Root.Workspace.transform.position), 1e-3f, "the previous dock stays");
            Assert.IsTrue(_h.Root.Workspace.IsShown);
            Assert.AreEqual(0, _anchors.Creates);
        }

        [UnityTest]
        public IEnumerator AFingertipRestingAtAButtonIsNoCorner()
        {
            yield return _h.StartDemo();
            yield return OpenMenuAndSetUp();
            yield return Tap(TL);
            // the finger rests 1 cm in front of "Back" for more than the dwell, without pressing it
            var back = _h.Root.SetupView.BackButton.transform;
            for (var i = 0; i < 12; i++)
            {
                _h.Hands.Right = Hand(tip: back.TransformPoint(new Vector3(0f, 0f, -0.01f)));
                yield return null;
            }
            _h.Hands.Right = Hand();
            yield return Frames(2);
            Assert.AreEqual(1, _h.Root.Setup.Points.Count, "a finger at a button never taps a corner");
        }

        [UnityTest]
        public IEnumerator TheSetupCancelsARunningHoldAndNothingIsApprovableMeanwhile()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PinchHold(false, 4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold runs");
            // the wrist menu is not offered while a block waits: the setup starts from the menu's own action (as the menu would)
            var begin = typeof(AppRoot).GetMethod("BeginScreenSetup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            begin.Invoke(_h.Root, null);
            yield return null;
            Assert.IsNull(_h.Root.Session.Card, "no block during the setup");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the hold is cancelled");
            yield return Frames(30); // the same pinch held on
            Assert.AreEqual(0, _h.Decisions(), "nothing approved");
            _h.ReleaseHands();
            yield return _h.PinchHold(true, 30);
            Assert.AreEqual(0, _h.Decisions(), "a fresh hold during the setup approves nothing either");
            _h.ReleaseHands();
            yield return ThreeTaps();
            yield return Frames(3);
            Assert.IsFalse(_h.Root.Setup.Active);
            Assert.IsNotNull(_h.Root.Session.Card, "the block is back after the setup");
            Assert.AreEqual(0, _h.Decisions());
            // the dock band is up: still nothing can be approved, until it closes by itself after 10 s
            Assert.IsNotNull(_h.Root.DockBand);
            yield return _h.PinchHold(false, 20);
            Assert.AreEqual(0, _h.Decisions(), "a completed gesture approves nothing while the dock band shows");
            _h.ReleaseHands();
            yield return Frames(90);
            Assert.IsNull(_h.Root.DockBand, "it closes after 10 s without input");
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, _h.Decisions(), "then a fresh hold approves as usual");
        }

        [UnityTest]
        public IEnumerator TheDockIsSavedAndRestoredAndALostAnchorFallsBackWithTheBanner()
        {
            yield return _h.StartDemo();
            yield return OpenMenuAndSetUp();
            yield return ThreeTaps();
            yield return DockTo(DockSide.Left);
            var dock = _h.Root.Dock;
            var prefs = _h.Prefs.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
            _h.TearDown();
            yield return Frames(2);

            // a new run with the same prefs and the same device anchors: docked at once
            _h = new WorkspaceHarness { Configure = r => r.ScreenAnchors = _anchors };
            _h.SetUp();
            foreach (var kv in prefs) _h.Prefs.Values[kv.Key] = kv.Value;
            yield return _h.Start();
            Assert.IsTrue(_h.Root.OnScreen, "restored");
            Assert.Less(Vector3.Distance(Ui.ToVector(dock.Center), Ui.ToVector(_h.Root.Dock.Center)), 1e-3f, "where it stood");
            AssertSecondMonitor(DockSide.Left);
            _h.TearDown();
            yield return Frames(2);

            // the anchor is not found: the default pose and the banner
            _anchors.Lose = true;
            _h = new WorkspaceHarness { Configure = r => r.ScreenAnchors = _anchors };
            _h.SetUp();
            foreach (var kv in prefs) _h.Prefs.Values[kv.Key] = kv.Value;
            yield return _h.Start();
            Assert.IsFalse(_h.Root.OnScreen);
            Assert.AreEqual(Texts.ScreenLost, _h.Root.Banner.Line);
            Assert.AreEqual(WorkspaceDock.InFrontMeters, new Vector2(_h.Root.Dock.Center.X - HeadPos.x, _h.Root.Dock.Center.Z - HeadPos.z).magnitude, 1e-3f, "the default pose");
        }

        [UnityTest]
        public IEnumerator ADockSavedWithoutASeatByAnOlderBuildIsNotUsed()
        {
            yield return _h.StartDemo();
            yield return OpenMenuAndSetUp();
            yield return ThreeTaps();
            var prefs = _h.Prefs.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
            _h.TearDown();
            yield return Frames(2);
            // the record as an older build wrote it: width, height and side only
            var parts = prefs[ScreenRecord.Key].Split(';');
            Assert.AreEqual(7, parts.Length);
            prefs[ScreenRecord.Key] = string.Join(";", parts.Take(3));
            _h = new WorkspaceHarness { Configure = r => r.ScreenAnchors = _anchors };
            _h.SetUp();
            foreach (var kv in prefs) _h.Prefs.Values[kv.Key] = kv.Value;
            yield return _h.Start();
            yield return Frames(5);
            Assert.IsFalse(_h.Root.OnScreen, "not docked: in the display's plane it would stand at the display's distance");
            Assert.AreEqual(WorkspaceDock.InFrontMeters, new Vector2(_h.Root.Dock.Center.X - HeadPos.x, _h.Root.Dock.Center.Z - HeadPos.z).magnitude, 1e-3f,
                "the free window at its default distance");
            Assert.AreEqual(1, _h.Lotse.Count(l => l == Ev.Prefix + "dock phase=rejected reason=NoSeat"), "said once in the device log");
            Assert.AreNotEqual(Texts.ScreenLost, _h.Root.Banner.Line, "no banner: nothing was lost");
        }

        [UnityTest]
        public IEnumerator FurtherAndCloserMoveADockedWindowAlongTheRaysAndLargerUndocksOnce()
        {
            yield return _h.StartDemo();
            yield return OpenMenuAndSetUp();
            yield return ThreeTaps();
            yield return DockTo(DockSide.Left);
            yield return _h.RayPinch(_h.Root.DockBand.Buttons[1].transform.position);
            Assert.IsTrue(_h.Root.OnScreen);
            // U10: Further and Closer work while docked: along the rays from the seat, the docking stays
            yield return _h.OpenMenu();
            _h.Root.Workspace.Menu.FurtherButton.Click();
            yield return Frames(2);
            Assert.IsTrue(_h.Root.OnScreen, "still docked");
            AssertSecondMonitor(DockSide.Left, DockSlots.PreferredMinDistance + WindowPlacement.StepMeters);
            for (var i = 0; i < 12; i++) _h.Root.Workspace.Menu.CloserButton.Click();
            yield return Frames(2);
            AssertSecondMonitor(DockSide.Left, WindowPlacement.MinDistance);
            Assert.IsNull(_h.Root.Banner.Line == Texts.Undocked ? "undocked" : null, "no Undocked banner");
            // kept: a new run stands where it stood
            var docked = _h.Root.Dock;
            var prefs = _h.Prefs.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
            _h.TearDown();
            yield return Frames(2);
            _h = new WorkspaceHarness { Configure = r => r.ScreenAnchors = _anchors };
            _h.SetUp();
            foreach (var kv in prefs) _h.Prefs.Values[kv.Key] = kv.Value;
            yield return _h.StartDemo();
            Assert.IsTrue(_h.Root.OnScreen, "docked again");
            Assert.Less(Vector3.Distance(Ui.ToVector(docked.Center), Ui.ToVector(_h.Root.Dock.Center)), 2e-3f, "at the saved distance on the same rays");

            // Larger changes the size, which a docked window takes from its display: that ends the docking, said once
            yield return _h.OpenMenu();
            _h.Root.Workspace.Menu.LargerButton.Click();
            yield return Frames(2);
            Assert.IsFalse(_h.Root.OnScreen, "a free placement now");
            Assert.AreEqual(Texts.Undocked, _h.Root.Banner.Line);
            Assert.IsFalse(_h.Root.Workspace.RailOnRight);
            _h.Root.Workspace.Menu.FurtherButton.Click();
            yield return Frames(2);
            Assert.AreEqual(1, _h.Lotse.Count(l => l.Contains("undocked from the display")), "Undocked is said once");
            prefs = _h.Prefs.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
            var expected = WindowPlacement.Distance(_h.Root.Dock, Ui.ToVec(HeadPos));
            _h.TearDown();
            yield return Frames(2);

            _h = new WorkspaceHarness { Configure = r => r.ScreenAnchors = _anchors };
            _h.SetUp();
            foreach (var kv in prefs) _h.Prefs.Values[kv.Key] = kv.Value;
            yield return _h.Start();
            Assert.IsFalse(_h.Root.OnScreen, "not docked again");
            Assert.AreEqual(expected, WindowPlacement.Distance(_h.Root.Dock, Ui.ToVec(HeadPos)), 2e-3f, "at the saved distance");
        }
    }
}
