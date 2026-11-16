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
    /// U5a's safety rules through AppRoot with a fake bridge and scripted hands: nothing opens by itself; an allow needs
    /// the block on screen for the selected agent, the gesture its risk asks for and a hold begun on that very request;
    /// selecting another agent, "Später", the request closing, focus loss, tracking loss and a disconnect each cancel a
    /// hold and send nothing; another agent's request can never be approved; only "Ablehnen" denies; the ray is off
    /// while a block is shown; the settings in front hide the block; untrusted text stays plain.
    /// </summary>
    public class WorkspaceSafetyTest
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

        /// <summary>Live; a request for `sid` arrives; when it is the selected agent's, its block shows.</summary>
        IEnumerator Request(string id, string sid, string risk = "safe")
        {
            _h.Socket.Receive(PermOpen(id, sid, risk));
            yield return Frames(2);
        }

        IEnumerator HoldBegunOnTheBlock(string risk = "safe")
        {
            yield return _h.GoLive();
            Assert.AreEqual("s1", _h.Root.SelectedAgent, "the first agent in the sort order is selected");
            yield return Request("r1", "s1", risk);
            Assert.IsTrue(_h.Desk.BlockShown, "the selected agent's block");
            yield return _h.PinchHold(risk != "safe", 4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0.2f, "a hold runs");
        }

        /// <summary>After a cancel: the hold is gone, and the same pinch held on for 3 s sends nothing.</summary>
        IEnumerator AssertCancelled(string why)
        {
            Assert.AreEqual(0f, _h.Root.Session?.HoldProgress ?? 0f, why + ": the hold is cancelled");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), why + ": nothing is sent");
        }

        [UnityTest]
        public IEnumerator NothingOpensByItselfAndThePillSelectsTheAgent()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s2");
            Assert.IsNull(_h.Root.Session.Card, "s2 is not selected: no block");
            Assert.IsFalse(_h.Block.IsShown);
            Assert.AreEqual("s1", _h.Root.SelectedAgent, "the selection does not change by itself");
            Assert.IsTrue(_h.Root.Workspace.Pill.IsShown, "only the notice");
            Assert.AreEqual("s2", _h.Root.Workspace.Pill.Sid);
            StringAssert.Contains("Checkout-Summen needs you: Claude listet die Dateien", _h.Root.Workspace.Pill.Line);
            Assert.AreEqual(OverviewText.Holding, _h.Rows.StatusShown("s2"), "its row says it holds");
            Assert.IsTrue(_h.Rows.RaisedOf("s2") && _h.Rows.LightsOf("s2") > 0, "and it is raised, with a stop bar");
            Assert.AreEqual(1, PokeButton.All.Count(b => b.transform.IsChildOf(_h.Root.Workspace.transform) && b.Label == "Hinweis"));

            yield return _h.Poke(_h.Root.Workspace.Pill.Button);
            Assert.AreEqual("s2", _h.Root.SelectedAgent, "the pill selects its agent");
            yield return Frames(8); // its row opens (0.6 s)
            Assert.IsTrue(_h.Desk.BlockShown);
            Assert.AreEqual("r1", _h.Block.RequestId);
            Assert.IsFalse(_h.Root.Workspace.Pill.IsShown);
            Assert.AreEqual(0, _h.Decisions(), "selecting decides nothing");
        }

        [UnityTest]
        public IEnumerator TheNoticeGoesAfterSixSeconds()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s2");
            Assert.IsTrue(_h.Root.Workspace.Pill.IsShown);
            yield return Frames(62);
            Assert.IsFalse(_h.Root.Workspace.Pill.IsShown, "6 s");
            Assert.IsNull(_h.Root.Session.Card, "and still nothing opened");
        }

        [UnityTest]
        public IEnumerator AnAllowNeedsTheGestureItsRiskAsksFor()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s1", "destructive");
            Assert.AreEqual(Texts.RiskDangerous, _h.Block.RiskShown);
            Assert.AreEqual(Texts.HintTwoHand, _h.Block.HintShown);
            yield return _h.PinchHold(false, 40);
            Assert.AreEqual(0, _h.Decisions(), "one hand never approves a dangerous request");
            _h.ReleaseHands();
            yield return _h.PinchHold(true, 17);
            Assert.AreEqual(1, _h.Decisions());
            StringAssert.Contains("\"behavior\":\"allow\",\"confirm\":\"two-hand\"", _h.DecisionFrames().Single());
            Assert.AreEqual(CardMode.Sent, _h.Root.Session.Mode);
            Assert.AreEqual(Texts.SentWaiting, _h.Conversation.Lines.FirstOrDefault(l => l == Texts.SentWaiting));
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(2);
            Assert.AreEqual(Headline.Cleared("Claude Code"), _h.Block.NoteShown, "cleared, on the bar's line");
            Assert.IsTrue(_h.Block.ClearedShown);
            Assert.AreEqual(_h.Block.LightsShown, _h.Block.LitShown, "every light is on again, in green");
            Assert.IsNull(_h.Block.RequestId, "nothing is asked any more");
            yield return Frames(32);
            Assert.IsFalse(_h.Block.IsShown, "for a moment");
            Assert.IsNull(_h.Desk.OpenSid, "then the row closes");
        }

        [UnityTest]
        public IEnumerator APinchHeldBeforeTheBlockShowedNeverApprovesIt()
        {
            yield return _h.GoLive();
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(2);
            yield return Request("r1", "s1");
            Assert.IsTrue(_h.Desk.BlockShown);
            _h.Hands.Right = Hand(1f, pinchAt: _h.GatePoint());
            yield return Frames(30);
            Assert.AreEqual(0, _h.Decisions(), "the hold has to begin while this request is shown");
            _h.ReleaseHands();
            yield return _h.PinchHold(false, 10);
            Assert.AreEqual(1, _h.Decisions(), "a fresh pinch-hold approves it");
        }

        [UnityTest]
        public IEnumerator SelectingAnotherAgentCancelsTheHold()
        {
            yield return HoldBegunOnTheBlock();
            _h.Rows.RowOf("s2").Click(); // the row's press
            yield return null;
            Assert.AreEqual("s2", _h.Root.SelectedAgent);
            Assert.IsFalse(_h.Block.IsShown, "s2 has no request");
            yield return AssertCancelled("another agent");
            _h.Rows.RowOf("s1").Click();
            yield return Frames(20);
            Assert.AreEqual(0, _h.Decisions(), "back on s1: the pinch still held does not count");
        }

        [UnityTest]
        public IEnumerator SpaeterCancelsTheHoldSendsNothingAndSelectingAgainShowsTheBlock()
        {
            yield return HoldBegunOnTheBlock();
            _h.Block.LaterButton.Click();
            yield return null;
            Assert.IsFalse(_h.Block.IsShown, "Später hides the block");
            yield return AssertCancelled("Später");
            CollectionAssert.IsEmpty(_h.Socket.Sent.Where(m => m.Contains("perm_decision")), "Später sends nothing");
            _h.ReleaseHands();
            yield return _h.PokeRow("s1");
            Assert.IsTrue(_h.Desk.BlockShown, "selecting the agent again shows the block again");
            Assert.AreEqual("r1", _h.Block.RequestId);
        }

        [UnityTest]
        public IEnumerator TheRequestClosingCancelsTheHold()
        {
            yield return HoldBegunOnTheBlock();
            _h.Socket.Receive(PermClosed("r1", "timeout"));
            yield return null;
            yield return AssertCancelled("closed");
            Assert.AreEqual(Texts.TimedOut, _h.Block.NoteShown, "the timeout text in the block's place");
        }

        [UnityTest]
        public IEnumerator LosingTheInputFocusCancelsTheHold()
        {
            yield return HoldBegunOnTheBlock();
            _h.Focus = false;
            yield return Frames(2);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            _h.Focus = true;
            yield return AssertCancelled("focus");
        }

        [UnityTest]
        public IEnumerator LosingTrackingCancelsTheHold()
        {
            yield return HoldBegunOnTheBlock();
            _h.Hands.Right = Hand(tracked: false);
            yield return Frames(2);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            _h.Hands.Right = Hand(1f, pinchAt: _h.GatePoint());
            yield return AssertCancelled("tracking");
        }

        [UnityTest]
        public IEnumerator ADisconnectCancelsTheHold()
        {
            yield return HoldBegunOnTheBlock();
            _h.Socket.Drop(1006);
            yield return null;
            Assert.IsNull(_h.Root.Session.Card);
            yield return AssertCancelled("disconnect");
            Assert.AreEqual(Texts.ConnectionLost, _h.Root.Banner.Line);
        }

        [UnityTest]
        public IEnumerator AnUnselectedAgentsRequestCannotBeApprovedByAnyGesture()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s2", "safe");
            Assert.IsFalse(_h.Block.IsShown);
            yield return _h.PinchHold(true, 30);
            _h.ReleaseHands();
            yield return _h.PinchHold(false, 30);
            Assert.AreEqual(0, _h.Decisions(), "no block, no approval");
            Assert.AreEqual(1, _h.Root.Session.Store.Pending.Count, "still open");
        }

        [UnityTest]
        public IEnumerator AblehnenSendsOneDenyAndThePalmDeniesNothing()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s1", "destructive");
            _h.Hands.Left = Hand(palm: true);
            yield return Frames(10);
            _h.Hands.Left = Hand();
            Assert.AreEqual(0, _h.Decisions(), "the push-away gesture is gone");
            yield return _h.Poke(_h.Block.DenyButton);
            Assert.AreEqual(1, _h.Decisions());
            Assert.AreEqual("{\"type\":\"perm_decision\",\"decision\":{\"id\":\"r1\",\"behavior\":\"deny\"}}", _h.DecisionFrames().Single());
            Assert.IsNull(_h.Block.DenyButton, "sent: no buttons any more");
            _h.Socket.Receive(PermClosed("r1", "deny"));
            yield return Frames(2);
            Assert.IsNull(_h.Desk.OpenSid, "denied from here: the row closes (U13)");
            Assert.IsFalse(_h.Block.IsShown);
        }

        [UnityTest]
        public IEnumerator ARayClickOnARowOpensItsBlockTheRayStaysOnAndAPinchBegunOnTheRowNeverApproves()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s2");
            var row = _h.Rows.RowOf("s2").transform.position;
            yield return _h.RayPinch(row);
            Assert.AreEqual("s2", _h.Root.SelectedAgent, "the ray click selected it");
            yield return Frames(8); // its row opens (0.6 s)
            Assert.IsTrue(_h.Desk.BlockShown);
            // a new pinch begun on a row (another agent's thin line), held on in the hold gate while the ray rests there
            row = _h.Rows.RowOf("s3").transform.position;
            _h.Hands.Right = Hand(rayAt: row, pinchAt: _h.GatePoint());
            yield return null;
            Assert.IsTrue(_h.Root.Pointer.RightShown, "the ray stays on while the block is shown (U5a fix 1)");
            _h.Hands.Right = Hand(1f, rayAt: row, pinchAt: _h.GatePoint());
            yield return Frames(30);
            Assert.AreEqual(0, _h.Decisions(), "a pinch begun on a target never approves");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
        }

        [UnityTest]
        public IEnumerator TheSettingsInFrontHideTheBlockAndNothingIsApprovableMeanwhile()
        {
            yield return _h.GoLive();
            yield return _h.OpenSettings();
            yield return Request("r1", "s1", "safe"); // the selected agent asks while the settings stand in front
            Assert.IsFalse(_h.Root.Session.ViewShown, "the window is covered");
            Assert.IsNull(_h.Root.Session.Card, "no block behind the settings");
            Assert.IsFalse(_h.Block.IsShown);
            yield return _h.PinchHold(true, 30);
            Assert.AreEqual(0, _h.Decisions(), "nothing is approvable meanwhile");
            _h.ReleaseHands();
            _h.Root.Settings.CloseButton.Click();
            yield return Frames(2);
            Assert.AreEqual("r1", _h.Block.RequestId, "the block shows when the settings close");
            yield return _h.PinchHold(false, 3);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(MenuHand.None, _h.Root.Menu.Shown);
            Assert.AreEqual(0, _h.Decisions());
        }

        [UnityTest]
        public IEnumerator TrennenOrNeuKoppelnInTheFrameARequestArrivesDecideNothing()
        {
            yield return _h.GoLive();
            yield return _h.OpenSettings();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            _h.Root.Settings.DisconnectButton.Click(); // Trennen in the same frame
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Start, _h.Root.Current);
            Assert.IsNull(_h.Root.Session);
            Assert.IsFalse(_h.Root.Workspace.IsShown, "no phantom block");
            yield return _h.PinchHold(true, 30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing decided");
        }

        [UnityTest]
        public IEnumerator UntrustedTextOnTheBlockStaysPlainAndCapped()
        {
            yield return _h.GoLive();
            const string evil = "<b>rm</b> -rf / <color=red>x</color> \\\\u0041 ";
            _h.Socket.Receive("{\"type\":\"perm_open\",\"request\":" + WorkspaceHarness.Request("r1", "s1", "safe", evil + new string('y', 400)) +
                              ",\"explain\":{\"text\":\"<size=200%>Gross</size>: weiter\",\"replies\":[],\"source\":\"rules\"}}");
            yield return Frames(3);
            Assert.IsTrue(_h.Desk.BlockShown);
            StringAssert.StartsWith("<b>rm</b> -rf / <color=red>x</color>", _h.Block.SubjectShown, "tags are shown, never applied");
            StringAssert.EndsWith(Ui.CutSuffix, _h.Block.SubjectShown, "cut, and it says so");
            StringAssert.StartsWith("<size=200%>Gross</size>", _h.Block.HeadlineShown);
            foreach (var t in _h.Block.GetComponentsInChildren<TMPro.TextMeshPro>())
                if (t.name != "Risk") Assert.IsFalse(t.richText, t.name + " is plain");
            Assert.IsTrue(_h.Root.Session.Card.NeedsTwoHand, "a request the block had to cut needs both hands");
            Assert.AreEqual(Texts.RiskDangerous, _h.Block.RiskShown);
            Assert.LessOrEqual(_h.Block.Height, _h.Block.MaxHeight + 1e-4f, "the block stays inside its room");
            Assert.Less(_h.Block.Height, 0.2f + 1e-4f);
            yield return _h.PinchHold(false, 30);
            Assert.AreEqual(0, _h.Decisions(), "one hand never approves what the block had to cut");
        }

        [UnityTest]
        public IEnumerator UntrustedTitlesInTheListTheHeaderAndTheNoticeStayPlain()
        {
            yield return _h.GoLive(agents: Agent("s1", "<b>A</b>", "working", "<color=red>Rot</color> zwei") + "," +
                                           Agent("s2", "B", "working", "<i>Kursiv</i>"));
            StringAssert.Contains("<i>Kursiv</i>", _h.Rows.TitleShown("s2"), "a tag in a title is shown, never applied");
            StringAssert.StartsWith("<color=red>Rot</color>", _h.Rows.Shown.First(r => r.Sid == "s1").Title);
            _h.Socket.Receive(PermOpen("r1", "s2"));
            yield return Frames(2);
            StringAssert.StartsWith("<i>Kursiv</i> needs you", _h.Root.Workspace.Pill.Line);
            foreach (var t in _h.Root.Workspace.GetComponentsInChildren<TMPro.TextMeshPro>())
                if (t.GetComponentInParent<WorkspaceHeader>() == null || t.name == "Say")
                    Assert.IsFalse(t.richText, t.name + " is plain");
            // the header's rich text holds our own words and numbers only, never an agent's title
            StringAssert.DoesNotContain("Kursiv", _h.Root.Workspace.Header.Plain);
            StringAssert.DoesNotContain("Rot", _h.Root.Workspace.Header.Plain);
        }
    

        [UnityTest]
        public IEnumerator MarkupAndBidiCharactersInAPlanStayPlainInTheOverviewAndTheOpenChecklist()
        {
            // U6 review 1, Minor 4: plan steps are agent text; they are drawn plain everywhere
            _h.OpenConversation = false;
            var plan = ",\"plan\":{\"steps\":[{\"text\":\"<color=red>Rot</color> <size=300%>gross\",\"status\":\"in_progress\"}," +
                       "{\"text\":\"\u202Eumgedreht\",\"status\":\"pending\"}],\"more\":0,\"updatedAt\":1}";
            yield return _h.GoLive(agents: Agent("s1", "A", "working", "Eins").TrimEnd('}') + plan + "}," + Agent("s2", "B", "working", "Zwei"));
            yield return Frames(3);
            StringAssert.Contains("<color=red>Rot</color>", _h.Rows.NowShown("s1"), "the tag is shown above the dot, never applied");
            foreach (var t in _h.Rows.GetComponentsInChildren<TMPro.TextMeshPro>()) Assert.IsFalse(t.richText, t.name + " is plain");
            yield return _h.PokeRow("s1");
            yield return Frames(2);
            StringAssert.Contains("<color=red>Rot</color>", _h.Conversation.PlanShown);
            StringAssert.Contains("U+202E", _h.Conversation.PlanShown, "the bidi character is shown as a marker");
            foreach (var t in _h.Conversation.GetComponentsInChildren<TMPro.TextMeshPro>()) Assert.IsFalse(t.richText, t.name + " is plain");
        }

        // ---- U13: a request is approvable only on its open row, once the opening motion has finished ----

        IEnumerator PokeLeft(PokeButton b)
        {
            var t = b.transform;
            _h.Hands.Left = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            _h.Hands.Left = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.01f)));
            yield return Frames(2);
            _h.Hands.Left = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, 0.003f)));
            yield return null;
            _h.Hands.Left = Hand();
            yield return null;
        }

        [UnityTest]
        public IEnumerator AHoldBegunWhileTheRowStillOpensNeverApproves([Values("safe", "destructive")] string risk)
        {
            var two = risk != "safe";
            yield return _h.GoLive();
            yield return Request("r1", "s2", risk);
            // where a pinch counts once the block stands: learnt with the row open, then the row is closed again
            yield return _h.OpenRow("s2");
            Assert.IsTrue(_h.Desk.BlockShown);
            var at = _h.GatePoint();
            yield return _h.CloseRow();
            Assert.IsNull(_h.Root.Session.Card);

            // the row is pressed, and at once the hands pinch and hold, all through the opening and 3 s beyond
            _h.Rows.RowOf("s2").Click();
            yield return null;
            Assert.IsFalse(_h.Root.Workspace.OpenSettled, "the row still opens");
            _h.Hands.Right = Hand(pinchAt: at);
            _h.Hands.Left = Hand(pinchAt: at + Vector3.left * 0.15f);
            yield return null;
            _h.Hands.Right = Hand(1f, pinchAt: at);
            if (two) _h.Hands.Left = Hand(1f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(40);
            Assert.IsTrue(_h.Root.Workspace.OpenSettled, "the row has opened meanwhile");
            Assert.AreEqual("r1", _h.Block.RequestId, "its request stands on it");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the hold begun during the opening never counts");
            Assert.AreEqual(0, _h.Decisions(), "nothing is sent");
            Assert.AreEqual(_h.Block.LightsShown, _h.Block.LitShown, "every light is still lit");
            // a fresh pinch after the motion ended approves it
            _h.ReleaseHands();
            yield return Frames(2);
            yield return _h.PinchHold(two, two ? 17 : 10);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(1, _h.Decisions(), "a fresh hold does");
        }

        [UnityTest]
        public IEnumerator AHoldBegunWhileTheContentShowsButTheMotionStillRunsNeverApproves()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s2");
            yield return _h.OpenRow("s2");
            var at = _h.GatePoint();
            yield return _h.CloseRow();
            _h.Rows.RowOf("s2").Click();
            // in small steps of time, so that the motion's last part takes several frames
            Time.captureDeltaTime = 0.02f;
            var guard = 0;
            while (_h.Root.Session.Card == null && guard++ < 60) yield return null;
            Assert.AreEqual("r1", _h.Root.Session.Card?.Id, "the request's content shows");
            Assert.IsFalse(_h.Root.Workspace.OpenSettled, "while the row still moves");
            // the open hand is seen with the request on screen, then it pinches: a clean pinch but for the motion
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            Assert.IsFalse(_h.Root.Workspace.OpenSettled, "the row still moves as the pinch begins");
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return null;
            Time.captureDeltaTime = 0.1f;
            yield return Frames(30);
            Assert.IsTrue(_h.Root.Workspace.OpenSettled);
            Assert.AreEqual(0, _h.Decisions(), "nothing is sent");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            _h.ReleaseHands();
            yield return Frames(2);
            yield return _h.PinchHold(false, 10);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(1, _h.Decisions(), "a fresh hold after the motion does");
        }

        [UnityTest]
        public IEnumerator SwitchingRowsCancelsAHoldAndTheHeldPinchNeverApprovesTheOtherRequest()
        {
            yield return HoldBegunOnTheBlock();
            _h.Socket.Receive(PermOpen("r2", "s2"));
            yield return null;
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "the hold on r1 still runs");
            // the left hand pokes the other agent's thin line while the right keeps pinching
            yield return PokeLeft(_h.Rows.RowOf("s2"));
            Assert.AreEqual("s2", _h.Desk.OpenSid, "the row switched");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "switching rows cancels the hold");
            yield return Frames(30); // the same pinch, still held, while s2's row opens and its request shows
            Assert.AreEqual("r2", _h.Block.RequestId, "the other agent's request");
            Assert.AreEqual(0, _h.Decisions(), "the pinch held across the switch approves neither");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            _h.ReleaseHands();
            yield return Frames(2);
            yield return _h.PinchHold(false, 10);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(1, _h.Decisions());
            StringAssert.Contains("\"id\":\"r2\"", _h.DecisionFrames().Single(), "a fresh hold approves what is shown");
        }

        [UnityTest]
        public IEnumerator ClosingTheRowWithAllAgentsCancelsAHold()
        {
            yield return HoldBegunOnTheBlock();
            yield return PokeLeft(_h.Root.Workspace.Header.AllButton);
            Assert.IsNull(_h.Desk.OpenSid, "the row closed");
            Assert.IsNull(_h.Root.Session.Card, "no request is on screen");
            yield return AssertCancelled("All agents");
        }

        [UnityTest]
        public IEnumerator TheHeadlineChangingCancelsAHold()
        {
            yield return HoldBegunOnTheBlock();
            var said = _h.Root.Workspace.Header.Said;
            StringAssert.StartsWith("Claude listet die Dateien", said, "the headline is the request's sentence");
            _h.Socket.Receive("{\"type\":\"perm_explain\",\"id\":\"r1\",\"explain\":{\"text\":\"Claude liest dein Home: ls -la\",\"replies\":[],\"source\":\"ai\"}}");
            yield return Frames(2);
            Assert.AreNotEqual(said, _h.Root.Workspace.Header.Said, "the headline changed");
            StringAssert.StartsWith("Claude liest dein Home", _h.Root.Workspace.Header.Said);
            yield return AssertCancelled("the headline changed");
        }

        [UnityTest]
        public IEnumerator NothingIsApprovableInTheOverviewTheRailOrWithNoRowOpen()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s1");
            Assert.IsTrue(_h.Desk.BlockShown);
            var at = _h.GatePoint();
            yield return _h.CloseRow();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(30);
            Assert.AreEqual(0, _h.Decisions(), "no row open: nothing to approve");
            _h.ReleaseHands();
            yield return Frames(2);
            yield return _h.Poke(_h.Root.Workspace.Header.CollapseButton);
            yield return Frames(4);
            Assert.IsTrue(_h.Root.Workspace.Folded);
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(30);
            Assert.AreEqual(0, _h.Decisions(), "the rail: nothing to approve");
        }

        [UnityTest]
        public IEnumerator ARowPressOpensItAndOpeningAndClosingAreLogged()
        {
            yield return _h.GoLive();
            yield return _h.CloseRow();
            CollectionAssert.Contains(_h.Lotse, "[Lotse] ev row close");
            yield return Request("r1", "s2");
            _h.Lotse.Clear();
            yield return _h.OpenRow("s2");
            var index = _h.Root.Session.Store.Agents.ToList().FindIndex(a => a.Sid == "s2");
            CollectionAssert.Contains(_h.Lotse, "[Lotse] ev row open id=" + index + " kind=request");
            CollectionAssert.Contains(_h.Lotse, "[Lotse] ev screen name=open");
            yield return _h.OpenRow("s3");
            var done = _h.Root.Session.Store.Agents.ToList().FindIndex(a => a.Sid == "s3");
            CollectionAssert.Contains(_h.Lotse, "[Lotse] ev row open id=" + done + " kind=working");
            _h.Lotse.Clear();
            yield return _h.CloseRow();
            CollectionAssert.Contains(_h.Lotse, "[Lotse] ev row close");
            yield return Frames(2);
            CollectionAssert.Contains(_h.Lotse, "[Lotse] ev screen name=overview");
        }

        [UnityTest]
        public IEnumerator TheStopBarShowsTheHoldsProgressAndIsNotAControl()
        {
            yield return _h.GoLive();
            yield return Request("r1", "s1", "destructive");
            yield return _h.PinchHold(true, 7);
            Assert.Greater(_h.Root.Session.HoldProgress, 0.2f, "a two-hand hold runs");
            var b = _h.Block;
            Assert.AreEqual(StopBar.TwoHandLights, b.LightsShown, "two hands: 21 lights");
            Assert.AreEqual(StopBar.LitCount(_h.Root.Session.HoldProgress, true), b.LitShown, "lit as the hold's progress says, and nothing else");
            Assert.Less(b.LitShown, b.LightsShown, "going dark");
            Assert.AreEqual(Texts.KeepHolding, b.HintShown);
            // nothing on the bar is pressable: the block's only controls are Later and Deny
            var controls = PokeButton.All.Where(p => p.transform.IsChildOf(b.transform)).Select(p => p.Label).OrderBy(l => l).ToArray();
            CollectionAssert.AreEqual(new[] { Texts.Deny, Texts.Later }, controls);
            Assert.IsNull(b.transform.GetComponentInChildren<Collider>(), "no collider either");
            _h.ReleaseHands();
            yield return Frames(6);
            Assert.AreEqual(b.LightsShown, b.LitShown, "let go: the lights are back on");
            Assert.AreEqual(Texts.HintTwoHand, b.HintShown);
            Assert.AreEqual(0, _h.Decisions());
        }
    }
}
