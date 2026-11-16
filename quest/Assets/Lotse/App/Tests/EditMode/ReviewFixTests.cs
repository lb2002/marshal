#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// Fixes from the U1+U2 review (task-u2-review-1.md), pure parts: the bridge's detail errors never read as a
    /// decision refusal (I1), the wrist menu's palm ignores the system gesture (I2), a sphere poke needs an approach
    /// from above or the front and a dwell (M8), sids are capped in logs and the detail cache is bounded (M9).
    /// </summary>
    public class ReviewFixTests
    {
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

        // ---- I1: detail errors ----

        [TestCase("unknown-agent")]
        [TestCase("rate-limited")]
        public void ADetailErrorIsTheDetailsAnswerNotARefusal(string code)
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out _));
            s.Receive(Bridge.Error(code));
            var got = live.Poll(16);
            Assert.AreEqual(1, got.Count);
            Assert.IsInstanceOf<AgentDetailFailedMessage>(got[0], "no ErrorMessage: no banner, no decision path");
            Assert.AreEqual("a1", ((AgentDetailFailedMessage)got[0]).Sid);
            Assert.IsTrue(live.RequestDetail("a1", out _), "the bridge knows the request: details stay on");
        }

        [Test]
        public void AnErrorAnswersTheOldestOutstandingDetailRequest()
        {
            var (live, s) = PairedLive();
            live.RequestDetail("a1", out _);
            live.RequestDetail("a2", out _);
            s.Receive(Bridge.Error("unknown-agent"));
            s.Receive("{\"type\":\"agent_detail\",\"sid\":\"a2\",\"title\":\"t\",\"summary\":\"s\",\"recent\":[],\"waitingFor\":null,\"source\":\"rules\"}");
            var got = live.Poll(16);
            Assert.AreEqual("a1", ((AgentDetailFailedMessage)got[0]).Sid);
            Assert.IsInstanceOf<AgentDetailMessage>(got[1]);
            s.Receive(Bridge.Error("unknown-agent"));
            Assert.IsInstanceOf<ErrorMessage>(live.Poll(16)[0], "nothing outstanding: a real error");
        }

        const string PermOpen = "{\"type\":\"perm_open\",\"request\":{\"id\":\"p1\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"ls\",\"truncated\":false," +
                                "\"paths\":[],\"risk\":{\"risk\":\"safe\",\"reason\":\"t\"},\"createdAt\":1,\"expiresAt\":90001}," +
                                "\"explain\":{\"text\":\"Listet.\",\"replies\":[\"Nein.\"],\"source\":\"rules\"}}";

        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);

        [TestCase("unknown-agent")]
        [TestCase("rate-limited")]
        public void ADetailErrorWhileADecisionIsInFlightLeavesTheSentCardAlone(string code)
        {
            var (live, s) = PairedLive();
            var session = new DemoSession(live);
            session.SetViewShown(true);
            session.Select("s1"); // the agent whose request comes
            var refusals = new List<string>();
            var failed = new List<string>();
            session.BridgeError += refusals.Add;
            session.AgentDetailFailed += failed.Add;
            session.Step(16, Open, Open, HoldPose.Open);
            Assert.IsTrue(session.RequestDetail("a1"));

            // a card opens and is approved before the detail's answer arrives
            s.Receive(PermOpen);
            session.Step(16, Open, Open, HoldPose.Open);
            Assert.AreEqual("p1", session.Card?.Id);
            session.Step(16, Open, Open, HoldPose.Open); // the hand seen open on the card: the next pinch is a fresh one
            for (var i = 0; i < 60 && session.Mode != CardMode.Sent; i++) session.Step(20, Open, Pinch, HoldPose.Open);
            Assert.AreEqual(CardMode.Sent, session.Mode, "the decision is on its way");

            s.Receive(Bridge.Error(code));
            session.Step(16, Open, Pinch, HoldPose.Open);
            Assert.AreEqual(CardMode.Sent, session.Mode, "the detail's error is not the decision's refusal");
            CollectionAssert.IsEmpty(refusals, "no refusal banner");
            CollectionAssert.AreEqual(new[] { "a1" }, failed);

            // a genuine refusal of the decision still reaches the decision path
            s.Receive(Bridge.Error("needs-two-hand"));
            session.Step(16, Open, Open, HoldPose.Open);
            CollectionAssert.AreEqual(new[] { "needs-two-hand" }, refusals);
            Assert.AreEqual(CardMode.Hold, session.Mode, "back to Hold for a fresh hold");
        }

        // ---- U3 review: reply errors and correlation by sid ----

        [Test]
        public void ADetailAnswerClearsOnlyItsOwnRequest()
        {
            var (live, s) = PairedLive();
            live.RequestDetail("a1", out _);
            live.RequestDetail("a2", out _);
            s.Receive("{\"type\":\"agent_detail\",\"sid\":\"a2\",\"title\":\"t\",\"summary\":\"s\",\"recent\":[],\"waitingFor\":null,\"source\":\"rules\"}");
            s.Receive(Bridge.Error("unknown-agent"));
            var got = live.Poll(16);
            Assert.IsInstanceOf<AgentDetailMessage>(got[0]);
            Assert.AreEqual("a1", ((AgentDetailFailedMessage)got[1]).Sid, "a1 was still outstanding");
        }

        [Test]
        public void AReplyAndADetailOutstandingTogetherGetTheirOwnErrors()
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out _));
            Assert.IsNotNull(live.SendReply("s1", "Hallo"));
            s.Receive(Bridge.Error("unknown-agent"));
            s.Receive(Bridge.Error("rate-limited"));
            var got = live.Poll(16);
            Assert.AreEqual(2, got.Count);
            Assert.AreEqual("a1", ((AgentDetailFailedMessage)got[0]).Sid, "the older request first");
            var reply = (AgentReplyFailedMessage)got[1];
            Assert.AreEqual("s1", reply.Sid);
            Assert.AreEqual("rate-limited", reply.Error);
        }

        [Test]
        public void AReplyErrorWhileADecisionIsInFlightLeavesTheSentCardAlone()
        {
            var (live, s) = PairedLive();
            var session = new DemoSession(live);
            session.SetViewShown(true);
            session.Select("s1"); // the agent whose request comes
            var refusals = new List<string>();
            var replyErrors = new List<string>();
            session.BridgeError += refusals.Add;
            session.ReplyFailed += (sid, error) => replyErrors.Add(sid + ":" + error);
            session.Step(16, Open, Open, HoldPose.Open);
            Assert.IsTrue(session.SendReply("s1", "Hallo"));
            s.Receive(PermOpen);
            session.Step(16, Open, Open, HoldPose.Open);
            session.Step(16, Open, Open, HoldPose.Open);
            for (var i = 0; i < 60 && session.Mode != CardMode.Sent; i++) session.Step(20, Open, Pinch, HoldPose.Open);
            Assert.AreEqual(CardMode.Sent, session.Mode);
            s.Receive(Bridge.Error("bad-message"));
            session.Step(16, Open, Pinch, HoldPose.Open);
            Assert.AreEqual(CardMode.Sent, session.Mode, "the reply's error is not the decision's refusal");
            CollectionAssert.IsEmpty(refusals);
            CollectionAssert.AreEqual(new[] { "s1:bad-message" }, replyErrors);
        }

        [Test]
        public void AnErrorJustAfterARequestExpiredIsDroppedSilently()
        {
            var (live, s) = PairedLive();
            live.RequestDetail("a1", out _);
            live.Poll(LiveSource.DetailAnswerTimeoutMs); // given up
            s.Receive(Bridge.Error("rate-limited"));
            CollectionAssert.IsEmpty(live.Poll(16), "a late answer to the given-up request: no banner");
            live.Poll(LiveSource.DetailAnswerTimeoutMs);
            s.Receive(Bridge.Error("rate-limited"));
            Assert.IsInstanceOf<ErrorMessage>(live.Poll(16)[0], "long after: a real error");
        }

        // ---- I2: the wrist menu's palm ----

        [Test]
        public void ThePalmForTheWristMenuIgnoresTheSystemGesture()
        {
            Assert.IsTrue(HandMapping.PalmUsable(isTracked: true, highConfidence: true, bonesReady: true),
                "a palm turned up toward the face starts the system gesture: the menu must still see it");
            Assert.IsFalse(HandMapping.PalmUsable(isTracked: false, highConfidence: true, bonesReady: true));
            Assert.IsFalse(HandMapping.PalmUsable(isTracked: true, highConfidence: false, bonesReady: true));
            Assert.IsFalse(HandMapping.PalmUsable(isTracked: true, highConfidence: true, bonesReady: false));
            // the system gesture itself is still never an approval
            var s = HandMapping.ToSample(true, systemGesture: true, highConfidence: true, indexPinch: 1f, palmForward: true);
            Assert.IsFalse(s.Usable);
            Assert.AreEqual(0f, s.PinchStrength);
        }

        // ---- M9: logs and bounds ----

        [Test]
        public void SidsInLogsAreCappedAndPlain()
        {
            Assert.AreEqual("demo-a1", Texts.LogSid("demo-a1"));
            Assert.AreEqual(12, Texts.LogSid(new string('s', 64)).Length);
            Assert.AreEqual(-1, Texts.LogSid("ab\ncd\u202Eef").IndexOf('\n'));
            Assert.AreEqual(-1, Texts.LogSid("ab\u202Eef").IndexOf('\u202E'));
            Assert.AreEqual("-", Texts.LogSid(null));
        }

        [Test]
        public void TheDetailCacheKeepsAtMostThirtyTwoAgents()
        {
            var c = new AgentDetails();
            for (var i = 0; i < 40; i++)
                c.Put(new AgentDetailMessage { Sid = "a" + i, Title = "t", Summary = "s" });
            Assert.AreEqual(AgentDetails.MaxAgents, c.Count);
            Assert.AreEqual(32, AgentDetails.MaxAgents);
            Assert.IsNull(c.Get("a0"), "the oldest went first");
            Assert.IsNotNull(c.Get("a39"));
        }
    }
}
