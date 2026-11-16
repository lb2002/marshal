#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U14: the tour's rules. A step ends only on its own real event; a done step shows its word for a second; the hint
    /// comes after 20 s; "Skip" leaves a step undone; the log has one line per begin, done, skip and hint.
    /// </summary>
    public class TourTests
    {
        readonly List<string> _lines = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _lines.Clear();
            Ev.Sink = _lines.Add;
        }

        [TearDown]
        public void TearDown() => Ev.Sink = null;

        int Lines(string exact) => _lines.Count(l => l == Ev.Prefix + exact);

        static readonly TourFacts Quiet = new TourFacts { WindowOpen = true };
        /// <summary>The row of the step's request is open.</summary>
        static readonly TourFacts Shown = new TourFacts { WindowOpen = true, RequestShown = true };

        // ---- U14b: an approval step says how to open its request while no row shows it ----

        [TestCase(2, "One agent is waiting for you. Tap the raised line.", "It wants to change one file. Pinch and hold until the bar is dark.",
            "Touch thumb and index finger, keep them together.")]
        [TestCase(3, "This one deletes a folder for good. Tap the raised line.", "This one deletes a folder for good. Pinch with both hands and hold.",
            "Both hands up, thumb and index finger together.")]
        public void AnApprovalStepsInstructionFollowsWhetherItsRequestIsOpen(int index, string waiting, string gesture, string gestureHint)
        {
            var t = At(index);
            Assert.AreEqual(waiting, t.Text, "nothing is open (after Skip, a deny, Later, a halt): no gesture is asked for");
            t.Step(100, Quiet);
            var version = t.Version;
            t.Step(100, Quiet);
            Assert.AreEqual(version, t.Version, "nothing changed: the band is not drawn again");
            t.Step(100, Shown);
            Assert.AreEqual(gesture, t.Text, "the request's row is open: the gesture");
            Assert.Greater(t.Version, version, "the band is told");
            version = t.Version;
            t.Step(100, Quiet);
            Assert.AreEqual(waiting, t.Text, "closed again (Later, or the next request is another agent's)");
            Assert.Greater(t.Version, version);
            Assert.AreEqual("", t.Note);
            // the hint fits both states
            t.Step(Tour.HintMs, Quiet);
            Assert.IsTrue(t.HintShown);
            Assert.AreEqual("Point at the raised line and pinch briefly.", t.Note);
            version = t.Version;
            t.Step(100, Shown);
            Assert.AreEqual(gestureHint, t.Note);
            Assert.Greater(t.Version, version);
            Assert.AreEqual(index, t.Index, "opening and closing is not the step's event");
            Assert.AreEqual(TourPhase.Instruction, t.Phase);
            Assert.AreEqual(1, Lines("tour step=" + (index + 1) + " name=" + Tour.Steps[index].Id + " phase=hint"));
        }

        [Test]
        public void TheStepAfterAnOpenRowBeginsWithTheGestureAndOtherStepsHaveOneInstruction()
        {
            // "open" is done with the row open: "one-hand" begins with its gesture at once
            var t = At(1);
            t.Step(100, new TourFacts { WindowOpen = true, RequestRowOpen = true, RequestShown = true });
            Assert.AreEqual("Opened.", t.Text);
            t.Step(Tour.ConfirmMs, Shown);
            Assert.AreEqual(2, t.Index);
            Assert.AreEqual(Tour.Steps[2].Instruction, t.Text);
            // the row closed while the word showed: the step begins with "Tap the raised line"
            t = At(1);
            t.Step(100, new TourFacts { WindowOpen = true, RequestRowOpen = true, RequestShown = true });
            t.Step(Tour.ConfirmMs, Quiet);
            Assert.AreEqual(Tour.Steps[2].Waiting, t.Text);
            // stopped: the halt's sentence, whatever is open
            t.Step(100, new TourFacts { WindowOpen = true, Halted = true });
            Assert.AreEqual(Tour.HaltedText, t.Text);
            foreach (var s in Tour.Steps)
            {
                var approval = s.Advance == TourAdvance.OneHand || s.Advance == TourAdvance.TwoHand;
                Assert.AreEqual(approval, s.Waiting.Length > 0, s.Id);
                Assert.AreEqual(approval, s.WaitingHint.Length > 0, s.Id);
                if (approval) StringAssert.DoesNotContain("raised line", s.Instruction + s.Hint, s.Id + ": open, there is no line to tap");
            }
            for (var i = 0; i < Tour.Steps.Length; i++)
            {
                if (Tour.Steps[i].Waiting.Length > 0 || i == 6) continue;
                t = At(i);
                var version = t.Version;
                t.Step(1, Shown);
                t.Step(1, Quiet);
                if (t.Index == i && t.Phase == TourPhase.Instruction) Assert.AreEqual(version, t.Version, Tour.Steps[i].Id + " does not care");
            }
        }

        /// <summary>A tour standing at step `index` (0-based), reached by skipping; halted when the step is "continue".</summary>
        static Tour At(int index)
        {
            var t = new Tour();
            t.Begin();
            while (t.Index < index)
            {
                t.SkipStep();
                // "continue" stays only while everything is stopped
                if (t.Index == 6 && index == 6) t.Step(0, new TourFacts { Halted = true, WindowOpen = true });
            }
            return t;
        }

        /// <summary>Everything a user can do, except what `own` step is about.</summary>
        static void EverythingBut(Tour t, TourAdvance own)
        {
            var halted = own == TourAdvance.Released; // "continue" begins stopped
            if (own != TourAdvance.OneHand) t.Decided(true, false);
            if (own != TourAdvance.TwoHand) t.Decided(true, true);
            if (own != TourAdvance.Deny) t.Decided(false, false);
            var f = new TourFacts { Halted = halted, WindowOpen = true };
            t.Step(100, f);
            f.Pinching = own != TourAdvance.TimeOrPinch;
            t.Step(100, f);
            f.RequestRowOpen = own != TourAdvance.RowOpen;
            t.Step(100, f);
            if (own != TourAdvance.Unfolded)
            {
                f.Folded = true;
                f.WindowOpen = false;
                t.Step(100, f);
                f.Folded = false;
                f.WindowOpen = true;
                t.Step(100, f);
            }
            if (own != TourAdvance.Halted && own != TourAdvance.Released)
            {
                f.Halted = true;
                t.Step(100, f);
                f.Halted = false;
                t.Step(100, f);
            }
        }

        [Test]
        public void TheStepsAreTheEightOfTheBriefAndEveryGestureStepHasAHintAndAWord()
        {
            CollectionAssert.AreEqual(new[] { "lines", "open", "one-hand", "two-hand", "deny", "stop", "continue", "fold" },
                Tour.Steps.Select(s => s.Id).ToArray());
            Assert.AreEqual("Each line is one agent. The dot shows where it is.", Tour.Steps[0].Instruction);
            Assert.AreEqual("It wants to change one file. Pinch and hold until the bar is dark.", Tour.Steps[2].Instruction);
            foreach (var s in Tour.Steps.Skip(1))
            {
                Assert.IsNotEmpty(s.Hint, s.Id + " has a hint");
                Assert.IsNotEmpty(s.Done, s.Id + " has its word");
                Assert.IsFalse(s.Done.Trim().Contains(" "), s.Id + ": one word");
                Assert.AreNotEqual(s.Instruction, s.Hint);
            }
            Assert.AreEqual(TestNeed.HarmlessRequest, Tour.Steps[1].Need);
            Assert.AreEqual(TestNeed.HarmlessRequest, Tour.Steps[2].Need);
            Assert.AreEqual(TestNeed.DangerousRequest, Tour.Steps[3].Need);
            Assert.AreEqual(TestNeed.HarmlessRequest, Tour.Steps[4].Need);
        }

        [Test]
        public void EveryStepEndsOnlyOnItsOwnEvent()
        {
            for (var i = 0; i < Tour.Steps.Length; i++)
            {
                var t = At(i);
                Assert.AreEqual(i, t.Index);
                EverythingBut(t, Tour.Steps[i].Advance);
                Assert.AreEqual(i, t.Index, Tour.Steps[i].Id + " is still on show");
                Assert.AreEqual(TourPhase.Instruction, t.Phase, Tour.Steps[i].Id + " is not done by another step's event");
            }
        }

        [Test]
        public void EveryStepEndsOnItsOwnEvent()
        {
            for (var i = 0; i < Tour.Steps.Length; i++)
            {
                var t = At(i);
                _lines.Clear();
                var done = 0;
                t.StepDone += _ => done++;
                switch (Tour.Steps[i].Advance)
                {
                    case TourAdvance.TimeOrPinch: t.Step(Tour.LinesMs, Quiet); break;
                    case TourAdvance.RowOpen: t.Step(100, new TourFacts { RequestRowOpen = true, WindowOpen = true }); break;
                    case TourAdvance.OneHand: t.Decided(true, false); break;
                    case TourAdvance.TwoHand: t.Decided(true, true); break;
                    case TourAdvance.Deny: t.Decided(false, false); break;
                    case TourAdvance.Halted: t.Step(100, new TourFacts { Halted = true, WindowOpen = true }); break;
                    case TourAdvance.Released: t.Step(100, Quiet); break;
                    case TourAdvance.Unfolded:
                        t.Step(100, new TourFacts { Folded = true });
                        Assert.AreEqual(TourPhase.Instruction, t.Phase, "folded is half of it");
                        t.Step(100, new TourFacts { Folded = false, WindowOpen = false });
                        Assert.AreEqual(TourPhase.Instruction, t.Phase, "still unfolding");
                        t.Step(100, Quiet);
                        break;
                }
                var id = Tour.Steps[i].Id;
                Assert.AreEqual(1, done, id + ": the tick once");
                Assert.AreEqual(1, Lines("tour step=" + (i + 1) + " name=" + id + " phase=done"), id);
                if (i == 0)
                {
                    Assert.AreEqual(1, t.Index, "the first step has no word: the next one at once");
                    continue;
                }
                Assert.AreEqual(TourPhase.Confirm, t.Phase, id);
                Assert.AreEqual(Tour.Steps[i].Done, t.Text, "the word shows");
                Assert.AreEqual(TestNeed.Nothing, t.Need, "no request is asked for while the word shows");
                t.Step(Tour.ConfirmMs - 1, Quiet);
                Assert.AreEqual(TourPhase.Confirm, t.Phase, "for about a second");
                t.Step(1, Quiet);
                if (i + 1 < Tour.Steps.Length)
                {
                    Assert.AreEqual(i + 1, t.Index);
                    Assert.AreEqual(TourPhase.Instruction, t.Phase);
                    Assert.AreEqual(1, Lines("tour step=" + (i + 2) + " name=" + Tour.Steps[i + 1].Id + " phase=begin"));
                }
                else
                {
                    Assert.AreEqual(TourPhase.End, t.Phase);
                    Assert.AreEqual(Tour.EndText, t.Text);
                    Assert.AreEqual("", t.Progress);
                    Assert.AreEqual(1, Lines("tour phase=end"));
                }
            }
        }

        [Test]
        public void TheFirstStepEndsAfterFiveSecondsOrOnAPinchThatBeganInIt()
        {
            var t = new Tour();
            t.Begin();
            Assert.AreEqual("1 of 8", t.Progress);
            Assert.AreEqual(1, Lines("tour step=1 name=lines phase=begin"));
            // the pinch that pressed "Take the tour" is still held: it is not a pinch of this step
            var held = new TourFacts { Pinching = true, WindowOpen = true };
            t.Step(100, held);
            t.Step(100, held);
            Assert.AreEqual(0, t.Index);
            t.Step(100, Quiet);
            t.Step(100, held);
            Assert.AreEqual(1, t.Index, "a new pinch ends it");

            t = new Tour();
            t.Begin();
            t.Step(Tour.LinesMs - 1, Quiet);
            Assert.AreEqual(0, t.Index);
            t.Step(1, Quiet);
            Assert.AreEqual(1, t.Index, "or five seconds");
            Assert.AreEqual("2 of 8", t.Progress);
        }

        [Test]
        public void TheHintComesAfterTwentySecondsOnceAndGoesWithTheStep()
        {
            var t = At(2);
            t.Step(0, Shown); // the request's row is open
            _lines.Clear();
            var version = t.Version;
            t.Step(Tour.HintMs - 1, Shown);
            Assert.IsFalse(t.HintShown);
            Assert.AreEqual("", t.Note);
            Assert.AreEqual(version, t.Version, "nothing on the band changed");
            t.Step(1, Shown);
            Assert.IsTrue(t.HintShown);
            Assert.AreEqual("Touch thumb and index finger, keep them together.", t.Note);
            Assert.Greater(t.Version, version);
            t.Step(60000, Shown);
            Assert.AreEqual(1, Lines("tour step=3 name=one-hand phase=hint"), "logged once");
            Assert.AreEqual(Tour.Steps[2].Instruction, t.Text, "the instruction stays");
            t.Decided(true, false);
            Assert.AreEqual("", t.Note, "no hint under the word");
            t.Step(Tour.ConfirmMs, Shown);
            Assert.IsFalse(t.HintShown, "the next step starts without one");
        }

        [Test]
        public void SkipLeavesAStepUndoneAndContinueIsSkippedWhenNothingIsStopped()
        {
            var t = new Tour();
            t.Begin();
            var done = 0;
            t.StepDone += _ => done++;
            t.SkipStep();
            Assert.AreEqual(1, t.Index);
            Assert.AreEqual(1, Lines("tour step=1 name=lines phase=skip"));
            Assert.AreEqual(0, Lines("tour step=1 name=lines phase=done"));
            for (var i = 0; i < 4; i++) t.SkipStep();
            Assert.AreEqual("stop", t.Current.Id);
            t.SkipStep();
            Assert.AreEqual("continue", t.Current.Id);
            t.Step(100, Quiet);
            Assert.AreEqual("fold", t.Current.Id, "nothing is stopped: there is nothing to continue");
            Assert.AreEqual(1, Lines("tour step=7 name=continue phase=skip"));
            t.SkipStep();
            Assert.AreEqual(TourPhase.End, t.Phase);
            Assert.AreEqual(0, done, "no tick for a skipped step");
            t.SkipStep();
            t.Decided(true, true);
            t.Step(100000, Quiet);
            Assert.AreEqual(TourPhase.End, t.Phase, "the end stays until Again or Done");
            Assert.AreEqual(1, Lines("tour phase=end"));
            t.End();
            Assert.IsFalse(t.Active);
        }

        [Test]
        public void SkipDoesNothingWhileTheWordShows()
        {
            var t = At(4);
            t.Decided(false, false);
            Assert.AreEqual(TourPhase.Confirm, t.Phase);
            t.SkipStep();
            Assert.AreEqual(4, t.Index);
            Assert.AreEqual(TourPhase.Confirm, t.Phase);
        }

        [Test]
        public void StoppedBeforeTheStopStepTheBandSaysHowToGoOn()
        {
            var t = At(2);
            Assert.AreEqual(TestNeed.HarmlessRequest, t.Need);
            var halted = new TourFacts { Halted = true, WindowOpen = true };
            t.Step(100, halted);
            Assert.IsTrue(t.Blocked);
            Assert.AreEqual(Tour.HaltedText, t.Text);
            Assert.AreEqual(TestNeed.Nothing, t.Need, "no request is offered while everything is stopped");
            t.Step(Tour.HintMs, halted);
            Assert.IsFalse(t.HintShown, "the step's clock waits");
            Assert.AreEqual(2, t.Index);
            t.Step(100, Shown);
            Assert.IsFalse(t.Blocked);
            Assert.AreEqual(Tour.Steps[2].Instruction, t.Text);
            Assert.AreEqual(2, t.Index, "continuing here is not the step's event");
            // at the stop step itself the halt is the event, not a blockage
            t = At(5);
            t.Step(100, halted);
            Assert.IsFalse(t.Blocked);
            Assert.AreEqual("Stopped.", t.Text);
        }

        [Test]
        public void EveryLineIsKeyValuePairs()
        {
            var t = new Tour();
            t.Begin();
            t.Step(Tour.HintMs, Quiet);
            for (var i = 0; i < 9; i++) t.SkipStep();
            Assert.IsNotEmpty(_lines);
            foreach (var l in _lines)
            {
                StringAssert.StartsWith(Ev.Prefix + "tour ", l);
                foreach (var pair in l.Substring(Ev.Prefix.Length).Split(' ').Skip(1))
                    StringAssert.IsMatch("^[A-Za-z0-9_.-]+=[A-Za-z0-9_.-]+$", pair, l);
            }
        }

        // ---- the tour's request source ----

        static Lotse.Core.DemoPack Pack() => Lotse.Core.DemoPack.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets", "demo", "jury.pack.json")));

        [Test]
        public void TheToursSourceStartsWithEveryAgentAndItsPlan()
        {
            var source = new GuidedSource(Pack(), fromPlans: true);
            var agents = new Dictionary<string, Lotse.Core.AgentState>();
            foreach (var m in source.Poll(16))
                if (m is Lotse.Core.AgentMessage a) agents[a.Agent.Sid] = a.Agent;
            Assert.AreEqual(4, agents.Count, "the four agents are there in the first frame");
            foreach (var a in agents.Values) Assert.IsNotNull(a.Plan, a.Sid + " has its plan: its line is complete");
            Assert.IsFalse(source.Poll(16).Any(m => m is Lotse.Core.PermOpenMessage), "no request by itself");
            // the developer's guided test still starts with the pack's own pace
            var slow = new GuidedSource(Pack());
            Assert.Less(slow.Poll(16).Count(m => m is Lotse.Core.AgentMessage), 4);
        }

        [Test]
        public void FromTheFirstRequestOnThePackNoLongerMovesTheAgents()
        {
            var source = new GuidedSource(Pack(), fromPlans: true);
            source.Poll(16);
            Assert.IsTrue(source.Offer(false));
            var sid = source.Open!.Sid;
            var first = source.Poll(16);
            Assert.AreEqual("blocked", first.OfType<Lotse.Core.AgentMessage>().Last(a => a.Agent.Sid == sid).Agent.Status);
            Assert.AreEqual(1, first.Count(m => m is Lotse.Core.PermOpenMessage));
            // thirty seconds on: nothing from the recording takes the "waiting" from the agent that asks
            var later = new List<Lotse.Core.ServerMessage>();
            for (var i = 0; i < 300; i++) later.AddRange(source.Poll(100));
            Assert.IsEmpty(later, "the pack's start is over");
            Assert.IsNotNull(source.Open);
        }
    }
}
