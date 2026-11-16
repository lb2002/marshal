#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.App.Tests
{
    /// <summary>The whole demo pack through the session (U5a: requests wait for the user to select their agent).</summary>
    public class DemoSessionTests
    {
        static string PackPath => Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json");
        static DemoPack LoadPack() => DemoPack.Parse(File.ReadAllText(PackPath));

        const double Frame = 20;

        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);
        static readonly HandSample Lost = new HandSample(false, false, 0f, false);

        /// <summary>
        /// A scripted juror. The window is on screen. When a notice comes it selects that agent (as a poke on the pill
        /// would); it waits `thinkMs` after a block appears, then approves it with the gesture the block asks for, or,
        /// for destructive requests when `stopDangerous`, presses "Ablehnen".
        /// </summary>
        sealed class Juror
        {
            readonly DemoSession _s;
            readonly bool _stopDangerous;
            readonly double _thinkMs;
            string? _cardId;
            double _cardSince;

            public readonly List<string> Reviews = new List<string>();
            public readonly List<string> Notices = new List<string>();
            public int CardChanges;

            public Juror(DemoSession s, bool stopDangerous, double thinkMs = 1000)
            {
                _s = s;
                _stopDangerous = stopDangerous;
                _thinkMs = thinkMs;
                s.SetViewShown(true);
                s.ReviewLine += Reviews.Add;
                s.CardChanged += () => CardChanges++;
                s.NoticeRaised += r =>
                {
                    Notices.Add(r.Id);
                    if (s.Card == null) s.Select(r.Sid);
                };
            }

            public void Frame()
            {
                var left = Open;
                var right = Open;
                var card = _s.Card;
                if (card == null && _s.Store.Pending.Count > 0 && _s.Selected != _s.Store.Pending[0].Sid && _s.NowMs > _cardSince + _thinkMs)
                    _s.Select(_s.Store.Pending[0].Sid); // the row in red: selected by hand
                card = _s.Card;
                if (card != null && card.Id != _cardId)
                {
                    _cardId = card.Id;
                    _cardSince = _s.NowMs;
                }

                if (card != null && _s.Mode == CardMode.Hold && _s.NowMs - _cardSince >= _thinkMs)
                {
                    if (card.Risk.IsDestructive && _stopDangerous)
                    {
                        Assert.IsTrue(_s.Deny(card.Id));
                    }
                    else
                    {
                        right = Pinch;
                        if (card.NeedsTwoHand) left = Pinch;
                    }
                }
                _s.Step(DemoSessionTests.Frame, left, right);
            }
        }

        static void RunToEnd(DemoSession s, Juror j)
        {
            var guard = 0;
            while (!s.Finished && guard++ < 200000) j.Frame();
            Assert.IsTrue(s.Finished, "the demo plays to the end");
        }

        [Test]
        public void WholeDemoWithBothDangerousCommandsDenied()
        {
            var s = new DemoSession(LoadPack());
            var j = new Juror(s, stopDangerous: true);
            RunToEnd(s, j);

            Assert.AreEqual(19, s.Decisions.Count);
            var denies = s.Decisions.Where(d => d.Behavior == "deny").ToList();
            CollectionAssert.AreEquivalent(new[] { "p11", "p14" }, denies.Select(d => d.Id));
            Assert.IsTrue(denies.All(d => d.Message == null && d.Confirm == null), "Ablehnen sends a plain deny");
            Assert.IsTrue(s.Decisions.Where(d => d.Behavior == "allow").All(d => d.Confirm == "pinch-hold"));
            Assert.AreEqual(2, s.Stats.DangerousStopped);
            Assert.AreEqual(j.Notices.Count, j.Notices.Distinct().Count(), "one notice per request at most");

            // the four agents each finished once
            Assert.AreEqual(4, j.Reviews.Count, string.Join(" | ", j.Reviews));
            StringAssert.StartsWith("Agent 1: Done:", j.Reviews[0]);
            Assert.IsFalse(j.Reviews.Any(r => r.Contains("Fertig:")), "the demo speaks English (pack a4ecdbf)");

            // the juror's 19 waits of about a second are on top of the recorded waiting time
            var total = Stats.TotalWaitingMs(s.Store.Agents);
            Assert.Greater(total, 19 * 1000.0);
            StringAssert.EndsWith("2 dangerous commands stopped.", Texts.EndSummary(total, s.Stats.DangerousStopped));
        }

        [Test]
        public void WholeDemoApprovingDangerousCommandsNeedsTwoHands()
        {
            var s = new DemoSession(LoadPack());
            var j = new Juror(s, stopDangerous: false);
            RunToEnd(s, j);

            Assert.AreEqual(19, s.Decisions.Count);
            Assert.IsTrue(s.Decisions.All(d => d.Behavior == "allow"));
            Assert.AreEqual("two-hand", s.Decisions.Single(d => d.Id == "p11").Confirm);
            Assert.AreEqual("two-hand", s.Decisions.Single(d => d.Id == "p14").Confirm);
            Assert.AreEqual(0, s.Stats.DangerousStopped);
        }

        /// <summary>Plays until the block with this id is shown.</summary>
        static void PlayUntilCard(DemoSession s, string id)
        {
            var j = new Juror(s, stopDangerous: false);
            var guard = 0;
            while (s.Card?.Id != id && guard++ < 200000) j.Frame();
            Assert.AreEqual(id, s.Card?.Id);
        }

        /// <summary>Plays (approving everything) until this request is pending, without selecting its agent.</summary>
        static void PlayUntilPending(DemoSession s, string id)
        {
            s.SetViewShown(true);
            var guard = 0;
            string? cardId = null;
            double since = 0;
            while (!s.Store.Pending.Any(p => p.Id == id) && guard++ < 200000)
            {
                var right = Open;
                if (s.Card == null && s.Store.Pending.Count > 0 && s.Store.Pending[0].Id != id) s.Select(s.Store.Pending[0].Sid);
                if (s.Card != null && s.Card.Id != cardId)
                {
                    cardId = s.Card.Id;
                    since = s.NowMs;
                }
                if (s.Card != null && s.NowMs - since > 100) right = Pinch;
                s.Step(Frame, Open, right);
            }
            Assert.IsTrue(s.Store.Pending.Any(p => p.Id == id));
        }

        static void Hold(DemoSession s, double ms, HandSample left, HandSample right)
        {
            for (double t = 0; t < ms; t += Frame) s.Step(Frame, left, right);
        }

        [Test]
        public void NothingOpensByItselfTheRequestOnlyGetsANotice()
        {
            var s = new DemoSession(LoadPack());
            var notices = new List<string>();
            s.NoticeRaised += r => notices.Add(r.Id);
            s.SetViewShown(true);
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 200000) s.Step(Frame, Open, Open);
            Hold(s, 5000, Open, Open);
            Assert.AreEqual("p1", s.Store.Pending.Single().Id);
            Assert.IsNull(s.Card, "no block for an agent the user has not selected");
            CollectionAssert.AreEqual(new[] { "p1" }, notices, "only the notice");
            s.Select("demo-a1");
            Assert.AreEqual("p1", s.Card?.Id, "selecting the agent shows its block");
            Assert.AreEqual(CardMode.Hold, s.Mode);
        }

        [Test]
        public void AnUnselectedAgentsRequestCannotBeApprovedByAnyGesture()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilPending(s, "p1");
            s.Select("demo-a2");
            Assert.IsNull(s.Card);
            Hold(s, 40, Open, Open);
            Hold(s, 5000, Pinch, Pinch);
            Hold(s, 40, Open, Open);
            Hold(s, 5000, Open, Pinch);
            Assert.AreEqual(0, s.Decisions.Count);
            Assert.IsTrue(s.Store.Pending.Any(p => p.Id == "p1"), "still open");
        }

        [Test]
        public void OneHandNeverApprovesADangerousRequest()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilCard(s, "p11");
            var before = s.Decisions.Count;
            Hold(s, 40, Open, Open);
            Hold(s, 5000, Open, Pinch);
            Hold(s, 40, Open, Open);
            Hold(s, 5000, Pinch, Open);
            Assert.AreEqual("p11", s.Card?.Id);
            Assert.AreEqual(before, s.Decisions.Count);
        }

        [Test]
        public void TrackingLossDuringAHoldDecidesNothing()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilCard(s, "p1");
            Hold(s, 40, Open, Open);
            Hold(s, 600, Open, Pinch);
            Assert.Greater(s.HoldProgress, 0.5f);
            Hold(s, 40, Open, Lost);
            Assert.AreEqual(0f, s.HoldProgress);
            Assert.AreEqual(0, s.Decisions.Count);
            Assert.AreEqual("p1", s.Card?.Id);
        }

        [Test]
        public void SpaeterSendsNothingAndSelectingAgainShowsTheBlock()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilCard(s, "p1");
            s.Later();
            Assert.IsNull(s.Card);
            Hold(s, 5000, Open, Open);
            Assert.AreEqual(0, s.Decisions.Count);
            Assert.IsTrue(s.Store.Pending.Any(p => p.Id == "p1"));
            s.Select("demo-a1");
            Assert.AreEqual("p1", s.Card?.Id);
        }

        [Test]
        public void AblehnenDeniesTheShownRequest()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilCard(s, "p11");
            Assert.IsFalse(s.Deny("p1"), "only the shown request");
            Assert.IsTrue(s.Deny("p11"));
            Assert.AreEqual("deny", s.Decisions.Last().Behavior);
            Assert.AreEqual("p11", s.Decisions.Last().Id);
            Assert.IsNull(s.Decisions.Last().Message);
            Assert.AreEqual(1, s.Stats.DangerousStopped);
            Assert.IsNull(s.Card, "the pack closes it at once");
        }

        [Test]
        public void ASubjectTheBlockCouldNotShowInFullNeedsBothHands()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilCard(s, "p1");
            var changes = 0;
            s.CardChanged += () => changes++;
            Assert.IsFalse(s.Card!.NeedsTwoHand);
            Assert.IsTrue(s.SubjectCut("p1"), "the block re-renders");
            Assert.AreEqual(1, changes);
            Assert.IsTrue(s.Card.NeedsTwoHand);
            Assert.IsFalse(s.SubjectCut("p1"), "once");
            Assert.IsFalse(s.SubjectCut("p2"), "only the block shown");

            Hold(s, 40, Open, Open);
            Hold(s, 3000, Open, Pinch);
            Assert.AreEqual(0, s.Decisions.Count, "one hand no longer approves it");
            Hold(s, 40, Open, Open);
            Hold(s, GestureMachine.TwoHandHoldMs + 3 * Frame, Pinch, Pinch);
            Assert.AreEqual("two-hand", s.Decisions.Single().Confirm);
        }

        [Test]
        public void AStepWithABlockShownAllocatesNothing()
        {
            var s = new DemoSession(LoadPack());
            PlayUntilCard(s, "p1");
            var pose = new HoldPose(new Vec3(0f, 1.6f, 0f), new Vec3(0f, 0f, 1f), new Vec3(0f, 1.7f, 0.6f), 1.35f,
                new Vec3(-0.1f, 1.45f, 0.35f), new Vec3(0.1f, 1.45f, 0.35f));
            for (var i = 0; i < 10; i++) s.Step(Frame, Open, Open, pose); // warm up
            Assert.That(() =>
            {
                for (var i = 0; i < 100; i++) s.Step(Frame, Open, Open, pose);
            }, UnityEngine.TestTools.Constraints.ConstraintExtensions.AllocatingGCMemory(Is.Not));
            Assert.AreEqual("p1", s.Card?.Id);
        }

        [Test]
        public void TheNextNoticeWaitsForTheQuietTimeAfterAGesture()
        {
            // p4 opens one second (pack time) after p3: its notice has to wait for the quiet time after p3's gesture
            var s = new DemoSession(LoadPack());
            var notices = new List<(string id, double at)>();
            s.NoticeRaised += r => notices.Add((r.Id, s.NowMs));
            PlayUntilCard(s, "p3");
            Hold(s, 40, Open, Open);
            var frames = 0;
            while (s.Card != null && frames++ < 100) s.Step(Frame, Open, Pinch);
            Assert.IsNull(s.Card, "p3 approved");
            var decidedAt = s.NowMs; // the frame of the confirming gesture
            double? p4PendingAt = null;
            var guard = 0;
            while (!notices.Any(n => n.id == "p4") && guard++ < 100000)
            {
                s.Step(Frame, Open, Open);
                if (p4PendingAt == null && s.Store.Pending.Any(p => p.Id == "p4")) p4PendingAt = s.NowMs;
            }
            var p4 = notices.Single(n => n.id == "p4");
            Assert.Less(p4PendingAt!.Value - decidedAt, InterruptionScheduler.QuietMs, "p4 was pending before the quiet time ran out");
            Assert.GreaterOrEqual(p4.at - decidedAt, InterruptionScheduler.QuietMs - Frame);
        }
    }
}
