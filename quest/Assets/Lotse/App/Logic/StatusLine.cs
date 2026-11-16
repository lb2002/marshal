#nullable enable
using System;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>How a status line is coloured: secondary text, or red for "needs you".</summary>
    public enum StatusTone { Normal, NeedsYou }

    /// <summary>One agent's status line in the list: its words and its tone.</summary>
    public readonly struct StatusText
    {
        public readonly string Text;
        public readonly StatusTone Tone;

        public StatusText(string text, StatusTone tone)
        {
            Text = text;
            Tone = tone;
        }

        public override string ToString() => Text;
    }

    /// <summary>
    /// The one-line status under an agent's title (U5a), from its state and its last action: "wartet auf dich" (red)
    /// while it needs the user, "Fehler", "fertig vor 3 Min.", "Befehl läuft", "bearbeitet Header.tsx", "liest
    /// Cart.tsx", "nutzt WebSearch", "denkt nach", else "arbeitet". The bridge's lastAction words (bridge/src/state.ts)
    /// and the demo pack's are read; a file shows by its name only. Untrusted parts are capped and their hidden
    /// characters shown. Pure.
    /// </summary>
    public static class StatusLine
    {
        public const string NeedsYou = "needs you";
        public const string Failed = "error";
        public const string DoneNow = "done";
        public const string Command = "running a command";
        public const string Thinking = "thinking";
        public const string Working = "working";
        public const string EditingFiles = "editing files";
        public const string ReadingFiles = "reading files";
        const int FileMax = 40;

        /// <param name="needsYou">It has an open request (or waits for an answer).</param>
        /// <param name="lastFeedKind">The kind of its newest feed entry, when its feed is known; else null.</param>
        /// <param name="doneForMs">How long it has been done, when it is.</param>
        public static StatusText For(AgentState agent, bool needsYou, string? lastFeedKind, double doneForMs)
        {
            if (needsYou || agent.Status == "blocked" || agent.Status == "waiting") return new StatusText(NeedsYou, StatusTone.NeedsYou);
            switch (OrbState.For(agent, false, lastFeedKind))
            {
                case OrbMode.Error: return new StatusText(Failed, StatusTone.Normal);
                case OrbMode.Done: return new StatusText(DoneAgo(doneForMs), StatusTone.Normal);
                case OrbMode.Command: return new StatusText(Command, StatusTone.Normal);
                case OrbMode.Thinking: return new StatusText(Thinking, StatusTone.Normal);
            }
            var action = agent.LastAction ?? "";
            // the bridge's lastAction words: German until the bridge is translated, English after (both are read)
            if (After(action, out var edited, "Bearbeitet ", "Editing ", "Edits ")) return new StatusText(File("editing ", edited, EditingFiles), StatusTone.Normal);
            if (After(action, out var read, "Liest ", "Reading ", "Reads ")) return new StatusText(File("reading ", read, ReadingFiles), StatusTone.Normal);
            if (After(action, out var tool, "Nutzt ", "Using ", "Uses ")) return new StatusText("using " + Untrusted.Clamp(tool.Trim(), Untrusted.ToolMax), StatusTone.Normal);
            if (agent.Status == "editing") return new StatusText(EditingFiles, StatusTone.Normal);
            if (agent.Status == "reading") return new StatusText(ReadingFiles, StatusTone.Normal);
            return new StatusText(Working, StatusTone.Normal);
        }

        /// <summary>"gerade fertig", "fertig vor 3 Min.", "fertig vor 2 Std."</summary>
        public static string DoneAgo(double ms)
        {
            var min = ms <= 0 ? 0L : (long)Math.Floor(ms / 60000.0);
            if (min < 1) return DoneNow;
            if (min < 60) return "done " + min + " min ago";
            return "done " + (min / 60) + " h ago";
        }

        /// <summary>The verb and a file's name ("bearbeitet Header.tsx"); `fallback` for "(Edit)" or nothing.</summary>
        static string File(string verb, string target, string fallback)
        {
            target = target.Trim();
            // the demo pack's bare project placeholder is no file name
            if (target.Length == 0 || target.StartsWith("(", StringComparison.Ordinal) || target == "<repo>") return fallback;
            var cut = Math.Max(target.LastIndexOf('/'), target.LastIndexOf('\\'));
            var name = cut >= 0 && cut < target.Length - 1 ? target.Substring(cut + 1) : target;
            return verb + Untrusted.Clamp(name, FileMax);
        }

        internal static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

        /// <summary>The rest after the first of `prefixes` that `s` starts with.</summary>
        internal static bool After(string s, out string rest, params string[] prefixes)
        {
            foreach (var p in prefixes)
                if (Starts(s, p))
                {
                    rest = s.Substring(p.Length);
                    return true;
                }
            rest = "";
            return false;
        }
    }
}
