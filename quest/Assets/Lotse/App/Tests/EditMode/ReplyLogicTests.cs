#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U3: replying to an agent. When it may be answered, the draft (capped, kept when a card takes priority), an
    /// explicit send with a client rate limit, the bridge's ack and the 5 s timeout. The source and session sides:
    /// the pack's canned reaction, the live frame, and that a reply never touches a card.
    /// </summary>
    public class ReplyLogicTests
    {
        static AgentState Agent(string status, string? source = "claude") =>
            new AgentState { Sid = "a1", Name = "Agent 1", Status = status, Source = source };

        // ---- when "Antworten" is offered ----

        [TestCase("done")]
        [TestCase("waiting")]
        [TestCase("failed")]
        public void AnAgentThatWaitsOrStoppedCanBeAnswered(string status)
        {
            Assert.AreEqual(ReplyAvailability.Available, ReplyLogic.Can(Agent(status), null));
        }

        [TestCase("working")]
        [TestCase("reading")]
        [TestCase("blocked")]
        public void AWorkingAgentCannotYet(string status)
        {
            Assert.AreEqual(ReplyAvailability.NotWaiting, ReplyLogic.Can(Agent(status), null));
            Assert.AreEqual(ReplyAvailability.Available, ReplyLogic.Can(Agent(status), "Deine Antwort auf die Frage"),
                "unless its detail says it waits for something");
            Assert.AreEqual(ReplyAvailability.NotWaiting, ReplyLogic.Can(Agent(status), ""));
        }

        [TestCase("muse")]
        [TestCase("codex")]
        [TestCase("gemini")]
        [TestCase(null)]
        public void OnlyClaudeCodeForNow(string? source)
        {
            Assert.AreEqual(ReplyAvailability.NotSupported, ReplyLogic.Can(Agent("done", source), null));
        }

        // ---- opening and the draft ----

        [Test]
        public void ItNeverOpensWhileACardIsOpenOrForAnAgentThatCannotBeAnswered()
        {
            var r = new ReplyLogic();
            Assert.IsFalse(r.Open("a1", blocked: true, ReplyAvailability.Available));
            Assert.IsFalse(r.Open("a1", blocked: false, ReplyAvailability.NotWaiting));
            Assert.IsFalse(r.Open("a1", blocked: false, ReplyAvailability.NotSupported));
            Assert.IsNull(r.OpenSid);
            Assert.IsTrue(r.Open("a1", blocked: false, ReplyAvailability.Available));
            Assert.AreEqual("a1", r.OpenSid);
            Assert.AreEqual(ReplyPhase.Editing, r.Phase);
            Assert.AreEqual("", r.Draft, "nothing prefilled");
        }

        [Test]
        public void TheDraftIsCappedAtAThousandCharacters()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText(new string('x', 1500));
            Assert.AreEqual(ReplyLogic.MaxChars, r.Draft.Length);
            Assert.AreEqual(1000, ReplyLogic.MaxChars);
            r.SetText("a" + new string('b', 998) + "\U0001F600");
            Assert.AreEqual(999, r.Draft.Length, "never half a surrogate pair");
        }

        [Test]
        public void ACardTakesPriorityAndTheDraftIsKept()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Bitte nimm die zweite Variante");
            Assert.AreEqual(ReplyStep.None, r.Step(100, blocked: false));
            Assert.AreEqual(ReplyStep.ClosedForCard, r.Step(200, blocked: true), "a card opened");
            Assert.IsNull(r.OpenSid);
            Assert.AreEqual(ReplyStep.None, r.Step(300, blocked: true), "once");
            Assert.IsTrue(r.Open("a1", false, ReplyAvailability.Available));
            Assert.AreEqual("Bitte nimm die zweite Variante", r.Draft, "the draft is back");
            Assert.AreEqual(ReplyPhase.Editing, r.Phase);
        }

        [Test]
        public void CancelDiscardsTheDraftAndOtherAgentsHaveTheirOwn()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("eins");
            r.Open("a2", false, ReplyAvailability.Available);
            Assert.AreEqual("", r.Draft);
            r.SetText("zwei");
            r.Cancel();
            Assert.IsNull(r.OpenSid);
            r.Open("a2", false, ReplyAvailability.Available);
            Assert.AreEqual("", r.Draft, "cancelled: gone");
            r.Open("a1", false, ReplyAvailability.Available);
            Assert.AreEqual("eins", r.Draft);
        }

        // ---- sending ----

        [Test]
        public void OnlyAnExplicitSendSendsAndNeverAnEmptyText()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            Assert.AreEqual(SendResult.Empty, r.Send(0, out _));
            r.SetText("   \n ");
            Assert.AreEqual(SendResult.Empty, r.Send(0, out _));
            r.SetText("Ja, mach weiter.\nDanke");
            Assert.AreEqual(ReplyPhase.Editing, r.Phase, "typing sends nothing");
            Assert.AreEqual(SendResult.Sent, r.Send(1000, out var text));
            Assert.AreEqual("Ja, mach weiter.\nDanke", text, "exactly what was typed");
            Assert.AreEqual(ReplyPhase.Sending, r.Phase);
            Assert.AreEqual(SendResult.NotOpen, new ReplyLogic().Send(0, out _));
        }

        [Test]
        public void DeliveredClearsTheDraftAndClosesAfterAMoment()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Weiter so");
            r.Send(0, out _);
            Assert.IsFalse(r.OnAck("a2", true, 50), "another agent's ack");
            Assert.AreEqual(ReplyPhase.Sending, r.Phase);
            Assert.IsTrue(r.OnAck("a1", true, 100));
            Assert.AreEqual(ReplyPhase.Delivered, r.Phase);
            Assert.AreEqual(ReplyStep.None, r.Step(100 + ReplyLogic.DeliveredShowMs - 200, false));
            Assert.AreEqual(ReplyStep.ClosedDelivered, r.Step(100 + ReplyLogic.DeliveredShowMs, false));
            Assert.IsNull(r.OpenSid);
            r.Open("a1", false, ReplyAvailability.Available);
            Assert.AreEqual("", r.Draft, "delivered: the draft is gone");
        }

        [Test]
        public void NotDeliveredKeepsTheDraftToTryAgain()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Weiter so");
            r.Send(0, out _);
            r.OnAck("a1", false, 100);
            Assert.AreEqual(ReplyPhase.NotDelivered, r.Phase);
            Assert.AreEqual("Weiter so", r.Draft);
            Assert.AreEqual("a1", r.OpenSid, "stays open");
        }

        [Test]
        public void NoAckWithinFiveSecondsMeansNoAnswer()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Hallo");
            r.Send(1000, out _);
            Assert.AreEqual(ReplyStep.None, r.Step(1000 + ReplyLogic.AckTimeoutMs - 1, false));
            Assert.AreEqual(ReplyPhase.Sending, r.Phase);
            r.Step(1000 + ReplyLogic.AckTimeoutMs, false);
            Assert.AreEqual(ReplyPhase.NoAnswer, r.Phase);
            Assert.AreEqual("Hallo", r.Draft, "kept");
            Assert.IsFalse(r.OnAck("a1", true, 7000), "a late ack changes nothing");
        }

        [Test]
        public void AtMostOneReplyPerAgentEveryFiveAndAHalfSeconds()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Hallo");
            Assert.AreEqual(SendResult.Sent, r.Send(0, out _));
            r.OnAck("a1", false, 100);
            Assert.AreEqual(5500.0, ReplyLogic.SendEveryMs, "a little over the bridge's 5 s");
            Assert.AreEqual(SendResult.TooSoon, r.Send(5499, out _));
            Assert.AreEqual(ReplyPhase.NotDelivered, r.Phase, "nothing went out");
            Assert.AreEqual(SendResult.Sent, r.Send(5500, out _));
            Assert.AreEqual(SendResult.Busy, r.Send(20000, out _), "not while one is on its way");
        }

        [Test]
        public void TypingAfterAResultGoesBackToEditing()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Hallo");
            r.Send(0, out _);
            r.OnAck("a1", false, 100);
            r.SetText("Hallo nochmal");
            Assert.AreEqual(ReplyPhase.Editing, r.Phase);
            r.SetText("ignored while sending?");
            r.Send(6000, out _);
            r.SetText("changed during sending");
            Assert.AreEqual(ReplyPhase.Sending, r.Phase, "the text on its way is not changed");
            Assert.AreEqual("ignored while sending?", r.Draft);
        }

        [Test]
        public void TheEmptyCheckFollowsTheBridgesCleanRule()
        {
            // bridge/src/replies.ts cleanReply: CR to LF, other controls removed, trimmed
            Assert.IsTrue(ReplyLogic.IsBlank("\u0001\u0002 \t\r\n"));
            Assert.IsTrue(ReplyLogic.IsBlank("\uFEFF \u00A0"));
            Assert.IsTrue(ReplyLogic.IsBlank("\u0085\u009F"));
            Assert.IsFalse(ReplyLogic.IsBlank("a\u0001"));
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("\u0007\u001B ");
            Assert.AreEqual(SendResult.Empty, r.Send(0, out _), "the bridge would refuse it as empty");
        }

        [Test]
        public void ASendThatCouldNotGoOutCanBeRetriedAtOnce()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Hallo");
            Assert.AreEqual(SendResult.Sent, r.Send(0, out _));
            r.SendFailed();
            Assert.AreEqual(SendResult.Sent, r.Send(100, out _), "nothing went out: the rate limit was not used up");
        }

        [Test]
        public void ABridgeRefusalSaysWhyAndKeepsTheDraft()
        {
            var r = new ReplyLogic();
            r.Open("a1", false, ReplyAvailability.Available);
            r.SetText("Hallo");
            r.Send(0, out _);
            Assert.IsFalse(r.OnFailed("a2", "rate-limited"), "another agent's");
            Assert.IsTrue(r.OnFailed("a1", "rate-limited"));
            Assert.AreEqual(ReplyPhase.Refused, r.Phase);
            Assert.AreEqual("rate-limited", r.RefusedError);
            Assert.AreEqual("Hallo", r.Draft);
            r.Send(6000, out _);
            r.OnFailed("a1", "bad-message");
            Assert.AreEqual("bad-message", r.RefusedError);
            Assert.AreEqual(Texts.ReplyTooSoon, Texts.ReplyRefused("rate-limited"));
            Assert.AreEqual(Texts.ReplyNotAccepted, Texts.ReplyRefused("bad-message"));
            Assert.AreEqual(Texts.ReplyNotAccepted, Texts.ReplyRefused("anything"));
            Assert.AreEqual("Message not accepted", Texts.ReplyNotAccepted);
        }

        [Test]
        public void TheDraftsTailNeverSplitsAMarker()
        {
            var text = "Anfang " + new string('x', 20) + "‹U+202E›" + "Ende";
            for (var max = 6; max < 30; max++)
            {
                var tail = Untrusted.Tail(text, max);
                Assert.LessOrEqual(tail.Length, max);
                StringAssert.StartsWith("…", tail);
                var open = tail.IndexOf('‹');
                var close = tail.IndexOf('›');
                Assert.IsFalse(close >= 0 && (open < 0 || open > close), $"max {max}: '{tail}' starts inside a marker");
            }
            Assert.AreEqual("kurz", Untrusted.Tail("kurz", 10));
        }

        // ---- the session and the sources ----

        const string Pack = "{\"version\":1,\"budgetUsd\":15,\"durationMs\":1000,\"messages\":[" +
                            "{\"t\":0,\"msg\":{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}}," +
                            "{\"t\":0,\"msg\":{\"type\":\"agent\",\"agent\":{\"sid\":\"demo-a1\",\"name\":\"Agent 1\",\"source\":\"claude\",\"status\":\"done\",\"lastAction\":\"Fertig\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}}," +
                            "{\"t\":0,\"msg\":{\"type\":\"agent\",\"agent\":{\"sid\":\"demo-a2\",\"name\":\"Agent 2\",\"source\":\"claude\",\"status\":\"done\",\"lastAction\":\"Fertig\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}}]";

        [Test]
        public void OfflineThePackAcknowledgesAndTheAgentReacts()
        {
            var s = new DemoSession(DemoPack.Parse(Pack + ",\"replies\":{\"demo-a1\":\"Alles klar, ich passe das an.\"}}"));
            s.Step(16, default, default, HoldPose.Open);
            var acks = new List<(string, bool)>();
            s.ReplyAcked += (sid, delivered) => acks.Add((sid, delivered));
            var updates = new List<AgentState>();
            s.AgentUpdated += (a, prev) => updates.Add(a);
            Assert.IsTrue(s.SendReply("demo-a1", "Bitte noch die Tests"));
            CollectionAssert.AreEqual(new[] { ("demo-a1", true) }, acks);
            var a1 = s.Store.Agents.Single(a => a.Sid == "demo-a1");
            Assert.AreEqual("working", a1.Status);
            Assert.AreEqual("Alles klar, ich passe das an.", a1.LastAction);
            Assert.AreEqual(1, updates.Count);

            Assert.IsTrue(s.SendReply("demo-a2", "Und du?"));
            Assert.AreEqual(Texts.ReplyFallbackReaction, s.Store.Agents.Single(a => a.Sid == "demo-a2").LastAction,
                "without a canned reaction: the fallback");
            Assert.AreEqual(0, s.Decisions.Count, "a reply decides nothing");
        }

        [Test]
        public void AReplyIsRefusedWhileItsAgentHasAnOpenRequestAndNeverTouchesIt()
        {
            var s = new DemoSession(DemoPack.Parse(File()));
            s.SetViewShown(true);
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100000) s.Step(100, default, default, HoldPose.Open);
            Assert.IsFalse(s.SendReply("demo-a1", "Hallo"), "never to an agent with an open request, shown or not");
            s.Select("demo-a1");
            Assert.IsNotNull(s.Card);
            var card = s.Card;
            var mode = s.Mode;
            Assert.IsFalse(s.SendReply("demo-a1", "Hallo"), "never while its block is open");
            Assert.AreSame(card, s.Card);
            Assert.AreEqual(mode, s.Mode);
            Assert.AreEqual(0, s.Decisions.Count);
        }

        static string File() => System.IO.File.ReadAllText(System.IO.Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets", "demo", "jury.pack.json"));

        static (LiveSource live, FakeSocket socket) PairedLive()
        {
            var net = new FakeNet();
            var live = new LiveSource(new HostBook(new MemoryStore()), net.Open);
            Assert.IsTrue(live.PairWithCode(Bridge.Host, "123456"));
            var s = net.Last;
            s.Open();
            s.Receive(Bridge.EmptySnapshot);
            s.Receive(Bridge.Paired("tok"));
            live.Poll(0);
            return (live, s);
        }

        [Test]
        public void LiveTheReplyGoesToTheBridgeAndTheAckComesBack()
        {
            var (live, s) = PairedLive();
            var session = new DemoSession(live);
            session.Step(16, default, default, HoldPose.Open);
            var acks = new List<(string, bool)>();
            session.ReplyAcked += (sid, d) => acks.Add((sid, d));
            Assert.IsTrue(session.SendReply("s1", "Bitte weiter"));
            Assert.AreEqual("{\"type\":\"agent_reply\",\"sid\":\"s1\",\"text\":\"Bitte weiter\"}", s.Sent.Last());
            CollectionAssert.IsEmpty(acks, "live, the ack comes later");
            s.Receive("{\"type\":\"agent_reply_ack\",\"sid\":\"s1\",\"delivered\":false}");
            session.Step(16, default, default, HoldPose.Open);
            CollectionAssert.AreEqual(new[] { ("s1", false) }, acks);
        }

        [Test]
        public void NotLiveNothingIsSent()
        {
            var live = new LiveSource(new HostBook(new MemoryStore()), new FakeNet().Open);
            Assert.IsNull(live.SendReply("s1", "x"));
        }

        [Test]
        public void ABadMessageAfterAReplyIsTheRepliesRefusal()
        {
            var (live, s) = PairedLive();
            Assert.IsNotNull(live.SendReply("s1", "Hallo"));
            s.Receive(Bridge.Error("bad-message"));
            var got = live.Poll(16);
            Assert.AreEqual(1, got.Count);
            var failed = (AgentReplyFailedMessage)got[0];
            Assert.AreEqual("s1", failed.Sid, "no refusal banner: the reply panel says \"Message not accepted\"");
            Assert.AreEqual("bad-message", failed.Error);
            Assert.AreEqual(LinkState.Live, live.State);
            Assert.IsNotNull(live.SendReply("s1", "Hallo"), "replies are still sent");
            live.Poll(LiveSource.DetailAnswerTimeoutMs);
            s.Receive(Bridge.Error("bad-message"));
            CollectionAssert.IsEmpty(live.Poll(16), "the given-up reply's late answer: dropped");
            live.Poll(LiveSource.DetailAnswerTimeoutMs);
            s.Receive(Bridge.Error("bad-message"));
            Assert.IsInstanceOf<ErrorMessage>(live.Poll(16)[0], "long after: a real error");
        }
    }
}
