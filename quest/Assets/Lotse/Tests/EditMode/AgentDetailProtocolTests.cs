#nullable enable
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    /// <summary>
    /// U2: agent identity and detail on the wire (shared contract 2026-11-12-contract-agent-detail.md):
    /// AgentState.title / project / source, the server's agent_detail, the client's agent_detail_request and the
    /// demo pack's optional top-level `agents`.
    /// </summary>
    public class AgentDetailProtocolTests
    {
        const string AgentWithTitle = "{\"type\":\"agent\",\"agent\":{\"sid\":\"demo-a1\",\"name\":\"Agent 1\",\"source\":\"codex\",\"title\":\"Warenkorb-Seite umbauen\",\"project\":\"shop\",\"status\":\"working\",\"lastAction\":\"Arbeitet\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}";

        const string Detail = "{\"type\":\"agent_detail\",\"sid\":\"demo-a1\",\"title\":\"Warenkorb-Seite umbauen\",\"summary\":\"Baut die Warenkorb-Seite um.\",\"recent\":[\"Bearbeitet src/cart.ts\",\"Liest src/api.ts\"],\"waitingFor\":\"Freigabe für npm install\",\"source\":\"ai\"}";

        [Test]
        public void AnAgentCarriesItsTitleProjectAndSource()
        {
            var m = Protocol.Parse(AgentWithTitle) as AgentMessage;
            Assert.NotNull(m);
            Assert.AreEqual("Warenkorb-Seite umbauen", m!.Agent.Title);
            Assert.AreEqual("shop", m.Agent.Project);
            Assert.AreEqual("codex", m.Agent.Source);
            var copy = m.Agent.Clone();
            Assert.AreEqual("Warenkorb-Seite umbauen", copy.Title);
            Assert.AreEqual("shop", copy.Project);
        }

        [Test]
        public void AnOlderAgentHasNoTitleOrProject()
        {
            var m = Protocol.Parse("{\"type\":\"agent\",\"agent\":{\"sid\":\"a\",\"name\":\"Agent 1\",\"status\":\"working\"}}") as AgentMessage;
            Assert.NotNull(m);
            Assert.IsNull(m!.Agent.Title);
            Assert.IsNull(m.Agent.Project);
            Assert.IsNull(m.Agent.Source);
        }

        [Test]
        public void ParsesAgentDetail()
        {
            var d = Protocol.Parse(Detail) as AgentDetailMessage;
            Assert.NotNull(d);
            Assert.AreEqual("demo-a1", d!.Sid);
            Assert.AreEqual("Warenkorb-Seite umbauen", d.Title);
            Assert.AreEqual("Baut die Warenkorb-Seite um.", d.Summary);
            CollectionAssert.AreEqual(new[] { "Bearbeitet src/cart.ts", "Liest src/api.ts" }, d.Recent);
            Assert.AreEqual("Freigabe für npm install", d.WaitingFor);
            Assert.AreEqual("ai", d.Source);
        }

        [Test]
        public void AgentDetailWithNullWaitingForAndNoRecent()
        {
            var d = Protocol.Parse("{\"type\":\"agent_detail\",\"sid\":\"a\",\"title\":\"t\",\"summary\":\"s\",\"recent\":[],\"waitingFor\":null,\"source\":\"rules\"}") as AgentDetailMessage;
            Assert.NotNull(d);
            Assert.IsNull(d!.WaitingFor);
            Assert.AreEqual(0, d.Recent.Count);
        }

        [TestCase("{\"type\":\"agent_detail\",\"title\":\"t\"}")]
        [TestCase("{\"type\":\"agent_detail\",\"sid\":\"\"}")]
        [TestCase("{\"type\":\"agent_detail\",\"sid\":5}")]
        [TestCase("{\"type\":\"agent_detail\",\"sid\":\"a\",\"recent\":\"not a list\"}")]
        public void AgentDetailWithoutAUsableSidOrShapeIsDropped(string json)
        {
            Assert.IsNull(Protocol.Parse(json));
        }

        [Test]
        public void SerialisesAgentDetailRequest()
        {
            Assert.AreEqual("{\"type\":\"agent_detail_request\",\"sid\":\"demo-a1\"}",
                Protocol.Serialize(new AgentDetailRequestMessage { Sid = "demo-a1" }));
        }

        const string PackHead = "{\"version\":1,\"budgetUsd\":15,\"durationMs\":1000,\"messages\":[{\"t\":0,\"msg\":{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}}]";

        [Test]
        public void APackCarriesStaticAgentDetail()
        {
            var pack = DemoPack.Parse(PackHead + ",\"agents\":{\"demo-a1\":{\"title\":\"Warenkorb\",\"summary\":\"Baut um.\",\"recent\":[\"Bearbeitet src/cart.ts\"],\"waitingFor\":null}}}");
            Assert.IsTrue(pack.Agents.TryGetValue("demo-a1", out var d));
            Assert.AreEqual("demo-a1", d!.Sid);
            Assert.AreEqual("Warenkorb", d.Title);
            Assert.AreEqual("Baut um.", d.Summary);
            CollectionAssert.AreEqual(new[] { "Bearbeitet src/cart.ts" }, d.Recent);
            Assert.IsNull(d.WaitingFor);
        }

        [Test]
        public void APackWithoutAgentsHasNoDetail()
        {
            Assert.AreEqual(0, DemoPack.Parse(PackHead + "}").Agents.Count);
        }

        [Test]
        public void AMalformedPackAgentEntryIsSkippedNotFatal()
        {
            var pack = DemoPack.Parse(PackHead + ",\"agents\":{\"x\":5,\"y\":{\"recent\":7},\"z\":{\"summary\":\"ok\"}}}");
            Assert.IsFalse(pack.Agents.ContainsKey("x"));
            Assert.IsFalse(pack.Agents.ContainsKey("y"));
            Assert.AreEqual("ok", pack.Agents["z"].Summary);
            Assert.AreEqual(1, pack.Messages.Count, "the messages are read as before");
        }
    }
}
