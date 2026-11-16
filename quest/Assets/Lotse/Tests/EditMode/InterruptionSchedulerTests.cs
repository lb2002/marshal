#nullable enable
using System.Collections.Generic;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    public class InterruptionSchedulerTests
    {
        static PermRequest R(string id) => new PermRequest { Id = id };

        static readonly double NoGesture = double.NegativeInfinity;

        [Test]
        public void NothingPendingShowsNothing()
        {
            var s = new InterruptionScheduler();
            Assert.IsNull(s.Pick(0, false, NoGesture, new List<PermRequest>()));
        }

        [Test]
        public void NeverShowsWhileACardIsOpen()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            Assert.IsNull(s.Pick(10_000, true, NoGesture, pending));
        }

        [Test]
        public void ShowsOldestWhenQuiet()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1"), R("p2") };
            Assert.AreEqual("p1", s.Pick(0, false, NoGesture, pending)!.Id);
        }

        [Test]
        public void WaitsTwoSecondsAfterTheLastGestureEvent()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            Assert.IsNull(s.Pick(5000, false, 4000, pending));
            Assert.IsNull(s.Pick(5999, false, 4000, pending));
            Assert.AreEqual("p1", s.Pick(6000, false, 4000, pending)!.Id);
        }

        [Test]
        public void EscalatesARequestWaiting45SecondsEvenInsideTheQuietWindow()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            Assert.IsNull(s.Pick(0, false, 0, pending)); // first seen at 0, quiet window still on
            Assert.IsNull(s.Pick(44_999, false, 44_500, pending));
            Assert.AreEqual("p1", s.Pick(45_000, false, 44_900, pending)!.Id);
        }

        [Test]
        public void EscalationPicksTheOldestEscalatedRequest()
        {
            // oldest first is also the order of escalation: the first one to reach 45 s is the oldest
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            s.Pick(0, false, 0, pending);
            pending.Add(R("p2"));
            s.Pick(30_000, false, 29_000, pending);
            Assert.AreEqual("p1", s.Pick(50_000, false, 49_000, pending)!.Id);
        }

        [Test]
        public void EscalationNeedsNoOpenCard()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            s.Pick(0, false, NoGesture, pending);
            Assert.IsNull(s.Pick(60_000, true, NoGesture, pending));
        }

        [Test]
        public void WaitingTimeCountsFromWhenTheRequestWasFirstSeen()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            s.Pick(100_000, true, NoGesture, pending); // first seen at 100 s while a card was open
            Assert.IsNull(s.Pick(144_000, false, 143_000, pending));
            Assert.AreEqual("p1", s.Pick(145_000, false, 143_000, pending)!.Id);
        }

        [Test]
        public void AcceptsAnOptionalFirstSeenTimeForNewRequests()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            // the caller knows p1 has been open since 0 (for example after a reconnect)
            Assert.AreEqual("p1", s.Pick(50_000, false, 49_000, pending, id => id == "p1" ? 0 : (double?)null)!.Id);
        }

        [Test]
        public void FirstSeenFunctionIsOnlyAskedForNewRequests()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            s.Pick(0, false, NoGesture, pending);
            var asked = 0;
            s.Pick(10, false, 0, pending, id => { asked++; return 0; });
            Assert.AreEqual(0, asked);
        }

        [Test]
        public void ClosedRequestsAreForgotten()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("p1") };
            s.Pick(0, false, NoGesture, pending);
            pending.Clear();
            Assert.IsNull(s.Pick(10_000, false, NoGesture, pending));
            pending.Add(R("p1")); // same id again counts as new
            Assert.IsNull(s.Pick(20_000, false, 19_500, pending));
            Assert.AreEqual("p1", s.Pick(21_500, false, 19_500, pending)!.Id);
        }

        [Test]
        public void PickingEveryFrameAllocatesNothing()
        {
            var s = new InterruptionScheduler();
            var pending = new List<PermRequest> { R("a"), R("b"), R("c") };
            s.Pick(0, true, double.NegativeInfinity, pending);
            s.Pick(10, true, double.NegativeInfinity, new List<PermRequest> { R("a") }); // b and c forgotten once
            s.Pick(20, true, double.NegativeInfinity, pending);
            Assert.That(() =>
            {
                for (var i = 0; i < 100; i++) s.Pick(30 + i, true, double.NegativeInfinity, pending);
            }, UnityEngine.TestTools.Constraints.ConstraintExtensions.AllocatingGCMemory(Is.Not));
        }
    }
}
