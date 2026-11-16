using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U5a: the real AppRoot with fakes (hands, system keyboard, bridge socket, prefs) for the workspace's play-mode tests:
    /// start the demo or go live, poke buttons and rows, pinch at the block (inside the hold gate), drag with the ray.
    /// No desk anchor: the workspace stands 0.75 m in front of the head.
    /// </summary>
    public sealed class WorkspaceHarness
    {
        public sealed class ScriptedHands : IHandSource
        {
            public HandFrame Left = HandFrame.Untracked;
            public HandFrame Right = HandFrame.Untracked;

            public void Read(Vector3 head, out HandFrame left, out HandFrame right)
            {
                left = Left;
                right = Right;
            }
        }

        public sealed class FakeKeyboard : IReplyKeyboard
        {
            public readonly List<string> OpenedWith = new List<string>();
            public string Text { get; set; } = "";
            public KeyboardStatus Status { get; set; } = KeyboardStatus.Closed;

            public bool Open(string text)
            {
                OpenedWith.Add(text);
                Text = text;
                Status = KeyboardStatus.Visible;
                return true;
            }

            public void Close() => Status = KeyboardStatus.Closed;
        }

        public sealed class FakeSocket : ILinkSocket
        {
            public readonly List<string> Sent = new List<string>();
            public event Action Opened;
            public event Action<string> Received;
            public event Action<int> Closed;
            public void Connect() { }

            public bool Send(string text)
            {
                Sent.Add(text);
                return true;
            }

            public void Close() { }
            public void Pump() { }
            public void Open() => Opened?.Invoke();
            public void Receive(string json) => Received?.Invoke(json);
            public void Drop(int code) => Closed?.Invoke(code);
        }

        public sealed class MemoryStore : IKeyValueStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => Values[key] = value;
            public void Delete(string key) => Values.Remove(key);
        }

        public static readonly Vector3 HeadPos = new Vector3(0f, 1.6f, 0f);
        /// <summary>Hands resting in the lap: below the hold gate, never an approval.</summary>
        public static readonly Vector3 Lap = new Vector3(0.1f, 1.0f, 0.3f);
        public static readonly Vector3 RayFrom = new Vector3(0.15f, 1.3f, 0.25f);

        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Lotse = new List<string>();
        public readonly List<FakeSocket> Sockets = new List<FakeSocket>();
        public readonly MemoryStore Prefs = new MemoryStore();
        public GameObject Camera;
        public GameObject App;
        public ScriptedHands Hands;
        public FakeKeyboard Keyboard;
        public AppRoot Root;
        public FakeSocket Socket;
        public bool Focus = true;
        /// <summary>GoLive opens the first row when several agents arrive at once (the app does not).</summary>
        public bool SelectFirst = true;
        /// <summary>
        /// U13: the window opens with no row open; the U5a tests work on an open row, so StartDemo and GoLive open the
        /// selected (else the first) agent's row (the user's choice) unless this is false.
        /// </summary>
        public bool OpenConversation = true;
        /// <summary>Applied to the AppRoot before it starts (fakes for the desk placement and so on).</summary>
        public Action<AppRoot> Configure;

        public WorkspaceController Desk => Root.Desktop;
        public ConversationView Conversation => Root.Workspace.Conversation;
        public ApprovalBlockView Block => Root.Workspace.Conversation.Block;
        public TaxiRowsView Rows => Root.Workspace.Rows;

        void OnLog(string message, string stack, LogType type)
        {
            if (message.StartsWith("[Lotse]")) Lotse.Add(message);
            if (type == LogType.Exception || (type == LogType.Error && message.StartsWith("[Lotse]")))
                Errors.Add(type + ": " + message + "\n" + stack);
        }

        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true; // the editor's XR setup may log errors with no headset; ours are collected
            Errors.Clear();
            Lotse.Clear();
            Sockets.Clear();
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = 0.1f;
        }

        public void TearDown()
        {
            Time.captureDeltaTime = 0f;
            Application.logMessageReceived -= OnLog;
            if (App != null) UnityEngine.Object.Destroy(App);
            if (Camera != null) UnityEngine.Object.Destroy(Camera);
            var anchor = GameObject.Find("LotseAnchor");
            if (anchor != null) UnityEngine.Object.Destroy(anchor);
            Chime.On = true;
            Theme.Calm = false;
            Theme.Still = false;
            CollectionAssert.IsEmpty(Errors, string.Join("\n", Errors));
        }

        public static IEnumerator Frames(int n)
        {
            for (var i = 0; i < n; i++) yield return null;
        }

        public static HandFrame Hand(float pinch = 0f, Vector3? rayAt = null, Vector3? tip = null, Vector3? pinchAt = null, bool tracked = true,
            bool palm = false) => new HandFrame
        {
            Sample = new HandSample(tracked, tracked, pinch, palm),
            TipValid = tip.HasValue,
            Tip = tip ?? Vector3.zero,
            PinchValid = tracked,
            PinchPoint = pinchAt ?? Lap,
            RayValid = rayAt.HasValue,
            RayOrigin = RayFrom,
            RayDirection = rayAt.HasValue ? (rayAt.Value - RayFrom).normalized : Vector3.zero,
        };

        /// <summary>Starts the app (no desk: the fallback placement) and waits for the start panel.</summary>
        public IEnumerator Start(Action<AppRoot> configure = null)
        {
            Camera = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            Camera.transform.position = HeadPos;
            Hands = new ScriptedHands { Left = Hand(), Right = Hand() };
            Keyboard = new FakeKeyboard();
            App = new GameObject("LotseApp");
            App.SetActive(false);
            Root = App.AddComponent<AppRoot>();
            Root.Hands = Hands;
            Root.Head = Camera.transform;
            Root.HeadTracked = () => true;
            Root.InputFocus = () => Focus;
            Root.Keyboard = Keyboard;
            Root.Prefs = Prefs;
            Root.SocketFactory = url =>
            {
                var s = new FakeSocket();
                Sockets.Add(s);
                return s;
            };
            (configure ?? Configure)?.Invoke(Root);
            App.SetActive(true);
            var guard = 0;
            while (Root.Current != AppRoot.Phase.Start && guard++ < 600) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, Root.Current, "the start panel");
        }

        public IEnumerator StartDemo()
        {
            yield return Start();
            PokeButton.All.First(b => b.Label == Texts.StartButton).Click();
            yield return Frames(2);
            Assert.AreEqual(AppRoot.Phase.Playing, Root.Current);
            if (OpenConversation) yield return OpenFirst();
        }

        /// <summary>Opens the selected agent's row, else the first row on screen.</summary>
        public IEnumerator OpenFirst()
        {
            var guard = 0;
            while (Rows.Shown.Count == 0 && guard++ < 30) yield return null;
            if (Rows.Shown.Count == 0) yield break;
            var sid = Root.SelectedAgent;
            if (sid == null || Rows.RowOf(sid) == null) sid = Rows.Shown[0].Sid;
            yield return OpenRow(sid);
        }

        /// <summary>Opens `sid`'s row (a poke on it) and waits until the opening motion has finished.</summary>
        public IEnumerator OpenRow(string sid, bool settle = true)
        {
            if (Root.Desktop.OpenSid != sid)
            {
                var row = Rows.RowOf(sid);
                Assert.IsNotNull(row, sid + "'s row is on screen");
                yield return Poke(row);
            }
            Assert.AreEqual(sid, Root.Desktop.OpenSid, sid + "'s row is open");
            if (!settle) yield break;
            var guard = 0;
            while (!Root.Workspace.OpenSettled && guard++ < 40) yield return null;
            Assert.IsTrue(Root.Workspace.OpenSettled, "the row has opened");
            yield return Frames(2);
        }

        /// <summary>"All agents" in the header: the open row closes.</summary>
        public IEnumerator CloseRow()
        {
            if (Root.Desktop.OpenSid != null) yield return Poke(Root.Workspace.Header.AllButton);
            yield return Frames(1);
            Assert.IsNull(Root.Desktop.OpenSid, "no row is open");
            var guard = 0;
            while (!Rows.Settled && guard++ < 40) yield return null;
        }

        public static string Agent(string sid, string name, string status, string title = null, string lastAction = "") =>
            "{\"sid\":\"" + sid + "\",\"name\":\"" + name + "\",\"source\":\"claude\",\"status\":\"" + status +
            "\",\"lastAction\":\"" + lastAction + "\",\"since\":0,\"files\":[],\"waitingMsTotal\":0" +
            (title != null ? ",\"title\":\"" + title + "\",\"project\":\"shop-web\"" : "") + "}";

        public static string Request(string id, string sid, string risk = "safe", string command = "ls -la", bool truncated = false) =>
            "{\"id\":\"" + id + "\",\"sid\":\"" + sid + "\",\"tool\":\"Bash\",\"command\":\"" + command + "\",\"truncated\":" +
            (truncated ? "true" : "false") + ",\"paths\":[],\"risk\":{\"risk\":\"" + risk + "\",\"reason\":\"t\"},\"createdAt\":1000,\"expiresAt\":91000}";

        public static string PermOpen(string id, string sid, string risk = "safe", string command = "ls -la", string explain = "Claude listet die Dateien: ls -la") =>
            "{\"type\":\"perm_open\",\"request\":" + Request(id, sid, risk, command) + ",\"explain\":{\"text\":\"" + explain +
            "\",\"replies\":[\"Nein.\"],\"source\":\"rules\"}}";

        public static string PermClosed(string id, string reason) => "{\"type\":\"perm_closed\",\"id\":\"" + id + "\",\"reason\":\"" + reason + "\"}";

        public static string Entry(string id, string kind, string label, string text) =>
            "{\"id\":\"" + id + "\",\"t\":1,\"kind\":\"" + kind + "\",\"label\":\"" + label + "\",\"text\":\"" + text + "\"}";

        public static string Entries(int from, int to) =>
            string.Join(",", Enumerable.Range(from, to - from).Select(i => Entry("e" + i, i % 3 == 0 ? "tool" : "text", i % 3 == 0 ? "liest" : "sagt",
                "Eintrag " + i)));

        /// <summary>Goes live with a stored session: s1 waits for an answer, s2 works, s3 is done (`agents` overrides).</summary>
        public IEnumerator GoLive(string agents = null, bool withSession = true)
        {
            Prefs.Set(HostBook.HostsKey, "192.168.1.20");
            if (withSession) Prefs.Set(HostBook.SessionPrefix + "192.168.1.20", "tok");
            yield return Start();
            PokeButton.All.First(b => b.Label == Texts.BridgeButton).Click();
            yield return Frames(2);
            if (!withSession) yield break; // the caller types the code
            Socket = Sockets[0];
            Socket.Open();
            Socket.Receive("{\"type\":\"snapshot\",\"agents\":[" + (agents ?? Agent("s1", "Agent 1", "waiting", "Warenkorb-Badge") + "," +
                Agent("s2", "Agent 2", "working", "Checkout-Summen") + "," + Agent("s3", "Agent 3", "done", "Alte Seiten")) +
                "],\"pending\":[],\"explains\":{}}");
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, Root.Current);
            Assert.IsTrue(Root.Workspace.IsShown, "the workspace is up");
            // several agents at once: nothing is selected by itself (U5a fix 1); the user takes the first row
            if (OpenConversation && (SelectFirst || Root.SelectedAgent != null)) yield return OpenFirst();
        }

        public int Decisions() => Socket.Sent.Count(m => m.Contains("\"perm_decision\""));

        public IEnumerable<string> DecisionFrames() => Sockets.SelectMany(s => s.Sent).Where(m => m.Contains("\"perm_decision\""));

        /// <summary>A poke on a button from its front: away, in front (held a moment), through, away.</summary>
        public IEnumerator Poke(PokeButton b, bool slow = true)
        {
            Assert.IsNotNull(b, "the button exists");
            var t = b.transform;
            Hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            if (slow)
            {
                Hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.01f)));
                yield return null;
                yield return null;
            }
            Hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, 0.003f)));
            yield return null;
            Hands.Right = Hand();
            yield return null;
        }

        /// <summary>Opens an agent's row (pokes it) and waits for the motion.</summary>
        public IEnumerator PokeRow(string sid)
        {
            yield return OpenRow(sid);
        }

        /// <summary>A ray click: the right hand's ray on `at`, a short pinch, released on the same spot, then the ray away.</summary>
        public IEnumerator RayPinch(Vector3 at)
        {
            Hands.Right = Hand(rayAt: at);
            yield return null;
            Hands.Right = Hand(1f, rayAt: at);
            yield return Frames(2);
            Hands.Right = Hand(rayAt: at);
            yield return null;
            Hands.Right = Hand();
            yield return null;
        }

        /// <summary>Where a pinch counts for the hold gate: 0.35 m from the eye toward the block, the head facing it.</summary>
        public Vector3 GatePoint()
        {
            var block = Block.IsShown ? Block.Center : Root.Workspace.transform.position;
            Camera.transform.rotation = Quaternion.LookRotation(block - HeadPos, Vector3.up);
            return HeadPos + (block - HeadPos).normalized * 0.35f;
        }

        /// <summary>Both hands open inside the gate, then pinching there (both when `twoHands`), for `frames` frames.</summary>
        public IEnumerator PinchHold(bool twoHands, int frames)
        {
            var at = GatePoint();
            Hands.Right = Hand(pinchAt: at);
            Hands.Left = Hand(pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(2);
            Hands.Right = Hand(1f, pinchAt: at);
            if (twoHands) Hands.Left = Hand(1f, pinchAt: at + Vector3.left * 0.15f);
            yield return Frames(frames);
        }

        public void ReleaseHands()
        {
            Hands.Right = Hand();
            Hands.Left = Hand();
        }

        /// <summary>The header's Menu (U8: the wrist menu is off), then "Settings".</summary>
        public IEnumerator OpenSettings()
        {
            yield return OpenMenu();
            Root.Workspace.Menu.SettingsButton.Click();
            yield return Frames(2);
            Assert.IsTrue(Root.Settings.IsOpen);
        }

        /// <summary>"Menu" in the header, pressed: the menu opens.</summary>
        public IEnumerator OpenMenu()
        {
            Root.Workspace.Header.MenuButton.Click();
            yield return Frames(1);
            Assert.IsTrue(Root.Workspace.Menu.IsOpen, "the header's menu is open");
        }
    }
}
