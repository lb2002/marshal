using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U13, the "taxi lines" design's pure rules: the header's sentence, the stop bar's lights as a function of the
    /// hold's progress, the dashes fitted to their stretch, where a step's text stands, the one ease-out curve.
    /// </summary>
    public class TaxiLinesTests
    {
        // ---- the headline ----

        [Test]
        public void TheHeadlineSaysTheOneThingThatMattersInItsOrder()
        {
            // halted beats everything
            Assert.AreEqual("Everything is stopped.", Headline.Overview(true, 4, 2, "Codex", 1));
            Assert.AreEqual("Everything is stopped.", Headline.Overview(true, 0, 0, null, 0));
            // one agent holding: its vendor
            Assert.AreEqual("Codex is holding for you.", Headline.Overview(false, 4, 1, "Codex", 1));
            Assert.AreEqual("One agent is holding for you.", Headline.Overview(false, 4, 1, null, 0), "an unknown vendor is not made up");
            // several holding: how many, as a word
            Assert.AreEqual("Two agents are holding for you.", Headline.Overview(false, 4, 2, "Codex", 0));
            Assert.AreEqual("Six agents are holding for you.", Headline.Overview(false, 6, 6, null, 0));
            // holding beats landed and moving
            Assert.AreEqual("Codex is holding for you.", Headline.Overview(false, 4, 1, "Codex", 3));
            // all done
            Assert.AreEqual("All four have landed.", Headline.Overview(false, 4, 0, null, 4));
            // else
            Assert.AreEqual("All four are moving.", Headline.Overview(false, 4, 0, null, 0));
            Assert.AreEqual("All four are moving.", Headline.Overview(false, 4, 0, null, 3), "one still under way");
            Assert.AreEqual("All six are moving.", Headline.Overview(false, 6, 0, null, 0));
            Assert.AreEqual("All 7 are moving.", Headline.Overview(false, 7, 0, null, 0), "digits above six");
            Assert.AreEqual("9 agents are holding for you.", Headline.Overview(false, 12, 9, null, 0));
            // none, and one
            Assert.AreEqual("No agents yet.", Headline.Overview(false, 0, 0, null, 0));
            Assert.AreEqual("Start one in your terminal.", Headline.NoAgentsHint);
            Assert.AreEqual("Your agent is moving.", Headline.Overview(false, 1, 0, null, 0));
            Assert.AreEqual("Your agent has landed.", Headline.Overview(false, 1, 0, null, 1));
        }

        [Test]
        public void AnOpenWorkingAgentsHeadlineNamesItsStep()
        {
            Assert.AreEqual("Codex is on step 3 of 5.", Headline.Working("Codex", 3, 5));
            Assert.AreEqual("Codex is working.", Headline.Working("Codex", 0, 0), "no plan");
            Assert.AreEqual("This agent is working.", Headline.Working(null, 0, 0));
            Assert.AreEqual("Muse is on step 4 of 4.", Headline.Working("Muse", 9, 4));
            Assert.AreEqual("Cleared. Codex is moving again", Headline.Cleared("Codex"));
        }

        // ---- the stop bar: a display of the progress, a pure function of progress and kind ----

        static string Bar(float p, bool two) => new string(Enumerable.Range(0, StopBar.Lights(two)).Select(i => StopBar.Lit(i, p, two) ? '#' : '.').ToArray());

        [Test]
        public void TheTwoHandBarHasTwentyOneLightsGoingDarkFromBothEndsTowardTheMiddle()
        {
            Assert.AreEqual(21, StopBar.Lights(true));
            Assert.AreEqual("#####################", Bar(0f, true));
            Assert.AreEqual(".###################.", Bar(1f / 11f, true));
            Assert.AreEqual(".....###########.....", Bar(5f / 11f, true));
            Assert.AreEqual("..........#..........", Bar(10f / 11f, true), "the middle one is the last");
            Assert.AreEqual(".....................", Bar(1f, true));
            // always symmetric, never brighter as the hold goes on
            var before = 21;
            for (var p = 0f; p <= 1.0001f; p += 0.01f)
            {
                var bar = Bar(p, true);
                Assert.AreEqual(bar, new string(bar.Reverse().ToArray()), "symmetric at " + p);
                var lit = StopBar.LitCount(p, true);
                Assert.LessOrEqual(lit, before, "never comes back on while the progress grows");
                Assert.AreEqual(bar.Count(c => c == '#'), lit);
                before = lit;
            }
            Assert.AreEqual(0, before);
        }

        [Test]
        public void TheOneHandBarHasNineLightsGoingDarkFromLeftToRight()
        {
            Assert.AreEqual(9, StopBar.Lights(false));
            Assert.AreEqual("#########", Bar(0f, false));
            Assert.AreEqual(".########", Bar(1f / 9f, false));
            Assert.AreEqual("....#####", Bar(4f / 9f, false));
            Assert.AreEqual("........#", Bar(8f / 9f, false));
            Assert.AreEqual(".........", Bar(1f, false));
        }

        [Test]
        public void TheBarShowsNothingButTheProgress()
        {
            // the same progress, the same lights, whatever came before; out of range is clamped; no light outside the bar
            foreach (var two in new[] { true, false })
            {
                Assert.AreEqual(Bar(0f, two), Bar(-3f, two));
                Assert.AreEqual(Bar(0f, two), Bar(float.NaN, two));
                Assert.AreEqual(Bar(1f, two), Bar(7f, two));
                Assert.AreEqual(Bar(0.5f, two), Bar(0.5f, two));
                Assert.IsFalse(StopBar.Lit(-1, 0f, two));
                Assert.IsFalse(StopBar.Lit(StopBar.Lights(two), 0f, two));
                Assert.AreEqual(StopBar.Lights(two), StopBar.LitCount(0f, two));
                Assert.AreEqual(0, StopBar.LitCount(1f, two));
            }
            // a released hold falls back to zero: all lit again
            Assert.AreEqual(21, StopBar.LitCount(0f, true));
        }

        // ---- the line ----

        [Test]
        public void MarksStandEvenlyBetweenTheLinesEnds()
        {
            Assert.AreEqual(0.015f, TaxiLine.At(0, 5), 1e-6f);
            Assert.AreEqual(0.985f, TaxiLine.At(4, 5), 1e-6f);
            Assert.AreEqual(0.5f, TaxiLine.At(2, 5), 1e-6f);
            Assert.AreEqual(0.015f, TaxiLine.At(0, 1), 1e-6f, "one step: at the start");
            Assert.AreEqual(0.015f, TaxiLine.At(-2, 4), 1e-6f);
            Assert.AreEqual(0.985f, TaxiLine.At(9, 4), 1e-6f);
        }

        [Test]
        public void DashesFitTheirStretchSoEveryStretchStartsWithADashAtItsMark()
        {
            var dashes = new List<(float from, float to)>();
            // the design's rows: a 560 unit line with four or three stretches
            foreach (var stretch in new[] { 140f, 186.7f, 97f * 5.6f / 3f, 25f, 12f, 333f })
            {
                TaxiLine.Dashes(100f, 100f + stretch, Taxi.DashPeriod, Taxi.DashShare, dashes);
                Assert.GreaterOrEqual(dashes.Count, 1);
                Assert.AreEqual(100f, dashes[0].from, 1e-4f, "a dash right at the mark");
                var each = stretch / dashes.Count;
                Assert.That(each, Is.InRange(Taxi.DashPeriod * 0.66f, Taxi.DashPeriod * 1.5f).Or.EqualTo(stretch), "periods near 25 units");
                for (var i = 0; i < dashes.Count; i++)
                {
                    Assert.AreEqual(100f + i * each, dashes[i].from, 1e-3f, "equal periods");
                    Assert.AreEqual(each * Taxi.DashShare, dashes[i].to - dashes[i].from, 1e-3f, "56 % dash");
                }
                Assert.Less(dashes[dashes.Count - 1].to, 100f + stretch, "a gap before the next mark, never a dash cut off by it");
            }
            // 140 units: round(140 / 25) = 6 dashes
            TaxiLine.Dashes(0f, 140f, 25f, 0.56f, dashes);
            Assert.AreEqual(6, dashes.Count);
            // nothing for an empty stretch; the list is cleared
            TaxiLine.Dashes(5f, 5f, 25f, 0.56f, dashes);
            Assert.AreEqual(0, dashes.Count);
            TaxiLine.Dashes(5f, 1f, 25f, 0.56f, dashes);
            Assert.AreEqual(0, dashes.Count);
        }

        [Test]
        public void TheStepsTextIsNeverUnderTheStopBar()
        {
            Assert.AreEqual(NowAnchor.Left, TaxiLine.Anchor(0.015f, false));
            Assert.AreEqual(NowAnchor.Left, TaxiLine.Anchor(0.5f, false));
            Assert.AreEqual(NowAnchor.Right, TaxiLine.Anchor(0.74f, false), "past 60 %: right-anchored");
            Assert.AreEqual(NowAnchor.Right, TaxiLine.Anchor(0.985f, false));
            // a bar stands ahead of the dot
            Assert.AreEqual(NowAnchor.AfterBar, TaxiLine.Anchor(0.015f, true), "near the start: behind the bar");
            Assert.AreEqual(NowAnchor.Right, TaxiLine.Anchor(0.5f, true), "past 30 %: before the dot");
            Assert.AreEqual(NowAnchor.Right, TaxiLine.Anchor(0.9f, true));
        }

        [Test]
        public void TheDesignsTextColoursPassTheContrastThresholdsOnBothSurfaces()
        {
            foreach (var surface in new[] { Look.Asphalt, Look.Raised })
            {
                Assert.GreaterOrEqual(Look.Contrast(Look.Ink, surface), 7.0, "ink");
                Assert.GreaterOrEqual(Look.Contrast(Look.TaxiMuted, surface), 4.5, "muted (lightened to #AFB2B6 for the 7:1 rule of WindowPlacementTests)");
                Assert.GreaterOrEqual(Look.Contrast(Look.Stop, surface), 4.5, "Holding, Stopped, Gone");
                Assert.GreaterOrEqual(Look.Contrast(Look.StopText, surface), 4.5, "the deny label");
                Assert.GreaterOrEqual(Look.Contrast(Look.Taxi, surface), 3.0, "the painted line");
                Assert.GreaterOrEqual(Look.Contrast(Look.Go, surface), 3.0);
            }
            Assert.GreaterOrEqual(Look.Contrast(Look.Ink, Look.Inset), 7.0, "the command");
            Assert.GreaterOrEqual(Look.Contrast(Look.TaxiMuted, Look.Inset), 4.5, "the reply field's placeholder");
            Assert.GreaterOrEqual(Look.Contrast(Look.Asphalt, Look.Ink), 12.0, "a primary button's label");
        }

        [Test]
        public void TheOneEaseOutCurve()
        {
            Assert.AreEqual(0f, Taxi.Ease(0f));
            Assert.AreEqual(1f, Taxi.Ease(1f));
            Assert.AreEqual(0f, Taxi.Ease(-1f));
            Assert.AreEqual(1f, Taxi.Ease(2f));
            var last = 0f;
            for (var t = 0.05f; t < 1f; t += 0.05f)
            {
                var y = Taxi.Ease(t);
                Assert.GreaterOrEqual(y, last, "never back");
                Assert.Greater(y, t, "ease-out: ahead of linear");
                last = y;
            }
            Assert.Greater(Taxi.Ease(0.3f), 0.75f, "most of the way early");
        }

        [Test]
        public void AnOpenAgentThatLandedOrWaitsHasItsOwnSentence()
        {
            Assert.AreEqual("Codex has landed.", Headline.Landed("Codex"));
            Assert.AreEqual("This agent has landed.", Headline.Landed(null));
            Assert.AreEqual("Muse is waiting for you.", Headline.Waiting("Muse"));
            Assert.AreEqual("Cleared. Codex is moving again", Headline.Cleared("Codex"));
        }

        [Test]
        public void TheBarsLitCountIsAPureFunctionOfProgressAndKind()
        {
            foreach (var two in new[] { true, false })
            {
                var n = StopBar.Lights(two);
                Assert.AreEqual(n, StopBar.LitCount(0f, two), "nothing held: every light is lit");
                Assert.AreEqual(0, StopBar.LitCount(1f, two), "held to the end: the bar is dark");
                var last = n;
                for (var p = 0f; p <= 1f; p += 0.01f)
                {
                    var lit = StopBar.LitCount(p, two);
                    Assert.AreEqual(lit, StopBar.LitCount(p, two), "the same progress, the same lights");
                    Assert.LessOrEqual(lit, last, "never more lights as the hold goes on");
                    Assert.AreEqual(n - (two ? System.Math.Min(n, 2 * StopBar.Dark(p, two) - (StopBar.Dark(p, two) > n / 2 ? 1 : 0)) : StopBar.Dark(p, two)), lit);
                    last = lit;
                }
            }
            Assert.AreEqual(21, StopBar.Lights(true));
            Assert.AreEqual(9, StopBar.Lights(false));
        }

        [Test]
        public void AFactThatIsNotKnownHasNoCellAndGoneIsOnlyForASureLoss()
        {
            var gone = ImpactFacts.For(new Lotse.Core.Impact { Files = 214, Bytes = 38e6, OutsideProject = true, Recoverable = "no" }, 5000);
            CollectionAssert.AreEqual(new[] { "214 files", "38 MB in size", "Outside this project", "Gone no way back" }, gone.Cells.Select(c => c.ToString()).ToArray());
            Assert.IsTrue(gone.Cells.Last().Red, "only \"Gone\" is red");
            Assert.IsFalse(gone.Cells.Take(3).Any(c => c.Red));
            var unknown = ImpactFacts.For(new Lotse.Core.Impact { Recoverable = "unknown" }, 5000);
            CollectionAssert.AreEqual(new[] { "Unknown effects" }, unknown.Cells.Select(c => c.ToString()).ToArray(), "no counts, no place: no cells for them");
            Assert.IsFalse(unknown.Cells[0].Red);
            var maybe = ImpactFacts.For(new Lotse.Core.Impact { Files = 1, Recoverable = "yes" }, 5000);
            CollectionAssert.AreEqual(new[] { "1 file", "Unknown effects" }, maybe.Cells.Select(c => c.ToString()).ToArray(), "nothing here ever says it is safe");
            var capped = ImpactFacts.For(new Lotse.Core.Impact { Files = 20000, FilesCapped = true, Recoverable = "no" }, 5000);
            Assert.AreEqual("20,000 files at least", capped.Cells[0].ToString());
            var none = ImpactFacts.For(null, 100);
            CollectionAssert.AreEqual(new[] { "Checking effects" }, none.Cells.Select(c => c.ToString()).ToArray());
            CollectionAssert.AreEqual(new[] { "Unknown effects" }, ImpactFacts.For(null, 5000).Cells.Select(c => c.ToString()).ToArray());
        }

        [Test]
        public void ThePageOfTheOpenRowsAgentIsRevealed()
        {
            var p = new ColumnPages();
            p.Update(12);
            p.Reveal(7);
            Assert.AreEqual(1, p.Page);
            Assert.AreEqual(5, p.First);
            p.Reveal(11);
            Assert.AreEqual(2, p.Page);
            p.Reveal(40);
            Assert.AreEqual(2, p.Page, "no such item: the page stays");
            p.Reveal(0);
            Assert.AreEqual(0, p.Page);
        }

        [Test]
        public void ARowsOpeningAndClosingAreLoggedAsTwoWords()
        {
            Assert.AreEqual("[Lotse] ev row open id=2 kind=request", Ev.Line("row open", ("id", 2), ("kind", "request")));
            Assert.AreEqual("[Lotse] ev row close", Ev.Line("row close"));
        }
    }
}
