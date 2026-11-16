#nullable enable
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    public class ProtocolTests
    {
        // Samples copied from demo/jury.pack.json (message stream of the real bridge pipeline).
        const string Snapshot = "{\"type\":\"snapshot\",\"agents\":[{\"sid\":\"demo-a1\",\"name\":\"Agent 1\",\"status\":\"working\",\"lastAction\":\"Arbeitet\",\"since\":0,\"ctxPct\":12,\"costUsd\":0.5,\"files\":[\"src/a.ts\"],\"waitingMsTotal\":0}],"
            + "\"pending\":[{\"id\":\"p1\",\"sid\":\"demo-a1\",\"tool\":\"Edit\",\"truncated\":false,\"path\":\"<repo>/src/components/Cart.tsx\",\"paths\":[\"<repo>/src/components/Cart.tsx\"],\"risk\":{\"risk\":\"safe\",\"reason\":\"Unbedenklich\"},\"createdAt\":24000,\"expiresAt\":114000}],"
            + "\"explains\":{\"p1\":{\"text\":\"Claude ändert die Datei: src/components/Cart.tsx\",\"replies\":[\"Nicht jetzt.\"],\"source\":\"rules\"}}}";

        const string Agent = "{\"type\":\"agent\",\"agent\":{\"sid\":\"demo-a2\",\"name\":\"Agent 2\",\"status\":\"working\",\"lastAction\":\"Sitzung gestartet\",\"since\":700,\"files\":[],\"waitingMsTotal\":0}}";

        const string Incident = "{\"type\":\"incident\",\"incident\":{\"kind\":\"collision\",\"sids\":[\"demo-a4\",\"demo-a2\"],\"detail\":\"Agent 4 und Agent 2 bearbeiten lib/pricing.ts\",\"t\":309000}}";

        const string PermOpen = "{\"type\":\"perm_open\",\"request\":{\"id\":\"p11\",\"sid\":\"demo-a3\",\"tool\":\"Bash\",\"command\":\"rm -rf ../legacy-site\",\"commandLength\":21,\"truncated\":false,\"paths\":[],\"risk\":{\"risk\":\"destructive\",\"reason\":\"Löscht Dateien rekursiv\"},\"createdAt\":150000,\"expiresAt\":240000},"
            + "\"explain\":{\"text\":\"Claude will den Ordner legacy-site löschen.\",\"replies\":[\"Nicht löschen.\",\"Verschiebe den Ordner.\"],\"source\":\"manual\"}}";

        const string PermExplain = "{\"type\":\"perm_explain\",\"id\":\"p11\",\"explain\":{\"text\":\"Regeltext\",\"aiText\":\"KI-Satz\",\"replies\":[\"A\"],\"source\":\"ai\"}}";
        const string PermClosed = "{\"type\":\"perm_closed\",\"id\":\"p11\",\"reason\":\"deny\"}";
        const string Paired = "{\"type\":\"paired\",\"session\":\"s-1\"}";
        const string Error = "{\"type\":\"error\",\"error\":\"bad code\"}";

        [Test]
        public void ParsesSnapshot()
        {
            var m = Protocol.Parse(Snapshot) as SnapshotMessage;
            Assert.NotNull(m);
            Assert.AreEqual(1, m!.Agents.Count);
            Assert.AreEqual("demo-a1", m.Agents[0].Sid);
            Assert.AreEqual(12, m.Agents[0].CtxPct);
            Assert.AreEqual(0.5, m.Agents[0].CostUsd);
            Assert.AreEqual("src/a.ts", m.Agents[0].Files[0]);
            Assert.AreEqual("p1", m.Pending[0].Id);
            Assert.AreEqual("safe", m.Pending[0].Risk.Level);
            Assert.AreEqual(114000, m.Pending[0].ExpiresAt);
            Assert.AreEqual("rules", m.Explains["p1"].Source);
        }

        [Test]
        public void ParsesAgent()
        {
            var m = Protocol.Parse(Agent) as AgentMessage;
            Assert.NotNull(m);
            Assert.AreEqual("Agent 2", m!.Agent.Name);
            Assert.AreEqual(700, m.Agent.Since);
            Assert.IsNull(m.Agent.CtxPct);
            Assert.IsNull(m.Agent.CostUsd);
        }

        [Test]
        public void ParsesIncident()
        {
            var m = Protocol.Parse(Incident) as IncidentMessage;
            Assert.NotNull(m);
            Assert.AreEqual("collision", m!.Incident.Kind);
            CollectionAssert.AreEqual(new[] { "demo-a4", "demo-a2" }, m.Incident.Sids);
            Assert.AreEqual(309000, m.Incident.T);
        }

        [Test]
        public void ParsesPermOpen()
        {
            var m = Protocol.Parse(PermOpen) as PermOpenMessage;
            Assert.NotNull(m);
            Assert.AreEqual("p11", m!.Request.Id);
            Assert.AreEqual("rm -rf ../legacy-site", m.Request.Command);
            Assert.AreEqual(21, m.Request.CommandLength);
            Assert.IsTrue(m.Request.Risk.IsDestructive);
            Assert.IsTrue(m.Request.NeedsTwoHand);
            Assert.AreEqual("manual", m.Explain.Source);
            Assert.AreEqual(2, m.Explain.Replies.Count);
        }

        [Test]
        public void DateLikeStringsStayVerbatim()
        {
            const string json = "{\"type\":\"perm_open\",\"request\":{\"id\":\"2026-11-11T10:00:00Z\",\"sid\":\"s\",\"tool\":\"Bash\"," +
                                "\"command\":\"2026-11-11T10:00:00.000+01:00\",\"truncated\":false,\"paths\":[],\"risk\":{\"risk\":\"safe\",\"reason\":\"r\"}," +
                                "\"createdAt\":1,\"expiresAt\":2},\"explain\":{\"text\":\"2026-01-02T03:04:05Z\",\"replies\":[],\"source\":\"rules\"}}";
            var m = Protocol.Parse(json) as PermOpenMessage;
            Assert.NotNull(m);
            Assert.AreEqual("2026-11-11T10:00:00Z", m!.Request.Id);
            Assert.AreEqual("2026-11-11T10:00:00.000+01:00", m.Request.Command);
            Assert.AreEqual("2026-01-02T03:04:05Z", m.Explain.Text);

            var closed = Protocol.Parse("{\"type\":\"perm_closed\",\"id\":\"2026-11-11T10:00:00Z\",\"reason\":\"allow\"}") as PermClosedMessage;
            Assert.AreEqual("2026-11-11T10:00:00Z", closed!.Id);

            var pack = DemoPack.Parse("{\"messages\":[{\"t\":0,\"msg\":" + json + "}]}");
            Assert.AreEqual("2026-11-11T10:00:00.000+01:00", ((PermOpenMessage)pack.Messages[0].Msg).Request.Command);
        }

        [Test]
        public void ParsesPermExplainWithAiText()
        {
            var m = Protocol.Parse(PermExplain) as PermExplainMessage;
            Assert.NotNull(m);
            Assert.AreEqual("p11", m!.Id);
            Assert.AreEqual("KI-Satz", m.Explain.AiText);
            Assert.AreEqual("ai", m.Explain.Source);
        }

        [Test]
        public void ParsesPermClosedPairedAndError()
        {
            var c = (PermClosedMessage)Protocol.Parse(PermClosed)!;
            Assert.AreEqual("p11", c.Id);
            Assert.AreEqual("deny", c.Reason);
            Assert.AreEqual("s-1", ((PairedMessage)Protocol.Parse(Paired)!).Session);
            Assert.AreEqual("bad code", ((ErrorMessage)Protocol.Parse(Error)!).Error);
        }

        [Test]
        public void ParsesPong()
        {
            Assert.IsInstanceOf<PongMessage>(Protocol.Parse("{\"type\":\"pong\"}"));
        }

        [Test]
        public void TruncatedRequestNeedsTwoHand()
        {
            var m = (PermOpenMessage)Protocol.Parse(PermOpen.Replace("\"truncated\":false", "\"truncated\":true").Replace("destructive", "network"))!;
            Assert.IsFalse(m.Request.Risk.IsDestructive);
            Assert.IsTrue(m.Request.NeedsTwoHand);
        }

        [TestCase("{\"type\":\"nope\"}")]
        [TestCase("not json at all")]
        [TestCase("")]
        [TestCase("[1,2]")]
        [TestCase("null")]
        [TestCase("{\"foo\":1}")]
        [TestCase("{\"type\":\"perm_open\"}")]
        [TestCase("{\"type\":\"agent\",\"agent\":5}")]
        [TestCase("{\"type\":\"perm_closed\"}")]
        [TestCase("{\"type\":\"snapshot\",\"agents\":\"x\"")]
        public void UnknownOrMalformedFrameGivesNull(string json)
        {
            Assert.IsNull(Protocol.Parse(json));
        }

        [Test]
        public void SerialisesHelloWithCode()
        {
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"123456\"}", Protocol.Serialize(new HelloMessage { Code = "123456" }));
        }

        [Test]
        public void SerialisesHelloWithSession()
        {
            Assert.AreEqual("{\"type\":\"hello\",\"session\":\"abc\"}", Protocol.Serialize(new HelloMessage { Session = "abc" }));
        }

        [Test]
        public void SerialisingAHelloWithNeitherCodeNorSessionThrows()
        {
            Assert.Throws<System.ArgumentException>(() => Protocol.Serialize(new HelloMessage()));
        }

        [Test]
        public void SerialisesAllowWithConfirm()
        {
            var json = Protocol.Serialize(new PermDecisionMessage { Decision = ClientDecision.Allow("p11", "two-hand") });
            Assert.AreEqual("{\"type\":\"perm_decision\",\"decision\":{\"id\":\"p11\",\"behavior\":\"allow\",\"confirm\":\"two-hand\"}}", json);
        }

        [Test]
        public void SerialisesDenyWithMessage()
        {
            var json = Protocol.Serialize(new PermDecisionMessage { Decision = ClientDecision.Deny("p11", "Nicht löschen.") });
            Assert.AreEqual("{\"type\":\"perm_decision\",\"decision\":{\"id\":\"p11\",\"behavior\":\"deny\",\"message\":\"Nicht löschen.\"}}", json);
        }

        [Test]
        public void SerialisesDenyWithoutMessage()
        {
            var json = Protocol.Serialize(new PermDecisionMessage { Decision = ClientDecision.Deny("p1") });
            Assert.AreEqual("{\"type\":\"perm_decision\",\"decision\":{\"id\":\"p1\",\"behavior\":\"deny\"}}", json);
        }

        // ---- fail closed on risk ----

        const string RiskJson = "\"risk\":{\"risk\":\"destructive\",\"reason\":\"Löscht Dateien rekursiv\"},";

        [Test]
        public void UnknownRiskLevelNeedsTwoHand()
        {
            var m = (PermOpenMessage)Protocol.Parse(PermOpen.Replace("\"destructive\"", "\"critical\""))!;
            Assert.AreEqual("critical", m.Request.Risk.Level);
            Assert.IsTrue(m.Request.NeedsTwoHand);
        }

        [TestCase("safe", false)]
        [TestCase("network", false)]
        [TestCase("destructive", true)]
        [TestCase("", true)]
        [TestCase("SAFE", true)]
        public void NeedsTwoHandOnlyForKnownNonDestructiveLevels(string level, bool expected)
        {
            var r = new PermRequest { Risk = new RiskResult { Level = level } };
            Assert.AreEqual(expected, r.NeedsTwoHand);
        }

        [Test]
        public void RequestWithoutAnyRiskObjectNeedsTwoHand()
        {
            Assert.IsTrue(new PermRequest().NeedsTwoHand);
            Assert.IsTrue(new PermRequest { Risk = new RiskResult() }.NeedsTwoHand);
        }

        [Test]
        public void PermOpenWithoutRiskIsRejected()
        {
            Assert.IsNull(Protocol.Parse(PermOpen.Replace(RiskJson, "")));
        }

        [Test]
        public void PermOpenWithNullRiskIsRejected()
        {
            Assert.IsNull(Protocol.Parse(PermOpen.Replace(RiskJson, "\"risk\":null,")));
        }

        [Test]
        public void PermOpenWithRiskObjectWithoutLevelIsRejected()
        {
            Assert.IsNull(Protocol.Parse(PermOpen.Replace(RiskJson, "\"risk\":{},")));
        }

        [Test]
        public void PermOpenWithEmptyRequestIsRejected()
        {
            Assert.IsNull(Protocol.Parse("{\"type\":\"perm_open\",\"request\":{},\"explain\":{\"text\":\"t\",\"replies\":[],\"source\":\"rules\"}}"));
        }

        [Test]
        public void PermOpenWithoutIdIsRejected()
        {
            Assert.IsNull(Protocol.Parse(PermOpen.Replace("\"id\":\"p11\",", "")));
        }

        [Test]
        public void SnapshotWithAPendingRequestWithoutRiskOrIdIsRejected()
        {
            Assert.IsNull(Protocol.Parse(Snapshot.Replace("\"risk\":{\"risk\":\"safe\",\"reason\":\"Unbedenklich\"},", "")));
            Assert.IsNull(Protocol.Parse(Snapshot.Replace("{\"id\":\"p1\",\"sid\"", "{\"sid\"")));
        }
        [TestCase("")]
        [TestCase("\"truncated\":\"no\",")]
        [TestCase("\"truncated\":null,")]
        [TestCase("\"truncated\":0,")]
        public void PermOpenNeedsABooleanTruncated(string truncated)
        {
            Assert.IsNotNull(Protocol.Parse(PermOpen));
            Assert.IsNull(Protocol.Parse(PermOpen.Replace("\"truncated\":false,", truncated)));
        }

        [Test]
        public void SnapshotWithAPendingRequestWithoutTruncatedIsRejected()
        {
            Assert.IsNotNull(Protocol.Parse(Snapshot));
            Assert.IsNull(Protocol.Parse(Snapshot.Replace("\"truncated\":false,", "")));
        }
    }
}
