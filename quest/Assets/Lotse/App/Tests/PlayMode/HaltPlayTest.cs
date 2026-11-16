using System.Collections;
using System.Linq;
using Lotse.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// Halt-all (U7) through AppRoot with a fake bridge and scripted hands: the stop gesture sends one halt and cancels a
    /// hold the moment it starts to count; the state shown follows only the bridge's halt_state; no block and no pill
    /// while halted; release needs "Continue" and its confirmation; the wrist menu's button asks once; the demo halts
    /// locally; the gesture is ignored in every excluded situation; nothing here ever sends an allow.
    /// </summary>
    public class HaltPlayTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
        }

        [TearDown]
        public void TearDown()
        {
            _h.TearDown();
        }

        const string HaltOn = "{\"type\":\"halt\",\"on\":true}";
        const string HaltOff = "{\"type\":\"halt\",\"on\":false}";

        static string HaltState(bool on) =>
            "{\"type\":\"halt_state\",\"on\":" + (on ? "true" : "false") +
            ",\"since\":1000,\"enforcement\":{\"claude\":\"every-step\",\"codex\":\"every-step\",\"muse\":\"requests-only\"}}";

        int Halts(string frame) => _h.Sockets.SelectMany(s => s.Sent).Count(f => f == frame);

        int Allows() => _h.DecisionFrames().Count(f => f.Contains("\"allow\""));

        /// <summary>An open hand in the stop pose: palm away from the head, fingers straight, at `x` beside the head.</summary>
        HandFrame StopHand(float x, float pinch = 0f, Vector3? tip = null)
        {
            var head = _h.Camera.transform;
            var at = head.position + head.forward * 0.4f + head.right * x - Vector3.up * 0.1f;
            var f = Hand(pinch, tip: tip, pinchAt: at);
            f.PalmValid = true;
            f.PalmCenter = at;
            f.PalmNormal = head.forward;
            f.FingersValid = true;
            f.IndexStraight = f.MiddleStraight = f.RingStraight = f.LittleStraight = 0.98f;
            return f;
        }

        void StopPose()
        {
            _h.Hands.Left = StopHand(-0.2f);
            _h.Hands.Right = StopHand(0.2f);
        }

        /// <summary>The stop pose held this long (frames of 0.1 s).</summary>
        IEnumerator HoldStop(int frames)
        {
            StopPose();
            yield return Frames(frames);
            _h.ReleaseHands();
            yield return Frames(1);
        }

        IEnumerator Halted()
        {
            _h.Socket.Receive(HaltState(true));
            yield return Frames(2);
            Assert.IsTrue(_h.Root.Session.Halted);
        }

        [UnityTest]
        public IEnumerator TheStopGestureCancelsAHoldTheMomentItCountsAndSendsOneHalt()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "rm -rf ../legacy-site"));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown);
            yield return _h.PinchHold(true, 4);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a two-hand hold runs");

            StopPose();
            yield return Frames(8); // the hands were not seen in the pose yet: half a second of quiet first
            Assert.IsTrue(_h.Root.Stop.Counting, "the stop gesture counts");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the hold is cancelled the moment it counts");
            Assert.IsTrue(_h.Root.Workspace.Halt.BarShown, "the amber bar fills");
            Assert.AreEqual(Texts.HaltAll, _h.Root.Workspace.Halt.BarLabel);
            Assert.That(_h.Root.Workspace.Halt.BarProgress, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.IsTrue(_h.Lotse.Contains("[Lotse] stop gesture: counting"));

            yield return Frames(11);
            Assert.AreEqual(1, Halts(HaltOn), "one halt");
            Assert.IsTrue(_h.Lotse.Contains("[Lotse] stop gesture: triggered"));
            Assert.IsFalse(_h.Root.Workspace.Halt.BarShown);
            Assert.IsFalse(_h.Root.Session.Halted, "not shown halted before the bridge says so");
            Assert.IsFalse(_h.Root.Workspace.Halted);
            yield return Frames(20);
            Assert.AreEqual(1, Halts(HaltOn), "held on, still one halt");
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "nothing decided");

        }

        [UnityTest]
        public IEnumerator TheHaltedStateFollowsTheBridgesHaltStateOnly()
        {
            yield return _h.GoLive();
            yield return HoldStop(10); // let go early: it resets, the reason logged
            Assert.IsTrue(_h.Lotse.Contains("[Lotse] stop gesture: counting"));
            Assert.IsTrue(_h.Lotse.Contains("[Lotse] stop gesture: reset (tracking lost)"), "the reset is logged with its reason");
            Assert.AreEqual(0, Halts(HaltOn));
            yield return HoldStop(22);
            Assert.AreEqual(1, Halts(HaltOn));
            Assert.IsFalse(_h.Root.Workspace.Halt.BandShown, "no band before halt_state");

            yield return Halted();
            var halt = _h.Root.Workspace.Halt;
            Assert.IsTrue(halt.BandShown);
            StringAssert.StartsWith("Halted for ", halt.BandText);
            Assert.AreEqual("Claude Code and Codex stop before their next step. Muse: only requests are denied.", halt.EnforcementText);
            Assert.IsTrue(_h.Root.Workspace.Halted);
            Assert.AreEqual(Headline.Stopped, _h.Root.Workspace.Header.Said, "the header says it");
            Assert.IsTrue(_h.Rows.Shown.Where(r => r.State != TaxiState.Landed).All(r => r.State == TaxiState.Stopped && r.Status == OverviewText.Stopped),
                "every agent that has not landed is stopped");
            Assert.IsFalse(_h.Root.Workspace.Menu.HaltOffered, "no halt button while halted");

            _h.Socket.Receive(HaltState(false));
            yield return Frames(2);
            Assert.IsFalse(halt.BandShown, "the bridge released it");
            Assert.IsFalse(_h.Root.Workspace.Halted);
            yield return Frames(3);
            Assert.IsTrue(_h.Rows.Shown.All(r => r.State != TaxiState.Stopped));
            Assert.AreNotEqual(Headline.Stopped, _h.Root.Workspace.Header.Said);
            Assert.AreEqual(0, Halts(HaltOff), "nothing here released it");
        }

        [UnityTest]
        public IEnumerator WhileHaltedThereIsNoBlockAndNoPill()
        {
            yield return _h.GoLive();
            yield return Halted();
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive"));
            _h.Socket.Receive(PermOpen("r2", "s2"));
            yield return Frames(80);
            Assert.IsFalse(_h.Desk.BlockShown, "no approval block while halted");
            Assert.IsFalse(_h.Root.Workspace.Pill.IsShown, "no notice pill while halted");
            yield return _h.PinchHold(true, 30);
            Assert.AreEqual(0, _h.DecisionFrames().Count());

            // the window still works: the overview shows
            yield return _h.CloseRow();
            Assert.IsTrue(_h.Rows.gameObject.activeInHierarchy);
            Assert.AreEqual(Taxi.RowLights, _h.Rows.LightsOf("s2"), "a stopped agent has a stop bar");
        }

        [UnityTest]
        public IEnumerator ReleaseNeedsContinueAndItsConfirmation()
        {
            yield return _h.GoLive();
            yield return Halted();
            var halt = _h.Root.Workspace.Halt;
            yield return _h.Poke(halt.ContinueButton);
            Assert.IsTrue(halt.Asking);
            Assert.AreEqual(Texts.ContinueAsk, halt.BandText);
            Assert.AreEqual(0, Halts(HaltOff), "Continue alone sends nothing");
            yield return _h.Poke(halt.CancelButton);
            Assert.IsFalse(halt.Asking);
            Assert.AreEqual(0, Halts(HaltOff));

            yield return _h.Poke(halt.ContinueButton);
            yield return _h.Poke(halt.YesButton);
            Assert.AreEqual(1, Halts(HaltOff), "Yes after Continue releases");
            Assert.IsTrue(_h.Root.Session.Halted, "still halted until the bridge says otherwise");
            _h.Socket.Receive(HaltState(false));
            yield return Frames(2);
            Assert.IsFalse(halt.BandShown);
            Assert.AreEqual(0, Allows());
        }

        [UnityTest]
        public IEnumerator TheMenuButtonAsksOnceBeforeItHalts()
        {
            // U8: the header's Menu (the wrist menu is off)
            yield return _h.GoLive();
            yield return _h.OpenMenu();
            var menu = _h.Root.Workspace.Menu;
            menu.HaltButton.Click();
            yield return Frames(1);
            Assert.IsTrue(menu.AskingHalt);
            Assert.AreEqual(0, Halts(HaltOn), "the button alone sends nothing");
            menu.HaltCancelButton.Click();
            yield return Frames(1);
            Assert.IsFalse(menu.AskingHalt);
            Assert.AreEqual(0, Halts(HaltOn));
            yield return _h.OpenMenu(); // U9: the question is the window's band; the menu closed when it asked
            menu.HaltButton.Click();
            yield return Frames(1);
            Assert.IsFalse(menu.IsOpen, "the menu gives way to the question band");
            Assert.AreEqual(Texts.HaltAsk, _h.Root.Workspace.Question.TextShown);
            menu.HaltYesButton.Click();
            yield return Frames(1);
            Assert.AreEqual(1, Halts(HaltOn));
        }

        [UnityTest]
        public IEnumerator WithoutAConnectionTheBannerSaysNothingIsHalted()
        {
            yield return _h.GoLive();
            _h.Socket.Drop(1006);
            yield return Frames(2);
            yield return HoldStop(22);
            Assert.AreEqual(0, Halts(HaltOn));
            Assert.AreEqual(Texts.HaltNotConnected, _h.Root.Banner.Line);
            Assert.IsFalse(_h.Root.Session.Halted);
        }

        [UnityTest]
        public IEnumerator TheDemoPausesShowsItsRequestDeniedAndResumes()
        {
            yield return _h.StartDemo();
            var s = _h.Root.Session;
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 3000) yield return null;
            Assert.AreEqual(1, s.Store.Pending.Count, "a demo request is open");
            var closed = "";
            s.RequestClosed += (r, reason, sent) => closed = reason;

            yield return HoldStop(22);
            Assert.IsTrue(s.Halted);
            Assert.AreEqual("halt", closed, "the open demo request shows as denied with the halt message");
            Assert.AreEqual(0, s.Store.Pending.Count);
            var halt = _h.Root.Workspace.Halt;
            Assert.IsTrue(halt.BandShown);
            Assert.AreEqual("Claude Code, Codex, Muse and Gemini CLI stop before their next step.", halt.EnforcementText);
            var before = string.Join("|", s.Store.Agents.Select(a => a.Status + a.LastAction));
            yield return Frames(60);
            Assert.AreEqual(before, string.Join("|", s.Store.Agents.Select(a => a.Status + a.LastAction)), "playback paused");

            yield return _h.Poke(halt.ContinueButton);
            yield return _h.Poke(halt.YesButton);
            Assert.IsFalse(s.Halted);
            Assert.IsFalse(halt.BandShown);
            guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 3000) yield return null;
            Assert.AreEqual(1, s.Store.Pending.Count, "playback resumed");
            Assert.AreEqual(0, s.Decisions.Count(d => d.Behavior == "allow"));
        }

        // ---- ignored in every excluded situation ----

        [UnityTest]
        public IEnumerator TheGestureIsIgnoredWhileTheDeskKeyboardIsOpen()
        {
            yield return _h.GoLive();
            yield return _h.PokeRow("s2");
            var toggle = PokeButton.All.FirstOrDefault(b => b.Label == Texts.KeyboardToggle && b.isActiveAndEnabled);
            Assert.IsNotNull(toggle, "the keyboard button");
            toggle.Click();
            yield return Frames(2);
            Assert.IsTrue(_h.Root.Reply.Typing, "the desk keyboard is open");
            yield return HoldStop(20);
            Assert.AreEqual(0, Halts(HaltOn));
            Assert.IsFalse(_h.Lotse.Contains("[Lotse] stop gesture: counting"));
        }

        [UnityTest]
        public IEnumerator TheGestureIsIgnoredWhileAFingertipIsAtAButton()
        {
            yield return _h.GoLive();
            var b = _h.Root.Workspace.Header.MenuButton;
            _h.Hands.Left = StopHand(-0.2f);
            _h.Hands.Right = StopHand(0.2f, tip: b.transform.TransformPoint(new Vector3(0f, 0f, -0.01f)));
            yield return Frames(20);
            Assert.AreEqual(0, Halts(HaltOn), "a poke may be in progress");
        }

        [UnityTest]
        public IEnumerator TheGestureIsIgnoredWhilePairing()
        {
            yield return _h.GoLive(withSession: false);
            Assert.AreEqual(AppRoot.Phase.Pairing, _h.Root.Current);
            yield return HoldStop(20);
            Assert.IsFalse(_h.Lotse.Contains("[Lotse] stop gesture: counting"));
        }

        [UnityTest]
        public IEnumerator TheGestureIsIgnoredWhileSettingUpADisplay()
        {
            yield return _h.GoLive();
            var begin = typeof(AppRoot).GetMethod("BeginScreenSetup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            begin.Invoke(_h.Root, null);
            yield return Frames(1);
            Assert.IsTrue(_h.Root.Setup.Active);
            yield return HoldStop(20);
            Assert.AreEqual(0, Halts(HaltOn), "not during the display setup");


        }

        [UnityTest]
        public IEnumerator FocusOrTrackingFlickerDuringThePoseNeverTriggers()
        {
            yield return _h.GoLive();
            for (var i = 0; i < 6; i++)
            {
                StopPose();
                yield return Frames(4);
                _h.Focus = false; // the system menu took the focus for a frame
                yield return Frames(1);
                _h.Focus = true;
            }
            for (var i = 0; i < 6; i++)
            {
                StopPose();
                yield return Frames(4);
                _h.Hands.Left = Hand(tracked: false);
                yield return Frames(1);
            }
            _h.ReleaseHands();
            yield return Frames(2);
            Assert.AreEqual(0, Halts(HaltOn));
        }

        [UnityTest]
        public IEnumerator OneHandOrAPinchInThePoseNeverTriggers()
        {
            yield return _h.GoLive();
            _h.Hands.Left = Hand();
            _h.Hands.Right = StopHand(0.2f);
            yield return Frames(30);
            _h.Hands.Left = StopHand(-0.2f, pinch: 1f);
            _h.Hands.Right = StopHand(0.2f);
            yield return Frames(30);
            Assert.AreEqual(0, Halts(HaltOn));
            Assert.IsFalse(_h.Lotse.Contains("[Lotse] stop gesture: counting"));
        }

        [UnityTest]
        public IEnumerator TheHaltedWorkspaceStaysWithinFortyEightDrawCalls()
        {
            yield return _h.GoLive();
            yield return Halted();
            yield return _h.Poke(_h.Root.Workspace.Halt.ContinueButton); // the question: its most buttons
            yield return Frames(2);
            var window = _h.Root.Workspace.Window;
            var renderers = RenderProbe.Renderers(window);
            var passes = RenderProbe.Passes(window);
            Debug.Log($"[Lotse] render probe, halted workspace: {renderers} renderers, {passes} passes: {RenderProbe.Describe(window)}");
            Assert.LessOrEqual(renderers, 48);
            Assert.LessOrEqual(passes, 48);
        }
    }
}
