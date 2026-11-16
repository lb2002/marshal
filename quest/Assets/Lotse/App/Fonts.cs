using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The typeface (U5a): Geist (SIL Open Font License 1.1; Assets/Lotse/Fonts/Geist/OFL.txt), as static TextMeshPro
    /// atlases built by Lotse.EditorTools.FontSetup, with the old default font as their fallback. Regular for body and
    /// small text, Medium for titles and row titles, SemiBold only for the window title "Lotse" and the approval block's
    /// first line, Mono for commands, paths and URLs. A missing asset falls back to TMP's default font.
    /// </summary>
    public static class Fonts
    {
        static TMP_FontAsset _regular, _medium, _semiBold, _mono;

        public static TMP_FontAsset Regular => Load(ref _regular, "Geist-Regular SDF");
        public static TMP_FontAsset Medium => Load(ref _medium, "Geist-Medium SDF");
        public static TMP_FontAsset SemiBold => Load(ref _semiBold, "Geist-SemiBold SDF");
        public static TMP_FontAsset Mono => Load(ref _mono, "GeistMono-Regular SDF");

        /// <summary>The atlases as loaded (for the device log): "Geist-Regular SDF 2048x2048 @90", ...</summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in new[] { Regular, Medium, SemiBold, Mono })
            {
                if (sb.Length > 0) sb.Append(", ");
                var atlas = f != null && f.atlasTextures != null && f.atlasTextures.Length > 0 ? f.atlasTextures[0] : null;
                sb.Append(f != null ? f.name : "none").Append(' ')
                  .Append(atlas != null ? atlas.width + "x" + atlas.height : "?").Append(" @").Append(f != null ? f.faceInfo.pointSize : 0);
            }
            return sb.ToString();
        }

        static TMP_FontAsset Load(ref TMP_FontAsset slot, string name)
        {
            if (slot != null) return slot;
            slot = Resources.Load<TMP_FontAsset>("Fonts/" + name);
            if (slot == null) slot = TMP_Settings.defaultFontAsset;
            // known by name, so our own rich text can switch to it (<font="Geist-Regular SDF">)
            else MaterialReferenceManager.AddFontAsset(slot);
            return slot;
        }
    }
}
