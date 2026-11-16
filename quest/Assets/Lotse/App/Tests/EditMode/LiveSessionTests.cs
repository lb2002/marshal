#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// The session's approval pipeline on the live bridge, through a fake socket (U5a: the block is the selected agent's
    /// oldest request; nothing opens by itself; only the block's gesture allows and only "Ablehnen" denies).
    /// </summary>
    public class LiveSessionTests
    {
        const double Frame = 20;

        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);
        static readonly HandSample Palm = new HandSample(true, true, 0f, true);

        FakeNet _net = null!;
        LiveSource _live = null!;
        DemoSession _s = null!;
        FakeSocket _socket = null!;
        readonly List<string> _dropped = new List<string>();
        int _lost;

        [SetUp]
        public void SetUp()
        {
            _net = new FakeNet();
            _live = new LiveSource(new HostBook(new MemoryStore()), _net.Open);
            _s = new DemoSession(_live);
            _s.DecisionDropped += _dropped.Add;
            _s.ConnectionLost += () => _lost++;
            _dropped.Clear();
            _lost = 0;

            Assert.IsTrue(_live.PairWithCode(Bridge.Host, "123456"));
            _socket = _net.Last;
            _socket.Open();
            _socket.Receive(Bridge.EmptySnapshot);
            _socket.Receive(Bridge.Paired("tok"));
            _s.SetViewShown(true);
            _s.Select("s1");
            Step(Frame);
            Assert.AreEqual(LinkState.Live, _live.State);
        }

        void Step(double ms, HandSample? left = null, HandSample? right = null)
        {
            for (double t = 0; t < ms; t += Frame) _s.Step(Frame, left ?? Open, right ?? Open);
        }

        /// <summary>Everything the client sent after the hello.</summary>
        string[] Decisions() => _socket.Sent.Skip(1).Where(f => f.Contains("perm_decision")).ToArray();

        void OpenCard(string id, string risk = "safe")
        {
            _socket.Receive(Bridge.PermOpen(id, risk));
            Step(Frame);
            Assert.AreEqual(id, _s.Card?.Id, "the selected agent's block is shown");
            Assert.AreEqual(CardMode.Hold, _s.Mode);
            Step(2 * Frame); // hands seen released: the rising edge is armed
        }

        const string AllowR1 = "{\"type\":\"perm_decision\",\"decision\":{\"id\":\"r1\",\"behavior\":\"allow\",\"confirm\":\"pinch-hold\"}}";
        const string DenyR1 = "{\"type\":\"perm_decision\",\"decision\":{\"id\":\"r1\",\"behavior\":\"deny\"}}";

        [Test]
        public void APinchHoldSendsTheAllowAndTheBlockWaitsForTheBridge()
        {
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            CollectionAssert.AreEqual(new[] { AllowR1 }, Decisions());
            Assert.AreEqual("r1", _s.Card?.Id, "the block stays until the bridge closes it");
            Assert.AreEqual(CardMode.Sent, _s.Mode);

            Step(3000, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length, "one decision per request");

            _socket.Receive(Bridge.PermClosed("r1", "allow"));
            Step(Frame);
            Assert.IsNull(_s.Card);
            Assert.AreEqual(CardMode.None, _s.Mode);
        }

        [Test]
        public void ADestructiveRequestIsAllowedOnlyWithBothHands()
        {
            OpenCard("r1", "destructive");
            Step(5000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions(), "one hand never approves a destructive request");
            Step(2 * Frame);
            Step(GestureMachine.TwoHandHoldMs + 2 * Frame, Pinch, Pinch);
            CollectionAssert.AreEqual(
                new[] { "{\"type\":\"perm_decision\",\"decision\":{\"id\":\"r1\",\"behavior\":\"allow\",\"confirm\":\"two-hand\"}}" },
                Decisions());
        }

        [Test]
        public void AConfirmedForAnotherIdIsDroppedAndLogged()
        {
            OpenCard("r1");
            _s.HandleGesture(GestureEvent.Confirmed("pinch-hold", "r2"));
            CollectionAssert.IsEmpty(Decisions(), "nothing is sent");
            Assert.AreEqual(0, _s.Decisions.Count);
            Assert.AreEqual(CardMode.Hold, _s.Mode);
            Assert.AreEqual(1, _dropped.Count, "the drop is reported for the log");
            StringAssert.Contains("r2", _dropped[0]);
            StringAssert.Contains("r1", _dropped[0]);

            // the same seam with the right id does send: the drop above was the id check
            _s.HandleGesture(GestureEvent.Confirmed("pinch-hold", "r1"));
            Assert.AreEqual(1, Decisions().Length);
        }

        // ---- U5a: only "Ablehnen" denies ----

        [Test]
        public void AblehnenSendsOneDenyForTheShownBlock()
        {
            OpenCard("r1");
            Assert.IsTrue(_s.Deny("r1"));
            CollectionAssert.AreEqual(new[] { DenyR1 }, Decisions(), "a deny without a message");
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            Assert.IsFalse(_s.Deny("r1"), "one decision per request");
            Step(3000, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length);

            _socket.Receive(Bridge.PermClosed("r1", "deny"));
            Step(Frame);
            Assert.IsNull(_s.Card);
        }

        [Test]
        public void AblehnenForAnotherRequestOrAHiddenBlockSendsNothing()
        {
            _socket.Receive(Bridge.PermOpen("r2", sid: "s2"));
            OpenCard("r1");
            Assert.IsFalse(_s.Deny("r2"), "the other agent's request is not on the block");
            _s.Later();
            Assert.IsFalse(_s.Deny("r1"), "Später hid the block");
            _s.Select("s1");
            _s.SetViewShown(false);
            Assert.IsFalse(_s.Deny("r1"), "no window, no block");
            CollectionAssert.IsEmpty(Decisions());
            Assert.AreEqual(3, _dropped.Count);
        }

        [Test]
        public void ThePalmNoLongerDeniesItOnlyCancelsTheHold()
        {
            OpenCard("r1");
            Step(400, Open, Pinch);
            Assert.Greater(_s.HoldProgress, 0f);
            Step(GestureMachine.PalmStopMs + 4 * Frame, Palm, Pinch);
            Assert.AreEqual(0f, _s.HoldProgress, "the palm cancels the hold");
            Step(3000, Palm, Open);
            CollectionAssert.IsEmpty(Decisions(), "the push-away gesture is gone: no deny");
            Assert.AreEqual(CardMode.Hold, _s.Mode, "the block still waits");
            Assert.AreEqual("r1", _s.Card?.Id);
        }

        // ---- U5a: what cancels a hold ----

        [Test]
        public void SelectingAnotherAgentCancelsTheHoldAndComingBackNeedsAFreshPinch()
        {
            OpenCard("r1");
            Step(600, Open, Pinch);
            Assert.Greater(_s.HoldProgress, 0.5f);
            _s.Select("s2");
            Assert.IsNull(_s.Card, "s2 has no request: no block");
            Assert.AreEqual(0f, _s.HoldProgress);
            Step(400, Open, Pinch);
            _s.Select("s1");
            Assert.AreEqual("r1", _s.Card?.Id);
            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions(), "the pinch held across the switch never confirms");
            Step(2 * Frame);
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length, "a fresh hold does");
        }

        [Test]
        public void SpaeterCancelsTheHoldSendsNothingAndSelectingAgainShowsTheBlock()
        {
            OpenCard("r1");
            Step(600, Open, Pinch);
            _s.Later();
            Assert.IsNull(_s.Card);
            Assert.IsTrue(_s.PutOff);
            Assert.AreEqual(0f, _s.HoldProgress);
            Step(3000, Pinch, Pinch);
            CollectionAssert.IsEmpty(_socket.Sent.Skip(1).Where(f => f.Contains("perm_decision")), "Später sends nothing");
            _s.Select("s1");
            Assert.AreEqual("r1", _s.Card?.Id, "selecting the agent again shows the block again");
            Assert.AreEqual(CardMode.Hold, _s.Mode);
        }

        [Test]
        public void TheWindowGoingAwayCancelsTheHold()
        {
            OpenCard("r1");
            Step(600, Open, Pinch);
            _s.SetViewShown(false);
            Assert.IsNull(_s.Card);
            Step(3000, Open, Pinch);
            _s.SetViewShown(true);
            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions());
        }

        [Test]
        public void ARequestOfAnUnselectedAgentCannotBeApprovedByAnyGesture()
        {
            _socket.Receive(Bridge.PermOpen("r9", "safe", sid: "s2"));
            Step(Frame);
            Assert.IsNull(_s.Card, "s1 is selected: s2's request opens nothing");
            Step(2 * Frame);
            Step(5000, Pinch, Pinch);
            Step(2 * Frame);
            Step(5000, Open, Pinch);
            _s.HandleGesture(GestureEvent.Confirmed("two-hand", "r9"));
            CollectionAssert.IsEmpty(Decisions(), "nothing is approved without its block");
            Assert.AreEqual(0, _s.Decisions.Count);
        }

        [Test]
        public void NoBlockOnScreenMeansNoGestureApprovesAnything()
        {
            _s.SetViewShown(false);
            _socket.Receive(Bridge.PermOpen("r1"));
            Step(Frame);
            Assert.IsNull(_s.Card);
            Step(2 * Frame);
            Step(5000, Pinch, Pinch);
            CollectionAssert.IsEmpty(Decisions());
        }

        [Test]
        public void ADisconnectClosesTheBlockAndSendsNothing()
        {
            OpenCard("r1");
            Step(400, Open, Pinch); // mid-hold
            Assert.Greater(_s.HoldProgress, 0f);

            _socket.Drop(1006);
            Step(Frame, Open, Pinch);
            Assert.AreEqual(1, _lost, "the banner is raised");
            Assert.IsNull(_s.Card);
            Assert.AreEqual(CardMode.None, _s.Mode);
            CollectionAssert.IsEmpty(_s.Store.Pending, "the bridge has dropped every request");

            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions(), "nothing was sent");
            Assert.AreEqual(0, _s.Decisions.Count);
            foreach (var sock in _net.Sockets.Skip(1)) CollectionAssert.IsEmpty(sock.Sent.Where(t => t.Contains("perm_decision")));
        }

        [Test]
        public void ADisconnectWhileWaitingForTheBridgeClosesTheBlock()
        {
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            _socket.Drop(1006);
            Step(Frame);
            Assert.IsNull(_s.Card);
            Assert.AreEqual(1, Decisions().Length, "the one allow from before, nothing more");
        }

        [Test]
        public void APermClosedMidHoldResetsTheGestures()
        {
            OpenCard("r1");
            Step(400, Open, Pinch);
            Assert.AreEqual(GestureState.PinchHolding, _s.GestureState);

            _socket.Receive(Bridge.PermClosed("r1", "timeout"));
            Step(Frame, Open, Pinch);
            Assert.IsNull(_s.Card);
            Assert.AreEqual(GestureState.Idle, _s.GestureState, "the hold is gone");

            // the next request while the same pinch is still held: it never confirms (needs a fresh pinch)
            _socket.Receive(Bridge.PermOpen("r2"));
            Step(Frame, Open, Pinch);
            Assert.AreEqual("r2", _s.Card?.Id);
            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions());
        }

        [Test]
        public void ThePinchOfTheFirstRequestNeverApprovesTheSecond()
        {
            _socket.Receive(Bridge.PermOpen("r1"));
            _socket.Receive(Bridge.PermOpen("r2"));
            Step(3 * Frame);
            Assert.AreEqual("r1", _s.Card?.Id, "the oldest first");
            Assert.AreEqual(1, _s.MoreRequests, "+1 weitere");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            _socket.Receive(Bridge.PermClosed("r1", "allow"));
            Step(Frame, Open, Pinch);
            Assert.AreEqual("r2", _s.Card?.Id);
            Assert.AreEqual(0, _s.MoreRequests);
            Step(3000, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length, "the pinch still held from r1 does not approve r2");
        }

        [Test]
        public void APermExplainUpdatesTheBlockText()
        {
            OpenCard("r1");
            var changes = 0;
            _s.CardChanged += () => changes++;
            _socket.Receive(Bridge.PermExplain("r1", "Neuer Text.", "Die KI sagt etwas."));
            Step(Frame);
            Assert.AreEqual(1, changes);
            Assert.AreEqual("Neuer Text.", _s.CardExplain?.Text);
            Assert.AreEqual("Die KI sagt etwas.", _s.CardExplain?.AiText);
            Assert.AreEqual(CardMode.Hold, _s.Mode, "still approvable");
        }

        [Test]
        public void AChangedExplanationCancelsAHoldInProgress()
        {
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs / 2, Open, Pinch);
            Assert.Greater(_s.HoldProgress, 0f, "a hold runs");
            _socket.Receive(Bridge.PermExplain("r1", "Neuer Text.", "Die KI warnt."));
            Step(Frame, Open, Pinch);
            Assert.AreEqual(0f, _s.HoldProgress, "every visible change cancels a hold (U5a review 2)");
            Assert.AreEqual(CardMode.Hold, _s.Mode);
            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions(), "the pinch held from before the change approves nothing");
            Step(2 * Frame);
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length, "a fresh pinch on the new text does");
        }

        [Test]
        public void ABridgeErrorWhileWaitingReturnsTheBlockToHoldWithAFreshGesture()
        {
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            string? error = null;
            _s.BridgeError += e => error = e;

            _socket.Receive(Bridge.Error("needs-two-hand"));
            Step(Frame, Open, Pinch);
            Assert.AreEqual("needs-two-hand", error);
            Assert.AreEqual(CardMode.Hold, _s.Mode, "approvable again");
            Step(3000, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length, "the pinch from before does not approve again");
            Step(2 * Frame);
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(2, Decisions().Length, "a fresh hold does");
        }

        [Test]
        public void ASentBlockStaysSentWhenTheUserLooksAtAnotherAgentAndComesBack()
        {
            OpenCard("r1");
            Assert.IsTrue(_s.Deny("r1"));
            _s.Select("s2");
            _s.Select("s1");
            Assert.AreEqual("r1", _s.Card?.Id);
            Assert.AreEqual(CardMode.Sent, _s.Mode, "it waits for the bridge, it is not approvable again");
            Step(3000, Open, Pinch);
            Assert.AreEqual(1, Decisions().Length);
        }

        [Test]
        public void TheAgentsOfAReconnectSnapshotAreShown()
        {
            var updates = new List<string>();
            _s.AgentUpdated += (a, prev) => updates.Add(a.Sid + ":" + (prev ?? "new") + "->" + a.Status);
            _socket.Receive(Bridge.Agent("s1", "working"));
            Step(Frame);
            _socket.Receive("{\"type\":\"snapshot\",\"agents\":[{\"sid\":\"s1\",\"name\":\"Alpha\",\"status\":\"blocked\",\"lastAction\":\"\"," +
                            "\"since\":0,\"files\":[],\"waitingMsTotal\":0},{\"sid\":\"s2\",\"name\":\"Beta\",\"status\":\"done\"," +
                            "\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}],\"pending\":[],\"explains\":{}}");
            Step(Frame);
            CollectionAssert.AreEqual(new[] { "s1:new->working", "s1:working->blocked", "s2:new->done" }, updates);
        }

        [Test]
        public void ACutSubjectStaysTwoHandedWhenASnapshotBringsTheRequestAgain()
        {
            OpenCard("r1");
            Assert.IsTrue(_s.SubjectCut("r1"));
            _socket.Receive("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[" + Bridge.Request("r1") + "],\"explains\":{}}");
            Step(Frame);
            Assert.AreEqual("r1", _s.Card?.Id);
            Assert.IsTrue(_s.Card!.NeedsTwoHand, "the fresh copy is still cut for display");
            Step(2 * Frame);
            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Decisions(), "one hand never approves what the block had to cut");
        }

        [Test]
        public void AnAllowClosedByTimeoutIsNotShownAsApproved()
        {
            OpenCard("r1");
            var closed = new List<string>();
            _s.CardClosed += (req, reason, sent) => closed.Add(req.Id + "/" + reason + "/" + (sent?.Behavior ?? "-"));
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            _socket.Receive(Bridge.PermClosed("r1", "timeout"));
            Step(Frame);
            CollectionAssert.AreEqual(new[] { "r1/timeout/allow" }, closed, "the reason travels with the close");
            Assert.AreEqual(Texts.TimedOut, Texts.Closed("timeout", ClientDecision.Allow("r1", "pinch-hold")));
            Assert.AreEqual("Timed out. Please answer in the terminal.", Texts.TimedOut);
            Assert.AreNotEqual(Texts.Approved, Texts.Closed("hook-gone", ClientDecision.Allow("r1", "pinch-hold")));
        }

        [Test]
        public void TheBridgesAllowAndDenyAreShownAsSuch()
        {
            OpenCard("r1");
            string? reason = null;
            _s.CardClosed += (req, r, sent) => reason = r;
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            _socket.Receive(Bridge.PermClosed("r1", "allow"));
            Step(Frame);
            Assert.AreEqual("allow", reason);
            Assert.AreEqual("Allowed", Texts.Closed("allow", ClientDecision.Allow("r1", "pinch-hold")));
            Assert.AreEqual("Denied", Texts.Closed("deny", ClientDecision.Deny("r1")));
            Assert.AreEqual(Texts.NotTaken, Texts.Closed("hook-gone", null));
        }

        [Test]
        public void ARequestOfAnUnselectedAgentThatClosesIsReportedForItsConversation()
        {
            var closed = new List<string>();
            _s.RequestClosed += (req, reason, sent) => closed.Add(req.Sid + "/" + req.Id + "/" + reason);
            var cards = 0;
            _s.CardClosed += (req, reason, sent) => cards++;
            _socket.Receive(Bridge.PermOpen("r5", sid: "s2"));
            Step(Frame);
            _socket.Receive(Bridge.PermClosed("r5", "timeout"));
            Step(Frame);
            CollectionAssert.AreEqual(new[] { "s2/r5/timeout" }, closed);
            Assert.AreEqual(0, cards, "it was never on the block");
        }

        [Test]
        public void ABridgeThatDoesNotAnswerADecisionDropsTheBlock()
        {
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            Step(LiveSource.AnswerTimeoutMs - 4 * Frame);
            Assert.AreEqual(CardMode.Sent, _s.Mode, "still waiting");
            Step(6 * Frame);
            Assert.IsNull(_s.Card, "no perm_closed within 5 s: the link counts as lost");
            Assert.AreEqual(1, _lost);
            Assert.AreEqual(LinkState.Retrying, _live.State);
        }

        [Test]
        public void AnAnswerThatNeverCameIsShownAsUnclear()
        {
            string? closedText = null;
            _s.CardClosed += (req, reason, sent) => closedText = Texts.Closed(reason, sent);
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            Step(LiveSource.AnswerTimeoutMs + 2 * Frame);
            Assert.AreEqual(Texts.SentUnclear, closedText);
        }

        [Test]
        public void ASentDenyThenADroppedLinkIsShownAsUnclear()
        {
            string? closedText = null;
            _s.CardClosed += (req, reason, sent) => closedText = Texts.Closed(reason, sent);
            OpenCard("r1");
            Assert.IsTrue(_s.Deny("r1"));
            Assert.AreEqual(CardMode.Sent, _s.Mode);
            _socket.Drop(1006);
            Step(Frame);
            Assert.AreEqual(Texts.SentUnclear, closedText);
        }

        [Test]
        public void AnIncidentNeverOpensABlockOrDecides()
        {
            var incidents = new List<Incident>();
            _s.IncidentRaised += incidents.Add;
            _socket.Receive("{\"type\":\"incident\",\"incident\":{\"kind\":\"collision\",\"sids\":[\"a\",\"b\"],\"detail\":\"A und B bearbeiten x.ts\",\"t\":1}}");
            Step(5 * Frame, Pinch, Pinch);
            Assert.AreEqual(1, incidents.Count);
            Assert.IsNull(_s.Card);
            OpenCard("r1");
            _socket.Receive("{\"type\":\"incident\",\"incident\":{\"kind\":\"budget\",\"sids\":[\"a\"],\"detail\":\"A: 15 USD\",\"t\":2}}");
            Step(Frame);
            Assert.AreEqual(2, incidents.Count);
            Assert.AreEqual("r1", _s.Card?.Id);
            Assert.AreEqual(CardMode.Hold, _s.Mode);
            CollectionAssert.IsEmpty(Decisions(), "nothing is sent");
            Assert.AreEqual(0, _s.Decisions.Count);
        }

        [Test]
        public void TheProbeReplyNeverReachesTheBlock()
        {
            OpenCard("r1");
            Step(GestureMachine.PinchHoldMs + 2 * Frame, Open, Pinch);
            string? error = null;
            _s.BridgeError += e => error = e;
            _socket.Receive(Bridge.PermClosed("r1", "allow"));
            Step(LiveSource.IdleProbeMs + Frame);
            Assert.AreEqual(LiveSource.Probe, _socket.Sent.Last());
            _socket.Receive(Bridge.Error("bad-message"));
            Step(Frame);
            Assert.IsNull(error);
            Assert.AreEqual(LinkState.Live, _live.State);
        }

        [Test]
        public void NothingIsSentWhileTheBridgeIsAway()
        {
            OpenCard("r1");
            _socket.Drop(1006);
            Step(Frame);
            // a block that somehow stayed would still not reach a socket: Send refuses while not live
            Assert.IsNull(_live.Send(ClientDecision.Allow("r1", "pinch-hold")));
            Assert.IsFalse(_s.Finished, "a live session never finishes by itself");
        }

        // ---- notices ----

        [Test]
        public void EachRequestGetsOneNoticeNeverTwoAtOnceAndNoneForTheBlockOnScreen()
        {
            var notices = new List<string>();
            _s.NoticeRaised += r => notices.Add(r.Id);
            _socket.Receive(Bridge.PermOpen("r1", sid: "s1")); // on the block at once: no notice
            _socket.Receive(Bridge.PermOpen("r2", sid: "s2"));
            _socket.Receive(Bridge.PermOpen("r3", sid: "s3"));
            Step(Frame);
            CollectionAssert.AreEqual(new[] { "r2" }, notices, "the oldest not on screen, at once");
            Step(DemoSession.NoticeMs - 4 * Frame);
            CollectionAssert.AreEqual(new[] { "r2" }, notices, "never while one stands");
            Step(8 * Frame);
            CollectionAssert.AreEqual(new[] { "r2", "r3" }, notices);
            Step(30000);
            CollectionAssert.AreEqual(new[] { "r2", "r3" }, notices, "once per request");
            CollectionAssert.IsEmpty(Decisions(), "a notice decides nothing");
        }

        [Test]
        public void TheBlocksCountdownRunsFromWhenTheRequestWasFirstSeen()
        {
            OpenCard("r1"); // createdAt 1000, expiresAt 91000: 90 s
            var start = _s.CardTimeLeft!.Value;
            Assert.Greater(start, 0.99);
            for (var i = 0; i < 45; i++)
            {
                Step(1000);
                if (i % 10 == 9) _socket.Receive(Bridge.Pong); // the bridge stays alive
            }
            Assert.AreEqual("r1", _s.Card?.Id);
            Assert.AreEqual(0.5, _s.CardTimeLeft!.Value, 0.01);
        }

        [Test]
        public void NoReplyGoesToAnAgentWithAnOpenRequest()
        {
            _socket.Receive(Bridge.Agent("s1", "waiting"));
            OpenCard("r1");
            Assert.IsFalse(_s.SendReply("s1", "Ja, mach weiter"), "the request comes first");
            CollectionAssert.IsEmpty(_socket.Sent.Where(f => f.Contains("agent_reply")));
            _socket.Receive(Bridge.PermClosed("r1", "allow"));
            Step(Frame);
            Assert.IsTrue(_s.SendReply("s1", "Ja, mach weiter"));
        }

        /// <summary>
        /// While the desk is placed, AppRoot feeds untracked hands to the session: an open block must neither
        /// time out nor be decided on our side, however long that takes (the bridge has its own timeout).
        /// </summary>
        [TestCase("safe")]
        [TestCase("destructive")]
        public void UntrackedHandsForTwoMinutesNeitherDecideNorCloseTheBlock(string risk)
        {
            var untracked = new HandSample(false, false, 0f, false);
            OpenCard("r1", risk);
            for (var s = 0; s < 120; s++)
            {
                Step(1000, untracked, untracked);
                if (s % 10 == 9) _socket.Receive(Bridge.Pong); // the bridge stays alive
            }
            Assert.IsEmpty(Decisions(), "nothing decided");
            Assert.AreEqual("r1", _s.Card?.Id, "the block is still open");
            Assert.AreEqual(CardMode.Hold, _s.Mode);
            Assert.AreEqual(LinkState.Live, _live.State);
            Assert.AreEqual(0, _s.Decisions.Count);
        }
    }
}
