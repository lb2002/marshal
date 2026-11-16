#nullable enable
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class LiveSourceTests
    {
        const string Host = Bridge.Host;
        const string SessionKey = HostBook.SessionPrefix + Host;

        MemoryStore _store = null!;
        FakeNet _net = null!;
        LiveSource _live = null!;

        [SetUp]
        public void SetUp()
        {
            _store = new MemoryStore();
            _net = new FakeNet();
            _live = new LiveSource(new HostBook(_store), _net.Open);
        }

        /// <summary>Pairs with a code: open, hello, snapshot, paired. Returns the socket.</summary>
        FakeSocket PairLive(string session = "tok-1")
        {
            Assert.IsTrue(_live.PairWithCode(Host, "123456"));
            var s = _net.Last;
            s.Open();
            s.Receive(Bridge.EmptySnapshot);
            s.Receive(Bridge.Paired(session));
            _live.Poll(0);
            Assert.AreEqual(LinkState.Live, _live.State);
            return s;
        }

        [Test]
        public void PairingSendsTheCodeAndStoresTheSessionByHost()
        {
            Assert.IsTrue(_live.PairWithCode(Host, "123456"));
            Assert.AreEqual(1, _net.Sockets.Count);
            var s = _net.Last;
            Assert.AreEqual("ws://192.168.1.20:47821", s.Url);
            Assert.IsTrue(s.ConnectCalled);
            Assert.AreEqual(LinkState.Connecting, _live.State);
            Assert.AreEqual(Texts.Connecting, _live.Status);
            CollectionAssert.IsEmpty(s.Sent, "nothing before the socket is open");

            s.Open();
            CollectionAssert.AreEqual(new[] { "{\"type\":\"hello\",\"code\":\"123456\"}" }, s.Sent);

            s.Receive(Bridge.EmptySnapshot);
            var got = _live.Poll(16);
            Assert.AreEqual(1, got.Count);
            Assert.IsInstanceOf<SnapshotMessage>(got[0]);
            Assert.AreEqual(LinkState.Live, _live.State);
            Assert.AreEqual(Texts.Paired, _live.Status);

            s.Receive(Bridge.Paired("tok-1"));
            CollectionAssert.IsEmpty(_live.Poll(16), "paired is a connection matter, not store state");
            Assert.AreEqual("tok-1", _store.Get(SessionKey));
            CollectionAssert.AreEqual(new[] { Host }, new HostBook(_store).Recent);
        }

        [Test]
        public void ResumeSendsTheStoredSessionFirst()
        {
            _store.Set(SessionKey, "tok-9");
            Assert.IsTrue(_live.Resume(Host));
            var s = _net.Last;
            s.Open();
            CollectionAssert.AreEqual(new[] { "{\"type\":\"hello\",\"session\":\"tok-9\"}" }, s.Sent);
            s.Receive(Bridge.EmptySnapshot);
            _live.Poll(16);
            Assert.AreEqual(LinkState.Live, _live.State);
            Assert.AreEqual("tok-9", _store.Get(SessionKey), "a resume keeps the token");
        }

        [Test]
        public void ResumeWithoutAStoredSessionDoesNothing()
        {
            Assert.IsFalse(_live.Resume(Host));
            CollectionAssert.IsEmpty(_net.Sockets);
            Assert.AreEqual(LinkState.Idle, _live.State);
        }

        [TestCase("192.168.1.20", "12345")]
        [TestCase("192.168.1.20", "1234567")]
        [TestCase("192.168.1.20", "12a456")]
        [TestCase("192.168.1.20", "")]
        [TestCase("256.1.1.1", "123456")]
        [TestCase("192.168.1", "123456")]
        [TestCase("", "123456")]
        public void ABadCodeOrAddressIsRefusedBeforeConnecting(string host, string code)
        {
            Assert.IsFalse(_live.PairWithCode(host, code));
            CollectionAssert.IsEmpty(_net.Sockets);
        }

        [TestCase(1004)] // NativeWebSocket's name for the bridge's 4001 "bad-credentials"
        [TestCase(4001)]
        public void ARejectedSessionAsksForANewCodeAndKeepsTheToken(int closeCode)
        {
            _store.Set(SessionKey, "stale");
            _live.Resume(Host);
            var s = _net.Last;
            s.Open();
            s.Drop(closeCode);
            _live.Poll(16);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual(Texts.SessionExpired, _live.Status);
            Assert.AreEqual("stale", _store.Get(SessionKey), "1004 is ambiguous: the token stays until a new pairing replaces it");
            _live.Poll(60000);
            Assert.AreEqual(1, _net.Sockets.Count, "no automatic retry: only a new code helps");
        }

        [Test]
        public void AnErrorReplyToTheSessionHelloAsksForANewCode()
        {
            _store.Set(SessionKey, "stale");
            _live.Resume(Host);
            var s = _net.Last;
            s.Open();
            s.Receive(Bridge.Error("bad-message"));
            _live.Poll(16);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual("stale", _store.Get(SessionKey));
            Assert.IsTrue(s.ClosedByClient);
            _live.Poll(60000);
            Assert.AreEqual(1, _net.Sockets.Count);
        }

        [Test]
        public void AWrongCodeAsksForANewCodeAndIsNotRetried()
        {
            _live.PairWithCode(Host, "000000");
            var s = _net.Last;
            s.Open();
            s.Drop(1004);
            _live.Poll(16);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual(Texts.CodeRejected, _live.Status);
            _live.Poll(60000);
            Assert.AreEqual(1, _net.Sockets.Count);
        }

        [Test]
        public void AnUnreachableBridgeIsRetriedWithBackoffUpToTenSeconds()
        {
            // a bridge the headset is paired with (U11 fix round 1: only a stored session is retried without limit)
            _store.Set(SessionKey, "tok");
            _live.Resume(Host);
            var expected = new[] { 1000, 2000, 4000, 8000, 10000, 10000 };
            foreach (var delay in expected)
            {
                var count = _net.Sockets.Count;
                _net.Last.Drop(1006); // never opened
                _live.Poll(0);
                Assert.AreEqual(LinkState.Retrying, _live.State);
                StringAssert.StartsWith("Bridge not reachable", _live.Status);
                _live.Poll(delay - 1);
                Assert.AreEqual(count, _net.Sockets.Count, $"no attempt before {delay} ms");
                _live.Poll(1);
                Assert.AreEqual(count + 1, _net.Sockets.Count, $"next attempt after {delay} ms");
                Assert.AreEqual(LinkState.Connecting, _live.State);
            }
            _net.Last.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"session\":\"tok\"}", _net.Last.Sent.Single(), "the session is tried again");
        }

        [Test]
        public void AResumeAskedForOnceGetsOneAttemptOfThreeSecondsAndIsThenDropped()
        {
            _store.Set(SessionKey, "tok");
            // nobody answers at the stored address: the socket closes
            Assert.IsTrue(_live.Resume(Host, once: true));
            _net.Last.Drop(1006);
            _live.Poll(0);
            Assert.AreEqual(LinkState.NeedsCode, _live.State, "one attempt, no retry");
            Assert.AreEqual("", _live.Status, "nothing to say about an address the user does not see");
            _live.Poll(600000);
            Assert.AreEqual(1, _net.Sockets.Count, "no further attempt to the stored address");
            Assert.AreEqual("tok", _store.Get(SessionKey), "the session stays for another day");
            Assert.IsTrue(_live.Resume(Host), "and is not marked as refused");
            _live.Disconnect();
            // or nothing happens at all: three seconds, then dropped
            var n = _net.Sockets.Count;
            Assert.IsTrue(_live.Resume(Host, once: true));
            _live.Poll(LiveSource.ResumeOnceMs - 1);
            Assert.AreEqual(LinkState.Connecting, _live.State);
            _live.Poll(1);
            Assert.AreEqual(LinkState.NeedsCode, _live.State, "three seconds are up");
            Assert.IsTrue(_net.Last.ClosedByClient);
            _live.Poll(600000);
            Assert.AreEqual(n + 1, _net.Sockets.Count);
            // an open socket that sends no snapshot within the three seconds: the same
            Assert.IsTrue(_live.Resume(Host, once: true));
            _net.Last.Open();
            _live.Poll(LiveSource.ResumeOnceMs);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            _live.Poll(600000);
            Assert.AreEqual(n + 2, _net.Sockets.Count);
        }

        [Test]
        public void AResumeAskedForOnceThatPairsReconnectsWithoutLimitWhenItDropsLater()
        {
            _store.Set(SessionKey, "tok");
            Assert.IsTrue(_live.Resume(Host, once: true));
            _net.Last.Open();
            _net.Last.Receive(Bridge.EmptySnapshot);
            _live.Poll(0);
            Assert.AreEqual(LinkState.Live, _live.State);
            // the bridge restarts later: the live session is reconnected as always, attempt after attempt
            _net.Last.Drop(1006);
            _live.Poll(0);
            Assert.AreEqual(LinkState.Retrying, _live.State);
            for (var i = 0; i < 8; i++)
            {
                var count = _net.Sockets.Count;
                _live.Poll(LiveSource.MaxBackoffMs);
                Assert.AreEqual(count + 1, _net.Sockets.Count, "attempt " + (i + 1));
                _live.Poll(LiveSource.ResumeOnceMs + 1);
                Assert.AreEqual(LinkState.Connecting, _live.State, "the usual eight seconds apply again, not three");
                _net.Last.Drop(1006);
                _live.Poll(0);
                Assert.AreEqual(LinkState.Retrying, _live.State);
            }
        }

        [Test]
        public void ATypedCodeIsTriedThreeTimesAtAnUnreachableAddressAndThenGivenUp()
        {
            _live.PairWithCode(Host, "123456");
            _net.Last.Drop(1006); // never opened
            _live.Poll(0);
            Assert.AreEqual(LinkState.Retrying, _live.State);
            _live.Poll(1000);
            Assert.AreEqual(2, _net.Sockets.Count, "a second attempt");
            _net.Last.Drop(1006);
            _live.Poll(0);
            _live.Poll(2000);
            Assert.AreEqual(3, _net.Sockets.Count, "a third");
            _net.Last.Drop(1006);
            _live.Poll(0);
            Assert.AreEqual(LinkState.NeedsCode, _live.State, "given up: the code is not kept for an address that does not answer");
            Assert.AreEqual(Texts.UnreachableGaveUp, _live.Status);
            _live.Poll(600000);
            Assert.AreEqual(3, _net.Sockets.Count, "nothing connects later");
            // the handshake timing out counts the same
            _live.PairWithCode(Host, "654321");
            for (var i = 0; i < 3; i++)
            {
                _live.Poll(LiveSource.HandshakeTimeoutMs);
                _live.Poll(LiveSource.MaxBackoffMs);
            }
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            var n = _net.Sockets.Count;
            _live.Poll(600000);
            Assert.AreEqual(n, _net.Sockets.Count);
            Assert.AreEqual(6, n, "three attempts each");
        }

        [Test]
        public void AScannedCodeWhoseSocketClosesInsideConnectIsStillOneAttempt()
        {
            _live.PairWithCode(Host, "123456");
            _net.Last.Open();
            _net.Last.Drop(4001);
            _live.Poll(0);
            var n = _net.Sockets.Count;
            _net.ClosesInConnect = 1006; // refused at once, before PairWithCode returns
            _live.PairWithCode("192.168.1.197", "481216", single: true);
            Assert.AreEqual(LinkState.NeedsCode, _live.State, "one attempt, not a retry");
            Assert.AreEqual(Host, _live.Host, "the address is what it was before");
            _live.Poll(600000);
            Assert.AreEqual(n + 1, _net.Sockets.Count, "no second attempt");
        }

        [Test]
        public void AScannedCodeGetsExactlyOneConnectionAttemptAndLeavesTheAddressAsItWas()
        {
            _live.PairWithCode(Host, "123456"); // an earlier, typed attempt: its address is the current one
            _net.Last.Open();
            _net.Last.Drop(4001);
            _live.Poll(0);
            Assert.AreEqual(Host, _live.Host);
            const string other = "192.168.1.197";
            Assert.IsTrue(_live.PairWithCode(other, "481216", single: true));
            Assert.AreEqual(other, _live.Host);
            var n = _net.Sockets.Count;
            _net.Last.Drop(1006); // nobody listens there
            _live.Poll(0);
            Assert.AreEqual(LinkState.NeedsCode, _live.State, "one attempt, no retry");
            Assert.AreEqual(Host, _live.Host, "the address is what it was before");
            _live.Poll(600000);
            Assert.AreEqual(n, _net.Sockets.Count, "it never connects later, also when the host starts answering");
            CollectionAssert.DoesNotContain(new HostBook(_store).Recent, other);
            // refused by the bridge, or timed out: the same
            Assert.IsTrue(_live.PairWithCode(other, "481216", single: true));
            _net.Last.Open();
            _net.Last.Drop(4001);
            _live.Poll(0);
            Assert.AreEqual(Host, _live.Host);
            Assert.IsTrue(_live.PairWithCode(other, "481216", single: true));
            _live.Poll(LiveSource.HandshakeTimeoutMs);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual(Host, _live.Host);
            _live.Poll(600000);
            Assert.AreEqual(n + 2, _net.Sockets.Count);
            CollectionAssert.DoesNotContain(new HostBook(_store).Recent, other);
            // and when it pairs, the address is the scanned one and it is remembered
            Assert.IsTrue(_live.PairWithCode(other, "481216", single: true));
            _net.Last.Open();
            _net.Last.Receive(Bridge.EmptySnapshot);
            _live.Poll(0);
            Assert.AreEqual(LinkState.Live, _live.State);
            Assert.AreEqual(other, _live.Host);
            Assert.AreEqual(other, new HostBook(_store).Recent[0]);
        }

        [Test]
        public void ALostConnectionIsReportedInOrderAndResumedWithTheSession()
        {
            var s = PairLive();
            s.Receive(Bridge.Agent("s1", "working"));
            s.Drop(1006);
            var got = _live.Poll(0); // the backoff counts from the drop
            Assert.AreEqual(2, got.Count);
            Assert.IsInstanceOf<AgentMessage>(got[0], "what arrived before the drop comes first");
            Assert.IsInstanceOf<ConnectionLostMessage>(got[1]);
            Assert.AreEqual(LinkState.Retrying, _live.State);
            Assert.AreEqual(Texts.ConnectionLost, _live.Status);

            _live.Poll(999);
            Assert.AreEqual(1, _net.Sockets.Count);
            _live.Poll(1);
            Assert.AreEqual(2, _net.Sockets.Count, "reconnect after 1 s");
            _net.Last.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"session\":\"tok-1\"}", _net.Last.Sent.Single(), "hello {session} first");
            _net.Last.Receive(Bridge.EmptySnapshot);
            _live.Poll(16);
            Assert.AreEqual(LinkState.Live, _live.State);
        }

        [Test]
        public void ASuccessfulConnectionResetsTheBackoff()
        {
            _live.PairWithCode(Host, "123456");
            _net.Last.Drop(1006);
            _live.Poll(1000);
            _net.Last.Drop(1006);
            _live.Poll(2000);
            _net.Last.Open();
            _net.Last.Receive(Bridge.EmptySnapshot);
            _net.Last.Receive(Bridge.Paired("tok-2"));
            _live.Poll(0);
            Assert.AreEqual(LinkState.Live, _live.State);

            _net.Last.Drop(1006);
            _live.Poll(0);
            var count = _net.Sockets.Count;
            _live.Poll(1000);
            Assert.AreEqual(count + 1, _net.Sockets.Count, "back to 1 s after a good connection");
        }

        [Test]
        public void AHandshakeThatHangsIsAbandonedAndLateEventsOfTheOldSocketAreIgnored()
        {
            _live.PairWithCode(Host, "123456");
            var old = _net.Last;
            _live.Poll(LiveSource.HandshakeTimeoutMs - 1);
            Assert.AreEqual(LinkState.Connecting, _live.State);
            _live.Poll(1);
            Assert.AreEqual(LinkState.Retrying, _live.State);
            Assert.IsTrue(old.ClosedByClient);

            old.Open();
            old.Receive(Bridge.EmptySnapshot);
            old.Drop(1006);
            CollectionAssert.IsEmpty(old.Sent, "no hello on a socket we gave up");
            CollectionAssert.IsEmpty(_live.Poll(0));
            Assert.AreEqual(LinkState.Retrying, _live.State);
        }

        [Test]
        public void DecisionsAreSentOnlyWhileLiveInTheBridgeShape()
        {
            Assert.IsNull(_live.Send(ClientDecision.Allow("r1", "pinch-hold")), "not connected");
            _live.PairWithCode(Host, "123456");
            _net.Last.Open();
            Assert.IsNull(_live.Send(ClientDecision.Deny("r1")), "not paired yet");
            Assert.AreEqual(1, _net.Last.Sent.Count, "only the hello");

            _net.Last.Receive(Bridge.EmptySnapshot);
            _live.Poll(0);
            var answer = _live.Send(ClientDecision.Allow("r1", "two-hand"));
            Assert.IsNotNull(answer);
            CollectionAssert.IsEmpty(answer, "the bridge's perm_closed comes later");
            Assert.AreEqual("{\"type\":\"perm_decision\",\"decision\":{\"id\":\"r1\",\"behavior\":\"allow\",\"confirm\":\"two-hand\"}}",
                _net.Last.Sent.Last());
        }

        [Test]
        public void ErrorsWhileLiveArePassedOn()
        {
            var s = PairLive();
            s.Receive(Bridge.Error("needs-two-hand"));
            var got = _live.Poll(0);
            Assert.AreEqual("needs-two-hand", ((ErrorMessage)got.Single()).Error);
            Assert.AreEqual(LinkState.Live, _live.State);
        }

        [Test]
        public void DisconnectClosesWithoutRetryOrLostMessage()
        {
            var s = PairLive();
            _live.Disconnect();
            Assert.IsTrue(s.ClosedByClient);
            s.Drop(1000);
            CollectionAssert.IsEmpty(_live.Poll(60000));
            Assert.AreEqual(LinkState.Idle, _live.State);
            Assert.AreEqual(1, _net.Sockets.Count);
        }

        [Test]
        public void ADropBeforeTheSessionTokenArrivedAsksForANewCode()
        {
            // the drop came after the snapshot but before `paired`: the code is used up and there is no token
            _live.PairWithCode(Host, "123456");
            _net.Last.Open();
            _net.Last.Receive(Bridge.EmptySnapshot);
            _live.Poll(0);
            _net.Last.Drop(1006);
            _live.Poll(0);
            _live.Poll(1000);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual(1, _net.Sockets.Count, "no hello with a used-up code");
        }

        [Test]
        public void PairingAgainWhileConnectingOpensNoSecondSocket()
        {
            Assert.IsTrue(_live.PairWithCode(Host, "123456"));
            Assert.IsFalse(_live.PairWithCode(Host, "123456"), "OK twice");
            _store.Set(SessionKey, "tok");
            Assert.IsFalse(_live.Resume(Host));
            Assert.AreEqual(1, _net.Sockets.Count);
        }

        [Test]
        public void ATimeoutAfterTheCodeHelloAsksForANewCodeAndNeverSendsItAgain()
        {
            _live.PairWithCode(Host, "123456");
            _net.Last.Open();
            _live.Poll(LiveSource.HandshakeTimeoutMs);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual(Texts.CodeUnconfirmed, _live.Status);
            _live.Poll(60000);
            Assert.AreEqual(1, _net.Sockets.Count, "the single-use code is never sent twice");
            Assert.AreEqual(1, _net.Last.Sent.Count);
        }

        [Test]
        public void ANetworkCloseAfterTheCodeHelloAsksForANewCode()
        {
            _live.PairWithCode(Host, "123456");
            _net.Last.Open();
            _net.Last.Drop(1006);
            _live.Poll(60000);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);
            Assert.AreEqual(1, _net.Sockets.Count);
        }

        [Test]
        public void ACloseBeforeTheHelloWentOutIsNoVerdict()
        {
            // an app stall: the open is seen late, the socket is already closing (bridge 4008 hello-timeout, reported as 1004)
            _store.Set(SessionKey, "tok");
            _live.Resume(Host);
            var s = _net.Last;
            s.Accepts = false;
            s.Open();
            s.Drop(1004);
            _live.Poll(0);
            Assert.AreEqual(LinkState.Retrying, _live.State);
            Assert.AreEqual("tok", _store.Get(SessionKey));
            _live.Poll(1000);
            Assert.AreEqual(2, _net.Sockets.Count);
            _net.Last.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"session\":\"tok\"}", _net.Last.Sent.Single());
        }

        [Test]
        public void ACodeHelloThatCouldNotBeSentIsTriedAgain()
        {
            _live.PairWithCode(Host, "123456");
            _net.Last.Accepts = false;
            _net.Last.Open();
            _net.Last.Drop(1004);
            _live.Poll(1000);
            Assert.AreEqual(2, _net.Sockets.Count, "the code never left the headset: it may be sent");
            _net.Last.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"123456\"}", _net.Last.Sent.Single());
        }

        [Test]
        public void ASnapshotForACodeHelloForgetsTheOldToken()
        {
            _store.Set(SessionKey, "old");
            _live.PairWithCode(Host, "123456");
            _net.Last.Open();
            _net.Last.Receive(Bridge.EmptySnapshot);
            Assert.IsNull(_store.Get(SessionKey), "the bridge has a new session now");
            _net.Last.Receive(Bridge.Paired("new"));
            Assert.AreEqual("new", _store.Get(SessionKey));
        }

        [Test]
        public void NoAnswerToADecisionWithinFiveSecondsCountsAsLost()
        {
            var s = PairLive();
            Assert.IsNotNull(_live.Send(ClientDecision.Allow("r1", "pinch-hold")));
            _live.Poll(LiveSource.AnswerTimeoutMs - 1);
            Assert.AreEqual(LinkState.Live, _live.State);
            var got = _live.Poll(1);
            Assert.IsInstanceOf<ConnectionLostMessage>(got.Single());
            Assert.AreEqual(LinkState.Retrying, _live.State);
            Assert.IsTrue(s.ClosedByClient, "the socket is dropped");
            _live.Poll(1000);
            Assert.AreEqual(2, _net.Sockets.Count, "and it reconnects");
        }

        [Test]
        public void AnAnsweredDecisionKeepsTheLink()
        {
            var s = PairLive();
            _live.Send(ClientDecision.Allow("r1", "pinch-hold"));
            _live.Poll(1000);
            s.Receive(Bridge.PermClosed("r1", "allow"));
            _live.Poll(10000);
            Assert.AreEqual(LinkState.Live, _live.State);

            _live.Send(ClientDecision.Deny("r2"));
            s.Receive(Bridge.Error("unknown"));
            _live.Poll(10000);
            Assert.AreEqual(LinkState.Live, _live.State, "an error is an answer too");
        }

        [Test]
        public void ASilentLinkIsProbedAndDroppedWhenTheProbeGetsNoAnswer()
        {
            var s = PairLive();
            _live.Poll(LiveSource.IdleProbeMs - 1);
            Assert.AreEqual(1, s.Sent.Count, "hello only");
            _live.Poll(1);
            Assert.AreEqual(LiveSource.Probe, s.Sent.Last(), "a probe after 15 s of silence");
            CollectionAssert.IsEmpty(_live.Poll(LiveSource.IdleTimeoutMs - LiveSource.IdleProbeMs - 1));
            Assert.AreEqual(LinkState.Live, _live.State);
            var got = _live.Poll(1);
            Assert.IsInstanceOf<ConnectionLostMessage>(got.Single());
            Assert.AreEqual(LinkState.Retrying, _live.State);
        }

        [Test]
        public void APongKeepsAHealthyLinkLiveFor100Seconds()
        {
            var s = PairLive();
            var probes = 0;
            for (double t = 0; t < 100000; t += 100)
            {
                CollectionAssert.IsEmpty(_live.Poll(100), "a pong is not passed on");
                if (s.Sent.Last() == LiveSource.Probe && s.Sent.Count - 1 > probes)
                {
                    probes = s.Sent.Count - 1;
                    s.Receive(Bridge.Pong); // the current bridge's answer
                }
                Assert.AreEqual(LinkState.Live, _live.State, $"still live at {t + 100} ms");
            }
            Assert.GreaterOrEqual(probes, 6, "a probe about every 15 s");
            Assert.AreEqual(1, _net.Sockets.Count);
        }

        [Test]
        public void ABadMessageThatIsNotTheProbeAnswerIsPassedOn()
        {
            var s = PairLive();
            _live.Poll(LiveSource.IdleProbeMs);
            Assert.AreEqual(LiveSource.Probe, s.Sent.Last());
            s.Receive(Bridge.Pong);
            s.Receive(Bridge.Error("bad-message")); // not right after the probe: a real error
            var got = _live.Poll(0);
            Assert.AreEqual("bad-message", ((ErrorMessage)got.Single()).Error);
        }

        [Test]
        public void AFrameAfterTheProbeEndsTheOldBridgeSwallow()
        {
            var s = PairLive();
            _live.Poll(LiveSource.IdleProbeMs);
            s.Receive(Bridge.Agent("s1", "working")); // first frame after the probe: the link is alive
            s.Receive(Bridge.Error("bad-message"));
            var got = _live.Poll(0);
            Assert.AreEqual(2, got.Count);
            Assert.IsInstanceOf<ErrorMessage>(got[1]);
        }

        [Test]
        public void AStaleTokenIsNotResumedAgainAfterItsVerdict()
        {
            _store.Set(SessionKey, "stale");
            Assert.IsTrue(_live.Resume(Host));
            _net.Last.Open();
            _net.Last.Drop(1004);
            _live.Poll(0);
            Assert.AreEqual(LinkState.NeedsCode, _live.State);

            for (var i = 0; i < 5; i++) Assert.IsFalse(_live.Resume(Host), "tap " + (i + 1));
            Assert.AreEqual(1, _net.Sockets.Count, "no further hello {session}: the address must not get locked");
            Assert.IsFalse(new LiveSource(new HostBook(_store), _net.Open).Resume(Host), "also after a restart");

            // a new pairing clears the verdict
            Assert.IsTrue(_live.PairWithCode(Host, "123456"));
            _net.Last.Open();
            _net.Last.Receive(Bridge.EmptySnapshot);
            _net.Last.Receive(Bridge.Paired("fresh"));
            _live.Disconnect();
            Assert.IsTrue(_live.Resume(Host));
        }

        [Test]
        public void AnAnsweredProbeKeepsTheLinkAndItsReplyIsSwallowed()
        {
            var s = PairLive();
            for (var i = 0; i < 4; i++)
            {
                _live.Poll(LiveSource.IdleProbeMs);
                Assert.AreEqual(LiveSource.Probe, s.Sent.Last());
                s.Receive(Bridge.Error("bad-message")); // an old bridge's answer to a frame it does not know
                CollectionAssert.IsEmpty(_live.Poll(0), "the probe's answer is no bridge error for the session");
            }
            Assert.AreEqual(LinkState.Live, _live.State);
        }

        [Test]
        public void ADecisionTheSocketRefusesIsNotSent()
        {
            var s = PairLive();
            s.Accepts = false;
            Assert.IsNull(_live.Send(ClientDecision.Allow("r1", "pinch-hold")));
        }
    }
}
