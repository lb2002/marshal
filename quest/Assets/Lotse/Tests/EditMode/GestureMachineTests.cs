#nullable enable
using System.Collections.Generic;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    public class GestureMachineTests
    {
        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);
        static readonly HandSample Palm = new HandSample(true, true, 0f, true);
        static readonly HandSample Lost = new HandSample(false, false, 0f, false);
        static readonly HandSample LowConf = new HandSample(true, false, 1f, false);

        const double Frame = 20;

        /// <summary>Feeds the same pair of samples every frame for `ms`, collecting every non-None event.</summary>
        static List<GestureEvent> Hold(GestureMachine m, ref double t, HandSample l, HandSample r, double ms)
        {
            var events = new List<GestureEvent>();
            var end = t + ms;
            while (t < end)
            {
                t += Frame;
                var e = m.Update(t, l, r);
                if (e.Kind != GestureEventKind.None) events.Add(e);
            }
            return events;
        }

        static PermRequest Req(string risk, bool truncated = false, string id = "p") => new PermRequest
        {
            Id = id, Truncated = truncated, Risk = new RiskResult { Level = risk },
        };

        /// <summary>A machine with the card open and both hands already seen open (ready for a fresh pinch).</summary>
        static GestureMachine Card(PermRequest r, out double t)
        {
            var m = new GestureMachine();
            t = 0;
            m.OpenCard(r);
            Hold(m, ref t, Open, Open, 40);
            return m;
        }

        // ---- the matrix: risk x gesture -> outcome ---------------------------------------------

        // gesture: "none", "pinch" (one hand, 2 s), "two" (both hands, 2 s)
        [TestCase("safe", false, "none", null)]
        [TestCase("safe", false, "pinch", "pinch-hold")]
        [TestCase("safe", false, "two", "pinch-hold")]
        [TestCase("network", false, "none", null)]
        [TestCase("network", false, "pinch", "pinch-hold")]
        [TestCase("network", false, "two", "pinch-hold")]
        [TestCase("destructive", false, "none", null)]
        [TestCase("destructive", false, "pinch", null)]
        [TestCase("destructive", false, "two", "two-hand")]
        [TestCase("critical", false, "pinch", null)]
        [TestCase("critical", false, "two", "two-hand")]
        [TestCase("safe", true, "none", null)]
        [TestCase("safe", true, "pinch", null)]
        [TestCase("safe", true, "two", "two-hand")]
        [TestCase("network", true, "pinch", null)]
        [TestCase("network", true, "two", "two-hand")]
        [TestCase("destructive", true, "pinch", null)]
        [TestCase("destructive", true, "two", "two-hand")]
        public void RiskTimesGesture(string risk, bool truncated, string gesture, string? expectedConfirm)
        {
            var m = Card(Req(risk, truncated, "p7"), out var t);
            var events = gesture switch
            {
                "pinch" => Hold(m, ref t, Open, Pinch, 2000),
                "two" => Hold(m, ref t, Pinch, Pinch, 2000),
                _ => Hold(m, ref t, Open, Open, 2000),
            };
            if (expectedConfirm == null)
            {
                Assert.AreEqual(0, events.Count);
            }
            else
            {
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(GestureEventKind.Confirmed, events[0].Kind);
                Assert.AreEqual(expectedConfirm, events[0].Confirm);
                Assert.AreEqual("p7", events[0].Id, "a confirmation is bound to the card's request id");
            }
        }

        [Test]
        public void SingleHandPinchOnDestructiveNeverConfirmsEvenForAMinute()
        {
            var m = Card(Req("destructive"), out var t);
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Open, 60_000).Count);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 60_000).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        // ---- timing ----------------------------------------------------------------------------

        [Test]
        public void PinchHoldConfirmsAfterExactly800MsOfPinching()
        {
            var m = Card(Req("safe"), out var t);
            double firstPinch = t + Frame;
            GestureEvent e = GestureEvent.None;
            while (e.Kind == GestureEventKind.None && t < 5000)
            {
                t += Frame;
                e = m.Update(t, Open, Pinch);
            }
            Assert.AreEqual(GestureEventKind.Confirmed, e.Kind);
            Assert.AreEqual("pinch-hold", e.Confirm);
            Assert.AreEqual(800, t - firstPinch, 0.001);
        }

        [Test]
        public void ProgressRisesFromZeroToOneWhilePinchHolding()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 20);
            Assert.AreEqual(GestureState.PinchHolding, m.State);
            Assert.AreEqual(0f, m.Progress, 0.001f);
            Hold(m, ref t, Open, Pinch, 400);
            Assert.AreEqual(0.5f, m.Progress, 0.001f);
            Hold(m, ref t, Open, Pinch, 360);
            Assert.Less(m.Progress, 1f);
            Assert.Greater(m.Progress, 0.9f);
        }

        [Test]
        public void TwoHandHoldConfirmsAfterExactly1500MsNotBefore()
        {
            var m = Card(Req("destructive"), out var t);
            double firstPinch = t + Frame;
            GestureEvent e = GestureEvent.None;
            var sawTwoHand = false;
            while (e.Kind == GestureEventKind.None && t < 10_000)
            {
                t += Frame;
                e = m.Update(t, Pinch, Pinch);
                sawTwoHand |= m.State == GestureState.TwoHandHolding;
            }
            Assert.IsTrue(sawTwoHand);
            Assert.AreEqual(GestureEventKind.Confirmed, e.Kind);
            Assert.AreEqual("two-hand", e.Confirm);
            Assert.AreEqual(1500, t - firstPinch, 0.001);
        }

        [Test]
        public void ConfirmedOnceThenIdleAndNoSecondConfirm()
        {
            var m = Card(Req("safe"), out var t);
            Assert.AreEqual(1, Hold(m, ref t, Open, Pinch, 3000).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        [Test]
        public void NoCardMeansNoConfirm()
        {
            var m = new GestureMachine();
            double t = 0;
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 3000).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        // ---- rising edge: a pinch that was already there when the card opened never confirms -----

        [Test]
        public void PinchHeldAcrossOpenCardNeverConfirms()
        {
            var m = new GestureMachine();
            double t = 0;
            Hold(m, ref t, Open, Pinch, 100); // the user pinches for something else
            m.OpenCard(Req("safe"));
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 5000).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        [Test]
        public void ReleaseThenPinchConfirmsAfterOpenCard()
        {
            var m = new GestureMachine();
            double t = 0;
            Hold(m, ref t, Open, Pinch, 100);
            m.OpenCard(Req("safe"));
            Hold(m, ref t, Open, Pinch, 1000);
            Hold(m, ref t, Open, Open, Frame); // one released frame is enough
            Assert.AreEqual(1, Hold(m, ref t, Open, Pinch, 1000).Count);
        }

        [Test]
        public void PinchHeldAcrossOpenCardNeverConfirmsTwoHand()
        {
            var m = new GestureMachine();
            double t = 0;
            Hold(m, ref t, Pinch, Pinch, 100);
            m.OpenCard(Req("destructive"));
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 5000).Count);
        }

        [Test]
        public void TwoHandNeedsBothHandsSeenReleased()
        {
            var m = new GestureMachine();
            double t = 0;
            Hold(m, ref t, Pinch, Pinch, 100);
            m.OpenCard(Req("destructive"));
            Hold(m, ref t, Open, Pinch, Frame * 2); // only the left hand lets go
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 3000).Count);
            Hold(m, ref t, Open, Open, Frame);
            Assert.AreEqual(1, Hold(m, ref t, Pinch, Pinch, 2000).Count);
        }

        [Test]
        public void OpenCardMidHoldCancelsTheHoldAndNeedsANewPinch()
        {
            var m = Card(Req("safe", id: "A"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            Assert.AreEqual(GestureState.PinchHolding, m.State);
            m.OpenCard(Req("safe", id: "B"));
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(0f, m.Progress);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 3000).Count, "the old pinch does not carry over");
            Hold(m, ref t, Open, Open, Frame);
            var events = Hold(m, ref t, Open, Pinch, 1000);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("B", events[0].Id);
        }

        [Test]
        public void DestructiveCardOpenedMidPinchStillNeedsTheTwoHandHold()
        {
            var m = Card(Req("safe", id: "A"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            m.OpenCard(Req("destructive", id: "B"));
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 5000).Count, "single hand never");
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 5000).Count, "right hand never released since the card opened");
            Hold(m, ref t, Open, Open, Frame);
            var events = Hold(m, ref t, Pinch, Pinch, 2000);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("two-hand", events[0].Confirm);
            Assert.AreEqual("B", events[0].Id);
        }

        // ---- a second hand joining ------------------------------------------------------------

        [Test]
        public void SecondHandJoiningAOneHandPinchKeepsTheRunningPinchHold()
        {
            var m = Card(Req("safe"), out var t);
            double start = t + Frame;
            Hold(m, ref t, Open, Pinch, 400);
            var events = new List<GestureEvent>();
            var at = 0.0;
            while (events.Count == 0 && t < 5000)
            {
                events.AddRange(Hold(m, ref t, Pinch, Pinch, Frame));
                at = t;
            }
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("pinch-hold", events[0].Confirm);
            Assert.AreEqual(800, at - start, 0.001, "the hold was not restarted by the second hand");
        }

        [Test]
        public void SecondHandJoiningOnDestructiveStartsTheTwoHandHoldFromTheJoin()
        {
            var m = Card(Req("destructive"), out var t);
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Open, 1000).Count);
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 1400).Count);
            var events = Hold(m, ref t, Pinch, Pinch, 200);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("two-hand", events[0].Confirm);
        }

        // ---- frame gaps -----------------------------------------------------------------------

        [Test]
        public void FrameGapOver200MsCancelsTheHoldAndNeedsANewPinch()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            t += 500;
            Assert.AreEqual(GestureEventKind.None, m.Update(t, Open, Pinch).Kind);
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 3000).Count, "pinch unseen during the gap: release first");
            Hold(m, ref t, Open, Open, Frame);
            Assert.AreEqual(1, Hold(m, ref t, Open, Pinch, 1000).Count);
        }

        [Test]
        public void FrameGapOf200MsOrLessKeepsTheHold()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            t += 200;
            m.Update(t, Open, Pinch);
            Assert.AreEqual(GestureState.PinchHolding, m.State);
        }

        [Test]
        public void TimeGoingBackwardsCancelsTheHold()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            Assert.AreEqual(GestureEventKind.None, m.Update(t - 10, Open, Pinch).Kind);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        [Test]
        public void OpeningACardRestartsThePalmTimer()
        {
            var m = new GestureMachine();
            double t = 0;
            Hold(m, ref t, Open, Open, 40);
            Assert.AreEqual(0, Hold(m, ref t, Palm, Open, 260).Count, "a palm raised before the card");
            m.OpenCard(Req("safe"));
            Assert.AreEqual(0, Hold(m, ref t, Palm, Open, 260).Count, "the card has to be shown for 300 ms of palm");
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 60).Count);
        }

        [Test]
        public void FrameGapRestartsThePalmTimer()
        {
            var m = new GestureMachine();
            double t = 0;
            Hold(m, ref t, Palm, Open, 260);
            t += 500;
            Assert.AreEqual(GestureEventKind.None, m.Update(t, Palm, Open).Kind);
            Assert.AreEqual(0, Hold(m, ref t, Palm, Open, 260).Count);
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 100).Count);
        }

        // ---- cancel paths: nothing is ever sent ------------------------------------------------

        [Test]
        public void ReleasingThePinchMidHoldCancelsSilently()
        {
            var m = Card(Req("safe"), out var t);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 600).Count);
            Assert.AreEqual(0, Hold(m, ref t, Open, Open, 100).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(0f, m.Progress);
            // a new hold starts from zero
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 700).Count);
            Assert.AreEqual(1, Hold(m, ref t, Open, Pinch, 200).Count);
        }

        [Test]
        public void TrackingLossMidPinchHoldSendsNothing()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 600);
            Assert.AreEqual(0, Hold(m, ref t, Open, Lost, 100).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
            Hold(m, ref t, Open, Open, Frame);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 700).Count, "restarts from zero after the loss");
        }

        [Test]
        public void ConfidenceLossMidPinchHoldSendsNothing()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 600);
            Assert.AreEqual(0, Hold(m, ref t, Open, LowConf, 100).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        [Test]
        public void LosingTheOtherHandDoesNotCancelAPinchHold()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            var events = Hold(m, ref t, Lost, Pinch, 500);
            Assert.AreEqual(1, events.Count);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TrackingLossOfEitherHandMidTwoHandHoldSendsNothing(bool loseLeft)
        {
            var m = Card(Req("destructive"), out var t);
            Hold(m, ref t, Pinch, Pinch, 1000);
            Assert.AreEqual(GestureState.TwoHandHolding, m.State);
            var events = loseLeft ? Hold(m, ref t, Lost, Pinch, 2000) : Hold(m, ref t, Pinch, Lost, 2000);
            Assert.AreEqual(0, events.Count);
            Assert.AreEqual(GestureState.Idle, m.State);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ConfidenceLossOfEitherHandMidTwoHandHoldSendsNothing(bool lowLeft)
        {
            var m = Card(Req("destructive"), out var t);
            Hold(m, ref t, Pinch, Pinch, 1000);
            var events = lowLeft ? Hold(m, ref t, LowConf, Pinch, 2000) : Hold(m, ref t, Pinch, LowConf, 2000);
            Assert.AreEqual(0, events.Count);
        }

        [Test]
        public void TwoHandHoldInterruptedRestartsFromZero()
        {
            var m = Card(Req("destructive"), out var t);
            Hold(m, ref t, Pinch, Pinch, 1200);
            Hold(m, ref t, Open, Open, Frame * 2);
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 1400).Count);
            Assert.AreEqual(1, Hold(m, ref t, Pinch, Pinch, 200).Count);
        }

        [Test]
        public void OneHandDroppingBelowPinchOffCancelsTheTwoHandHold()
        {
            var m = Card(Req("destructive"), out var t);
            Hold(m, ref t, Pinch, Pinch, 1000);
            Assert.AreEqual(GestureState.TwoHandHolding, m.State);
            var loose = new HandSample(true, true, GestureMachine.PinchOff - 0.05f, false);
            Assert.AreEqual(0, Hold(m, ref t, loose, Pinch, 40).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(0f, m.Progress);
            // a new two-hand hold starts from zero
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 1400).Count);
            Assert.AreEqual(1, Hold(m, ref t, Pinch, Pinch, 200).Count);
        }

        [Test]
        public void ResetMidHoldSendsNothingAndClearsTheCard()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 600);
            m.Reset();
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(0f, m.Progress);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 3000).Count, "no card after Reset");
            m.OpenCard(Req("safe"));
            Hold(m, ref t, Open, Open, Frame);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 700).Count);
            Assert.AreEqual(1, Hold(m, ref t, Open, Pinch, 200).Count);
        }

        [Test]
        public void ResetMidTwoHandHoldSendsNothing()
        {
            var m = Card(Req("destructive"), out var t);
            Hold(m, ref t, Pinch, Pinch, 1000);
            m.Reset();
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(0, Hold(m, ref t, Pinch, Pinch, 3000).Count);
        }

        // ---- palm stop -------------------------------------------------------------------------

        [TestCase(true)]
        [TestCase(false)]
        public void PalmForwardFor300MsStopsFromEitherHand(bool left)
        {
            var m = new GestureMachine();
            double t = 0;
            var events = left ? Hold(m, ref t, Palm, Open, 400) : Hold(m, ref t, Open, Palm, 400);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(GestureEventKind.Stop, events[0].Kind);
            Assert.AreEqual(GestureState.Stopped, m.State);
        }

        [Test]
        public void PalmForwardBelow300MsDoesNotStop()
        {
            var m = new GestureMachine();
            double t = 0;
            Assert.AreEqual(0, Hold(m, ref t, Palm, Open, 280).Count);
            Assert.AreEqual(0, Hold(m, ref t, Open, Open, 100).Count);
            Assert.AreEqual(GestureState.Idle, m.State);
            // the timer restarted: another 280 ms is still not enough
            Assert.AreEqual(0, Hold(m, ref t, Palm, Open, 280).Count);
        }

        [Test]
        public void StopFiresExactlyAtThreeHundredMs()
        {
            var m = new GestureMachine();
            double t = 1000;
            m.Update(t, Palm, Open);
            while (t < 1280)
            {
                t += Frame;
                Assert.AreEqual(GestureEventKind.None, m.Update(t, Palm, Open).Kind, "t=" + t);
            }
            t += Frame;
            Assert.AreEqual(1300, t);
            Assert.AreEqual(GestureEventKind.Stop, m.Update(t, Palm, Open).Kind);
        }

        [Test]
        public void StopFiresOnceWhileThePalmStaysUp()
        {
            var m = new GestureMachine();
            double t = 0;
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 3000).Count);
            Assert.AreEqual(GestureState.Stopped, m.State);
            Hold(m, ref t, Open, Open, 40);
            Assert.AreEqual(GestureState.Idle, m.State);
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 400).Count, "a new palm gesture stops again");
        }

        [Test]
        public void ResetWhilePalmStillRaisedDoesNotFireStopAgain()
        {
            var m = new GestureMachine();
            double t = 0;
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 400).Count);
            m.Reset();
            Assert.AreEqual(0, Hold(m, ref t, Palm, Open, 2000).Count, "the palm has not been lowered since");
            Hold(m, ref t, Open, Open, Frame);
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 400).Count);
        }

        [Test]
        public void ResetWhileStoppedStaysStoppedUntilThePalmIsLowered()
        {
            // the juror stops a card, the card closes (Reset) and the next one opens while the palm is still up
            var m = Card(Req("safe", id: "p1"), out var t);
            Assert.AreEqual(1, Hold(m, ref t, Palm, Open, 400).Count);
            m.Reset();
            Assert.AreEqual(GestureState.Stopped, m.State, "Reset does not end a stop while the palm is raised");
            m.OpenCard(Req("safe", id: "p2"));
            Hold(m, ref t, Palm, Open, 40);
            Assert.AreEqual(0, Hold(m, ref t, Palm, Pinch, 2000).Count, "no confirm while the palm is raised");
            Assert.AreEqual(GestureState.Stopped, m.State);
            Assert.AreEqual(0, Hold(m, ref t, Open, Pinch, 2000).Count, "palm lowered, but the pinch is not fresh");
            Hold(m, ref t, Open, Open, 40);
            var events = Hold(m, ref t, Open, Pinch, 900);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("p2", events[0].Id);
        }

        [Test]
        public void PalmStopWorksWithoutACardAndCancelsAHold()
        {
            var m = Card(Req("safe"), out var t);
            Hold(m, ref t, Open, Pinch, 400);
            var events = Hold(m, ref t, Palm, Pinch, 400);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(GestureEventKind.Stop, events[0].Kind);
            Assert.AreEqual(GestureState.Stopped, m.State);
        }

        [Test]
        public void PalmWithoutTrackingOrConfidenceDoesNotStop()
        {
            var m = new GestureMachine();
            double t = 0;
            Assert.AreEqual(0, Hold(m, ref t, new HandSample(false, true, 0f, true), Open, 1000).Count);
            Assert.AreEqual(0, Hold(m, ref t, new HandSample(true, false, 0f, true), Open, 1000).Count);
        }
    }
}
