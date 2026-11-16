#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.Core
{
    public enum StoreChangeKind { Snapshot, Agent, PermOpened, PermExplain, PermClosed, Incident, PermImpact }

    public readonly struct StoreChange
    {
        public readonly StoreChangeKind Kind;
        /// <summary>Agent sid or request id; empty for Snapshot and Incident.</summary>
        public readonly string Id;

        public StoreChange(StoreChangeKind kind, string id)
        {
            Kind = kind;
            Id = id;
        }
    }

    /// <summary>The headset's view of the bridge state, built by applying ServerMessages. Each agent's plan is the one its latest message carries (none: cleared, U5b).</summary>
    public sealed class AgentStore
    {
        public const int MaxIncidents = 50;
        const string RepoPrefix = "<repo>/";

        readonly List<AgentState> _agents = new List<AgentState>();
        readonly List<PermRequest> _pending = new List<PermRequest>();
        readonly Dictionary<string, Explanation> _explains = new Dictionary<string, Explanation>();
        readonly List<Incident> _incidents = new List<Incident>();

        public IReadOnlyList<AgentState> Agents => _agents;
        /// <summary>In open order, oldest first.</summary>
        public IReadOnlyList<PermRequest> Pending => _pending;
        public IReadOnlyList<Incident> Incidents => _incidents;

        public event Action<StoreChange>? Changed;

        public Explanation? ExplainFor(string id) => _explains.TryGetValue(id, out var e) ? e : null;

        readonly Dictionary<string, Impact> _impacts = new Dictionary<string, Impact>();

        /// <summary>What the open request `id` would hit, as the bridge worked it out; null when nothing is known (U7).</summary>
        public Impact? ImpactFor(string id) => _impacts.TryGetValue(id, out var i) ? i : null;

        /// <summary>Strips a leading "&lt;repo&gt;/" from a path; for free text use DisplayText.</summary>
        public static string DisplayPath(string path) =>
            path.StartsWith(RepoPrefix, StringComparison.Ordinal) ? path.Substring(RepoPrefix.Length) : path;

        /// <summary>Replaces every "&lt;repo&gt;/" in free text (a command, an explanation).</summary>
        public static string DisplayText(string text) => text.Replace(RepoPrefix, "");

        /// <summary>A snapshot replaces agents and pending requests but keeps incidents: snapshots carry none.</summary>
        public void Apply(ServerMessage msg)
        {
            switch (msg)
            {
                case SnapshotMessage s:
                    // the bridge sends `plan` in every snapshot entry while it knows one: an entry without it has none
                    _agents.Clear();
                    _agents.AddRange(s.Agents);
                    _pending.Clear();
                    _pending.AddRange(s.Pending);
                    _explains.Clear();
                    _impacts.Clear();
                    foreach (var p in s.Pending)
                    {
                        if (s.Explains.TryGetValue(p.Id, out var e)) _explains[p.Id] = e;
                        if (s.Impacts.TryGetValue(p.Id, out var im)) _impacts[p.Id] = im;
                    }
                    Raise(StoreChangeKind.Snapshot, "");
                    break;

                case AgentMessage a:
                {
                    var i = _agents.FindIndex(x => x.Sid == a.Agent.Sid);
                    // the bridge sends `plan` in every agent message while it knows one: a message without it clears it
                    if (i >= 0) _agents[i] = a.Agent;
                    else _agents.Add(a.Agent);
                    Raise(StoreChangeKind.Agent, a.Agent.Sid);
                    break;
                }

                case PermOpenMessage o:
                {
                    var i = _pending.FindIndex(x => x.Id == o.Request.Id);
                    if (i >= 0) _pending[i] = o.Request;
                    else _pending.Add(o.Request);
                    _explains[o.Request.Id] = o.Explain;
                    Raise(StoreChangeKind.PermOpened, o.Request.Id);
                    break;
                }

                case PermExplainMessage x:
                    // a late explanation for a request that is already closed is dropped
                    if (_pending.Exists(p => p.Id == x.Id))
                    {
                        _explains[x.Id] = x.Explain;
                        Raise(StoreChangeKind.PermExplain, x.Id);
                    }
                    break;

                case PermImpactMessage pi:
                    // only while the request is still open
                    if (_pending.Exists(p => p.Id == pi.Id))
                    {
                        _impacts[pi.Id] = pi.Impact;
                        Raise(StoreChangeKind.PermImpact, pi.Id);
                    }
                    break;

                case PermClosedMessage c:
                    if (_pending.RemoveAll(p => p.Id == c.Id) > 0)
                    {
                        _explains.Remove(c.Id);
                        _impacts.Remove(c.Id);
                        Raise(StoreChangeKind.PermClosed, c.Id);
                    }
                    break;

                case IncidentMessage im:
                    _incidents.Add(im.Incident);
                    if (_incidents.Count > MaxIncidents) _incidents.RemoveRange(0, _incidents.Count - MaxIncidents);
                    Raise(StoreChangeKind.Incident, "");
                    break;

                // paired / error are connection concerns, not store state
            }
        }

        void Raise(StoreChangeKind kind, string id) => Changed?.Invoke(new StoreChange(kind, id));
    }
}
