#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U5a, the workspace's pure parts: the dock's default pose, the agent list's order and its stability, the status
    /// line, the orb states (and that "Ruhig" stops all motion), the block's visibility rules, the countdown and the
    /// list's scroll.
    /// </summary>
    public class WorkspaceLogicTests
    {
        const float Eps = 1e-4f;

        static AgentState Agent(string sid, string status = "working", string lastAction = "", string? title = null, string? project = null,
            string source = "claude") =>
            new AgentState { Sid = sid, Name = sid, Status = status, LastAction = lastAction, Title = title, Project = project, Source = source };

        static PermRequest Req(string id, string sid, string risk = "safe", bool truncated = false, double created = 1000, double expires = 91000) =>
            new PermRequest { Id = id, Sid = sid, Tool = "Bash", Command = "ls", Risk = new RiskResult { Level = risk }, Truncated = truncated, CreatedAt = created, ExpiresAt = expires };

        static void Near(Vec3 expected, Vec3 actual, string what)
        {
            Assert.AreEqual(expected.X, actual.X, Eps, what + " x");
            Assert.AreEqual(expected.Y, actual.Y, Eps, what + " y");
            Assert.AreEqual(expected.Z, actual.Z, Eps, what + " z");
        }

        // ---- WorkspaceDock ----

        [Test]
        public void TheDefaultWindowIsNinetyByFiftyTwoCentimetresAndItsWidthFollowsItsHeight()
        {
            var d = WorkspaceDock.InFrontOfHead(new Vec3(0f, 1.2f, 0f), new Vec3(0f, 0f, 1f));
            Assert.AreEqual(0.455f * 2.0f, d.Height, Eps, "U10: 0.455 of the distance, at 2.0 m");
            Assert.AreEqual(0.91f * 1.72f, d.Width, 0.003f);
            var tall = new WorkspaceDock(d.Center, d.Forward, d.Up, 0.5f, DockSide.Left);
            Assert.AreEqual(0.5f * 1.72f, tall.Width, Eps, "width is always height x 1.72");
        }

        [Test]
        public void ByDefaultItStandsSeventyFiveCentimetresAheadAndFiveBelowTheEyesFacingTheHead()
        {
            var head = new Vec3(0.2f, 1.5f, -0.3f);
            var d = WorkspaceDock.InFrontOfHead(head, new Vec3(0f, -0.6f, 0.8f)); // looking down: only the yaw counts
            Near(new Vec3(0.2f, 1.45f, -0.3f + 2.0f), d.Center, "centre (U10: 2.0 m)");
            Near(new Vec3(0f, 0f, 1f), d.Forward, "from the head through the window: its front faces the head");
            Near(new Vec3(0f, 1f, 0f), d.Up, "upright");
            Assert.AreEqual(2.0f, (float)System.Math.Sqrt(System.Math.Pow(d.Center.X - head.X, 2) + System.Math.Pow(d.Center.Z - head.Z, 2)), Eps);
            var turned = WorkspaceDock.InFrontOfHead(head, new Vec3(1f, 0.2f, 0f));
            Near(new Vec3(1f, 0f, 0f), turned.Forward, "turned to the right: still facing the head");
            Assert.AreEqual(1f, WorkspaceDock.InFrontOfHead(head, new Vec3(0f, -1f, 0f)).Forward.Length, Eps, "straight down: a unit facing");
        }

        [Test]
        public void TheDocksRightIsTheUsersRight()
        {
            var d = new WorkspaceDock(default, new Vec3(0f, 0f, 1f), new Vec3(0f, 1f, 0f), 0.36f, DockSide.Right);
            Near(new Vec3(1f, 0f, 0f), d.Right, "right");
            Near(new Vec3(0f, 0.18f, 0f), d.TopCenter, "top edge");
        }

        // ---- AgentOrder ----

        [Test]
        public void AgentsThatNeedYouComeFirstOldestRequestFirstThenWorkingThenFinished()
        {
            var order = new AgentOrder();
            var agents = new List<AgentState>
            {
                Agent("done1", "done"), Agent("work1"), Agent("ask-late"), Agent("failed1", "failed"), Agent("ask-early"), Agent("waits", "waiting"),
            };
            var pending = new List<PermRequest> { Req("p1", "ask-early"), Req("p2", "ask-late") };
            Assert.IsTrue(order.Update(agents, pending));
            CollectionAssert.AreEqual(new[] { "ask-early", "ask-late", "waits", "work1", "done1", "failed1" }, order.Sids.ToArray());
            Assert.AreEqual(AgentGroup.NeedsYou, order.GroupOf("waits"), "waiting for an answer needs the user too");
        }

        [Test]
        public void RowsMoveOnlyWhenAnAgentChangesGroup()
        {
            var order = new AgentOrder();
            var agents = new List<AgentState> { Agent("a"), Agent("b"), Agent("c") };
            var none = new List<PermRequest>();
            order.Update(agents, none);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order.Sids.ToArray());
            var version = order.Version;

            // events inside the same group: nothing moves, even when the order of the store changes
            agents = new List<AgentState> { Agent("c", "editing", "Bearbeitet x.ts"), Agent("a", "reading"), Agent("b", "testing") };
            Assert.IsFalse(order.Update(agents, none), "no group changed");
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order.Sids.ToArray());
            Assert.AreEqual(version, order.Version);

            // b asks: it goes to the top; c finishes: to the bottom; a keeps its place in between
            agents = new List<AgentState> { Agent("a"), Agent("b", "blocked"), Agent("c", "done") };
            var pending = new List<PermRequest> { Req("p1", "b") };
            Assert.IsTrue(order.Update(agents, pending));
            CollectionAssert.AreEqual(new[] { "b", "a", "c" }, order.Sids.ToArray());

            // a second request of b, and c's status line changing: still nothing moves
            pending.Add(Req("p2", "b"));
            Assert.IsFalse(order.Update(agents, pending));

            // b answered: back to working, at the end of the working group
            agents = new List<AgentState> { Agent("a"), Agent("b"), Agent("c", "done") };
            Assert.IsTrue(order.Update(agents, new List<PermRequest>()));
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order.Sids.ToArray());
        }

        [Test]
        public void WhileARowIsOpenTheAgentsKeepTheirPlacesAndTheChangesComeAfterwards()
        {
            var order = new AgentOrder();
            var a = Agent("a", "blocked");
            var b = Agent("b");
            var c = Agent("c");
            order.Update(new[] { a, b, c }, new[] { Req("r1", "a") });
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order.Sids);
            var version = order.Version;
            // a's request is approved while its row is open: it works again, and c asks
            a.Status = "working";
            Assert.IsFalse(order.Update(new[] { a, b, c }, new[] { Req("r2", "c") }, keepPlaces: true));
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order.Sids, "nobody moves while a row is open");
            Assert.AreEqual(version, order.Version);
            // an agent that comes is still added, one that goes is dropped; the others stay where they are
            var d = Agent("d");
            Assert.IsTrue(order.Update(new[] { a, c, d }, new[] { Req("r2", "c") }, keepPlaces: true));
            CollectionAssert.AreEqual(new[] { "a", "c", "d" }, order.Sids);
            // the row has closed: now the order is the usual one
            Assert.IsTrue(order.Update(new[] { a, c, d }, new[] { Req("r2", "c") }));
            CollectionAssert.AreEqual(new[] { "c", "d", "a" }, order.Sids, "the asking agent first; the one that left its group at the end of its new one");
        }

        [Test]
        public void AnAgentThatLeavesIsDroppedAndOneThatComesIsAddedAtTheEndOfItsGroup()
        {
            var order = new AgentOrder();
            var none = new List<PermRequest>();
            order.Update(new List<AgentState> { Agent("a"), Agent("b", "done") }, none);
            order.Update(new List<AgentState> { Agent("a"), Agent("b", "done"), Agent("c") }, none);
            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, order.Sids.ToArray());
            Assert.IsTrue(order.Update(new List<AgentState> { Agent("c"), Agent("b", "done") }, none));
            CollectionAssert.AreEqual(new[] { "c", "b" }, order.Sids.ToArray());
        }

        // ---- StatusLine ----

        [Test]
        public void TheStatusLineSaysWhatTheAgentDoesInAFewWords()
        {
            Assert.AreEqual(("needs you", StatusTone.NeedsYou), Line(Agent("a", "working", "Bearbeitet x.ts"), needsYou: true));
            Assert.AreEqual(("needs you", StatusTone.NeedsYou), Line(Agent("a", "waiting", "Wartet auf Eingabe")));
            Assert.AreEqual(("editing Header.tsx", StatusTone.Normal), Line(Agent("a", "editing", "Bearbeitet src/components/Header.tsx")));
            Assert.AreEqual(("reading Cart.tsx", StatusTone.Normal), Line(Agent("a", "reading", "Liest components/Cart.tsx")));
            Assert.AreEqual(("editing files", StatusTone.Normal), Line(Agent("a", "editing", "Bearbeitet (MultiEdit)")));
            Assert.AreEqual(("running a command", StatusTone.Normal), Line(Agent("a", "working", "Bash: npm")));
            Assert.AreEqual(("running a command", StatusTone.Normal), Line(Agent("a", "testing", "Führt Tests aus")));
            Assert.AreEqual(("running a command", StatusTone.Normal), Line(Agent("a", "working", "Führt aus: ls")));
            Assert.AreEqual(("thinking", StatusTone.Normal), Line(Agent("a", "working", "Arbeitet")));
            Assert.AreEqual(("thinking", StatusTone.Normal), Line(Agent("a", "editing", "Bearbeitet x.ts"), feed: "thinking"));
            Assert.AreEqual(("using WebSearch", StatusTone.Normal), Line(Agent("a", "working", "Nutzt WebSearch")));
            Assert.AreEqual(("error", StatusTone.Normal), Line(Agent("a", "failed", "Fehlgeschlagen")));
            Assert.AreEqual(("done 3 min ago", StatusTone.Normal), Line(Agent("a", "done", "Fertig: Badge"), doneMs: 3.5 * 60000));
            Assert.AreEqual("done", StatusLine.DoneAgo(20000));
            Assert.AreEqual("done 2 h ago", StatusLine.DoneAgo(125 * 60000));
        }

        [Test]
        public void AFileNameInTheStatusLineIsCappedAndItsHiddenCharactersShow()
        {
            var name = new string('x', 80) + ".ts";
            var line = StatusLine.For(Agent("a", "editing", "Bearbeitet src/" + name), false, null, 0).Text;
            Assert.LessOrEqual(line.Length, "editing ".Length + 40);
            var hidden = StatusLine.For(Agent("a", "editing", "Bearbeitet a/b" + (char)0x202E + "c.ts"), false, null, 0).Text;
            Assert.IsFalse(hidden.Contains((char)0x202E), "a bidi override is shown, never applied");
        }

        static (string, StatusTone) Line(AgentState a, bool needsYou = false, string? feed = null, double doneMs = 0)
        {
            var s = StatusLine.For(a, needsYou, feed, doneMs);
            return (s.Text, s.Tone);
        }

        // ---- OrbState ----

        [Test]
        public void TheOrbStateFollowsTheStatusAndTheLastFeedKind()
        {
            Assert.AreEqual(OrbMode.NeedsYou, OrbState.For(Agent("a", "working"), true, null));
            Assert.AreEqual(OrbMode.NeedsYou, OrbState.For(Agent("a", "blocked"), false, null));
            Assert.AreEqual(OrbMode.NeedsYou, OrbState.For(Agent("a", "waiting"), false, null));
            Assert.AreEqual(OrbMode.Done, OrbState.For(Agent("a", "done"), false, null));
            Assert.AreEqual(OrbMode.Error, OrbState.For(Agent("a", "failed"), false, null));
            Assert.AreEqual(OrbMode.Command, OrbState.For(Agent("a", "testing", "Führt Tests aus"), false, null));
            Assert.AreEqual(OrbMode.Command, OrbState.For(Agent("a", "working", "Bash: git"), false, "thinking"), "a running command wins");
            Assert.AreEqual(OrbMode.Thinking, OrbState.For(Agent("a", "working", "Arbeitet"), false, null));
            Assert.AreEqual(OrbMode.Thinking, OrbState.For(Agent("a", "editing", "Bearbeitet x"), false, "thinking"));
            Assert.AreEqual(OrbMode.Working, OrbState.For(Agent("a", "editing", "Bearbeitet x"), false, "tool"));
            Assert.AreEqual(OrbMode.Working, OrbState.For(Agent("a", "reading", "Liest x"), false, null));
        }

        [Test]
        public void EachAgentKeepsItsPaletteColourAndNeedsYouIsRedAndAnErrorGrey()
        {
            Assert.AreEqual(6, OrbState.Palette.Length);
            Assert.AreEqual(Look.Hex(0x7FD3FF), OrbState.ColorsFor(OrbMode.Working, 0).A, "blue first");
            Assert.AreEqual(Look.Hex(0x5B6CFF), OrbState.ColorsFor(OrbMode.Thinking, 0).B);
            Assert.AreEqual(Look.Hex(0xC6A7FF), OrbState.ColorsFor(OrbMode.Done, 1).A, "violet second");
            Assert.AreEqual(Look.Hex(0xB8C4CF), OrbState.ColorsFor(OrbMode.Working, 5).A, "slate sixth");
            Assert.AreEqual(OrbState.ColorsFor(OrbMode.Working, 0).A, OrbState.ColorsFor(OrbMode.Working, 6).A, "then around again");
            Assert.AreEqual(Look.Hex(0xFF9A6B), OrbState.ColorsFor(OrbMode.NeedsYou, 3).A);
            Assert.AreEqual(Look.Hex(0xFF4E6A), OrbState.ColorsFor(OrbMode.NeedsYou, 3).B);
            Assert.AreEqual(OrbState.Grey.A, OrbState.ColorsFor(OrbMode.Error, 2).A);
        }

        [Test]
        public void WorkingTurnsOnceInSevenSecondsAndBreathesBetweenOneAndOnePointZeroFive()
        {
            Assert.AreEqual(0f, OrbState.At(OrbMode.Working, 0, false).TurnDegrees, 0.01f);
            Assert.AreEqual(180f, OrbState.At(OrbMode.Working, 3.5, false).TurnDegrees, 0.01f);
            Assert.AreEqual(0f, OrbState.At(OrbMode.Working, 7, false).TurnDegrees, 0.01f);
            Assert.AreEqual(1f, OrbState.At(OrbMode.Working, 0, false).Scale, Eps);
            Assert.AreEqual(1.05f, OrbState.At(OrbMode.Working, 1.7, false).Scale, Eps);
            for (var t = 0.0; t < 10; t += 0.13)
            {
                var s = OrbState.At(OrbMode.Working, t, false).Scale;
                Assert.That(s, Is.InRange(1f - Eps, 1.05f + Eps));
            }
            // U9: thinking is as calm as working (once in 7 s, the same soft breath); no fast spin
            Assert.AreEqual(180f, OrbState.At(OrbMode.Thinking, 3.5, false).TurnDegrees, 0.01f, "thinking: once in 7 s");
            Assert.AreEqual(1.05f, OrbState.At(OrbMode.Thinking, 1.7, false).Scale, Eps);
            // U9: no dashed ring; a running command looks like working
            var cmd = OrbState.At(OrbMode.Command, 2, false);
            Assert.IsFalse(cmd.DashedRing);
            Assert.AreEqual(OrbState.At(OrbMode.Working, 2, false).TurnDegrees, cmd.TurnDegrees, 0.01f);
            // U9: "needs you" is one thin solid ring, standing: the same at any time
            var ask = OrbState.At(OrbMode.NeedsYou, 0.8, false);
            Assert.IsTrue(ask.Pulse);
            Assert.AreEqual(OrbState.PulseStillScale, ask.PulseScale, Eps);
            Assert.AreEqual(1f, ask.PulseAlpha, Eps);
            Assert.AreEqual(ask.PulseScale, OrbState.At(OrbMode.NeedsYou, 0.1, false).PulseScale, Eps, "the ring stands");
            Assert.IsFalse(OrbState.Moves(OrbMode.NeedsYou, false));
            Assert.AreEqual(0f, OrbState.Glow(OrbState.At(OrbMode.Working, 0, false)), Eps, "the breath is brightness: none at rest");
            Assert.AreEqual(1f, OrbState.Glow(OrbState.At(OrbMode.Working, 1.7, false)), 1e-3f, "and full at the top of a breath");
            Assert.IsFalse(OrbState.At(OrbMode.Done, 1, false).Pulse);
            Assert.IsFalse(OrbState.Moves(OrbMode.Done, false));
            Assert.IsFalse(OrbState.Moves(OrbMode.Error, false));
        }

        [Test]
        public void RuhigStopsAllMotionAndTheStatesStayApartByColourRingAndBadge()
        {
            foreach (OrbMode mode in System.Enum.GetValues(typeof(OrbMode)))
            {
                Assert.IsFalse(OrbState.Moves(mode, true), mode + " moves in Ruhig");
                var a = OrbState.At(mode, 1.234, true);
                var b = OrbState.At(mode, 5.678, true);
                Assert.AreEqual(a.TurnDegrees, b.TurnDegrees, mode + ": turned");
                Assert.AreEqual(a.Scale, b.Scale, mode + ": breathed");
                Assert.AreEqual(a.PulseScale, b.PulseScale, mode + ": pulsed");
                Assert.AreEqual(a.PulseAlpha, b.PulseAlpha, mode + ": faded");
                Assert.AreEqual(a.RingDegrees, b.RingDegrees, mode + ": the ring turned");
                Assert.AreEqual(1f, a.Scale, mode.ToString());
            }
            Assert.IsTrue(OrbState.At(OrbMode.NeedsYou, 0, true).Pulse, "needs you keeps a standing ring");
            Assert.Greater(OrbState.At(OrbMode.NeedsYou, 0, true).PulseAlpha, 0.3f);
            Assert.IsFalse(OrbState.At(OrbMode.Command, 0, true).DashedRing, "U9: no dashed ring any more");
            Assert.AreNotEqual(OrbState.ColorsFor(OrbMode.NeedsYou, 0).A, OrbState.ColorsFor(OrbMode.Working, 0).A);
        }

        [Test]
        public void TheConicGradientRunsFromTheFirstStopToTheSecondAndBackWithoutASeam()
        {
            var c = OrbState.Palette[0];
            Assert.AreEqual(c.A, OrbState.Conic(c, 0f, 0f));
            var opposite = OrbState.Conic(c, 180f, 0f);
            Assert.AreEqual(c.B.R, opposite.R, Eps);
            Assert.AreEqual(c.B.B, opposite.B, Eps);
            Assert.AreEqual(OrbState.Conic(c, 359.9f, 0f).R, OrbState.Conic(c, 0.1f, 0f).R, 1e-3f, "no seam at 12 o'clock");
            Assert.AreEqual(OrbState.Conic(c, 90f, 90f), OrbState.Conic(c, 0f, 0f), "turning moves the gradient");
        }

        // ---- ApprovalFocus and the countdown ----

        [Test]
        public void TheBlockShowsOnlyTheSelectedAgentsOldestRequestWhileTheViewIsShown()
        {
            var f = new ApprovalFocus();
            var pending = new List<PermRequest> { Req("p1", "a"), Req("p2", "b"), Req("p3", "a"), Req("p4", "a") };
            Assert.IsNull(f.Pick(pending, true, out _), "nothing selected: no block");
            f.Select("b");
            Assert.AreEqual("p2", f.Pick(pending, true, out var more)?.Id);
            Assert.AreEqual(0, more);
            f.Select("a");
            Assert.AreEqual("p1", f.Pick(pending, true, out more)?.Id, "the oldest of the selected agent");
            Assert.AreEqual(2, more, "+2 weitere");
            Assert.IsNull(f.Pick(pending, false, out _), "the view not shown: no block");
            f.Select("c");
            Assert.IsNull(f.Pick(pending, true, out _), "an agent without a request: no block, never another agent's");
        }

        [Test]
        public void SpaeterHidesTheBlockUntilTheAgentIsSelectedAgain()
        {
            var f = new ApprovalFocus();
            var pending = new List<PermRequest> { Req("p1", "a") };
            f.Select("a");
            f.Later();
            Assert.IsTrue(f.PutOff);
            Assert.IsNull(f.Pick(pending, true, out _));
            pending.Add(Req("p2", "a"));
            Assert.IsNull(f.Pick(pending, true, out _), "a new request of the same agent does not bring it back");
            f.Select("a");
            Assert.AreEqual("p1", f.Pick(pending, true, out _)?.Id, "selecting it again shows the block again");
        }

        [Test]
        public void TheCountdownRunsFromTheRequestsOwnSpanSinceWeFirstSawIt()
        {
            var r = Req("p1", "a", created: 5_000_000, expires: 5_090_000); // the bridge's clock, 90 s span
            Assert.AreEqual(1.0, Countdown.Fraction(r, 1000, 1000)!.Value, 1e-9);
            Assert.AreEqual(0.5, Countdown.Fraction(r, 1000, 46000)!.Value, 1e-9);
            Assert.AreEqual(45000, Countdown.LeftMs(r, 1000, 46000)!.Value, 1e-9);
            Assert.AreEqual(0.0, Countdown.Fraction(r, 1000, 200000)!.Value, 1e-9, "never below zero");
            Assert.AreEqual(1.0, Countdown.Fraction(r, 1000, 0)!.Value, 1e-9, "never above one");
            Assert.IsNull(Countdown.Fraction(Req("p2", "a", created: 0, expires: 0), 0, 10), "no span: no countdown");
            Assert.IsNull(Countdown.Fraction(Req("p3", "a", created: 10, expires: 5), 0, 10));
        }

        // ---- ListScroll ----

        [Test]
        public void SevenRowsShowAndMoreScrollWithTheHand()
        {
            var s = new ListScroll(7);
            s.Update(5);
            s.Scroll(3);
            Assert.AreEqual(0, s.Top, "five rows fit: nothing to scroll");
            s.Update(10);
            s.Drag(0.05f, 0.04f);
            Assert.AreEqual(1, s.Top, "a hand moving up by one row shows the next row");
            s.Drag(0.5f, 0.04f);
            Assert.AreEqual(3, s.Top, "at most to the last row");
            s.Drag(-0.2f, 0.04f);
            Assert.AreEqual(0, s.Top);
            s.Reveal(9);
            Assert.AreEqual(3, s.Top, "a row that must show is scrolled into view");
            s.Update(8);
            Assert.AreEqual(1, s.Top, "fewer rows: Top stays within reach");
        }

        [Test]
        public void TheOrbSlotHoldsTheWidestPulseWithEightMillimetresOnEachSide()
        {
            const float d = 0.02f;
            Assert.AreEqual(d * OrbState.PulseMaxScale + 0.016f, OrbState.Slot(d), 1e-6f);
            Assert.GreaterOrEqual(OrbState.PulseMaxScale, OrbState.PulseStillScale, "the still ring fits too");
        }

        [Test]
        public void AFeedEntryNeverRepeatsItsLabel()
        {
            Assert.AreEqual("src/app.ts", Texts.WithoutLabel("liest src/app.ts", "liest"));
            Assert.AreEqual("fertig", Texts.WithoutLabel("Sagt: fertig", "sagt"));
            Assert.AreEqual("liestmehr", Texts.WithoutLabel("liestmehr", "liest"), "only a whole word");
            Assert.AreEqual("liest", Texts.WithoutLabel("liest", "liest"), "never empties the text");
            Assert.AreEqual("x", Texts.WithoutLabel("x", null));
        }
    }
}
