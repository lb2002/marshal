#nullable enable
using System.IO;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;

namespace Lotse.App.Tests
{
    /// <summary>
    /// The facts row of a dangerous request (U7): perm_impact and snapshot.impacts parsed fail soft; the row's texts for
    /// both verdicts, capped counts and the 2 s "Checking effects"; an impact arriving or changing cancels a hold; the
    /// verdict never changes the gesture; only two-hand requests get the row; the demo pack's two impacts.
    /// </summary>
    public class ImpactTests
    {
        const double Frame = 20;
        static readonly HandSample Open = new HandSample(true, true, 0f, false);
        static readonly HandSample Pinch = new HandSample(true, true, 1f, false);

        FakeNet _net = null!;
        LiveSource _live = null!;
        DemoSession _s = null!;
        FakeSocket _socket = null!;

        const string AgentJson = "{\"sid\":\"s1\",\"name\":\"A\",\"status\":\"waiting\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}";
        const string Agent1 = "{\"type\":\"agent\",\"agent\":" + AgentJson + "}";

        static string Req(string id, string risk) =>
            "{\"id\":\"" + id + "\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"rm -rf ../legacy-site\",\"truncated\":false,\"paths\":[]," +
            "\"risk\":{\"risk\":\"" + risk + "\",\"reason\":\"t\"},\"createdAt\":1,\"expiresAt\":90000}";

        const string Explain = "{\"text\":\"Deletes\",\"replies\":[],\"source\":\"rules\"}";

        static string PermOpen(string id, string risk) => "{\"type\":\"perm_open\",\"request\":" + Req(id, risk) + ",\"explain\":" + Explain + "}";

        static string Snapshot(string impact) => "{\"type\":\"snapshot\",\"agents\":[" + AgentJson + "],\"pending\":[" + Req("r1", "destructive") +
                                                 "],\"explains\":{\"r1\":" + Explain + "},\"impacts\":{\"r1\":" + impact + "}}";

        const string GoneImpact = "{\"kind\":\"delete\",\"target\":\"~/code/legacy-site\",\"files\":214,\"bytes\":38000000,\"outsideProject\":true," +
                                  "\"recoverable\":\"no\",\"reason\":\"Not in a Git project: gone for good.\"}";

        static string PermImpact(string id, string impact) => "{\"type\":\"perm_impact\",\"id\":\"" + id + "\",\"impact\":" + impact + "}";

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

        int Allows() => _socket.Sent.Count(f => f.Contains("\"allow\""));

        // ---- wire ----

        [Test]
        public void PermImpactIsParsedFailSoftCappedAndPlain()
        {
            var m = Protocol.Parse(PermImpact("r1", GoneImpact)) as PermImpactMessage;
            Assert.IsNotNull(m);
            Assert.AreEqual("r1", m!.Id);
            var i = m.Impact;
            Assert.AreEqual("delete", i.Kind);
            Assert.AreEqual("~/code/legacy-site", i.Target);
            Assert.AreEqual(214, i.Files);
            Assert.AreEqual(38000000, i.Bytes);
            Assert.AreEqual(true, i.OutsideProject);
            Assert.AreEqual("no", i.Recoverable);

            foreach (var other in new[] { "\"yes\"", "\"partly\"", "true", "1", "null", "\"No\"" })
            {
                var o = Protocol.Parse(PermImpact("r1", "{\"recoverable\":" + other + ",\"reason\":\"x\"}")) as PermImpactMessage;
                Assert.AreEqual("unknown", o!.Impact.Recoverable, "any other recoverable value is unknown: " + other);
            }
            var odd = (Protocol.Parse(PermImpact("r1", "{\"kind\":\"nuke\",\"files\":-3,\"bytes\":\"many\",\"outsideProject\":\"yes\",\"target\":5}")) as PermImpactMessage)!.Impact;
            Assert.AreEqual("other", odd.Kind);
            Assert.IsNull(odd.Files);
            Assert.IsNull(odd.Bytes);
            Assert.IsNull(odd.OutsideProject);
            Assert.IsNull(odd.Target);
            var longText = new string('x', 300);
            var cut = (Protocol.Parse(PermImpact("r1", "{\"target\":\"" + longText + "\",\"reason\":\"" + longText + "\"}")) as PermImpactMessage)!.Impact;
            Assert.LessOrEqual(cut.Target!.Length, Impact.TextMax);
            Assert.LessOrEqual(cut.Reason.Length, Impact.TextMax);

            Assert.IsNull(Protocol.Parse(PermImpact("r1", "\"bad\"")), "a malformed impact is no impact");
            Assert.IsNull(Protocol.Parse("{\"type\":\"perm_impact\",\"impact\":" + GoneImpact + "}"), "no id");
            var snap = Protocol.Parse("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{},\"impacts\":{\"r1\":" + GoneImpact + ",\"r2\":7}}") as SnapshotMessage;
            Assert.AreEqual(1, snap!.Impacts.Count, "the malformed one is left out");
            Assert.AreEqual("no", snap.Impacts["r1"].Recoverable);
        }

