#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// The live feed offline (U4b): no session log exists for the recorded demo, so the feed is derived from the pack.
    /// The snapshot: the task ("Deine Aufgabe", the agent's title), a first thought, the recent actions from the pack's
    /// `agents` (oldest first) and the summary as what the agent says. While subscribed, every new action of the agent
    /// in the recording appends an entry, and so does a reply the user sends. Ids are stable: the snapshot's are the
    /// same every time ("demo-a1-s3"), the appends' count on and never repeat ("demo-a1-7").
    /// Our own German words around the pack's text. Pure.
    /// </summary>
    public sealed class DemoFeed
    {
        public const string FirstThought = "Reads the code first and plans the steps.";
        public const string NextThought = "Works out the next step.";

        readonly Dictionary<string, int> _next = new Dictionary<string, int>();
        readonly Dictionary<string, string> _lastAction = new Dictionary<string, string>();

        /// <summary>The snapshot for `sid` from the pack's detail and the agent as it is now (either may be missing).</summary>
        public List<FeedEntry> Snapshot(string sid, AgentDetailMessage? detail, AgentState? agent)
        {
            var list = new List<FeedEntry>();
            var title = !string.IsNullOrWhiteSpace(detail?.Title) ? detail!.Title : agent?.Title;
            if (!string.IsNullOrWhiteSpace(title)) list.Add(Entry("prompt", "Your task", title!));
            list.Add(Entry("thinking", "thinks", FirstThought));
            if (detail != null)
                for (var i = detail.Recent.Count - 1; i >= 0; i--) list.Add(ForAction(detail.Recent[i]));
            if (!string.IsNullOrWhiteSpace(detail?.Summary)) list.Add(Entry("text", "says", detail!.Summary));
            for (var i = 0; i < list.Count; i++) list[i].Id = sid + "-s" + i;
            if (agent != null && !_lastAction.ContainsKey(sid)) _lastAction[sid] = agent.LastAction ?? "";
            return list;
        }

        /// <summary>The entry for an agent's newest action, or null when it did not change (or says nothing).</summary>
        public FeedEntry? Update(AgentState agent)
        {
            var action = agent.LastAction ?? "";
            if (_lastAction.TryGetValue(agent.Sid, out var last) && last == action) return null;
            _lastAction[agent.Sid] = action;
            return string.IsNullOrWhiteSpace(action) ? null : Numbered(agent.Sid, ForAction(action));
        }

        /// <summary>The user's own reply, as the bridge's session log would show it.</summary>
        public FeedEntry Reply(string sid, string text) => Numbered(sid, Entry("prompt", "Your reply", text));

        /// <summary>A recorded action in words: "Liest x" is a tool entry "liest", "Bash: ls" a "Befehl", and so on.</summary>
        public static FeedEntry ForAction(string action)
        {
            // the pack's lastAction words: German until the pack is translated, English after (both are read)
            if (Starts(action, "Liest ", out var rest) || Starts(action, "Reading ", out rest) || Starts(action, "Reads ", out rest))
                return Entry("tool", "reads", rest, "Read");
            if (Starts(action, "Bearbeitet ", out rest) || Starts(action, "Editing ", out rest) || Starts(action, "Edits ", out rest))
                return Entry("tool", "edits", rest, "Edit");
            if (Starts(action, "Bash: ", out rest)) return Entry("tool", "command", rest, "Bash");
            if (action.StartsWith("Runs a command") || action.StartsWith("Führt Befehl aus")) return Entry("tool", "command", action, "Bash");
            if (action == "Testet" || action.StartsWith("Führt Tests aus") || action.StartsWith("Runs tests"))
                return Entry("tool", "tests", "npm test", "Bash");
            if (action.StartsWith("Wartet auf Freigabe") || action.StartsWith("Waiting for approval"))
                return Entry("system", "waiting", "for your approval" + After(action));
            if (action.StartsWith("Wartet") || action.StartsWith("Waiting")) return Entry("system", "waiting", action);
            if (action == "Arbeitet" || action == "Sitzung gestartet" || action == "Working" || action == "Session started")
                return Entry("thinking", "thinks", NextThought);
            return Entry("text", "says", action);
        }

        static string After(string action)
        {
            var i = action.IndexOf(':');
            return i >= 0 && i + 1 < action.Length ? " (" + action.Substring(i + 1).Trim() + ")" : "";
        }

        static bool Starts(string s, string prefix, out string rest)
        {
            rest = s.StartsWith(prefix) ? s.Substring(prefix.Length) : "";
            return rest.Length > 0;
        }

        static FeedEntry Entry(string kind, string label, string text, string? tool = null) =>
            new FeedEntry { Kind = kind, Label = label, Text = text, Tool = tool };

        FeedEntry Numbered(string sid, FeedEntry e)
        {
            _next.TryGetValue(sid, out var n);
            _next[sid] = n + 1;
            e.Id = sid + "-" + n;
            return e;
        }
    }
}
