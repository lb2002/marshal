#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.App.Tests
{
    /// <summary>
    /// Halt-all (U7) in the session: the wire format, the state shown follows only the bridge's halt_state (and the
    /// snapshot), no block and no notice while halted, release only on request, the demo pack's local halt, the texts.
    /// </summary>
    public class HaltTests
    {
        const double Frame = 20;
        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);

        FakeNet _net = null!;
        LiveSource _live = null!;
        DemoSession _s = null!;
        FakeSocket _socket = null!;

        static string HaltState(bool on, string enforcement = "{\"claude\":\"every-step\"}") =>
            "{\"type\":\"halt_state\",\"on\":" + (on ? "true" : "false") + ",\"since\":1000,\"enforcement\":" + enforcement + "}";

        const string Agent1 = "{\"type\":\"agent\",\"agent\":{\"sid\":\"s1\",\"name\":\"A\",\"status\":\"waiting\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}";

        static string PermOpen(string id, string risk = "safe") =>
            "{\"type\":\"perm_open\",\"request\":{\"id\":\"" + id + "\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"ls\",\"truncated\":false,\"paths\":[]," +
            "\"risk\":{\"risk\":\"" + risk + "\",\"reason\":\"t\"},\"createdAt\":1,\"expiresAt\":90000},\"explain\":{\"text\":\"Lists\",\"replies\":[],\"source\":\"rules\"}}";

        [SetUp]
        public void SetUp()
        {
            _net = new FakeNet();
            _live = new LiveSource(new HostBook(new MemoryStore()), _net.Open);
            _s = new DemoSession(_live);
            Assert.IsTrue(_live.PairWithCode(Bridge.Host, "123456"));
            _socket = _net.Last;
            _socket.Open();
            _socket.Receive(Bridge.EmptySnapshot);
            _s.SetViewShown(true);
            _s.Select("s1");
            _socket.Receive(Agent1);
            Step(Frame);
        }

        void Step(double ms, HandSample? left = null, HandSample? right = null)
        {
            for (double t = 0; t < ms; t += Frame) _s.Step(Frame, left ?? Open, right ?? Open);
        }

        string[] Sent(string type) => _socket.Sent.Where(f => f.Contains("\"type\":\"" + type + "\"")).ToArray();

        // ---- wire ----

        [Test]
        public void HaltStateIsParsedFailSoftAndTheHaltMessageIsSerialised()
        {
            var h = Protocol.Parse(HaltState(true, "{\"claude\":\"every-step\",\"muse\":\"requests-only\",\"x\":\"weird\",\"y\":3}")) as HaltStateMessage;
            Assert.IsNotNull(h);
            Assert.IsTrue(h!.On);
            Assert.AreEqual(1000, h.Since);
            Assert.AreEqual("every-step", h.Enforcement["claude"]);
            Assert.AreEqual("requests-only", h.Enforcement["muse"]);
            Assert.AreEqual("none", h.Enforcement["x"], "an unknown word reads none");
            Assert.IsFalse(h.Enforcement.ContainsKey("y"), "a value that is no string is skipped");

            Assert.IsNull(Protocol.Parse("{\"type\":\"halt_state\",\"on\":\"yes\"}"), "no boolean on: dropped, never guessed");
            Assert.IsNull(Protocol.Parse("{\"type\":\"halt_state\"}"));
            var snap = Protocol.Parse("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{},\"halt\":{\"on\":true,\"enforcement\":{}}}") as SnapshotMessage;
            Assert.IsTrue(snap!.Halt!.On);
            var old = Protocol.Parse(Bridge.EmptySnapshot) as SnapshotMessage;
            Assert.IsNull(old!.Halt);

            Assert.AreEqual("{\"type\":\"halt\",\"on\":true}", Protocol.Serialize(new HaltMessage { On = true }));
            Assert.AreEqual("{\"type\":\"halt\",\"on\":false}", Protocol.Serialize(new HaltMessage { On = false }));
        }

        // ---- the state follows the bridge only ----

        [Test]
        public void RequestingAHaltSendsOneHaltButTheStateWaitsForTheBridge()
        {
            Assert.IsTrue(_s.RequestHalt());
            CollectionAssert.AreEqual(new[] { "{\"type\":\"halt\",\"on\":true}" }, Sent("halt"));
            Step(Frame);
            Assert.IsFalse(_s.Halted, "never a local guess: halted only once the bridge says so");

            _socket.Receive(HaltState(true));
            Step(Frame);
            Assert.IsTrue(_s.Halted);
            Assert.AreEqual("every-step", _s.Enforcement["claude"]);

            _socket.Receive(HaltState(false, "{}"));
            Step(Frame);
            Assert.IsFalse(_s.Halted);
        }

        [Test]
        public void SendingHaltTwiceOrALateAnswerNeverTogglesTheStateBack()
        {
            _socket.Receive(HaltState(true));
            Step(Frame);
            Assert.IsTrue(_s.RequestHalt());
            Assert.IsTrue(_s.RequestHalt());
            _socket.Receive(HaltState(true)); // the late answers
            _socket.Receive(HaltState(true));
            Step(Frame);
            Assert.IsTrue(_s.Halted);
            Assert.AreEqual(0, Sent("halt").Count(f => f.Contains("false")), "no path here sends a release");
        }

        [Test]
        public void ASnapshotAfterAReconnectCarriesTheHaltAndOneWithoutItIsNotHalted()
        {
            _socket.Receive("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{},\"halt\":{\"on\":true,\"enforcement\":{\"codex\":\"every-step\"}}}");
            Step(Frame);
            Assert.IsTrue(_s.Halted);
            _socket.Receive(Bridge.EmptySnapshot);
            Step(Frame);
            Assert.IsFalse(_s.Halted, "an older bridge cannot halt");
        }

        [Test]
        public void WithoutAConnectionNothingIsSentAndNothingIsHalted()
        {
            _live.Disconnect();
            Assert.IsFalse(_s.RequestHalt());
            Assert.IsFalse(_s.Halted);
        }

        [Test]
        public void WhileHaltedThereIsNoBlockNoNoticeAndARunningHoldIsCancelled()
        {
            var notices = 0;
            _s.NoticeRaised += _ => notices++;
            _socket.Receive(PermOpen("r1"));
            Step(3 * Frame);
            Assert.AreEqual("r1", _s.Card?.Id);
            Step(GestureMachine.PinchHoldMs * 0.5, Open, Pinch);
            Assert.Greater(_s.HoldProgress, 0f, "a hold runs");

            _socket.Receive(HaltState(true));
            Step(Frame, Open, Pinch);
            Assert.IsNull(_s.Card, "no block while halted");
            Assert.AreEqual(0f, _s.HoldProgress);
            Step(3000, Open, Pinch);
            CollectionAssert.IsEmpty(Sent("perm_decision"), "nothing is approved while halted");

            _socket.Receive("{\"type\":\"agent\",\"agent\":{\"sid\":\"s2\",\"name\":\"B\",\"status\":\"waiting\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}");
            _socket.Receive(PermOpen("r2").Replace("\"sid\":\"s1\"", "\"sid\":\"s2\""));
            Step(10000);
            Assert.AreEqual(0, notices, "no notice while halted");

            _socket.Receive(HaltState(false, "{}"));
            Step(3 * Frame);
            Assert.AreEqual("r1", _s.Card?.Id, "released: the block is back");
            Step(GestureMachine.PinchHoldMs * 0.5, Open, Pinch);
            CollectionAssert.IsEmpty(Sent("perm_decision"), "the pinch held across the halt never approves: a fresh one is needed");
        }

        [Test]
        public void ReleaseIsSentOnlyWhenRequested()
        {
            _socket.Receive(HaltState(true));
            Step(5000);
            Assert.AreEqual(0, Sent("halt").Length);
            Assert.IsTrue(_s.RequestRelease());
            CollectionAssert.AreEqual(new[] { "{\"type\":\"halt\",\"on\":false}" }, Sent("halt"));
            Assert.IsTrue(_s.Halted, "still halted until the bridge says otherwise");
        }

        [Test]
        public void ThePermClosedReasonHaltReadsAsDenied()
        {
            Assert.AreEqual(Texts.HaltDenied, Texts.Closed("halt", null));
            StringAssert.StartsWith("Denied", Texts.HaltDenied);
        }

        // ---- the demo ----

        static string PackPath => Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json");

        [Test]
        public void TheDemoHaltsLocallyDenyingTheOpenRequestAndPausingUntilReleased()
        {
            var s = new DemoSession(DemoPack.Parse(File.ReadAllText(PackPath)));
            var closed = new List<(string id, string reason)>();
            s.RequestClosed += (r, reason, sent) => closed.Add((r.Id, reason));
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100000) s.Step(Frame, Open, Open);
            var open = s.Store.Pending[0].Id;

            Assert.IsTrue(s.RequestHalt());
            Assert.IsTrue(s.Halted);
            CollectionAssert.Contains(closed, (open, "halt"), "the open demo request shows as denied with the halt message");
            Assert.AreEqual(0, s.Decisions.Count(d => d.Behavior == "allow"), "never an allow");
            Assert.AreEqual(4, s.Enforcement.Count);
            Assert.IsTrue(s.Enforcement.Values.All(v => v == "every-step"));
            StringAssert.Contains("stop before their next step", Texts.Enforcement(s.Enforcement));

            var agents = s.Store.Agents.Select(a => a.Status + a.LastAction).ToArray();
            for (var i = 0; i < 1000; i++) s.Step(Frame, Open, Open);
            CollectionAssert.AreEqual(agents, s.Store.Agents.Select(a => a.Status + a.LastAction).ToArray(), "playback stands still");
            Assert.AreEqual(0, s.Store.Pending.Count);

            Assert.IsTrue(s.RequestRelease());
            Assert.IsFalse(s.Halted);
            guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 100000) s.Step(Frame, Open, Open);
            Assert.AreEqual(1, s.Store.Pending.Count, "playback resumed up to the next request");
        }

        // ---- texts ----

        [Test]
        public void TheEnforcementLineNamesEverySourceByWhatTheBridgeCanDo()
        {
            string E(params (string, string)[] kv) => Texts.Enforcement(kv.ToDictionary(p => p.Item1, p => p.Item2));
            Assert.AreEqual("Claude Code and Codex stop before their next step. Muse: only requests are denied.",
                E(("muse", "requests-only"), ("codex", "every-step"), ("claude", "every-step")));
            Assert.AreEqual("Claude Code stops before its next step.", E(("claude", "every-step")));
            Assert.AreEqual("Gemini CLI keeps running.", E(("gemini", "none")));
            Assert.AreEqual("Claude Code, Codex and Muse stop before their next step. Gemini CLI keeps running.",
                E(("claude", "every-step"), ("codex", "every-step"), ("muse", "every-step"), ("gemini", "none")));
            Assert.AreEqual("Muse and Gemini CLI: only requests are denied.", E(("muse", "requests-only"), ("gemini", "requests-only")));
            Assert.AreEqual("Codex stops before its next step. Other agents keep running.",
                E(("codex", "every-step"), ("<b>evil</b>", "none"), ("zz", "none")));
            Assert.AreEqual("", E());
            // every combination of the four sources and three modes: each named source appears once
            var modes = new[] { "every-step", "requests-only", "none" };
            var sources = new[] { "claude", "codex", "muse", "gemini" };
            var names = new[] { "Claude Code", "Codex", "Muse", "Gemini CLI" };
            for (var mask = 1; mask < 256; mask++)
            {
                var d = new Dictionary<string, string>();
                for (var i = 0; i < 4; i++)
                {
                    var m = (mask >> (2 * i)) & 3;
                    if (m < 3) d[sources[i]] = modes[m];
                }
                var line = Texts.Enforcement(d);
                for (var i = 0; i < 4; i++)
                    Assert.AreEqual(d.ContainsKey(sources[i]) ? 1 : 0, CountOf(line, names[i]), line);
            }
        }

        static int CountOf(string s, string word)
        {
            var n = 0;
            for (var i = s.IndexOf(word, System.StringComparison.Ordinal); i >= 0; i = s.IndexOf(word, i + word.Length, System.StringComparison.Ordinal)) n++;
            return n;
        }

        [Test]
        public void TheBandSaysHowLongItHasBeenHalted()
        {
            Assert.AreEqual("Halted for 0 s", Texts.HaltedFor(0));
            Assert.AreEqual("Halted for 12 s", Texts.HaltedFor(12900));
            Assert.AreEqual("Halted for 3 min", Texts.HaltedFor(200000));
        }
    }
}
