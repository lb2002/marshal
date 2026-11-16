#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>F3: untrusted text is capped on the client and its control, format and bidi characters are shown.</summary>
    public class UntrustedTests
    {
        static string FortyLines()
        {
            var sb = new StringBuilder();
            for (var i = 1; i <= 40; i++) sb.Append("echo line ").Append(i).Append('\n');
            return sb.ToString();
        }

        [TestCase("a\nb", "a¶b")]
        [TestCase("a\tb", "a→b")]
        [TestCase("a\rb", "a¶b")]
        [TestCase("a\vb", "a¶b")]
        [TestCase("a\u2028b\u2029c", "a¶b¶c")]
        [TestCase("ok\u202Eexe.txt", "ok‹U+202E›exe.txt")]
        [TestCase("\u200Bx\u2066y\u2069\u061C", "‹U+200B›x‹U+2066›y‹U+2069›‹U+061C›")]
        [TestCase("bell\u0007 del\u007F c1\u0085", "bell‹U+0007› del‹U+007F› c1‹U+0085›")]
        [TestCase("tag\U000E0041", "tag‹U+E0041›")]
        [TestCase("Grüße – „ok“ 😀", "Grüße – „ok“ 😀")]
        public void ControlFormatAndBidiCharactersAreShown(string input, string expected)
        {
            Assert.AreEqual(expected, Untrusted.Visible(input));
        }

        [Test]
        public void VisibleIsIdempotent()
        {
            var once = Untrusted.Visible("a\nb\u202Ec\td");
            Assert.AreEqual(once, Untrusted.Visible(once));
            Assert.AreEqual(once, Untrusted.Clamp(once, 300));
        }

        [Test]
        public void ClampCapsAndMarksTheCut()
        {
            Assert.AreEqual("short", Untrusted.Clamp("short", 10));
            var cut = Untrusted.Clamp(new string('x', 50), 10);
            Assert.AreEqual(10, cut.Length);
            StringAssert.EndsWith("…", cut);
            Assert.AreEqual(cut, Untrusted.Clamp(cut, 10), "a clamped text stays as it is");
            Assert.AreEqual("", Untrusted.Clamp(null, 10));
        }

        [Test]
        public void AFortyLineCommandIsCappedAndNeedsBothHands()
        {
            var req = new PermRequest { Id = "r", Tool = "Bash", Command = FortyLines(), Risk = new RiskResult { Level = "safe" } };
            Assert.IsFalse(req.NeedsTwoHand);
            Assert.IsTrue(Untrusted.Request(req), "cut");
            Assert.LessOrEqual(req.Command!.Length, Untrusted.CommandMax);
            StringAssert.DoesNotContain("\n", req.Command);
            StringAssert.StartsWith("echo line 1¶echo line 2¶", req.Command);
            StringAssert.EndsWith("…", req.Command);
            Assert.IsTrue(req.Truncated, "what the user cannot see in full needs two hands");
            Assert.IsTrue(req.NeedsTwoHand);
        }

        [Test]
        public void AFiveKilobytePathIsCapped()
        {
            var huge = "src/" + new string('a', 5 * 1024) + ".ts";
            var req = new PermRequest { Id = "r", Tool = "Edit", Path = huge, Paths = new List<string> { huge }, Risk = new RiskResult { Level = "safe" } };
            Assert.IsTrue(Untrusted.Request(req));
            Assert.AreEqual(Untrusted.PathMax, req.Path!.Length);
            Assert.AreEqual(Untrusted.PathMax, req.Paths[0].Length);
            Assert.IsTrue(req.Truncated);
        }

        [Test]
        public void ABidiOverrideInACommandIsShownAndCountsAsCut()
        {
            var req = new PermRequest { Id = "r", Tool = "Bash", Command = "cat notes\u202Etxt.sh", Risk = new RiskResult { Level = "safe" } };
            Untrusted.Request(req);
            Assert.AreEqual("cat notes‹U+202E›txt.sh", req.Command);
            Assert.IsFalse(req.Command!.Contains('\u202E'));
            Assert.IsFalse(req.Truncated, "shown in full (visibly), nothing hidden");
        }

        [Test]
        public void ToolPathsExplanationAndRepliesAreCapped()
        {
            var req = new PermRequest
            {
                Id = "r", Tool = new string('T', 41), Risk = new RiskResult { Level = "network", Reason = new string('r', 500) },
                Paths = Enumerable.Range(0, 7).Select(i => "p" + i).ToList(), Url = "https://x/" + new string('u', 400),
            };
            Assert.IsTrue(Untrusted.Request(req));
            Assert.AreEqual(Untrusted.ToolMax, req.Tool.Length);
            Assert.AreEqual(Untrusted.MaxPaths, req.Paths.Count);
            Assert.LessOrEqual(req.Url!.Length, Untrusted.UrlMax);
            Assert.LessOrEqual(req.Risk.Reason.Length, Untrusted.ExplainMax);
            Assert.IsTrue(req.Truncated);

            var e = new Explanation
            {
                Text = new string('e', 141) + "\n", AiText = "KI\u202E" + new string('a', 200),
                Replies = new List<string> { new string('y', 71), "Nein.\nWirklich." },
            };
            Untrusted.Explain(e);
            Assert.AreEqual(Untrusted.ExplainMax, e.Text.Length);
            Assert.LessOrEqual(e.AiText!.Length, Untrusted.ExplainMax);
            StringAssert.StartsWith("KI‹U+202E›", e.AiText);
            Assert.AreEqual(Untrusted.ReplyMax, e.Replies[0].Length);
            Assert.AreEqual("Nein.¶Wirklich.", e.Replies[1]);
        }

        [Test]
        public void ANormalRequestIsLeftAlone()
        {
            var req = new PermRequest { Id = "r", Tool = "Bash", Command = "npm test", Paths = new List<string> { "<repo>/a.ts" }, Risk = new RiskResult { Level = "safe" } };
            Assert.IsFalse(Untrusted.Request(req));
            Assert.AreEqual("npm test", req.Command);
            Assert.AreEqual("<repo>/a.ts", req.Paths[0]);
            Assert.IsFalse(req.Truncated);
        }

        [Test]
        public void AgentsAndIncidentsAreCapped()
        {
            var a = new AgentState { Sid = "s", Name = "N\u202E" + new string('n', 60), LastAction = new string('l', 300) };
            Untrusted.Agent(a);
            Assert.LessOrEqual(a.Name.Length, Untrusted.NameMax);
            StringAssert.StartsWith("N‹U+202E›", a.Name);
            Assert.LessOrEqual(a.LastAction.Length, Untrusted.ExplainMax);

            var i = new Incident { Kind = "collision", Detail = "a\nb" + new string('d', 300) };
            Untrusted.Incident(i);
            StringAssert.StartsWith("a¶b", i.Detail);
            Assert.LessOrEqual(i.Detail.Length, Untrusted.ExplainMax);
        }

        [Test]
        public void PlainKeepsOurLineBreaksButShowsEveryOtherControl()
        {
            Assert.AreEqual("a\nb→c‹U+202E›", Texts.Plain("a\nb\tc\u202E"));
            Assert.AreEqual("c:\\\u2060x", Texts.Plain("c:\\x"), "backslashes are still defused");
        }

        [Test]
        public void TheSessionCapsWhatComesIn()
        {
            var net = new FakeNet();
            var live = new LiveSource(new HostBook(new MemoryStore()), net.Open);
            var s = new DemoSession(live);
            s.SetViewShown(true);
            s.Select("s1");
            Assert.IsTrue(live.PairWithCode(Bridge.Host, "123456"));
            var sock = net.Last;
            sock.Open();
            sock.Receive(Bridge.EmptySnapshot);
            sock.Receive(Bridge.Paired("tok"));
            sock.Receive("{\"type\":\"perm_open\",\"request\":{\"id\":\"r1\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"ls\\u202E\\nrm -rf /\"," +
                         "\"truncated\":false,\"paths\":[],\"risk\":{\"risk\":\"safe\",\"reason\":\"t\"},\"createdAt\":1,\"expiresAt\":2}," +
                         "\"explain\":{\"text\":\"" + new string('e', 300) + "\",\"replies\":[\"Nein.\"],\"source\":\"rules\"}}");
            for (var i = 0; i < 3; i++) s.Step(20, new HandSample(true, true, 0f, false), new HandSample(true, true, 0f, false));
            Assert.AreEqual("r1", s.Card?.Id);
            Assert.AreEqual("ls‹U+202E›¶rm -rf /", s.Card!.Command);
            Assert.AreEqual(Untrusted.ExplainMax, s.CardExplain!.Text.Length);
        }
    }
}
