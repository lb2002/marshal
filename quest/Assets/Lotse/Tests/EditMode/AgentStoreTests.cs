#nullable enable
using System.Collections.Generic;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    public class AgentStoreTests
    {
        static AgentState Agent(string sid, string action = "x") => new AgentState { Sid = sid, Name = sid, LastAction = action };

        static PermOpenMessage Open(string id, string sid = "a1", string text = "t") => new PermOpenMessage
        {
            Request = new PermRequest { Id = id, Sid = sid, Tool = "Edit" },
            Explain = new Explanation { Text = text },
        };

        [Test]
        public void SnapshotReplacesEverything()
        {
            var s = new AgentStore();
            s.Apply(new AgentMessage { Agent = Agent("old") });
            s.Apply(Open("pold"));
            s.Apply(new SnapshotMessage
            {
                Agents = { Agent("a1"), Agent("a2") },
                Pending = { new PermRequest { Id = "p1", Sid = "a1" } },
                Explains = { ["p1"] = new Explanation { Text = "e1" } },
            });
            Assert.AreEqual(2, s.Agents.Count);
            Assert.AreEqual("a1", s.Agents[0].Sid);
            Assert.AreEqual(1, s.Pending.Count);
            Assert.AreEqual("e1", s.ExplainFor("p1")!.Text);
            Assert.IsNull(s.ExplainFor("pold"));
        }

        [Test]
        public void AgentUpsertsKeepingOrder()
        {
            var s = new AgentStore();
            s.Apply(new AgentMessage { Agent = Agent("a1", "one") });
            s.Apply(new AgentMessage { Agent = Agent("a2") });
            s.Apply(new AgentMessage { Agent = Agent("a1", "two") });
            Assert.AreEqual(2, s.Agents.Count);
            Assert.AreEqual("two", s.Agents[0].LastAction);
            Assert.AreEqual("a2", s.Agents[1].Sid);
        }

        [Test]
        public void PermOpenKeepsOpenOrderAndExplain()
        {
            var s = new AgentStore();
            s.Apply(Open("p2", text: "two"));
            s.Apply(Open("p1", text: "one"));
            Assert.AreEqual("p2", s.Pending[0].Id);
            Assert.AreEqual("p1", s.Pending[1].Id);
            Assert.AreEqual("one", s.ExplainFor("p1")!.Text);
        }

        [Test]
        public void PermOpenForKnownIdReplacesInPlace()
        {
            var s = new AgentStore();
            s.Apply(Open("p1", text: "a"));
            s.Apply(Open("p2"));
            s.Apply(Open("p1", text: "b"));
            Assert.AreEqual(2, s.Pending.Count);
            Assert.AreEqual("p1", s.Pending[0].Id);
            Assert.AreEqual("b", s.ExplainFor("p1")!.Text);
        }

        [Test]
        public void PermExplainReplacesExplainOnlyWhilePending()
        {
            var s = new AgentStore();
            s.Apply(Open("p1", text: "rules"));
            s.Apply(new PermExplainMessage { Id = "p1", Explain = new Explanation { Text = "rules", AiText = "ki", Source = "ai" } });
            Assert.AreEqual("ki", s.ExplainFor("p1")!.AiText);
            s.Apply(new PermExplainMessage { Id = "gone", Explain = new Explanation { Text = "late" } });
            Assert.IsNull(s.ExplainFor("gone"));
        }

        [Test]
        public void PermClosedRemovesPendingAndExplain()
        {
            var s = new AgentStore();
            s.Apply(Open("p1"));
            s.Apply(Open("p2"));
            s.Apply(new PermClosedMessage { Id = "p1", Reason = "deny" });
            Assert.AreEqual(1, s.Pending.Count);
            Assert.AreEqual("p2", s.Pending[0].Id);
            Assert.IsNull(s.ExplainFor("p1"));
        }

        [Test]
        public void IncidentsAreCappedAtFiftyKeepingTheNewest()
        {
            var s = new AgentStore();
            for (var i = 0; i < 60; i++)
                s.Apply(new IncidentMessage { Incident = new Incident { Kind = "collision", Detail = "d" + i, T = i } });
            Assert.AreEqual(AgentStore.MaxIncidents, s.Incidents.Count);
            Assert.AreEqual("d10", s.Incidents[0].Detail);
            Assert.AreEqual("d59", s.Incidents[49].Detail);
        }

        [Test]
        public void RaisesOneEventPerChange()
        {
            var s = new AgentStore();
            var kinds = new List<string>();
            s.Changed += c => kinds.Add(c.Kind + ":" + c.Id);
            s.Apply(new SnapshotMessage());
            s.Apply(new AgentMessage { Agent = Agent("a1") });
            s.Apply(Open("p1"));
            s.Apply(new PermExplainMessage { Id = "p1", Explain = new Explanation() });
            s.Apply(new IncidentMessage { Incident = new Incident() });
            s.Apply(new PermClosedMessage { Id = "p1" });
            s.Apply(new PermClosedMessage { Id = "unknown" }); // no change, no event
            s.Apply(new PairedMessage()); // not store state
            CollectionAssert.AreEqual(new[]
            {
                "Snapshot:", "Agent:a1", "PermOpened:p1", "PermExplain:p1", "Incident:", "PermClosed:p1",
            }, kinds);
        }

        [TestCase("<repo>/src/a.ts", "src/a.ts")]
        [TestCase("src/a.ts", "src/a.ts")]
        [TestCase("rm -rf <repo>/build", "rm -rf <repo>/build")]
        [TestCase("", "")]
        public void DisplayPathStripsLeadingRepoOnly(string input, string expected)
        {
            Assert.AreEqual(expected, AgentStore.DisplayPath(input));
        }

        [TestCase("rm -rf <repo>/build", "rm -rf build")]
        [TestCase("cp <repo>/a <repo>/b", "cp a b")]
        [TestCase("no placeholder", "no placeholder")]
        [TestCase("", "")]
        public void DisplayTextReplacesEveryRepoPlaceholder(string input, string expected)
        {
            Assert.AreEqual(expected, AgentStore.DisplayText(input));
        }

        [Test]
        public void SnapshotKeepsIncidentsBecauseSnapshotsCarryNone()
        {
            var s = new AgentStore();
            s.Apply(new IncidentMessage { Incident = new Incident { Kind = "collision" } });
            s.Apply(new SnapshotMessage());
            Assert.AreEqual(1, s.Incidents.Count);
        }

        static AgentPlan Plan(params string[] texts)
        {
            var p = new AgentPlan { UpdatedAt = 1 };
            foreach (var t in texts) p.Steps.Add(new PlanStep { Text = t, Status = PlanStatus.Pending });
            return p;
        }

        [Test]
        public void TheLatestPlanOfAnAgentIsKept()
        {
            var s = new AgentStore();
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1", Plan = Plan("eins") } });
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1", Plan = Plan("eins", "zwei") } });
            Assert.AreEqual(2, s.Agents[0].Plan!.Steps.Count);
        }

        [Test]
        public void AnAgentMessageOrASnapshotEntryWithoutAPlanClearsIt()
        {
            // U5b ruling: the bridge includes `plan` in every agent message and snapshot entry while it knows one
            var s = new AgentStore();
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1", Plan = Plan("eins") } });
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1", LastAction = "neu" } });
            Assert.AreEqual("neu", s.Agents[0].LastAction);
            Assert.IsNull(s.Agents[0].Plan, "an agent message without a plan: the bridge cleared it");
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1", Plan = Plan("zwei") } });
            s.Apply(new SnapshotMessage { Agents = { new AgentState { Sid = "a1" }, new AgentState { Sid = "a2", Plan = Plan("drei") } } });
            Assert.IsNull(s.Agents[0].Plan, "a snapshot entry without a plan clears it too");
            Assert.AreEqual("drei", s.Agents[1].Plan!.Steps[0].Text, "one with a plan keeps its own");
        }

        [Test]
        public void ANewSessionStartsWithoutTheOldPlan()
        {
            var s = new AgentStore();
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1", Plan = Plan("eins") } });
            // the session went (a snapshot without it), then a session with the same id came back: no old plan
            s.Apply(new SnapshotMessage { Agents = { new AgentState { Sid = "a2" } } });
            s.Apply(new AgentMessage { Agent = new AgentState { Sid = "a1" } });
            Assert.IsNull(s.Agents[1].Plan);
        }
    }
}
