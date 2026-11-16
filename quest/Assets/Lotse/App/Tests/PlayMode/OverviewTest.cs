using System.Collections;
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
    /// U6 through AppRoot (U13: rows instead of columns and tabs): the overview with each agent's line (the window opens
    /// on it, nothing there can be approved), opening and closing a row (it cancels a hold), the plan on the open row,
    /// and the rail: fold away (by the button or after 20 s
    /// with the head away, kept open by a draft or a keyboard, the draft kept), expand (gaze, a click, an orb), and the
    /// safety rules: a pinch held while the window opens never approves, no gaze expand while a hand pinches, no expand on a
    /// new request.
    /// </summary>
    public class OverviewTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness { OpenConversation = false };
            _h.SetUp();
        }

        [TearDown]
        public void TearDown() => _h.TearDown();

        static string Plan(string statuses, int more = 0) =>
            ",\"plan\":{\"steps\":[" + string.Join(",", statuses.Select((c, i) =>
                "{\"text\":\"Schritt " + (i + 1) + "\",\"status\":\"" + (c == 'd' ? "done" : c == 'i' ? "in_progress" : "pending") + "\"}")) +
            "],\"more\":" + more + ",\"updatedAt\":1}";

        static string WithPlan(string sid, string name, string status, string title, string statuses, string lastAction = "") =>
            Agent(sid, name, status, title, lastAction).TrimEnd('}') + Plan(statuses) + "}";

        /// <summary>s1 waits (a plan), s2 works (a plan), s3 is done without a plan (its trail), s4 works without a plan.</summary>
        static readonly string Four = string.Join(",", WithPlan("s1", "A", "working", "Checkout-Summen", "ddipp"), WithPlan("s2", "B", "working",
                "Warenkorb-Badge", "dddi"), Agent("s3", "C", "done", "Alte Seiten", "Fertig"), Agent("s4", "D", "editing", "Suche", "Bearbeitet src/Search.tsx"));

        WorkspaceView View => _h.Root.Workspace;
        TaxiRowsView Rows => View.Rows;
        WorkspaceController Ctl => _h.Desk;

        IEnumerator PokeLeft(PokeButton b)
        {
            var t = b.transform;
            _h.Hands.Left = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            _h.Hands.Left = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.01f)));
            yield return Frames(2);
            _h.Hands.Left = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, 0.003f)));
            yield return null;
            _h.Hands.Left = Hand();
            yield return null;
        }

        /// <summary>A point on the rail below its orbs and counts (only the rail's own area there).</summary>
        Vector3 RailFreeStrip() => View.transform.TransformPoint(View.Rail.transform.localPosition + new Vector3(RailView.Width * 0.5f, -(View.Rail.Height - 0.002f), 0f));

        IEnumerator PokeLeftAt(Vector3 at)
        {
            var away = View.transform.forward;
            _h.Hands.Left = Hand(tip: at - away * 0.08f);
            yield return null;
            _h.Hands.Left = Hand(tip: at - away * 0.01f);
            yield return Frames(2);
            _h.Hands.Left = Hand(tip: at + away * 0.001f);
            yield return null;
            _h.Hands.Left = Hand();
            yield return null;
        }

        void LookAt(Vector3 at) => _h.Camera.transform.rotation = Quaternion.LookRotation(at - HeadPos, Vector3.up);

        IEnumerator Collapse()
        {
            yield return _h.Poke(View.Header.CollapseButton);
            yield return Frames(3);
            Assert.IsTrue(View.Folded, "folded into the rail");
        }

        // ---- the overview ----

        [UnityTest]
        public IEnumerator TheWindowOpensOnUebersichtWithThePlansOfTheDemo()
        {
            yield return _h.StartDemo();
            Assert.IsNull(Ctl.OpenSid, "the window opens with no row open");
            var s = _h.Root.Session;
            var guard = 0;
            while ((s.Store.Agents.Count < 4 || s.Store.Agents.Any(a => a.Plan == null)) && guard++ < 600) yield return null;
            yield return Frames(3); // agent data redraws the rows at most every 250 ms (U6 review 1)
            Assert.AreEqual(4, Rows.Shown.Count);
            foreach (var r in Rows.Shown)
            {
                var a = s.Store.Agents.First(x => x.Sid == r.Sid);
                Assert.AreEqual(System.Math.Min(16, PlanLines.Total(a.Plan)), Rows.MarksOf(r.Sid).marks, "a mark per step of " + r.Sid);
                Assert.IsNotEmpty(Rows.NowShown(r.Sid), "its step stands above the dot");
                Assert.AreEqual(TaxiRowMode.Full, Rows.ModeOf(r.Sid));
            }
            StringAssert.Contains("four", View.Header.Said);
            // the demo goes on; its first request comes: no block in the overview, no row opens
            guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 600) yield return null;
            yield return Frames(3);
            Assert.IsNull(Ctl.OpenSid, "a row never opens by itself");
            Assert.IsNull(s.Card, "no block in the overview");
            var waiting = s.Store.Pending[0].Sid;
            Assert.IsTrue(Rows.RaisedOf(waiting), "its row is raised");
            Assert.AreEqual(Taxi.RowLights, Rows.LightsOf(waiting), "a stop bar stands across its line");
            Assert.AreEqual(OverviewText.Holding, Rows.StatusShown(waiting));
            StringAssert.EndsWith("is holding for you.", View.Header.Said);
        }

        [UnityTest]
        public IEnumerator AColumnShowsItsChecklistTrailAndBottomLineInStableOrder()
        {
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(3);
            CollectionAssert.AreEqual(new[] { "s1", "s2", "s4", "s3" }, Rows.Shown.Select(c => c.Sid).ToArray(), "waiting, working, done");
            Assert.AreEqual(OverviewText.Holding, Rows.StatusShown("s1"));
            Assert.AreEqual((5, 3), Rows.MarksOf("s1"), "five steps, painted up to the one it is held at");
            Assert.AreEqual("Schritt 3", Rows.NowShown("s1"), "the step in progress above the dot");
            Assert.AreEqual(TaxiState.Holding, Rows.Shown[0].State);
            Assert.IsTrue(Rows.RaisedOf("s1"));
            Assert.AreEqual(Taxi.RowLights, Rows.LightsOf("s1"), "the step in progress waits: a stop bar");
            Assert.AreEqual("4 of 4", Rows.StatusShown("s2"));
            Assert.AreEqual((4, 4), Rows.MarksOf("s2"));
            Assert.IsFalse(Rows.RaisedOf("s2"));
            Assert.AreEqual(0, Rows.LightsOf("s2"));
            Assert.IsTrue(Rows.PingShown("s2"), "a working agent's dot sends out its ring");
            Assert.IsFalse(Rows.PingShown("s1"), "a held one does not");
            Assert.AreEqual(OverviewText.Landed, Rows.StatusShown("s3"));
            Assert.AreEqual((0, 0), Rows.MarksOf("s3"), "no plan: a line without marks");
            Assert.AreEqual(OverviewText.LandedNoPlan, Rows.NowShown("s3"));
            Assert.AreEqual(OverviewText.NoPlan, Rows.StatusShown("s4"), "no plan: \"no plan\" as the status");
            Assert.AreEqual("Bearbeitet src/Search.tsx", Rows.NowShown("s4"), "its latest activity above the dot");
            Assert.AreEqual(TaxiLine.Inset, Rows.DotOf("s4"), 1e-4f, "the dot at the start");
            // the rows are equally high
            Assert.AreEqual(Rows.HeightOf("s1"), Rows.HeightOf("s3"), 1e-5f);
            Assert.AreEqual("Claude Code is holding for you.", View.Header.Said);
        }

        [UnityTest]
        public IEnumerator RuhigStopsTheRing()
        {
            _h.Prefs.Set(LookSettings.CalmKey, "on");
            yield return _h.GoLive(agents: Four);
            Assert.IsTrue(Theme.Calm);
            yield return Frames(5);
            Assert.IsFalse(Rows.PingShown("s2"), "no ring is sent out");
        }

        [UnityTest]
        public IEnumerator NothingInUebersichtApprovesWithAnyGesture()
        {
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "rm -rf build"));
            _h.Socket.Receive(PermOpen("r2", "s2", "safe"));
            yield return Frames(3);
            Assert.IsNull(_h.Root.Session.Card, "no block");
            Assert.IsFalse(_h.Root.Session.ViewShown);
            // one hand, two hands, the palm, held long at the window's centre
            var at = HeadPos + (View.transform.position - HeadPos).normalized * 0.35f;
            LookAt(View.transform.position);
            _h.Hands.Right = Hand(pinchAt: at);
            _h.Hands.Left = Hand(pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(20);
            _h.Hands.Left = Hand(1f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(30);
            _h.ReleaseHands();
            yield return Frames(2);
            _h.Hands.Right = Hand(palm: true, pinchAt: at);
            yield return Frames(10);
            // a ray held on a row (no click), the raised one with its stop bar
            var col = Rows.RowOf("s1").transform.position;
            _h.Hands.Right = Hand(rayAt: col);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: col);
            yield return Frames(25);
            _h.ReleaseHands();
            yield return Frames(3);
            Assert.AreEqual(0, _h.Decisions(), "nothing approved or denied in Übersicht");
            Assert.IsNull(Ctl.OpenSid, "a long pinch is no click");
            Assert.IsNull(_h.Root.Session.Card);
        }

        [UnityTest]
        public IEnumerator AColumnClickSelectsTheAgentAndSwitchesToUnterhaltung()
        {
            yield return _h.GoLive(agents: Four);
            yield return _h.Poke(Rows.RowOf("s2"));
            Assert.AreEqual("s2", Ctl.OpenSid);
            Assert.AreEqual("s2", _h.Root.SelectedAgent);
            yield return _h.CloseRow();
            yield return _h.RayPinch(Rows.RowOf("s4").transform.position + View.transform.right * 0.03f);
            Assert.AreEqual("s4", Ctl.OpenSid, "a ray click too");
            Assert.AreEqual("s4", _h.Root.SelectedAgent);
        }

        [UnityTest]
        public IEnumerator SwitchingTabsCancelsAHoldAndComingBackNeedsAFreshPinch()
        {
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PokeRow("s1");
            Assert.AreEqual("r1", _h.Block.RequestId);
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold runs");
            // the left hand pokes "All agents" while the right keeps pinching
            yield return PokeLeft(View.Header.AllButton);
            Assert.IsNull(Ctl.OpenSid);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the hold is cancelled");
            Assert.IsNull(_h.Root.Session.Card);
            yield return Frames(10);
            yield return PokeLeft(Rows.RowOf("s1"));
            yield return Frames(30); // the same pinch, still held
            Assert.AreEqual("r1", _h.Root.Session.Card?.Id, "the block is back");
            Assert.AreEqual(0, _h.Decisions(), "the pinch held across the switch never approves");
            _h.ReleaseHands();
            yield return Frames(2);
            yield return _h.PinchHold(false, 10);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(1, _h.Decisions(), "a fresh pinch does");
        }

        [UnityTest]
        public IEnumerator SixAgentsPageWithTwoButtons()
        {
            var six = string.Join(",", Enumerable.Range(1, 6).Select(i => WithPlan("x" + i, "X" + i, "working", "Aufgabe " + i, "di")));
            yield return _h.GoLive(agents: six);
            Assert.AreEqual(TaxiRowsView.PerPage, Rows.Shown.Count);
            Assert.AreEqual("1/2", View.Header.PageShown);
            yield return _h.Poke(View.Header.NextButton);
            yield return Frames(1);
            Assert.AreEqual("2/2", View.Header.PageShown);
            CollectionAssert.AreEqual(new[] { "x6" }, Rows.Shown.Select(c => c.Sid).ToArray());
            yield return _h.RayPinch(View.Header.PreviousButton.transform.position);
            yield return Frames(1);
            Assert.AreEqual("1/2", View.Header.PageShown);
        }

        [UnityTest]
        public IEnumerator TheNewestIncidentIsOneLineWithAnAmberDot()
        {
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive("{\"type\":\"incident\",\"incident\":{\"kind\":\"collision\",\"sids\":[\"s2\",\"s1\"],\"detail\":\"Erste\",\"t\":1}}");
            _h.Socket.Receive("{\"type\":\"incident\",\"incident\":{\"kind\":\"collision\",\"sids\":[\"s2\",\"s1\"],\"detail\":\"Zwei agents bearbeiten gerade dieselbe Datei: src/lib/pricing.ts\",\"t\":2}}");
            yield return Frames(2);
            Assert.AreEqual("Zwei agents bearbeiten gerade dieselbe Datei: src/lib/pricing.ts", Rows.IncidentShown, "only the newest");
            Assert.AreEqual(0, _h.Decisions());
        }

        // ---- the plan on the open row ----

        [UnityTest]
        public IEnumerator ThePlanLineInUnterhaltungOpensAndCloses()
        {
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: Four);
            yield return _h.PokeRow("s1");
            var conv = _h.Conversation;
            Assert.AreEqual("Schritt 1\nSchritt 2\nSchritt 3\nSchritt 4\nSchritt 5", conv.PlanShown, "the plan stands at the right, always");
            CollectionAssert.AreEqual(new[] { "done", "done", "now", "ahead", "ahead" }, conv.PlanMarks, "it works at step 3: a white dot");
            Assert.AreEqual("Claude Code is on step 3 of 5.", View.Header.Said);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(4);
            CollectionAssert.AreEqual(new[] { "done", "done", "held", "ahead", "ahead" }, conv.PlanMarks, "held at step 3 by the request: a red light");
            yield return _h.PokeRow("s3");
            Assert.AreEqual(Texts.NoPlanYet, conv.PlanShown, "no plan");
        }

        // ---- the rail ----

        [UnityTest]
        public IEnumerator EinklappenFoldsIntoTheRailAndAClickOpensTheTabThatWasOpen()
        {
            yield return _h.GoLive(agents: Four);
            yield return _h.OpenRow("s1");
            yield return Collapse();
            Assert.AreEqual(4, View.Rail.OrbsShown);
            Assert.AreEqual(RailSign.Dot, View.Rail.SignOf("s1"));
            Assert.AreEqual(RailSign.Landed, View.Rail.SignOf("s3"));
            Assert.AreEqual(RailView.Width, View.Background.transform.localScale.x, 1e-4f, "68 units wide");
            Assert.IsFalse(View.Header.gameObject.activeInHierarchy, "nothing of the window but the rail");
            // the rail's free strip under the orbs and counts (U5b: the rail is as high as its content)
            yield return _h.RayPinch(RailFreeStrip());
            yield return Frames(3);
            Assert.IsTrue(View.Open);
            Assert.AreEqual("s1", Ctl.OpenSid, "the row that was open");
            yield return Collapse();
            yield return _h.Poke(View.Rail.AreaOf("s4"));
            yield return Frames(3);
            Assert.IsTrue(View.Open);
            Assert.AreEqual("s4", _h.Root.SelectedAgent, "a sign opens its agent");
            Assert.AreEqual("s4", Ctl.OpenSid);
        }

        [UnityTest]
        public IEnumerator TheRailNeverOpensByGazeOnlyByAPokeOrARayClick()
        {
            // U10 (device test): the head is on the rail right after "Collapse"; a dwell reopened it at once
            yield return _h.GoLive(agents: Four);
            yield return Collapse();
            LookAt(View.RailCentre);
            yield return Frames(50); // 5 s with the head on the rail
            Assert.IsTrue(View.Folded, "still collapsed after 5 s of the head on the rail");
            // a pinch that is still held from before is ignored until it is released
            var at = RailFreeStrip();
            _h.Hands.Right = Hand(1f, pinchAt: Lap);
            yield return Frames(3);
            _h.Hands.Right = Hand(1f, rayAt: at);
            yield return Frames(10);
            _h.Hands.Right = Hand(rayAt: at);
            yield return Frames(2);
            Assert.IsTrue(View.Folded, "a pinch held from before never opens it");
            _h.Hands.Right = Hand();
            yield return Frames(2);
            yield return _h.RayPinch(at);
            yield return Frames(3);
            Assert.IsTrue(View.Open, "a ray click on the rail opens it");
        }

        [UnityTest]
        public IEnumerator TheWindowFoldsAfterTwentySecondsWithTheHeadAwayUnlessADraftOrAKeyboardKeepsItOpen()
        {
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: Four);
            yield return _h.PokeRow("s3");
            var away = HeadPos + new Vector3(2f, 0f, 0.2f);
            // a keyboard open (the system keyboard, typing)
            yield return _h.Poke(_h.Conversation.Input.EditButton);
            Assert.IsTrue(_h.Root.Reply.SystemKeyboardUp);
            _h.Keyboard.Text = "Bitte";
            LookAt(away);
            yield return Frames(250);
            Assert.IsTrue(View.Open, "a keyboard keeps it open");
            // the keyboard closes; the draft stays and keeps it open
            _h.Keyboard.Status = KeyboardStatus.Done;
            yield return Frames(250);
            Assert.AreEqual("Bitte", _h.Root.Reply.Logic.DraftOf("s3"));
            Assert.IsTrue(View.Open, "a draft keeps it open");
            // "Einklappen" still works, and the draft is kept
            LookAt(View.transform.position);
            yield return Collapse();
            Assert.AreEqual("Bitte", _h.Root.Reply.Logic.DraftOf("s3"), "collapsing keeps the draft");
            yield return _h.Poke(View.Rail.AreaOf("s3"));
            yield return Frames(3);
            Assert.AreEqual("Bitte", _h.Root.Reply.Logic.DraftOf("s3"));
            // without the draft: 20 s away fold it
            _h.Root.Reply.Logic.SetText("");
            LookAt(away);
            yield return Frames(190);
            Assert.IsTrue(View.Open, "19 s");
            yield return Frames(15);
            Assert.IsTrue(View.Collapsed, "20 s with the head away");
        }

        [UnityTest]
        public IEnumerator ABlockWaitingForItsDecisionKeepsTheWindowOpenWithTheHeadAway()
        {
            // U6 review 1, Minor 1 ruling: the window does not fold by itself while an approval block is shown
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PokeRow("s1");
            Assert.AreEqual("r1", _h.Block.RequestId);
            LookAt(HeadPos + new Vector3(2f, 0f, 0.2f));
            // 25 s away; the bridge says something every 10 s, so the link stays up
            for (var i = 0; i < 5; i++)
            {
                yield return Frames(50);
                _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s2", "B", "working", "Warenkorb-Badge") + "}");
            }
            Assert.IsTrue(View.Open, "25 s with the head away: the block keeps it open");
            Assert.AreEqual("r1", _h.Block.RequestId);
            // the request goes: then 20 s away fold it
            _h.Socket.Receive(PermClosed("r1", "allow"));
            for (var i = 0; i < 5; i++)
            {
                yield return Frames(43);
                _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s2", "B", "working", "Warenkorb-Badge") + "}");
            }
            Assert.IsTrue(View.Collapsed, "without a block it folds after 20 s");
        }

        [UnityTest]
        public IEnumerator ARayOrAPokeOnTheWindowAndTheSettingKeepItOpen()
        {
            yield return _h.GoLive(agents: Four);
            var away = HeadPos + new Vector3(2f, 0f, 0.2f);
            LookAt(away);
            _h.Hands.Right = Hand(rayAt: View.transform.position);
            yield return Frames(250);
            Assert.IsTrue(View.Open, "a ray resting on the window keeps it open");
            _h.Hands.Right = Hand();
            _h.Root.Desktop.AutoCollapse = false;
            yield return Frames(250);
            Assert.IsTrue(View.Open, "Automatisch einklappen: Aus");
            _h.Root.Desktop.AutoCollapse = true;
            yield return Frames(210);
            Assert.IsTrue(View.Collapsed);
        }

        [UnityTest]
        public IEnumerator APinchHeldWhileTheWindowOpensNeverApproves()
        {
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PokeRow("s1");
            Assert.AreEqual("r1", _h.Block.RequestId);
            var at = _h.GatePoint();
            yield return Collapse();
            Assert.IsNull(_h.Root.Session.Card, "collapsed: no block");
            // a pinch is held at the gate point, then the left hand opens the rail
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(3);
            yield return PokeLeft(View.Rail.AreaOf("s1"));
            yield return Frames(3);
            Assert.IsTrue(View.Open);
            Assert.AreEqual("r1", _h.Block.RequestId, "the block is back");
            _h.Camera.transform.rotation = Quaternion.LookRotation(_h.Block.Center - HeadPos, Vector3.up);
            yield return Frames(30);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress);
            Assert.AreEqual(0, _h.Decisions(), "a pinch held while the window opened never approves");
            _h.ReleaseHands();
            yield return Frames(2);
            yield return _h.PinchHold(false, 10);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(1, _h.Decisions(), "a fresh one does");
        }

        [UnityTest]
        public IEnumerator CollapsingCancelsAHoldAndANewRequestNeverOpensTheRail()
        {
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: Four);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PokeRow("s1");
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f);
            yield return PokeLeft(View.Header.CollapseButton);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "collapsing cancels the hold");
            _h.ReleaseHands();
            yield return Frames(5);
            Assert.IsTrue(View.Folded);
            // a new request: the orb pulses, nothing opens, no pill
            LookAt(HeadPos + new Vector3(2f, 0f, 0.2f));
            _h.Socket.Receive(PermOpen("r2", "s2"));
            yield return Frames(60);
            Assert.IsTrue(View.Folded, "the rail never opens by itself");
            Assert.IsFalse(View.Pill.IsShown, "no pill while collapsed");
            Assert.IsTrue(_h.Lotse.Any(m => m.Contains("notice for request r2")), "the notice (and its chime) still came");
            Assert.AreEqual(RailSign.Lights, View.Rail.SignOf("s2"), "its sign: three red lights");
            Assert.AreEqual(0, _h.Decisions());
        }

        [UnityTest]
        public IEnumerator TheSettingsOfferAutomatischEinklappen()
        {
            yield return _h.GoLive(agents: Four);
            yield return _h.OpenSettings();
            Assert.Contains(OverviewText.AutoCollapse, _h.Root.Settings.Lines);
            yield return _h.Poke(_h.Root.Settings.AutoCollapseOffButton);
            Assert.IsFalse(_h.Root.Desktop.AutoCollapse);
            Assert.AreEqual("off", _h.Prefs.Get(LookSettings.AutoCollapseKey), "kept for the next run");
        }

        // ---- draw calls ----

        [UnityTest]
        public IEnumerator TheOverviewWithFourFullColumnsAndTheRailStayWithinFortyEightDrawCalls()
        {
            // five rows (a page), one of them held, an incident line
            var agents = string.Join(",", WithPlan("s1", "A", "working", "Checkout-Summen korrigieren", "dddddipppppp"),
                WithPlan("s2", "B", "working", "Warenkorb-Badge einbauen", "ddipppp"), WithPlan("s3", "C", "working", "Alte Seiten migrieren", "ddddddddi"),
                WithPlan("s4", "D", "testing", "Produktsuche mit Preisfilter", "dddipp", "Führt Tests aus"),
                WithPlan("s5", "E", "working", "Die Rechnungen als PDF", "dip"));
            yield return _h.GoLive(agents: agents);
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "rm -rf build"));
            _h.Socket.Receive("{\"type\":\"incident\",\"incident\":{\"kind\":\"collision\",\"sids\":[\"s2\",\"s1\"],\"detail\":\"Zwei agents bearbeiten gerade dieselbe Datei: src/lib/pricing.ts\",\"t\":2}}");
            yield return Frames(3);
            Assert.AreEqual(5, Rows.Shown.Count, "a full page");
            var window = View.Window;
            var renderers = RenderProbe.Renderers(window);
            var passes = RenderProbe.Passes(window);
            Debug.Log($"[Lotse] render probe, overview with 5 rows: {renderers} renderers, {passes} passes: {RenderProbe.Describe(window)}");
            Assert.LessOrEqual(renderers, 48);
            Assert.LessOrEqual(passes, 48);

            // the two-hand request open, the other four rows thin
            yield return _h.OpenRow("s1");
            Assert.AreEqual("r1", _h.Block.RequestId);
            Assert.AreEqual(StopBar.TwoHandLights, _h.Block.LightsShown);
            Assert.AreEqual(TaxiRowMode.Thin, Rows.ModeOf("s2"));
            renderers = RenderProbe.Renderers(window);
            passes = RenderProbe.Passes(window);
            Debug.Log($"[Lotse] render probe, an open two-hand request with 4 thin rows: {renderers} renderers, {passes} passes: {RenderProbe.Describe(window)}");
            Assert.LessOrEqual(renderers, 48);
            Assert.LessOrEqual(passes, 48);
            yield return _h.CloseRow();

            // the rail with 7 orbs and "+2" (9 agents), two of them asking
            var nine = string.Join(",", Enumerable.Range(1, 9).Select(i => WithPlan("x" + i, "X" + i, i == 3 ? "testing" : "working", "Aufgabe " + i, "dip")));
            _h.Socket.Receive("{\"type\":\"snapshot\",\"agents\":[" + nine + "],\"pending\":[],\"explains\":{}}");
            _h.Socket.Receive(PermOpen("q1", "x1"));
            _h.Socket.Receive(PermOpen("q2", "x2"));
            yield return Frames(2);
            yield return Collapse();
            yield return Frames(2);
            Assert.AreEqual(7, View.Rail.OrbsShown);
            Assert.AreEqual("+2", View.Rail.CountsShown);
            renderers = RenderProbe.Renderers(window);
            passes = RenderProbe.Passes(window);
            Debug.Log($"[Lotse] render probe, the rail with 7 orbs: {renderers} renderers, {passes} passes: {RenderProbe.Describe(window)}");
            Assert.LessOrEqual(renderers, 48);
            Assert.LessOrEqual(passes, 48);
        }
    }
}
