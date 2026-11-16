#nullable enable
using System.Collections.Generic;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class LookAndTextsTests
    {
        static PermRequest Req(string? level, bool truncated = false, string? command = null, string? path = null) =>
            new PermRequest { Id = "p", Tool = "Bash", Command = command, Path = path, Truncated = truncated, Risk = new RiskResult { Level = level } };

        [Test]
        public void TheFourVendorsAreNamedAndAMissingSourceFallsBackToAgent()
        {
            Assert.AreEqual("Claude Code", Texts.SourceWord("claude"));
            Assert.AreEqual("Codex", Texts.SourceWord("codex"));
            Assert.AreEqual("Gemini CLI", Texts.SourceWord("gemini"));
            Assert.AreEqual("Muse", Texts.SourceWord("muse"));
            Assert.AreEqual("Agent", Texts.SourceWord(null), "no source: a neutral word, never a wrong vendor");
            Assert.AreEqual("Agent", Texts.SourceWord(""));
            Assert.AreEqual("Agent", Texts.SourceWord("<b>evil</b>"), "an unknown source is never shown as it came");
            Assert.AreEqual("Agent in shop", Texts.SourceLine(new AgentState { Sid = "s", Project = "shop" }));
        }

        [Test]
        public void TheRiskWordFailsClosed()
        {
            Assert.AreEqual("Harmless", Texts.RiskWord(Req("safe")));
            Assert.AreEqual("Network", Texts.RiskWord(Req("network")));
            Assert.AreEqual("Dangerous", Texts.RiskWord(Req("destructive")));
            Assert.AreEqual("Dangerous", Texts.RiskWord(Req("safe", truncated: true)), "a cut request is dangerous");
            Assert.AreEqual("Dangerous", Texts.RiskWord(Req("network", truncated: true)));
            Assert.AreEqual("Dangerous", Texts.RiskWord(Req("critical")), "an unknown level fails closed");
            Assert.AreEqual("Dangerous", Texts.RiskWord(Req(null)), "no level fails closed");
        }

        [Test]
        public void TheHeadlineIsTheExplanationsFirstPartAndTheDetailItsRest()
        {
            var rules = new Explanation { Text = "Claude ändert die Datei: src/components/Cart.tsx", Source = "rules" };
            Assert.AreEqual("Claude ändert die Datei", Texts.Headline(rules, Req("safe")));
            Assert.AreEqual("", Texts.Detail(rules), "the path after the colon is the subject line already");
            var manual = new Explanation { Text = "Claude will den Ordner löschen, mit 214 Dateien. Das ist endgültig.", Source = "manual", AiText = "Prüfe vorher." };
            Assert.AreEqual("Claude will den Ordner löschen, mit 214 Dateien", Texts.Headline(manual, Req("destructive")));
            Assert.AreEqual("Das ist endgültig. AI: Prüfe vorher.", Texts.Detail(manual));
            Assert.AreEqual("Wants to use Bash", Texts.Headline(null, Req("safe")));
            Assert.AreEqual("Wants to use Bash", Texts.Headline(new Explanation { Text = "  " }, Req("safe")));
            var long1 = new Explanation { Text = new string('w', 200) };
            Assert.LessOrEqual(Texts.Headline(long1, Req("safe")).Length, 90);
        }

        [Test]
        public void TheWorkspaceWords()
        {
            Assert.AreEqual("4 agents", Texts.AgentCount(4));
            Assert.AreEqual("1 agent", Texts.AgentCount(1));
            Assert.AreEqual("+2 more", Texts.MoreRequests(2));
            var a = new AgentState { Sid = "s1", Name = "Agent 2", Title = "Checkout-Summen korrigieren", Source = "claude", Project = "shop-web" };
            Assert.AreEqual("Checkout-Summen korrigieren", Texts.AgentTitle(a));
            Assert.AreEqual("Claude Code in shop-web", Texts.SourceLine(a));
            Assert.AreEqual("Checkout-Summen korrigieren needs you: Claude ändert die Datei", Texts.Notice(a, "s1", "Claude ändert die Datei"));
            Assert.AreEqual("Agent 2", Texts.AgentTitle(new AgentState { Name = "Agent 2" }), "no title: the name");
            Assert.AreEqual("Muse", Texts.SourceLine(new AgentState { Source = "muse" }), "no project: the source alone, as text");
            Assert.AreEqual("Codex", Texts.SourceWord("codex"));
            Assert.AreEqual("Gemini CLI", Texts.SourceWord("gemini"));
            Assert.AreEqual("Agent", Texts.SourceWord("whatever"));
        }

        [Test]
        public void NoLabelUsesAMiddleDotAnArrowOrAllCaps()
        {
            foreach (var label in new[] { Texts.StartHint, Texts.Deny, Texts.Later, Texts.ReplySend, Texts.KeyboardToggle, Texts.NoAgents,
                         Texts.HintPinch, Texts.HintTwoHand, Texts.TruncatedReason, Texts.Settings, Texts.SettingsConnection, Texts.SettingsSound,
                         Texts.SettingsMotion, Texts.MotionCalm, Texts.Approved, Texts.Denied, Texts.TimedOut, Texts.NotTaken, Texts.SentUnclear })
            {
                Assert.IsFalse(label.Contains("·"), label);
                Assert.IsFalse(label.Contains("→") || label.Contains("←") || label.Contains("↔") || label.Contains("↕"), label);
                Assert.AreNotEqual(label.ToUpperInvariant(), label, label);
            }
        }

        [Test]
        public void GestureHintFollowsNeedsTwoHand()
        {
            Assert.AreEqual(Texts.HintPinch, Texts.GestureHint(Req("safe")));
            Assert.AreEqual(Texts.HintPinch, Texts.GestureHint(Req("network")));
            Assert.AreEqual(Texts.HintTwoHand, Texts.GestureHint(Req("destructive")));
            Assert.AreEqual(Texts.HintTwoHand, Texts.GestureHint(Req("safe", truncated: true)));
            Assert.AreEqual(Texts.HintTwoHand, Texts.GestureHint(Req(null)));
        }

        [Test]
        public void TheHintsSayWhichFingersAndHowLong()
        {
            Assert.AreEqual("Pinch with both hands and hold until the bar is dark", Texts.GestureHint(Req("destructive")));
            Assert.AreEqual("Pinch and hold until the bar is dark", Texts.GestureHint(Req("safe")));
            Assert.AreEqual("Pinch and hold until the bar is dark", Texts.GestureHint(Req("network")));
        }

        [Test]
        public void ATruncatedSafeCardSaysWhyItNeedsBothHands()
        {
            Assert.AreEqual("Shortened, so both hands", Texts.TwoHandReason(Req("safe", truncated: true)));
            Assert.AreEqual("Shortened, so both hands", Texts.TwoHandReason(Req("network", truncated: true)));
            Assert.IsNull(Texts.TwoHandReason(Req("safe")));
            Assert.IsNull(Texts.TwoHandReason(Req("destructive", truncated: true)), "dangerous needs both hands anyway");
            Assert.IsNull(Texts.TwoHandReason(Req("destructive")));
        }

        [Test]
        public void AiTextIsLabelled()
        {
            Assert.AreEqual("AI: prüft", Texts.AiLine("prüft"));
        }

        [Test]
        public void SubjectStripsTheRepoPlaceholderInDemoModeOnly()
        {
            Assert.AreEqual("cat src/a.ts && rm src/b.ts", Texts.Subject(Req("safe", command: "cat <repo>/src/a.ts && rm <repo>/src/b.ts"), stripRepo: true));
            Assert.AreEqual("src/components/Cart.tsx", Texts.Subject(Req("safe", path: "<repo>/src/components/Cart.tsx"), stripRepo: true));
            var multi = new PermRequest { Tool = "Edit", Paths = new List<string> { "<repo>/a.ts", "<repo>/b.ts" }, Risk = new RiskResult { Level = "safe" } };
            Assert.AreEqual("a.ts\nb.ts", Texts.Subject(multi, stripRepo: true));
            Assert.AreEqual("WebFetch", Texts.Subject(new PermRequest { Tool = "WebFetch", Risk = new RiskResult { Level = "network" } }, stripRepo: true));
            StringAssert.EndsWith("(shortened)", Texts.Subject(Req("safe", truncated: true, command: "echo"), stripRepo: true));

            // live: shown exactly as the bridge sent it
            Assert.AreEqual("cat <repo>/src/a.ts", Texts.Subject(Req("safe", command: "cat <repo>/src/a.ts"), stripRepo: false));
            Assert.AreEqual("<repo>/a.ts\n<repo>/b.ts", Texts.Subject(multi, stripRepo: false));
        }

        [Test]
        public void SubjectShowsControlCharactersAndCaps()
        {
            var s = Texts.Subject(Req("safe", command: "ls\u202E\n" + new string('x', 400)), stripRepo: false);
            StringAssert.StartsWith("ls‹U+202E›¶", s);
            Assert.LessOrEqual(s.Length, Untrusted.CommandMax);
        }

        static readonly System.Text.RegularExpressions.Regex AnyNoParseEnd =
            new System.Text.RegularExpressions.Regex(@"<\s*/\s*noparse\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        [TestCase("<b>x</b>")]
        [TestCase("<color=green>Sicher</color>")]
        [TestCase("a</noparse><color=green>Sicher</color>")]
        [TestCase("a</NOPARSE><color=green>Sicher</color>")]
        [TestCase("a</NoParse><color=green>Sicher</color>")]
        [TestCase("a< /noparse ><color=green>Sicher</color>")]
        [TestCase("a</\tnoparse\n>b</noparse>c")]
        public void LiteralKeepsRichTextTagsInert(string untrusted)
        {
            var shown = Texts.Literal(untrusted);
            StringAssert.StartsWith("<noparse>", shown);
            StringAssert.EndsWith("</noparse>", shown);
            Assert.AreEqual(1, AnyNoParseEnd.Matches(shown).Count, "only the closing tag we add, in any spelling: " + shown);
        }

        [Test]
        public void LiteralLeavesHarmlessTextAlone()
        {
            Assert.AreEqual("<noparse><color=green>Sicher</color></noparse>", Texts.Literal("<color=green>Sicher</color>"));
            Assert.AreEqual("<noparse>a\u2039/noparse\u203Ab</noparse>", Texts.Literal("a</NoParse>b"));
        }

        [TestCase("a\\u0041b")]
        [TestCase("a\\nb")]
        [TestCase("C:\\Users\\x\\\\")]
        [TestCase("\\U0001F600")]
        [TestCase("no backslash")]
        public void PlainDefusesBackslashEscapes(string untrusted)
        {
            var shown = Texts.Plain(untrusted);
            StringAssert.DoesNotContain("\\u", shown);
            StringAssert.DoesNotContain("\\U", shown);
            StringAssert.DoesNotContain("\\n", shown);
            Assert.AreEqual(untrusted, shown.Replace("\u2060", ""), "only word joiners are added");
            for (var i = 0; i < shown.Length; i++)
                if (shown[i] == '\\') Assert.AreEqual('\u2060', shown[i + 1], "every backslash is followed by U+2060");
        }

        [TestCase(0, "0 seconds")]
        [TestCase(999, "0 seconds")]
        [TestCase(1000, "1 second")]
        [TestCase(45500, "45 seconds")]
        [TestCase(60000, "1 minute")]
        [TestCase(61000, "1 minute 1 second")]
        [TestCase(252000, "4 minutes 12 seconds")]
        public void Durations(double ms, string expected)
        {
            Assert.AreEqual(expected, Texts.Duration(ms));
        }

        [Test]
        public void EndSummarySentence()
        {
            Assert.AreEqual("Your agents waited for you for 4 minutes 12 seconds in total. 2 dangerous commands stopped.",
                Texts.EndSummary(252000, 2));
            StringAssert.EndsWith("1 dangerous command stopped.", Texts.EndSummary(1000, 1));
            StringAssert.EndsWith("0 dangerous commands stopped.", Texts.EndSummary(1000, 0));
        }

        [Test]
        public void ReviewLines()
        {
            Assert.AreEqual("Agent 1: Done: x in <repo>/a.ts", Texts.Review("Agent 1", "Done: x in <repo>/a.ts"));
        }

        [Test]
        public void ASentDecisionWhoseLinkWasLostIsUnclear()
        {
            var allow = ClientDecision.Allow("r", "pinch-hold");
            Assert.AreEqual("Sent, result unclear. Please check the terminal.", Texts.Closed("client-disconnected", allow));
            Assert.AreEqual("Sent, result unclear. Please check the terminal.", Texts.Closed("client-disconnected", ClientDecision.Deny("r", "x")));
            Assert.AreEqual(Texts.NotTaken, Texts.Closed("client-disconnected", null), "nothing was sent: the terminal asks");
            Assert.AreEqual(Texts.Approved, Texts.Closed("allow", allow));
            Assert.AreEqual(Texts.TimedOut, Texts.Closed("timeout", allow));
            Assert.AreEqual(Texts.NotTaken, Texts.Closed("hook-gone", allow));
            Assert.AreEqual(Texts.NotTaken, Texts.Closed("gone", null));
        }

        [Test]
        public void TheSoundAndRuhigAreKeptAndAnythingUnknownReadsAsTheDefault()
        {
            var store = new MemoryStore();
            var settings = new LookSettings(store);
            Assert.IsTrue(settings.Sound, "the sound is on by default");
            Assert.IsFalse(settings.Calm, "Ruhig is off by default");
            settings.Sound = false;
            settings.Calm = true;
            var again = new LookSettings(store);
            Assert.IsFalse(again.Sound);
            Assert.IsTrue(again.Calm);
            store.Set(LookSettings.SoundKey, "vielleicht");
            store.Set(LookSettings.CalmKey, "vielleicht");
            Assert.IsTrue(again.Sound);
            Assert.IsFalse(again.Calm);
        }

        [Test]
        public void TheConnectionLineSaysWhereItStands()
        {
            Assert.AreEqual("Connected to 192.168.1.20", Texts.ConnectionLine(LinkState.Live, "192.168.1.20"));
            StringAssert.StartsWith("Connecting to 192.168.1.20", Texts.ConnectionLine(LinkState.Connecting, "192.168.1.20"));
            Assert.AreEqual(Texts.NotConnected, Texts.ConnectionLine(LinkState.Idle, null));
            StringAssert.Contains("interrupted", Texts.ConnectionLine(LinkState.Retrying, "192.168.1.20"));
        }
    }
}
