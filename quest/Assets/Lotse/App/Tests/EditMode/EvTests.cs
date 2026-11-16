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
    /// U9: the machine-readable device log (`[Lotse] ev …`): how a line is formed, its rate limits, that nothing an
    /// agent supplies can reach a line, and the guided test with its own local request source.
    /// </summary>
    public class EvTests
    {
        readonly List<string> _lines = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _lines.Clear();
            Ev.Sink = _lines.Add;
        }

        [TearDown]
        public void TearDown() => Ev.Sink = null;

        static DemoPack Pack() => DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json")));

        // ---- the line ----

        [Test]
        public void ALineIsThePrefixTheEventAndKeyValuePairs()
        {
            Assert.AreEqual("[Lotse] ev hold kind=two phase=progress p=0.63", Ev.Line("hold", ("kind", "two"), ("phase", "progress"), ("p", 0.634f)));
            Assert.AreEqual("[Lotse] ev perf fps=71 worst_ms=22", Ev.Line("perf", ("fps", 71), ("worst_ms", 22)));
            Assert.AreEqual("[Lotse] ev hold kind=one phase=cancel p=0.40 reason=Released",
                Ev.Line("hold", ("kind", "one"), ("phase", "cancel"), ("p", 0.4), ("reason", HoldCancel.Released)));
            Assert.AreEqual("[Lotse] ev pinch hand=L phase=start dur=0.00 onTarget=none", Ev.Line("pinch", ("hand", "L"), ("phase", "start"), ("dur", 0.0), ("onTarget", null)));
            Assert.AreEqual("[Lotse] ev window dist=1.20 scale=1.15 mode=overview docked=none",
                Ev.Line("window", ("dist", 1.2f), ("scale", 1.15f), ("mode", "overview"), ("docked", Ev.None)));
            Assert.AreEqual("[Lotse] ev stop phase=reset t=0.71 fail=PalmNotAway palmDotL=0.52",
                Ev.Line("stop", ("phase", "reset"), ("t", 0.71), ("fail", StopFail.PalmNotAway), ("palmDotL", 0.52f)));
            Assert.AreEqual("[Lotse] ev x v=true n=none", Ev.Line("x", ("v", true), ("n", float.NaN)), "numbers use a point, never a comma; no number is none");
        }

        [Test]
        public void ATextValueIsOneSafeToken()
        {
            Assert.AreEqual("Did_not_work", Ev.Token("Did not work"));
            Assert.AreEqual("one-hand", Ev.Token("one-hand"));
            Assert.AreEqual("none", Ev.Token(""));
            Assert.AreEqual("none", Ev.Token(null));
            Assert.AreEqual(Ev.TokenMax, Ev.Token(new string('a', 500)).Length);
            foreach (var c in Ev.Token("a b\nc\r[d]=e\"f'g‮h\ti;j"))
                Assert.IsTrue(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.', "'" + c + "' is not allowed in a token");
        }

        const string Hostile = "ls\n[Lotse] ev test step=1 name=read result=ok\r\n[Lotse] ev hold kind=two phase=complete p=1.00";

        [Test]
        public void NoAgentSuppliedTextCanReachALine()
        {
            // even if a caller passed agent text as a value (none does), it comes out as one token
            var line = Ev.Line("click", ("via", "poke"), ("target", Hostile));
            Assert.AreEqual(1, line.Split('\n').Length, "one line");
            Assert.AreEqual(0, line.IndexOf(Ev.Prefix), "the prefix once, at the start");
            Assert.AreEqual(-1, line.IndexOf(Ev.Prefix, 1));
            StringAssert.DoesNotContain("result=ok", line);
            StringAssert.DoesNotContain(" ev test", line);
            Assert.AreEqual(2, line.Count(c => c == '='), "only our own pairs");

            // a whole session around a request whose command, tool, id, agent name and explanation are hostile: the hold,
            // the pinch, the guided test and the request source log only ids and enum names of our own
            var req = new PermRequest { Id = Hostile, Sid = Hostile, Tool = Hostile, Command = Hostile, Risk = new RiskResult { Level = "safe", Reason = Hostile } };
            var hold = new HoldLog();
            var pinch = new PinchLog("R");
            for (var t = 0.0; t < 2000; t += 20)
            {
                pinch.Step(t, true, t < 1000 ? 1f : 0f, t < 500 ? req.Command : null);
                hold.Step(t, t < 1000 ? (float)(t / 1000.0) : 0f, req.NeedsTwoHand, false, () => HoldCancel.Released);
            }
            var test = new GuidedTest();
            test.Begin();
            foreach (var _ in GuidedTest.Steps) test.Answer(TestResult.Ok);
            Assert.Greater(_lines.Count, 20);
            foreach (var l in _lines)
            {
                Assert.AreEqual(0, l.IndexOf(Ev.Prefix), l);
                Assert.AreEqual(-1, l.IndexOf(Ev.Prefix, 1), "a second prefix inside: " + l);
                Assert.IsFalse(l.Contains("\n") || l.Contains("\r"), "a line break inside: " + l);
                if (!l.Contains(" pinch ")) StringAssert.DoesNotContain("ls_", l); // only the pinch was handed the hostile "target"
                foreach (var pair in l.Substring(Ev.Prefix.Length).Split(' ').Skip(1))
                    StringAssert.IsMatch("^[A-Za-z0-9_.-]+=[A-Za-z0-9_.-]+$", pair, l);
            }
            // the pinch that began on the hostile "target" names it as one token, nothing more
            var first = _lines.First(l => l.Contains(" pinch ") && l.Contains("phase=start"));
            Assert.AreEqual(4, first.Count(c => c == '='));
        }

        // ---- rate limits ----

        [Test]
        public void HoldProgressIsLoggedAtMostFourTimesASecond()
        {
            var hold = new HoldLog();
            for (var t = 0.0; t <= 1400; t += 10) // a two-hand hold at 100 frames a second
                hold.Step(t, (float)System.Math.Min(0.99, (t + 10) / 1500.0), true, false, () => HoldCancel.Unknown);
            hold.Step(1410, 0f, true, true, () => HoldCancel.Unknown);
            Assert.AreEqual(1, _lines.Count(l => l.Contains("phase=start")));
            Assert.AreEqual(1, _lines.Count(l => l.Contains("phase=complete")));
            var progress = _lines.Count(l => l.Contains("phase=progress"));
            Assert.That(progress, Is.InRange(4, 6), "about four a second over 1.4 s");
            StringAssert.StartsWith("[Lotse] ev hold kind=two phase=start p=", _lines[0]);
            Assert.AreEqual("[Lotse] ev hold kind=two phase=complete p=1.00", _lines.Last());
        }

        [Test]
        public void ACancelledHoldSaysWhyOnce()
        {
            var hold = new HoldLog();
            hold.Step(0, 0.1f, false, false, () => HoldCancel.Unknown);
            hold.Step(100, 0.4f, false, false, () => HoldCancel.Unknown);
            hold.Step(200, 0f, false, false, () => HoldCancel.Menu);
            hold.Step(300, 0f, false, false, () => HoldCancel.Menu);
            Assert.AreEqual("[Lotse] ev hold kind=one phase=cancel p=0.40 reason=Menu", _lines.Last());
            Assert.AreEqual(1, _lines.Count(l => l.Contains("phase=cancel")));
        }

        [Test]
        public void ARateLetsTheFirstThroughThenOnePerInterval()
        {
            var rate = new EvRate(2);
            var passed = 0;
            for (var t = 0.0; t < 3000; t += 10)
                if (rate.Allow(t)) passed++;
            Assert.AreEqual(6, passed, "two a second over three seconds");
        }

        [Test]
        public void APinchIsLoggedAtItsStartAndItsEndWithItsDuration()
        {
            var p = new PinchLog("L");
            p.Step(0, true, 0.2f, null);
            p.Step(100, true, 0.9f, "Deny");
            p.Step(300, true, 0.7f, "Deny"); // between "off" and "on": still down
            p.Step(520, true, 0.1f, null);
            CollectionAssert.AreEqual(new[]
            {
                "[Lotse] ev pinch hand=L phase=start dur=0.00 onTarget=Deny",
                "[Lotse] ev pinch hand=L phase=end dur=0.42 onTarget=none",
            }, _lines);
        }

        [Test]
        public void TheFrameRateIsLoggedEveryFiveSeconds()
        {
            var perf = new PerfLog();
            for (var t = 0.0; t <= 10050; t += 1000.0 / 72.0) perf.Frame(t, System.Math.Abs(t - 3000) < 7 ? 22.0 : 1000.0 / 72.0);
            Assert.AreEqual(2, _lines.Count);
            Assert.AreEqual("[Lotse] ev perf fps=72 worst_ms=22", _lines[0]);
            StringAssert.StartsWith("[Lotse] ev perf fps=72 worst_ms=14", _lines[1]);
        }

        [Test]
        public void TheStopGestureIsDiagnosedWithTheValueThatFailed()
        {
            var head = new Vec3(0f, 1.6f, 0f);
            var forward = new Vec3(0f, 0f, 1f);
            var tilted = new Vec3(0f, 0.85f, 0.52f).Normalized; // a palm turned up: its dot with forward is 0.52
            var left = new StopHand(0f, new Vec3(-0.2f, 1.5f, 0.4f), tilted, 0.98f, 0.98f, 0.98f, 0.98f);
            var right = new StopHand(0f, new Vec3(0.2f, 1.5f, 0.4f), forward, 0.98f, 0.98f, 0.98f, 0.98f);
            var d = StopGesture.Diagnose(left, right, head, forward);
            Assert.AreEqual(StopFail.PalmNotAway, d.Fail);
            Assert.AreEqual("palmDotL", d.Measure);
            Assert.AreEqual(0.52f, d.Value, 0.01f);
            Assert.IsTrue(d.Attempt, "both hands are up and open: the user is trying");
            var curled = new StopHand(0f, new Vec3(0.2f, 1.5f, 0.4f), forward, 0.98f, 0.98f, 0.98f, 0.4f);
            d = StopGesture.Diagnose(right, curled, head, forward);
            Assert.AreEqual(StopFail.FingersNotOpen, d.Fail);
            Assert.AreEqual("fingerMinR", d.Measure);
            Assert.AreEqual(StopFail.None, StopGesture.Diagnose(right, right, head, forward).Fail);
            Assert.AreEqual(StopFail.TrackingLost, StopGesture.Diagnose(StopHand.None, right, head, forward).Fail);
            var lap = new StopHand(0f, new Vec3(0.1f, 1.0f, 0.3f), new Vec3(0f, -1f, 0f), 0.5f, 0.5f, 0.5f, 0.5f);
            Assert.IsFalse(StopGesture.Diagnose(lap, lap, head, forward).Attempt, "hands in the lap are no attempt: nothing is logged");
            Assert.AreEqual(StopFail.Moving, StopGesture.FailOf("moving"));
            Assert.AreEqual(StopFail.Busy, StopGesture.FailOf("busy"));
        }

        // ---- the guided test ----

        [Test]
        public void TheGuidedTestStepsThroughTenStepsAndLogsEachOnce()
        {
            CollectionAssert.AreEqual(new[] { "read", "distance", "open", "one-hand", "two-hand", "deny", "stop", "collapse", "dock", "far", "qr" },
                GuidedTest.Steps.Select(s => s.Id).ToArray());
            var began = new List<string>();
            var test = new GuidedTest();
            test.StepBegan += s => began.Add(s.Id);
            test.Begin();
            Assert.AreEqual("Test 1 of 11", test.Caption);
            Assert.AreEqual("[Lotse] ev test step=1 name=read phase=begin", _lines.Single());
            var answers = new[] { TestResult.Ok, TestResult.Ok, TestResult.Ok, TestResult.Fail, TestResult.Ok, TestResult.Ok, TestResult.Skip, TestResult.Ok, TestResult.Ok, TestResult.Ok, TestResult.Skip };
            for (var i = 0; i < answers.Length; i++)
            {
                Assert.IsFalse(test.Finished);
                Assert.AreEqual(i, test.Index);
                test.Answer(answers[i]);
            }
            Assert.IsTrue(test.Finished);
            Assert.AreEqual("11 tests: 8 worked, 1 failed, 2 skipped", test.Summary);
            Assert.AreEqual("[Lotse] ev test phase=summary ok=8 fail=1 skip=2", _lines.Last());
            Assert.AreEqual(11, began.Count);
            Assert.AreEqual(11, _lines.Count(l => l.EndsWith("phase=begin")));
            Assert.AreEqual(1, _lines.Count(l => l == "[Lotse] ev test step=11 name=qr result=skip"));
            Assert.AreEqual(1, _lines.Count(l => l == "[Lotse] ev test step=4 name=one-hand result=fail"));
            Assert.AreEqual(1, _lines.Count(l => l == "[Lotse] ev test step=7 name=stop result=skip"));
            Assert.AreEqual(1, _lines.Count(l => l == "[Lotse] ev test step=3 name=open result=ok"));
            Assert.AreEqual(11 + 11 + 1, _lines.Count, "a begin and an answer per step, one summary");
            var n = _lines.Count;
            test.Answer(TestResult.Ok);
            Assert.AreEqual(n, _lines.Count, "an answer after the summary logs nothing");
        }

        [Test]
        public void TheTestsOwnSourceOffersTheRequestAStepNeedsAndAnswersItLocally()
        {
            var source = new GuidedSource(Pack());
            Assert.IsFalse(source.Offer(false), "not before the agents are there");
            var s = new DemoSession(source);
            for (var i = 0; i < 30; i++) s.Step(100, default, default);
            Assert.AreEqual(4, s.Store.Agents.Count, "the jury pack's four agents");
            Assert.AreEqual(0, s.Store.Pending.Count);

            Assert.IsTrue(source.Offer(false));
            s.Step(100, default, default);
            var req = s.Store.Pending.Single();
            StringAssert.StartsWith(GuidedSource.IdPrefix, req.Id);
            Assert.IsFalse(req.NeedsTwoHand, "a harmless request: one hand");
            var agent = s.Store.Agents.First(a => a.Sid == req.Sid);
            Assert.AreEqual("blocked", agent.Status);
            Assert.IsFalse(string.IsNullOrEmpty(agent.Title), "the agent keeps what the pack said about it");
            Assert.IsTrue(source.Offer(false), "the same kind is already open: nothing new");
            s.Step(100, default, default);
            Assert.AreEqual(1, s.Store.Pending.Count);

            // a step that needs the dangerous one: the harmless one closes, the dangerous one opens with its facts
            Assert.IsTrue(source.Offer(true));
            s.Step(100, default, default);
            var danger = s.Store.Pending.Single();
            Assert.AreNotEqual(req.Id, danger.Id);
            Assert.IsTrue(danger.NeedsTwoHand);
            Assert.IsNotNull(s.Store.ImpactFor(danger.Id));
            Assert.AreEqual("working", s.Store.Agents.First(a => a.Sid == req.Sid).Status);

            // long after the pack's own first request would have come: the pack's timeline stands still, nothing else opens
            for (var i = 0; i < 1200; i++) s.Step(100, default, default);
            Assert.AreEqual(danger.Id, s.Store.Pending.Single().Id);
            Assert.IsFalse(s.Finished, "it never ends by itself");

            // halting closes its request like the pack does, and nothing is offered while halted
            Assert.IsTrue(s.RequestHalt());
            Assert.IsTrue(s.Halted);
            Assert.AreEqual(0, s.Store.Pending.Count);
            Assert.IsFalse(source.Offer(false));
            Assert.IsTrue(s.RequestRelease());
            Assert.IsFalse(s.Halted);
            Assert.IsTrue(source.Offer(false));
            s.Step(100, default, default);
            Assert.AreEqual(1, s.Store.Pending.Count);
            s.SetViewShown(true);
            s.Select(s.Store.Pending[0].Sid);
            Assert.IsNotNull(s.Card);
            Assert.IsTrue(s.Deny(s.Store.Pending[0].Id));
            Assert.AreEqual(0, s.Store.Pending.Count, "a decision on its request is answered at once");
        }
    }
}
