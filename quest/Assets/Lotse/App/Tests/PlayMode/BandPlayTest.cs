using System.Collections;
using System.Linq;
using System.Reflection;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U9: the bands inside the window.
    /// - The questions the app asks (the display offer, "Halt all agents?") stand in a band under the header and count
    ///   like the open menu: while one shows, a completed gesture approves nothing.
    /// - The guided test's band along the bottom edge is not a question: the app behaves as always while it shows,
    ///   approvals included; its three buttons log once each and can never approve.
    /// </summary>
    public class BandPlayTest
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

        WindowBand Question => _h.Root.Workspace.Question;
        WindowBand TestBand => _h.Root.Workspace.TestBand;
        WindowMenu Menu => _h.Root.Workspace.Menu;
        int Allows() => _h.DecisionFrames().Count(f => f.Contains("\"allow\""));

        IEnumerator Request(string risk)
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", risk));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown);
        }

        void ShowOffer() => typeof(AppRoot).GetMethod("ShowScreenOffer", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_h.Root, null);

        // ---- question bands: nothing can be approved while one shows ----

        [UnityTest]
        public IEnumerator AFullHoldWhileTheDisplayOfferBandShowsApprovesNothing()
        {
            yield return Request("safe");
            ShowOffer();
            yield return Frames(2);
            Assert.IsNotNull(_h.Root.ScreenOffer, "the offer is the window's band");
            Assert.IsTrue(Question.transform.IsChildOf(_h.Root.Workspace.transform), "inside the main window");
            Assert.AreEqual(2, Question.Buttons.Count);
            foreach (var b in Question.Buttons) Assert.AreEqual(Theme.H, b.Size.y, 1e-6f, "its buttons are H high");
            Assert.IsTrue(_h.Block.Covered, "the block is dimmed");
            Assert.AreEqual(Texts.AnswerQuestionFirst, _h.Block.CoverLine);
            Assert.IsFalse(_h.Block.DenyButton.Interactable);
            yield return _h.PinchHold(false, 8);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "no hold starts while the band shows");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "a completed gesture approves nothing");
            _h.Block.DenyButton.Click();
            _h.Block.LaterButton.Click();
            yield return Frames(2);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "Deny is not pressable either");
            Assert.IsTrue(_h.Desk.BlockShown, "nor is Later");

            // answered while the pinch is still held: that pinch never counts; a fresh hold approves
            Question.Buttons[1].Click(); // "Later"
            yield return Frames(1);
            Assert.IsNull(_h.Root.ScreenOffer);
            Assert.IsFalse(_h.Block.Covered, "the block is back");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "a pinch held across the answer never approves");
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, Allows(), "a fresh hold approves as usual");
        }

        [UnityTest]
        public IEnumerator TheOfferBandCancelsAHoldInProgress()
        {
            yield return Request("safe");
            yield return _h.PinchHold(false, 3);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold runs");
            ShowOffer();
            yield return Frames(1);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the band cancels the hold");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing sent");
            StringAssert.Contains("reason=Question", _h.Lotse.Last(l => l.StartsWith(Ev.Prefix + "hold") && l.Contains("phase=cancel")), "the log says why");
        }

        [UnityTest]
        public IEnumerator AFullTwoHandHoldWhileTheHaltQuestionShowsApprovesNothing()
        {
            yield return Request("destructive");
            yield return _h.OpenMenu();
            Menu.HaltButton.Click();
            yield return Frames(2);
            Assert.IsTrue(Menu.AskingHalt);
            Assert.IsFalse(Menu.IsOpen, "the menu gave way to the band");
            Assert.AreEqual(Texts.HaltAsk, Question.TextShown);
            Assert.IsTrue(_h.Block.Covered);
            yield return _h.PinchHold(true, 8);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "no hold starts while the question shows");
            yield return Frames(32);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "a completed two-hand gesture approves nothing");
            Assert.AreEqual(0, _h.Socket.Sent.Count(f => f.Contains("\"halt\"")), "and the question alone halts nothing");
            Menu.HaltCancelButton.Click();
            _h.ReleaseHands();
            yield return Frames(3);
            Assert.IsFalse(Question.IsShown);
            yield return _h.PinchHold(true, 20);
            Assert.AreEqual(1, Allows(), "after the answer, a fresh two-hand hold approves");
        }

        // ---- the guided test's band ----

        int Lines(string exact) => _h.Lotse.Count(l => l == Ev.Prefix + exact);

        IEnumerator StartTest()
        {
            _h.OpenConversation = false;
            yield return _h.StartDemo();
            yield return _h.OpenMenu();
            Assert.IsTrue(PokeButton.All.Any(b => b.Label == GuidedTest.MenuItem && b.transform.IsChildOf(Menu.transform)), "the menu has \"Guided test\"");
            Menu.TestButton.Click();
            yield return Frames(3);
            Assert.IsTrue(_h.Root.Test.Active);
            Assert.IsTrue(TestBand.IsShown);
        }

        IEnumerator Answer(int button)
        {
            TestBand.Buttons[button].Click();
            yield return Frames(2);
        }

        [UnityTest]
        public IEnumerator TheTestBandStepsThroughAndEachButtonLogsOnce()
        {
            yield return StartTest();
            Assert.AreEqual("Test 1 of 11", TestBand.CaptionShown);
            Assert.AreEqual(GuidedTest.Steps[0].Instruction, TestBand.TextShown);
            CollectionAssert.AreEqual(new[] { GuidedTest.Worked, GuidedTest.DidNotWork, GuidedTest.Skip }, TestBand.Buttons.Select(b => b.Label).ToArray());
            foreach (var b in TestBand.Buttons) Assert.AreEqual(Theme.H, b.Size.y, 1e-6f);
            Assert.AreEqual(1, Lines("test step=1 name=read phase=begin"));
            Assert.IsFalse(_h.Root.Workspace.Asking, "it is not a question: the app behaves as always");
            // the window made room for it under its body
            var bg = _h.Root.Workspace.Background.transform;
            Assert.AreEqual(WorkspaceView.BaseHeight + WindowBand.CaptionedHeight, bg.localScale.y, 1e-4f);

            yield return Answer(0);
            Assert.AreEqual(1, Lines("test step=1 name=read result=ok"));
            Assert.AreEqual(1, Lines("test step=2 name=distance phase=begin"));
            Assert.AreEqual("Test 2 of 11", TestBand.CaptionShown);
            yield return Answer(1);
            Assert.AreEqual(1, Lines("test step=2 name=distance result=fail"));
            yield return Answer(2);
            Assert.AreEqual(1, Lines("test step=3 name=open result=skip"));
            Assert.AreEqual("Test 4 of 11", TestBand.CaptionShown);
            for (var i = 4; i <= 11; i++) yield return Answer(0);
            Assert.IsTrue(_h.Root.Test.Finished);
            Assert.AreEqual("11 tests: 9 worked, 1 failed, 1 skipped", TestBand.TextShown, "the summary band");
            Assert.AreEqual(1, Lines("test phase=summary ok=9 fail=1 skip=1"));
            Assert.AreEqual(11, _h.Lotse.Count(l => l.StartsWith(Ev.Prefix + "test ") && l.Contains("result=")), "one line per answer");
            Assert.AreEqual(11, _h.Lotse.Count(l => l.StartsWith(Ev.Prefix + "test ") && l.EndsWith("phase=begin")));
            Assert.AreEqual(1, TestBand.Buttons.Count);
            yield return Answer(0); // "Done"
            Assert.IsFalse(TestBand.IsShown);
            Assert.AreEqual(AppRoot.Phase.Start, _h.Root.Current, "back at the start panel");
            // every ev line is machine-readable: key=value pairs only
            foreach (var l in _h.Lotse.Where(x => x.StartsWith(Ev.Prefix)))
                foreach (var pair in l.Substring(Ev.Prefix.Length).Split(' ').Skip(1))
                    StringAssert.IsMatch("^[A-Za-z0-9_.-]+=[A-Za-z0-9_.-]+$", pair, l);
            Assert.IsTrue(_h.Lotse.Any(l => l.StartsWith(Ev.Prefix + "click via=ray target=Worked")), "a click names its control");
            Assert.IsTrue(_h.Lotse.Any(l => l.StartsWith(Ev.Prefix + "screen name=overview")));
        }

        [UnityTest]
        public IEnumerator AnApprovalStillWorksWhileTheTestBandShowsAndItsButtonsCannotApprove()
        {
            yield return StartTest();
            yield return Answer(0);
            yield return Answer(0);
            // step 3, "open": the test's own source offers a harmless request
            var s = _h.Root.Session;
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100) yield return null;
            var req = s.Store.Pending.Single();
            StringAssert.StartsWith(GuidedSource.IdPrefix, req.Id);
            Assert.IsFalse(req.NeedsTwoHand);
            Assert.IsNull(s.Card, "no block in the overview");
            Assert.IsTrue(_h.Rows.RaisedOf(req.Sid), "its row is the raised one");
            _h.Rows.RowOf(req.Sid).Click();
            yield return Frames(10); // the row opens (0.6 s)
            Assert.IsNotNull(s.Card, "its block");
            Assert.IsFalse(_h.Block.Covered, "the test band covers nothing");
            Assert.IsNotNull(_h.Root.Banner.Line, "a banner is up (placed without a desk): it hangs below the band, never over it");

            // the band's buttons cannot approve: a pinch held on one for two seconds is neither a click nor a hold
            var at = TestBand.Buttons[0].transform.position;
            _h.Hands.Right = Hand(rayAt: at);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: at);
            yield return Frames(20);
            Assert.AreEqual(0f, s.HoldProgress, "a pinch that began on a button never feeds the hold");
            _h.Hands.Right = Hand(rayAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand();
            yield return Frames(1);
            Assert.AreEqual(0, s.Decisions.Count, "nothing decided");
            Assert.AreEqual("Test 3 of 11", TestBand.CaptionShown, "and a held pinch is no click");
            // a click on it answers the test and decides nothing
            yield return _h.RayPinch(TestBand.Buttons[0].transform.position);
            yield return Frames(2);
            Assert.AreEqual("Test 4 of 11", TestBand.CaptionShown);
            Assert.AreEqual(0, s.Decisions.Count, "a band button never approves or denies");
            Assert.IsNotNull(s.Card, "the request stays for the next step");

            // step 4, "one-hand": the approval works as always while the band shows
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, s.Decisions.Count);
            Assert.AreEqual("allow", s.Decisions[0].Behavior);
            Assert.AreEqual("pinch-hold", s.Decisions[0].Confirm);
            Assert.IsTrue(_h.Lotse.Any(l => l == Ev.Prefix + "hold kind=one phase=complete p=1.00"), "the hold is in the log");
            _h.ReleaseHands();
            yield return Frames(3);

            // step 5, "two-hand": the dangerous request; one hand never approves it, both do
            yield return Answer(0);
            guard = 0;
            while ((s.Card == null || !s.Card.NeedsTwoHand) && guard++ < 100)
            {
                if (s.Card == null && s.Store.Pending.Count > 0) _h.Root.Desktop.OpenAgent(s.Store.Pending[0].Sid);
                yield return null;
            }
            Assert.IsTrue(s.Card.NeedsTwoHand, "the dangerous request is offered");
            yield return Frames(3);
            Assert.IsFalse(string.IsNullOrEmpty(_h.Block.VerdictShown), "with its facts row");
            yield return _h.PinchHold(false, 25);
            Assert.AreEqual(1, s.Decisions.Count, "one hand never approves the dangerous request");
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(true, 20);
            Assert.AreEqual(2, s.Decisions.Count);
            Assert.AreEqual("two-hand", s.Decisions[1].Confirm);
        }
    }
}
