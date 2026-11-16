#nullable enable
using System.Text;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// What a request shows on its block and what decides its gesture (U5a fix 1, security review): the id, the tool, the
    /// risk level and reason, two hands or not, truncated, the command and its length, the path and every path, the URL.
    /// A copy of the shown request that differs in any of these is a new block: it is drawn again and a running hold is
    /// cancelled, so what the user approves is always what the block shows. Pure.
    /// </summary>
    public static class RequestContent
    {
        /// <summary>Whether `a` and `b` show the same thing and ask for the same gesture.</summary>
        public static bool Same(PermRequest? a, PermRequest? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return Key(a) == Key(b);
        }

        /// <summary>Every displayed or gesture-relevant field, joined (with lengths, so no two requests share a key).</summary>
        public static string Key(PermRequest r)
        {
            var sb = new StringBuilder(128);
            Add(sb, r.Id);
            Add(sb, r.Tool);
            Add(sb, r.Risk?.Level);
            Add(sb, r.Risk?.Reason);
            Add(sb, r.NeedsTwoHand ? "2" : "1");
            Add(sb, r.Truncated ? "t" : "f");
            Add(sb, r.Command);
            Add(sb, r.CommandLength?.ToString());
            Add(sb, r.Path);
            Add(sb, r.Paths?.Count.ToString());
            if (r.Paths != null)
                foreach (var p in r.Paths)
                    Add(sb, p);
            Add(sb, r.Url);
            return sb.ToString();
        }

        static void Add(StringBuilder sb, string? s)
        {
            if (s == null)
            {
                sb.Append("-|");
                return;
            }
            sb.Append(s.Length).Append(':').Append(s).Append('|');
        }
    }
}
