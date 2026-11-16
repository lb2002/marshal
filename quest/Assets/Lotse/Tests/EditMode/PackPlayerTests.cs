#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.Tests
{
    public class PackPlayerTests
    {
        static string PackPath => Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json");

        static DemoPack LoadPack() => DemoPack.Parse(File.ReadAllText(PackPath));

        const double Step = 250;

        /// <summary>Plays the whole pack. The juror decides `decideAfterMs` after each request opened.</summary>
        sealed class Run
        {
            public readonly PackPlayer Player;
            public readonly AgentStore Store = new AgentStore();
            public readonly Stats Stats = new Stats();
            public readonly List<string> Opened = new List<string>();
            public readonly List<Incident> Incidents = new List<Incident>();
            public readonly Dictionary<string, double> DecidedAt = new Dictionary<string, double>();
            public readonly Dictionary<string, AgentState> AfterDecide = new Dictionary<string, AgentState>();
            public readonly Dictionary<string, double> Waits = new Dictionary<string, double>();
            public readonly Dictionary<string, List<double>> SinceBySid = new Dictionary<string, List<double>>();
            public readonly double DecideAfter;

            public Run(DemoPack pack, System.Func<string, bool> allow, double decideAfterMs = 4000)
            {
                Player = new PackPlayer(pack);
                DecideAfter = decideAfterMs;
                var guard = 0;
                while (!Player.Finished && guard++ < 100000)
                {
                    foreach (var m in Player.Tick(Step)) Feed(m);
                    if (Player.IsPaused)
                    {
                        var id = Player.PendingId!;
                        var req = Store.Pending.First(p => p.Id == id);
                        var at = Player.NowMs + decideAfterMs;
                        var ok = allow(id);
                        Stats.RecordDecision(req, ok);
                        DecidedAt[id] = at;
                        var sid = pack.Perms[id].OnAllow.Sid;
                        var before = Player.DisplayedWaitingMs(sid);
                        foreach (var m in Player.Decide(id, ok, at)) Feed(m);
                        Waits[id] = Player.DisplayedWaitingMs(sid) - before;
                        AfterDecide[id] = Store.Agents.First(a => a.Sid == req.Sid).Clone();
                    }
                }
                Assert.Less(guard, 100000, "player never finished");
            }

            void Feed(ServerMessage m)
            {
                if (m is PermOpenMessage po) Opened.Add(po.Request.Id);
                if (m is IncidentMessage im) Incidents.Add(im.Incident);
                if (m is AgentMessage am)
                {
                    if (!SinceBySid.TryGetValue(am.Agent.Sid, out var l)) SinceBySid[am.Agent.Sid] = l = new List<double>();
                    l.Add(am.Agent.Since);
                }
                Store.Apply(m);
            }
        }

        [Test]
        public void PausesExactlyNineteenTimesAndFinishes()
        {
            var run = new Run(LoadPack(), id => id != "p11" && id != "p14");
            Assert.AreEqual(19, run.Opened.Count);
            Assert.IsTrue(run.Player.Finished);
            Assert.IsFalse(run.Player.IsPaused);
            Assert.IsNull(run.Player.PendingId);
            Assert.AreEqual(0, run.Store.Pending.Count, "every request is closed");
        }

        [Test]
        public void PlayerIsPausedUntilDecided()
        {
            var p = new PackPlayer(LoadPack());
            var guard = 0;
            while (!p.IsPaused && guard++ < 10000) p.Tick(Step);
            Assert.IsTrue(p.IsPaused);
            Assert.AreEqual("p1", p.PendingId);
            Assert.IsFalse(p.Finished);
            Assert.AreEqual(0, p.Tick(60000).Count, "nothing is due while paused");
            Assert.IsTrue(p.IsPaused);
        }

        [Test]
        public void DestructiveRequestsAreP11AndP14()
        {
            var pack = LoadPack();
            var destructive = pack.Messages
                .Select(e => e.Msg).OfType<PermOpenMessage>()
                .Where(m => m.Request.NeedsTwoHand).Select(m => m.Request.Id).ToArray();
            CollectionAssert.AreEqual(new[] { "p11", "p14" }, destructive);
        }

        [Test]
        public void DecideAppliesOnAllowPatchAndSetsSinceInPackTime()
        {
            var pack = LoadPack();
            var run = new Run(pack, id => true);
            var a = run.AfterDecide["p11"];
            Assert.AreEqual("demo-a3", a.Sid);
            Assert.AreEqual(pack.Perms["p11"].OnAllow.LastAction, a.LastAction);
            Assert.AreEqual(pack.Perms["p11"].OnAllow.Status, a.Status);
            // `since` lives in pack time: the timeline stood still at the request, so that is where it resumes
            var createdAt = pack.Messages.Select(e => e.Msg).OfType<PermOpenMessage>().First(m => m.Request.Id == "p11").Request.CreatedAt;
            Assert.AreEqual(createdAt, a.Since);
            Assert.AreNotEqual(run.DecidedAt["p11"], a.Since, "not the shifted clock");
        }

        [Test]
        public void DecideAppliesOnDenyPatch()
        {
            var pack = LoadPack();
            var run = new Run(pack, id => id != "p11");
            Assert.AreEqual(pack.Perms["p11"].OnDeny.LastAction, run.AfterDecide["p11"].LastAction);
            Assert.AreNotEqual(pack.Perms["p11"].OnAllow.LastAction, run.AfterDecide["p11"].LastAction);
        }

        [Test]
        public void DecideReturnsMergedAgentThenSynthesisedClose()
        {
            var p = new PackPlayer(LoadPack());
            var guard = 0;
            while (!p.IsPaused && guard++ < 10000) p.Tick(Step);
            var out1 = p.Decide("p1", true, p.NowMs + 2000);
            Assert.AreEqual(2, out1.Count);
            Assert.IsInstanceOf<AgentMessage>(out1[0]);
            var closed = (PermClosedMessage)out1[1];
            Assert.AreEqual("p1", closed.Id);
            Assert.AreEqual("allow", closed.Reason);
            Assert.IsFalse(p.IsPaused);
        }

        [Test]
        public void DecideWithWrongIdOrNotPausedDoesNothing()
        {
            var p = new PackPlayer(LoadPack());
            Assert.AreEqual(0, p.Decide("p1", true, 0).Count);
            var guard = 0;
            while (!p.IsPaused && guard++ < 10000) p.Tick(Step);
            Assert.AreEqual(0, p.Decide("p9", true, p.NowMs).Count);
            Assert.IsTrue(p.IsPaused);
        }

        [Test]
        public void DisplayedWaitingAddsTheJurorsWait()
        {
            var pack = LoadPack();
            var p = new PackPlayer(pack);
            var guard = 0;
            while (!p.IsPaused && guard++ < 10000) p.Tick(Step);
            var req = pack.Messages.Select(e => e.Msg).OfType<PermOpenMessage>().First(m => m.Request.Id == "p1").Request;
            var sid = req.Sid;
            var before = p.DisplayedWaitingMs(sid);
            // the shifted timeline: before any earlier decision, the request opened at its createdAt
            var decisionAt = req.CreatedAt + 6000;
            p.Decide("p1", true, decisionAt);
            Assert.AreEqual(before + 6000, p.DisplayedWaitingMs(sid), 0.001);
        }

        [Test]
        public void DisplayedWaitingAccumulatesAcrossDecisionsAndShowsInAgentMessages()
        {
            var run = new Run(LoadPack(), id => true, decideAfterMs: 3000);
            foreach (var a in run.Store.Agents)
                Assert.AreEqual(a.WaitingMsTotal, run.Player.DisplayedWaitingMs(a.Sid), 0.001, a.Sid);
            Assert.AreEqual(19, run.Waits.Count);
            foreach (var kv in run.Waits)
            {
                Assert.GreaterOrEqual(kv.Value, run.DecideAfter, kv.Key);
                Assert.Less(kv.Value, run.DecideAfter + Step, kv.Key);
            }
            Assert.AreEqual(Stats.TotalWaitingMs(run.Store.Agents),
                run.Store.Agents.Sum(a => run.Player.DisplayedWaitingMs(a.Sid)), 0.001);
        }

        [Test]
        public void EmitsOneCollisionAndOneBudgetNearIncident()
        {
            var run = new Run(LoadPack(), id => true);
            Assert.AreEqual(2, run.Incidents.Count);
            Assert.AreEqual(1, run.Incidents.Count(i => i.Kind == "collision"));
            Assert.AreEqual(1, run.Incidents.Count(i => i.Kind == "budget_near"));
        }

        [Test]
        public void DenyingTheDestructiveRequestsCountsTwoDangerousStopped()
        {
            var run = new Run(LoadPack(), id => id != "p11" && id != "p14");
            Assert.AreEqual(2, run.Stats.DangerousStopped);
        }

        [Test]
        public void IsDeterministicForTheSameDecisions()
        {
            var a = new Run(LoadPack(), id => id != "p11");
            var b = new Run(LoadPack(), id => id != "p11");
            CollectionAssert.AreEqual(a.Opened, b.Opened);
            CollectionAssert.AreEqual(
                a.Store.Agents.Select(x => x.LastAction + x.Since + x.WaitingMsTotal),
                b.Store.Agents.Select(x => x.LastAction + x.Since + x.WaitingMsTotal));
        }

        [Test]
        public void SinceIsMonotonicPerAgentAcrossPackAndDecidedMessages()
        {
            var run = new Run(LoadPack(), id => id != "p11");
            foreach (var kv in run.SinceBySid)
                for (var i = 1; i < kv.Value.Count; i++)
                    Assert.GreaterOrEqual(kv.Value[i], kv.Value[i - 1], kv.Key + " message " + i);
        }

        [Test]
        public void DecideWithoutTimeUsesThePlayersOwnClock()
        {
            var p = new PackPlayer(LoadPack());
            var guard = 0;
            while (!p.IsPaused && guard++ < 10000) p.Tick(Step);
            var req = LoadPack().Messages.Select(e => e.Msg).OfType<PermOpenMessage>().First(m => m.Request.Id == p.PendingId).Request;
            var before = p.DisplayedWaitingMs(req.Sid);
            for (var i = 0; i < 20; i++) p.Tick(Step); // 5000 ms while paused
            var result = p.Decide(p.PendingId!, true);
            Assert.AreEqual(2, result.Count);
            var wait = p.DisplayedWaitingMs(req.Sid) - before;
            Assert.GreaterOrEqual(wait, 5000);
            Assert.Less(wait, 5000 + Step);
            Assert.IsFalse(p.IsPaused);
        }

        const string TinyPack = "{\"version\":1,\"budgetUsd\":1,\"durationMs\":10,\"messages\":["
            + "{\"t\":0,\"msg\":{\"type\":\"agent\",\"agent\":{\"sid\":\"s1\",\"name\":\"Alpha\",\"status\":\"working\",\"lastAction\":\"x\",\"since\":0,\"ctxPct\":40,\"costUsd\":1.5,\"files\":[\"a.ts\"],\"waitingMsTotal\":7}}},"
            + "{\"t\":10,\"msg\":{\"type\":\"perm_open\",\"request\":{\"id\":\"p1\",\"sid\":\"s1\",\"tool\":\"Edit\",\"truncated\":false,\"paths\":[],\"risk\":{\"risk\":\"safe\",\"reason\":\"ok\"},\"createdAt\":10,\"expiresAt\":100},\"explain\":{\"text\":\"t\",\"replies\":[],\"source\":\"rules\"}}}],"
            + "\"perms\":{\"p1\":{\"onAllow\":{\"sid\":\"s1\",\"status\":\"editing\",\"lastAction\":\"Freigegeben\"},\"onDeny\":{\"sid\":\"s1\",\"status\":\"working\",\"lastAction\":\"Abgelehnt\"}}}}";

        [Test]
        public void MergeKeepsNameFilesCtxAndCostAndOnlyPatchesStatusAndLastAction()
        {
            var p = new PackPlayer(DemoPack.Parse(TinyPack));
            while (!p.IsPaused) p.Tick(Step);
            var a = ((AgentMessage)p.Decide("p1", true, p.NowMs + 1000)[0]).Agent;
            Assert.AreEqual("Alpha", a.Name);
            CollectionAssert.AreEqual(new[] { "a.ts" }, a.Files);
            Assert.AreEqual(40, a.CtxPct);
            Assert.AreEqual(1.5, a.CostUsd);
            Assert.AreEqual("editing", a.Status);
            Assert.AreEqual("Freigegeben", a.LastAction);
            Assert.AreEqual(10, a.Since);
        }

        const string AgentMsg = "{\"t\":0,\"msg\":{\"type\":\"agent\",\"agent\":{\"sid\":\"a\",\"name\":\"A\",\"status\":\"working\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}}";

        [Test]
        public void UnknownMessageTypesAreSkippedAndCounted()
        {
            var pack = DemoPack.Parse("{\"messages\":[" + AgentMsg + ",{\"t\":1,\"msg\":{\"type\":\"telemetry\",\"x\":1}}," +
                                      "{\"t\":2,\"msg\":{\"type\":\"future_thing\"}}," + AgentMsg + "]}");
            Assert.AreEqual(2, pack.Messages.Count);
            Assert.AreEqual(2, pack.SkippedUnknown);
            // the jury pack (a4ecdbf) carries two perm_impact messages: read since U7, nothing skipped
            var jury = LoadPack();
            Assert.AreEqual(0, jury.SkippedUnknown);
            Assert.AreEqual(2, jury.Messages.Count(m => m.Msg is PermImpactMessage), "the two impacts are read");
        }

        [Test]
        public void AnInvalidPermOpenStillRejectsThePack()
        {
            // no boolean "truncated": not a request we can show safely
            const string bad = "{\"t\":1,\"msg\":{\"type\":\"perm_open\",\"request\":{\"id\":\"p\",\"sid\":\"a\",\"tool\":\"Bash\",\"paths\":[]," +
                               "\"risk\":{\"risk\":\"safe\",\"reason\":\"r\"},\"createdAt\":1,\"expiresAt\":2},\"explain\":{\"text\":\"t\",\"replies\":[],\"source\":\"rules\"}}}";
            Assert.Throws<System.FormatException>(() => DemoPack.Parse("{\"messages\":[" + AgentMsg + "," + bad + "]}"));
            Assert.Throws<System.FormatException>(() => DemoPack.Parse("{\"messages\":[{\"t\":1,\"msg\":{\"x\":1}}]}"), "a message without a type");
        }

        [Test]
        public void TickingWhilePausedAllocatesNothing()
        {
            var p = new PackPlayer(LoadPack());
            var guard = 0;
            while (!p.IsPaused && guard++ < 10000) p.Tick(Step);
            Assert.IsTrue(p.IsPaused);
            p.Tick(Step);
            Assert.That(() =>
            {
                for (var i = 0; i < 100; i++) p.Tick(Step);
            }, UnityEngine.TestTools.Constraints.ConstraintExtensions.AllocatingGCMemory(Is.Not));
        }
    }
}
