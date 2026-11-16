using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U5a: the whole offline demo in the workspace with scripted hands only: poke "Demo ansehen", let each request's
    /// notice appear, select its agent (the pill by poke, or the row by hand ray), approve the harmless ones with a
    /// pinch-hold (a low pinch first: the block asks for the hands higher), deny both dangerous ones with "Ablehnen",
    /// see the incidents as lines and notices, reach the end card and poke "Nochmal". The wrist menu shows over an
    /// upturned palm, never while a block waits. Fixed 100 ms frames, so it runs fast.
    /// </summary>
    public class DemoSliceSmokeTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
        }

        [TearDown]
        public void TearDown() => _h.TearDown();

        [UnityTest]
        public IEnumerator TheWholeDemoRunsInTheWorkspaceWithHandsOnly()
        {
            yield return _h.Start();
            Assert.IsNotNull(PokeButton.All.FirstOrDefault(b => b.Label == Texts.BridgeButton && b.Interactable), "live mode is offered too");
            yield return _h.Poke(PokeButton.All.First(b => b.Label == Texts.StartButton));
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current);
            var s = _h.Root.Session;
            var incidents = new List<string>();
            s.IncidentRaised += i => incidents.Add(i.Kind);
            var notices = new List<string>();
            s.NoticeRaised += r => notices.Add(r.Id);
            var pillPokes = 0;
            var rayPicks = 0;
            var lowTried = false;
            var menuChecked = false;
            var incidentLine = false;
            var guard = 0;
            while (_h.Root.Current == AppRoot.Phase.Playing && guard++ < 20000)
            {
                _h.ReleaseHands();
                if (_h.Conversation.IncidentShown != null) incidentLine = true;
                if (s.Card == null)
                {
                    if (s.Store.Pending.Count == 0)
                    {
                        yield return null;
                        continue;
                    }
                    var wanted = s.Store.Pending[0];
                    Assert.IsFalse(_h.Block.IsShown && _h.Block.RequestId != null, "nothing opens by itself");
                    // select it: by the pill (a poke) or by the row (a ray click), in turn
                    var pill = _h.Root.Workspace.Pill;
                    if (pill.IsShown && pill.Sid == wanted.Sid && pillPokes <= rayPicks)
                    {
                        yield return _h.Poke(pill.Button);
                        pillPokes++;
                    }
                    else if (_h.Rows.RowOf(wanted.Sid) != null)
                    {
                        // U10: no pill where the window says it (the raised row): the row opens its agent
                        if (_h.Root.Desktop.OpenSid == null) Assert.IsFalse(pill.IsShown, "no pill above the overview");
                        yield return _h.RayPinch(_h.Rows.RowOf(wanted.Sid).transform.position);
                        rayPicks++;
                    }
                    else yield return null;
                    continue;
                }
                Assert.AreEqual(_h.Root.SelectedAgent, s.Card.Sid, "a block only for the selected agent");
                if (!menuChecked)
                {
                    menuChecked = true;
                    var palmAt = HeadPos + new Vector3(-0.1f, -0.2f, 0.4f);
                    _h.Hands.Left = new HandFrame { Sample = new HandSample(true, true, 0f, false), PalmValid = true, PalmCenter = palmAt, PalmNormal = Vector3.up };
                    yield return Frames(8);
                    Assert.AreEqual(MenuHand.None, _h.Root.Menu.Shown, "no wrist menu while a block waits");
                    _h.ReleaseHands();
                }
                if (!lowTried)
                {
                    lowTried = true;
                    _h.Hands.Right = Hand();
                    yield return null;
                    _h.Hands.Right = Hand(1f); // pinching in the lap
                    yield return Frames(20);
                    Assert.AreEqual(0, s.Decisions.Count, "2 s of pinching low: nothing");
                    Assert.AreEqual(Texts.CueRaiseHands, _h.Block.HintShown, "the block asks for the hands higher");
                    _h.ReleaseHands();
                    yield return null;
                }
                if (s.Card.Risk.IsDestructive)
                {
                    Assert.AreEqual(Texts.RiskDangerous, _h.Block.RiskShown);
                    yield return _h.Poke(_h.Block.DenyButton);
                    continue;
                }
                yield return _h.PinchHold(s.Card.NeedsTwoHand, s.Card.NeedsTwoHand ? 17 : 10);
            }
            _h.ReleaseHands();
            Assert.AreEqual(AppRoot.Phase.End, _h.Root.Current, "the demo reached its end");
            Assert.AreEqual(19, s.Decisions.Count);
            Assert.AreEqual(2, s.Decisions.Count(d => d.Behavior == "deny"));
            Assert.AreEqual(2, s.Stats.DangerousStopped);
            // (U13) after a decision the row closes, so the next request is found in the overview (its raised row); the
            // pill shows only while another agent's row is open, which this run never leaves open
            Assert.Greater(rayPicks, 0, "selected by ray");
            Assert.AreEqual(notices.Count, notices.Distinct().Count(), "one notice per request at most");
            CollectionAssert.AreEqual(new[] { "collision", "budget_near" }, incidents);
            Assert.IsTrue(incidentLine, "an incident showed as a line in a conversation");
            StringAssert.EndsWith("2 dangerous commands stopped.", _h.Root.EndSummary);
            Assert.IsFalse(_h.Root.Workspace.IsShown, "the end card stands in the window's place");

            yield return Frames(3);
            yield return _h.Poke(PokeButton.All.First(b => b.Label == Texts.Again));
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current);
            Assert.AreNotSame(s, _h.Root.Session);
            yield return Frames(2);
            Assert.IsNull(_h.Desk.OpenSid, "a new run opens with no row open (U6)");
            yield return Frames(3);
            Assert.IsTrue(_h.Rows.Shown.All(c => _h.Root.Session.Store.Agents.Any(a => a.Sid == c.Sid)), "a restart drops the old rows");
            Assert.AreEqual(_h.Root.Session.Store.Agents.Count, _h.Rows.Shown.Count, "a restart drops the old rows");
        }
    }
}
