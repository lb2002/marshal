#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>What one incident puts on screen (U5a): one muted line, and the agents it is about.</summary>
    public readonly struct IncidentShow
    {
        /// <summary>The line: the incident's detail, plain and capped.</summary>
        public readonly string Line;
        /// <summary>The agents it is about, each once, in order.</summary>
        public readonly IReadOnlyList<string> Sids;

        public IncidentShow(string line, IReadOnlyList<string> sids)
        {
            Line = line;
            Sids = sids;
        }
    }

    /// <summary>
    /// Incidents from the bridge (bridge/src/incidents.ts): "collision" (two agents edit the same file), "budget_near"
    /// and "budget". U5a: they are only shown, as one muted line at the top of each affected agent's conversation and as
    /// the notice above the window. They never decide anything. Pure.
    /// </summary>
    public static class IncidentLook
    {
        public static IncidentShow For(Incident incident)
        {
            var sids = new List<string>();
            foreach (var sid in incident.Sids)
                if (!string.IsNullOrEmpty(sid) && !sids.Contains(sid)) sids.Add(sid);
            return new IncidentShow(Untrusted.Clamp(incident.Detail, Untrusted.ExplainMax), sids);
        }
    }
}
