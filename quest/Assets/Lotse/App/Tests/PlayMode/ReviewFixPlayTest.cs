using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lotse.App.Tests
{
    /// <summary>
    /// Fixes from the U1+U2 review (task-u2-review-1.md) through AppRoot, as they stand in U5a's workspace: fallback
    /// entry points for "Neu platzieren" (I2; "Trennen" is in the settings since U4a), the conversation redrawn only when
    /// what it shows changes (M3), text fading without TMP mesh rebuilds (M4), a hovered button growing to 1.04 (M10).
    /// </summary>
    public class ReviewFixPlayTest
    {
        sealed class ScriptedHands : IHandSource
        {
            public HandFrame Left = HandFrame.Untracked;
            public HandFrame Right = HandFrame.Untracked;

            public void Read(Vector3 head, out HandFrame left, out HandFrame right)
            {
                left = Left;
                right = Right;
            }
        }

        sealed class FakeSocket : ILinkSocket
        {
            public readonly List<string> Sent = new List<string>();
            public event Action Opened;
            public event Action<string> Received;
#pragma warning disable 67
            public event Action<int> Closed;
#pragma warning restore 67
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
        }

        /// <summary>A room whose raycasts are switched on and off by the test (off: placement falls back at once).</summary>
        sealed class Room : IDeskRaycaster
        {
            public RaycastAvailability Availability { get; set; } = RaycastAvailability.Unsupported;
            public void Begin() { }
            public void End() { }
            public DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
            {
                point = normal = Vector3.zero;
                return DeskRayStatus.NotReady;
            }
        }

        sealed class MemoryStore : IKeyValueStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => Values[key] = value;
            public void Delete(string key) => Values.Remove(key);
        }

        static readonly Vector3 HeadPos = new Vector3(0f, 1.6f, 0f);
        static readonly Vector3 Low = new Vector3(0.1f, 1.0f, 0.3f);
        static readonly Vector3 RayFrom = new Vector3(0.15f, 1.3f, 0.25f);

        static HandFrame Hand(float pinch = 0f, Vector3? rayAt = null, Vector3? tip = null) => new HandFrame
        {
            Sample = new HandSample(true, true, pinch, false),
            TipValid = tip.HasValue,
            Tip = tip ?? Vector3.zero,
            PinchValid = true,
            PinchPoint = Low, // below the hold gate: never an approval
            RayValid = rayAt.HasValue,
            RayOrigin = RayFrom,
            RayDirection = rayAt.HasValue ? (rayAt.Value - RayFrom).normalized : Vector3.zero,
        };

        readonly List<string> _errors = new List<string>();
        readonly List<FakeSocket> _sockets = new List<FakeSocket>();
        GameObject _camera;
        GameObject _app;
        GameObject _extra;
        ScriptedHands _hands;
        AppRoot _root;
        Room _room;

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Exception || (type == LogType.Error && message.StartsWith("[Lotse]")))
                _errors.Add(type + ": " + message + "\n" + stack);
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _errors.Clear();
            _sockets.Clear();
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = 0.1f;
        }

        [TearDown]
        public void TearDown()
        {
            Time.captureDeltaTime = 0f;
            Application.logMessageReceived -= OnLog;
            if (_app != null) UnityEngine.Object.Destroy(_app);
            if (_camera != null) UnityEngine.Object.Destroy(_camera);
            if (_extra != null) UnityEngine.Object.Destroy(_extra);
            var anchor = GameObject.Find("LotseAnchor");
            if (anchor != null) UnityEngine.Object.Destroy(anchor);
            CollectionAssert.IsEmpty(_errors, string.Join("\n", _errors));
        }

        static IEnumerator Frames(int n)
        {
            for (var i = 0; i < n; i++) yield return null;
        }

        static PokeButton ButtonUnder(string parent, string label) =>
            PokeButton.All.FirstOrDefault(b => b.Label == label && b.transform.GetComponentsInParent<Transform>().Any(t => t.name == parent));

        IEnumerator StartApp(MemoryStore prefs = null)
        {
            _camera = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            _camera.transform.position = HeadPos;
            _hands = new ScriptedHands { Left = Hand(), Right = Hand() };
            _app = new GameObject("LotseApp");
            _app.SetActive(false);
            _root = _app.AddComponent<AppRoot>();
            _root.Hands = _hands;
            _root.Head = _camera.transform;
            _root.HeadTracked = () => true;
            _room = new Room();
            _root.Raycaster = _room;
            if (prefs != null)
            {
                _root.Prefs = prefs;
                _root.SocketFactory = url =>
                {
                    var s = new FakeSocket();
                    _sockets.Add(s);
                    return s;
                };
            }
            _app.SetActive(true);
            var guard = 0;
            while (_root.Current == AppRoot.Phase.Loading && guard++ < 600) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
        }

        IEnumerator RayPinch(Vector3 at)
        {
            _hands.Right = Hand(rayAt: at);
            yield return null;
            _hands.Right = Hand(1f, rayAt: at);
            yield return Frames(2);
            _hands.Right = Hand(rayAt: at); // released on the target: the click
            yield return null;
            _hands.Right = Hand();
            yield return null;
        }

        IEnumerator Poke(PokeButton b)
        {
            Assert.IsNotNull(b);
            var t = b.transform;
            _hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            _hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.02f)));
            yield return null;
            _hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, 0.004f)));
            yield return null;
            _hands.Right = Hand();
            yield return null;
        }

        static string Agent(string sid, string status, string extra = "") =>
            "{\"sid\":\"" + sid + "\",\"name\":\"Agent " + sid + "\",\"source\":\"claude\",\"status\":\"" + status +
            "\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0" + extra + "}";

        IEnumerator GoLive(FakeSocket[] socket, string agents)
        {
            var prefs = new MemoryStore();
            prefs.Set(HostBook.HostsKey, "192.168.1.20");
            prefs.Set(HostBook.SessionPrefix + "192.168.1.20", "tok");
            yield return StartApp(prefs);
            yield return RayPinch(ButtonUnder("StartPanel", Texts.BridgeButton).transform.position);
            socket[0] = _sockets[0];
            socket[0].Open();
            socket[0].Receive("{\"type\":\"snapshot\",\"agents\":[" + agents + "],\"pending\":[],\"explains\":{}}");
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, _root.Current);
            // U13: the window opens with no row open; these tests work on the first agent's open row (ray-clicked)
            yield return RayPinch(_root.Workspace.Rows.RowOf(_root.Workspace.Rows.Shown[0].Sid).transform.position);
            Assert.IsNotNull(_root.Desktop.OpenSid);
            yield return Frames(10); // the row opens (0.6 s)
        }

        // ---- I2: fallback entry points ----

        [UnityTest]
        public IEnumerator TheStartPanelOffersNeuPlatzieren()
        {
            yield return StartApp();
            var replace = ButtonUnder("StartPanel", Texts.Replace);
            Assert.IsNotNull(replace, "not only in the wrist menu");
            Assert.IsFalse(_root.Placer.Active);
            yield return Frames(3);
            _room.Availability = RaycastAvailability.Starting; // the room's raycasts start up: placement waits
            yield return Poke(replace);
            Assert.IsTrue(_root.Placer.Active, "placing again");
        }

        /// <summary>The left palm turned up toward the head, low in front (the wrist menu); down first, for the rising edge.</summary>
        IEnumerator OpenWristMenu()
        {
            _hands.Left = new HandFrame { Sample = new HandSample(true, true, 0f, false), PalmValid = true,
                PalmCenter = HeadPos + new Vector3(-0.1f, -0.2f, 0.4f), PalmNormal = Vector3.down };
            yield return null;
            _hands.Left = new HandFrame { Sample = new HandSample(true, true, 0f, false), PalmValid = true,
                PalmCenter = HeadPos + new Vector3(-0.1f, -0.2f, 0.4f), PalmNormal = Vector3.up };
            var guard = 0;
            while (_root.Menu.Shown == MenuHand.None && guard++ < 20) yield return null;
            Assert.AreNotEqual(MenuHand.None, _root.Menu.Shown, "the wrist menu");
        }

        IEnumerator OpenSettings()
        {
            // U8: from the header's Menu (the wrist menu is off)
            if (!_root.Workspace.Menu.IsOpen) _root.Workspace.Header.MenuButton.Click();
            yield return null;
            yield return Poke(_root.Workspace.Menu.SettingsButton);
            yield return null;
            Assert.IsTrue(_root.Settings.IsOpen, "the settings from the header's Menu");
        }

        // U4a: "Trennen" left the middle of the view; it is in the settings (from the wrist menu)
        [UnityTest]
        public IEnumerator WhileLiveTrennenIsInTheSettingsAndNeverInTheMiddleOfTheView()
        {
            var socket = new FakeSocket[1];
            yield return GoLive(socket, Agent("s1", "working"));
            Assert.IsNull(PokeButton.All.FirstOrDefault(b => b.Label == Texts.Disconnect), "no Trennen pill in view");
            Assert.IsNull(GameObject.Find("LiveBar"));

            yield return OpenSettings();
            StringAssert.StartsWith("Connected to 192.168.1.20", _root.Settings.Shown.Connection);
            Assert.IsTrue(_root.Settings.DisconnectButton.Interactable);

            // a request for the selected agent while the settings are open: its block stays hidden behind them
            socket[0].Receive("{\"type\":\"perm_open\",\"request\":{\"id\":\"p1\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"ls\",\"truncated\":false," +
                              "\"paths\":[],\"risk\":{\"risk\":\"safe\",\"reason\":\"t\"},\"createdAt\":1,\"expiresAt\":90001}," +
                              "\"explain\":{\"text\":\"Listet.\",\"replies\":[\"Nein.\"],\"source\":\"rules\"}}");
            yield return Frames(2);
            Assert.IsTrue(_root.Settings.IsOpen, "the settings stay");
            Assert.IsNull(_root.Session.Card, "the window is covered: no block, nothing approvable");
            Assert.IsFalse(socket[0].Sent.Any(m => m.Contains("perm_decision")), "nothing was decided");
            yield return Frames(3);
            yield return Poke(_root.Settings.DisconnectButton);
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current, "disconnected");
            Assert.AreEqual(LinkState.Idle, _root.Live.State);
            Assert.IsFalse(_root.Settings.IsOpen);
        }

        // ---- M7: panels stop rays ----

        [UnityTest]
        public IEnumerator TheConversationIsRedrawnOnlyWhenWhatItShowsChanges()
        {
            var socket = new FakeSocket[1];
            yield return GoLive(socket, Agent("s1", "done"));
            Assert.AreEqual("s1", _root.SelectedAgent);
            socket[0].Receive("{\"type\":\"agent_detail\",\"sid\":\"s1\",\"title\":\"t\",\"summary\":\"Baut um.\",\"recent\":[],\"waitingFor\":null,\"source\":\"rules\"}");
            yield return Frames(3);
            var conv = _root.Workspace.Conversation;
            var send = conv.Input.SendButton;
            var header = _root.Workspace.Header.Version;
            var rows = _root.Workspace.Rows.Version;
            var row = _root.Workspace.Rows.Shown[0];
            Assert.IsNotNull(send);
            socket[0].Receive("{\"type\":\"agent\",\"agent\":" + Agent("s1", "done", ",\"ctxPct\":42") + "}");
            yield return Frames(3);
            Assert.AreEqual(header, _root.Workspace.Header.Version, "only ctxPct changed: the header is not redrawn");
            Assert.AreEqual(rows, _root.Workspace.Rows.Version, "nor the rows laid out again");
            Assert.AreEqual(row.Key, _root.Workspace.Rows.Shown[0].Key, "its row shows the same");
            socket[0].Receive("{\"type\":\"agent\",\"agent\":" + Agent("s1", "working", ",\"ctxPct\":42") + "}");
            yield return Frames(3);
            Assert.AreNotEqual(row.Key, _root.Workspace.Rows.Shown[0].Key, "the status changed: its row is redrawn");
            Assert.AreSame(send, conv.Input.SendButton, "the input bar is not");
            Assert.AreEqual(TaxiState.Moving, _root.Workspace.Rows.Shown[0].State);
        }

        // ---- M4 and M6: no per-frame rebuilds or writes ----

        [UnityTest]
        public IEnumerator TextFadesWithoutMeshRebuilds()
        {
            var socket = new FakeSocket[1];
            yield return GoLive(socket, Agent("s1", "done"));
            yield return OpenSettings();
            // the panel appears: its text fades through the material property block, not the text's vertex alpha
            var texts = _root.Settings.GetComponentsInChildren<TextMeshPro>();
            Assert.IsNotEmpty(texts);
            var block = new MaterialPropertyBlock();
            yield return Frames(3);
            foreach (var t in texts)
            {
                Assert.AreEqual(1f, t.alpha, 1e-4f, t.name + ": vertex alpha untouched");
                t.GetComponent<Renderer>().GetPropertyBlock(block);
                Assert.AreEqual(1f, block.GetColor("_FaceColor").a, 1e-3f, t.name + ": faded in through _FaceColor");
            }

        }

        [UnityTest]
        public IEnumerator TheFadeStartsTransparent()
        {
            yield return StartApp();
            var start = GameObject.Find("StartPanel");
            var block = new MaterialPropertyBlock();
            var t = start.GetComponentsInChildren<TextMeshPro>().First();
            t.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.Less(block.GetColor("_FaceColor").a, 1f, "just appeared: still fading in");
            Assert.AreEqual(1f, t.alpha, 1e-4f);
        }

        // ---- M10: behaviour instead of constants ----

        [UnityTest]
        public IEnumerator AButtonUnderTheRayShowsItsHoverStateAndNothingMoves()
        {
            yield return StartApp();
            yield return Frames(3);
            var b = ButtonUnder("StartPanel", Texts.BridgeButton);
            Assert.AreEqual(1f, b.VisualScale, 1e-4f);
            _hands.Right = Hand(rayAt: b.transform.position);
            yield return Frames(4);
            Assert.IsTrue(b.HoverShown, "hovered: drawn lighter, its border lit");
            Assert.AreEqual(1f, b.VisualScale, 1e-4f, "U10: nothing moves on hover");
            Assert.AreEqual(1f, b.transform.localScale.x, 1e-6f, "the pressable face does not grow");
            _hands.Right = Hand(rayAt: new Vector3(0.15f, 3f, 1f));
            yield return Frames(4);
            Assert.IsFalse(b.HoverShown, "back");
        }

    }
}
