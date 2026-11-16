#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lotse.Core
{
    /// <summary>A patch for one agent, applied when the juror decides.</summary>
    public sealed class Consequence
    {
        public string Sid { get; set; } = "";
        public string Status { get; set; } = "working";
        public string LastAction { get; set; } = "";
    }

    public sealed class PermOutcome
    {
        public Consequence OnAllow { get; set; } = new Consequence();
        public Consequence OnDeny { get; set; } = new Consequence();
    }

    public sealed class PackEntry
    {
        public double T { get; set; }
        public ServerMessage Msg { get; set; } = new SnapshotMessage();
    }

    /// <summary>The offline demo pack (bridge/src/pack.ts).</summary>
    public sealed class DemoPack
    {
        public double BudgetUsd { get; set; }
        public double DurationMs { get; set; }
        /// <summary>Messages of a type a pack does not carry (a newer bridge's, say): skipped, counted for the log.</summary>
        public int SkippedUnknown { get; private set; }
        public List<PackEntry> Messages { get; set; } = new List<PackEntry>();
        public Dictionary<string, PermOutcome> Perms { get; set; } = new Dictionary<string, PermOutcome>();
        /// <summary>Static agent detail from the optional top-level `agents` (the player answers agent_detail_request from it).</summary>
        public Dictionary<string, AgentDetailMessage> Agents { get; set; } = new Dictionary<string, AgentDetailMessage>();
        /// <summary>Canned agent reactions to a reply, by sid, from the optional top-level `replies` (display text only).</summary>
        public Dictionary<string, string> Replies { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Throws on a pack that is not valid JSON or has an unreadable message of a known type (an invalid
        /// perm_open rejects the whole pack: no card with a guessed risk). A message of an unknown type is skipped.
        /// </summary>
        public static DemoPack Parse(string json)
        {
            var o = Protocol.ParseToken(json) as JObject ?? throw new FormatException("pack is not a JSON object");
            var pack = new DemoPack
            {
                BudgetUsd = o.Value<double?>("budgetUsd") ?? 0,
                DurationMs = o.Value<double?>("durationMs") ?? 0,
            };
            var messages = o["messages"] as JArray ?? throw new FormatException("pack has no messages");
            foreach (var m in messages)
            {
                var type = (m["msg"] as JObject)?["type"] is JValue tv && tv.Type == JTokenType.String ? (string?)tv : null;
                if (type != null && !Protocol.IsPackType(type))
                {
                    pack.SkippedUnknown++;
                    continue;
                }
                var msg = Protocol.FromToken(m["msg"]) ?? throw new FormatException("pack message not understood: " + m["msg"]);
                pack.Messages.Add(new PackEntry { T = m.Value<double?>("t") ?? 0, Msg = msg });
            }
            // the player relies on time order; a stable sort keeps same-time messages in file order
            var indexed = new List<KeyValuePair<int, PackEntry>>();
            for (var i = 0; i < pack.Messages.Count; i++) indexed.Add(new KeyValuePair<int, PackEntry>(i, pack.Messages[i]));
            indexed.Sort((a, b) =>
            {
                var c = a.Value.T.CompareTo(b.Value.T);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            pack.Messages = indexed.ConvertAll(kv => kv.Value);

            // optional static detail per agent (display text only): a malformed entry is skipped, never fatal
            if (o["agents"] is JObject agents)
            {
                foreach (var a in agents.Properties())
                {
                    try
                    {
                        var d = a.Value is JObject entry ? Protocol.DetailOf(entry, new JValue(a.Name)) : null;
                        if (d != null) pack.Agents[a.Name] = d;
                    }
                    catch (Exception)
                    {
                        // skipped
                    }
                }
            }

            // optional canned reactions to a reply: a string, or an object with a string `reaction`; others are skipped
            if (o["replies"] is JObject replies)
            {
                foreach (var r in replies.Properties())
                {
                    var reaction = r.Value is JValue v && v.Type == JTokenType.String ? (string?)v
                        : r.Value is JObject ro && ro["reaction"] is JValue rv && rv.Type == JTokenType.String ? (string?)rv
                        : null;
                    if (!string.IsNullOrEmpty(reaction)) pack.Replies[r.Name] = reaction!;
                }
            }

            if (o["perms"] is JObject perms)
            {
                foreach (var p in perms.Properties())
                {
                    pack.Perms[p.Name] = new PermOutcome
                    {
                        OnAllow = p.Value["onAllow"]?.ToObject<Consequence>(Camel) ?? new Consequence(),
                        OnDeny = p.Value["onDeny"]?.ToObject<Consequence>(Camel) ?? new Consequence(),
                    };
                }
            }
            return pack;
        }

        static readonly JsonSerializer Camel = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        });
    }

    /// <summary>
    /// Plays a DemoPack as the message stream the headset would get live, without a PC.
    ///
    /// Clocks. `NowMs` is the sum of all Tick deltas and runs on while paused. The pack timeline does not:
    /// it stops at each perm_open and, on Decide, resumes shifted by the time the juror took, so a request
    /// opened at pack time `createdAt` sits at `createdAt + shift` on the player clock. `Decide(id, allow)`
    /// uses the player's own `NowMs`, so the juror's wait is the time ticked while paused. (The overload
    /// with an explicit time is internal, for tests.)
    ///
    /// `since` lives in pack time throughout, so pack and decided values stay comparable. The timeline
    /// stood still at the request, so a decided agent gets `since` = `createdAt` of its request
    /// (`NowMs - shift` after the shift). `createdAt`, `expiresAt` and incident `t` stay in pack time too.
    ///
    /// Player contract (pack.ts): on Decide, merge only status and lastAction of the consequence into the
    /// agent, set its `since`, add the juror's wait to that agent's juror waits, synthesise perm_closed
    /// (reason "allow" or "deny"), resume. Agent messages leaving the player carry `waitingMsTotal` =
    /// pack value + juror waits, so the store and Stats show the displayed value.
    ///
    /// No expiry: in demo mode a request never times out, the juror sets the pace. This deviates on
    /// purpose from "or the hold expires" in the pack.ts header; `expiresAt` is ignored.
    /// </summary>
    public sealed class PackPlayer
    {
        readonly DemoPack _pack;
        readonly Dictionary<string, AgentState> _agents = new Dictionary<string, AgentState>();
        readonly Dictionary<string, double> _packWaiting = new Dictionary<string, double>();
        readonly Dictionary<string, double> _jurorWaiting = new Dictionary<string, double>();
        readonly List<ServerMessage> _due = new List<ServerMessage>();
        int _next;
        double _shift;
        PermRequest? _pendingRequest;

        public PackPlayer(DemoPack pack)
        {
            _pack = pack;
        }

        public double NowMs { get; private set; }
        public bool IsPaused => _pendingRequest != null;
        public string? PendingId => _pendingRequest?.Id;
        public bool Finished => _pendingRequest == null && _next >= _pack.Messages.Count;

        /// <summary>
        /// The messages due after `dtMs` more time; stops after a perm_open and then stays paused. Runs every
        /// frame, so the list is reused: it is valid until the next Tick.
        /// </summary>
        public IReadOnlyList<ServerMessage> Tick(double dtMs)
        {
            NowMs += dtMs;
            _due.Clear();
            if (IsPaused) return _due;

            while (_next < _pack.Messages.Count && _pack.Messages[_next].T + _shift <= NowMs)
            {
                var entry = _pack.Messages[_next++];
                _due.Add(Emit(entry.Msg));
                if (entry.Msg is PermOpenMessage open)
                {
                    _pendingRequest = open.Request;
                    // its impact follows it at once (the bridge sends it right after perm_open), while the timeline waits
                    while (_next < _pack.Messages.Count && _pack.Messages[_next].Msg is PermImpactMessage pi && pi.Id == open.Request.Id)
                        _due.Add(_pack.Messages[_next++].Msg);
                    break;
                }
            }
            return _due;
        }

        /// <summary>Decides at the player's own clock; empty when `id` is not the pending request.</summary>
        public IReadOnlyList<ServerMessage> Decide(string id, bool allow) => Decide(id, allow, NowMs);

        /// <summary>Test seam: `nowMs` is on the player clock (never earlier than the request opened).</summary>
        internal IReadOnlyList<ServerMessage> Decide(string id, bool allow, double nowMs)
        {
            var req = _pendingRequest;
            var result = new List<ServerMessage>();
            if (req == null || req.Id != id) return result;

            var opened = req.CreatedAt + _shift;
            if (nowMs < opened) nowMs = opened;
            var wait = nowMs - opened;
            _shift += wait;
            if (nowMs > NowMs) NowMs = nowMs;
            _pendingRequest = null;

            if (_pack.Perms.TryGetValue(id, out var outcome))
            {
                var c = allow ? outcome.OnAllow : outcome.OnDeny;
                if (!_agents.TryGetValue(c.Sid, out var agent))
                {
                    agent = new AgentState { Sid = c.Sid, Name = c.Sid };
                    _agents[c.Sid] = agent;
                }
                agent.Status = c.Status;
                agent.LastAction = c.LastAction;
                agent.Since = nowMs - _shift; // pack time: where the timeline resumes
                _jurorWaiting[c.Sid] = JurorWaiting(c.Sid) + wait;
                var shown = agent.Clone();
                shown.WaitingMsTotal = DisplayedWaitingMs(c.Sid);
                result.Add(new AgentMessage { Agent = shown });
            }
            result.Add(new PermClosedMessage { Id = id, Reason = allow ? "allow" : "deny" });
            return result;
        }

        /// <summary>
        /// The agent's reaction to a reply (demo mode): it works again, its last action is the reaction. Null for an
        /// agent the pack has not shown. Does not move the pack timeline.
        /// </summary>
        public AgentMessage? Reply(string sid, string reaction)
        {
            if (!_agents.TryGetValue(sid, out var agent)) return null;
            agent.Status = "working";
            agent.LastAction = reaction;
            agent.Since = NowMs - _shift; // pack time, as for a decision
            var shown = agent.Clone();
            shown.WaitingMsTotal = DisplayedWaitingMs(sid);
            return new AgentMessage { Agent = shown };
        }

        /// <summary>Pack value of the latest agent message plus the juror's own waits.</summary>
        public double DisplayedWaitingMs(string sid) =>
            (_packWaiting.TryGetValue(sid, out var p) ? p : 0) + JurorWaiting(sid);

        double JurorWaiting(string sid) => _jurorWaiting.TryGetValue(sid, out var w) ? w : 0;

        ServerMessage Emit(ServerMessage msg)
        {
            if (!(msg is AgentMessage am)) return msg;
            var a = am.Agent;
            _packWaiting[a.Sid] = a.WaitingMsTotal;
            _agents[a.Sid] = a.Clone();
            var shown = a.Clone();
            shown.WaitingMsTotal = DisplayedWaitingMs(a.Sid);
            return new AgentMessage { Agent = shown };
        }
    }
}
