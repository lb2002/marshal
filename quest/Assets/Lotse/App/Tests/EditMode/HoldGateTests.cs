#nullable enable
using System.IO;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.App.Tests
{
    /// <summary>
    /// F1 and residual R1: a pinch or two-hand hold only starts and only continues while the head faces the card
    /// (35°) and every pinching hand is above the floor (desk + 0.10 m on the desk, eyes − 0.45 m without one)
    /// inside the 70° cone around the head's forward axis. A gated pinch gets a cue on the card.
    /// </summary>
    public class HoldGateTests
    {
        const double Frame = 20;

        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);

        // no desk (head-relative anchor): eyes at 1.6 m looking straight ahead, the card 0.6 m ahead, a little above
        static readonly Vec3 Head = new Vec3(0f, 1.6f, 0f);
        static readonly Vec3 Ahead = new Vec3(0f, 0f, 1f);
        static readonly Vec3 Card = new Vec3(0f, 1.7f, 0.6f);
        static readonly float Floor = HoldGate.FallbackFloor(1.6f); // 1.15
        static readonly Vec3 Raised = new Vec3(0.1f, 1.45f, 0.35f);
        static readonly Vec3 RaisedLeft = new Vec3(-0.1f, 1.45f, 0.35f);
        static readonly Vec3 Low = new Vec3(0.1f, 1.05f, 0.3f);    // eyes − 0.55 m: the lap, the keyboard
        static readonly Vec3 Side = new Vec3(0.5f, 1.5f, 0.1f);    // high enough but 79° off the head's forward

        static HoldPose Pose(Vec3? left, Vec3? right, Vec3? forward = null) =>
            new HoldPose(Head, forward ?? Ahead, Card, Floor, left, right);

        static DemoSession AtCard(string id)
        {
            var s = new DemoSession(DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json"))));
            // earlier cards are approved on the way (ungated test seam): release for a few frames, then pinch
            var guard = 0;
            string? seen = null;
            var frames = 0;
            s.SetViewShown(true);
            while (s.Card?.Id != id && guard++ < 200000)
            {
                if (s.Card == null && s.Store.Pending.Count > 0) s.Select(s.Store.Pending[0].Sid); // the user picks the agent that asks
                var card = s.Card;
                if (card != null && card.Id != seen)
                {
                    seen = card.Id;
                    frames = 0;
                }
                var pinch = card != null && ++frames > 3;
                s.Step(Frame, pinch && card!.NeedsTwoHand ? Pinch : Open, pinch ? Pinch : Open);
            }
            Assert.AreEqual(id, s.Card?.Id);
            return s;
        }

        static void Hold(DemoSession s, double ms, HandSample left, HandSample right, HoldPose pose)
        {
            for (double t = 0; t < ms; t += Frame) s.Step(Frame, left, right, pose);
        }

        [Test]
        public void TheGateGeometry()
        {
            Assert.IsTrue(HoldGate.Facing(Pose(null, Raised)), "looking at the card");
            Assert.IsFalse(HoldGate.Facing(Pose(null, Raised, new Vec3(1f, 0f, 0f))), "turned 80° away");
            Assert.IsTrue(HoldGate.Facing(Pose(null, Raised, new Vec3(0.5f, 0f, 1f).Normalized)), "26° to the side still faces it");
            Assert.IsFalse(HoldGate.Facing(Pose(null, Raised, new Vec3(0.8f, 0f, 1f).Normalized)), "38° to the side does not");

            Assert.IsTrue(HoldGate.HandInside(Pose(null, Raised), Raised));
            Assert.IsFalse(HoldGate.HandInside(Pose(null, Low), Low), "below the floor");
            Assert.IsFalse(HoldGate.HandInside(Pose(null, null), new Vec3(0.1f, Floor - 0.01f, 0.35f)), "1 cm below the floor");
            Assert.IsTrue(HoldGate.HandInside(Pose(null, null), new Vec3(0.1f, Floor + 0.01f, 0.35f)), "1 cm above the floor");
            Assert.IsFalse(HoldGate.HandInside(Pose(null, Side), Side), "outside the 70° cone");
            Assert.IsFalse(HoldGate.HandInside(Pose(null, null), null), "no pinch point: closed");
        }

        [Test]
        public void TheRuledValuesLiveInOnePlace()
        {
            Assert.AreEqual(0.10f, HoldGate.DeskFloorMeters);
            Assert.AreEqual(0.45f, HoldGate.BelowEyesMeters);
            Assert.AreEqual(70f, HoldGate.HandConeDegrees);
            Assert.AreEqual(35f, HoldGate.FacingDegrees);
            Assert.AreEqual(0.85f, HoldGate.DeskFloor(0.75f), 1e-6f);
            Assert.AreEqual(1.15f, HoldGate.FallbackFloor(1.6f), 1e-6f);
        }

        // ---- realistic seated poses (residual R1) ----

        /// <summary>At a desk: eyes 1.15 m, desk 0.75 m, the card above the desk anchor (desk + 0.08 + 0.35), looked at.</summary>
        static HoldPose AtDesk(Vec3? left, Vec3? right)
        {
            var head = new Vec3(0f, 1.15f, 0f);
            var card = new Vec3(0f, 0.75f + 0.08f + 0.35f, 0.45f);
            return new HoldPose(head, (card - head).Normalized, card, HoldGate.DeskFloor(0.75f), left, right);
        }

        /// <summary>No desk: eyes 1.6 m, the card 0.6 m ahead above the head-relative anchor (eyes − 0.25 + 0.35), looked at.</summary>
        static HoldPose NoDesk(Vec3? left, Vec3? right)
        {
            var head = new Vec3(0f, 1.6f, 0f);
            var card = new Vec3(0f, 1.7f, 0.6f);
            return new HoldPose(head, (card - head).Normalized, card, HoldGate.FallbackFloor(1.6f), left, right);
        }

        [Test]
        public void ARelaxedSeatedPinchAtTheDeskCounts()
        {
            var left = new Vec3(-0.1f, 0.90f, 0.25f);
            var right = new Vec3(0.1f, 0.90f, 0.25f);
            var pose = AtDesk(left, right);
            Assert.IsTrue(HoldGate.Facing(pose));
            Assert.IsTrue(HoldGate.HandInside(pose, left), "hands at 0.90 m, 0.25 m forward");
            Assert.IsTrue(HoldGate.HandInside(pose, right));
        }

        [Test]
        public void APenOnTheDeskDoesNotCount()
        {
            var pen = new Vec3(0.1f, 0.77f, 0.3f);
            Assert.IsFalse(HoldGate.HandInside(AtDesk(null, pen), pen));
        }

        [Test]
        public void WithoutADeskAPinchFortyCentimetresBelowTheEyesCounts()
        {
            var hand = new Vec3(0.1f, 1.6f - 0.40f, 0.3f);
            var pose = NoDesk(null, hand);
            Assert.IsTrue(HoldGate.Facing(pose));
            Assert.IsTrue(HoldGate.HandInside(pose, hand));
        }

        [Test]
        public void APinchAtLapHeightDoesNotCount()
        {
            var lap = new Vec3(0.1f, 1.6f - 0.60f, 0.3f);
            Assert.IsFalse(HoldGate.HandInside(NoDesk(null, lap), lap));
        }

        [Test]
        public void ASeatedPinchAtTheDeskConfirmsThroughTheSession()
        {
            var s = AtCard("p1");
            var pose = AtDesk(new Vec3(-0.1f, 0.90f, 0.25f), new Vec3(0.1f, 0.90f, 0.25f));
            Hold(s, 40, Open, Open, pose);
            Hold(s, GestureMachine.PinchHoldMs + 3 * Frame, Open, Pinch, pose);
            Assert.AreEqual(1, s.Decisions.Count);
        }

        // ---- cues ----

        [Test]
        public void APinchTheGateHoldsBackSaysRaiseYourHands()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Low));
            Assert.AreEqual(HoldCue.None, s.HoldCue, "nothing pinches");
            Hold(s, 3 * Frame, Open, Pinch, Pose(RaisedLeft, Low));
            Assert.AreEqual(HoldCue.RaiseHands, s.HoldCue, "pinching low");
            Hold(s, 3 * Frame, Open, Pinch, Pose(RaisedLeft, Raised, new Vec3(1f, 0f, 0f)));
            Assert.AreEqual(HoldCue.RaiseHands, s.HoldCue, "pinching while looking away");
            Assert.AreEqual("Hands a little higher, look at the request", Texts.Cue(HoldCue.RaiseHands));
        }

        [Test]
        public void AHandThatCameInPinchedIsAskedToLetGo()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Low));
            Hold(s, 3 * Frame, Open, Pinch, Pose(RaisedLeft, Low));
            Hold(s, 3 * Frame, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.AreEqual(HoldCue.Release, s.HoldCue);
            Assert.AreEqual("Let go briefly, then pinch again", Texts.Cue(HoldCue.Release));
            Hold(s, 2 * Frame, Open, Open, Pose(RaisedLeft, Raised));
            Assert.AreEqual(HoldCue.None, s.HoldCue);
            Hold(s, 3 * Frame, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.Greater(s.HoldProgress, 0f);
            Assert.AreEqual(HoldCue.None, s.HoldCue, "a running hold needs no cue");
            Assert.IsNull(Texts.Cue(HoldCue.None));
        }

        [Test]
        public void NoCueOutsideHoldMode()
        {
            var s = AtCard("p1");
            s.Later(); // "Später": no block
            Assert.AreEqual(CardMode.None, s.Mode);
            Hold(s, 3 * Frame, Open, Pinch, Pose(RaisedLeft, Low));
            Assert.AreEqual(HoldCue.None, s.HoldCue);
        }

        [Test]
        public void ARaisedHandLookingAtTheCardConfirms()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Raised));
            Hold(s, GestureMachine.PinchHoldMs + 3 * Frame, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.AreEqual(1, s.Decisions.Count);
            Assert.AreEqual("allow", s.Decisions[0].Behavior);
            Assert.AreEqual("pinch-hold", s.Decisions[0].Confirm);
        }

        [Test]
        public void APinchWithTheHandLowNeverConfirms()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Low));
            Hold(s, 5000, Open, Pinch, Pose(RaisedLeft, Low));
            Assert.AreEqual(0, s.Decisions.Count);
            Assert.AreEqual(0f, s.HoldProgress, "the hold never started");
            Assert.AreEqual("p1", s.Card?.Id);
        }

        [Test]
        public void APinchWithTheHeadTurnedAwayNeverConfirms()
        {
            var s = AtCard("p1");
            var away = Pose(RaisedLeft, Raised, new Vec3(1f, 0f, 0f));
            Hold(s, 40, Open, Open, away);
            Hold(s, 5000, Open, Pinch, away);
            Assert.AreEqual(0, s.Decisions.Count);
        }

        [Test]
        public void APinchOutsideTheConeNeverConfirms()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Side));
            Hold(s, 5000, Open, Pinch, Pose(RaisedLeft, Side));
            Assert.AreEqual(0, s.Decisions.Count);
        }

        [Test]
        public void TurningAwayMidHoldCancelsItAsARelease()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Raised));
            Hold(s, 600, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.Greater(s.HoldProgress, 0.5f);
            Hold(s, 2 * Frame, Open, Pinch, Pose(RaisedLeft, Raised, new Vec3(1f, 0f, 0f)));
            Assert.AreEqual(0f, s.HoldProgress, "cancelled");
            // looking back with the pinch still closed does not resume it: the hand has to open first
            Hold(s, 3000, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.AreEqual(0, s.Decisions.Count);
            Hold(s, 2 * Frame, Open, Open, Pose(RaisedLeft, Raised));
            Hold(s, GestureMachine.PinchHoldMs + 3 * Frame, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.AreEqual(1, s.Decisions.Count, "a fresh pinch inside the gate confirms");
        }

        [Test]
        public void AHandRaisedIntoTheGateAlreadyPinchingDoesNotConfirm()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Low));
            Hold(s, 200, Open, Pinch, Pose(RaisedLeft, Low)); // pinched on the keyboard
            Hold(s, 3000, Open, Pinch, Pose(RaisedLeft, Raised)); // lifted into view, still pinched
            Assert.AreEqual(0, s.Decisions.Count);
        }

        [Test]
        public void AHandBackFromATrackingLossStillPinchingDoesNotConfirm()
        {
            var s = AtCard("p1");
            var lost = new HandSample(false, false, 0f, false);
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Raised));
            Hold(s, 400, Open, Pinch, Pose(RaisedLeft, Raised));
            // as while placing: the session gets untracked hands, the pinch points are still where they were
            Hold(s, 3 * Frame, Open, lost, Pose(RaisedLeft, Raised));
            Assert.AreEqual(0f, s.HoldProgress, "the hold is cancelled");
            Hold(s, 3000, Open, Pinch, Pose(RaisedLeft, Raised)); // tracked again, still pinching
            Assert.AreEqual(0, s.Decisions.Count, "only a fresh pinch counts");
        }

        [Test]
        public void TwoHandsNeedBothHandsInsideTheGate()
        {
            var s = AtCard("p11");
            var before = s.Decisions.Count; // the cards approved on the way
            Hold(s, 40, Open, Open, Pose(Low, Raised));
            Hold(s, 5000, Pinch, Pinch, Pose(Low, Raised));
            Assert.AreEqual(before, s.Decisions.Count, "the left hand is low");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Raised));
            Hold(s, GestureMachine.TwoHandHoldMs + 3 * Frame, Pinch, Pinch, Pose(RaisedLeft, Raised));
            Assert.AreEqual(before + 1, s.Decisions.Count);
            Assert.AreEqual("p11", s.Decisions[before].Id);
            Assert.AreEqual("two-hand", s.Decisions[before].Confirm);
        }

        [Test]
        public void ThePalmIsNotGatedAndOnlyCancelsTheHold()
        {
            var s = AtCard("p1");
            Hold(s, 40, Open, Open, Pose(RaisedLeft, Raised));
            Hold(s, 400, Open, Pinch, Pose(RaisedLeft, Raised));
            Assert.Greater(s.HoldProgress, 0f);
            var palm = new HandSample(true, true, 0f, true);
            Hold(s, GestureMachine.PalmStopMs + 3 * Frame, palm, Pinch, Pose(Low, Raised));
            Assert.AreEqual(0f, s.HoldProgress, "a palm cancels the hold, wherever it is");
            Assert.AreEqual(CardMode.Hold, s.Mode, "it no longer denies (U5a): only Ablehnen does");
            Assert.AreEqual(0, s.Decisions.Count);
        }
    }
}
