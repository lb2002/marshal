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
    /// U14 part 1: the security review of U13. Every visible change of the shown request cancels a running hold, and
    /// the pinch held across it never approves ("+n more" changing, the rows laid out anew, the block's room changing,
    /// the window jumping); the verdict cell is never the one left out; the green "Cleared" stands its second before
    /// the same agent's next request, and nothing is approvable meanwhile; plus the tests the review missed.
    /// </summary>
    public class ReviewU13PlayTest
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

        DemoSession Session => _h.Root.Session;
        int Allows() => _h.DecisionFrames().Count(f => f.Contains("\"allow\""));

        IEnumerator Holding(string risk = "safe")
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", risk));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown, "s1's request on its open row");
            yield return _h.PinchHold(risk != "safe", 4);
            Assert.Greater(Session.HoldProgress, 0.1f, "a hold runs");
        }

        /// <summary>The hold is gone; the same pinch held on for 3 s sends nothing; a fresh full hold approves.</summary>
        IEnumerator CancelledAndOnlyAFreshHoldApproves(string why, bool twoHands = false)
        {
            Assert.AreEqual(0f, Session.HoldProgress, why + ": the hold is cancelled");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), why + ": the pinch held across it approves nothing");
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(twoHands, twoHands ? 20 : 12);
            Assert.AreEqual(1, Allows(), why + ": a fresh full hold approves");
        }

        // ---- finding 2: visible changes cancel a hold ----

        [UnityTest]
        public IEnumerator AnotherRequestChangingPlusNMoreCancelsAHold()
        {
            yield return Holding();
            var more = Session.MoreRequests;
            var builds = _h.Desk.BlockBuilds;
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(2);
            Assert.AreNotEqual(more, Session.MoreRequests, "\"+n more\" changed");
            Assert.AreEqual("r1", _h.Block.RequestId, "the same request is still shown");
            Assert.Greater(_h.Desk.BlockBuilds, builds, "its block was drawn again");
            Assert.AreEqual(0f, Session.HoldProgress, "the hold is cancelled");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "the pinch held across it approves nothing");
            Assert.IsTrue(_h.Lotse.Any(l => l.StartsWith(Ev.Prefix + "hold") && l.Contains("phase=cancel")), "the cancel is in the log");
        }

        [UnityTest]
        public IEnumerator AnotherAgentArrivingLaysTheRowsOutAnewAndCancelsAHold()
        {
            yield return Holding("destructive");
            var version = _h.Rows.Version;
            var room = _h.Block.MaxHeight;
            var builds = _h.Desk.BlockBuilds;
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s4", "Agent 4", "working", "Neue Seite") + "}");
            yield return Frames(3);
            Assert.AreNotEqual(version, _h.Rows.Version, "the rows were laid out anew");
            Assert.Less(_h.Block.MaxHeight, room, "the open row has less room (finding 4)");
            Assert.Greater(_h.Desk.BlockBuilds, builds, "so the block was built again for it");
            Assert.AreEqual("r1", _h.Block.RequestId);
            StringAssert.Contains("reason=BlockChanged", _h.Lotse.Last(l => l.StartsWith(Ev.Prefix + "hold") && l.Contains("phase=cancel")), "the log says why");
            yield return CancelledAndOnlyAFreshHoldApproves("another agent arrived", twoHands: true);
        }

        [UnityTest]
        public IEnumerator TheWindowJumpingCancelsAHold()
        {
            yield return Holding();
            var anchor = GameObject.Find("LotseAnchor").transform;
            var window = _h.Root.Workspace.transform.position;
            anchor.position += anchor.right * 0.03f; // the anchor is corrected by 3 cm: the window follows it
            yield return Frames(2);
            Assert.Greater((_h.Root.Workspace.transform.position - window).magnitude, 0.02f, "the window moved");
            StringAssert.Contains("reason=Moved", _h.Lotse.Last(l => l.StartsWith(Ev.Prefix + "hold") && l.Contains("phase=cancel")), "the log says why");
            yield return CancelledAndOnlyAFreshHoldApproves("the window jumped");
        }

        /// <summary>
        /// U14b: a change that happens after the app's Update of frame N (a layout in LateUpdate, an anchor correction)
        /// is seen before the session steps in frame N+1: the hold cannot complete in that frame.
        /// </summary>
        [UnityTest]
        public IEnumerator AHoldCannotCompleteInTheFrameAfterTheWindowJumped()
        {
            yield return Holding();
            // up to the frame before the one that would complete the hold
            var last = Session.HoldProgress;
            var guard = 0;
            while (guard++ < 40)
            {
                yield return null;
                var now = Session.HoldProgress;
                var step = now - last;
                last = now;
                if (now + step >= 1f - 1e-3f) break;
            }
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "not complete yet");
            Assert.Greater(Session.HoldProgress, 0.5f, "one frame before the hold completes");
            // here the app's Update of this frame is over
            var anchor = GameObject.Find("LotseAnchor").transform;
            anchor.position += anchor.right * 0.03f;
            yield return null;
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "the hold did not complete in the frame of the change");
            Assert.AreEqual(0f, Session.HoldProgress, "it is cancelled");
            yield return null;
            StringAssert.Contains("reason=Moved", _h.Lotse.Last(l => l.StartsWith(Ev.Prefix + "hold") && l.Contains("phase=cancel")), "the log says why");
            yield return CancelledAndOnlyAFreshHoldApproves("the window jumped in the hold's last frame");
        }

        /// <summary>The same loop without a change: the next frame completes the hold (the test above stops at the right frame).</summary>
        [UnityTest]
        public IEnumerator WithoutAChangeTheNextFrameCompletesTheHold()
        {
            yield return Holding();
            var last = Session.HoldProgress;
            var guard = 0;
            while (guard++ < 40)
            {
                yield return null;
                var now = Session.HoldProgress;
                var step = now - last;
                last = now;
                if (now + step >= 1f - 1e-3f) break;
            }
            Assert.AreEqual(0, Allows());
            yield return null;
            Assert.AreEqual(1, Allows(), "the frame after completes it");
        }

        [UnityTest]
        public IEnumerator AWindowThatStandsStillCancelsNothing()
        {
            yield return Holding();
            var anchor = GameObject.Find("LotseAnchor").transform;
            // an anchor's small corrections (a millimetre a frame) are not a change of the screen
            for (var i = 0; i < 8 && Allows() == 0; i++)
            {
                anchor.position += anchor.right * 0.001f;
                yield return null;
            }
            yield return Frames(4);
            Assert.AreEqual(1, Allows(), "the hold completes");
        }

        // ---- finding 3: the verdict is never the cell left out ----

        [UnityTest]
        public IEnumerator TheVerdictCellStaysWhenTheOtherFactsAreLong()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "rm -rf ../legacy-site", "Deletes a folder: rm -rf ../legacy-site"));
            yield return Frames(2);
            _h.Socket.Receive("{\"type\":\"perm_impact\",\"id\":\"r1\",\"impact\":{\"kind\":\"delete\",\"target\":\"~/code/legacy-site\",\"files\":9007199254740991," +
                              "\"filesCapped\":true,\"bytes\":999999999999999999,\"outsideProject\":true,\"recoverable\":\"no\",\"reason\":\"Not in a Git project: gone for good.\"}}");
            yield return Frames(3);
            Assert.AreEqual(Texts.FactGone + " " + Texts.FactGoneCaption, _h.Block.VerdictShown, "\"Gone / no way back\" is on the block");
            Assert.AreEqual(Theme.Stop, _h.Block.VerdictColor, "in red");
            var facts = _h.Block.GetComponentsInChildren<TMPro.TextMeshPro>().Where(t => t.name == "Fact").ToArray();
            Assert.IsTrue(facts.Any(t => t.text == Texts.FactGone), "as a drawn cell");
            Assert.Less(facts.Length, 4, "a cell that does not fit beside it is the one left out");
        }

        // ---- finding 5: "Cleared" stands its second before the same agent's next request ----

        [UnityTest]
        public IEnumerator ClearedStandsItsSecondBeforeTheNextRequestAndTheHeldPinchesNeverApproveIt()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive"));
            _h.Socket.Receive(PermOpen("r2", "s1", "destructive"));
            yield return Frames(2);
            Assert.AreEqual("r1", _h.Block.RequestId);
            yield return _h.PinchHold(true, 20);
            Assert.AreEqual(1, Allows(), "r1 approved with both hands");
            // both pinches stay closed from here on
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(2);
            Assert.IsTrue(_h.Block.ClearedShown, "the bar is green");
            Assert.AreEqual(Headline.Cleared("Claude Code"), _h.Block.NoteShown, "and says that the request was approved");
            Assert.IsNull(_h.Block.RequestId, "the next request is not on screen yet");
            Assert.AreEqual("r2", Session.Card?.Id, "though the session already holds it");
            yield return Frames(5);
            Assert.IsTrue(_h.Block.ClearedShown, "for its second");
            Assert.AreEqual(0f, Session.HoldProgress, "nothing can be held meanwhile");
            Assert.AreEqual(1, Allows());
            yield return Frames(8);
            Assert.IsFalse(_h.Block.ClearedShown);
            Assert.AreEqual("r2", _h.Block.RequestId, "then the next request takes the block");
            Assert.IsTrue(_h.Block.DenyButton.gameObject.activeInHierarchy, "with its buttons");
            yield return Frames(40);
            Assert.AreEqual(0f, Session.HoldProgress, "the pinches held through \"Cleared\" start no hold");
            Assert.AreEqual(1, Allows(), "and never approve the next request");
            _h.ReleaseHands();
            yield return Frames(3);
            yield return _h.PinchHold(true, 20);
            Assert.AreEqual(2, Allows(), "a fresh two-hand hold approves it");
        }

        [UnityTest]
        public IEnumerator AFreshPinchDuringClearedApprovesNothing()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(2);
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, Allows());
            _h.ReleaseHands();
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(2);
            Assert.IsTrue(_h.Block.ClearedShown);
            // released and pinched again while the green bar still stands: r2 is not on screen, so this is no approval
            yield return _h.PinchHold(false, 6);
            Assert.IsTrue(_h.Block.ClearedShown || _h.Block.RequestId == "r2");
            yield return Frames(30);
            Assert.AreEqual(1, Allows(), "a pinch begun while \"Cleared\" stood never approves the request that follows");
        }

        // ---- U14: the open row keeps its place ----

        [UnityTest]
        public IEnumerator AnApprovedRequestsRowStaysWhereItIsUntilItHasClosed()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            string[] Order() => _h.Rows.Shown.Select(r => r.Sid).ToArray();
            var before = Order();
            Assert.AreEqual("s1", before[0], "the asking agent is the first row, open");
            yield return _h.PinchHold(false, 12);
            _h.ReleaseHands();
            Assert.AreEqual(1, Allows());
            // the bridge says so: the agent works again (it would now belong at the end of the working ones)
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s1", "Agent 1", "working", "Warenkorb-Badge") + "}");
            _h.Socket.Receive(PermClosed("r1", "allow"));
            for (var i = 0; i < 9; i++)
            {
                yield return null;
                CollectionAssert.AreEqual(before, Order(), "while \"Cleared\" stands the row does not jump (frame " + i + ")");
            }
            Assert.IsTrue(_h.Block.ClearedShown);
            var guard = 0;
            while ((_h.Root.Desktop.OpenSid != null || !_h.Rows.Settled) && guard++ < 60)
            {
                yield return null;
                if (_h.Root.Desktop.OpenSid != null || !_h.Rows.Settled) CollectionAssert.AreEqual(before, Order(), "nor while it closes");
            }
            Assert.IsNull(_h.Root.Desktop.OpenSid, "the row has closed");
            yield return Frames(3);
            CollectionAssert.AreNotEqual(before, Order(), "then the rows take their usual order");
            Assert.AreEqual("s1", Order().Last(x => x != "s3"), "the agent that works again stands at the end of the working ones");
        }

        // ---- the tests the review missed ----

        [UnityTest]
        public IEnumerator DenyInTheFrameTheHoldWouldCompleteDoesNotAllow()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            var at = _h.GatePoint();
            _h.Hands.Right = Hand(pinchAt: at);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            var guard = 0;
            float last = 0f, step = 0f;
            // up to the frame before the hold completes
            while (guard++ < 40)
            {
                yield return null;
                var p = Session.HoldProgress;
                step = Mathf.Max(step, p - last);
                last = p;
                if (p > 0f && p + step >= 1f - 1e-4f) break;
            }
            Assert.That(Session.HoldProgress, Is.GreaterThan(0.5f).And.LessThan(1f), "one frame short of an approval");
            Assert.AreEqual(0, _h.DecisionFrames().Count());
            _h.Block.DenyButton.Click(); // "Deny" is pressed before the session's step of the next frame, as a poke is
            yield return Frames(10);
            Assert.AreEqual(1, _h.DecisionFrames().Count(), "one decision");
            StringAssert.Contains("\"behavior\":\"deny\"", _h.DecisionFrames().Single(), "and it is the deny");
            Assert.AreEqual(0, Allows(), "the hold that would have completed in that frame allows nothing");
        }

        [UnityTest]
        public IEnumerator ARequestOnAThinRowCannotBeApproved([Values("safe", "destructive")] string risk)
        {
            yield return _h.GoLive();
            yield return _h.OpenRow("s2");
            _h.Socket.Receive(PermOpen("r1", "s1", risk));
            yield return Frames(3);
            Assert.AreEqual("s2", _h.Root.Desktop.OpenSid, "another agent's row is open");
            Assert.IsTrue(_h.Rows.RaisedOf("s1"), "the asking agent's row is thin, raised, with its small stop bar");
            Assert.IsNull(Session.Card, "no request is on show");
            Assert.IsFalse(_h.Desk.BlockShown);
            yield return _h.PinchHold(true, 40);
            Assert.AreEqual(0f, Session.HoldProgress);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "no gesture approves a request whose row is closed");
            // nor one aimed at the thin row itself
            _h.ReleaseHands();
            yield return Frames(2);
            var row = _h.Rows.RowOf("s1").transform.position;
            _h.Camera.transform.rotation = Quaternion.LookRotation(row - HeadPos, Vector3.up);
            var at = HeadPos + (row - HeadPos).normalized * 0.35f;
            _h.Hands.Right = Hand(pinchAt: at);
            _h.Hands.Left = Hand(pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(2);
            _h.Hands.Right = Hand(1f, pinchAt: at);
            _h.Hands.Left = Hand(1f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(40);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nor a hold aimed at the thin row");
        }
    }
}
