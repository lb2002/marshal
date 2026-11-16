#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U2: agent detail. The cache asks at most once per sid every 3 s, keeps the latest answer and gives up after
    /// 5 s; every detail is capped and its hidden characters shown. Offline the pack's `agents` answer at once;
    /// live the request goes to the bridge, and an older bridge's "bad-message" is swallowed.
    /// </summary>
    public class AgentDetailsTests
    {
        static AgentDetailMessage Detail(string sid = "a1", string summary = "Baut die Warenkorb-Seite um.") => new AgentDetailMessage
        {
            Sid = sid,
            Title = "Warenkorb",
            Summary = summary,
            Recent = new List<string> { "Bearbeitet src/cart.ts" },
            WaitingFor = null,
            Source = "ai",
        };

        // ---- the cache ----

        [Test]
        public void ASidIsAskedAtMostOnceEveryThreeAndAHalfSeconds()
        {
            var c = new AgentDetails();
            Assert.AreEqual(3500.0, AgentDetails.RequestEveryMs, "a little over the bridge's 3 s, so it never drops ours");
            Assert.IsTrue(c.TryRequest("a1", 0));
            Assert.IsFalse(c.TryRequest("a1", 3499), "within 3.5 s: not again");
            Assert.IsTrue(c.TryRequest("a2", 100), "another agent may be asked");
            Assert.IsTrue(c.TryRequest("a1", 3500), "after 3.5 s again");
        }

        [Test]
        public void AnUnansweredRequestIsClearedAfterFiveSecondsAndMayBeAskedAgain()
        {
            var c = new AgentDetails();
            c.TryRequest("a1", 0);
            Assert.AreEqual(DetailState.Loading, c.StateOf("a1", 4999));
            Assert.AreEqual(DetailState.None, c.StateOf("a1", 5000), "cleared: no endless loading");
            Assert.IsTrue(c.TryRequest("a1", 5000), "asked again");
            Assert.AreEqual(DetailState.Loading, c.StateOf("a1", 5001));
        }

        [Test]
        public void ANewerDetailReplacesTheOlderRulesThenAi()
        {
            var c = new AgentDetails();
            c.TryRequest("a1", 0);
            var rules = Detail(summary: "Regeln: bearbeitet Dateien.");
            rules.Source = "rules";
            c.Put(rules);
            var ai = Detail(summary: "AI: baut den Warenkorb um.");
            c.Put(ai);
            Assert.AreEqual("AI: baut den Warenkorb um.", c.Get("a1")!.Summary, "the newest is kept");
            Assert.AreEqual("ai", c.Get("a1")!.Source);
        }

        [Test]
        public void LoadingThenReadyWithTheLatestDetail()
        {
            var c = new AgentDetails();
            Assert.AreEqual(DetailState.None, c.StateOf("a1", 0), "never asked");
            c.TryRequest("a1", 0);
            Assert.AreEqual(DetailState.Loading, c.StateOf("a1", 100));
            c.Put(Detail());
            Assert.AreEqual(DetailState.Ready, c.StateOf("a1", 200));
            Assert.AreEqual("Baut die Warenkorb-Seite um.", c.Get("a1")!.Summary);
            c.Put(Detail(summary: "Jetzt die Tests."));
            Assert.AreEqual("Jetzt die Tests.", c.Get("a1")!.Summary, "a newer detail replaces the older one");
            c.TryRequest("a1", 4000);
            Assert.AreEqual(DetailState.Ready, c.StateOf("a1", 4100), "asked again: the known detail stays on show");
        }

        [Test]
        public void NoAnswerWithinFiveSecondsMeansNoDetails()
        {
            var c = new AgentDetails();
            c.TryRequest("a1", 0);
            Assert.AreEqual(DetailState.Loading, c.StateOf("a1", AgentDetails.AnswerTimeoutMs - 1));
            Assert.AreEqual(DetailState.None, c.StateOf("a1", AgentDetails.AnswerTimeoutMs));
            c.Put(Detail());
            Assert.AreEqual(DetailState.Ready, c.StateOf("a1", AgentDetails.AnswerTimeoutMs + 1), "a late answer still shows");
        }

        [Test]
        public void ASourceThatCannotAskMeansNoDetailsAtOnce()
        {
            var c = new AgentDetails();
            c.TryRequest("a1", 0);
            c.NoAnswer("a1");
            Assert.AreEqual(DetailState.None, c.StateOf("a1", 1));
            Assert.IsNull(c.Get("a1"));
        }

        [Test]
        public void ClearForgetsEverything()
        {
            var c = new AgentDetails();
            c.TryRequest("a1", 0);
            c.Put(Detail());
            c.Clear();
            Assert.IsNull(c.Get("a1"));
            Assert.IsTrue(c.TryRequest("a1", 1), "a new session may ask at once");
        }

        // ---- caps (shared contract: title 40, summary 280, recent 5 × 80, waitingFor 140; agent title 40, project 30) ----

        [Test]
        public void TheCacheCapsAndDefusesWhatComesIn()
        {
            var c = new AgentDetails();
            var d = new AgentDetailMessage
            {
                Sid = "a1",
                Title = "T\u202E" + new string('t', 60),
                Summary = "<b>fett</b>\n" + new string('s', 400),
                Recent = Enumerable.Range(0, 8).Select(i => "Bearbeitet\t" + i + new string('r', 100)).ToList(),
                WaitingFor = "Freigabe\u2028" + new string('w', 200),
                Source = "ai",
            };
            c.Put(d);
            var got = c.Get("a1")!;
            Assert.LessOrEqual(got.Title.Length, Untrusted.TitleMax);
            StringAssert.Contains("‹U+202E›", got.Title);
            Assert.LessOrEqual(got.Summary.Length, Untrusted.SummaryMax);
            StringAssert.StartsWith("<b>fett</b>¶", got.Summary, "kept as plain characters, the line break shown");
            StringAssert.EndsWith("…", got.Summary);
            Assert.AreEqual(Untrusted.RecentMax, got.Recent.Count, "at most 5 actions");
            foreach (var r in got.Recent)
            {
                Assert.LessOrEqual(r.Length, Untrusted.RecentItemMax);
                StringAssert.Contains("→", r, "the tab is shown");
            }
            StringAssert.StartsWith("Bearbeitet→0", got.Recent[0], "newest first, as sent");
            Assert.LessOrEqual(got.WaitingFor!.Length, Untrusted.WaitingForMax);
            StringAssert.Contains("¶", got.WaitingFor);
        }

        [Test]
        public void AnEmptyOrMissingWaitingForStaysNone()
        {
            var d = Detail();
            d.WaitingFor = "";
            Untrusted.Detail(d);
            Assert.IsNull(d.WaitingFor, "nothing to wait for");
        }

        [Test]
        public void AgentTitleProjectAndSourceAreCappedOnArrival()
        {
            var a = new AgentState { Sid = "a", Name = "n", Title = new string('t', 90) + "\u200B", Project = new string('p', 50), Source = new string('s', 80) };
            Untrusted.Message(new AgentMessage { Agent = a });
            Assert.LessOrEqual(a.Title!.Length, Untrusted.TitleMax);
            Assert.LessOrEqual(a.Project!.Length, Untrusted.ProjectMax);
            Assert.LessOrEqual(a.Source!.Length, Untrusted.NameMax);
        }

        [Test]
        public void ADetailMessageIsCappedWhenTheSessionGetsIt()
        {
            var d = Detail(summary: new string('x', 1000));
            Untrusted.Message(d);
            Assert.LessOrEqual(d.Summary.Length, Untrusted.SummaryMax);
        }

        // ---- offline: the pack's agents ----

        const string Pack = "{\"version\":1,\"budgetUsd\":15,\"durationMs\":1000,\"messages\":[" +
                            "{\"t\":0,\"msg\":{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}}," +
                            "{\"t\":0,\"msg\":{\"type\":\"agent\",\"agent\":{\"sid\":\"demo-a1\",\"name\":\"Agent 1\",\"status\":\"working\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}}]," +
                            "\"agents\":{\"demo-a1\":{\"title\":\"Warenkorb\",\"summary\":\"" + "S\u202E" + "\",\"recent\":[\"Bearbeitet src/cart.ts\"],\"waitingFor\":\"Deine Freigabe\"}}}";

        [Test]
        public void OfflineThePackAnswersAtOnceCappedAndWithoutChangingThePack()
        {
            var pack = DemoPack.Parse(Pack);
            var s = new DemoSession(pack);
            var got = new List<AgentDetailMessage>();
            s.AgentDetailArrived += got.Add;
            Assert.IsTrue(s.RequestDetail("demo-a1"));
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual("demo-a1", got[0].Sid);
            Assert.AreEqual("Warenkorb", got[0].Title);
            Assert.AreEqual("S‹U+202E›", got[0].Summary, "capped and defused on arrival");
            Assert.AreEqual("Deine Freigabe", got[0].WaitingFor);
            Assert.AreEqual("S\u202E", pack.Agents["demo-a1"].Summary, "the pack's own entry is untouched (Nochmal replays it)");
            Assert.AreEqual(0, s.Decisions.Count, "asking decides nothing");
        }

        [Test]
        public void OfflineAnAgentWithoutAnEntryCannotBeAsked()
        {
            var s = new DemoSession(DemoPack.Parse(Pack));
            var got = new List<AgentDetailMessage>();
            s.AgentDetailArrived += got.Add;
            Assert.IsFalse(s.RequestDetail("demo-a9"));
            CollectionAssert.IsEmpty(got);
        }

        // ---- live: the bridge ----

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
            Assert.AreEqual(LinkState.Live, live.State);
            return (live, s);
        }

        const string DetailFrame = "{\"type\":\"agent_detail\",\"sid\":\"a1\",\"title\":\"t\",\"summary\":\"s\",\"recent\":[],\"waitingFor\":null,\"source\":\"rules\"}";

        [Test]
        public void LiveTheRequestGoesToTheBridgeAndTheAnswerComesThroughPoll()
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out var answer));
            Assert.IsNull(answer, "live, the answer comes later");
            Assert.AreEqual("{\"type\":\"agent_detail_request\",\"sid\":\"a1\"}", s.Sent.Last());
            s.Receive(DetailFrame);
            var got = live.Poll(16);
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual("a1", ((AgentDetailMessage)got[0]).Sid);
        }

        [Test]
        public void NotLiveNothingIsAsked()
        {
            var live = new LiveSource(new HostBook(new MemoryStore()), new FakeNet().Open);
            Assert.IsFalse(live.RequestDetail("a1", out _));
        }

        [Test]
        public void NotWhileADecisionWaitsForItsAnswer()
        {
            var (live, s) = PairedLive();
            Assert.IsNotNull(live.Send(ClientDecision.Allow("p1", "pinch-hold")));
            var sent = s.Sent.Count;
            Assert.IsFalse(live.RequestDetail("a1", out _), "a decision's answer must not be confused with anything else");
            Assert.AreEqual(sent, s.Sent.Count);
            s.Receive(Bridge.PermClosed("p1", "allow"));
            live.Poll(16);
            Assert.IsTrue(live.RequestDetail("a1", out _), "once answered, details can be asked again");
        }

        [Test]
        public void AnOlderBridgesBadMessageIsSwallowedAndDetailIsNotAskedAgain()
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out _));
            s.Receive(Bridge.Agent("a1", "working")); // other frames may come first
            s.Receive(Bridge.Error("bad-message"));
            var got = live.Poll(16);
            Assert.AreEqual(2, got.Count, "the agent update, then the detail request's answer: none (no error, no banner)");
            Assert.IsInstanceOf<AgentMessage>(got[0]);
            Assert.IsInstanceOf<AgentDetailFailedMessage>(got[1]);
            Assert.IsFalse(live.RequestDetail("a2", out _), "this bridge does not know the request: not asked again");
            Assert.AreEqual(LinkState.Live, live.State, "the link is fine");
        }

        [Test]
        public void ARequestWithoutAnAnswerForFiveSecondsIsClearedAndNeverLocksUp()
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out _));
            live.Poll(LiveSource.DetailAnswerTimeoutMs); // the bridge dropped it silently (its rate limit)
            s.Receive(Bridge.Error("bad-message"));
            CollectionAssert.IsEmpty(live.Poll(16), "the request was given up: its late answer is dropped, never a refusal (U3 review M2)");
            Assert.IsTrue(live.RequestDetail("a1", out _), "and details are still asked: the late bad-message did not switch them off");
        }

        [Test]
        public void ANewConnectionAsksForDetailsAgainAfterAnOlderBridge()
        {
            var net = new FakeNet();
            var live = new LiveSource(new HostBook(new MemoryStore()), net.Open);
            Assert.IsTrue(live.PairWithCode(Bridge.Host, "123456"));
            var s = net.Last;
            s.Open();
            s.Receive(Bridge.EmptySnapshot);
            s.Receive(Bridge.Paired("tok"));
            live.Poll(0);
            Assert.IsTrue(live.RequestDetail("a1", out _));
            s.Receive(Bridge.Error("bad-message"));
            live.Poll(16);
            Assert.IsFalse(live.RequestDetail("a1", out _), "this bridge does not know the request");

            // the link drops and comes back (the bridge may have been updated meanwhile): details are asked again
            s.Drop(1006);
            live.Poll(16);
            live.Poll(LiveSource.MaxBackoffMs);
            Assert.AreEqual(2, net.Sockets.Count, "reconnected");
            net.Last.Open();
            net.Last.Receive(Bridge.EmptySnapshot);
            live.Poll(16);
            Assert.AreEqual(LinkState.Live, live.State);
            Assert.IsTrue(live.RequestDetail("a1", out _), "a new connection tries again");
        }

        [Test]
        public void AFollowUpDetailForTheSameRequestPassesThroughToo()
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out _));
            s.Receive(DetailFrame); // the rules first
            s.Receive(DetailFrame.Replace("\"rules\"", "\"ai\""));
            var got = live.Poll(16);
            Assert.AreEqual(2, got.Count, "rules, then the AI's follow-up");
            Assert.AreEqual("ai", ((AgentDetailMessage)got[1]).Source);
        }

        [Test]
        public void ARealErrorAfterADetailAnswerIsPassedOn()
        {
            var (live, s) = PairedLive();
            Assert.IsTrue(live.RequestDetail("a1", out _));
            s.Receive(DetailFrame);
            s.Receive(Bridge.Error("bad-message"));
            var got = live.Poll(16);
            Assert.AreEqual(2, got.Count, "the detail request was answered: a later error is a real one");
            Assert.IsInstanceOf<ErrorMessage>(got[1]);
        }

        [Test]
        public void LiveTheSessionPassesTheCappedDetailOn()
        {
            var (live, s) = PairedLive();
            var session = new DemoSession(live);
            var got = new List<AgentDetailMessage>();
            session.AgentDetailArrived += got.Add;
            session.Step(16, default, default, HoldPose.Open);
            Assert.IsTrue(session.RequestDetail("a1"));
            s.Receive("{\"type\":\"agent_detail\",\"sid\":\"a1\",\"title\":\"t\",\"summary\":\"" + new string('z', 600) +
                      "\",\"recent\":[],\"waitingFor\":null,\"source\":\"ai\"}");
            session.Step(16, default, default, HoldPose.Open);
            Assert.AreEqual(1, got.Count);
            Assert.LessOrEqual(got[0].Summary.Length, Untrusted.SummaryMax);
            Assert.AreEqual(0, session.Decisions.Count);
            Assert.IsNull(session.Card, "a detail never opens a card");
        }
    }
}
