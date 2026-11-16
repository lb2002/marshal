#nullable enable
using System.IO;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U1, U5a fix 1: pointing with a hand ray and clicking with a short pinch: on release, on the same target, the
    /// other hand open, no hold running; a pinch that began on a target is masked from the hold, so a click can never
    /// become an approval and a hold never a click.
    /// </summary>
    public class RayClickTests
    {
        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);
        static readonly HandSample Half = new HandSample(true, true, 0.7f, false); // between PinchOff and PinchOn
        static readonly HandSample Lost = new HandSample(false, false, 0f, false);

        // a button 0.5 m ahead at eye height, facing the user (its forward points away from the user)
        static readonly PokeRect Button = new PokeRect(new Vec3(0f, 1.2f, 0.5f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), 0.05f, 0.02f);
        static readonly Vec3 Eye = new Vec3(0f, 1.2f, 0f);
        static readonly Vec3 Ahead = new Vec3(0f, 0f, 1f);

        // ---- hits ----

        [Test]
        public void ARayHitsAButtonFromTheFrontOnly()
        {
            Assert.IsTrue(RayPick.HitsRect(Eye, Ahead, Button, 0f, 2f, out var d));
            Assert.AreEqual(0.5f, d, 1e-5f);
            Assert.IsFalse(RayPick.HitsRect(new Vec3(0f, 1.2f, 1f), new Vec3(0f, 0f, -1f), Button, 0f, 2f, out _), "from behind");
            Assert.IsFalse(RayPick.HitsRect(new Vec3(0f, 1.2f, 0.6f), Ahead, Button, 0f, 2f, out _), "starting past it");
            Assert.IsFalse(RayPick.HitsRect(Eye, Ahead, Button, 0f, 0.4f, out _), "beyond the ray's reach");
            Assert.IsFalse(RayPick.HitsRect(Eye, new Vec3(1f, 0f, 0f), Button, 0f, 2f, out _), "parallel");
        }

        [Test]
        public void TheButtonsEdgeAndMargin()
        {
            Assert.IsTrue(RayPick.HitsRect(new Vec3(0.054f, 1.2f, 0f), Ahead, Button, 0.005f, 2f, out _));
            Assert.IsFalse(RayPick.HitsRect(new Vec3(0.056f, 1.2f, 0f), Ahead, Button, 0.005f, 2f, out _));
            Assert.IsFalse(RayPick.HitsRect(new Vec3(0f, 1.226f, 0f), Ahead, Button, 0.005f, 2f, out _), "above it");
            Assert.IsTrue(RayPick.HitsRect(new Vec3(0f, 1.15f, 0f), new Vec3(0f, 0.05f, 0.5f).Normalized, Button, 0f, 2f, out _), "slanted up into it");
        }

        [Test]
        public void ARayHitsASphere()
        {
            var center = new Vec3(0.3f, 0.9f, 0.6f);
            var toward = (center - Eye).Normalized;
            Assert.IsTrue(RayPick.HitsSphere(Eye, toward, center, 0.045f, 2f, out var d));
            Assert.AreEqual(Vec3.Distance(Eye, center) - 0.045f, d, 1e-4f, "where it enters");
            var aside = (center + new Vec3(0.06f, 0f, 0f) - Eye).Normalized;
            Assert.IsFalse(RayPick.HitsSphere(Eye, aside, center, 0.045f, 2f, out _), "6 cm aside");
            Assert.IsFalse(RayPick.HitsSphere(Eye, -toward, center, 0.045f, 2f, out _), "behind the hand");
            Assert.IsFalse(RayPick.HitsSphere(Eye, toward, center, 0.045f, 0.5f, out _), "beyond the ray's reach");
        }

        // ---- clicks (U5a fix 1: on release, kept apart from the hold) ----

        const double F = 20;
        const int T = 7; // a target
        const int U = 9; // another

        /// <summary>One frame of both hands; returns what the left clicked and what the right clicked.</summary>
        static (int l, int r) Step(RayClickLogic c, double now, HandSample left, long lt, HandSample right, long rt, bool holding = false)
        {
            var l = c.Update(now, left, lt, right, rt, holding, out var r);
            return ((int)l, (int)r);
        }

        [Test]
        public void AShortPinchOnATargetClicksOnRelease()
        {
            var c = new RayClickLogic();
            Assert.AreEqual((0, 0), Step(c, 0, Open, 0, Open, T));
            Assert.AreEqual((0, 0), Step(c, F, Open, 0, Pinch, T), "nothing on the press");
            Assert.IsTrue(c.MaskRight, "a pinch that began on a target never reaches the hold");
            Assert.AreEqual((0, 0), Step(c, 2 * F, Open, 0, Pinch, T));
            Assert.AreEqual((0, T), Step(c, 3 * F, Open, 0, Open, T), "fires on release");
            Assert.IsFalse(c.MaskRight);
        }

        [Test]
        public void APinchHeldLongerThanSixTenthsOfASecondDoesNotClick()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Open, 0, Pinch, T);
            for (var t = 2 * F; t < 2000; t += F) Assert.AreEqual((0, 0), Step(c, t, Open, 0, Pinch, T));
            Assert.IsTrue(c.MaskRight, "masked for its whole duration");
            Assert.AreEqual((0, 0), Step(c, 2000, Open, 0, Open, T), "held 2 s: no click");
        }

        [Test]
        public void TheReleaseMustBeOnTheSameTarget()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Open, 0, Pinch, T);
            Step(c, 2 * F, Open, 0, Pinch, U);
            Assert.AreEqual((0, 0), Step(c, 3 * F, Open, 0, Open, T), "it left the target in between");
            Step(c, 4 * F, Open, 0, Pinch, T);
            Assert.AreEqual((0, 0), Step(c, 5 * F, Open, 0, Open, U), "released on another target");
        }

        [Test]
        public void APinchThatStartsOnNothingNeverClicksAndIsNotMasked()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, 0);
            Step(c, F, Open, 0, Pinch, 0);
            Assert.IsFalse(c.MaskRight, "a pinch off every target belongs to the hold");
            Step(c, 2 * F, Open, 0, Pinch, T);
            Assert.AreEqual((0, 0), Step(c, 3 * F, Open, 0, Open, T), "moving onto a target later does not make it a click");
        }

        [Test]
        public void TheOtherHandPinchingAtAnyTimeSpoilsTheClick()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Pinch, 0, Open, T); // the left pinches first (a hold)
            Step(c, 2 * F, Pinch, 0, Pinch, T);
            Assert.AreEqual((0, 0), Step(c, 3 * F, Pinch, 0, Open, T), "with one hand pinching, a quick pinch of the other does not click");
            Step(c, 4 * F, Open, 0, Open, T);
            Step(c, 5 * F, Open, 0, Pinch, T);
            Step(c, 6 * F, Pinch, 0, Pinch, T); // the other hand comes in, then goes again
            Step(c, 7 * F, Open, 0, Pinch, T);
            Assert.AreEqual((0, 0), Step(c, 8 * F, Open, 0, Open, T), "the other hand pinched in between");
        }

        [Test]
        public void NoClickWhileAHoldIsInProgress()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T, holding: true);
            Step(c, F, Open, 0, Pinch, T, holding: true);
            Assert.AreEqual((0, 0), Step(c, 2 * F, Open, 0, Open, T, holding: true));
            Step(c, 3 * F, Open, 0, Pinch, T);
            Step(c, 4 * F, Open, 0, Pinch, T, holding: true);
            Assert.AreEqual((0, 0), Step(c, 5 * F, Open, 0, Open, T), "a hold began in between");
        }

        [Test]
        public void TrackingLossSpoilsTheClick()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Open, 0, Pinch, T);
            Assert.AreEqual((0, 0), Step(c, 2 * F, Open, 0, Lost, T));
            Assert.AreEqual((0, 0), Step(c, 3 * F, Open, 0, Open, T), "released after a loss: no click");
            Assert.IsFalse(c.MaskRight, "seen open: the mask goes");
        }

        /// <summary>
        /// U5a review 2: a masked pinch stays masked across a tracking loss until the hand is seen open again, whatever the
        /// ray rests on when tracking comes back. Pure: no HoldGate in the picture.
        /// </summary>
        [Test]
        public void AMaskedPinchStaysMaskedAcrossATrackingLossUntilTheHandIsSeenOpen()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Open, 0, Pinch, T);
            Assert.IsTrue(c.MaskRight, "began on a target");
            Step(c, 2 * F, Open, 0, Lost, 0);
            Assert.IsTrue(c.MaskRight, "a lost hand is not an open hand");
            for (var i = 3; i < 33; i++)
            {
                Assert.AreEqual((0, 0), Step(c, i * F, Open, 0, Pinch, 0), "back, still pinching, the ray off the target");
                Assert.IsTrue(c.MaskRight, "still masked");
            }
            Step(c, 40 * F, Open, 0, Half, 0);
            Assert.IsTrue(c.MaskRight, "half open is not open");
            Assert.AreEqual((0, 0), Step(c, 41 * F, Open, 0, Open, 0));
            Assert.IsFalse(c.MaskRight, "seen open: the next pinch starts fresh");
            Step(c, 42 * F, Open, 0, Pinch, 0);
            Assert.IsFalse(c.MaskRight, "a fresh pinch off every target may feed a hold");
        }

        [Test]
        public void TheRaysGoingOffKeepAMaskedPinchMaskedAndClickNothing()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Open, 0, Pinch, T);
            c.Reset(); // placing
            Assert.IsTrue(c.MaskRight);
            Assert.AreEqual((0, 0), Step(c, 2 * F, Open, 0, Open, T), "no click after the rays went off");
            Assert.IsFalse(c.MaskRight);
        }

        [Test]
        public void TwoTargetsNeverShareAnIdAcrossButtonsAndPressKeys()
        {
            // the ray's ids: the uid high, the press key low; formerly a hash that could collide
            long Id(int uid, int key) => RayClickLogic.TargetId(uid, key);
            var seen = new System.Collections.Generic.HashSet<long>();
            for (var uid = 1; uid < 300; uid++)
                for (var key = 0; key < 300; key++)
                    Assert.IsTrue(seen.Add(Id(uid, key)), uid + "/" + key);
        }

        [Test]
        public void HalfAPinchIsNoPressAndTheRayIsOffOnlyWhilePlacing()
        {
            var c = new RayClickLogic();
            Step(c, 0, Open, 0, Open, T);
            Step(c, F, Open, 0, Half, T);
            Assert.AreEqual((0, 0), Step(c, 2 * F, Open, 0, Open, T));
            Assert.AreEqual(RayScope.Off, RayClickLogic.Scope(true));
            Assert.AreEqual(RayScope.All, RayClickLogic.Scope(false), "also while a block is shown (U5a fix 1)");
        }

        /// <summary>
        /// A pinch held with the ray on a row (masked) never feeds the block's hold; a fresh pinch off every target approves.
        /// </summary>
        [Test]
        public void APinchOnARowNeverFeedsTheHoldOfTheBlock()
        {
            var s = new DemoSession(DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json"))));
            var head = new Vec3(0f, 1.6f, 0f);
            var pose = new HoldPose(head, Ahead, new Vec3(0f, 1.45f, 0.6f), HoldGate.FallbackFloor(head.Y), null, new Vec3(0.1f, 1.45f, 0.35f));
            var c = new RayClickLogic();
            s.SetViewShown(true);
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100000) s.Step(F, Open, Open, pose);
            s.Select("demo-a1");
            Assert.AreEqual("p1", s.Card?.Id);
            double now = 0;
            HandSample Fed(HandSample h) => c.MaskRight ? new HandSample(h.Tracked, h.HighConfidence, 0f, h.PalmForward) : h;
            for (var i = 0; i < 150; i++)
            {
                Step(c, now += F, Open, 0, Pinch, T);
                s.Step(F, Open, Fed(Pinch), pose);
            }
            Assert.AreEqual(0, s.Decisions.Count, "a pinch that began on a target never approves");
            Step(c, now += F, Open, 0, Open, T);
            s.Step(F, Open, Open, pose);
            for (var i = 0; i < 60 && s.Decisions.Count == 0; i++)
            {
                Step(c, now += F, Open, 0, Pinch, 0);
                s.Step(F, Open, Fed(Pinch), pose);
            }
            Assert.AreEqual(1, s.Decisions.Count, "a pinch off every target approves");
        }

        /// <summary>
        /// The app checks the confirm's kind against the shown block's risk before it sends an allow: a one-hand hold
        /// begun on a harmless block never allows it once the block needs both hands (fail closed).
        /// </summary>
        [Test]
        public void AnAllowIsSentOnlyWithTheGestureTheBlocksRiskAsksFor()
        {
            var s = new DemoSession(DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json"))));
            var head = new Vec3(0f, 1.6f, 0f);
            var pose = new HoldPose(head, Ahead, new Vec3(0f, 1.45f, 0.6f), HoldGate.FallbackFloor(head.Y), null, new Vec3(0.1f, 1.45f, 0.35f));
            s.SetViewShown(true);
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100000) s.Step(F, Open, Open, pose);
            s.Select("demo-a1");
            Assert.AreEqual("p1", s.Card?.Id);
            Assert.IsFalse(s.Card.NeedsTwoHand);
            s.Step(F, Open, Open, pose);
            s.Step(F, Open, Pinch, pose);
            s.Card.Truncated = true; // the shown request now needs both hands, the one-hand hold already runs
            for (var i = 0; i < 100; i++) s.Step(F, Open, Pinch, pose);
            Assert.AreEqual(0, s.Decisions.Count, "a pinch-hold never allows a block that needs both hands");
        }

        /// <summary>
        /// When the block goes in the middle of a hold (Später), the gesture is reset with it: the held pinch never even
        /// completes a confirm, and selecting the agent again needs a fresh hold.
        /// </summary>
        [Test]
        public void TheGestureIsResetWhenTheBlockGoes()
        {
            var s = new DemoSession(DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json"))));
            var head = new Vec3(0f, 1.6f, 0f);
            var pose = new HoldPose(head, Ahead, new Vec3(0f, 1.45f, 0.6f), HoldGate.FallbackFloor(head.Y), null, new Vec3(0.1f, 1.45f, 0.35f));
            var dropped = new System.Collections.Generic.List<string>();
            s.DecisionDropped += dropped.Add;
            s.SetViewShown(true);
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100000) s.Step(F, Open, Open, pose);
            s.Select("demo-a1");
            Assert.AreEqual("p1", s.Card?.Id);
            s.Step(F, Open, Open, pose);
            for (var i = 0; i < 5; i++) s.Step(F, Open, Pinch, pose);
            Assert.Greater(s.HoldProgress, 0f, "a hold runs");
            s.Later();
            Assert.IsNull(s.Card);
            for (var i = 0; i < 100; i++) s.Step(F, Open, Pinch, pose);
            CollectionAssert.IsEmpty(dropped, "no confirm was even attempted after the block went");
            Assert.AreEqual(0, s.Decisions.Count);
        }
    }
}