        // ---- the row's texts ----

        [Test]
        public void TheRowSaysWhatWasCheckedWhatItWouldHitAndTheVerdict()
        {
            var gone = ImpactFacts.For((Protocol.Parse(PermImpact("r1", GoneImpact)) as PermImpactMessage)!.Impact, 0);
            Assert.AreEqual("Checked: ~/code/legacy-site", gone.Checked);
            Assert.AreEqual("214 files, 38 MB", gone.Counts);
            Assert.AreEqual("outside the project", gone.Where);
            Assert.AreEqual(ImpactVerdict.GoneForGood, gone.Verdict);
            Assert.AreEqual("Gone for good", gone.VerdictText);
            Assert.AreEqual("Not in a Git project: gone for good.", gone.Reason);

            var unknown = ImpactFacts.For(new Impact { Target = "src", OutsideProject = false, Recoverable = "unknown",
                Reason = "In a Git project. Whether everything is saved is not checked." }, 0);
            Assert.AreEqual("inside the project", unknown.Where);
            Assert.AreEqual("Effects unknown", unknown.VerdictText);
            Assert.IsNull(unknown.Counts, "no counts: no cell");

            var capped = ImpactFacts.For(new Impact { Files = 20000, FilesCapped = true }, 0);
            Assert.AreEqual("at least 20,000 files", capped.Counts);
            Assert.IsNull(capped.Checked, "no target: no Checked line");
            Assert.IsNull(capped.Where, "unknown where: no cell");
            Assert.AreEqual("1 file", ImpactFacts.For(new Impact { Files = 1 }, 0).Counts);

            var chain = ImpactFacts.For(new Impact { Kind = "other", Reason = "Target unclear. Effects unknown." }, 0);
            Assert.AreEqual(ImpactVerdict.Unknown, chain.Verdict);
            Assert.AreEqual("Target unclear. Effects unknown.", chain.Reason);
        }

        [Test]
        public void WithoutAnImpactItIsCheckingForTwoSecondsThenUnknown()
        {
            Assert.AreEqual(Texts.CheckingEffects, ImpactFacts.For(null, 0).VerdictText);
            Assert.AreEqual(Texts.CheckingEffects, ImpactFacts.For(null, ImpactFacts.CheckingMs - 1).VerdictText);
            Assert.AreEqual(Texts.EffectsUnknown, ImpactFacts.For(null, ImpactFacts.CheckingMs).VerdictText);
            Assert.IsNull(ImpactFacts.For(null, 0).Reason);
            // nothing in the row ever reassures
            foreach (var t in new[] { Texts.GoneForGood, Texts.EffectsUnknown, Texts.CheckingEffects, Texts.InsideProject, Texts.OutsideProject })
                foreach (var word in new[] { "safe", "recoverable", "OK", "√", "✓", "no risk" })
                    StringAssert.DoesNotContain(word, t);
        }

        // ---- the session ----

        void OpenDangerous(string id = "r1")
        {
            _socket.Receive(PermOpen(id, "destructive"));
            Step(3 * Frame);
            Assert.AreEqual(id, _s.Card?.Id);
        }

        [Test]
        public void OnlyATwoHandRequestHasTheRowAndALateImpactStillReplacesTheWaitingText()
        {
            _socket.Receive(PermOpen("r0", "safe"));
            Step(3 * Frame);
            Assert.IsNull(_s.CardFacts, "a pinch-hold request has no facts row");
            _socket.Receive("{\"type\":\"perm_closed\",\"id\":\"r0\",\"reason\":\"deny\"}");
            Step(Frame);

            OpenDangerous();
            Assert.AreEqual(ImpactVerdict.Checking, _s.CardFacts!.Value.Verdict);
            Step(ImpactFacts.CheckingMs);
            Assert.AreEqual(ImpactVerdict.Unknown, _s.CardFacts!.Value.Verdict);
            _socket.Receive(PermImpact("r1", GoneImpact));
            Step(Frame);
            Assert.AreEqual(ImpactVerdict.GoneForGood, _s.CardFacts!.Value.Verdict, "an impact that arrives later still replaces it");
        }

