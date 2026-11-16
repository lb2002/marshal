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
    /// U5a's workspace through AppRoot: one window where the dock says (in front of the head without a desk), nothing
    /// else in the room; the list's order and the selection made once; the empty state; the orbs (and that "Ruhig" and a
    /// running hold stop all their motion); incidents as one muted line and the notice; the draw-call budget.
    /// </summary>
    public class WorkspaceTest
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
        public IEnumerator OneWindowStandsWhereTheDockSaysAndNothingElseSpawns()
        {
            yield return _h.StartDemo();
            var dock = _h.Root.Dock;
            Assert.AreEqual(WorkspaceDock.DefaultHeight, dock.Height, 1e-4f, "U10: 0.455 of the distance, at 2.0 m");
            Assert.AreEqual(WorkspaceDock.DefaultHeight * WorkspaceDock.Aspect, dock.Width, 2e-3f);
            var w = _h.Root.Workspace.transform;
            // 0.75 m in front of the head, 0.05 m below the eyes, its readable front facing the head within 5 degrees
            Assert.AreEqual(WorkspaceDock.InFrontMeters, new Vector2(w.position.x - HeadPos.x, w.position.z - HeadPos.z).magnitude, 1e-3f);
            Assert.AreEqual(HeadPos.y - 0.05f, w.position.y, 1e-3f);
            Assert.Less(Vector3.Angle(Vector3.ProjectOnPlane(-w.forward, Vector3.up), Vector3.ProjectOnPlane(HeadPos - w.position, Vector3.up)), 5f,
                "the readable front (content looks along -forward) faces the head");
            Assert.AreEqual(0f, w.forward.y, 1e-4f, "upright");
            var bg = _h.Root.Workspace.Background.transform;
            Assert.AreEqual(dock.Width, bg.lossyScale.x, 1e-3f);
            Assert.AreEqual(dock.Height, bg.lossyScale.y, 1e-3f);

            // the head moves: the window stays where it is
            var at = w.position;
            _h.Camera.transform.position = HeadPos + new Vector3(0.2f, 0f, 0f);
            yield return Frames(3);
            Assert.Less(Vector3.Distance(at, w.position), 1e-4f, "it does not follow the head");

            // the demo's agents fill the list; no other panel, card or figure stands in the room
            var guard = 0;
            while (_h.Root.Session.Store.Agents.Count < 4 && guard++ < 200) yield return null;
            yield return Frames(2);
            Assert.AreEqual(4, _h.Rows.Shown.Count);
            var buttonsOutside = PokeButton.All.Where(b => !b.transform.IsChildOf(_h.Root.Workspace.transform)).Select(b => b.Label).ToList();
            CollectionAssert.IsEmpty(buttonsOutside, "every button is in the window");
            Assert.IsNull(GameObject.Find("StartPanel"));
            Assert.IsNull(GameObject.Find("Card"));
        }

        [UnityTest]
        public IEnumerator WithoutAgentsTheConversationSaysHowToConnect()
        {
            yield return _h.GoLive(agents: "");
            Assert.AreEqual(Headline.NoAgents, _h.Root.Workspace.Header.Said);
            Assert.AreEqual(Headline.NoAgentsHint, _h.Rows.EmptyShown);
            Assert.AreEqual(0, _h.Rows.Shown.Count);
            Assert.IsNull(_h.Root.SelectedAgent);
            Assert.IsNull(_h.Desk.OpenSid);
            Assert.IsNull(_h.Conversation.Input, "no input bar without an agent");
        }

        [UnityTest]
        public IEnumerator TheListSortsByNeedThenWorkThenDoneAndNothingIsSelectedByItself()
        {
            _h.SelectFirst = false;
            yield return _h.GoLive(agents: Agent("a", "A", "done", "Fertiger") + "," + Agent("b", "B", "working", "Arbeiter") + "," +
                                           Agent("c", "C", "waiting", "Fragender"));
            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, _h.Rows.Shown.Select(r => r.Sid).ToArray());
            Assert.IsNull(_h.Root.SelectedAgent, "three agents at once: the user chooses (U5a fix 1)");
            Assert.IsNull(_h.Desk.OpenSid, "no row opens by itself");
            Assert.AreEqual("Claude Code is holding for you.", _h.Root.Workspace.Header.Said);
            StringAssert.Contains("Fragender", _h.Rows.TitleShown("c"));
            Assert.AreEqual(OverviewText.Holding, _h.Rows.StatusShown("c"));
            Assert.AreEqual(OverviewText.NoPlan, _h.Rows.StatusShown("b"), "working without a plan");
            Assert.AreEqual(OverviewText.Landed, _h.Rows.StatusShown("a"));
            yield return _h.PokeRow("c");
            Assert.AreEqual("c", _h.Root.SelectedAgent);

            // b asks: it moves to the top behind c (the older ask); the selection stays
            _h.Socket.Receive(PermOpen("r1", "b"));
            yield return Frames(2);
            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, _h.Rows.Shown.Select(r => r.Sid).ToArray(), "c asked first");
            Assert.AreEqual("c", _h.Root.SelectedAgent, "a later arrival never changes the selection");
            Assert.AreEqual("c", _h.Desk.OpenSid, "nor the open row");
            Assert.AreEqual(TaxiState.Holding, _h.Rows.Shown[1].State);
            Assert.IsTrue(_h.Rows.RaisedOf("b"), "a thin line that holds keeps its raised fill");
            Assert.Greater(_h.Rows.LightsOf("b"), 0, "and a small stop bar");

            // a new agent arrives working: at the end of the working group; the selection stays
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("d", "D", "editing", "Neuer", "Bearbeitet src/Header.tsx") + "}");
            yield return Frames(4);
            CollectionAssert.AreEqual(new[] { "c", "b", "d", "a" }, _h.Rows.Shown.Select(r => r.Sid).ToArray());
            Assert.AreEqual("Bearbeitet src/Header.tsx", _h.Rows.Shown[2].Now);
            Assert.AreEqual("c", _h.Root.SelectedAgent);

            // events that keep d in its group never move a row
            yield return Frames(8);
            var version = _h.Rows.Version;
            var key = _h.Rows.Shown[2].Key;
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("d", "D", "editing", "Neuer", "Bearbeitet src/Header.tsx") + "}");
            yield return Frames(4);
            Assert.AreEqual(version, _h.Rows.Version, "nothing changed: nothing laid out again");
            Assert.AreEqual(key, _h.Rows.Shown[2].Key);
        }

        [UnityTest]
        public IEnumerator SelectingARowLightsItAndShowsItsConversation()
        {
            yield return _h.GoLive();
            yield return _h.PokeRow("s3");
            Assert.AreEqual("s3", _h.Root.SelectedAgent);
            Assert.AreEqual("s3", _h.Rows.OpenSid);
            Assert.AreEqual(TaxiRowMode.Open, _h.Rows.ModeOf("s3"));
            Assert.AreEqual(TaxiRowMode.Thin, _h.Rows.ModeOf("s2"));
            Assert.AreEqual(Headline.Landed("Claude Code"), _h.Root.Workspace.Header.Said);
            // a ray click selects too (no block shown: the ray is on)
            yield return _h.RayPinch(_h.Rows.RowOf("s2").transform.position);
            Assert.AreEqual("s2", _h.Root.SelectedAgent);
            Assert.AreEqual("s2", _h.Desk.OpenSid);
        }

        [UnityTest]
        public IEnumerator MoreThanSevenAgentsScrollTheListWithADrag()
        {
            // U13: five rows a page, a quiet pager in the header's tools; paging closes an open row
            var agents = string.Join(",", Enumerable.Range(1, 10).Select(i => Agent("x" + i, "X" + i, "working", "Agent Nummer " + i)));
            yield return _h.GoLive(agents: agents);
            Assert.AreEqual(TaxiRowsView.PerPage, _h.Rows.Shown.Count, "five rows a page");
            Assert.AreEqual("1/2", _h.Root.Workspace.Header.PageShown);
            Assert.IsNotNull(_h.Desk.OpenSid, "a row is open");
            yield return _h.Poke(_h.Root.Workspace.Header.NextButton);
            yield return Frames(2);
            Assert.IsNull(_h.Desk.OpenSid, "paging closes it");
            Assert.AreEqual("2/2", _h.Root.Workspace.Header.PageShown);
            Assert.AreEqual("x10", _h.Rows.Shown.Last().Sid);
            Assert.AreEqual(5, _h.Rows.Shown.Count);
        }

        [UnityTest]
        public IEnumerator EachOrbStateHasItsLookAndRuhigStopsAllMotion()
        {
            _h.OpenConversation = false;
            yield return _h.GoLive(agents: Agent("w", "W", "editing", "Bearbeiter", "Bearbeitet a.ts") + "," +
                                           Agent("c", "C", "testing", "Tester", "Führt Tests aus") + "," +
                                           Agent("n", "N", "waiting", "Fragender") + "," +
                                           Agent("d", "D", "done", "Fertiger") + "," +
                                           Agent("e", "E", "failed", "Kaputter"));
            yield return Frames(2);
            TaxiState State(string sid) => _h.Rows.Shown.First(r => r.Sid == sid).State;
            Assert.AreEqual(TaxiState.Moving, State("w"));
            Assert.AreEqual(TaxiState.Moving, State("c"));
            Assert.AreEqual(TaxiState.Holding, State("n"));
            Assert.AreEqual(TaxiState.Landed, State("d"));
            Assert.AreEqual(TaxiState.Idle, State("e"));
            Assert.AreEqual(OverviewText.Failed, _h.Rows.StatusShown("e"));
            Assert.IsTrue(_h.Rows.RaisedOf("n") && _h.Rows.LightsOf("n") == Taxi.RowLights, "needs you: raised, a stop bar");
            Assert.IsFalse(_h.Rows.RaisedOf("w") || _h.Rows.LightsOf("w") > 0);
            Assert.AreEqual(0, _h.Rows.LightsOf("d") + _h.Rows.LightsOf("e"));

            // only working agents send out a ring
            Assert.IsTrue(_h.Rows.PingShown("w") && _h.Rows.PingShown("c"), "working: the dot sends out its ring");
            Assert.IsFalse(_h.Rows.PingShown("n") || _h.Rows.PingShown("d") || _h.Rows.PingShown("e"), "the others stand still");

            // Calm, from the settings: nothing moves, the stop bar stays
            yield return _h.OpenSettings();
            yield return _h.Poke(_h.Root.Settings.CalmButton);
            Assert.IsTrue(Theme.Calm);
            Assert.AreEqual("on", _h.Prefs.Get(LookSettings.CalmKey), "kept for the next run");
            _h.Root.Settings.CloseButton.Click();
            yield return Frames(4);
            Assert.IsFalse(_h.Rows.PingShown("w") || _h.Rows.PingShown("c"), "no ring in Calm");
            Assert.AreEqual(Taxi.RowLights, _h.Rows.LightsOf("n"), "needs you keeps its stop bar");
        }

        [UnityTest]
        public IEnumerator NothingMovesWhileAHoldRuns()
        {
            yield return _h.GoLive(agents: Agent("s1", "A", "waiting", "Fragender") + "," + Agent("w", "W", "editing", "Bearbeiter", "Bearbeitet a.ts"));
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(4);
            Assert.IsTrue(_h.Rows.PingShown("w"), "before the hold: the working agent's ring");
            yield return _h.PinchHold(false, 3);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f);
            Assert.IsTrue(Theme.Still, "a hold runs: still");
            yield return Frames(2);
            Assert.IsFalse(_h.Rows.PingShown("w"), "nothing moves while the hold runs");
        }

        [UnityTest]
        public IEnumerator AnIncidentIsAMutedLineInTheAgentsConversationsAndANotice()
        {
            yield return _h.GoLive();
            _h.Socket.Receive("{\"type\":\"incident\",\"incident\":{\"kind\":\"collision\",\"sids\":[\"s2\",\"s1\"],\"detail\":\"Agent 2 und Agent 1 bearbeiten pricing.ts\",\"t\":1}}");
            yield return Frames(2);
            Assert.AreEqual("Agent 2 und Agent 1 bearbeiten pricing.ts", _h.Conversation.IncidentShown, "s1 is shown: its line");
            Assert.IsTrue(_h.Root.Workspace.Pill.IsShown);
            Assert.AreEqual("s2", _h.Root.Workspace.Pill.Sid);
            yield return _h.PokeRow("s3");
            Assert.IsNull(_h.Conversation.IncidentShown, "s3 is not involved");
            yield return _h.PokeRow("s2");
            Assert.AreEqual("Agent 2 und Agent 1 bearbeiten pricing.ts", _h.Conversation.IncidentShown);
            Assert.IsNull(_h.Root.Session.Card);
            Assert.AreEqual(0, _h.Decisions(), "an incident decides nothing");
        }

        [UnityTest]
        public IEnumerator TheTimeoutTextStaysUntilTheNextFeedEntry()
        {
            yield return _h.GoLive();
            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" + Entries(0, 3) + "]}");
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            _h.Socket.Receive(PermClosed("r1", "timeout"));
            yield return Frames(2);
            Assert.AreEqual(Texts.TimedOut, _h.Block.NoteShown);
            yield return Frames(60);
            Assert.AreEqual(Texts.TimedOut, _h.Block.NoteShown, "it stays");
            _h.Socket.Receive("{\"type\":\"feed_append\",\"sid\":\"s1\",\"entries\":[" + Entry("e9", "text", "sagt", "Weiter") + "]}");
            yield return Frames(2);
            Assert.IsFalse(_h.Block.IsShown, "until the next feed entry");
        }

        [UnityTest]
        public IEnumerator AtThePeakSixAgentsABlockAFullFeedTheNoticeAndAHoldStayWithinFortyEightDrawCalls()
        {
            var agents = string.Join(",", new[]
            {
                Agent("s1", "A", "blocked", "Checkout-Summen korrigieren"), Agent("s2", "B", "editing", "Warenkorb-Badge", "Bearbeitet src/Cart.tsx"),
                Agent("s3", "C", "testing", "Alte Seiten migrieren", "Führt Tests aus"), Agent("s4", "D", "working", "Produktsuche", "Arbeitet"),
                Agent("s5", "E", "done", "Header aufräumen"), Agent("s6", "F", "failed", "Login reparieren"),
            });
            yield return _h.GoLive(agents: agents);
            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" + Entries(0, 12) + "]}");
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "git push --force origin main"));
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(3);
            Assert.IsTrue(_h.Desk.BlockShown);
            // U10: a pressed area shows its pressed state for 0.18 s; the two presses that led here are over by now. One
            // control at most is hovered or pressed at a time: one renderer more than counted here.
            _h.Socket.Receive(PermOpen("r3", "s2")); // another agent asks: the notice shows
            yield return _h.PinchHold(true, 4); // and a hold runs on r1
            Assert.IsTrue(_h.Root.Workspace.Pill.IsShown, "the notice is shown");
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold is in progress");
            var window = _h.Root.Workspace.Window;
            var renderers = RenderProbe.Renderers(window);
            var passes = RenderProbe.Passes(window);
            Debug.Log($"[Lotse] render probe at the peak: {renderers} renderers, {passes} passes: {RenderProbe.Describe(window)}");
            Assert.LessOrEqual(renderers, 48, "the workspace's draw calls at the peak");
            Assert.LessOrEqual(passes, 48, "counted per shader pass too");
        }
    }
}
