#nullable enable
using System.Collections.Generic;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// The stop gesture (U7): both open hands, palms away from the head, still, for 1.0 s. Each condition, the timer,
    /// and the sequences that must never trigger it.
    /// </summary>
    public class StopGestureTests
    {
        const double Frame = 20;
        static readonly Vec3 Head = new Vec3(0f, 1.6f, 0f);
        static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);
        static readonly Vec3 Away = new Vec3(0f, 0f, 1f);
        static readonly Vec3 Down = new Vec3(0f, -1f, 0f);

        StopGesture _g = null!;
        double _now;
        readonly List<StopEvent> _events = new List<StopEvent>();

        [SetUp]
        public void SetUp()
        {
            _g = new StopGesture();
            _now = 0;
            _events.Clear();
        }

        /// <summary>An open hand at `at` with its palm along `normal`, fingers straight unless `curl`.</summary>
        static StopHand Hand(Vec3 at, Vec3? normal = null, float pinch = 0f, float straight = 0.98f, float? little = null) =>
            new StopHand(pinch, at, normal ?? Away, straight, straight, straight, little ?? straight);

        static Vec3 LeftAt => new Vec3(-0.2f, 1.5f, 0.4f);
        static Vec3 RightAt => new Vec3(0.2f, 1.5f, 0.4f);
        static StopHand LeftPose => Hand(LeftAt);
        static StopHand RightPose => Hand(RightAt);

        int Triggers => _events.FindAll(e => e.Kind == StopEventKind.Triggered).Count;

        void Run(double ms, System.Func<double, (StopHand l, StopHand r)> hands, bool blocked = false, Vec3? forward = null)
        {
            for (double t = 0; t < ms; t += Frame)
            {
                _now += Frame;
                var (l, r) = hands(t);
                var e = _g.Step(_now, blocked, l, r, Head, forward ?? Forward);
                if (e.Kind != StopEventKind.None) _events.Add(e);
            }
        }

        void Hold(double ms) => Run(ms, _ => (LeftPose, RightPose));

        [Test]
        public void BothOpenHandsPalmsAwayHeldStillForOneSecondTriggerOnce()
        {
            Hold(StopGesture.HoldMs * 0.5);
            Assert.IsTrue(_g.Counting);
            Assert.AreEqual(StopEventKind.Started, _events[0].Kind);
            Assert.That(_g.Progress, Is.InRange(0.4f, 0.6f));
            Assert.AreEqual(0, Triggers);
            Hold(StopGesture.HoldMs * 0.5 + 3 * Frame);
            Assert.AreEqual(1, Triggers);
            Hold(5000);
            Assert.AreEqual(1, Triggers, "held on, it does not trigger again");
            Run(3 * Frame, _ => (LeftPose, StopHand.None));
            Hold(StopGesture.QuietAfterLossMs);
            Hold(StopGesture.HoldMs + 3 * Frame);
            Assert.AreEqual(2, Triggers, "after leaving the pose it can trigger again");
        }

        [Test]
        public void EachConditionIsNeeded()
        {
            void Expect(string what, StopHand l, StopHand r, bool blocked = false, Vec3? forward = null)
            {
                SetUp();
                Run(StopGesture.HoldMs * 3, _ => (l, r), blocked, forward);
                Assert.AreEqual(0, Triggers, what);
                Assert.IsFalse(_g.Counting, what);
            }
            Expect("left untracked", StopHand.None, RightPose);
            Expect("right untracked", LeftPose, StopHand.None);
            Expect("pinching", LeftPose, Hand(RightAt, pinch: StopGesture.PinchMax));
            Expect("palm facing the user", LeftPose, Hand(RightAt, new Vec3(0f, 0f, -1f)));
            Expect("palm not away enough", LeftPose, Hand(RightAt, new Vec3(0f, 0.75f, 0.66f)));
            Expect("index curled", LeftPose, new StopHand(0f, RightAt, Away, StopGesture.FingerStraightMin, 1f, 1f, 1f));
            Expect("little finger curled", LeftPose, Hand(RightAt, little: 0.3f));
            Expect("too low", LeftPose, Hand(new Vec3(0.2f, Head.Y - StopGesture.BelowHeadMax - 0.01f, 0.4f)));
            Expect("too far", LeftPose, Hand(new Vec3(0.2f, 1.5f, StopGesture.ReachMax + 0.05f)));
            Expect("blocked", LeftPose, RightPose, blocked: true);
            Expect("no head forward", LeftPose, RightPose, forward: new Vec3(0f, 0f, 0f));

            // and with every condition met it does trigger (the cases above fail for their own reason)
            SetUp();
            Hold(StopGesture.HoldMs + 3 * Frame);
            Assert.AreEqual(1, Triggers);
        }

        [Test]
        public void AnyViolationResetsTheTimerWithItsReason()
        {
            Hold(StopGesture.HoldMs * 0.8);
            Run(Frame, _ => (LeftPose, Hand(RightAt, pinch: 0.9f)));
            Assert.IsFalse(_g.Counting);
            Assert.AreEqual(0f, _g.Progress);
            var reset = _events.Find(e => e.Kind == StopEventKind.Reset);
            Assert.AreEqual("pinch", reset.Reason);
            Hold(StopGesture.HoldMs * 0.8);
            Assert.AreEqual(0, Triggers, "the full second again");
            Hold(StopGesture.HoldMs * 0.3);
            Assert.AreEqual(1, Triggers);
        }

        [Test]
        public void TrackingFlickerDuringThePoseResetsAndNeedsHalfASecondOfQuiet()
        {
            Hold(StopGesture.HoldMs * 0.9);
            Run(Frame, _ => (StopHand.None, RightPose));
            Assert.AreEqual("tracking lost", _events.Find(e => e.Kind == StopEventKind.Reset).Reason);
            Hold(StopGesture.QuietAfterLossMs - 2 * Frame);
            Assert.IsFalse(_g.Counting, "not within half a second of a tracking loss");
            // flicker every 0.4 s through a long pose: never a trigger
            Run(10000, t => ((long)(t / Frame) % 20 == 0 ? StopHand.None : LeftPose, RightPose));
            Assert.AreEqual(0, Triggers);
        }

        // ---- the sequences that must never trigger ----

        [Test]
        public void TypingOnADeskKeyboardNeverTriggers()
        {
            // hands low over the desk, palms down, fingers half bent, small fast movements
            Run(10000, t =>
            {
                var j = (float)System.Math.Sin(t / 37.0) * 0.01f;
                return (Hand(new Vec3(-0.12f + j, 0.8f, 0.35f), Down, 0.1f, 0.6f), Hand(new Vec3(0.12f - j, 0.8f, 0.35f), Down, 0.2f, 0.5f));
            });
            Assert.AreEqual(0, Triggers);
            Assert.IsFalse(_g.Counting);
        }

        [Test]
        public void RestingBothHandsOnTheDeskPalmsDownNeverTriggers()
        {
            Run(10000, _ => (Hand(new Vec3(-0.2f, 0.75f, 0.4f), Down), Hand(new Vec3(0.2f, 0.75f, 0.4f), Down)));
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void WavingNeverTriggers()
        {
            // both hands open, palms away, swinging side to side at about 1 m/s
            Run(10000, t =>
            {
                var x = (float)System.Math.Sin(t / 1000.0 * 2 * System.Math.PI) * 0.15f;
                return (Hand(new Vec3(-0.2f + x, 1.5f, 0.4f)), Hand(new Vec3(0.2f + x, 1.5f, 0.4f)));
            });
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void ReachingForwardToPokeWithOneHandNeverTriggers()
        {
            Run(5000, t => (Hand(new Vec3(-0.2f, 0.9f, 0.3f), Down, 0f, 0.5f),
                new StopHand(0f, new Vec3(0.1f, 1.3f, 0.3f + (float)System.Math.Min(t, 1000) / 1000f * 0.3f), Away, 0.98f, 0.2f, 0.1f, 0.1f)));
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void StretchingBothArmsForwardWithRelaxedCurledFingersNeverTriggers()
        {
            Run(10000, _ => (Hand(new Vec3(-0.2f, 1.4f, 0.6f), new Vec3(0f, -0.6f, 0.8f), 0.1f, 0.55f),
                Hand(new Vec3(0.2f, 1.4f, 0.6f), new Vec3(0f, -0.6f, 0.8f), 0.1f, 0.6f)));
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void OneHandInThePoseAndTheOtherAtTheSideNeverTriggers()
        {
            Run(10000, _ => (Hand(new Vec3(-0.35f, 0.9f, 0.05f), new Vec3(1f, 0f, 0f), 0f, 0.9f), RightPose));
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void HandsInThePoseButMovingNeverTrigger()
        {
            // a slow drift of 0.4 m/s toward the user
            Run(10000, t =>
            {
                var z = 0.4f - (float)((t / 1000.0 * 0.4) % 0.3);
                return (Hand(new Vec3(-0.2f, 1.5f, z)), Hand(new Vec3(0.2f, 1.5f, z)));
            });
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void StillHandsAtTheSpeedLimitDoNotCount()
        {
            Run(5000, t => (Hand(new Vec3(-0.2f, 1.5f, 0.4f)), Hand(new Vec3(0.2f, 1.5f, 0.3f + (float)(t / 1000.0 * (StopGesture.SpeedMax + 0.05)) % 0.3f))));
            Assert.AreEqual(0, Triggers);
        }

        [Test]
        public void StraightnessIsTheCosineBetweenPalmAndFinger()
        {
            var w = new Vec3(0f, 0f, 0f);
            var k = new Vec3(0f, 0.1f, 0f);
            Assert.AreEqual(1f, StopGesture.Straightness(w, k, new Vec3(0f, 0.18f, 0f)), 1e-4f);
            Assert.AreEqual(0f, StopGesture.Straightness(w, k, new Vec3(0.08f, 0.1f, 0f)), 1e-4f);
            Assert.Less(StopGesture.Straightness(w, k, new Vec3(0.02f, 0.05f, 0f)), 0f, "curled back");
            Assert.AreEqual(-1f, StopGesture.Straightness(w, w, k), "unknown reads curled");
        }
    }
}
