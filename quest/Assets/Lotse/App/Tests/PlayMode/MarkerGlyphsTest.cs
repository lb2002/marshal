using System.Collections.Generic;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Lotse.App.Tests
{
    /// <summary>
    /// The characters that stand in for invisible ones (line break, tab, ‹U+XXXX›, the cut's ellipsis) must exist
    /// in the font the card uses, or TMP draws a □ in their place and the marker says nothing. The check must not
    /// write to the tracked font assets: their static tables are only read, and a glyph that would come from the
    /// dynamic fallback is looked up in a runtime copy built from the same font file.
    /// </summary>
    public class MarkerGlyphsTest
    {
        static readonly List<TMP_FontAsset> Copies = new List<TMP_FontAsset>();

        [TearDown]
        public void TearDown()
        {
            foreach (var copy in Copies)
            {
                if (copy == null) continue;
                if (copy.atlasTextures != null)
                    foreach (var t in copy.atlasTextures)
                        if (t != null) Object.Destroy(t);
                if (copy.material != null) Object.Destroy(copy.material);
                Object.Destroy(copy);
            }
            Copies.Clear();
        }

        static IEnumerable<TMP_FontAsset> FontAndFallbacks(TMP_FontAsset font)
        {
            yield return font;
            if (font.fallbackFontAssetTable != null)
                foreach (var f in font.fallbackFontAssetTable)
                    if (f != null) yield return f;
            if (TMP_Settings.fallbackFontAssets != null)
                foreach (var f in TMP_Settings.fallbackFontAssets)
                    if (f != null) yield return f;
        }

        /// <summary>In the static table of the font or a fallback, or in the font file a dynamic fallback draws from.</summary>
        static bool Renders(TMP_FontAsset font, char c)
        {
            foreach (var f in FontAndFallbacks(font))
            {
                if (f.HasCharacter(c, false, false)) return true;
                if (f.atlasPopulationMode != AtlasPopulationMode.Dynamic || f.sourceFontFile == null) continue;
                var copy = TMP_FontAsset.CreateFontAsset(f.sourceFontFile);
                Copies.Add(copy);
                if (copy.HasCharacter(c, false, true)) return true;
            }
            return false;
        }

        static int CharacterCount(TMP_FontAsset font)
        {
            var n = 0;
            foreach (var f in FontAndFallbacks(font)) n += f.characterTable.Count;
            return n;
        }

        [Test]
        public void EveryMarkerCharacterIsInTheFont()
        {
            var font = TMP_Settings.defaultFontAsset;
            Assert.IsNotNull(font, "TMP's default font (Liberation Sans SDF)");
            var before = CharacterCount(font);
            foreach (var c in Untrusted.MarkerCharacters + Ui.CutSuffix)
                Assert.IsTrue(Renders(font, c), $"U+{(int)c:X4} '{c}' is not in {font.name} or its fallbacks");
            Assert.AreEqual(before, CharacterCount(font), "the tracked font assets were not written to");
        }

        /// <summary>U2: the buttons' icons, the source badge letters, the tag's dash and the hint's middle dot.</summary>
        [Test]
        public void EveryIconAndBadgeCharacterIsInTheFont()
        {
            var font = TMP_Settings.defaultFontAsset;
            var before = CharacterCount(font);
            foreach (var c in Icons.All + Ui.Ellipsis + Texts.StartHint + Texts.LoadingDetails + Texts.ReplyDelivered +
                                   Texts.ReplyNotDelivered + Texts.ReplyPlaceholder + Texts.ReplyFallbackReaction)
                Assert.IsTrue(Renders(font, c), $"U+{(int)c:X4} '{c}' is not in {font.name} or its fallbacks");
            Assert.AreEqual(before, CharacterCount(font), "the tracked font assets were not written to");
        }

        /// <summary>U4a: the workers' bubbles and every text of the settings.</summary>
        [Test]
        public void EveryWorkspaceAndSettingsCharacterIsInTheFont()
        {
            var font = TMP_Settings.defaultFontAsset;
            var before = CharacterCount(font);
            foreach (var c in Texts.Settings + Texts.SettingsConnection + Texts.SettingsSound + Texts.SettingsMotion + Texts.MotionNormal +
                                   Texts.MotionCalm + Texts.SoundOn + Texts.SoundOff + Texts.Repair + Texts.DemoRunning + Texts.NoAgents +
                                   Texts.PickAgent + Texts.Deny + Texts.Later + Texts.RiskDangerous + Texts.RiskNetwork + Texts.RiskSafe +
                                   Texts.HintPinch + Texts.HintTwoHand + Texts.TruncatedReason + Texts.TimedOut + Texts.Denied + Texts.Approved +
                                   Texts.SentUnclear + Texts.NotTaken + Texts.ReplyAfterRequest + Texts.MoreRequests(2) + Texts.AgentCount(4) +
                                   " needs you: " + StatusLine.NeedsYou + StatusLine.Command + StatusLine.Thinking + StatusLine.DoneAgo(180000) +
                                   StatusLine.Failed + Texts.SourceWord("claude") + Texts.SourceWord("muse") + Texts.SourceWord("gemini") + Icons.All +
                                   Texts.ConnectionLine(LinkState.Retrying, null) + Texts.ConnectionLine(LinkState.Connecting, "1.2.3.4"))
                Assert.IsTrue(Renders(font, c), $"U+{(int)c:X4} '{c}' is not in {font.name} or its fallbacks");
            Assert.AreEqual(before, CharacterCount(font), "the tracked font assets were not written to");
        }

        /// <summary>U4b: the desk keyboard's keys (QWERTZ, umlauts, ↑ and ← for ⇧ and ⌫), the feed's icons and texts.</summary>
        [Test]
        public void EveryDeskKeyAndFeedCharacterIsInTheFont()
        {
            var font = TMP_Settings.defaultFontAsset;
            var before = CharacterCount(font);
            var keys = new DeskKeys();
            var all = new System.Text.StringBuilder();
            foreach (var row in keys.Rows) foreach (var k in row) all.Append(keys.Label(k));
            keys.Press(new DeskKey(DeskKeyKind.Layer), "", out _);
            foreach (var row in keys.Rows) foreach (var k in row) all.Append(keys.Label(k)).Append(k.Char);
            all.Append("qwertzuiopüasdfghjklöäyxcvbnm▲▼").Append(Texts.FeedNewBelow(3)).Append(Texts.FeedLoading)
                .Append(Texts.FeedUnavailable).Append(Texts.FeedEmpty).Append(Texts.ReplyAgentBusy)
                .Append(Texts.FeedLabel("prompt", "")).Append(Texts.WaitingForLine("x"));
            foreach (var c in all.ToString())
                Assert.IsTrue(Renders(font, c), $"U+{(int)c:X4} '{c}' is not in {font.name} or its fallbacks");
            Assert.AreEqual(before, CharacterCount(font), "the tracked font assets were not written to");
        }

        /// <summary>
        /// U5a: every character of our own texts (every string constant of Texts, StatusLine, Icons and DeskKeys, the
        /// markers, the key labels of both layers, the feed's labels, the formatted lines) is in each Geist atlas itself,
        /// not only in the fallback: Geist is the face the app is meant to show.
        /// </summary>
        [Test]
        public void EveryUiTextKeyAndFeedCharacterIsInTheGeistAtlases()
        {
            var all = new System.Text.StringBuilder();
            foreach (var t in new[] { typeof(Texts), typeof(StatusLine), typeof(Icons), typeof(DeskKeys), typeof(OverviewText), typeof(PlanLines), typeof(Headline) })
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (f.IsLiteral && f.FieldType == typeof(string)) all.Append((string)f.GetRawConstantValue());
            all.Append(Untrusted.MarkerCharacters).Append(Ui.CutSuffix).Append(Ui.Ellipsis);
            var keys = new DeskKeys();
            foreach (var row in keys.Rows) foreach (var k in row) all.Append(keys.Label(k));
            keys.Press(new DeskKey(DeskKeyKind.Layer), "", out _);
            foreach (var row in keys.Rows) foreach (var k in row) all.Append(keys.Label(k)).Append(k.Char);
            foreach (var kind in new[] { "prompt", "thinking", "text", "tool", "result", "error", "system" }) all.Append(Texts.FeedLabel(kind, ""));
            all.Append(Texts.FeedNewBelow(3)).Append(Texts.AgentCount(4)).Append(Texts.MoreRequests(2)).Append(StatusLine.DoneAgo(200 * 60000))
                .Append(Texts.ConnectionLine(LinkState.Retrying, "1.2.3.4")).Append(Texts.Unreachable(2000)).Append(Texts.CodeLine("12"))
                .Append(Texts.EndSummary(252000, 2)).Append(Texts.WaitingForLine("x")).Append(PlanLines.More(3)).Append(PlanLines.FoldBeforeText(3, true)).Append(OverviewText.Page(0, 2)).Append("qwertzuiopüasdfghjklöäyxcvbnmß0123456789");
            foreach (var face in new[] { Fonts.Regular, Fonts.Medium, Fonts.SemiBold, Fonts.Mono })
            {
                Assert.IsNotNull(face);
                StringAssert.StartsWith("Geist", face.name, "the Geist assets are built (FontSetup)");
                Assert.AreEqual(AtlasPopulationMode.Static, face.atlasPopulationMode, face.name + ": a static atlas");
                Assert.IsTrue(face.fallbackFontAssetTable.Contains(TMP_Settings.defaultFontAsset), face.name + ": the old font as its fallback");
                foreach (var c in all.ToString())
                    if (c != (char)10) Assert.IsTrue(face.HasCharacter(c, false, false), $"U+{(int)c:X4} '{c}' is not in {face.name}");
            }
        }

        [Test]
        public void ACharacterTheFontLacksIsReported()
        {
            Assert.IsFalse(Renders(TMP_Settings.defaultFontAsset, '⏎'), "⏎ is not in Liberation Sans");
        }
    }
}
