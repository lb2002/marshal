#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// The few glyphs our own texts draw: characters the Geist atlases carry (checked in MarkerGlyphsTest), so no icon
    /// font or image is needed. U5a: buttons carry words only, no icons and no arrows.
    /// </summary>
    public static class Icons
    {
        public const string Close = "×";
        public const string Ok = "√";
        public const string Bullet = "•";
        /// <summary>The summary line's toggle: closed and open.</summary>
        public const string Closed = "▼";
        public const string Open = "▲";
        /// <summary>Every icon character, for the font check.</summary>
        public const string All = Close + Ok + Bullet + Closed + Open;
    }
}
