#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// Text from the bridge or the pack is untrusted. Every field is capped on the client (the same limits the
    /// bridge sets for commands, paths and tools, so a wrong or hostile bridge cannot flood the card) and its
    /// control, format and bidi characters are shown instead of acting: line breaks as "¶", a tab as "→", any
    /// other as "‹U+XXXX›" (a right-to-left override cannot reorder a command, a zero-width space cannot hide in
    /// a path). A cut ends in "…". Idempotent: capping a capped text changes nothing.
    /// A request whose command, path(s), URL or tool had to be cut is marked Truncated, so it needs the two-hand
    /// hold: what the user cannot see in full is never approved with one hand.
    /// </summary>
    public static class Untrusted
    {
        public const int CommandMax = 300;
        public const int PathMax = 300;
        public const int UrlMax = 300;
        public const int ToolMax = 40;
        public const int MaxPaths = 5;
        public const int ExplainMax = 140;
        public const int ReplyMax = 70;
        public const int NameMax = 40;
        /// <summary>Agent detail (shared contract 2026-11-12): what an agent does, its project, its summary and actions.</summary>
        public const int TitleMax = 40;
        public const int ProjectMax = 30;
        public const int SummaryMax = 280;
        public const int RecentMax = 5;
        public const int RecentItemMax = 80;
        public const int WaitingForMax = 140;
        /// <summary>Live feed (contract 2026-11-12-contract-live-feed.md): a label, a text, a tool name, an id.</summary>
        public const int FeedLabelMax = 40;
        public const int FeedTextMax = 400;
        public const int FeedToolMax = 40;
        public const int FeedIdMax = 120;
        /// <summary>At most this many entries are taken from one feed frame (the bridge sends at most 30).</summary>
        public const int FeedEntriesMax = 60;

        const string Return = "¶";
        const string Tab = "→";
        const string Ellipsis = "…";

        /// <summary>The text with every control, format and bidi character made visible (see the class summary).</summary>
        public static string Visible(string text) => Visible(text, keepLineFeeds: false);

        /// <summary>
        /// As Visible, but a line feed stays a line break: for a whole display string whose untrusted parts are
        /// already visible and whose line breaks are our own (Texts.Plain).
        /// </summary>
        internal static string Visible(string text, bool keepLineFeeds)
        {
            if (!NeedsWork(text)) return text;
            var sb = new StringBuilder(text.Length + 16);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\n' && keepLineFeeds)
                {
                    sb.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '\n':
                    case '\r':
                    case '\v':
                    case '\u2028':
                    case '\u2029':
                        sb.Append(Return);
                        continue;
                    case '\t':
                        sb.Append(Tab);
                        continue;
                }
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    var cp = char.ConvertToUtf32(c, text[i + 1]);
                    if (Hidden(CharUnicodeInfo.GetUnicodeCategory(text, i))) sb.Append(Code(cp));
                    else sb.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }
                if (Hidden(CharUnicodeInfo.GetUnicodeCategory(c))) sb.Append(Code(c));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The last part of an already visible text, at most `max` characters, starting with "…" when cut.</summary>
        public static string Tail(string visible, int max)
        {
            if (visible.Length <= max) return visible;
            var start = visible.Length - (max - 1);
            if (start < visible.Length && char.IsLowSurrogate(visible[start])) start++; // never half a surrogate pair
            // never inside a "‹U+XXXX›" marker: start after its end
            var open = visible.LastIndexOf(CodeOpen[0], start - 1 < 0 ? 0 : start - 1);
            if (open >= 0)
            {
                var close = visible.IndexOf(CodeClose[0], open);
                if (close >= start) start = close + 1;
            }
            return Ellipsis + visible.Substring(start);
        }

        /// <summary>Visible, then at most `max` characters; a cut ends in "…" (counted in `max`).</summary>
        public static string Clamp(string? text, int max) => Clamp(text, max, out _);

        public static string Clamp(string? text, int max, out bool cut)
        {
            var v = Visible(text ?? "");
            cut = v.Length > max;
            if (!cut) return v;
            var end = max - 1;
            if (end > 0 && char.IsHighSurrogate(v[end - 1])) end--; // never half a surrogate pair
            return v.Substring(0, end) + Ellipsis;
        }

        /// <summary>Caps the request's display fields in place; true (and Truncated set) when anything shown was cut.</summary>
        public static bool Request(PermRequest req)
        {
            var cut = false;
            if (req.Command != null) req.Command = Field(req.Command, CommandMax, ref cut);
            if (req.Path != null) req.Path = Field(req.Path, PathMax, ref cut);
            if (req.Paths.Count > MaxPaths)
            {
                req.Paths.RemoveRange(MaxPaths, req.Paths.Count - MaxPaths);
                cut = true;
            }
            for (var i = 0; i < req.Paths.Count; i++) req.Paths[i] = Field(req.Paths[i], PathMax, ref cut);
            if (req.Url != null) req.Url = Field(req.Url, UrlMax, ref cut);
            req.Tool = Field(req.Tool, ToolMax, ref cut);
            req.Risk.Reason = Clamp(req.Risk.Reason, ExplainMax);
            if (cut) req.Truncated = true;
            return cut;
        }

        public static void Explain(Explanation e)
        {
            e.Text = Clamp(e.Text, ExplainMax);
            if (e.AiText != null) e.AiText = Clamp(e.AiText, ExplainMax);
            for (var i = 0; i < e.Replies.Count; i++) e.Replies[i] = Clamp(e.Replies[i], ReplyMax);
        }

        public static void Agent(AgentState a)
        {
            a.Name = Clamp(a.Name, NameMax);
            a.LastAction = Clamp(a.LastAction, ExplainMax);
            if (a.Title != null) a.Title = Clamp(a.Title, TitleMax);
            if (a.Project != null) a.Project = Clamp(a.Project, ProjectMax);
            if (a.Source != null) a.Source = Clamp(a.Source, NameMax);
            if (a.Plan != null) Plan(a.Plan);
        }

        /// <summary>A plan: at most 12 steps (the rest counts into `more`), each text plain, its hidden characters shown, at most 80.</summary>
        public static void Plan(AgentPlan p)
        {
            if (p.Steps.Count > AgentPlan.MaxSteps)
            {
                p.More += p.Steps.Count - AgentPlan.MaxSteps;
                p.Steps.RemoveRange(AgentPlan.MaxSteps, p.Steps.Count - AgentPlan.MaxSteps);
            }
            if (p.More < 0) p.More = 0;
            foreach (var s in p.Steps) s.Text = Clamp(s.Text, AgentPlan.MaxText);
        }

        public static void Incident(Incident i) => i.Detail = Clamp(i.Detail, ExplainMax);

        /// <summary>Caps an agent detail in place: title 40, summary 280, at most 5 actions of 80, waitingFor 140.</summary>
        public static void Detail(AgentDetailMessage d)
        {
            d.Title = Clamp(d.Title, TitleMax);
            d.Summary = Clamp(d.Summary, SummaryMax);
            if (d.Recent.Count > RecentMax) d.Recent.RemoveRange(RecentMax, d.Recent.Count - RecentMax);
            for (var i = 0; i < d.Recent.Count; i++) d.Recent[i] = Clamp(d.Recent[i], RecentItemMax);
            d.WaitingFor = string.IsNullOrEmpty(d.WaitingFor) ? null : Clamp(d.WaitingFor, WaitingForMax);
            d.Source = Clamp(d.Source, NameMax);
        }

        /// <summary>Caps everything a message would put on screen, in place (the session calls it on arrival).</summary>
        public static void Message(ServerMessage msg)
        {
            switch (msg)
            {
                case SnapshotMessage s:
                    foreach (var a in s.Agents) Agent(a);
                    foreach (var p in s.Pending) Request(p);
                    foreach (var e in s.Explains.Values) Explain(e);
                    foreach (var i in s.Impacts.Values) Impact(i);
                    break;
                case AgentMessage a:
                    Agent(a.Agent);
                    break;
                case IncidentMessage i:
                    Incident(i.Incident);
                    break;
                case PermOpenMessage o:
                    Request(o.Request);
                    Explain(o.Explain);
                    break;
                case PermExplainMessage x:
                    Explain(x.Explain);
                    break;
                case PermImpactMessage pi:
                    Impact(pi.Impact);
                    break;
                case AgentDetailMessage d:
                    Detail(d);
                    break;
                case FeedSnapshotMessage fs:
                    Feed(fs.Entries);
                    break;
                case FeedAppendMessage fa:
                    Feed(fa.Entries);
                    break;
            }
        }

        /// <summary>An impact's two texts: capped and their hidden characters shown (display only).</summary>
        public static void Impact(Impact i)
        {
            if (i.Target != null) i.Target = Clamp(i.Target, Lotse.Core.Impact.TextMax);
            i.Reason = Clamp(i.Reason, Lotse.Core.Impact.TextMax);
        }

        /// <summary>Feed entries: at most FeedEntriesMax (the newest), every field capped and its hidden characters shown.</summary>
        public static void Feed(List<FeedEntry> entries)
        {
            if (entries.Count > FeedEntriesMax) entries.RemoveRange(0, entries.Count - FeedEntriesMax);
            foreach (var e in entries)
            {
                e.Id = Clamp(e.Id, FeedIdMax);
                e.Label = Clamp(e.Label, FeedLabelMax);
                e.Text = Clamp(e.Text, FeedTextMax);
                if (e.Tool != null) e.Tool = Clamp(e.Tool, FeedToolMax);
            }
        }

        static string Field(string text, int max, ref bool cut)
        {
            var s = Clamp(text, max, out var c);
            cut |= c;
            return s;
        }

        static bool Hidden(UnicodeCategory cat) =>
            cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.LineSeparator ||
            cat == UnicodeCategory.ParagraphSeparator || cat == UnicodeCategory.Surrogate;

        const string CodeOpen = "‹";
        const string CodeClose = "›";

        /// <summary>Every character this class writes in place of an invisible one (the font must have each).</summary>
        public const string MarkerCharacters = Return + Tab + Ellipsis + CodeOpen + CodeClose;

        static string Code(int codePoint) => CodeOpen + "U+" + codePoint.ToString(codePoint > 0xFFFF ? "X5" : "X4") + CodeClose;

        static bool NeedsWork(string text)
        {
            foreach (var c in text)
                if (c < ' ' || c >= '\u007F') return true; // only ASCII printables need no look
            return false;
        }
    }
}
