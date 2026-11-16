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
    /// U5a fix 1 through AppRoot: an agent's second request gets a block that is built once and stays pressable (Ablehnen
    /// by poke and by ray click); no meshes leak as blocks come and go; placing and the pairing panel hide the workspace and
    /// cancel a hold; a press means the agent it was aimed at, also when the rows re-sort mid-press; an empty window shows
    /// its first agent, and only then.
    /// </summary>
    public class BlockLifecycleTest
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

        /// <summary>No room at the start (the fallback placement), a room without a desk once "Neu platzieren" is pressed.</summary>
        sealed class EmptyRoom : IDeskRaycaster
        {
            public RaycastAvailability Availability { get; set; } = RaycastAvailability.Unsupported;
            public void Begin() { }
            public void End() { }

            public DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
            {
                point = normal = Vector3.zero;
                return DeskRayStatus.NoHit;
            }
        }

        IEnumerator Approve(bool twoHands)
        {
            yield return _h.PinchHold(twoHands, twoHands ? 17 : 10);
            _h.ReleaseHands();
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheSecondRequestOfAnAgentCanBeDeniedByPokeAndByRayClick()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(2);
            Assert.AreEqual("r1", _h.Block.RequestId);
            yield return Approve(false);
            Assert.AreEqual(1, _h.Decisions(), "r1 allowed");
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(13); // (U14) the green "Cleared" stands its second first
            Assert.AreEqual("r2", _h.Block.RequestId, "the second request takes the block");
            var builds = _h.Desk.BlockBuilds;
            var deny = _h.Block.DenyButton;
            yield return Frames(10);
            Assert.AreEqual(builds, _h.Desk.BlockBuilds, "built once, then left alone");
            Assert.AreSame(deny, _h.Block.DenyButton);
            yield return _h.Poke(_h.Block.DenyButton);
            Assert.AreEqual(2, _h.Decisions(), "one poke on Ablehnen: one deny");
            StringAssert.Contains("\"id\":\"r2\",\"behavior\":\"deny\"", _h.DecisionFrames().Last());

            // again with a timeout before, and a ray click (the ray stays on while a block is shown)
            _h.Socket.Receive(PermClosed("r2", "deny"));
            _h.Socket.Receive(PermOpen("r3", "s1"));
            _h.Socket.Receive(PermOpen("r4", "s1"));
            yield return Frames(2);
            yield return _h.OpenRow("s1"); // (U13) the deny from here closed the row
            _h.Socket.Receive(PermClosed("r3", "timeout"));
            yield return Frames(3);
            Assert.AreEqual("r4", _h.Block.RequestId);
            yield return _h.RayPinch(_h.Block.DenyButton.transform.position);
            Assert.AreEqual(3, _h.Decisions(), "one ray click on Ablehnen: one deny");
            StringAssert.Contains("\"id\":\"r4\",\"behavior\":\"deny\"", _h.DecisionFrames().Last());
        }

        /// <summary>
        /// U5a review 2: when the agent's next request takes the block at once, the previous request's closing note stands as
        /// one line above the new block ("Allowed" for 3 s, the timeout text until the next feed entry), and the new
        /// block is neither rebuilt by it nor made unpressable.
        /// </summary>
        [UnityTest]
        public IEnumerator TheClosingNoteStandsAboveTheNextBlockWhichStaysBuiltOnceAndPressable()
        {
            yield return _h.GoLive();
            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" + Entries(0, 3) + "]}");
            _h.Socket.Receive(PermOpen("r1", "s1"));
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(2);
            yield return Approve(false);
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(2);
            // (U14) the closing note of U5a is, since U13, the green bar: the user is told that the request before was
            // approved before the next one takes the block
            Assert.IsTrue(_h.Block.ClearedShown, "the bar is green");
            Assert.AreEqual(Headline.Cleared("Claude Code"), _h.Block.NoteShown, "\"Cleared. Claude Code is moving again\"");
            Assert.IsNull(_h.Block.RequestId, "the next request is not shown yet");
            yield return Frames(12);
            Assert.IsFalse(_h.Block.ClearedShown);
            Assert.AreEqual("r2", _h.Block.RequestId);
            var builds = _h.Desk.BlockBuilds;
            yield return Frames(35); // the note's 3 s run out
            Assert.AreEqual(builds, _h.Desk.BlockBuilds, "the note never rebuilt the block");
            Assert.AreEqual("r2", _h.Block.RequestId);

            // a timeout while the agent has another request: the text stays above the block until the next feed entry
            _h.Socket.Receive(PermOpen("r3", "s1"));
            yield return Frames(2);
            _h.Socket.Receive(PermClosed("r2", "timeout"));
            yield return Frames(2);
            Assert.AreEqual("r3", _h.Block.RequestId);
            builds = _h.Desk.BlockBuilds;
            yield return Frames(60);
            Assert.AreEqual(builds, _h.Desk.BlockBuilds, "the note never rebuilt the block");
            yield return _h.Poke(_h.Block.DenyButton);
            StringAssert.Contains("\"id\":\"r3\",\"behavior\":\"deny\"", _h.DecisionFrames().Last(), "the block under the note is pressable");
        }

        [UnityTest]
        public IEnumerator NoMeshesLeakAcrossFiftyBlockChanges()
        {
            yield return _h.GoLive();
            for (var i = 0; i < 3; i++) // warm up
            {
                _h.Socket.Receive(PermOpen("w" + i, "s1"));
                yield return Frames(2);
                _h.Socket.Receive(PermClosed("w" + i, "timeout"));
                yield return Frames(2);
            }
            yield return Frames(2);
            var beforeAll = Resources.FindObjectsOfTypeAll<Mesh>();
            var before = beforeAll.Length;
            for (var i = 0; i < 50; i++)
            {
                _h.Socket.Receive(PermOpen("m" + i, "s1"));
                yield return Frames(2);
                Assert.AreEqual("m" + i, _h.Block.RequestId);
                _h.Socket.Receive(PermClosed("m" + i, "timeout"));
                yield return Frames(2);
            }
            yield return Frames(3);
            var afterAll = Resources.FindObjectsOfTypeAll<Mesh>();
            var after = afterAll.Length;
            var grown = string.Join(", ", afterAll.Except(beforeAll).GroupBy(m => m.name).Select(g => g.Key + " x" + g.Count()));
            Debug.Log($"[Lotse] meshes before {before}, after 50 block changes {after}");
            Assert.LessOrEqual(after - before, 4, "the block's meshes go with the block; new: " + grown);
        }

        [UnityTest]
        public IEnumerator PlacingHidesTheWorkspaceAndCancelsTheHold()
        {
            var room = new EmptyRoom();
            _h.Configure = r => r.Raycaster = room;
            yield return _h.GoLive();
            room.Availability = RaycastAvailability.Ready;
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PinchHold(false, 4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f);
            _h.Root.Placer.Begin(Time.timeAsDouble * 1000.0, trySaved: false); // "Neu platzieren"
            yield return Frames(2);
            Assert.IsTrue(_h.Root.Placer.Active);
            Assert.IsFalse(_h.Root.Session.ViewShown, "placing hides the block");
            Assert.IsNull(_h.Root.Session.Card);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the hold is cancelled");
            yield return Frames(20);
            Assert.AreEqual(0, _h.Decisions(), "nothing sent while placing");
        }

        [UnityTest]
        public IEnumerator ThePairingPanelHidesTheWorkspaceAndNothingIsDecided()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            yield return _h.PinchHold(false, 4);
            _h.Socket.Drop(1006);
            var guard = 0;
            while (_h.Sockets.Count < 2 && guard++ < 200) yield return null;
            Assert.AreEqual(2, _h.Sockets.Count, "it reconnects by itself");
            var again = _h.Sockets.Last();
            again.Open();
            again.Receive("{\"type\":\"error\",\"error\":\"bad-session\"}"); // the stored session is refused: a new code is needed
            guard = 0;
            while (_h.Root.Current != AppRoot.Phase.Pairing && guard++ < 10) yield return null;
            Assert.AreEqual(AppRoot.Phase.Pairing, _h.Root.Current, "the pairing panel");
            // in the very frame the panel appears (in the middle of the session's step), the block is gone already
            Assert.IsFalse(_h.Root.Session.ViewShown, "hidden at once, not one frame later");
            Assert.IsNull(_h.Root.Session.Card);
            yield return Frames(2);
            Assert.IsFalse(_h.Root.Workspace.IsShown, "the workspace goes");
            Assert.IsFalse(_h.Root.Session.ViewShown);
            Assert.IsNull(_h.Root.Session.Card);
            yield return Frames(20);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing decided");
        }

        [UnityTest]
        public IEnumerator APressMeansTheAgentItWasAimedAtEvenWhenTheRowsReSort()
        {
            _h.SelectFirst = false;
            yield return _h.GoLive(agents: Agent("a", "A", "working", "Erster") + "," + Agent("b", "B", "working", "Zweiter") + "," +
                                           Agent("c", "C", "working", "Dritter"));
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, _h.Rows.Shown.Select(r => r.Sid).ToArray());
            var row = _h.Rows.RowOf("b").transform;
            // the finger comes to the second row (b) and stops in front of it
            _h.Hands.Right = Hand(tip: row.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            _h.Hands.Right = Hand(tip: row.TransformPoint(new Vector3(0f, 0f, -0.01f)));
            yield return Frames(2);
            // c asks: it jumps to the top, so the second row now shows a
            _h.Socket.Receive(PermOpen("r1", "c"));
            yield return Frames(2);
            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, _h.Rows.Shown.Select(r => r.Sid).ToArray());
            // the finger goes through the second row
            _h.Hands.Right = Hand(tip: row.TransformPoint(new Vector3(0f, 0f, 0.003f)));
            yield return Frames(2);
            _h.Hands.Right = Hand();
            yield return null;
            Assert.AreNotEqual("a", _h.Root.SelectedAgent, "a press aimed at b never selects a");
        }

        [UnityTest]
        public IEnumerator AnEmptyWindowShowsItsFirstAgentAndOnlyThenWithoutAHoldCarryingOver()
        {
            yield return _h.GoLive(agents: "");
            Assert.IsNull(_h.Root.SelectedAgent);
            // a pinch is held in front of the window, then the first agent arrives with a request
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            yield return Frames(2);
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s1", "A", "blocked", "Erster") + "}");
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            Assert.AreEqual("s1", _h.Root.SelectedAgent, "from no agent to one: it is shown");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "no hold was running");
            _h.Hands.Right = Hand(1f, pinchAt: _h.GatePoint());
            yield return Frames(30);
            Assert.AreEqual(0, _h.Decisions(), "the pinch held before never approves");
            // a second agent never changes the selection
            _h.ReleaseHands();
            yield return _h.PokeRow("s1");
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s2", "B", "waiting", "Zweiter") + "}");
            yield return Frames(3);
            Assert.AreEqual("s1", _h.Root.SelectedAgent);
        }

        [UnityTest]
        public IEnumerator SeveralAgentsArrivingAtOnceSelectNothing()
        {
            _h.SelectFirst = false;
            yield return _h.GoLive(agents: Agent("a", "A", "blocked", "Erster") + "," + Agent("b", "B", "working", "Zweiter"));
            _h.Socket.Receive(PermOpen("r1", "a"));
            yield return Frames(3);
            Assert.IsNull(_h.Root.SelectedAgent, "only a single first agent is shown by itself");
            Assert.IsNull(_h.Root.Session.Card, "so no block opens by itself");
        }

        [UnityTest]
        public IEnumerator ACommandTheBlockHasToCutNeedsBothHandsThoughTheBridgeSentItWhole()
        {
            yield return _h.GoLive();
            // harmless and under the bridge's 300 characters (not truncated), but far more than the block's two lines
            var command = "echo " + string.Join(" ", Enumerable.Range(0, 40).Select(i => "wort" + i));
            Assert.Less(command.Length, 300);
            _h.Socket.Receive(PermOpen("r1", "s1", "safe", command));
            yield return Frames(3);
            Assert.AreEqual("r1", _h.Block.RequestId);
            Assert.IsTrue(_h.Root.Session.Card.NeedsTwoHand, "what the block had to cut needs both hands");
            Assert.AreEqual(Texts.RiskDangerous, _h.Block.RiskShown);
            yield return _h.PinchHold(false, 30);
            _h.ReleaseHands();
            yield return null;
            Assert.AreEqual(0, _h.Decisions(), "one hand never approves what the block had to cut");
        }

        // ---- the same request id, sent again with other content (security review): a new block, the hold cancelled ----

        static string Open(string tool = "Read", string risk = "safe", string paths = "\"src/a.ts\"", bool truncated = false, string explain = "") =>
            "{\"type\":\"perm_open\",\"request\":{\"id\":\"r1\",\"sid\":\"s1\",\"tool\":\"" + tool + "\",\"truncated\":" + (truncated ? "true" : "false") +
            ",\"paths\":[" + paths + "],\"risk\":{\"risk\":\"" + risk + "\",\"reason\":\"t\"},\"createdAt\":1000,\"expiresAt\":91000}," +
            "\"explain\":{\"text\":\"" + explain + "\",\"replies\":[],\"source\":\"rules\"}}";

        /// <summary>r1 shown, a one-hand hold running; `again` re-sends r1 changed; the block must show it and the hold restart.</summary>
        IEnumerator ResentWhileHolding(string first, string again, System.Func<ApprovalBlockView, string> shown, string expected)
        {
            yield return _h.GoLive();
            _h.Socket.Receive(first);
            yield return Frames(2);
            Assert.AreEqual("r1", _h.Block.RequestId);
            var before = shown(_h.Block);
            var builds = _h.Desk.BlockBuilds;
            yield return _h.PinchHold(false, 4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a hold runs");
            _h.Socket.Receive(again);
            yield return Frames(2);
            Assert.AreEqual("r1", _h.Block.RequestId);
            Assert.Greater(_h.Desk.BlockBuilds, builds, "the block is drawn again");
            StringAssert.Contains(expected, shown(_h.Block), "the block shows the new content");
            Assert.AreNotEqual(before, shown(_h.Block));
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the running hold is cancelled");
            yield return Frames(30); // the same pinch stays held: it never approves the changed request
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing sent");
            _h.ReleaseHands();
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheSameRequestResentWithAnotherRiskIsANewBlock() =>
            ResentWhileHolding(Open(risk: "safe"), Open(risk: "network"), b => b.RiskShown, Texts.RiskNetwork);

        [UnityTest]
        public IEnumerator TheSameRequestResentWithOtherPathsIsANewBlock() =>
            ResentWhileHolding(Open(paths: "\"src/a.ts\""), Open(paths: "\"src/a.ts\",\"etc/geheim.ts\""), b => b.SubjectShown, "etc/geheim.ts");

        [UnityTest]
        public IEnumerator TheSameRequestResentTruncatedIsANewBlock() =>
            ResentWhileHolding(Open(truncated: false), Open(truncated: true), b => b.RiskShown, Texts.RiskDangerous);

        [UnityTest]
        public IEnumerator TheSameRequestResentWithAnotherCommandIsANewBlock() =>
            ResentWhileHolding(PermOpen("r1", "s1", "safe", "ls -la"), PermOpen("r1", "s1", "safe", "rm -rf build"), b => b.SubjectShown, "rm -rf build");

        /// <summary>U5a review 2: a changed explanation (the headline) of the shown request cancels a running hold too.</summary>
        [UnityTest]
        public IEnumerator AChangedExplanationRedrawsTheBlockAndCancelsTheHold() =>
            ResentWhileHolding(PermOpen("r1", "s1", explain: "Claude listet die Dateien: ls -la"),
                "{\"type\":\"perm_explain\",\"id\":\"r1\",\"explain\":{\"text\":\"Claude liest dein Home: ls -la\",\"replies\":[],\"source\":\"ai\"}}",
                b => b.HeadlineShown, "Claude liest dein Home");

        /// <summary>U5a review 2: the ray half of the press binding: a ray press aimed at a row whose agent changes clicks nothing.</summary>
        [UnityTest]
        public IEnumerator ARayPressMeansTheAgentItWasAimedAtEvenWhenTheRowsReSort()
        {
            _h.SelectFirst = false;
            yield return _h.GoLive(agents: Agent("a", "A", "working", "Erster") + "," + Agent("b", "B", "working", "Zweiter") + "," +
                                           Agent("c", "C", "working", "Dritter"));
            var at = _h.Rows.RowOf("b").transform.position;
            _h.Hands.Right = Hand(rayAt: at);
            yield return null;
            _h.Hands.Right = Hand(1f, rayAt: at);
            yield return null;
            _h.Socket.Receive(PermOpen("r1", "c")); // c jumps to the top: the second row now shows a
            yield return Frames(2);
            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, _h.Rows.Shown.Select(r => r.Sid).ToArray());
            _h.Hands.Right = Hand(rayAt: at);
            yield return null;
            _h.Hands.Right = Hand();
            yield return null;
            Assert.IsNull(_h.Root.SelectedAgent, "a ray press aimed at b never selects a");
        }

        [UnityTest]
        public IEnumerator TheSameRequestResentWithAnotherToolIsANewBlock() =>
            ResentWhileHolding(Open(tool: "Read", paths: ""), Open(tool: "WebFetch", paths: ""), b => b.SubjectShown, "WebFetch");
    }
}
