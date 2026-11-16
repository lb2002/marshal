using System.Collections;
using System.Linq;
using Lotse.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U8: the header's Menu (further, closer, larger, smaller, the display, place again, the settings, halt all agents),
    /// the window's distance and size kept in prefs, the click-versus-hold rule on its buttons, no menu during a hold,
    /// approvals unchanged while it is open, the wrist menu off, the header and the halted band inside the panel.
    /// </summary>
    public class MenuPlayTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
        }

        [TearDown]
        public void TearDown()
        {
            _h.TearDown();
        }

        WindowMenu Menu => _h.Root.Workspace.Menu;
        float Distance => WindowPlacement.Distance(_h.Root.Dock, Ui.ToVec(HeadPos));
        float Scale => WindowPlacement.ScaleOf(_h.Root.Dock.Height);

        float Apparent => WindowPlacement.Apparent(_h.Root.Dock, Ui.ToVec(HeadPos));

        [UnityTest]
        public IEnumerator TheWindowStartsTwoMetresAwayAndTheMenuMovesItAtTheSameApparentSizeAndSizesIt()
        {
            yield return _h.GoLive();
            Assert.AreEqual(WindowPlacement.DefaultApparent, Apparent, 2e-3f);
            Assert.AreEqual(WorkspaceDock.InFrontMeters, new Vector2(_h.Root.Dock.Center.X - HeadPos.x, _h.Root.Dock.Center.Z - HeadPos.z).magnitude, 1e-3f);
            var d0 = Distance;
            var a0 = Apparent;
            yield return _h.OpenMenu();
            yield return _h.RayPinch(Menu.FurtherButton.transform.position);
            Assert.AreEqual(d0 + 0.25f, Distance, 2e-3f, "further by 0.25 m");
            Assert.AreEqual(a0, Apparent, 1e-3f, "the same apparent size: it grew with the distance");
            var w = _h.Root.Workspace.transform;
            var toWindow = Vector3.ProjectOnPlane(w.position - HeadPos, Vector3.up).normalized;
            Assert.Less(Vector3.Angle(Vector3.ProjectOnPlane(w.forward, Vector3.up), toWindow), 0.5f, "its front faces the head");
            yield return _h.RayPinch(Menu.CloserButton.transform.position);
            yield return _h.RayPinch(Menu.CloserButton.transform.position);
            Assert.AreEqual(d0 - 0.25f, Distance, 2e-3f);
            Assert.AreEqual(a0, Apparent, 1e-3f);
            var top = _h.Root.Dock.TopCenter;
            var h0 = _h.Root.Dock.Height;
            yield return _h.RayPinch(Menu.LargerButton.transform.position);
            Assert.AreEqual(h0 * 1.10f, _h.Root.Dock.Height, 2e-3f, "10 % larger");
            Assert.Less(Vec3.Distance(top, _h.Root.Dock.TopCenter), 1e-3f, "its top centre stays");
            for (var i = 0; i < 20; i++) Menu.LargerButton.Click();
            yield return Frames(1);
            Assert.LessOrEqual(Apparent, WindowPlacement.MaxApparent + 0.02f, "at most 0.75 of the distance");
            for (var i = 0; i < 12; i++) Menu.FurtherButton.Click();
            yield return Frames(1);
            Assert.AreEqual(WindowPlacement.MaxDistance, Distance, 2e-3f, "at most 3.0 m");
            var kept = Apparent;

            // kept in prefs, restored on a new run
            var prefs = _h.Prefs.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
            _h.TearDown();
            yield return Frames(2);
            _h = new WorkspaceHarness();
            _h.SetUp();
            foreach (var kv in prefs) _h.Prefs.Values[kv.Key] = kv.Value;
            yield return _h.Start();
            Assert.AreEqual(WindowPlacement.MaxDistance, Distance, 5e-3f, "the saved distance");
            Assert.AreEqual(kept, Apparent, 5e-3f, "the saved apparent size");
        }

        [UnityTest]
        public IEnumerator ALongPinchOnAMenuButtonDoesNothingAndAShortOneClicks()
        {
            yield return _h.GoLive();
            yield return _h.OpenMenu();
            var d0 = Distance;
            var at = Menu.FurtherButton.transform.position;
            _h.Hands.Right = Hand(rayAt: at);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: at);
            yield return Frames(20);
            _h.Hands.Right = Hand(rayAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand();
            yield return Frames(1);
            Assert.AreEqual(d0, Distance, 1e-4f, "a held pinch is no click");
            yield return _h.RayPinch(Menu.FurtherButton.transform.position);
            Assert.AreEqual(d0 + 0.25f, Distance, 2e-3f, "a short pinch clicks");
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing decided");
        }

        int Allows() => _h.DecisionFrames().Count(f => f.Contains("\"allow\""));

        IEnumerator Request(string risk)
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", risk));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown);
        }

        [UnityTest]
        public IEnumerator OpeningTheMenuCancelsAHoldInProgressAndCoversTheBlock()
        {
            yield return Request("safe");
            yield return _h.PinchHold(false, 3);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold runs");
            _h.Root.Workspace.Header.MenuButton.Click();
            yield return Frames(1);
            Assert.IsTrue(Menu.IsOpen);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "opening the menu cancels the hold");
            Assert.IsTrue(_h.Block.Covered, "the block is dimmed");
            Assert.AreEqual(Texts.CloseMenuToAnswer, _h.Block.CoverLine);
            Assert.IsFalse(_h.Block.DenyButton.Interactable, "Deny is not pressable");
            Assert.IsFalse(_h.Block.LaterButton.Interactable, "Later is not pressable");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing sent");
        }

        [UnityTest]
        public IEnumerator AFullTwoHandHoldWithTheMenuOpenSendsNothing()
        {
            yield return Request("destructive");
            yield return _h.OpenMenu();
            yield return _h.PinchHold(true, 8);
Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "no hold starts while the menu is open");
            yield return Frames(32);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "no hold while the menu is open");
            _h.Block.DenyButton.Click();
            yield return Frames(2);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "Deny is not pressable either");
            // closed: a fresh two-hand hold approves (the hold above failed for the menu, not for the pose)
            Menu.CloseButton.Click();
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(true, 20);
            Assert.AreEqual(1, Allows(), "after closing, a fresh two-hand hold approves");
        }

        [UnityTest]
        public IEnumerator APinchHeldAcrossClosingTheMenuSendsNothingAndAFreshHoldApproves()
        {
            yield return Request("safe");
            yield return _h.OpenMenu();
            yield return _h.PinchHold(false, 5);
            Menu.CloseButton.Click();
            yield return Frames(1);
            Assert.IsFalse(Menu.IsOpen);
            Assert.IsFalse(_h.Block.Covered, "the block is back");
            yield return Frames(30); // the pinch held on across the closing
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "a pinch held across closing never approves");
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, Allows(), "a fresh hold approves as usual");
        }

        [UnityTest]
        public IEnumerator TheMenuOffersEverythingTheWristMenuDidAndThePalmUpPoseDoesNothing()
        {
            yield return _h.GoLive();
            var palmAt = HeadPos + new Vector3(-0.1f, -0.2f, 0.4f);
            _h.Camera.transform.rotation = Quaternion.LookRotation(palmAt - HeadPos, Vector3.up);
            _h.Hands.Left = new HandFrame { Sample = new Core.HandSample(true, true, 0f, false), PalmValid = true, PalmCenter = palmAt, PalmNormal = Vector3.down };
            yield return null;
            _h.Hands.Left = new HandFrame { Sample = new Core.HandSample(true, true, 0f, false), PalmValid = true, PalmCenter = palmAt, PalmNormal = Vector3.up };
            yield return Frames(10);
            Assert.AreEqual(MenuHand.None, _h.Root.Menu.Shown, "the wrist menu is off");
            _h.Hands.Left = Hand();
            _h.Camera.transform.rotation = Quaternion.identity;
            yield return Frames(1);

            yield return _h.OpenMenu();
            foreach (var label in new[] { Texts.Further, Texts.Closer, Texts.Larger, Texts.Smaller, Texts.ScreenSetup, Texts.Replace, Texts.Settings, Texts.HaltAll, Texts.Close })
                Assert.IsTrue(PokeButton.All.Any(b => b.Label == label && b.transform.IsChildOf(Menu.transform)), label);
            // halt with its question
            yield return _h.Poke(Menu.HaltButton);
            Assert.IsTrue(Menu.AskingHalt);
            Assert.AreEqual(0, _h.Socket.Sent.Count(f => f.Contains("\"halt\"")));
            yield return _h.Poke(Menu.HaltYesButton);
            Assert.AreEqual(1, _h.Socket.Sent.Count(f => f == "{\"type\":\"halt\",\"on\":true}"));
            Assert.IsFalse(Menu.IsOpen);
            yield return _h.OpenMenu();
            yield return _h.Poke(Menu.CloseButton);
            Assert.IsFalse(Menu.IsOpen);
        }

        /// <summary>Every renderer under `part` lies inside the window's panel, `inset` metres in from its top edge.</summary>
        void AssertInsidePanel(GameObject part, float topInset, string what)
        {
            var bg = _h.Root.Workspace.Background.transform;
            foreach (var r in part.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy))
            {
                var b = r.bounds;
                foreach (var corner in new[] { b.min, b.max })
                {
                    var local = bg.InverseTransformPoint(corner); // the panel quad spans -0.5..0.5
                    Assert.LessOrEqual(Mathf.Abs(local.x), 0.5f + 1e-3f, what + ": " + r.name + " sideways");
                    Assert.LessOrEqual(local.y * bg.lossyScale.y, bg.lossyScale.y * 0.5f - topInset + 1e-4f, what + ": " + r.name + " at the top");
                    Assert.GreaterOrEqual(local.y, -0.5f - 1e-3f, what + ": " + r.name + " at the bottom");
                }
            }
        }

        [UnityTest]
        public IEnumerator TheHeaderAndTheHaltedBandLieInsideThePanelAtEveryScale()
        {
            yield return _h.GoLive();
            yield return _h.OpenMenu();
            foreach (var scale in new[] { -1, 0, 1 })
            {
                // the smallest apparent size, the default, the largest
                for (var i = 0; i < 14 && scale != 0; i++) { (scale < 0 ? Menu.SmallerButton : Menu.LargerButton).Click(); yield return null; }
                yield return Frames(2);
                var k = _h.Root.Workspace.transform.lossyScale.y;
                AssertInsidePanel(_h.Root.Workspace.Header.gameObject, WorkspaceHeader.TopPad * k * 0.5f, "the header at scale " + Scale.ToString("0.00"));
            }
            _h.Socket.Receive("{\"type\":\"halt_state\",\"on\":true,\"enforcement\":{\"claude\":\"every-step\"}}");
            yield return Frames(3);
            Assert.IsTrue(_h.Root.Workspace.Halt.BandShown);
            AssertInsidePanel(_h.Root.Workspace.Halt.transform.Find("Band").gameObject, 0f, "Continue and the halted line");
        }
    }
}
