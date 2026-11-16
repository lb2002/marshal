#nullable enable

namespace Lotse.App
{
    /// <summary>What a poke keypad has typed so far: digits, and dots when allowed, up to a maximum length.</summary>
    public sealed class KeypadEntry
    {
        public const int CodeLength = 6;
        /// <summary>"255.255.255.255".</summary>
        public const int HostLength = 15;

        readonly int _maxLength;
        readonly bool _allowDot;

        public KeypadEntry(int maxLength, bool allowDot)
        {
            _maxLength = maxLength;
            _allowDot = allowDot;
        }

        /// <summary>The 6-digit pairing code pad (0-9, back, OK).</summary>
        public static KeypadEntry ForCode() => new KeypadEntry(CodeLength, allowDot: false);

        /// <summary>The address pad (0-9 and ".", back, OK).</summary>
        public static KeypadEntry ForHost() => new KeypadEntry(HostLength, allowDot: true);

        public string Text { get; private set; } = "";

        /// <summary>Appends a digit (or a dot where allowed); false when the key is not allowed or the entry is full.</summary>
        public bool Type(char key)
        {
            var ok = (key >= '0' && key <= '9') || (key == '.' && _allowDot);
            if (!ok || Text.Length >= _maxLength) return false;
            Text += key;
            return true;
        }

        public void Backspace()
        {
            if (Text.Length > 0) Text = Text.Substring(0, Text.Length - 1);
        }

        public void Clear() => Text = "";
    }
}
