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
    /// U14: the tour through the real app with scripted hands. Each step ends only on the real thing (a row opened by a
    /// poke, a completed hold, a pressed "Deny", the stop gesture, "Continue" and its "Yes", the fold and the rail); the
    /// tour itself never performs a gesture and never decides; an approval needs its full gesture while the band
    /// shows; the band's link can never approve.
    /// </summary>
    public class TourPlayTest
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

        WindowBand Band => _h.Root.Workspace.TestBand;
        Tour Tour => _h.Root.Tour;
        DemoSession Session => _h.Root.Session;
        int Lines(string exact) => _h.Lotse.Count(l => l == Ev.Prefix + exact);
        PokeButton Skip => Band.Buttons.Single(b => b.Label == Tour.Skip);

        IEnumerator StartTour()
        {
            yield return _h.Start();
            var first = PokeButton.All.First(b => b.transform.IsChildOf(GameObject.Find("StartPanel").transform));
            Assert.AreEqual(Tour.StartButton, first.Label, "\"Take the tour\" is the first button of the start panel");
            first.Click();
            yield return Frames(2);
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current);
            Assert.IsTrue(Tour.Active);
            Assert.IsNotNull(_h.Root.Guided, "the guided test's local source");
            Assert.IsNull(_h.Root.Live, "never a bridge");
            Assert.IsTrue(Band.IsShown);
            Assert.AreEqual(AppRoot.TourTag, Band.Tag);
        }

        /// <summary>The tour stands at `id` (earlier steps skipped), its instruction on the band.</summary>
        IEnumerator At(string id)
        {
            yield return StartTour();
            var guard = 0;
            while (Tour.Current.Id != id && guard++ < 20)
            {
                Tour.SkipStep();
                yield return Frames(2);
            }
            Assert.AreEqual(id, Tour.Current.Id);
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase);
            // an approval step reached by "Skip" has no open row: it says how to open one (U14b)
            Assert.AreEqual(Tour.Current.Waiting.Length > 0 ? Tour.Current.Waiting : Tour.Current.Instruction, Band.TextShown);
        }

        /// <summary>The tour's request of this kind is pending; returns it.</summary>
        IEnumerator Offered(bool dangerous)
        {
            var guard = 0;
            while ((_h.Root.Guided.Open == null || _h.Root.Guided.OpenIsDangerous != dangerous || Session.Store.Pending.Count == 0) && guard++ < 200)
                yield return null;
            Assert.IsNotNull(_h.Root.Guided.Open, "the step's request is on offer");
            Assert.AreEqual(dangerous, _h.Root.Guided.OpenIsDangerous);
            Assert.AreEqual(_h.Root.Guided.Open.Id, Session.Store.Pending.Single().Id);
        }

        string Sid => _h.Root.Guided.Open.Sid;

        /// <summary>The word of the step shows, then the next instruction.</summary>
        IEnumerator WordThenNext(string word, string next)
        {
            Assert.AreEqual(TourPhase.Confirm, Tour.Phase);
            yield return Frames(1);
            Assert.AreEqual(word, Band.TextShown, "the one word");
            Assert.AreEqual(0, Band.Buttons.Count, "nothing to press while it shows");
            yield return Frames(12); // about a second (frames of 0.1 s)
            Assert.AreEqual(next, Tour.Current.Id);
            Assert.AreEqual(Tour.Text, Band.TextShown);
        }

        // ---- U14b: the approval steps ask for the gesture only while their request's row is open ----

        [UnityTest]
        public IEnumerator TheOneHandStepAsksForTheGestureOnlyWhileItsRequestIsOpen()
        {
            const string tap = "One agent is waiting for you. Tap the raised line.";
            const string gesture = "It wants to change one file. Pinch and hold until the bar is dark.";
            yield return At("one-hand");
            yield return Offered(false);
            yield return Frames(3);
            Assert.AreEqual(tap, Band.TextShown, "after Skip from the open step no row is open");
            // another agent's row is not the request
            var other = _h.Rows.Shown.First(r => r.Sid != Sid).Sid;
            yield return _h.OpenRow(other);
            yield return Frames(3);
            Assert.AreEqual(tap, Band.TextShown, "another agent's row is open: still nothing to pinch at");
            yield return _h.OpenRow(Sid);
            yield return Frames(3);
            Assert.AreEqual(gesture, Band.TextShown);
            // "Later" closes the row
            _h.Block.LaterButton.Click();
            yield return Frames(12);
            Assert.AreEqual(0, Session.Decisions.Count);
            Assert.AreEqual(tap, Band.TextShown, "after Later");
            // a deny: the next request is another agent's
            yield return _h.OpenRow(Sid);
            yield return Frames(3);
            Assert.AreEqual(gesture, Band.TextShown);
            var first = _h.Root.Guided.Open.Id;
            var denied = Sid;
            _h.Block.DenyButton.Click();
            yield return Frames(15);
            yield return Offered(false);
            Assert.AreNotEqual(first, _h.Root.Guided.Open.Id);
            Assert.AreNotEqual(denied, Sid, "the next request is another agent's");
            yield return Frames(3);
            Assert.AreEqual("one-hand", Tour.Current.Id);
            Assert.AreEqual(tap, Band.TextShown, "after a deny the request is not in the open row");
            yield return _h.OpenRow(Sid);
            yield return Frames(3);
            Assert.AreEqual(gesture, Band.TextShown);
            // the approval rule is the same as ever
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual("allow", Session.Decisions.Last().Behavior);
            Assert.AreEqual(1, Lines("tour step=3 name=one-hand phase=done"));
            _h.ReleaseHands();
        }

        [UnityTest]
        public IEnumerator TheTwoHandStepAsksForTheGestureOnlyWhileItsRequestIsOpen()
        {
            const string tap = "This one deletes a folder for good. Tap the raised line.";
            const string gesture = "This one deletes a folder for good. Pinch with both hands and hold.";
            yield return At("two-hand");
            yield return Offered(true);
            yield return Frames(3);
            Assert.AreEqual(tap, Band.TextShown);
            yield return _h.OpenRow(Sid);
            yield return Frames(3);
            Assert.AreEqual(gesture, Band.TextShown);
            // the text never changes while a hold runs
            yield return _h.PinchHold(true, 6);
            Assert.Greater(Session.HoldProgress, 0f);
            Assert.AreEqual(gesture, Band.TextShown);
            _h.ReleaseHands();
            yield return Frames(3);
            _h.Block.LaterButton.Click();
            yield return Frames(12);
            Assert.AreEqual(tap, Band.TextShown, "after Later");
            Assert.AreEqual(0, Session.Decisions.Count);
            // the hint fits the state
            yield return Frames(200);
            Assert.AreEqual("Point at the raised line and pinch briefly.", Band.NoteShown);
            yield return _h.OpenRow(Sid);
            yield return Frames(3);
            Assert.AreEqual("Both hands up, thumb and index finger together.", Band.NoteShown);
            Assert.AreEqual(gesture, Band.TextShown);
        }

        // ---- the start panel and the band ----

        [UnityTest]
        public IEnumerator TheStartPanelOffersTheTourFirstAndTheDemoStillPlaysWithoutIt()
        {
            yield return _h.Start();
            var panel = GameObject.Find("StartPanel").transform;
            var labels = PokeButton.All.Where(b => b.transform.IsChildOf(panel)).Select(b => b.Label).ToArray();
            CollectionAssert.AreEqual(new[] { Tour.StartButton, Texts.StartButton, Texts.BridgeButton, Texts.Replace }, labels);
            var tour = PokeButton.All.First(b => b.Label == Tour.StartButton);
            var demo = PokeButton.All.First(b => b.Label == Texts.StartButton);
            Assert.Greater(tour.transform.position.y, demo.transform.position.y + Theme.H, "the demo's button stands under it");
            PokeButton.All.First(b => b.Label == Texts.StartButton).Click();
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current);
            Assert.IsFalse(Tour.Active, "\"Watch the demo\" plays the recording without the tour");
            Assert.IsFalse(Band.IsShown);
            Assert.IsNull(_h.Root.Guided);
        }

        [UnityTest]
        public IEnumerator TheBandShowsOneInstructionTheProgressAndASkipLink()
        {
            yield return StartTour();
            Assert.AreEqual("1 of 8", Band.CaptionShown);
            Assert.AreEqual("Each line is one agent. The dot shows where it is.", Band.TextShown);
            Assert.AreEqual("", Band.NoteShown);
            Assert.AreEqual(1, Band.Buttons.Count);
            Assert.AreEqual(ButtonKind.Text, Skip.Kind, "\"Skip\" is a text link");
            Assert.AreEqual(Theme.H, Skip.Size.y, 1e-6f, "with the one control height as its hit area");
            Assert.IsFalse(Band.transform.Find("Background").GetComponent<MeshRenderer>().enabled, "the band is the window's asphalt");
            Assert.IsFalse(_h.Root.Workspace.Asking, "it is not a question band");
            Assert.AreEqual(1, Lines("tour step=1 name=lines phase=begin"));
            Assert.IsFalse(PokeButton.All.Any(b => b.Label == GuidedTest.Worked || b.Label == GuidedTest.DidNotWork), "no \"Worked / Did not work\"");
            // the developer's guided test is still in the menu
            yield return _h.OpenMenu();
            Assert.IsNotNull(_h.Root.Workspace.Menu.TestButton);
        }

        // ---- one test per step: only the real event ends it ----

        [UnityTest]
        public IEnumerator Step1EndsAfterFiveSecondsOrOnAPinch()
        {
            yield return StartTour();
            yield return Frames(30);
            Assert.AreEqual("lines", Tour.Current.Id, "three seconds: still the first step");
            _h.Hands.Right = Hand(1f);
            yield return Frames(2);
            Assert.AreEqual("open", Tour.Current.Id, "a pinch ends it");
            Assert.AreEqual(1, Lines("tour step=1 name=lines phase=done"));
            _h.ReleaseHands();

            // again, by time alone
            _h.TearDown();
            _h = new WorkspaceHarness();
            _h.SetUp();
            yield return StartTour();
            yield return Frames(45);
            Assert.AreEqual("lines", Tour.Current.Id);
            yield return Frames(10);
            Assert.AreEqual("open", Tour.Current.Id, "five seconds end it");
            Assert.AreEqual("2 of 8", Band.CaptionShown);
        }

        [UnityTest]
        public IEnumerator Step2EndsWhenTheWaitingAgentsRowIsOpen()
        {
            yield return At("open");
            yield return Offered(false);
            Assert.IsTrue(_h.Rows.RaisedOf(Sid), "its row is the raised one");
            Assert.IsNull(_h.Root.Desktop.OpenSid, "the tour opens nothing");
            yield return Frames(20);
            Assert.AreEqual("open", Tour.Current.Id, "waiting does not end it");
            // another agent's row is not the step
            var other = _h.Rows.Shown.First(r => r.Sid != Sid).Sid;
            yield return _h.OpenRow(other);
            yield return Frames(5);
            Assert.AreEqual("open", Tour.Current.Id, "another agent's row does not end it");
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase);
            yield return _h.OpenRow(Sid);
            Assert.AreEqual(1, Lines("tour step=2 name=open phase=done"));
            Assert.AreEqual(0, Session.Decisions.Count);
            yield return WordThenNext("Opened.", "one-hand");
        }

        [UnityTest]
        public IEnumerator Step3NeedsTheFullOneHandHold()
        {
            yield return At("one-hand");
            yield return Offered(false);
            yield return Frames(30);
            Assert.AreEqual(0, Session.Decisions.Count, "the tour approves nothing by itself");
            Assert.IsNull(Session.Card, "and opens nothing by itself");
            yield return _h.OpenRow(Sid);
            Assert.IsNotNull(Session.Card);
            Assert.IsFalse(_h.Block.Covered, "the band covers nothing");
            // "Later" and "Deny" stand under the plan at the right, never on its lines
            var plan = _h.Conversation.transform.Find("Plan").GetComponent<Renderer>().bounds;
            foreach (var b in new[] { _h.Block.LaterButton, _h.Block.DenyButton })
                Assert.Less(b.transform.position.y + b.Size.y * 0.5f * b.transform.lossyScale.y, plan.min.y, b.Label + " is below the plan's last line");
            // a short pinch is not an approval
            yield return _h.PinchHold(false, 3);
            Assert.Greater(Session.HoldProgress, 0f, "a hold runs while the band shows");
            _h.ReleaseHands();
            yield return Frames(3);
            Assert.AreEqual(0, Session.Decisions.Count, "let go early: nothing approved");
            Assert.AreEqual("one-hand", Tour.Current.Id);
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase, "and the step is not done");
            // a pinch in the lap (outside the hold gate) is none either
            _h.Hands.Right = Hand(1f);
            yield return Frames(20);
            Assert.AreEqual(0, Session.Decisions.Count, "a pinch outside the gate approves nothing");
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, Session.Decisions.Count);
            Assert.AreEqual("allow", Session.Decisions[0].Behavior);
            Assert.AreEqual("pinch-hold", Session.Decisions[0].Confirm);
            Assert.AreEqual(1, Lines("tour step=3 name=one-hand phase=done"));
            _h.ReleaseHands();
            yield return WordThenNext("Cleared.", "two-hand");
        }

        [UnityTest]
        public IEnumerator Step4NeedsBothHands()
        {
            yield return At("two-hand");
            yield return Offered(true);
            Assert.IsNull(Session.Card, "the request is queued; its row is not opened for the user");
            yield return _h.OpenRow(Sid);
            Assert.IsTrue(Session.Card.NeedsTwoHand);
            yield return _h.PinchHold(false, 25);
            Assert.AreEqual(0, Session.Decisions.Count, "one hand never approves it");
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase);
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(true, 6);
            _h.ReleaseHands();
            yield return Frames(3);
            Assert.AreEqual(0, Session.Decisions.Count, "both hands let go early: nothing");
            yield return _h.PinchHold(true, 20);
            Assert.AreEqual(1, Session.Decisions.Count);
            Assert.AreEqual("two-hand", Session.Decisions[0].Confirm);
            Assert.AreEqual(1, Lines("tour step=4 name=two-hand phase=done"));
            _h.ReleaseHands();
            yield return WordThenNext("Cleared.", "deny");
        }

        [UnityTest]
        public IEnumerator Step5EndsOnDenyAndNotOnAnApproval()
        {
            yield return At("deny");
            yield return Offered(false);
            var first = _h.Root.Guided.Open.Id;
            yield return _h.OpenRow(Sid);
            yield return _h.PinchHold(false, 12);
            _h.ReleaseHands();
            Assert.AreEqual(1, Session.Decisions.Count);
            Assert.AreEqual("allow", Session.Decisions[0].Behavior);
            yield return Frames(3);
            Assert.AreEqual("deny", Tour.Current.Id);
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase, "an approval is not a no");
            yield return Frames(15); // "Cleared", then the row closes
            yield return Offered(false);
            Assert.AreNotEqual(first, _h.Root.Guided.Open.Id, "the next request is on offer");
            yield return _h.OpenRow(Sid);
            Assert.IsNotNull(Session.Card);
            yield return _h.Poke(_h.Block.DenyButton);
            Assert.AreEqual(2, Session.Decisions.Count);
            Assert.AreEqual("deny", Session.Decisions[1].Behavior);
            Assert.AreEqual(1, Lines("tour step=5 name=deny phase=done"));
            yield return WordThenNext("Denied.", "stop");
        }

        HandFrame StopHand(float x)
        {
            var head = _h.Camera.transform;
            var at = head.position + head.forward * 0.4f + head.right * x - Vector3.up * 0.1f;
            var f = Hand(0f, pinchAt: at);
            f.PalmValid = true;
            f.PalmCenter = at;
            f.PalmNormal = head.forward;
            f.FingersValid = true;
            f.IndexStraight = f.MiddleStraight = f.RingStraight = f.LittleStraight = 0.98f;
            return f;
        }

        IEnumerator HoldStop(int frames)
        {
            _h.Hands.Left = StopHand(-0.2f);
            _h.Hands.Right = StopHand(0.2f);
            yield return Frames(frames);
            _h.ReleaseHands();
            yield return Frames(1);
        }

        [UnityTest]
        public IEnumerator Step6EndsWhenEverythingIsStoppedAndStep7OnContinueWithItsConfirmation()
        {
            yield return At("stop");
            yield return HoldStop(10); // let go early
            Assert.IsFalse(Session.Halted);
            Assert.AreEqual("stop", Tour.Current.Id, "half a gesture stops nothing and ends nothing");
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase);
            yield return HoldStop(22);
            Assert.IsTrue(Session.Halted, "the stop gesture halts the demo");
            Assert.AreEqual(1, Lines("tour step=6 name=stop phase=done"));
            yield return WordThenNext("Stopped.", "continue");

            var halt = _h.Root.Workspace.Halt;
            yield return Frames(20);
            Assert.AreEqual("continue", Tour.Current.Id, "waiting continues nothing");
            Assert.IsTrue(Session.Halted);
            yield return _h.Poke(halt.ContinueButton);
            yield return Frames(3);
            Assert.IsTrue(halt.Asking, "the existing confirmation");
            Assert.IsTrue(Session.Halted, "\"Continue\" alone releases nothing");
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase, "and does not end the step");
            yield return _h.Poke(PokeButton.All.First(b => b.Label == Texts.Yes));
            yield return Frames(3);
            Assert.IsFalse(Session.Halted);
            Assert.AreEqual(1, Lines("tour step=7 name=continue phase=done"));
            yield return WordThenNext("Moving.", "fold");
            Assert.AreEqual(0, Session.Decisions.Count, "nothing was ever decided");
        }

        [UnityTest]
        public IEnumerator Step8EndsWhenTheWindowIsOpenAgainAfterAFoldAndDoneReturnsToTheStartPanel()
        {
            yield return At("fold");
            var view = _h.Root.Workspace;
            yield return Frames(20);
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase, "an open window that was never folded is not the step");
            yield return _h.Poke(view.Header.CollapseButton);
            yield return Frames(20);
            Assert.IsTrue(_h.Root.Desktop.Mode.Collapsed);
            Assert.AreEqual("fold", Tour.Current.Id);
            Assert.AreEqual(TourPhase.Instruction, Tour.Phase, "folded is half of it");
            yield return _h.Poke(view.Rail.Area);
            var guard = 0;
            while (!view.Open && guard++ < 40) yield return null;
            yield return Frames(2);
            Assert.IsTrue(view.Open);
            Assert.AreEqual(1, Lines("tour step=8 name=fold phase=done"));
            Assert.AreEqual(TourPhase.Confirm, Tour.Phase);
            yield return Frames(13);
            Assert.AreEqual(TourPhase.End, Tour.Phase);
            Assert.AreEqual(1, Lines("tour phase=end"));
            Assert.AreEqual(Tour.EndText, Band.TextShown, "the closing sentence fits the band");
            // U14b: in full, on at most two lines, clear of the buttons and inside the band
            var end = Band.transform.Find("Content/Text").GetComponent<TMPro.TextMeshPro>();
            end.ForceMeshUpdate();
            Assert.AreEqual(Tour.EndText, end.text);
            StringAssert.DoesNotContain(Ui.Ellipsis, end.text);
            Assert.LessOrEqual(end.textInfo.lineCount, 2);
            Assert.AreEqual(Tour.EndText.Length, end.textInfo.characterCount, "every character is laid out");
            var endBox = end.GetComponent<Renderer>().bounds;
            foreach (var b in Band.Buttons)
                Assert.Less(endBox.max.x, b.transform.position.x - b.Size.x * 0.5f * b.transform.lossyScale.x + 1e-4f, "left of \"" + b.Label + "\"");
            var bandTop = Band.transform.position.y;
            Assert.LessOrEqual(endBox.max.y, bandTop + 1e-4f, "under the band's top edge");
            Assert.GreaterOrEqual(endBox.min.y, bandTop - Band.Height * Band.transform.lossyScale.y - 1e-4f, "above its bottom edge");
            CollectionAssert.AreEquivalent(new[] { Tour.Again, Tour.Done }, Band.Buttons.Select(b => b.Label).ToArray());
            var done = Band.Buttons.Single(b => b.Label == Tour.Done);
            var again = Band.Buttons.Single(b => b.Label == Tour.Again);
            Assert.Greater(done.transform.position.x, again.transform.position.x, "\"Done\" at the right end");

            again.Click();
            yield return Frames(3);
            Assert.IsTrue(Tour.Active);
            Assert.AreEqual("lines", Tour.Current.Id, "\"Again\" starts the tour again");
            Assert.AreEqual(2, Lines("tour step=1 name=lines phase=begin"));
            guard = 0;
            while (Tour.Phase != TourPhase.End && guard++ < 20)
            {
                Tour.SkipStep();
                yield return Frames(2);
            }
            Band.Buttons.Single(b => b.Label == Tour.Done).Click();
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Start, _h.Root.Current, "\"Done\" returns to the start panel");
            Assert.IsFalse(Tour.Active);
            Assert.IsFalse(Band.IsShown);
            Assert.IsTrue(PokeButton.All.Any(b => b.Label == Tour.StartButton));
        }

        // ---- skip, the hint, and what the band can never do ----

        [UnityTest]
        public IEnumerator SkipLeavesTheStepAndTheLinkCanNeverApprove()
        {
            yield return At("one-hand");
            yield return Offered(false);
            yield return _h.OpenRow(Sid);
            Assert.IsNotNull(Session.Card, "a request stands open, ready for its gesture");

            // a pinch that begins on "Skip" and is held for two seconds is neither a click nor a hold
            var at = Skip.transform.position;
            _h.Hands.Right = Hand(rayAt: at);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: at);
            yield return Frames(20);
            Assert.AreEqual(0f, Session.HoldProgress, "a pinch that began on the link never feeds the hold");
            _h.Hands.Right = Hand(rayAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand();
            yield return Frames(1);
            Assert.AreEqual(0, Session.Decisions.Count, "nothing decided");
            Assert.AreEqual("one-hand", Tour.Current.Id, "and a held pinch is no click");

            // the same with the pinch point inside the hold gate: still nothing
            var gate = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: gate, rayAt: Skip.transform.position);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: gate, rayAt: Skip.transform.position);
            yield return Frames(20);
            Assert.AreEqual(0f, Session.HoldProgress);
            Assert.AreEqual(0, Session.Decisions.Count, "a hand in the gate that began on the link approves nothing");
            _h.ReleaseHands();
            yield return Frames(3);

            // a click on it skips the step and decides nothing
            yield return _h.RayPinch(Skip.transform.position);
            yield return Frames(2);
            Assert.AreEqual("two-hand", Tour.Current.Id);
            Assert.AreEqual(1, Lines("tour step=3 name=one-hand phase=skip"));
            Assert.AreEqual(0, Lines("tour step=3 name=one-hand phase=done"));
            Assert.AreEqual(0, Session.Decisions.Count, "the link never approves or denies");
        }

        [UnityTest]
        public IEnumerator TheHintAppearsAfterTwentySecondsWithoutTheStepsEvent()
        {
            yield return At("stop");
            yield return Frames(150);
            Assert.AreEqual("", Band.NoteShown, "fifteen seconds: no hint yet");
            yield return Frames(60);
            Assert.AreEqual("Palms away from you, fingers up, like stopping traffic.", Band.NoteShown);
            Assert.AreEqual(Tour.Current.Instruction, Band.TextShown, "under the instruction, which stays");
            Assert.AreEqual(1, Lines("tour step=6 name=stop phase=hint"));
            Assert.AreEqual(1, Band.Buttons.Count, "\"Skip\" stays");
            Assert.IsFalse(Session.Halted, "the tour performs nothing itself");
        }

        [UnityTest]
        public IEnumerator TheTourNeverDecidesAndNeverStopsByItself()
        {
            yield return StartTour();
            // two minutes with open hands in the lap: the steps wait, nothing is approved, denied, stopped or opened
            yield return Frames(1200);
            Assert.AreEqual("open", Tour.Current.Id, "only the first step ends by time");
            Assert.AreEqual(0, Session.Decisions.Count);
            Assert.IsFalse(Session.Halted);
            Assert.IsNull(_h.Root.Desktop.OpenSid);
            Assert.AreEqual(1, Session.Store.Pending.Count, "its one request waits");
            Assert.AreEqual(0, _h.Sockets.Count, "and no connection was ever opened");
        }
    }
}
