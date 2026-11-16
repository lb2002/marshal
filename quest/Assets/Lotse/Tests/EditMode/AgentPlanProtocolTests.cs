#nullable enable
using System.Linq;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    /// <summary>
    /// U6: the optional `plan` on AgentState (contract 2026-11-12-contract-agent-plan.md). Fail soft: a malformed plan is
    /// no plan, never a dropped agent and never an exception; capped again here (12 steps, 80 characters).
    /// </summary>
    public class AgentPlanProtocolTests
    {
        static string Agent(string planJson) =>
            "{\"type\":\"agent\",\"agent\":{\"sid\":\"a1\",\"name\":\"Agent 1\",\"status\":\"working\",\"lastAction\":\"x\",\"since\":0," +
            "\"files\":[],\"waitingMsTotal\":0" + (planJson != null ? ",\"plan\":" + planJson : "") + "}}";

        static AgentState? Parse(string? planJson) => (Protocol.Parse(Agent(planJson!)) as AgentMessage)?.Agent;

        [Test]
        public void APlanIsParsedWithItsStepsMoreAndTime()
        {
            var a = Parse("{\"steps\":[{\"text\":\"Alte Seite löschen\",\"status\":\"done\"},{\"text\":\"Tests\",\"status\":\"in_progress\"}," +
                          "{\"text\":\"PR\",\"status\":\"pending\"}],\"more\":2,\"updatedAt\":1234}");
            Assert.IsNotNull(a);
            Assert.IsNotNull(a!.Plan);
            CollectionAssert.AreEqual(new[] { "Alte Seite löschen", "Tests", "PR" }, a.Plan!.Steps.Select(s => s.Text).ToArray());
            CollectionAssert.AreEqual(new[] { PlanStatus.Done, PlanStatus.InProgress, PlanStatus.Pending }, a.Plan.Steps.Select(s => s.Status).ToArray());
            Assert.AreEqual(2, a.Plan.More);
            Assert.AreEqual(1234, a.Plan.UpdatedAt);
        }

        [Test]
        public void WithoutAPlanTheAgentStillComesAndHasNone()
        {
            var a = Parse(null);
            Assert.IsNotNull(a, "an agent without the field is accepted");
            Assert.IsNull(a!.Plan);
            var n = Parse("null");
            Assert.IsNotNull(n);
            Assert.IsNull(n!.Plan);
        }

        [TestCase("42")]
        [TestCase("\"plan\"")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{\"steps\":{}}")]
        [TestCase("{\"steps\":[]}")]
        [TestCase("{\"steps\":[\"Tests\"]}")]
        [TestCase("{\"steps\":[{\"status\":\"done\"}]}")]
        [TestCase("{\"steps\":[{\"text\":7,\"status\":\"done\"}]}")]
        [TestCase("{\"steps\":[{\"text\":\"   \",\"status\":\"done\"}]}")]
        [TestCase("{\"steps\":[{\"text\":\"a\",\"status\":\"done\"}],\"more\":\"drei\"}")]
        [TestCase("{\"steps\":[{\"text\":\"a\",\"status\":\"done\"}],\"more\":-1}")]
        [TestCase("{\"steps\":[{\"text\":\"a\",\"status\":\"done\"}, null]}")]
        public void AMalformedPlanIsNoPlanAndTheAgentStays(string plan)
        {
            AgentState? a = null;
            Assert.DoesNotThrow(() => a = Parse(plan));
            Assert.IsNotNull(a, "the agent is never dropped for its plan");
            Assert.AreEqual("a1", a!.Sid);
            Assert.IsNull(a.Plan, "a malformed plan means no plan");
        }

        [Test]
        public void AMalformedPlanInASnapshotKeepsTheSnapshotAndItsOtherAgents()
        {
            var json = "{\"type\":\"snapshot\",\"agents\":[{\"sid\":\"a1\",\"name\":\"A\",\"status\":\"working\",\"lastAction\":\"\",\"since\":0,\"files\":[]," +
                       "\"waitingMsTotal\":0,\"plan\":{\"steps\":7}},{\"sid\":\"a2\",\"name\":\"B\",\"status\":\"working\",\"lastAction\":\"\"," +
                       "\"since\":0,\"files\":[],\"waitingMsTotal\":0,\"plan\":{\"steps\":[{\"text\":\"x\",\"status\":\"done\"}],\"more\":0,\"updatedAt\":1}}]," +
                       "\"pending\":[],\"explains\":{}}";
            var s = Protocol.Parse(json) as SnapshotMessage;
            Assert.IsNotNull(s);
            Assert.AreEqual(2, s!.Agents.Count);
            Assert.IsNull(s.Agents[0].Plan);
            Assert.AreEqual(1, s.Agents[1].Plan!.Steps.Count);
        }

        [Test]
        public void AnUnknownOrMissingStatusIsPending()
        {
            var a = Parse("{\"steps\":[{\"text\":\"a\",\"status\":\"completed\"},{\"text\":\"b\",\"status\":\"DONE\"},{\"text\":\"c\"}," +
                          "{\"text\":\"d\",\"status\":3},{\"text\":\"e\",\"status\":\"in_progress\"},{\"text\":\"f\",\"status\":\"in_progress\"}]}");
            CollectionAssert.AreEqual(new[]
            {
                PlanStatus.Pending, PlanStatus.Pending, PlanStatus.Pending, PlanStatus.Pending, PlanStatus.InProgress, PlanStatus.InProgress,
            }, a!.Plan!.Steps.Select(s => s.Status).ToArray(), "only the contract's words; several in progress stay as reported");
            Assert.AreEqual(0, a.Plan.More, "a missing more is 0");
        }

        [Test]
        public void ThePlanIsCappedAgainAtTwelveStepsAndEightyCharacters()
        {
            var steps = string.Join(",", Enumerable.Range(1, 15).Select(i => "{\"text\":\"Schritt " + i + "\",\"status\":\"pending\"}"));
            var a = Parse("{\"steps\":[" + steps + "],\"more\":4,\"updatedAt\":1}");
            Assert.AreEqual(12, a!.Plan!.Steps.Count);
            Assert.AreEqual(7, a.Plan.More, "the three cut here count with the four the bridge cut");
            var longText = new string('x', 200);
            var b = Parse("{\"steps\":[{\"text\":\"  " + longText + "  \",\"status\":\"done\"}]}");
            Assert.AreEqual(80, b!.Plan!.Steps[0].Text.Length);
            StringAssert.EndsWith("…", b.Plan.Steps[0].Text);
            var c = Parse("{\"steps\":[{\"text\":\"  kurz  \",\"status\":\"done\"}]}");
            Assert.AreEqual("kurz", c!.Plan!.Steps[0].Text, "trimmed");
        }

        [Test]
        public void MarkupInAStepStaysPlainText()
        {
            var a = Parse("{\"steps\":[{\"text\":\"<color=red>rot</color> <b>fett</b>\",\"status\":\"pending\"}]}");
            Assert.AreEqual("<color=red>rot</color> <b>fett</b>", a!.Plan!.Steps[0].Text, "kept as characters; the views draw it plain");
        }

        [Test]
        public void APackCarriesPlans()
        {
            var pack = DemoPack.Parse("{\"version\":1,\"durationMs\":10,\"messages\":[{\"t\":0,\"msg\":" + Agent(
                "{\"steps\":[{\"text\":\"a\",\"status\":\"done\"}],\"more\":0,\"updatedAt\":1}") + "}]}");
            var m = pack.Messages[0].Msg as AgentMessage;
            Assert.IsNotNull(m!.Agent.Plan);
        }
    }
}
