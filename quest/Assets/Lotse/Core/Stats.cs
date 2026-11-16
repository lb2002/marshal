#nullable enable
using System.Collections.Generic;

namespace Lotse.Core
{
    /// <summary>Numbers for the end-of-session summary.</summary>
    public sealed class Stats
    {
        readonly HashSet<string> _stopped = new HashSet<string>();

        /// <summary>Destructive requests the juror denied (each request counted once).</summary>
        public int DangerousStopped => _stopped.Count;

        public void RecordDecision(PermRequest request, bool allow)
        {
            if (!allow && request.Risk.IsDestructive) _stopped.Add(request.Id);
        }

        /// <summary>Waiting time over all agents, as displayed (the agents' waitingMsTotal).</summary>
        public static double TotalWaitingMs(IEnumerable<AgentState> agents)
        {
            double sum = 0;
            foreach (var a in agents) sum += a.WaitingMsTotal;
            return sum;
        }
    }
}