        [Test]
        public void AnImpactArrivingCancelsAHoldAndANewTwoHandHoldIsNeeded()
        {
            OpenDangerous();
            Step(GestureMachine.TwoHandHoldMs * 0.6, Pinch, Pinch);
            Assert.Greater(_s.HoldProgress, 0f, "a two-hand hold runs");
            _socket.Receive(PermImpact("r1", GoneImpact));
            Step(Frame, Pinch, Pinch);
            Assert.AreEqual(0f, _s.HoldProgress, "the impact arriving cancels the hold");
            Step(GestureMachine.TwoHandHoldMs * 2, Pinch, Pinch);
            Assert.AreEqual(0, Allows(), "the pinches held across it never approve");
            Step(2 * Frame);
            Step(GestureMachine.TwoHandHoldMs + 3 * Frame, Pinch, Pinch);
            Assert.AreEqual(1, Allows(), "a fresh two-hand hold approves");
        }

        [Test]
        public void AnImpactChangingInASnapshotCancelsAHoldButTheSameOneDoesNot()
        {
            OpenDangerous();
            _socket.Receive(PermImpact("r1", GoneImpact));
            Step(3 * Frame);
            Step(GestureMachine.TwoHandHoldMs * 0.5, Pinch, Pinch);
            Assert.Greater(_s.HoldProgress, 0f);
            _socket.Receive(Snapshot(GoneImpact));
            Step(Frame, Pinch, Pinch);
            Assert.Greater(_s.HoldProgress, 0f, "the same impact again changes nothing");
            _socket.Receive(Snapshot(GoneImpact.Replace("214", "215")));
            Step(Frame, Pinch, Pinch);
            Assert.AreEqual(0f, _s.HoldProgress, "a changed impact cancels the hold");
        }

        [Test]
        public void TheVerdictNeverChangesTheGestureNeeded()
        {
            // a destructive request with an "unknown" impact still needs both hands
            OpenDangerous();
            _socket.Receive(PermImpact("r1", "{\"recoverable\":\"unknown\",\"reason\":\"x\"}"));
            Step(3 * Frame);
            Assert.IsTrue(_s.Card!.NeedsTwoHand);
            Step(5000, Open, Pinch);
            Assert.AreEqual(0, Allows(), "one hand never approves it");
            _socket.Receive("{\"type\":\"perm_closed\",\"id\":\"r1\",\"reason\":\"deny\"}");
            Step(Frame);

            // a harmless request that (against the contract) gets an impact still takes the pinch, and shows no row
            _socket.Receive(PermOpen("r2", "safe"));
            Step(3 * Frame);
            _socket.Receive(PermImpact("r2", GoneImpact));
            Step(3 * Frame);
            Assert.IsFalse(_s.Card!.NeedsTwoHand);
            Assert.IsNull(_s.CardFacts);
            Step(GestureMachine.PinchHoldMs + 3 * Frame, Open, Pinch);
            Assert.AreEqual(1, _socket.Sent.Count(f => f.Contains("\"allow\"") && f.Contains("pinch-hold")));
        }

        [Test]
        public void AnImpactForARequestThatIsNotOpenIsDropped()
        {
            _socket.Receive(PermImpact("nope", GoneImpact));
            Step(Frame);
            Assert.IsNull(_s.Store.ImpactFor("nope"));
            OpenDangerous();
            _socket.Receive(PermImpact("r1", GoneImpact));
            Step(Frame);
            Assert.IsNotNull(_s.Store.ImpactFor("r1"));
            _socket.Receive("{\"type\":\"perm_closed\",\"id\":\"r1\",\"reason\":\"deny\"}");
            Step(Frame);
            Assert.IsNull(_s.Store.ImpactFor("r1"), "it goes with its request");
        }

        // ---- the demo ----

        static string PackPath => Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json");

        [Test]
        public void TheDemosTwoDangerousRequestsCarryTheirImpactsFromTheMomentTheyOpen()
        {
            var pack = DemoPack.Parse(File.ReadAllText(PackPath));
            Assert.AreEqual(0, pack.SkippedUnknown);
            var s = new DemoSession(pack);
            var seen = 0;
            var guard = 0;
            while (!s.Finished && guard++ < 200000)
            {
                s.Step(Frame, Open, Open);
                foreach (var p in s.Store.Pending.ToList())
                {
                    if (!p.Risk.IsDestructive) continue;
                    var i = s.Store.ImpactFor(p.Id);
                    Assert.IsNotNull(i, p.Id + " has its impact while it waits");
                    Assert.AreEqual("no", i!.Recoverable);
                    seen++;
                    s.Select(p.Sid);
                    s.SetViewShown(true);
                    s.Step(Frame, Open, Open);
                    Assert.AreEqual(ImpactVerdict.GoneForGood, s.CardFacts!.Value.Verdict);
                    Assert.IsTrue(s.Deny(p.Id));
                }
                foreach (var p in s.Store.Pending.ToList())
                {
                    s.Select(p.Sid);
                    s.SetViewShown(true);
                    s.Step(Frame, Open, Open);
                    if (s.Card != null && s.Mode == CardMode.Hold) s.Deny(s.Card.Id);
                }
            }
            Assert.AreEqual(2, seen, "the rm -rf and the force-push");
        }
    }
}
