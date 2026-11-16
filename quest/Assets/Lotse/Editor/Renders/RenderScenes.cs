using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using Unity.Profiling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lotse.EditorTools
{
    /// <summary>
    /// The script behind the renders (U5a): the real AppRoot with the demo pack, driven like a user would drive it (pokes
    /// and ray clicks become PokeButton.Click and ray pinches, holds are scripted hands inside the hold gate), with a
    /// render at each screen of the calm workspace. The geometry is the device's: head at 1.23 m, desk at 0.76 m, the
    /// desk anchor 0.45 m forward and 0.4 m right; the workspace stands 0.42 m right of it (WorkspaceDock). Fixed 100 ms
    /// frames, so the 3-minute demo takes seconds. Nothing is sent anywhere. The render probe logs the workspace's draw
    /// calls with six agents.
    /// </summary>
    sealed partial class RenderScenes
    {
        static readonly Vector3 Eye = new Vector3(0f, 1.23f, 0f);
        const float DeskY = 0.76f;
        static readonly Vector3 DeskSpot = new Vector3(0.4f, DeskY, 0.45f);
        // the right hand's ray starts low in front of the right shoulder; idle pinches sit at lap height
        static readonly Vector3 RayFrom = Eye + new Vector3(0.15f, -0.3f, 0.25f);
        static readonly Vector3 Lap = Eye + new Vector3(0.1f, -0.6f, 0.3f);
        // the left palm held up low in front of the user (the wrist menu)
        static readonly Vector3 PalmAt = Eye + new Vector3(-0.14f, -0.32f, 0.34f);
        const float FrameSeconds = 0.1f;
        const float HoldFrameSeconds = 0.02f;
        const float WallDistance = 1.1f;

        static readonly string[] Expected =
        {
            "00-start", "00-start-closeup", "06-pairing", "06-pairing-closeup",
            "u13-04-work", "u13-04-work-closeup", "08-desk-keyboard", "08-desk-keyboard-closeup",
            "07-settings", "07-settings-closeup", "u13-01-over", "u13-01-over-closeup",
            "02-workspace-harmless", "02-workspace-harmless-closeup", "u13-02-open", "u13-02-open-closeup", "u13-03-hold", "u13-03-hold-closeup",
            "09-six-agents-closeup",
            "10-overview", "10-overview-closeup", "11-overview-six-closeup",
            "14-rail", "14-rail-closeup", "u13-08-rail", "u13-08-rail-closeup",
            "16-screen-setup", "17-docked-band", "17-docked-band-closeup", "18-docked-right", "18-docked-right-closeup", "19-docked-above-laptop",
            "20-stop-bar-closeup", "u13-05-halt", "u13-05-halt-closeup", "22-menu-closeup", "23-header-scale-1-closeup", "23-header-scale-2-closeup",
            "24-question-band-closeup", "25-halt-question-closeup", "26-overview-no-plan-closeup", "27-test-band-closeup",
            "28-test-two-hand-closeup", "29-test-summary-closeup", "30-plain-overview", "31-plain-dangerous",
            "u13-06-pair", "u13-06-pair-closeup", "u13-07-found", "u13-07-found-closeup", "34-pair-denied-closeup",
            "35-pair-connecting-closeup", "36-pair-failed-closeup",
            "u14-00-start", "u14-00-start-closeup", "u14-01-lines", "u14-01-lines-closeup", "u14-02-open", "u14-02-open-closeup",
            "u14-03-opened-closeup", "u14-04-one-hand", "u14-04-one-hand-closeup", "u14-05-hint-closeup", "u14-06-end", "u14-06-end-closeup",
        };

        /// <summary>`-lotseTourOnly` on the command line: only the start panel and the tour's scenes (U14).</summary>
        static bool TourOnly => Environment.GetCommandLineArgs().Contains("-lotseTourOnly");

        static RenderScenes _current;

        readonly string _outDir;
        readonly List<string> _exceptions = new List<string>();
        RenderCamera _cam;
        RenderFakes.Hands _hands;
        RenderFakes.Keyboard _keyboard;
        RenderFakes.Qr _qr;
        GameObject _app;
        AppRoot _root;
        Action _restoreUrp;

        /// <summary>0: every expected PNG was written and none is black. 1: a failure. 3: URP drew nothing.</summary>
        public static int ExitCode => _current != null ? _current._exitCode : 1;
        int _exitCode = 1;

        public RenderScenes(string outDir)
        {
            _outDir = outDir;
            _current = this;
        }

        GameObject Window => _root.Workspace.Window;

        public IEnumerator Run()
        {
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = FrameSeconds;
            if (!Environment.GetCommandLineArgs().Contains("-lotseKeepTileOnly")) _restoreUrp = OffscreenUrp.TileOnlyOff();
            Debug.Log("[Renders] rendering with " + OffscreenUrp.Describe());
            _cam = new RenderCamera(_outDir, Eye);
            _cam.LookAt(DeskSpot + Vector3.up * 0.2f);
            StartApp();
            yield return Until(() => _root.Current == AppRoot.Phase.Start, 300, "the start panel");
            yield return Frames(5);
            if (!_root.OnDesk) throw new InvalidOperationException("the app is not on the desk anchor");
            Debug.Log($"[Renders] anchor on the desk at {_root.Anchor.position}, head at {Eye}, workspace: {_root.Dock}");

            var start = GameObject.Find("StartPanel");
            _cam.LookAt(RenderCamera.BoundsOf(start).center);
            if (!UrpDraws()) yield break;
            Shot("00-start", start);
            if (TourOnly)
            {
                yield return TourScenes();
                Report();
                yield break;
            }

            // U12: "Connect to my PC" opens the viewfinder (a scripted tracker; no connection: the socket never opens)
            Button(Texts.BridgeButton).Click();
            yield return Until(() => _root.Pairing != null && _root.Pairing.Finder != null, 20, "the viewfinder");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Frames(3);
            var finder = _root.Pairing.Finder.gameObject;
            Shot("u13-06-pair", finder);
            // a code on the laptop's screen, 0.6 m away, below the middle of the view: the brackets go to it
            _qr.Place = _cam.Head.position + _cam.Head.forward * 0.6f - _cam.Head.up * 0.03f + _cam.Head.right * 0.04f;
            _qr.Size = 0.04f;
            _qr.Payloads.Add(System.Text.Encoding.ASCII.GetBytes("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216"));
            yield return Frames(3);
            yield return new WaitForSecondsRealtime(0.9f); // "Pair" counts now
            yield return Frames(3);
            Shot("u13-07-found", finder);
            _root.Pairing.ScanBand.Buttons[0].Click(); // "Pair"
            yield return Frames(3);
            _qr.Payloads.Clear();
            _cam.CloseUp("35-pair-connecting-closeup", finder);
            // nobody answers: the one attempt ends
            yield return Until(() => _root.Pairing.View == PairView.Idle, 200, "the attempt to end");
            yield return Frames(3);
            _cam.CloseUp("36-pair-failed-closeup", finder);
            // the window behind "Type the code instead", with three digits typed
            _root.Pairing.TypeButton.Click();
            yield return Frames(3);
            foreach (var key in new[] { "4", "8", "1" })
            {
                Button(key, _root.Pairing.transform).Click();
                yield return Frames(1);
            }
            yield return Frames(3);
            _cam.LookAt(RenderCamera.BoundsOf(_root.Pairing.gameObject).center);
            Shot("06-pairing", _root.Pairing.gameObject);
            // without the permission: the window with one line
            Button(Texts.Back, _root.Pairing.transform).Click();
            yield return Until(() => _root.Current == AppRoot.Phase.Start, 20, "back to the start panel");
            yield return Frames(3);
            _qr.OnBegin = QrSourceState.Denied;
            Button(Texts.BridgeButton).Click();
            yield return Until(() => _root.Pairing != null && _root.Pairing.View == PairView.Manual, 20, "the window");
            yield return Frames(3);
            _cam.LookAt(RenderCamera.BoundsOf(_root.Pairing.gameObject).center);
            _cam.CloseUp("34-pair-denied-closeup", _root.Pairing.gameObject);
            _qr.OnBegin = QrSourceState.Scanning;
            Button(Texts.Back, _root.Pairing.transform).Click();
            yield return Until(() => _root.Current == AppRoot.Phase.Start, 20, "back to the start panel");
            yield return Frames(3);

            // the demo: four agents at work (5.4 s to 7 s); the first is selected once
            Button(Texts.StartButton).Click();
            var s = _root.Session;
            yield return Until(() => s.Store.Agents.Count == 4, 100, "four agents");
            yield return Until(() => s.Store.Agents.All(a => a.Status == "working"), 100, "all four working");
            yield return Frames(2);
            LookAtWorkspace();
            // 24. U9: the display offer is a band inside the window, under the header; "Later" answers it
            yield return Until(() => _root.ScreenOffer != null, 30, "the display offer in the window's band");
            yield return Until(() => s.Store.Agents.All(x => x.Plan != null), 300, "every agent's plan");
            yield return Frames(2);
            _cam.CloseUp("24-question-band-closeup", Window);
            _root.ScreenOffer.Buttons[1].Click();
            yield return Until(() => _root.ScreenOffer == null, 10, "the offer answered");
            yield return Frames(2);
            LookAtWorkspace();
            // U6: the window opens on Übersicht; the conversation scenes press "Unterhaltung"
            OpenARow();
            yield return Frames(2);

            // 3. a working agent's conversation: Agent 2, its feed of 8 entries
            _root.Desktop.OpenRow("demo-a2");
            yield return Until(() => _root.Workspace.Conversation.Feed.Ids.Count >= 3, 30, "Agent 2's feed");
            yield return Frames(6);
            Shot("u13-04-work", Window);

            // 14. the rail next to the monitor ("Einklappen"), then open again with a click on it
            _root.Workspace.Header.CollapseButton.Click();
            yield return Until(() => _root.Workspace.Folded, 10, "the rail");
            yield return Frames(3);
            _cam.LookAt(_root.Workspace.RailCentre + _root.Workspace.transform.right * 0.25f);
            _cam.Render("14-rail");
            _cam.CloseUp("14-rail-closeup", _root.Workspace.Rail.gameObject);
            Probe("the rail");
            _root.Workspace.Rail.Area.Click();
            yield return Until(() => _root.Workspace.Open, 10, "the window open again");
            yield return Frames(2);
            LookAtWorkspace();

            // 8. "Tastatur": the desk keyboard on the desk, the workspace beside it
            _root.Desktop.OpenRow(_root.Session.Store.Agents.First(x => x.Source == "claude" && !_root.Session.HasRequest(x.Sid)).Sid);
            yield return Until(() => _root.Desktop.OpenSettled && _root.Workspace.Conversation.Input != null, 30, "a Claude Code row open");
            yield return Frames(4);
            _root.Workspace.Conversation.Input.KeyboardButton.Click();
            yield return Until(() => _root.Desk.Shown, 20, "the desk keyboard");
            yield return Frames(2);
            foreach (var label in new[] { "H", "E", "L", "L", "O" })
            {
                _root.Desk.Key(label)?.Click();
                yield return Frames(1);
            }
            yield return Frames(2);
            var board = _root.Desk.gameObject;
            // the seated eye between the keyboard on the desk and the window's top edge, so both are in view
            var w = _root.Workspace.transform;
            _cam.LookAt(Vector3.Lerp(RenderCamera.BoundsOf(board).center, w.position + w.up * (_root.Dock.Height * 0.5f), 0.38f));
            _cam.Render("08-desk-keyboard");
            _cam.CloseUp("08-desk-keyboard-closeup", board, 0.45f);
            _root.Workspace.Conversation.Input.KeyboardButton.Click();
            yield return Frames(2);

            // 7. "Einstellungen" from the wrist menu, in front of the workspace
            yield return OpenSettings();
            _cam.LookAt(RenderCamera.BoundsOf(_root.Settings.gameObject).center);
            Shot("07-settings", _root.Settings.gameObject);
            _root.Settings.CloseButton.Click();
            yield return Frames(3);
            LookAtWorkspace();

            yield return HaltScenes();
            yield return HeaderScales();

            // U13 01. the overview when the first request comes: nothing opens, its row is raised with a stop bar
            _root.Desktop.CloseRow();
            yield return Until(() => s.Store.Pending.Count > 0, 400, "the first request");
            yield return Frames(8);
            if (s.Card != null) throw new InvalidOperationException("a block opened by itself");
            LookAtWorkspace();
            Shot("u13-01-over", Window);
            Probe("the overview, one agent holding");
            yield return OverviewScenes();

            // 2. the raised row pressed: Agent 1's harmless request on the open row, the short stop bar half dark
            _root.Desktop.CloseRow();
            yield return Frames(2);
            _root.Workspace.Rows.RowOf(s.Store.Pending[0].Sid).Click();
            yield return Until(() => s.Card != null && _root.Desktop.OpenSettled, 30, "Agent 1's block");
            yield return Frames(4);
            LookAtWorkspace();
            Time.captureDeltaTime = HoldFrameSeconds;
            yield return Pinch(s.Card.NeedsTwoHand);
            yield return Until(() => s.HoldProgress >= 0.5f, 400, "half the hold");
            Shot("02-workspace-harmless", Window);
            var first = s.Card.Id;
            yield return Until(() => s.Card == null || s.Card.Id != first, 400, "the first request approved");
            Time.captureDeltaTime = FrameSeconds;
            _hands.Right = Hand();
            _hands.Left = Hand();

            // the rest of the demo: each request's agent selected from its row, the harmless ones approved, the
            // dangerous ones denied with "Ablehnen"; 1. the first dangerous one (with its agents) rendered first
            var dangerousShot = false;
            string seen = null;
            var cardFrames = 0;
            var guard = 0;
            while (_root.Current == AppRoot.Phase.Playing)
            {
                if (guard++ > 20000) throw new TimeoutException("the demo did not end");
                _hands.Left = Hand();
                _hands.Right = Hand();
                var card = s.Card;
                if (card == null)
                {
                    if (s.Store.Pending.Count > 0) _root.Desktop.OpenRow(s.Store.Pending[0].Sid);
                    yield return null;
                    continue;
                }
                if (card.Id != seen)
                {
                    seen = card.Id;
                    cardFrames = 0;
                }
                LookAtWorkspace();
                var at = GatePoint();
                _hands.Right = Hand(pinchAt: at);
                _hands.Left = Hand(pinchAt: at + Vector3.left * 0.2f);
                if (++cardFrames > 4 && s.Mode == CardMode.Hold)
                {
                    if (card.Risk.IsDestructive)
                    {
                        if (!dangerousShot)
                        {
                            dangerousShot = true;
                            yield return Until(() => _root.Desktop.OpenSettled, 30, "the dangerous request's row open");
                            yield return Frames(25); // the facts have come
                            Shot("u13-02-open", Window);
                            _cam.Plain = true;
                            _cam.CloseUp("31-plain-dangerous", Window);
                            _cam.Plain = false;
                            Probe("4 agents, a dangerous block");
                            // U13 03. both hands holding: the bar half dark, from both ends
                            Time.captureDeltaTime = HoldFrameSeconds;
                            _hands.Right = Hand(pinch: 1f, pinchAt: at);
                            _hands.Left = Hand(pinch: 1f, pinchAt: at + Vector3.left * 0.2f);
                            yield return Until(() => s.HoldProgress >= 0.55f, 400, "half the two-hand hold");
                            Shot("u13-03-hold", Window);
                            Time.captureDeltaTime = FrameSeconds;
                            _hands.Left = Hand();
                            _hands.Right = Hand();
                            yield return Frames(4);
                            if (s.Card == null || s.Card.Id != card.Id) continue;
                        }
                        _root.Workspace.Conversation.Block.DenyButton.Click();
                        yield return Frames(1);
                        continue;
                    }
                    _hands.Right = Hand(pinch: 1f, pinchAt: at);
                    if (card.NeedsTwoHand) _hands.Left = Hand(pinch: 1f, pinchAt: at + Vector3.left * 0.2f);
                }
                yield return null;
            }
            if (!dangerousShot) throw new InvalidOperationException("no dangerous request in the demo");
            _hands.Left = Hand();
            _hands.Right = Hand();
            yield return Frames(3);

            // 5. the six orb states in a row, close up; 9. a live-like workspace with six agents for the draw-call budget
            yield return SixAgents();
            yield return GuidedTestScenes();
            yield return DockScenes();

            Report();
        }

        void LookAtWorkspace() => _cam.LookAt(RenderCamera.BoundsOf(Window).center);

        /// <summary>23. the header at scale 1.0 and 2.0 of the U5b size (U8: nothing in it may be clipped), then back.</summary>
        IEnumerator HeaderScales()
        {
            var before = _root.Dock;
            foreach (var scale in new[] { 1f, 2f })
            {
                var d = _root.Dock;
                // U10: the smallest and the largest apparent size (the render names keep "scale 1" and "scale 2")
                var head = Ui.ToVec(_root.Head.position);
                var factor = (scale < 1.5f ? WindowPlacement.MinApparent : WindowPlacement.MaxApparent) / WindowPlacement.Apparent(d, head);
                var placed = typeof(AppRoot).GetMethod("PlaceWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                placed.Invoke(_root, new object[] { WindowPlacement.Resize(d, head, factor) });
                yield return Frames(3);
                _cam.CloseUp("23-header-scale-" + (int)scale + "-closeup", _root.Workspace.Header.gameObject);
            }
            var restore = typeof(AppRoot).GetMethod("PlaceWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            restore.Invoke(_root, new object[] { before });
            yield return Frames(2);
            LookAtWorkspace();
        }

        /// <summary>An open hand in the stop pose beside the head: palm away, fingers straight (U7).</summary>
        HandFrame StopHand(float x)
        {
            var head = _root.Head;
            var at = head.position + head.forward * 0.4f + head.right * x - Vector3.up * 0.1f;
            var f = Hand(pinchAt: at, palm: at, palmNormal: head.forward);
            f.FingersValid = true;
            f.IndexStraight = f.MiddleStraight = f.RingStraight = f.LittleStraight = 0.98f;
            return f;
        }

        /// <summary>20. the amber bar half filled during the stop gesture; 21. the halted workspace (the demo halts itself), then Continue.</summary>
        IEnumerator HaltScenes()
        {
            // 25. U9: "Halt all agents?" from the menu is asked in the window's band; "Cancel" here
            _root.Workspace.Header.MenuButton.Click();
            yield return Until(() => _root.Workspace.Menu.IsOpen, 20, "the header's menu");
            _root.Workspace.Menu.HaltButton.Click();
            yield return Until(() => _root.Workspace.Menu.AskingHalt, 10, "the halt question");
            yield return Frames(2);
            LookAtWorkspace();
            _cam.CloseUp("25-halt-question-closeup", Window);
            _root.Workspace.Menu.HaltCancelButton.Click();
            yield return Frames(2);
            LookAtWorkspace();
            _hands.Left = StopHand(-0.2f);
            _hands.Right = StopHand(0.2f);
            yield return Until(() => _root.Stop.Progress >= 0.5f, 60, "the stop bar half filled");
            _cam.CloseUp("20-stop-bar-closeup", Window);
            yield return Until(() => _root.Session.Halted, 30, "the demo halted");
            _hands.Left = Hand();
            _hands.Right = Hand();
            yield return Frames(15); // "Halted for 1 s"
            LookAtWorkspace();
            Shot("u13-05-halt", Window);
            Probe("the halted workspace");
            _root.Workspace.Halt.ContinueButton.Click();
            yield return Frames(1);
            _root.Workspace.Halt.YesButton.Click();
            yield return Until(() => !_root.Session.Halted, 10, "the demo released");
            yield return Frames(2);
        }

        /// <summary>The render probe: the workspace's renderers and shader passes now (each one a draw call), and the frame's real count when the profiler has it.</summary>
        /// <summary>Opens a row: the agent whose request waits, else the selected one, else the first.</summary>
        void OpenARow()
        {
            var s = _root.Session;
            if (s == null || s.Store.Agents.Count == 0) return;
            var sid = s.Store.Pending.Count > 0 ? s.Store.Pending[0].Sid : _root.SelectedAgent ?? s.Store.Agents[0].Sid;
            _root.Desktop.OpenRow(sid);
        }

        void Probe(string when)
        {
            Debug.Log($"[Renders] render probe ({when}): {RenderProbe.Renderers(Window)} renderers, {RenderProbe.Passes(Window)} passes: " +
                      RenderProbe.Describe(Window));
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count"))
            {
                _cam.LitFraction(0.5f);
                var with = recorder.LastValue;
                Window.SetActive(false);
                _cam.LitFraction(0.5f);
                var without = recorder.LastValue;
                Window.SetActive(true);
                Debug.Log($"[Renders] profiler draw calls ({when}): {(recorder.Valid ? (with - without).ToString() : "not available")} " +
                          $"(with the window {with}, without {without})");
            }
        }

        // ---- U5b: docking to a display ----

        /// <summary>
        /// A stand-in display in the scene (a dark screen with a hairline bezel), since the backdrop photo's monitor cannot be
        /// lined up with the room: centre, its right and up (a laptop leans back), w x h metres. Returns its corners TL, TR, BR.
        /// </summary>
        static (Vector3 tl, Vector3 tr, Vector3 br) StandIn(string name, Vector3 centre, Vector3 up, float w, float h)
        {
            var forward = Vector3.Cross(Vector3.right, up).normalized;
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(forward, up));
            Ui.Panel(go.transform, "Bezel", new Vector3(0f, 0f, 0.004f), new Vector2(w + 0.02f, h + 0.02f), new Color(0.05f, 0.05f, 0.06f, 1f), blocksRay: false);
            Ui.Panel(go.transform, "Screen", new Vector3(0f, 0f, 0.003f), new Vector2(w, h), new Color(0.13f, 0.17f, 0.24f, 1f), blocksRay: false);
            var right = go.transform.right;
            return (centre - right * (w / 2) + up * (h / 2), centre + right * (w / 2) + up * (h / 2), centre + right * (w / 2) - up * (h / 2));
        }

        /// <summary>"Set up display", as the wrist menu's button calls it (the menu stays hidden while a block waits).</summary>
        void BeginSetup() => typeof(AppRoot).GetMethod("BeginScreenSetup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Invoke(_root, null);

        /// <summary>The right fingertip rests on a corner for a second (the dwell), then moves away.</summary>
        IEnumerator TapCorner(Vector3 at)
        {
            for (var i = 0; i < 10; i++)
            {
                _hands.Right = Hand(tip: at);
                yield return null;
            }
            _hands.Right = Hand(tip: at - (at - Eye).normalized * 0.06f);
            yield return Frames(2);
        }

        /// <summary>16 to 19: the three-tap setup, the four slots, the window docked right of a monitor and above a laptop.</summary>
        IEnumerator DockScenes()
        {
            // after the six-agent scene the demo runs again (its "Nochmal"); the window shows its conversation
            if (_root.Desktop.OpenSid == null) OpenARow();
            yield return Frames(2);

            var monitor = StandIn("StandInMonitor", new Vector3(-0.05f, 1.06f, 0.78f), Vector3.up, 0.6f, 0.34f);
            BeginSetup();
            yield return Until(() => _root.Setup.Active, 10, "the display setup");
            yield return TapCorner(monitor.tl);
            yield return TapCorner(monitor.tr);
            for (var i = 0; i < 5; i++)
            {
                _hands.Right = Hand(tip: monitor.br);
                yield return null;
            }
            _cam.LookAt((monitor.tl + monitor.br) * 0.5f + Vector3.down * 0.08f);
            _cam.Render("16-screen-setup");
            yield return Frames(6);
            _hands.Right = Hand();
            // U10: docked at once after the third corner (no chooser); a band says where and offers the other sides
            yield return Until(() => _root.OnScreen && _root.DockBand != null, 20, "docked, the band up");
            yield return Frames(3);
            _cam.LookAt(((monitor.tl + monitor.br) * 0.5f + _root.Workspace.transform.position) * 0.5f);
            Shot("17-docked-band", Window);
            _root.DockBand.Buttons[1].Click(); // "Done"
            yield return Frames(3);
            _cam.LookAt(((monitor.tl + monitor.br) * 0.5f + _root.Workspace.transform.position) * 0.5f);
            Shot("18-docked-right", Window);
            Probe("docked right of a monitor");
            Debug.Log($"[Renders] docked right of the monitor: {_root.Dock}");

            // a laptop leaning back 25 degrees, the window above it ("Other side" until it stands above)
            GameObject.Find("StandInMonitor").SetActive(false);
            var a = 25f * Mathf.Deg2Rad;
            var up = new Vector3(0f, Mathf.Cos(a), Mathf.Sin(a));
            var laptop = StandIn("StandInLaptop", new Vector3(-0.05f, 0.88f, 0.5f), up, 0.31f, 0.2f);
            BeginSetup();
            yield return Until(() => _root.Setup.Active, 10, "the display setup again");
            yield return TapCorner(laptop.tl);
            yield return TapCorner(laptop.tr);
            yield return TapCorner(laptop.br);
            _hands.Right = Hand();
            yield return Until(() => !_root.Setup.Active && _root.DockBand != null, 20, "docked at the laptop");
            for (var i = 0; i < 4 && _root.ScreenSide != DockSide.Above; i++)
            {
                _root.DockBand.Buttons[0].Click(); // "Other side"
                yield return Frames(2);
            }
            if (_root.ScreenSide != DockSide.Above) throw new InvalidOperationException("the window did not dock above the laptop");
            _root.DockBand.Buttons[1].Click();
            yield return Frames(3);
            _cam.LookAt(((laptop.tl + laptop.br) * 0.5f + _root.Workspace.transform.position) * 0.5f);
            _cam.Render("19-docked-above-laptop");
            Debug.Log($"[Renders] docked above the laptop: {_root.Dock}");
        }

        /// <summary>
        /// 27 to 29. U9: "Guided test" from the menu: the band along the bottom edge at its first step, at the two-hand step
        /// with the test's own dangerous request, and the summary. "Done" leads to the start panel; the demo starts again.
        /// </summary>
        IEnumerator GuidedTestScenes()
        {
            _root.Workspace.Header.MenuButton.Click();
            yield return Until(() => _root.Workspace.Menu.IsOpen, 20, "the header's menu");
            _root.Workspace.Menu.TestButton.Click();
            yield return Until(() => _root.Test.Active && _root.Workspace.TestBand.IsShown, 20, "the guided test");
            var s = _root.Session;
            yield return Until(() => s.Store.Agents.Count == 4 && s.Store.Agents.All(a => a.Plan != null), 300, "the test's four agents and their plans");
            yield return Frames(4);
            LookAtWorkspace();
            _cam.CloseUp("27-test-band-closeup", Window);
            for (var i = 0; i < 4; i++)
            {
                _root.Workspace.TestBand.Buttons[0].Click();
                yield return Frames(3);
            }
            yield return Until(() => s.Store.Pending.Count > 0 && s.Store.Pending[0].NeedsTwoHand, 30, "the test's dangerous request");
            _root.Desktop.OpenAgent(s.Store.Pending[0].Sid);
            yield return Until(() => s.Card != null, 10, "its block");
            yield return Frames(4);
            LookAtWorkspace();
            _cam.CloseUp("28-test-two-hand-closeup", Window);
            Probe("the guided test, a dangerous block, the band");
            _root.Workspace.TestBand.Buttons[1].Click();
            yield return Frames(2);
            _root.Workspace.TestBand.Buttons[2].Click();
            yield return Frames(2);
            while (!_root.Test.Finished && _root.Test.Index < GuidedTest.Steps.Length)
            {
                _root.Workspace.TestBand.Buttons[0].Click();
                yield return Frames(2);
            }
            yield return Until(() => _root.Test.Finished, 10, "the summary");
            yield return Frames(2);
            LookAtWorkspace();
            _cam.CloseUp("29-test-summary-closeup", Window);
            _root.Workspace.TestBand.Buttons[0].Click();
            yield return Until(() => _root.Current == AppRoot.Phase.Start, 20, "the start panel after the test");
            yield return Frames(3);
            yield return TourScenes();
            Button(Texts.StartButton).Click();
            yield return Until(() => _root.Current == AppRoot.Phase.Playing && _root.Session.Store.Agents.Count == 4, 100, "the demo again");
            yield return Frames(3);
        }

        /// <summary>
        /// U14: the start panel with "Take the tour", then the tour: its first step, the waiting agent's raised line, the
        /// word after the row was opened, the one-hand request with the band under it, the hint, and the end. The row is
        /// opened here as the user would; nothing is approved. Ends on the start panel.
        /// </summary>
        IEnumerator TourScenes()
        {
            var start = GameObject.Find("StartPanel");
            _cam.LookAt(RenderCamera.BoundsOf(start).center);
            yield return Frames(3);
            Shot("u14-00-start", start);
            Button(Tour.StartButton).Click();
            yield return Until(() => _root.Tour.Active && _root.Session != null, 20, "the tour");
            var s = _root.Session;
            yield return Until(() => s.Store.Agents.Count == 4 && s.Store.Agents.All(a => a.Plan != null), 300, "the tour's four agents and their plans");
            if (_root.Tour.Current.Id != "lines") throw new InvalidOperationException("the tour's first step went by before its agents were there");
            yield return Frames(2);
            LookAtWorkspace();
            Shot("u14-01-lines", Window);
            yield return Until(() => _root.Tour.Current.Id == "open" && s.Store.Pending.Count > 0, 200, "the tour's waiting agent");
            yield return Frames(8);
            LookAtWorkspace();
            Shot("u14-02-open", Window);
            _root.Desktop.OpenAgent(s.Store.Pending[0].Sid);
            yield return Until(() => _root.Tour.Phase == TourPhase.Confirm, 40, "\"Opened.\"");
            yield return Frames(2);
            LookAtWorkspace();
            _cam.CloseUp("u14-03-opened-closeup", Window);
            yield return Until(() => _root.Tour.Current.Id == "one-hand" && _root.Tour.Phase == TourPhase.Instruction && _root.Desktop.OpenSettled, 60, "the one-hand step");
            yield return Frames(4);
            LookAtWorkspace();
            Shot("u14-04-one-hand", Window);
            Probe("the tour, a harmless request, the band");
            yield return Until(() => _root.Tour.HintShown, (int)(Tour.HintMs / 1000.0 / FrameSeconds) + 20, "the hint");
            yield return Frames(3);
            _cam.CloseUp("u14-05-hint-closeup", Window);
            for (var i = 0; i < 12 && _root.Tour.Phase != TourPhase.End; i++)
            {
                _root.Tour.SkipStep();
                yield return Frames(2);
            }
            yield return Until(() => _root.Tour.Phase == TourPhase.End, 10, "the tour's end");
            yield return Frames(3);
            LookAtWorkspace();
            Shot("u14-06-end", Window);
            Button(Tour.Done).Click();
            yield return Until(() => _root.Current == AppRoot.Phase.Start, 20, "the start panel after the tour");
            yield return Frames(3);
        }

        /// <summary>The state as the user reads it (the row's words).</summary>
        /// <summary>9. "Nochmal", then six agents in the list (two more added by hand), Agent 1 selected with a request.</summary>
        IEnumerator SixAgents()
        {
            Button(Texts.Again).Click();
            yield return Until(() => _root.Current == AppRoot.Phase.Playing, 20, "the demo again");
            var s = _root.Session;
            yield return Until(() => s.Store.Agents.Count == 4, 100, "four agents");
            s.Store.Apply(new AgentMessage { Agent = new AgentState { Sid = "x5", Name = "Agent 5", Source = "codex", Title = "Tidy up the header", Project = "shop-web", Status = "done", LastAction = "Done" } });
            s.Store.Apply(new AgentMessage { Agent = new AgentState { Sid = "x6", Name = "Agent 6", Source = "gemini", Title = "Fix the login", Project = "auth", Status = "failed", LastAction = "Failed" } });
            yield return Until(() => s.Store.Pending.Count > 0, 400, "the first request");
            _root.Desktop.OpenAgent(s.Store.Pending[0].Sid); // its column (a new run opens on Übersicht)
            yield return Until(() => s.Card != null, 10, "its block");
            yield return Frames(6);
            LookAtWorkspace();
            _cam.CloseUp("09-six-agents-closeup", Window);
            Probe("6 agents, a block, the feed");

            // 11. the overview with six agents: page 1
            _root.Desktop.CloseRow();
            yield return Frames(4);
            _cam.CloseUp("11-overview-six-closeup", Window);
            Probe("the overview, 6 agents, page 1");
        }

        /// <summary>
        /// 10. Übersicht with four agents: Agent 1 waits (its first request), one done, one without a plan (its recent
        /// activity); 15. the rail with Agent 1's red orb. The done state and the missing plan are put into the store for this
        /// one shot (the pack's next messages bring the real ones back). Then back to Unterhaltung, the notice standing.
        /// </summary>
        IEnumerator OverviewScenes()
        {
            var s = _root.Session;
            var asking = s.Store.Pending[0].Sid;
            var others = s.Store.Agents.Where(a => a.Sid != asking && a.Sid != "demo-a2").Select(a => a.Sid).ToList();
            var done = s.Store.Agents.First(a => a.Sid == others[0]);
            done.Status = "done";
            done.LastAction = "Done";
            if (done.Plan != null)
                foreach (var step in done.Plan.Steps) step.Status = PlanStatus.Done;
            s.Store.Apply(new AgentMessage { Agent = done.Clone() });
            _root.Desktop.CloseRow();
            yield return Frames(3);
            // 10. every agent has a plan, the four vendors side by side
            Shot("10-overview", Window);
            _cam.Plain = true;
            _cam.CloseUp("30-plain-overview", Window);
            _cam.Plain = false;
            Probe("the overview, 4 agents");

            // 26. the one deliberate example without a plan: its latest activity under "Recent activity". The plan is
            // taken from the store while the other tab shows (the overview is drawn afresh when it comes back), and put back.
            OpenARow();
            yield return Frames(2);
            var noPlan = s.Store.Agents.First(a => a.Sid == others[1]);
            var plan = noPlan.Plan;
            noPlan.Plan = null;
            _root.Desktop.Trail.Note(noPlan.Sid, "Reads src/lib/search.ts");
            _root.Desktop.Trail.Note(noPlan.Sid, "Edits src/components/Filter.tsx");
            _root.Desktop.Trail.Note(noPlan.Sid, "Runs: npm test");
            s.Store.Apply(new AgentMessage { Agent = done.Clone() });
            _root.Desktop.CloseRow();
            yield return Frames(3);
            if (_root.Workspace.Rows.StatusShown(noPlan.Sid) != OverviewText.NoPlan) throw new InvalidOperationException("the row without a plan still shows one");
            _cam.CloseUp("26-overview-no-plan-closeup", Window);
            noPlan.Plan = plan;
            s.Store.Apply(new AgentMessage { Agent = done.Clone() });
            yield return Frames(3);

            _root.Workspace.Header.CollapseButton.Click();
            yield return Until(() => _root.Workspace.Folded, 10, "the rail");
            yield return Frames(3);
            _cam.LookAt(_root.Workspace.RailCentre + _root.Workspace.transform.right * 0.25f);
            _cam.Render("u13-08-rail");
            _cam.CloseUp("u13-08-rail-closeup", _root.Workspace.Rail.gameObject);
            _root.Workspace.Rail.Area.Click();
            yield return Until(() => _root.Workspace.Open, 10, "the window open again");
            OpenARow();
            yield return Frames(2);
            LookAtWorkspace();
        }

        void StartApp()
        {
            _hands = new RenderFakes.Hands { Left = Hand(), Right = Hand() };
            _keyboard = new RenderFakes.Keyboard();
            var prefs = new RenderFakes.MemoryPrefs();
            prefs.Set(HostBook.HostsKey, "192.168.1.20"); // a bridge used before, without a stored session
            prefs.Set(LookSettings.AutoCollapseKey, "off"); // the scenes look away from the window on purpose (the keyboard, the settings)
            var facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(DeskSpot - Eye, Vector3.up), Vector3.up);
            _app = new GameObject("LotseApp");
            _app.SetActive(false);
            _root = _app.AddComponent<AppRoot>();
            _root.Hands = _hands;
            _root.Head = _cam.Head;
            _root.HeadTracked = () => true;
            _root.InputFocus = () => true;
            _root.Prefs = prefs;
            _root.Raycaster = new RenderFakes.WallRoom(Eye, DeskSpot - Eye, WallDistance);
            _root.DeskAnchors = new RenderFakes.SavedDesk(DeskSpot, facing);
            _root.Keyboard = _keyboard;
            _root.SocketFactory = _ => new RenderFakes.DeadSocket();
            _qr = new RenderFakes.Qr();
            _root.QrSource = _qr;
            _app.SetActive(true);
        }

        /// <summary>URP draws into an offscreen texture in this Editor: the start panel without the backdrop lights pixels.</summary>
        bool UrpDraws()
        {
            _cam.BackdropShown = false;
            var lit = _cam.LitFraction(0.15f);
            _cam.BackdropShown = true;
            Debug.Log($"[Renders] URP check: {lit:P2} of the pixels lit by the start panel alone");
            if (lit > 0.002f) return true;
            Debug.LogError("[Renders] URP drew nothing into the offscreen texture (" + OffscreenUrp.Describe() + ")");
            _exitCode = 3;
            return false;
        }

        /// <summary>The scene render, and the close-up of `panel` 0.6 m straight on when given.</summary>
        void Shot(string name, GameObject panel = null)
        {
            _cam.Render(name);
            if (panel != null) _cam.CloseUp(name + "-closeup", panel);
        }

        void Report()
        {
            var written = _cam.Shots.Select(x => System.IO.Path.GetFileNameWithoutExtension(x.Path)).ToList();
            var missing = Expected.Where(n => !TourOnly || n.StartsWith("u14-") || n.StartsWith("00-")).Except(written).ToList();
            var black = _cam.Shots.Where(x => !x.LooksRendered).ToList();
            foreach (var shot in _cam.Shots) Debug.Log("[Renders] " + shot + (shot.LooksRendered ? "" : "  <- BLACK OR FLAT"));
            if (_exceptions.Count > 0)
                Debug.LogWarning($"[Renders] {_exceptions.Count} exception(s) were logged while rendering:\n" + string.Join("\n", _exceptions.Take(5)));
            if (missing.Count > 0) Debug.LogError("[Renders] missing: " + string.Join(", ", missing));
            if (black.Count > 0) Debug.LogError("[Renders] black or flat: " + string.Join(", ", black));
            _exitCode = missing.Count == 0 && black.Count == 0 && _exceptions.Count == 0 ? 0 : 1;
            Debug.Log($"[Renders] {_cam.Shots.Count} PNGs in {_outDir}, exit code {_exitCode}");
        }

        /// <summary>Undoes everything the run set up (called once, also after a failure).</summary>
        public static void CleanUp()
        {
            var r = _current;
            if (r == null) return;
            Application.logMessageReceived -= r.OnLog;
            Time.captureDeltaTime = 0f;
            if (r._app != null) Object.DestroyImmediate(r._app);
            if (r._cam != null) Object.DestroyImmediate(r._cam.Camera.gameObject);
            r._restoreUrp?.Invoke();
            r._restoreUrp = null;
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Exception) _exceptions.Add(message + "\n" + stack);
        }

        /// <summary>The wrist menu up for a moment and "Einstellungen" pressed.</summary>
        IEnumerator OpenSettings()
        {
            // U8: the header's Menu (the wrist menu is off)
            _root.Workspace.Header.MenuButton.Click();
            yield return Until(() => _root.Workspace.Menu.IsOpen, 20, "the header's menu");
            yield return Frames(2);
            _cam.CloseUp("22-menu-closeup", Window);
            _root.Workspace.Menu.SettingsButton.Click();
            yield return Until(() => _root.Settings.IsOpen, 20, "the settings");
            yield return Frames(3);
        }

        // ---- input ----

        /// <summary>Where a pinch counts for the hold gate: 0.35 m from the eye toward the block.</summary>
        Vector3 GatePoint() => Eye + (_root.Desktop.Block.Center - Eye).normalized * 0.35f;

        static HandFrame Hand(float pinch = 0f, Vector3? pinchAt = null, Vector3? rayAt = null, bool palmForward = false,
            Vector3? palm = null, Vector3? palmNormal = null, Vector3? tip = null) => new HandFrame
        {
            Sample = new HandSample(true, true, pinch, palmForward),
            TipValid = tip.HasValue,
            Tip = tip ?? Vector3.zero,
            PinchValid = true,
            PinchPoint = pinchAt ?? Lap,
            RayValid = rayAt.HasValue,
            RayOrigin = RayFrom,
            RayDirection = rayAt.HasValue ? (rayAt.Value - RayFrom).normalized : Vector3.zero,
            PalmValid = true,
            PalmCenter = palm ?? Lap,
            PalmNormal = palmNormal ?? Vector3.down,
        };

        /// <summary>A fresh pinch (released first) held at the gate point.</summary>
        IEnumerator Pinch(bool twoHands)
        {
            _hands.Right = Hand(pinchAt: GatePoint());
            _hands.Left = Hand(pinchAt: GatePoint() + Vector3.left * 0.2f);
            yield return Frames(1);
            _hands.Right = Hand(1f, GatePoint());
            if (twoHands) _hands.Left = Hand(1f, GatePoint() + Vector3.left * 0.2f);
        }

        static PokeButton Button(string label, Transform under = null)
        {
            var b = PokeButton.All.FirstOrDefault(x => x.Label == label && (under == null || x.transform.IsChildOf(under)));
            if (b == null) throw new InvalidOperationException($"no button \"{label}\"");
            return b;
        }

        static IEnumerator Frames(int n)
        {
            var until = Time.frameCount + n;
            while (Time.frameCount < until) yield return null;
        }

        static IEnumerator Until(Func<bool> done, int maxFrames, string what)
        {
            for (var i = 0; !done(); i++)
            {
                if (i > maxFrames) throw new TimeoutException("waited in vain for " + what);
                yield return null;
            }
        }
    }
}
