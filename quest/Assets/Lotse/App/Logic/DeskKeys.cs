#nullable enable
using System.Collections.Generic;

namespace Lotse.App
{
    public enum DeskKeyKind { Char, Shift, Backspace, Layer, Space, Send }

    /// <summary>One key of the desk keyboard: what it does and how wide it is (in key widths).</summary>
    public readonly struct DeskKey
    {
        public readonly DeskKeyKind Kind;
        /// <summary>The character a Char key types (lower case for letters).</summary>
        public readonly string Char;
        public readonly float Width;

        public DeskKey(DeskKeyKind kind, string ch = "", float width = 1f)
        {
            Kind = kind;
            Char = ch;
            Width = width;
        }
    }

    /// <summary>What a key press did.</summary>
    public enum DeskKeyResult { None, Typed, Deleted, Toggled, Send }

    /// <summary>
    /// The desk keyboard's layout and typing (U4b; QWERTY since U5b): QWERTYUIOP- / ASDFGHJKL'? / ↑ ZXCVBNM,. ← / 123, Space,
    /// Senden, and a layer of digits and signs. "↑" (shift, once) and "←" (backspace) stand in for ⇧ and ⌫, which
    /// Liberation Sans does not have. A draft starts with a capital letter. Typing stops at ReplyLogic.MaxChars. The
    /// keyboard never sends: "Senden" only reports Send, and the caller applies the explicit-Senden rules. Pure.
    /// </summary>
    public sealed class DeskKeys
    {
        public const string ShiftLabel = "↑";      // ↑
        public const string BackspaceLabel = "←";  // ←
        public const string NumbersLabel = "123";
        public const string LettersLabel = "ABC";

        static readonly DeskKey[][] Letters =
        {
            Chars("qwertyuiop-"), // QWERTY, no umlaut keys (U5b: English)
            Chars("asdfghjkl'?"),
            With(new DeskKey(DeskKeyKind.Shift, width: 1.5f), Chars("zxcvbnm,."), new DeskKey(DeskKeyKind.Backspace, width: 1.5f)),
            Bottom(),
        };

        static readonly DeskKey[][] Signs =
        {
            Chars("1234567890+"),
            Chars("-/:;()€&@\"?"),
            With(new DeskKey(DeskKeyKind.Shift, width: 1.5f), Chars("!'$*#=_%."), new DeskKey(DeskKeyKind.Backspace, width: 1.5f)),
            Bottom(),
        };

        static DeskKey[] Chars(string s)
        {
            var keys = new DeskKey[s.Length];
            for (var i = 0; i < s.Length; i++) keys[i] = new DeskKey(DeskKeyKind.Char, s[i].ToString());
            return keys;
        }

        static DeskKey[] With(DeskKey first, DeskKey[] middle, DeskKey last)
        {
            var keys = new DeskKey[middle.Length + 2];
            keys[0] = first;
            middle.CopyTo(keys, 1);
            keys[keys.Length - 1] = last;
            return keys;
        }

        static DeskKey[] Bottom() => new[]
        {
            new DeskKey(DeskKeyKind.Layer, width: 2f), new DeskKey(DeskKeyKind.Space, " ", 6f), new DeskKey(DeskKeyKind.Send, width: 3f),
        };

        /// <summary>The next letter is a capital (once).</summary>
        public bool Shift { get; private set; } = true;
        /// <summary>The digits and signs are shown.</summary>
        public bool Numbers { get; private set; }

        public IReadOnlyList<IReadOnlyList<DeskKey>> Rows => Numbers ? Signs : Letters;

        /// <summary>What a key shows: capitals for letters (like key caps; the shift key shows whether the next one is a capital), the stand-in glyphs, our words.</summary>
        public string Label(in DeskKey key)
        {
            switch (key.Kind)
            {
                case DeskKeyKind.Char: return key.Char.ToUpperInvariant();
                case DeskKeyKind.Shift: return ShiftLabel;
                case DeskKeyKind.Backspace: return BackspaceLabel;
                case DeskKeyKind.Layer: return Numbers ? LettersLabel : NumbersLabel;
                case DeskKeyKind.Space: return Texts.Space;
                default: return Texts.ReplySend;
            }
        }

        /// <summary>A fresh draft starts with a capital.</summary>
        public void Begin(string draft) => Shift = draft.Length == 0;

        /// <summary>One key on `draft`; `result` is the draft afterwards (unchanged unless typed or deleted).</summary>
        public DeskKeyResult Press(in DeskKey key, string draft, out string result)
        {
            result = draft ?? "";
            switch (key.Kind)
            {
                case DeskKeyKind.Char:
                case DeskKeyKind.Space:
                {
                    var ch = key.Kind == DeskKeyKind.Char && Shift ? key.Char.ToUpperInvariant() : key.Char;
                    if (result.Length + ch.Length > ReplyLogic.MaxChars) return DeskKeyResult.None;
                    result += ch;
                    if (key.Kind == DeskKeyKind.Char) Shift = false;
                    return DeskKeyResult.Typed;
                }
                case DeskKeyKind.Backspace:
                {
                    if (result.Length == 0) return DeskKeyResult.None;
                    var cut = result.Length >= 2 && char.IsLowSurrogate(result[result.Length - 1]) ? 2 : 1;
                    result = result.Substring(0, result.Length - cut);
                    if (result.Length == 0) Shift = true;
                    return DeskKeyResult.Deleted;
                }
                case DeskKeyKind.Shift:
                    Shift = !Shift;
                    return DeskKeyResult.Toggled;
                case DeskKeyKind.Layer:
                    Numbers = !Numbers;
                    return DeskKeyResult.Toggled;
                case DeskKeyKind.Send:
                    return DeskKeyResult.Send;
                default:
                    return DeskKeyResult.None;
            }
        }
    }
}
