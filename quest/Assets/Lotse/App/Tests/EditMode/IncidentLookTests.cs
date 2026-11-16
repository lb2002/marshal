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
    /// <summary>F8, U5a: incidents are shown (a muted line in the agents' conversations and the notice) and never decide.</summary>
    public class IncidentLookTests
    {
        static Incident I(string kind, string detail, params string[] sids) =>
            new Incident { Kind = kind, Detail = detail, Sids = sids.ToList() };

        [Test]
        public void ACollisionShowsItsDetailForBothAgents()
        {
            var show = IncidentLook.For(I("collision", "Agent 2 und Agent 4 bearbeiten dieselbe Datei: pricing.ts", "a2", "a4"));
            Assert.AreEqual("Agent 2 und Agent 4 bearbeiten dieselbe Datei: pricing.ts", show.Line);
            CollectionAssert.AreEqual(new[] { "a2", "a4" }, show.Sids);
        }

        [Test]
        public void AnAgentNamedTwiceIsShownOnce()
        {
            var show = IncidentLook.For(I("collision", "x", "a2", "a2", ""));
            CollectionAssert.AreEqual(new[] { "a2" }, show.Sids);
            Assert.AreEqual("x", show.Line, "the line still shows");
        }

        [TestCase("budget_near")]
        [TestCase("budget")]
        public void ABudgetIncidentIsALineForItsAgent(string kind)
        {
            var show = IncidentLook.For(I(kind, "Agent 4: 10.62 USD von 15.00 USD Budget (70 %)", "a4"));
            CollectionAssert.AreEqual(new[] { "a4" }, show.Sids);
            Assert.AreEqual("Agent 4: 10.62 USD von 15.00 USD Budget (70 %)", show.Line);
        }

        [Test]
        public void TheDetailIsPlainAndCapped()
        {
            var show = IncidentLook.For(I("collision", "<b>x</b>\n\u202E" + new string('d', 300), "a", "b"));
            StringAssert.StartsWith("<b>x</b>¶‹U+202E›", show.Line);
            Assert.LessOrEqual(show.Line.Length, Untrusted.ExplainMax);
        }

        [Test]
        public void TheDemoPacksIncidentsComeThroughTheSessionAndDecideNothing()
        {
            var s = new DemoSession(DemoPack.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "StreamingAssets", "demo", "jury.pack.json"))));
            var incidents = new List<Incident>();
            var cardsWhenRaised = new List<string?>();
            var decisionsWhenRaised = new List<int>();
            s.IncidentRaised += i =>
            {
                incidents.Add(i);
                cardsWhenRaised.Add(s.Card?.Id);
                decisionsWhenRaised.Add(s.Decisions.Count);
            };
            var open = new HandSample(true, true, 0f, false);
            var pinch = new HandSample(true, true, 1f, false);
            var guard = 0;
            string? seen = null;
            double since = 0;
            s.SetViewShown(true);
            while (!s.Finished && guard++ < 400000)
            {
                var right = open;
                var left = open;
                if (s.Card == null && s.Store.Pending.Count > 0) s.Select(s.Store.Pending[0].Sid);
                if (s.Card != null && s.Card.Id != seen)
                {
                    seen = s.Card.Id;
                    since = s.NowMs;
                }
                if (s.Card != null && s.NowMs - since > 500)
                {
                    right = pinch;
                    if (s.Card.NeedsTwoHand) left = pinch;
                }
                var before = s.Decisions.Count;
                s.Step(20, left, right);
                Assert.LessOrEqual(s.Decisions.Count - before, 1);
            }
            Assert.IsTrue(s.Finished);
            CollectionAssert.AreEqual(new[] { "collision", "budget_near" }, incidents.Select(i => i.Kind));
            Assert.AreEqual(19, s.Decisions.Count, "every decision came from a gesture, none from an incident");
        }
    }
}
