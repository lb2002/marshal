#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>
    /// What an agent without a plan shows in the overview (U6): its last 5 distinct `lastAction` lines, newest first. A
    /// line seen again moves to the front; blank lines are skipped. The lines are the bridge's untrusted words, already
    /// capped by Untrusted when they arrive; they are drawn plain. Pure.
    /// </summary>
    public sealed class ActivityTrail
    {
        public const int MaxLines = 5;

        readonly Dictionary<string, List<string>> _lines = new Dictionary<string, List<string>>();
        static readonly IReadOnlyList<string> None = Array.Empty<string>();

        /// <summary>The agent `sid` reported `lastAction`. True when its lines changed.</summary>
        public bool Note(string sid, string? lastAction)
        {
            var line = (lastAction ?? "").Trim();
            if (line.Length == 0) return false;
            if (!_lines.TryGetValue(sid, out var list)) _lines[sid] = list = new List<string>();
            if (list.Count > 0 && list[0] == line) return false;
            list.Remove(line);
            list.Insert(0, line);
            if (list.Count > MaxLines) list.RemoveRange(MaxLines, list.Count - MaxLines);
            return true;
        }

        /// <summary>Newest first, at most 5.</summary>
        public IReadOnlyList<string> Lines(string sid) => _lines.TryGetValue(sid, out var list) ? list : None;

        public void Clear() => _lines.Clear();
    }
}
