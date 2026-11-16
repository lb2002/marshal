#nullable enable
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U6's pure logic: the plan's counts and checklist window, the activity trail, the summary line, the column pages,
    /// the window's tabs and rail, and the gaze timers for collapse and expand (time and angles injected).
    /// </summary>
    public class OverviewLogicTests
    {
        /// <summary>A plan from a status string: d = done, i = in progress, p = pending; step i is "S{i+1}".</summary>
        static AgentPlan Plan(string statuses, int more = 0)
        {
            var p = new AgentPlan { More = more };
            for (var i = 0; i < statuses.Length; i++)
                p.Steps.Add(new PlanStep
                {
                    Text = "S" + (i + 1),
                    Status = statuses[i] == 'd' ? PlanStatus.Done : statuses[i] == 'i' ? PlanStatus.InProgress : PlanStatus.Pending,
                });
            return p;
        }

        static string[] Lines(AgentPlan p) => PlanLines.Window(p).Select(l => l.Kind == PlanLineKind.Step ? l.Text : "[" + l.Text + "]").ToArray();

        // ---- counts ----

        [Test]
        public void TheCountIsDoneStepsOfAllStepsIncludingTheCutOnes()
        {
            var p = Plan("ddipp", more: 3);
            Assert.AreEqual(2, PlanLines.Done(p));
            Assert.AreEqual(8, PlanLines.Total(p));
            Assert.AreEqual("2 / 8", PlanLines.Count(p));
            Assert.AreEqual("2/8", PlanLines.ShortCount(p));
        }

        [Test]
        public void TheCurrentStepIsTheOneInProgressElseTheNextPending()
        {
            Assert.AreEqual(2, PlanLines.Current(Plan("ddipp")));
            Assert.AreEqual(2, PlanLines.Current(Plan("ddppi").Let(x => x.Steps[4].Status = PlanStatus.Pending)));
            Assert.AreEqual(4, PlanLines.Current(Plan("pdddi")));
            Assert.AreEqual(-1, PlanLines.Current(Plan("ddd")));
            Assert.AreEqual("Plan 2 / 5: S3", PlanLines.Headline(Plan("ddipp")));
            Assert.AreEqual("Plan 3 / 3: all done", PlanLines.Headline(Plan("ddd")));
        }

        // ---- the checklist window ----

        [Test]
        public void SixStepsOrFewerShowWhole()
        {
            CollectionAssert.AreEqual(new[] { "S1", "S2", "S3" }, Lines(Plan("dip")));
            CollectionAssert.AreEqual(new[] { "S1", "S2", "S3", "S4", "S5", "S6" }, Lines(Plan("ddipps")));
        }

        [Test]
        public void MoreStepsFoldIntoOneLineAboveAndOneBelowAroundTheCurrentStep()
        {
            // 12 steps, the 6th in progress: one done step of context, the current one, then what fits
            CollectionAssert.AreEqual(new[] { "[4 done]", "S5", "S6", "S7", "S8", "[+4 more]" }, Lines(Plan("dddddipppppp")));
            Assert.AreEqual(6, PlanLines.Window(Plan("dddddipppppp")).Count);
            // at the start: no fold above
            CollectionAssert.AreEqual(new[] { "S1", "S2", "S3", "S4", "S5", "[+2 more]" }, Lines(Plan("ipppppp")));
            // all done: the end of the list
            CollectionAssert.AreEqual(new[] { "[7 done]", "S8", "S9", "S10", "S11", "S12" }, Lines(Plan("dddddddddddd")));
        }

        [Test]
        public void TheCutStepsCountIntoTheFoldBelow()
        {
            CollectionAssert.AreEqual(new[] { "S1", "S2", "S3", "S4", "S5", "[+3 more]" }, Lines(Plan("dipppp", more: 2)));
            CollectionAssert.AreEqual(new[] { "S1", "S2", "[+1 more]" }, Lines(Plan("di", more: 1)));
        }

        [Test]
        public void AFoldAboveOverStepsNotAllDoneSaysWeitere()
        {
            // the in-progress step comes after a pending one
            var lines = Lines(Plan("pppppppipp"));
            CollectionAssert.AreEqual(new[] { "[+5 more]", "S6", "S7", "S8", "S9", "S10" }, lines);
        }

        [Test]
        public void TheWindowNeverExceedsSixLinesAndAlwaysHoldsTheCurrentStep()
        {
            var statuses = new[] { 'd', 'i', 'p' };
            var rnd = new System.Random(7);
            for (var n = 1; n <= 12; n++)
                for (var trial = 0; trial < 40; trial++)
                {
                    var s = new string(Enumerable.Range(0, n).Select(_ => statuses[rnd.Next(3)]).ToArray());
                    var plan = Plan(s, more: rnd.Next(3));
                    var lines = PlanLines.Window(plan);
                    Assert.LessOrEqual(lines.Count, 6, s);
                    var c = PlanLines.Current(plan);
                    if (c >= 0) Assert.IsTrue(lines.Any(l => l.Index == c), s + ": the current step is shown");
                    var shown = lines.Count(l => l.Kind == PlanLineKind.Step);
                    var folded = lines.Where(l => l.Kind != PlanLineKind.Step).Sum(l => int.Parse(l.Text.Split(' ')[0]));
                    Assert.AreEqual(PlanLines.Total(plan), shown + folded, s + ": every step is shown or counted in a fold");
                }
        }

        // ---- the activity trail ----

        [Test]
        public void TheTrailKeepsTheLastFiveDistinctLinesNewestFirst()
        {
            var t = new ActivityTrail();
            foreach (var line in new[] { "Liest a.ts", "Liest a.ts", "Bearbeitet b.ts", "", "  ", "Testet", "Liest a.ts", "Führt aus: npm", "Liest c.ts", "Nutzt WebSearch" })
                t.Note("s1", line);
            CollectionAssert.AreEqual(new[] { "Nutzt WebSearch", "Liest c.ts", "Führt aus: npm", "Liest a.ts", "Testet" }, t.Lines("s1").ToArray());
            CollectionAssert.IsEmpty(t.Lines("s2"));
        }

        // ---- the summary line ----

        static AgentState A(string sid, string status, AgentPlan? plan = null) => new AgentState { Sid = sid, Status = status, Plan = plan };

        [Test]
        public void TheSummaryLineCountsAgentsWaitingAndSteps()
        {
            var agents = new List<AgentState>
            {
                A("a", "waiting", Plan("ddipp")), A("b", "working", Plan("dddddddd" + "ip", more: 2)), A("c", "done"), A("d", "working"),
            };
            Assert.AreEqual("4 agents, 1 needs you, 10 of 17 steps done",
                OverviewText.Summary(agents, x => AgentOrder.GroupOf(x, false) == AgentGroup.NeedsYou));
        }

        [Test]
        public void TheSummaryLineLeavesOutWhatIsNotThere()
        {
            var noPlans = new List<AgentState> { A("a", "working"), A("b", "done") };
            Assert.AreEqual("2 agents", OverviewText.Summary(noPlans, _ => false));
            Assert.AreEqual("2 agents, 2 need you", OverviewText.Summary(noPlans, _ => true));
            Assert.AreEqual("1 agent, 0 of 1 step done", OverviewText.Summary(new List<AgentState> { A("a", "working", Plan("i")) }, _ => false));
            Assert.AreEqual("0 agents", OverviewText.Summary(new List<AgentState>(), _ => false));
        }

        [Test]
        public void TheWaitingLineUsesTheNoticesShortText()
        {
            var req = new PermRequest { Id = "r", Sid = "a", Tool = "Bash" };
            var e = new Explanation { Text = "Claude ändert die Datei: src/a.ts" };
            Assert.AreEqual("Needs you: Claude ändert die Datei", OverviewText.Waits(Texts.Headline(e, req)));
        }

        // ---- column pages and the rail ----

        [Test]
        public void FourColumnsPerPageAndThePageIsKeptOrClamped()
        {
            var p = new ColumnPages();
            p.Update(3);
            Assert.AreEqual(1, p.Pages);
            Assert.AreEqual("1 / 1", p.Label);
            Assert.IsFalse(p.CanNext);
            p.Update(6);
            Assert.AreEqual(2, p.Pages);
            Assert.AreEqual(5, p.OnPage, "U13: five rows a page");
            p.Next();
            Assert.AreEqual(1, p.Page);
            Assert.AreEqual(5, p.First);
            Assert.AreEqual(1, p.OnPage);
            Assert.AreEqual("2 / 2", p.Label);
            p.Next();
            Assert.AreEqual(1, p.Page, "no page beyond the last");
            p.Update(9);
            Assert.AreEqual(1, p.Page, "more agents: the page stays");
            p.Update(4);
            Assert.AreEqual(0, p.Page, "the page went: back to the last one");
            p.Previous();
            Assert.AreEqual(0, p.Page);
        }

        [Test]
        public void TheRailShowsSevenOrbsThenACount()
        {
            Assert.AreEqual(4, RailSlots.Shown(4));
            Assert.AreEqual("", RailSlots.Extra(7));
            Assert.AreEqual(7, RailSlots.Shown(9));
            Assert.AreEqual("+2", RailSlots.Extra(9));
        }

        // ---- the window's mode ----

        [Test]
        public void TheWindowOpensOnUebersichtAndOnlyUnterhaltungShowsTheBlocksPlace()
        {
            var m = new WindowMode();
            Assert.AreEqual(WorkspaceTab.Overview, m.Tab);
            Assert.IsFalse(m.ConversationShown, "no approval block in Übersicht");
            Assert.IsTrue(m.Show(WorkspaceTab.Conversation));
            Assert.IsTrue(m.ConversationShown);
            Assert.IsTrue(m.Collapse());
            Assert.IsFalse(m.ConversationShown, "collapsed: no block");
            Assert.IsFalse(m.Show(WorkspaceTab.Overview), "no tab while collapsed");
            Assert.IsTrue(m.Expand());
            Assert.AreEqual(WorkspaceTab.Conversation, m.Tab, "back into the tab that was open");
            m.Show(WorkspaceTab.Overview);
            m.Collapse();
            Assert.IsTrue(m.Expand(WorkspaceTab.Conversation), "an orb's press");
            Assert.IsTrue(m.ConversationShown);
        }

        // ---- the gaze timers ----

        [Test]
        public void TheHeadAngleIsMeasuredFromTheCentreEyesForward()
        {
            var head = new Vec3(0f, 1.6f, 0f);
            Assert.AreEqual(0f, HeadAngle.Degrees(head, new Vec3(0f, 0f, 1f), new Vec3(0f, 1.6f, 2f)), 1e-3f);
            Assert.AreEqual(45f, HeadAngle.Degrees(head, new Vec3(0f, 0f, 1f), new Vec3(1f, 1.6f, 1f)), 1e-3f);
            Assert.AreEqual(90f, HeadAngle.Degrees(head, new Vec3(1f, 0f, 0f), new Vec3(0f, 1.6f, 1f)), 1e-3f);
        }

        [Test]
        public void TheWindowCollapsesAfterTwentySecondsWithTheHeadAway()
        {
            var t = new CollapseTimer();
            Assert.IsFalse(t.Step(0, true, 40f, false));
            Assert.IsFalse(t.Step(19900, true, 40f, false));
            Assert.IsTrue(t.Step(20000, true, 40f, false), "20 s away");
            Assert.IsFalse(t.Step(20100, true, 40f, false), "once");
            // 25° is still on the window
            var u = new CollapseTimer();
            for (var ms = 0; ms <= 30000; ms += 100) Assert.IsFalse(u.Step(ms, true, 25f, false));
        }

        [Test]
        public void LookingBackADraftAKeyboardAHoldOrATouchStartsTheTwentySecondsAgain()
        {
            var t = new CollapseTimer();
            t.Step(0, true, 40f, false);
            Assert.IsFalse(t.Step(15000, true, 10f, false), "looked back");
            Assert.IsFalse(t.Step(34000, true, 40f, false));
            Assert.IsTrue(t.Step(35000, true, 40f, false), "20 s after the last look");
            var k = new CollapseTimer();
            for (var ms = 0; ms <= 60000; ms += 100) Assert.IsFalse(k.Step(ms, true, 40f, true), "kept open the whole time");
            Assert.IsFalse(k.Step(60100, true, 40f, false));
            Assert.IsTrue(k.Step(80100, true, 40f, false));
        }

        [Test]
        public void TheSettingOffOrAWindowNotShownNeverCollapses()
        {
            var t = new CollapseTimer();
            for (var ms = 0; ms <= 60000; ms += 100) Assert.IsFalse(t.Step(ms, false, 90f, false));
            Assert.IsFalse(t.Step(60100, true, 90f, false), "the 20 s start when it is active");
            Assert.IsTrue(t.Step(80100, true, 90f, false));
        }

        // ---- plan caps on arrival ----

        [Test]
        public void APlanArrivesCappedAndPlain()
        {
            var p = new AgentPlan();
            for (var i = 0; i < 14; i++) p.Steps.Add(new PlanStep { Text = "Zeile\nzwei‮" + new string('y', 100) });
            var a = new AgentState { Sid = "a", Plan = p };
            Untrusted.Agent(a);
            Assert.AreEqual(12, a.Plan!.Steps.Count);
            Assert.AreEqual(2, a.Plan.More);
            Assert.AreEqual(80, a.Plan.Steps[0].Text.Length);
            StringAssert.StartsWith("Zeile¶zwei‹U+202E›", a.Plan.Steps[0].Text);
        }
    }

    static class LetExtension
    {
        public static T Let<T>(this T x, System.Action<T> f)
        {
            f(x);
            return x;
        }
    }
}
