using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Lotse.App;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Lotse.EditorTools
{
    /// <summary>
    /// Builds the Geist TextMeshPro font assets (U5a) from the TTFs in Assets/Lotse/Fonts/Geist (SIL Open Font License
    /// 1.1, OFL.txt next to them), in batch mode:
    /// Unity -batchmode -quit -projectPath quest -executeMethod Lotse.EditorTools.FontSetup.Build
    /// Static atlases with every character the app can show (FontSetup.Characters: ASCII, Latin-1 with the umlauts and ß,
    /// the typographic marks and every character of our own texts, icons and key labels); the old default font (Liberation
    /// Sans) stays their fallback, so an unexpected character still renders. Written to App/Resources/Fonts, where
    /// Fonts loads them at runtime.
    /// </summary>
    public static class FontSetup
    {
        /// <summary>Sharp on the headset (U5b, the first device test said blurry): 90 pt sampling, padding 9, 2048 atlases.</summary>
        public const int SamplingPointSize = 90;
        public const int AtlasPadding = 9;
        public const int AtlasSize = 2048;
        const string Source = "Assets/Lotse/Fonts/Geist/";
        const string Target = "Assets/Lotse/App/Resources/Fonts/";
        static readonly string[] Faces = { "Geist-Regular", "Geist-Medium", "Geist-SemiBold", "GeistMono-Regular" };

        /// <summary>Every character the atlases carry.</summary>
        public static string Characters
        {
            get
            {
                var set = new SortedSet<char>();
                for (var c = 0x20; c <= 0x7E; c++) set.Add((char)c);
                for (var c = 0xA0; c <= 0xFF; c++) set.Add((char)c);
                foreach (var c in "…–—„“”‚‘’‹›•√×←→↑↓▲▼¶€−") set.Add(c);
                foreach (var t in new[] { typeof(Texts), typeof(StatusLine), typeof(Icons), typeof(DeskKeys) })
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                        foreach (var c in (string)f.GetRawConstantValue()) set.Add(c);
                foreach (var c in Untrusted.MarkerCharacters + Ui.CutSuffix + Ui.Ellipsis) set.Add(c);
                var keys = new DeskKeys();
                foreach (var row in keys.Rows) foreach (var k in row) foreach (var c in keys.Label(k)) set.Add(c);
                foreach (var c in "qwertzuiopüasdfghjklöäyxcvbnmQWERTZUIOPÜASDFGHJKLÖÄYXCVBNM") set.Add(c);
                set.Remove('\n');
                return new string(set.ToArray());
            }
        }

        public static void Build()
        {
            AssetDatabase.Refresh();
            System.IO.Directory.CreateDirectory(Target);
            var fallback = TMP_Settings.defaultFontAsset;
            var chars = Characters;
            var ok = true;
            foreach (var face in Faces)
            {
                var font = AssetDatabase.LoadAssetAtPath<Font>(Source + face + ".ttf");
                if (font == null)
                {
                    Debug.LogError("[Fonts] no font at " + Source + face + ".ttf");
                    ok = false;
                    continue;
                }
                var path = Target + face + " SDF.asset";
                AssetDatabase.DeleteAsset(path);
                var fa = TMP_FontAsset.CreateFontAsset(font, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA, AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, false);
                fa.name = face + " SDF";
                fa.TryAddCharacters(chars, out var missing);
                fa.atlasPopulationMode = AtlasPopulationMode.Static;
                if (fallback != null) fa.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
                AssetDatabase.CreateAsset(fa, path);
                var atlas = fa.atlasTextures[0];
                atlas.name = face + " Atlas";
                AssetDatabase.AddObjectToAsset(atlas, fa);
                fa.material.name = face + " Material";
                AssetDatabase.AddObjectToAsset(fa.material, fa);
                EditorUtility.SetDirty(fa);
                var miss = new StringBuilder();
                foreach (var c in missing ?? "") miss.Append($" U+{(int)c:X4}");
                Debug.Log($"[Fonts] {face}: {fa.characterTable.Count} characters, atlas {atlas.width}x{atlas.height}" +
                          (miss.Length > 0 ? ", not in the face (fallback):" + miss : ""));
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[Fonts] characters: " + chars.Length);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
