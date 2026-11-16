using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Lotse.App;
using Lotse.Core;
using UnityEngine;

namespace Lotse.EditorTools
{
    /// <summary>
    /// U14 part 4: PNG frame sequences for the video, 1920 x 1080 at 30 fps, of the real app running its real
    /// animations in simulated time (fixed frames of 1/30 s). Written to quest/Captures/video-seq/&lt;name&gt;/0001.png …
    /// (git-ignored). The camera is fixed per sequence, straight in front of the window, which fills about 80 % of the
    /// frame's width. The room behind is the desk photo without its top fifth (a framed photo of a person stands
    /// there) and strongly blurred (so nothing in the room can be recognised, the laptop's screen included).
    /// The agents are the demo's; requests come from the guided test's local source; holds are real two-hand pinches of
    /// the scripted hands (no hands are drawn), so the bar shows the app's own hold progress.
    /// </summary>
    sealed partial class RenderScenes
    {
        public const int VideoWidth = 1920;
        public const int VideoHeight = 1080;
        public const int VideoFps = 30;
        /// <summary>The window's share of the frame's width.</summary>
        const float VideoFill = 0.8f;
        /// <summary>The share of the backdrop photo's height that is left out at its top.</summary>
        const float BackdropCropTop = 0.2f;

        readonly List<(string name, int frames)> _sequences = new List<(string, int)>();
        Vector3 _videoFrom;
        Quaternion _videoLook;

        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public IEnumerator RunVideo()
        {
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = FrameSeconds;
            if (!Environment.GetCommandLineArgs().Contains("-lotseKeepTileOnly")) _restoreUrp = OffscreenUrp.TileOnlyOff();
            _cam = new RenderCamera(_outDir, Eye);
            _cam.LookAt(DeskSpot + Vector3.up * 0.2f);
            StartApp();
            yield return Until(() => _root.Current == AppRoot.Phase.Start, 300, "the start panel");
            yield return Frames(5);
            _cam.LookAt(RenderCamera.BoundsOf(GameObject.Find("StartPanel")).center);
            if (!UrpDraws()) yield break;
            _cam.VideoMode(VideoWidth, VideoHeight, BackdropCropTop);

            // 1. overview: four agents, dots travelling, a request arrives and its row rises with the stop bar
            var src = default(GuidedSource);
            yield return VideoSession(s => src = s);
            var sids = _root.Session.Store.Agents.Select(a => a.Sid).ToArray();
            yield return Record("overview", 8f, i =>
            {
                if (i == 20) Advance(sids[1]);
                if (i == 60) Advance(sids[3]);
                if (i == 120 && !src.Offer(true)) throw new InvalidOperationException("the dangerous request was not offered");
                if (i == 200) Advance(sids[2]);
            });

            // 2. open-two-hand: the row opens, readable for 3 s, the bar goes dark from both ends, green, the row closes
            var asking = src.Open.Sid;
            var allowed = _root.Session.Decisions.Count;
            yield return Record("open-two-hand", 9f, i =>
            {
                if (i == 8) _root.Desktop.OpenAgent(asking);
                if (i == 122)
                {
                    _hands.Right = Hand(pinchAt: GatePoint());
                    _hands.Left = Hand(pinchAt: GatePoint() + Vector3.left * 0.2f);
                }
                if (i == 125)
                {
                    _hands.Right = Hand(1f, GatePoint());
                    _hands.Left = Hand(1f, GatePoint() + Vector3.left * 0.2f);
                }
                if (i > 125 && _root.Session.Decisions.Count > allowed)
                {
                    _hands.Right = Hand();
                    _hands.Left = Hand();
                }
                if (i == 232) Advance(asking);
            });
            if (_root.Session.Decisions.Count != allowed + 1 || _root.Session.Decisions.Last().Confirm != "two-hand")
                throw new InvalidOperationException("the two-hand hold of the sequence did not complete");

            // 3. deny: an open request, "Deny" pressed, the row closes
            yield return VideoSession(s => src = s);
            if (!src.Offer(false)) throw new InvalidOperationException("the harmless request was not offered");
            yield return Until(() => _root.Session.Store.Pending.Count > 0, 30, "the request to deny");
            _root.Desktop.OpenAgent(src.Open.Sid);
            yield return Until(() => _root.Session.Card != null && _root.Desktop.OpenSettled, 90, "its row open");
            yield return Frames(20);
            var denied = _root.Session.Decisions.Count;
            yield return Record("deny", 4f, i =>
            {
                var deny = _root.Workspace.Conversation.Block.DenyButton;
                if (i == 36 && deny != null) _hands.Right = Hand(rayAt: deny.transform.position);
                if (i == 54 && deny != null) _hands.Right = Hand(1f, rayAt: deny.transform.position);
                if (i == 62 && deny != null) _hands.Right = Hand(rayAt: deny.transform.position);
                if (i == 66) _hands.Right = Hand();
            });
            if (_root.Session.Decisions.Count != denied + 1 || _root.Session.Decisions.Last().Behavior != "deny")
                throw new InvalidOperationException("the deny of the sequence did not happen");

            // 4. halt: the stop gesture, "Everything is stopped." with the stop bars, then Continue
            yield return VideoSession(s => src = s);
            yield return Record("halt", 6f, i =>
            {
                if (i == 4)
                {
                    _hands.Left = StopHand(-0.2f);
                    _hands.Right = StopHand(0.2f);
                }
                if (i > 4 && i < 100 && _root.Session.Halted)
                {
                    _hands.Left = Hand();
                    _hands.Right = Hand();
                }
                if (i == 112) _root.Workspace.Halt.ContinueButton.Click();
                if (i == 132) Button(Texts.Yes).Click();
            });
            if (_root.Session.Halted) throw new InvalidOperationException("the halt of the sequence was not released");

            // 5. fold: away to the rail and back
            yield return VideoSession(s => src = s);
            yield return Record("fold", 5f, i =>
            {
                if (i == 20) _root.Workspace.Header.CollapseButton.Click();
                if (i == 88) _root.Workspace.Rail.Area.Click();
            });
            if (!_root.Workspace.Open) throw new InvalidOperationException("the window of the fold sequence did not open again");

            foreach (var (name, frames) in _sequences) Debug.Log($"[Renders] sequence {name}: {frames} frames");
            _exitCode = _exceptions.Count == 0 ? 0 : 1;
            if (_exceptions.Count > 0) Debug.LogWarning("[Renders] exceptions while rendering the sequences:\n" + string.Join("\n", _exceptions.Take(5)));
            Debug.Log($"[Renders] {_sequences.Count} sequences in {_outDir}, exit code {_exitCode}");
        }

