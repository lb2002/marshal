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
    /// U5a fix 1 through AppRoot: the ray stays on while a block is shown. A ray click is a short pinch of one hand, on and
    /// off the same target, the other hand open, no hold running; a pinch that began on a target never feeds the hold.
    /// </summary>
    public class RayDuringBlockTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
        }

        [TearDown]
        public void TearDown() => _h.TearDown();

        IEnumerator Block(string risk)
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", risk));
            yield return Frames(2);
            Assert.AreEqual("r1", _h.Block.RequestId);
        }

        [UnityTest]
        public IEnumerator ATwoHandHoldWithOneRayRestingOnAblehnenAtPinchStartApprovesNothingAndDeniesNothing()
        {
            yield return Block("destructive");
            var at = _h.GatePoint();
            var deny = _h.Block.DenyButton.transform.position;
            _h.Hands.Right = Hand(rayAt: deny, pinchAt: at);
            _h.Hands.Left = Hand(pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(2);
            Assert.IsTrue(_h.Root.Pointer.RightShown, "the ray is on while the block is shown");
            _h.Hands.Right = Hand(1f, rayAt: deny, pinchAt: at);
            _h.Hands.Left = Hand(1f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(30);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the hand that began on Ablehnen is not part of a hold");
            _h.ReleaseHands();
            yield return Frames(3);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "neither approved nor denied");
            Assert.AreEqual("r1", _h.Block.RequestId);
        }

        [UnityTest]
        public IEnumerator AClickOnSpaeterWhenNoHoldRunsWorks()
        {
            yield return Block("safe");
            yield return _h.RayPinch(_h.Block.LaterButton.transform.position);
            Assert.IsFalse(_h.Block.IsShown, "Später by ray click hides the block");
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "Später sends nothing");
        }

        [UnityTest]
        public IEnumerator APinchHeldTwoSecondsOnARowNeitherSelectsNorApproves()
        {
            yield return Block("safe");
            var row = _h.Rows.RowOf("s2").transform.position;
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(rayAt: row, pinchAt: at);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: row, pinchAt: at);
            yield return Frames(20);
            _h.Hands.Right = Hand(rayAt: row, pinchAt: at);
            yield return Frames(3);
            Assert.AreEqual("s1", _h.Root.SelectedAgent, "held 2 s: no click");
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "and no approval");
        }

        /// <summary>Both rays on (resting on the panel, on no target), as on the Quest: a clean two-hand hold approves.</summary>
        [UnityTest]
        public IEnumerator ACleanTwoHandHoldOffAllTargetsApprovesADangerousRequest()
        {
            yield return Block("destructive");
            var at = _h.GatePoint();
            var feed = _h.Conversation.Feed.Rect.Center;
            var onPanel = new Vector3(feed.X, feed.Y, feed.Z);
            _h.Hands.Right = Hand(rayAt: onPanel, pinchAt: at);
            _h.Hands.Left = Hand(rayAt: onPanel + Vector3.left * 0.03f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(2);
            Assert.IsTrue(_h.Root.Pointer.RightShown && _h.Root.Pointer.LeftShown, "both rays are on");
            _h.Hands.Right = Hand(1f, rayAt: onPanel, pinchAt: at);
            _h.Hands.Left = Hand(1f, rayAt: onPanel + Vector3.left * 0.03f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(17);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(1, _h.DecisionFrames().Count());
            StringAssert.Contains("\"id\":\"r1\",\"behavior\":\"allow\"", _h.DecisionFrames().Single());
        }

        /// <summary>U5a review 2: while a hold is in progress no ray click fires (through the app, not only the pure logic).</summary>
        [UnityTest]
        public IEnumerator NoRayClickFiresDuringAHold()
        {
            yield return Block("safe");
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(3);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold runs");
            // the left hand clicks Später quickly meanwhile
            var later = _h.Block.LaterButton.transform.position;
            _h.Hands.Left = Hand(rayAt: later);
            yield return null;
            _h.Hands.Left = Hand(1f, rayAt: later);
            yield return null;
            _h.Hands.Left = Hand(rayAt: later);
            yield return null;
            Assert.IsTrue(_h.Block.IsShown, "Später did not click during the hold");
            Assert.IsFalse(_h.Root.Session.PutOff);
            _h.ReleaseHands();
            yield return Frames(2);
        }

        /// <summary>
        /// U5a review 2: a pinch begun on a row, a one-frame tracking loss, the ray moved off, the pinch held 30 frames: it
        /// stays masked, nothing is sent and nothing is selected.
        /// </summary>
        [UnityTest]
        public IEnumerator AMaskedPinchStaysMaskedAcrossATrackingLoss()
        {
            yield return Block("safe");
            var row = _h.Rows.RowOf("s2").transform.position;
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(rayAt: row, pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, rayAt: row, pinchAt: at);
            yield return null;
            Assert.IsTrue(_h.Root.Pointer.MaskRight);
            _h.Hands.Right = Hand(1f, pinchAt: at, tracked: false);
            yield return null;
            _h.Hands.Right = Hand(1f, pinchAt: at); // back, the ray off the row
            yield return Frames(30);
            Assert.IsTrue(_h.Root.Pointer.MaskRight, "still masked");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            _h.ReleaseHands();
            yield return Frames(2);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing sent");
            Assert.AreEqual("s1", _h.Root.SelectedAgent, "nothing selected");
        }

        [UnityTest]
        public IEnumerator WithOneHandPinchingAQuickPinchOfTheOtherHandOnAButtonDoesNotClick()
        {
            yield return Block("safe");
            var at = _h.GatePoint();
            var deny = _h.Block.DenyButton.transform.position;
            // the left hand pinches off every target, then the right clicks Ablehnen quickly
            _h.Hands.Left = Hand(pinchAt: at + Vector3.left * 0.15f);
            _h.Hands.Right = Hand(rayAt: deny, pinchAt: Lap);
            yield return null;
            _h.Hands.Left = Hand(1f, pinchAt: Lap);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: deny, pinchAt: Lap);
            yield return Frames(2);
            _h.Hands.Right = Hand(rayAt: deny, pinchAt: Lap);
            yield return Frames(2);
            _h.ReleaseHands();
            yield return Frames(2);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "no click while the other hand pinches");
            Assert.AreEqual("r1", _h.Block.RequestId);
        }
    }
}
