#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U4b, the live feed (contract 2026-11-12-contract-live-feed.md): the wire frames, the caps, de-duplication by id,
    /// auto-scroll, the bridge link (subscribe, unsubscribe, errors that answer a subscription, never a decision) and
    /// the demo's feed derived from the pack.
    /// </summary>
    public class LiveFeedTests
    {
        static FeedEntry E(string id, string kind = "text", string text = "t") => new FeedEntry { Id = id, Kind = kind, Label = "", Text = text };

        // ---- the wire ----

        [Test]
        public void ParsesASnapshotAndAnAppendAndSkipsMalformedEntries()
        {
            var snap = Protocol.Parse("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" +
                                      "{\"id\":\"a\",\"t\":1700000000000,\"kind\":\"tool\",\"label\":\"liest\",\"text\":\"src/a.ts\",\"tool\":\"Read\"}," +
                                      "{\"id\":\"\",\"kind\":\"text\",\"label\":\"sagt\",\"text\":\"no id\"}," +
                                      "{\"kind\":\"text\",\"text\":\"no id at all\"}," +
                                      "{\"id\":\"b\",\"kind\":\"wild\",\"label\":\"?\",\"text\":\"unknown kind\"}," +
                                      "{\"id\":\"c\",\"kind\":\"text\",\"label\":\"sagt\",\"text\":42}," +
                                      "7]}") as FeedSnapshotMessage;
            Assert.IsNotNull(snap);
            Assert.AreEqual("s1", snap!.Sid);
            CollectionAssert.AreEqual(new[] { "a", "b" }, snap.Entries.Select(e => e.Id));
            Assert.AreEqual("tool", snap.Entries[0].Kind);
            Assert.AreEqual("Read", snap.Entries[0].Tool);
            Assert.AreEqual(1700000000000.0, snap.Entries[0].T);
            Assert.AreEqual("system", snap.Entries[1].Kind, "an unknown kind reads as system");

            var app = Protocol.Parse("{\"type\":\"feed_append\",\"sid\":\"s1\",\"entries\":[{\"id\":\"d\",\"kind\":\"error\",\"label\":\"Fehler\",\"text\":\"x\"}]}");
            Assert.IsInstanceOf<FeedAppendMessage>(app);
        }

        [TestCase("{\"type\":\"feed_snapshot\",\"entries\":[]}")]
        [TestCase("{\"type\":\"feed_snapshot\",\"sid\":\"\",\"entries\":[]}")]
        [TestCase("{\"type\":\"feed_snapshot\",\"sid\":3,\"entries\":[]}")]
        [TestCase("{\"type\":\"feed_append\",\"sid\":\"s1\",\"entries\":{}}")]
        [TestCase("{\"type\":\"feed_append\",\"sid\":\"s1\"}")]
        public void AFrameWithoutASidOrAnEntryListIsDropped(string json)
        {
            Assert.IsNull(Protocol.Parse(json));
        }

        [Test]
        public void SerialisesSubscribeAndUnsubscribe()
        {
            Assert.AreEqual("{\"type\":\"feed_subscribe\",\"sid\":\"s1\"}", Protocol.Serialize(new FeedSubscribeMessage { Sid = "s1" }));
            Assert.AreEqual("{\"type\":\"feed_unsubscribe\",\"sid\":\"s1\"}", Protocol.Serialize(new FeedUnsubscribeMessage { Sid = "s1" }));
        }

        [Test]
        public void EntriesAreCappedAndTheirHiddenCharactersShown()
        {
            var entries = Enumerable.Range(0, 80).Select(i => new FeedEntry
            {
                Id = "i" + i, Kind = "text", Label = new string('l', 90), Text = "<b>x</b>‮" + new string('t', 900), Tool = new string('w', 90),
            }).ToList();
            var msg = new FeedAppendMessage { Sid = "s1", Entries = entries };
            Untrusted.Message(msg);
            Assert.AreEqual(Untrusted.FeedEntriesMax, msg.Entries.Count, "at most 60 per frame");
            Assert.AreEqual("i20", msg.Entries[0].Id, "the newest are kept");
            foreach (var e in msg.Entries)
            {
                Assert.LessOrEqual(e.Label.Length, Untrusted.FeedLabelMax);
                Assert.LessOrEqual(e.Text.Length, Untrusted.FeedTextMax);
                Assert.LessOrEqual(e.Tool!.Length, Untrusted.FeedToolMax);
                Assert.AreEqual(-1, e.Text.IndexOf('‮'), "no raw override");
                StringAssert.Contains("‹U+202E›", e.Text);
                StringAssert.StartsWith("<b>x</b>", e.Text, "tags stay plain characters");
            }
        }

        // ---- the log ----

        [Test]
        public void EntriesAreDeDuplicatedByIdAndKeptInOrder()
        {
            var log = new FeedLog();
            log.Subscribed("s1");
            Assert.AreEqual(FeedState.Loading, log.StateOf("s1"));
            Assert.AreEqual(3, log.Merge("s1", new[] { E("a"), E("b"), E("c") }));
            Assert.AreEqual(FeedState.Ready, log.StateOf("s1"));
            var v = log.VersionOf("s1");
            // a snapshot after a re-subscribe repeats what is known: only the new one is added
            Assert.AreEqual(1, log.Merge("s1", new[] { E("b"), E("c"), E("d") }));
            Assert.AreEqual(0, log.Merge("s1", new[] { E("d") }));
            CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, log.Entries("s1").Select(e => e.Id));
            Assert.Greater(log.VersionOf("s1"), v);
            var after = log.VersionOf("s1");
            log.Merge("s1", new[] { E("a") });
            Assert.AreEqual(after, log.VersionOf("s1"), "nothing new: no redraw");
            CollectionAssert.IsEmpty(log.Entries("s2"), "per agent");
        }

        [Test]
        public void TheLogIsBoundedAndAFailedFeedSaysSo()
        {
            var log = new FeedLog();
            log.Merge("s1", Enumerable.Range(0, FeedLog.MaxEntries + 20).Select(i => E("e" + i)).ToList());
            Assert.AreEqual(FeedLog.MaxEntries, log.Entries("s1").Count);
            Assert.AreEqual("e20", log.Entries("s1")[0].Id, "the oldest go first");
            Assert.AreEqual(1, log.Merge("s1", new[] { E("e0") }), "a dropped id may come back");
            log.Subscribed("s9");
            log.Failed("s9");
            Assert.AreEqual(FeedState.Unavailable, log.StateOf("s9"));
            for (var i = 0; i < FeedLog.MaxAgents + 3; i++) log.Merge("x" + i, new[] { E("a") });
            Assert.IsEmpty(log.Entries("s1"), "at most 8 agents: the least recently changed went");
        }

        // ---- the subscription a view follows ----

        sealed class Source : IFeedSource
        {
            public readonly List<string> Calls = new List<string>();
            public bool Accepts = true;
            public bool SubscribeFeed(string sid)
            {
                Calls.Add("+" + sid);
                return Accepts;
            }
            public void UnsubscribeFeed(string sid) => Calls.Add("-" + sid);
        }

        [Test]
        public void OpeningAnotherAgentUnsubscribesTheOneBeforeAndClosingUnsubscribes()
        {
            var log = new FeedLog();
            var source = new Source();
            var sub = new FeedSubscription(log);
            sub.Use(source);
            Assert.IsTrue(sub.Open("a"));
            Assert.AreEqual(FeedState.Loading, log.StateOf("a"));
            Assert.IsTrue(sub.Open("b"));
            sub.Open("b");
            sub.Close();
            sub.Close();
            CollectionAssert.AreEqual(new[] { "+a", "-a", "+b", "+b", "-b" }, source.Calls, "the old one first, then the new; once closed, nothing more");
            Assert.IsNull(sub.Sid);
        }

        [Test]
        public void AReconnectSubscribesAgainAndTheRepeatIsDeDuplicated()
        {
            var log = new FeedLog();
            var source = new Source();
            var sub = new FeedSubscription(log);
            sub.Use(source);
            sub.Open("a");
            Assert.AreEqual(2, sub.Arrived("a", new[] { E("1"), E("2") }));
            Assert.IsTrue(sub.Reconnected());
            Assert.AreEqual("+a", source.Calls.Last());
            Assert.AreEqual(1, sub.Arrived("a", new[] { E("1"), E("2"), E("3") }), "the new snapshot repeats what is known");
            Assert.AreEqual(FeedState.Ready, log.StateOf("a"), "what is known stays shown while it loads again");
            sub.Close();
            Assert.IsFalse(sub.Reconnected(), "nothing followed: nothing to subscribe");
        }

        [Test]
        public void AFeedThatCannotBeHadSaysSoAndANewSessionForgetsEverything()
        {
            var log = new FeedLog();
            var source = new Source { Accepts = false };
            var sub = new FeedSubscription(log);
            sub.Use(source);
            Assert.IsFalse(sub.Open("a"));
            Assert.AreEqual(FeedState.Unavailable, log.StateOf("a"));
            source.Accepts = true;
            sub.Open("b");
            sub.Arrived("b", new[] { E("1") });
            sub.Failed("b");
            Assert.AreEqual(FeedState.Ready, log.StateOf("b"), "known entries stay after a later error");
            sub.Use(null);
            Assert.IsNull(sub.Sid);
            Assert.IsEmpty(log.Entries("b"));
            Assert.IsFalse(sub.Open("c"), "no session: no feed");
        }

        // ---- auto-scroll ----

        [Test]
        public void TheListFollowsTheEndUntilTheUserScrollsUp()
        {
            var s = new FeedScroll();
            s.Update(10);
            Assert.AreEqual(10, s.Bottom);
            Assert.IsTrue(s.FollowsEnd);
            s.Update(12);
            Assert.AreEqual(12, s.Bottom, "new entries scroll in by themselves");
            s.Scroll(-3);
            Assert.AreEqual(9, s.Bottom);
            Assert.IsFalse(s.FollowsEnd, "scrolled up: it stays put");
            s.Update(15);
            Assert.AreEqual(9, s.Bottom);
            Assert.AreEqual(3, s.NewBelow, "and counts what came since (not what was scrolled past)");
            s.Scroll(100);
            Assert.AreEqual(15, s.Bottom);
            Assert.IsTrue(s.FollowsEnd, "back at the end: it follows again");
            s.Scroll(-3);
            s.ScrollToEnd();
            Assert.IsTrue(s.FollowsEnd);
            Assert.AreEqual(0, s.NewBelow);
            s.Scroll(-100);
            Assert.AreEqual(1, s.Bottom, "never above the first entry");
        }

        [Test]
        public void ADragMovesTheContentWithTheHand()
        {
            var s = new FeedScroll();
            s.Update(20);
            s.Drag(-0.01f, 0.02f);
            Assert.AreEqual(20, s.Bottom, "less than one entry: nothing yet");
            s.Drag(-0.012f, 0.02f);
            Assert.AreEqual(19, s.Bottom, "the hand moved down an entry: an older one came in");
            s.Drag(-0.06f, 0.02f);
            Assert.AreEqual(16, s.Bottom);
            s.EndDrag();
            s.Drag(0.1f, 0.02f);
            Assert.AreEqual(20, s.Bottom, "up again, to the end");
            Assert.IsTrue(s.FollowsEnd);
        }

        // ---- the bridge link ----

        MemoryStore _store = null!;
        FakeNet _net = null!;
        LiveSource _live = null!;

        FakeSocket PairLive()
        {
            _store = new MemoryStore();
            _net = new FakeNet();
            _live = new LiveSource(new HostBook(_store), _net.Open);
            Assert.IsTrue(_live.PairWithCode(Bridge.Host, "123456"));
            var s = _net.Last;
            s.Open();
            s.Receive(Bridge.EmptySnapshot);
            s.Receive(Bridge.Paired("tok"));
            _live.Poll(0);
            Assert.AreEqual(LinkState.Live, _live.State);
            return s;
        }

        [Test]
        public void SubscribeAndUnsubscribeGoOutAndTheFeedComesThrough()
        {
            var s = PairLive();
            Assert.IsTrue(_live.SubscribeFeed("s1", out var answer));
            Assert.IsNull(answer, "live, the snapshot comes later");
            Assert.AreEqual("{\"type\":\"feed_subscribe\",\"sid\":\"s1\"}", s.Sent.Last());
            s.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[{\"id\":\"a\",\"kind\":\"text\",\"label\":\"sagt\",\"text\":\"hi\"}]}");
            s.Receive("{\"type\":\"feed_append\",\"sid\":\"s1\",\"entries\":[{\"id\":\"b\",\"kind\":\"text\",\"label\":\"sagt\",\"text\":\"ho\"}]}");
            var got = _live.Poll(16);
            Assert.IsInstanceOf<FeedSnapshotMessage>(got[0]);
            Assert.IsInstanceOf<FeedAppendMessage>(got[1]);
            _live.UnsubscribeFeed("s1");
            Assert.AreEqual("{\"type\":\"feed_unsubscribe\",\"sid\":\"s1\"}", s.Sent.Last());
        }

        [Test]
        public void AnUnknownAgentAnswersTheSubscriptionAndNeverReachesTheDecisionPath()
        {
            var s = PairLive();
            Assert.IsTrue(_live.SubscribeFeed("ghost", out _));
            s.Receive("{\"type\":\"error\",\"error\":\"unknown-agent\"}");
            var got = _live.Poll(16);
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual("ghost", (got[0] as FeedFailedMessage)?.Sid, "the feed's answer, not an ErrorMessage");
            Assert.IsTrue(_live.SubscribeFeed("s1", out _), "an unknown agent does not switch feeds off");
        }

        [Test]
        public void AnOlderBridgeSwitchesFeedsOffUntilTheNextConnection()
        {
            var s = PairLive();
            Assert.IsTrue(_live.SubscribeFeed("s1", out _));
            s.Receive("{\"type\":\"error\",\"error\":\"bad-message\"}");
            Assert.IsInstanceOf<FeedFailedMessage>(_live.Poll(16)[0]);
            var sent = s.Sent.Count;
            Assert.IsFalse(_live.SubscribeFeed("s1", out _), "not asked again on this connection");
            _live.UnsubscribeFeed("s1");
            Assert.AreEqual(sent, s.Sent.Count, "nothing goes to an older bridge");
        }

        [Test]
        public void NoSubscriptionWhileADecisionWaitsOrWithoutALink()
        {
            _store = new MemoryStore();
            _net = new FakeNet();
            _live = new LiveSource(new HostBook(_store), _net.Open);
            Assert.IsFalse(_live.SubscribeFeed("s1", out _), "not connected");
            var s = PairLive();
            Assert.IsNotNull(_live.Send(ClientDecision.Allow("p1", "pinch-hold")));
            Assert.IsFalse(_live.SubscribeFeed("s1", out _), "a decision waits for its answer");
            Assert.IsFalse(s.Sent.Any(m => m.Contains("feed_subscribe")));
        }

        [Test]
        public void ADetailAndAFeedOutstandingTogetherGetTheirOwnErrors()
        {
            var s = PairLive();
            Assert.IsTrue(_live.SubscribeFeed("s1", out _));
            Assert.IsTrue(_live.RequestDetail("s2", out _));
            s.Receive("{\"type\":\"error\",\"error\":\"unknown-agent\"}");
            s.Receive("{\"type\":\"error\",\"error\":\"unknown-agent\"}");
            var got = _live.Poll(16);
            Assert.AreEqual("s1", (got[0] as FeedFailedMessage)?.Sid, "the oldest request first: the subscription");
            Assert.AreEqual("s2", (got[1] as AgentDetailFailedMessage)?.Sid);
        }

        [Test]
        public void WhatTheOldConnectionHadInFlightNeverReachesTheNextSession()
        {
            var s = PairLive();
            s.Receive("{\"type\":\"perm_open\",\"request\":{\"id\":\"p1\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"ls\",\"truncated\":false," +
                      "\"paths\":[],\"risk\":{\"risk\":\"safe\",\"reason\":\"t\"},\"createdAt\":1,\"expiresAt\":90001}," +
                      "\"explain\":{\"text\":\"Listet.\",\"replies\":[\"Nein.\"],\"source\":\"rules\"}}");
            _live.Disconnect(); // "Trennen" or "Neu koppeln" in the frame the card arrived
            CollectionAssert.IsEmpty(_live.Poll(16), "no phantom card after the disconnect");
        }

        // ---- the demo ----

        static readonly HandSample Idle = new HandSample(false, false, 0f, false);

        static DemoPack Pack() => DemoPack.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets", "demo", "jury.pack.json")));

        [Test]
        public void TheDemoFeedComesFromThePackAndGrowsWithTheRecording()
        {
            var session = new DemoSession(Pack());
            var got = new List<FeedEntry>();
            session.FeedArrived += (sid, entries) => { if (sid == "demo-a1") got.AddRange(entries); };
            Assert.IsTrue(session.SubscribeFeed("demo-a1"));
            Assert.AreEqual("prompt", got[0].Kind);
            Assert.AreEqual("Add the cart badge", got[0].Text, "the task is the agent's title");
            Assert.AreEqual("thinking", got[1].Kind);
            Assert.IsTrue(got.Any(e => e.Kind == "tool" && e.Label == "edits" && e.Text == "components/CartBadge.tsx"));
            Assert.AreEqual("text", got.Last().Kind, "the summary as what it says");
            Assert.AreEqual(8, got.Count);
            var snapshot = got.Count;
            for (var i = 0; i < 200; i++) session.Step(100, Idle, Idle);
            Assert.Greater(got.Count, snapshot, "new actions in the recording append entries");
            Assert.AreEqual(got.Count, got.Select(e => e.Id).Distinct().Count(), "ids never repeat");
            session.UnsubscribeFeed("demo-a1");
            var after = got.Count;
            for (var i = 0; i < 300; i++) session.Step(100, Idle, Idle);
            Assert.AreEqual(after, got.Count, "nothing after unsubscribing");
            Assert.IsTrue(session.SubscribeFeed("demo-a1"));
            Assert.IsTrue(got.Skip(after).Take(8).Select(e => e.Id).SequenceEqual(got.Take(8).Select(e => e.Id)),
                "the snapshot's ids are the same every time, so they de-duplicate");
            Assert.AreEqual(0, session.Decisions.Count, "a feed decides nothing");
        }

        [TestCase("Liest lib/pricing.ts", "tool", "reads", "lib/pricing.ts")]
        [TestCase("Reading lib/pricing.ts", "tool", "reads", "lib/pricing.ts")]
        [TestCase("Bearbeitet pages/about.tsx", "tool", "edits", "pages/about.tsx")]
        [TestCase("Editing pages/about.tsx", "tool", "edits", "pages/about.tsx")]
        [TestCase("Bash: ls", "tool", "command", "ls")]
        [TestCase("Führt Tests aus", "tool", "tests", "npm test")]
        [TestCase("Runs tests", "tool", "tests", "npm test")]
        [TestCase("Wartet auf Freigabe: Bash", "system", "waiting", "for your approval (Bash)")]
        [TestCase("Waiting for approval: Bash", "system", "waiting", "for your approval (Bash)")]
        [TestCase("Arbeitet", "thinking", "thinks", DemoFeed.NextThought)]
        [TestCase("Working", "thinking", "thinks", DemoFeed.NextThought)]
        [TestCase("Fertig: alles gut", "text", "says", "Fertig: alles gut")]
        public void EachRecordedActionHasItsKindAndLabel(string action, string kind, string label, string text)
        {
            var e = DemoFeed.ForAction(action);
            Assert.AreEqual(kind, e.Kind);
            Assert.AreEqual(label, e.Label);
            Assert.AreEqual(text, e.Text);
        }
    }
}