        /// <summary>
        /// A fresh session for a sequence: the demo's four agents with their plans (the guided test's local source, no
        /// tour and no test band), the window open on the overview, the display offer answered, everything at rest;
        /// the camera straight in front of the window.
        /// </summary>
        IEnumerator VideoSession(Action<GuidedSource> made)
        {
            Time.captureDeltaTime = FrameSeconds;
            _hands.Left = Hand();
            _hands.Right = Hand();
            var pack = DemoPack.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "demo", "jury.pack.json")));
            var src = new GuidedSource(pack, fromPlans: true);
            typeof(AppRoot).GetMethod("BeginSession", Private).Invoke(_root, new object[] { new DemoSession(src), false });
            typeof(AppRoot).GetProperty("Current").SetValue(_root, AppRoot.Phase.Playing);
            made(src);
            var s = _root.Session;
            yield return Until(() => s.Store.Agents.Count == 4 && s.Store.Agents.All(a => a.Plan != null), 100, "the four agents and their plans");
            // the recording's start ends here: from now on only this script moves the agents (the pack's later updates
            // would take a dot back to where the recording has it)
            typeof(GuidedSource).GetField("_introEnd", Private).SetValue(src, 0.0);
            for (var i = 0; i < 30; i++)
            {
                if (_root.ScreenOffer != null)
                {
                    _root.ScreenOffer.Buttons[1].Click(); // "Later"
                    break;
                }
                yield return null;
            }
            yield return Frames(30);
            if (_root.Workspace.Asking) throw new InvalidOperationException("a question stands in the window");
            LookAtWorkspace();
            var bg = _root.Workspace.Background;
            var facing = bg.transform.forward;
            var halfW = bg.transform.lossyScale.x * 0.5f;
            var tan = Mathf.Tan(RenderCamera.FieldOfView * 0.5f * Mathf.Deg2Rad);
            var distance = halfW / VideoFill / (tan * VideoWidth / VideoHeight);
            _videoFrom = bg.bounds.center - facing * distance;
            _videoLook = Quaternion.LookRotation(facing, Vector3.up);
            Time.captureDeltaTime = 1f / VideoFps;
            yield return Frames(15);
        }

        /// <summary>
        /// Records `seconds` at 30 fps into `name`: `act(i)` sets up what happens in frame i (0-based), the app runs
        /// that frame, the picture is written as i + 1.
        /// </summary>
        IEnumerator Record(string name, float seconds, Action<int> act)
        {
            var dir = Path.Combine(_outDir, name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var frames = Mathf.RoundToInt(seconds * VideoFps);
            Time.captureDeltaTime = 1f / VideoFps;
            for (var i = 0; i < frames; i++)
            {
                act(i);
                yield return null;
                _cam.Frame(Path.Combine(dir, (i + 1).ToString("0000") + ".png"), _videoFrom, _videoLook);
            }
            _sequences.Add((name, frames));
            Debug.Log($"[Renders] sequence {name}: {frames} frames written");
        }

        /// <summary>The agent's dot moves on: its step in progress is done and the next one begins (as an agent's update would say).</summary>
        void Advance(string sid)
        {
            var s = _root.Session;
            var a = s.Store.Agents.First(x => x.Sid == sid).Clone();
            if (a.Plan == null) return;
            var steps = a.Plan.Steps;
            var now = steps.FindIndex(x => x.Status == PlanStatus.InProgress);
            if (now >= 0)
            {
                steps[now].Status = PlanStatus.Done;
                if (now + 1 < steps.Count) steps[now + 1].Status = PlanStatus.InProgress;
            }
            else
            {
                var next = steps.FindIndex(x => x.Status == PlanStatus.Pending);
                if (next >= 0) steps[next].Status = PlanStatus.InProgress;
            }
            a.Plan.UpdatedAt += 1;
            var message = new AgentMessage { Agent = a };
            var apply = typeof(DemoSession).GetMethod("Apply", Private, null, new[] { typeof(ServerMessage) }, null);
            if (apply != null) apply.Invoke(s, new object[] { message });
            else s.Store.Apply(message);
        }
    }
}
