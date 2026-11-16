#nullable enable
using System.IO;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.Tests
{
    /// <summary>
    /// U3: replying to an agent on the wire (shared contract 2026-11-12-contract-agent-reply.md): the client's
    /// agent_reply, the bridge's agent_reply_ack, the demo pack's optional canned `replies` and the player's reaction.
    /// </summary>
    public class AgentReplyProtocolTests
    {
        [Test]
        public void SerialisesAgentReply()
        {
            Assert.AreEqual("{\"type\":\"agent_reply\",\"sid\":\"demo-a1\",\"text\":\"Bitte\\nweiter\"}",
                Protocol.Serialize(new AgentReplyMessage { Sid = "demo-a1", Text = "Bitte\nweiter" }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParsesTheAck(bool delivered)
        {
            var m = Protocol.Parse("{\"type\":\"agent_reply_ack\",\"sid\":\"s1\",\"delivered\":" + (delivered ? "true" : "false") + "}") as AgentReplyAckMessage;
            Assert.NotNull(m);
            Assert.AreEqual("s1", m!.Sid);
            Assert.AreEqual(delivered, m.Delivered);
        }

        [TestCase("{\"type\":\"agent_reply_ack\",\"sid\":\"s1\"}")]
        [TestCase("{\"type\":\"agent_reply_ack\",\"sid\":\"s1\",\"delivered\":\"true\"}")]
        [TestCase("{\"type\":\"agent_reply_ack\",\"delivered\":true}")]
        [TestCase("{\"type\":\"agent_reply_ack\",\"sid\":\"\",\"delivered\":true}")]
        public void AnAckWithoutABooleanDeliveredOrASidIsDropped(string json)
        {
            Assert.IsNull(Protocol.Parse(json), "never read a missing flag as delivered");
        }

        const string PackHead = "{\"version\":1,\"budgetUsd\":15,\"durationMs\":1000,\"messages\":[" +
                                "{\"t\":0,\"msg\":{\"type\":\"agent\",\"agent\":{\"sid\":\"demo-a1\",\"name\":\"Agent 1\",\"status\":\"done\",\"lastAction\":\"Fertig\",\"since\":0,\"files\":[],\"waitingMsTotal\":1200}}}]";

        [Test]
        public void APackCarriesCannedReactions()
        {
            var pack = DemoPack.Parse(PackHead + ",\"replies\":{\"demo-a1\":\"Alles klar, ich passe das an.\",\"demo-a2\":{\"reaction\":\"Mache ich.\"},\"x\":5}}");
            Assert.AreEqual("Alles klar, ich passe das an.", pack.Replies["demo-a1"]);
            Assert.AreEqual("Mache ich.", pack.Replies["demo-a2"]);
            Assert.IsFalse(pack.Replies.ContainsKey("x"), "a malformed entry is skipped");
            Assert.AreEqual(0, DemoPack.Parse(PackHead + "}").Replies.Count);
        }

        [Test]
        public void ThePlayerReactsToAReplyTheAgentWorksAgain()
        {
            var player = new PackPlayer(DemoPack.Parse(PackHead + "}"));
            player.Tick(10);
            var m = player.Reply("demo-a1", "Verstanden.");
            Assert.NotNull(m);
            Assert.AreEqual("working", m!.Agent.Status);
            Assert.AreEqual("Verstanden.", m.Agent.LastAction);
            Assert.AreEqual("Agent 1", m.Agent.Name);
            Assert.AreEqual(1200, m.Agent.WaitingMsTotal, "its waiting time is kept");
            Assert.IsNull(player.Reply("demo-a9", "x"), "an unknown agent gets no reaction");
        }

        [Test]
        public void TheJuryPackStillParses()
        {
            var pack = DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json")));
            Assert.Greater(pack.Messages.Count, 100);
        }
    }
}
