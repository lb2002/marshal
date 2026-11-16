#nullable enable
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.Tests
{
    public class StatsTests
    {
        static PermRequest Req(string id, string risk, bool truncated = false) => new PermRequest
        {
            Id = id, Truncated = truncated, Risk = new RiskResult { Level = risk },
        };

        [Test]
        public void DenyingDestructiveRequestsCountsThem()
        {
            var s = new Stats();
            s.RecordDecision(Req("p11", "destructive"), false);
            s.RecordDecision(Req("p14", "destructive"), false);
            Assert.AreEqual(2, s.DangerousStopped);
        }

        [Test]
        public void AllowingDenyingSafeAndAllowingDestructiveDoNotCount()
        {
            var s = new Stats();
            s.RecordDecision(Req("p1", "safe"), true);
            s.RecordDecision(Req("p2", "safe"), false);
            s.RecordDecision(Req("p3", "network"), false);
            s.RecordDecision(Req("p4", "destructive"), true);
            Assert.AreEqual(0, s.DangerousStopped);
        }

        [Test]
        public void SameRequestCountedOnce()
        {
            var s = new Stats();
            s.RecordDecision(Req("p11", "destructive"), false);
            s.RecordDecision(Req("p11", "destructive"), false);
            Assert.AreEqual(1, s.DangerousStopped);
        }

        [Test]
        public void TotalWaitingSumsAgents()
        {
            var agents = new[]
            {
                new AgentState { Sid = "a", WaitingMsTotal = 1500 },
                new AgentState { Sid = "b", WaitingMsTotal = 2500 },
            };
            Assert.AreEqual(4000, Stats.TotalWaitingMs(agents));
            Assert.AreEqual(0, Stats.TotalWaitingMs(new AgentState[0]));
        }
    }
}
