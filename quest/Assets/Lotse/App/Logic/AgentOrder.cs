#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>The three groups of the agent list, in display order.</summary>
    public enum AgentGroup { NeedsYou = 0, Working = 1, Finished = 2 }

    /// <summary>
    /// The order of the workspace's agent list (U5a): agents that need the user first (an open request, or waiting for
    /// an answer), oldest first; then the working ones; then the finished ones (done or failed). Each agent keeps its
    /// place inside its group: the order changes only when an agent changes group (it then goes to the end of its new
    /// group, which for "needs you" is the newest request), never on an event that leaves its group as it was, so rows
    /// do not jump. Requests that open in the same update keep the bridge's open order. Pure.
    /// </summary>
    public sealed class AgentOrder
    {
        readonly Dictionary<string, (AgentGroup group, long seq)> _entered = new Dictionary<string, (AgentGroup, long)>();
        readonly List<string> _order = new List<string>();
        readonly HashSet<string> _seen = new HashSet<string>();
        readonly HashSet<string> _asking = new HashSet<string>();
        readonly List<string> _gone = new List<string>();
        long _seq;

        /// <summary>The sids in display order, as of the last Update.</summary>
        public IReadOnlyList<string> Sids => _order;

        /// <summary>Counts the changes of the order (a row moved, came or went).</summary>
        public int Version { get; private set; }

        /// <summary>The group an agent belongs in: an open request or "blocked"/"waiting" need the user.</summary>
        public static AgentGroup GroupOf(AgentState agent, bool hasRequest)
        {
            if (hasRequest || agent.Status == "blocked" || agent.Status == "waiting") return AgentGroup.NeedsYou;
            if (agent.Status == "done" || agent.Status == "failed") return AgentGroup.Finished;
            return AgentGroup.Working;
        }

        public AgentGroup GroupOf(string sid) => _entered.TryGetValue(sid, out var e) ? e.group : AgentGroup.Working;

        /// <param name="pending">Open requests, oldest first (AgentStore.Pending).</param>
        /// <param name="keepPlaces">
        /// (U14) A row stands open (or still moves): the agents on screen keep their places whatever group they
        /// change to, so the open row never jumps under the user (an approved request used to send its row, still
        /// showing "Cleared", to the end of the list). Agents that come or go are still added and dropped. The changes
        /// of group are taken up by the first update without it.
        /// </param>
        /// <returns>True when the order changed.</returns>
        public bool Update(IReadOnlyList<AgentState> agents, IReadOnlyList<PermRequest> pending, bool keepPlaces = false)
        {
            _asking.Clear();
            for (var i = 0; i < pending.Count; i++) _asking.Add(pending[i].Sid);
            _seen.Clear();
            for (var i = 0; i < agents.Count; i++) _seen.Add(agents[i].Sid);
            var changed = false;

            // agents that come to need the user, in the order of their oldest request
            for (var i = 0; i < pending.Count; i++)
            {
                var sid = pending[i].Sid;
                if (!_seen.Contains(sid)) continue;
                changed |= Enter(sid, AgentGroup.NeedsYou, keepPlaces);
            }
            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                changed |= Enter(a.Sid, GroupOf(a, _asking.Contains(a.Sid)), keepPlaces);
            }
            _gone.Clear();
            foreach (var sid in _entered.Keys)
                if (!_seen.Contains(sid)) _gone.Add(sid);
            foreach (var sid in _gone) _entered.Remove(sid);
            changed |= _gone.Count > 0;
            if (!changed) return false;

            _order.Clear();
            foreach (var sid in _entered.Keys) _order.Add(sid);
            _order.Sort((a, b) =>
            {
                var ea = _entered[a];
                var eb = _entered[b];
                var g = ea.group.CompareTo(eb.group);
                return g != 0 ? g : ea.seq.CompareTo(eb.seq);
            });
            Version++;
            return true;
        }

        bool Enter(string sid, AgentGroup group, bool keepPlaces)
        {
            if (_entered.TryGetValue(sid, out var e) && (e.group == group || keepPlaces)) return false;
            _entered[sid] = (group, _seq++);
            return true;
        }

        public void Clear()
        {
            _entered.Clear();
            _order.Clear();
            Version++;
        }
    }
}
