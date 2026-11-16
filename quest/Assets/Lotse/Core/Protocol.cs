#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Lotse.Core
{
    // Wire types of bridge/src/protocol.ts (JSON text frames over WebSocket).

    public sealed class RiskResult
    {
        /// <summary>"safe", "network" or "destructive". No default: a missing level is not safe.</summary>
        [JsonProperty("risk")] public string? Level { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; } = "";
        [JsonIgnore] public bool IsDestructive => Level == "destructive";
        /// <summary>Only the two levels the bridge calls cheap to approve; anything else fails closed.</summary>
        [JsonIgnore] public bool IsLowRisk => Level == "safe" || Level == "network";
    }

    public sealed class AgentState
    {
        public string Sid { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The coding agent behind the session: "claude", "muse", "codex" or "gemini" (untrusted; null from an older bridge).</summary>
        public string? Source { get; set; }
        /// <summary>What the agent is doing, at most 40 characters, plain text, untrusted; null when not known.</summary>
        public string? Title { get; set; }
        /// <summary>The basename of the session's working directory, at most 30 characters, untrusted; null when not known.</summary>
        public string? Project { get; set; }
        public string Status { get; set; } = "working";
        public string LastAction { get; set; } = "";
        public double Since { get; set; }
        public double? CtxPct { get; set; }
        public double? CostUsd { get; set; }
        public List<string> Files { get; set; } = new List<string>();
        public double WaitingMsTotal { get; set; }
        /// <summary>
        /// The agent's live to-do list (contract 2026-11-12-contract-agent-plan.md), or null when none is known. Parsed by
        /// hand (Protocol.PlanOf), fail soft: a malformed plan is null. Untrusted display text only: it never affects risk,
        /// gestures or decisions.
        /// </summary>
        [JsonIgnore] public AgentPlan? Plan { get; set; }

        public AgentState Clone()
        {
            var c = (AgentState)MemberwiseClone();
            c.Files = new List<string>(Files);
            c.Plan = Plan?.Clone();
            return c;
        }
    }

    /// <summary>A plan step's state; an unknown word on the wire is Pending.</summary>
    public enum PlanStatus { Pending, InProgress, Done }

    public sealed class PlanStep
    {
        /// <summary>Plain text, at most 80 characters, trimmed; untrusted.</summary>
        public string Text { get; set; } = "";
        public PlanStatus Status { get; set; }
    }

    /// <summary>An agent's plan: at most 12 steps, `More` steps cut beyond them, `UpdatedAt` in ms (the bridge's clock).</summary>
    public sealed class AgentPlan
    {
        public const int MaxSteps = 12;
        public const int MaxText = 80;

        public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
        public int More { get; set; }
        public double UpdatedAt { get; set; }

        public AgentPlan Clone()
        {
            var c = new AgentPlan { More = More, UpdatedAt = UpdatedAt };
            foreach (var s in Steps) c.Steps.Add(new PlanStep { Text = s.Text, Status = s.Status });
            return c;
        }
    }

    public sealed class Incident
    {
        /// <summary>"collision", "budget_near" or "budget".</summary>
        public string Kind { get; set; } = "";
        public List<string> Sids { get; set; } = new List<string>();
        public string Detail { get; set; } = "";
        public double T { get; set; }
    }

    public sealed class PermRequest
    {
        public string Id { get; set; } = "";
        public string Sid { get; set; } = "";
        public string Tool { get; set; } = "";
        public string? Command { get; set; }
        public int? CommandLength { get; set; }
        public bool Truncated { get; set; }
        public string? Path { get; set; }
        public List<string> Paths { get; set; } = new List<string>();
        public string? Url { get; set; }
        public RiskResult Risk { get; set; } = new RiskResult();
        public double CreatedAt { get; set; }
        public double ExpiresAt { get; set; }

        /// <summary>Fails closed: truncated, destructive, unknown or missing risk all need the two-hand hold.</summary>
        [JsonIgnore] public bool NeedsTwoHand => Truncated || !Risk.IsLowRisk;
    }

    /// <summary>
    /// What a dangerous request would hit (contract 2026-11-12-contract-impact-halt.md A), worked out by the bridge
    /// without running anything. Display text only, untrusted, capped: it never changes the risk, the gesture or what
    /// approving needs. `Recoverable` is "no" or "unknown" (anything else on the wire reads "unknown"): the bridge only
    /// states losses it is sure of, never that something is safe.
    /// </summary>
    public sealed class Impact
    {
        public const int TextMax = 120;

        public string Kind { get; set; } = "other";
        public string? Target { get; set; }
        public long? Files { get; set; }
        public bool FilesCapped { get; set; }
        public double? Bytes { get; set; }
        public bool? OutsideProject { get; set; }
        public string Recoverable { get; set; } = "unknown";
        public string Reason { get; set; } = "";
    }

    public sealed class PermImpactMessage : ServerMessage
    {
        public string Id { get; set; } = "";
        public Impact Impact { get; set; } = new Impact();
    }

    public sealed class Explanation
    {
        public string Text { get; set; } = "";
        public string? AiText { get; set; }
        public List<string> Replies { get; set; } = new List<string>();
        /// <summary>"rules", "ai" or "manual".</summary>
        public string Source { get; set; } = "rules";
    }

    public sealed class ClientDecision
    {
        public string Id { get; }
        /// <summary>"allow" or "deny".</summary>
        public string Behavior { get; }
        public string? Message { get; }
        /// <summary>"pinch-hold" or "two-hand".</summary>
        public string? Confirm { get; }

        ClientDecision(string id, string behavior, string? message, string? confirm)
        {
            Id = id;
            Behavior = behavior;
            Message = message;
            Confirm = confirm;
        }

        /// <param name="confirm">"pinch-hold" or "two-hand": the gesture that confirmed it.</param>
        public static ClientDecision Allow(string id, string confirm) => new ClientDecision(id, "allow", null, confirm);

        /// <param name="message">One of the explanation's replies, or null for a plain deny.</param>
        public static ClientDecision Deny(string id, string? message = null) => new ClientDecision(id, "deny", message, null);
    }

    public abstract class ServerMessage { }

    public sealed class SnapshotMessage : ServerMessage
    {
        public List<AgentState> Agents { get; set; } = new List<AgentState>();
        public List<PermRequest> Pending { get; set; } = new List<PermRequest>();
        public Dictionary<string, Explanation> Explains { get; set; } = new Dictionary<string, Explanation>();
        /// <summary>The bridge's halt state (contract 2026-11-12-contract-impact-halt.md B); null when it sent none (not halted).</summary>
        public HaltStateMessage? Halt { get; set; }
        /// <summary>The impacts of open requests by id (contract A); a malformed one is left out (no impact).</summary>
        public Dictionary<string, Impact> Impacts { get; set; } = new Dictionary<string, Impact>();
    }

    /// <summary>
    /// The bridge's halt state (`halt_state`, and `snapshot.halt`): whether every agent is halted, since when (ms, the
    /// bridge's clock) and, per source that has sessions, what the bridge can enforce ("every-step", "requests-only",
    /// "none"; anything else reads "none"). The headset shows halted only from this, never from its own guess.
    /// </summary>
    public sealed class HaltStateMessage : ServerMessage
    {
        public bool On { get; set; }
        public double? Since { get; set; }
        public Dictionary<string, string> Enforcement { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Halt every agent (on) or release them (off); accepted by the bridge only from the paired socket.</summary>
    public sealed class HaltMessage : ClientMessage
    {
        public bool On { get; set; }
    }

    public sealed class AgentMessage : ServerMessage
    {
        public AgentState Agent { get; set; } = new AgentState();
    }

    public sealed class IncidentMessage : ServerMessage
    {
        public Incident Incident { get; set; } = new Incident();
    }

    public sealed class PermOpenMessage : ServerMessage
    {
        public PermRequest Request { get; set; } = new PermRequest();
        public Explanation Explain { get; set; } = new Explanation();
    }

    public sealed class PermExplainMessage : ServerMessage
    {
        public string Id { get; set; } = "";
        public Explanation Explain { get; set; } = new Explanation();
    }

    public sealed class PermClosedMessage : ServerMessage
    {
        public string Id { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class PairedMessage : ServerMessage
    {
        public string Session { get; set; } = "";
    }

    /// <summary>The bridge's answer to a client `ping` (application-level liveness check).</summary>
    public sealed class PongMessage : ServerMessage
    {
    }

    public sealed class ErrorMessage : ServerMessage
    {
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// The bridge's answer to agent_detail_request (or the demo pack's static `agents` entry): what one agent is
    /// doing, in plain language. Display text only, untrusted: it never affects risk, gestures or decisions.
    /// </summary>
    public sealed class AgentDetailMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        /// <summary>The latest actions, newest first ("Bearbeitet src/cart.ts").</summary>
        public List<string> Recent { get; set; } = new List<string>();
        /// <summary>What the agent waits for, in plain language, or null.</summary>
        public string? WaitingFor { get; set; }
        /// <summary>"ai" or "rules" (who wrote the summary).</summary>
        public string Source { get; set; } = "rules";
    }

    /// <summary>
    /// The bridge's answer to agent_reply: `delivered` is true when the agent's reply listener was waiting and got the
    /// text, false when none was (the agent is not waiting, or not a Claude Code session).
    /// </summary>
    public sealed class AgentReplyAckMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
        public bool Delivered { get; set; }
    }

    /// <summary>
    /// One entry of an agent's live feed (contract 2026-11-12-contract-live-feed.md): what it was asked, thought,
    /// said or did. `Id` is stable per entry (the client de-duplicates by it); `T` is ms since the epoch; `Kind` one of
    /// prompt, thinking, text, tool, result, error, system; `Label` at most 40 characters ("denkt", "liest", "Befehl");
    /// `Text` at most 400 characters, redacted by the bridge. Untrusted display text only: it never affects risk,
    /// gestures or decisions, and it is never logged.
    /// </summary>
    public sealed class FeedEntry
    {
        public string Id { get; set; } = "";
        public double T { get; set; }
        public string Kind { get; set; } = "system";
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
        public string? Tool { get; set; }
    }

    /// <summary>The answer to feed_subscribe: the last at most 30 entries of one agent, oldest first.</summary>
    public sealed class FeedSnapshotMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
    }

    /// <summary>New entries of a subscribed agent, oldest first.</summary>
    public sealed class FeedAppendMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
    }

    public abstract class ClientMessage { }

    /// <summary>Starts the live feed of one agent (answered with feed_snapshot, then feed_append).</summary>
    public sealed class FeedSubscribeMessage : ClientMessage
    {
        public string Sid { get; set; } = "";
    }

    /// <summary>Ends the live feed of one agent (no answer).</summary>
    public sealed class FeedUnsubscribeMessage : ClientMessage
    {
        public string Sid { get; set; } = "";
    }

    /// <summary>The user's own reply to an agent, typed or dictated on the paired headset (never generated).</summary>
    public sealed class AgentReplyMessage : ClientMessage
    {
        public string Sid { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class HelloMessage : ClientMessage
    {
        public string? Code { get; set; }
        public string? Session { get; set; }
    }

    public sealed class PermDecisionMessage : ClientMessage
    {
        public ClientDecision Decision { get; set; } = ClientDecision.Deny("");
    }

    /// <summary>Asks the bridge for one agent's detail; answered with agent_detail (rate-limited per sid by the bridge).</summary>
    public sealed class AgentDetailRequestMessage : ClientMessage
    {
        public string Sid { get; set; } = "";
    }

    public static class Protocol
    {
        static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false },
            },
            NullValueHandling = NullValueHandling.Ignore,
        });

        /// <summary>Parses one server frame. An unknown type or a malformed frame gives null; never throws.</summary>
        public static ServerMessage? Parse(string json)
        {
            try
            {
                return FromToken(ParseToken(json));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// JSON text to a token with every string kept as written: no date parsing, so a command, an id or an
        /// explanation that looks like a date is never turned into a DateTime and printed back differently.
        /// </summary>
        internal static JToken ParseToken(string json)
        {
            using (var reader = new JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("additional text after the JSON value");
                return token;
            }
        }

        /// <summary>The message types a demo pack carries (bridge/src/pack.ts); anything else in a pack is skipped.</summary>
        internal static bool IsPackType(string type) =>
            type == "snapshot" || type == "agent" || type == "incident" || type == "perm_open" || type == "perm_explain" ||
            type == "perm_closed" || type == "perm_impact";

        /// <summary>Same as Parse for an already parsed token (used by the demo pack loader).</summary>
        internal static ServerMessage? FromToken(JToken? token)
        {
            try
            {
                if (!(token is JObject o)) return null;
                var type = o.Value<string>("type");
                switch (type)
                {
                    case "snapshot":
                        return new SnapshotMessage
                        {
                            Agents = AgentsOf(o["agents"]),
                            Pending = PendingOf(o["pending"]),
                            Explains = o["explains"] is JObject ex
                                ? ex.ToObject<Dictionary<string, Explanation>>(Serializer) ?? new Dictionary<string, Explanation>()
                                : new Dictionary<string, Explanation>(),
                            Halt = HaltOf(o["halt"]),
                            Impacts = ImpactsOf(o["impacts"]),
                        };
                    case "perm_impact":
                    {
                        if (!(o["id"] is JValue iv && iv.Type == JTokenType.String && ((string?)iv)?.Length > 0)) return null;
                        var impact = ImpactOf(o["impact"]);
                        return impact != null ? new PermImpactMessage { Id = (string)iv!, Impact = impact } : null;
                    }
                    case "halt_state":
                        return HaltOf(o);
                    case "agent":
                    {
                        var a = AgentOf(o["agent"]);
                        return a != null ? new AgentMessage { Agent = a } : null;
                    }
                    case "incident":
                    {
                        var i = Required<Incident>(o["incident"]);
                        return i != null ? new IncidentMessage { Incident = i } : null;
                    }
                    case "perm_open":
                    {
                        var req = RequestOf(o["request"]);
                        var exp = Required<Explanation>(o["explain"]);
                        return req != null && exp != null ? new PermOpenMessage { Request = req, Explain = exp } : null;
                    }
                    case "perm_explain":
                    {
                        var id = o.Value<string>("id");
                        var exp = Required<Explanation>(o["explain"]);
                        return id != null && exp != null ? new PermExplainMessage { Id = id, Explain = exp } : null;
                    }
                    case "perm_closed":
                    {
                        var id = o.Value<string>("id");
                        return id != null ? new PermClosedMessage { Id = id, Reason = o.Value<string>("reason") ?? "" } : null;
                    }
                    case "agent_detail":
                        return DetailOf(o, o["sid"]);
                    case "agent_reply_ack":
                    {
                        // a missing or non-boolean `delivered` is never read as delivered: the frame is dropped
                        if (!(o["sid"] is JValue sv && sv.Type == JTokenType.String && ((string?)sv)?.Length > 0)) return null;
                        if (!(o["delivered"] is JValue dv && dv.Type == JTokenType.Boolean)) return null;
                        return new AgentReplyAckMessage { Sid = (string)sv!, Delivered = (bool)dv };
                    }
                    case "feed_snapshot":
                    case "feed_append":
                    {
                        if (!(o["sid"] is JValue fs && fs.Type == JTokenType.String && ((string?)fs)?.Length > 0)) return null;
                        if (!(o["entries"] is JArray ea)) return null;
                        var entries = EntriesOf(ea);
                        return type == "feed_snapshot"
                            ? new FeedSnapshotMessage { Sid = (string)fs!, Entries = entries }
                            : (ServerMessage)new FeedAppendMessage { Sid = (string)fs!, Entries = entries };
                    }
                    case "paired":
                        return new PairedMessage { Session = o.Value<string>("session") ?? "" };
                    case "pong":
                        return new PongMessage();
                    case "error":
                        return new ErrorMessage { Error = o.Value<string>("error") ?? "" };
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static T? Required<T>(JToken? t) where T : class => t is JObject ? t.ToObject<T>(Serializer) : null;

        /// <summary>An agent with its plan (parsed apart: a malformed plan never drops the agent).</summary>
        static AgentState? AgentOf(JToken? t)
        {
            var a = Required<AgentState>(t);
            if (a != null) a.Plan = PlanOf(((JObject)t!)["plan"]);
            return a;
        }

        static List<AgentState> AgentsOf(JToken? t)
        {
            var list = new List<AgentState>();
            if (!(t is JArray arr)) return list;
            foreach (var item in arr)
            {
                // as before: an unreadable agent makes the snapshot unreadable; its plan never does
                var a = AgentOf(item) ?? throw new FormatException("invalid agent");
                list.Add(a);
            }
            return list;
        }

        /// <summary>
        /// `plan` per the contract, or null. Fail soft: anything malformed (not an object, no step list or an empty one, a
        /// step that is not an object with a non-blank string text, a `more` that is not a non-negative number) is no
        /// plan. A status other than the contract's three words is "pending". Capped again: 12 steps (the rest counts
        /// into `more`), each text trimmed and at most 80 characters (cut with "…"). Never throws.
        /// </summary>
        internal static AgentPlan? PlanOf(JToken? t)
        {
            try
            {
                if (!(t is JObject p) || !(p["steps"] is JArray steps) || steps.Count == 0) return null;
                var plan = new AgentPlan();
                var more = p["more"];
                if (more != null && more.Type != JTokenType.Null)
                {
                    if (more.Type != JTokenType.Integer && more.Type != JTokenType.Float) return null;
                    var m = (double)more;
                    if (!(m >= 0) || double.IsInfinity(m)) return null;
                    plan.More = m > 1e6 ? 1000000 : (int)m;
                }
                var at = p["updatedAt"];
                plan.UpdatedAt = at != null && (at.Type == JTokenType.Integer || at.Type == JTokenType.Float) ? (double)at : 0;
                foreach (var item in steps)
                {
                    if (!(item is JObject st) || !(st["text"] is JValue tv) || tv.Type != JTokenType.String) return null;
                    var text = ((string?)tv ?? "").Trim();
                    if (text.Length == 0) return null;
                    if (plan.Steps.Count >= AgentPlan.MaxSteps)
                    {
                        plan.More++;
                        continue;
                    }
                    plan.Steps.Add(new PlanStep { Text = CapStep(text), Status = StatusOf(st["status"]) });
                }
                return plan;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static readonly HashSet<string> ImpactKinds = new HashSet<string> { "delete", "overwrite", "force_push", "reset", "clean", "other" };

        /// <summary>
        /// An impact per the contract, or null when it is not an object (no impact). Fail soft field by field: a field of
        /// the wrong shape is left out; an unknown kind is "other"; `recoverable` other than "no" is "unknown"; counts must
        /// be non-negative numbers; target and reason are cut to 120 characters (display only). Never throws.
        /// </summary>
        internal static Impact? ImpactOf(JToken? t)
        {
            try
            {
                if (!(t is JObject o)) return null;
                var i = new Impact();
                var kind = Str(o["kind"]);
                i.Kind = kind != null && ImpactKinds.Contains(kind) ? kind : "other";
                var target = Str(o["target"]);
                if (!string.IsNullOrWhiteSpace(target)) i.Target = Cut(target!, Impact.TextMax);
                var files = Num(o["files"]);
                if (files.HasValue) i.Files = files.Value > 1e12 ? 1000000000000L : (long)files.Value;
                i.FilesCapped = o["filesCapped"] is JValue fc && fc.Type == JTokenType.Boolean && (bool)fc;
                i.Bytes = Num(o["bytes"]);
                if (o["outsideProject"] is JValue op && op.Type == JTokenType.Boolean) i.OutsideProject = (bool)op;
                i.Recoverable = Str(o["recoverable"]) == "no" ? "no" : "unknown";
                i.Reason = Cut(Str(o["reason"]) ?? "", Impact.TextMax);
                return i;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Dictionary<string, Impact> ImpactsOf(JToken? t)
        {
            var d = new Dictionary<string, Impact>();
            if (!(t is JObject o)) return d;
            foreach (var p in o.Properties())
            {
                var i = p.Name.Length > 0 ? ImpactOf(p.Value) : null;
                if (i != null) d[p.Name] = i;
            }
            return d;
        }

        static string? Str(JToken? t) => t is JValue v && v.Type == JTokenType.String ? (string?)v : null;

        static double? Num(JToken? t)
        {
            if (!(t is JValue v) || (v.Type != JTokenType.Integer && v.Type != JTokenType.Float)) return null;
            var d = (double)v;
            return d >= 0 && !double.IsInfinity(d) ? Math.Floor(d) : (double?)null;
        }

        /// <summary>At most `max` characters, cut with "…" (never half a surrogate pair).</summary>
        static string Cut(string text, int max)
        {
            if (text.Length <= max) return text;
            var end = max - 1;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            return text.Substring(0, end) + "…";
        }

        /// <summary>At most this many sources are kept from `enforcement` (the bridge knows four).</summary>
        public const int MaxEnforcementSources = 8;

        /// <summary>
        /// A halt state: it needs a boolean `on`, else null (dropped: a guess is never shown as the bridge's state).
        /// `since` only when a number; `enforcement` only string values under string keys, an unknown word reads "none",
        /// at most 8 sources. Never throws.
        /// </summary>
        internal static HaltStateMessage? HaltOf(JToken? t)
        {
            try
            {
                if (!(t is JObject h) || !(h["on"] is JValue on && on.Type == JTokenType.Boolean)) return null;
                var msg = new HaltStateMessage { On = (bool)on };
                if (h["since"] is JValue s && (s.Type == JTokenType.Integer || s.Type == JTokenType.Float)) msg.Since = (double)s;
                if (h["enforcement"] is JObject e)
                    foreach (var p in e.Properties())
                    {
                        if (msg.Enforcement.Count >= MaxEnforcementSources) break;
                        if (p.Name.Length == 0 || !(p.Value is JValue v) || v.Type != JTokenType.String) continue;
                        var word = (string?)v;
                        msg.Enforcement[p.Name] = word == "every-step" || word == "requests-only" ? word! : "none";
                    }
                return msg;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static PlanStatus StatusOf(JToken? t)
        {
            if (!(t is JValue v) || v.Type != JTokenType.String) return PlanStatus.Pending;
            switch ((string?)v)
            {
                case "done": return PlanStatus.Done;
                case "in_progress": return PlanStatus.InProgress;
                default: return PlanStatus.Pending;
            }
        }

        /// <summary>At most 80 characters, cut with "…" (never half a surrogate pair).</summary>
        internal static string CapStep(string text)
        {
            if (text.Length <= AgentPlan.MaxText) return text;
            var end = AgentPlan.MaxText - 1;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            return text.Substring(0, end) + "…";
        }

        static readonly HashSet<string> FeedKinds = new HashSet<string> { "prompt", "thinking", "text", "tool", "result", "error", "system" };

        /// <summary>
        /// Feed entries: one needs a non-empty string id and string text, label and kind; a malformed one is skipped
        /// (never the whole frame); an unknown kind reads as "system".
        /// </summary>
        static List<FeedEntry> EntriesOf(JArray arr)
        {
            var list = new List<FeedEntry>();
            foreach (var item in arr)
            {
                if (!(item is JObject e)) continue;
                if (!(e["id"] is JValue idv && idv.Type == JTokenType.String && ((string?)idv)?.Length > 0)) continue;
                if (!(e["text"] is JValue tv && tv.Type == JTokenType.String)) continue;
                var label = e["label"] is JValue lv && lv.Type == JTokenType.String ? (string?)lv ?? "" : "";
                var kind = e["kind"] is JValue kv && kv.Type == JTokenType.String ? (string?)kv ?? "" : "";
                var t = e["t"] is JValue tt && (tt.Type == JTokenType.Integer || tt.Type == JTokenType.Float) ? (double)tt : 0.0;
                var tool = e["tool"] is JValue tl && tl.Type == JTokenType.String ? (string?)tl : null;
                list.Add(new FeedEntry
                {
                    Id = (string)idv!, T = t, Kind = FeedKinds.Contains(kind) ? kind : "system", Label = label, Text = (string)tv!,
                    Tool = tool,
                });
            }
            return list;
        }

        /// <summary>
        /// An agent detail (a server frame, or a demo pack's `agents` entry with its sid as the key): it needs a
        /// non-empty string sid; the rest is display text. A field of the wrong shape drops the whole detail.
        /// </summary>
        internal static AgentDetailMessage? DetailOf(JObject o, JToken? sid)
        {
            if (!(sid is JValue sv && sv.Type == JTokenType.String && ((string?)sv)?.Length > 0)) return null;
            if (o["recent"] != null && o["recent"]!.Type != JTokenType.Array && o["recent"]!.Type != JTokenType.Null) return null;
            var d = o.ToObject<AgentDetailMessage>(Serializer);
            if (d == null) return null;
            d.Sid = (string)sv!;
            if (d.Recent == null) d.Recent = new List<string>();
            d.Recent.RemoveAll(r => r == null);
            return d;
        }

        /// <summary>
        /// A request needs a string id, a risk object with a level and a boolean `truncated` (the bridge always
        /// sends it), or it is rejected (fail closed: a missing flag must not read as "not truncated").
        /// </summary>
        static PermRequest? RequestOf(JToken? t)
        {
            if (!(t is JObject r)) return null;
            if (!(r["id"] is JValue idv && idv.Type == JTokenType.String && ((string?)idv)?.Length > 0)) return null;
            if (!(r["risk"] is JObject risk) || !(risk["risk"] is JValue lv && lv.Type == JTokenType.String)) return null;
            if (!(r["truncated"] is JValue tv && tv.Type == JTokenType.Boolean)) return null;
            return r.ToObject<PermRequest>(Serializer);
        }

        /// <summary>One invalid entry makes the whole snapshot invalid: better no state than a card with a guessed risk.</summary>
        static List<PermRequest> PendingOf(JToken? t)
        {
            var list = new List<PermRequest>();
            if (!(t is JArray arr)) return list;
            foreach (var item in arr)
                list.Add(RequestOf(item) ?? throw new FormatException("invalid pending request"));
            return list;
        }

        static List<T> ListOf<T>(JToken? t) =>
            t is JArray ? t.ToObject<List<T>>(Serializer) ?? new List<T>() : new List<T>();

        /// <summary>Serialises a client frame in the shape bridge parseClientMessage accepts.</summary>
        public static string Serialize(ClientMessage msg)
        {
            var o = new JObject();
            switch (msg)
            {
                case HelloMessage h:
                    if (h.Code == null && h.Session == null) throw new ArgumentException("hello needs a code or a session", nameof(msg));
                    o["type"] = "hello";
                    if (h.Code != null) o["code"] = h.Code;
                    if (h.Session != null) o["session"] = h.Session;
                    break;
                case PermDecisionMessage p:
                    o["type"] = "perm_decision";
                    var d = new JObject { ["id"] = p.Decision.Id, ["behavior"] = p.Decision.Behavior };
                    if (p.Decision.Confirm != null) d["confirm"] = p.Decision.Confirm;
                    if (p.Decision.Message != null) d["message"] = p.Decision.Message;
                    o["decision"] = d;
                    break;
                case AgentDetailRequestMessage r:
                    o["type"] = "agent_detail_request";
                    o["sid"] = r.Sid;
                    break;
                case FeedSubscribeMessage fs:
                    o["type"] = "feed_subscribe";
                    o["sid"] = fs.Sid;
                    break;
                case FeedUnsubscribeMessage fu:
                    o["type"] = "feed_unsubscribe";
                    o["sid"] = fu.Sid;
                    break;
                case HaltMessage halt:
                    o["type"] = "halt";
                    o["on"] = halt.On;
                    break;
                case AgentReplyMessage reply:
                    o["type"] = "agent_reply";
                    o["sid"] = reply.Sid;
                    o["text"] = reply.Text;
                    break;
                default:
                    throw new ArgumentException("unknown client message", nameof(msg));
            }
            return o.ToString(Formatting.None);
        }
    }
}
