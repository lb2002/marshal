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
    /// Live mode through the Unity layer with a fake bridge socket and scripted hands: poke "Mit Brücke
    /// verbinden", type a new address and the code on the poke pads, pair, approve a card with a pinch-hold
    /// (it shows "Gesendet …" until the bridge closes it), then lose the connection with a card open.
    /// </summary>
    public class LivePairingSmokeTest
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
            public readonly string Url;
            public readonly List<string> Sent = new List<string>();

            public FakeSocket(string url)
            {
                Url = url;
            }

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

        sealed class MemoryStore : IKeyValueStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => Values[key] = value;
            public void Delete(string key) => Values.Remove(key);
        }

        const string Snapshot = "{\"type\":\"snapshot\",\"agents\":[{\"sid\":\"s1\",\"name\":\"Alpha\",\"status\":\"working\"," +
                                "\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}],\"pending\":[],\"explains\":{}}";

        static string PermOpen(string id, string risk) =>
            "{\"type\":\"perm_open\",\"request\":{\"id\":\"" + id + "\",\"sid\":\"s1\",\"tool\":\"Bash\",\"command\":\"ls\"," +
            "\"truncated\":false,\"paths\":[],\"risk\":{\"risk\":\"" + risk + "\",\"reason\":\"test\"},\"createdAt\":1,\"expiresAt\":90001}," +
            "\"explain\":{\"text\":\"Listet Dateien.\",\"replies\":[\"Nein.\"],\"source\":\"rules\"}}";

        // the head is at (0, 1.6, 0) looking ahead: pinches are held up in front of the chest
        static HandFrame Hand(float pinch = 0f, Vector3? tip = null) => new HandFrame
        {
            Sample = new HandSample(true, true, pinch, false),
            TipValid = tip.HasValue,
            Tip = tip ?? Vector3.zero,
            PinchValid = true,
            PinchPoint = new Vector3(0.1f, 1.45f, 0.35f),
        };

        /// <summary>The left palm turned up toward the head, low in front: the wrist menu.</summary>
        static HandFrame PalmUpHand(bool up = true) => new HandFrame
        {
            Sample = new HandSample(true, true, 0f, false),
            PalmValid = true,
            PalmCenter = new Vector3(-0.1f, 1.4f, 0.4f),
            PalmNormal = up ? Vector3.up : Vector3.down,
        };

        /// <summary>U8: the header's Menu (the wrist menu is off).</summary>
        static IEnumerator OpenMenu(ScriptedHands hands)
        {
            var root = UnityEngine.Object.FindAnyObjectByType<AppRoot>();
            if (!root.Workspace.Menu.IsOpen) root.Workspace.Header.MenuButton.Click();
            yield return null;
        }

        static IEnumerator CloseMenu(ScriptedHands hands)
        {
            UnityEngine.Object.FindAnyObjectByType<AppRoot>().Workspace.Menu.Hide();
            yield return null;
        }

        readonly List<string> _errors = new List<string>();
        readonly List<FakeSocket> _sockets = new List<FakeSocket>();
        GameObject _camera;
        GameObject _app;

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Exception || (type == LogType.Error && message.StartsWith("[Lotse]")))
                _errors.Add(type + ": " + message + "\n" + stack);
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true; // the editor's XR setup may log errors with no headset; ours are collected
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = 0.1f;
            _sockets.Clear(); // NUnit keeps one fixture instance for all its tests
            _errors.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            Time.captureDeltaTime = 0f;
            Application.logMessageReceived -= OnLog;
            if (_app != null) UnityEngine.Object.Destroy(_app);
            if (_camera != null) UnityEngine.Object.Destroy(_camera);
            var anchor = GameObject.Find("LotseAnchor");
            if (anchor != null) UnityEngine.Object.Destroy(anchor);
        }

        static PokeButton Button(string label) => PokeButton.All.FirstOrDefault(b => b.Label == label);

        /// <summary>The wrist menu's button (the start panel has its own "Neu platzieren").</summary>
        static PokeButton MenuButton(string label) =>
            PokeButton.All.FirstOrDefault(b => b.Label == label && b.isActiveAndEnabled && b.GetComponentInParent<WindowMenu>() != null);

        static IEnumerator Poke(ScriptedHands hands, string label)
        {
            var b = Button(label);
            Assert.IsNotNull(b, "button exists: " + label);
            var t = b.transform;
            hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, -0.02f)));
            yield return null;
            hands.Right = Hand(tip: t.TransformPoint(new Vector3(0f, 0f, 0.004f)));
            yield return null;
            hands.Right = Hand();
            yield return null;
            yield return null;
        }

        static IEnumerator Frames(int n)
        {
            for (var i = 0; i < n; i++) yield return null;
        }

        /// <summary>The approval block (or the line in its place) shows this text.</summary>
        static bool CardShows(AppRoot root, string text) =>
            root.Workspace.Conversation.Block.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == text);

        [UnityTest]
        public IEnumerator PairApproveAndLoseTheConnectionWithHandsOnly()
        {
            _camera = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            _camera.transform.position = new Vector3(0f, 1.6f, 0f);
            var hands = new ScriptedHands { Left = Hand(), Right = Hand() };
            var prefs = new MemoryStore();
            _app = new GameObject("LotseApp");
            _app.SetActive(false);
            var root = _app.AddComponent<AppRoot>();
            root.Hands = hands;
            root.Head = _camera.transform;
            root.HeadTracked = () => true;
            root.Prefs = prefs;
            root.SocketFactory = url =>
            {
                var s = new FakeSocket(url);
                _sockets.Add(s);
                return s;
            };
            _app.SetActive(true);

            var guard = 0;
            while (root.Current == AppRoot.Phase.Loading && guard++ < 600) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, root.Current);

            yield return Poke(hands, Texts.BridgeButton);
            Assert.AreEqual(AppRoot.Phase.Pairing, root.Current);
            Assert.IsNotNull(root.Pairing);
            Assert.IsNull(root.Pairing.Host, "no address yet");
            CollectionAssert.IsEmpty(_sockets, "nothing to resume");

            // a new address on the address pad
            yield return Poke(hands, Texts.NewHost);
            foreach (var c in "192.168.1.20") yield return Poke(hands, c.ToString());
            yield return Poke(hands, Texts.Ok);
            Assert.AreEqual("192.168.1.20", root.Pairing.Host);

            // the code on the number pad, one wrong digit taken back
            foreach (var c in "13579") yield return Poke(hands, c.ToString());
            yield return Poke(hands, "8");
            yield return Poke(hands, Texts.Backspace);
            yield return Poke(hands, "2");
            yield return Poke(hands, Texts.Ok);
            Assert.AreEqual(1, _sockets.Count);
            yield return null;
            Assert.AreEqual(Texts.Connecting, root.Pairing.Status);
            yield return Poke(hands, Texts.Ok); // OK again while connecting: ignored, the code goes out once
            Assert.AreEqual(1, _sockets.Count, "one socket for two OKs");
            var sock = _sockets[0];
            Assert.AreEqual("ws://192.168.1.20:47821", sock.Url);
            yield return null;
            Assert.AreEqual("Connecting… please wait", root.Pairing.Status, "the second OK is answered");
            // U8: the wrist menu is off; during pairing "Back" leads to the start panel, which has "Place again"
            Assert.IsNotNull(Button(Texts.Back), "Back is reachable during pairing");
            Assert.IsNull(Button(Texts.Disconnect), "no Disconnect while pairing (the panel has Back)");

            sock.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"135792\"}", sock.Sent.Single());
            sock.Receive(Snapshot);
            sock.Receive("{\"type\":\"paired\",\"session\":\"tok\"}");
            sock.Receive(PermOpen("r1", "safe"));
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, root.Current);
            Assert.IsNull(root.Pairing, "the pairing panel is gone");
            Assert.AreEqual(Texts.Paired, root.Banner.Line);
            Assert.AreEqual("tok", prefs.Get(HostBook.SessionPrefix + "192.168.1.20"));
            Assert.AreEqual(WorkspaceTab.Overview, root.Desktop.Mode.Tab, "the window opens on Übersicht (U6)");
            Assert.IsNull(root.Session.Card, "no block in Übersicht");
            Assert.AreEqual(1, root.Workspace.Rows.Shown.Count, "the agent's row");
            yield return Poke(hands, "Row 1");
            yield return Frames(10); // the row opens (0.6 s)
            Assert.AreEqual("s1", root.SelectedAgent, "selected once, as the window opened");
            Assert.AreEqual("r1", root.Session.Card?.Id, "its block");

            // approve with a pinch-hold: sent, and the card waits for the bridge
            hands.Right = Hand();
            yield return Frames(2);
            hands.Right = Hand(pinch: 1f);
            yield return Frames(10);
            hands.Right = Hand();
            var decisions = sock.Sent.Where(m => m.Contains("perm_decision")).ToList();
            Assert.AreEqual(1, decisions.Count);
            Assert.AreEqual("{\"type\":\"perm_decision\",\"decision\":{\"id\":\"r1\",\"behavior\":\"allow\",\"confirm\":\"pinch-hold\"}}", decisions[0]);
            yield return null;
            Assert.AreEqual(CardMode.Sent, root.Session.Mode);
            Assert.IsTrue(CardShows(root, Texts.SentWaiting), "the block says Gesendet …");

            sock.Receive("{\"type\":\"perm_closed\",\"id\":\"r1\",\"reason\":\"allow\"}");
            yield return Frames(2);
            Assert.IsNull(root.Session.Card);
            Assert.AreEqual(Headline.Cleared(null), root.Workspace.Conversation.Block.NoteShown, "the bridge allowed: cleared");
            Assert.IsTrue(root.Workspace.Conversation.Block.ClearedShown);
            Assert.IsNull(Button(Texts.Disconnect), "Trennen is in the settings, not always in view");
            yield return OpenMenu(hands);
            Assert.IsNotNull(MenuButton(Texts.Settings), "the settings (with Disconnect) are in the header's Menu");
            Assert.IsNull(MenuButton(Texts.Disconnect), "not in the menu itself");
            Assert.IsNotNull(MenuButton(Texts.Replace));
            yield return CloseMenu(hands);

            // an allow the bridge closes for another reason (timeout): never shown as approved
            sock.Receive(PermOpen("r3", "safe"));
            yield return Frames(25);
            Assert.AreEqual("r3", root.Session.Card?.Id);
            hands.Right = Hand(pinch: 1f);
            yield return Frames(10);
            hands.Right = Hand();
            Assert.AreEqual(CardMode.Sent, root.Session.Mode);
            sock.Receive("{\"type\":\"perm_closed\",\"id\":\"r3\",\"reason\":\"timeout\"}");
            yield return Frames(2);
            Assert.IsTrue(CardShows(root, Texts.TimedOut), "a timeout sends the user to the terminal");
            Assert.IsFalse(CardShows(root, Texts.Approved));

            // a dangerous card, then the connection drops mid-hold: the card closes, nothing is sent
            sock.Receive(PermOpen("r2", "destructive"));
            yield return Frames(25); // the quiet time after the last gesture
            Assert.AreEqual("r2", root.Session.Card?.Id);
            hands.Left = Hand(pinch: 1f);
            hands.Right = Hand(pinch: 1f);
            yield return Frames(5);
            sock.Drop(1006);
            yield return Frames(2);
            Assert.IsNull(root.Session.Card);
            Assert.AreEqual(Texts.ConnectionLost, root.Banner.Line);
            yield return Frames(20);
            hands.Left = Hand();
            hands.Right = Hand();
            Assert.AreEqual(2, sock.Sent.Count(m => m.Contains("perm_decision")), "nothing more was sent");
            Assert.IsTrue(_sockets.Count >= 2, "it reconnects by itself");

            // "Trennen" in the settings while it retries: back to the start panel, no more attempts
            yield return OpenMenu(hands);
            yield return Poke(hands, Texts.Settings);
            yield return CloseMenu(hands);
            Assert.IsTrue(root.Settings.IsOpen, "the settings from the wrist menu");
            Assert.IsTrue(root.Settings.DisconnectButton.Interactable, "Trennen is offered while live");
            yield return Poke(hands, Texts.Disconnect);
            Assert.AreEqual(AppRoot.Phase.Start, root.Current);
            Assert.AreEqual(LinkState.Idle, root.Live.State);
            Assert.IsFalse(root.Settings.IsOpen, "the settings close with it");
            Assert.IsNull(Button(Texts.Disconnect), "gone with the live session");
            var count = _sockets.Count;
            yield return Frames(150);
            Assert.AreEqual(count, _sockets.Count, "no reconnect after Trennen");
            CollectionAssert.IsEmpty(_errors, string.Join("\n", _errors));
        }

        [UnityTest]
        public IEnumerator TappingAnAddressWhoseTokenWasRefusedSendsNoSessionHello()
        {
            _camera = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            _camera.transform.position = new Vector3(0f, 1.6f, 0f);
            var hands = new ScriptedHands { Left = Hand(), Right = Hand() };
            var prefs = new MemoryStore();
            prefs.Set(HostBook.HostsKey, "192.168.1.20");
            prefs.Set(HostBook.SessionPrefix + "192.168.1.20", "stale");
            _app = new GameObject("LotseApp");
            _app.SetActive(false);
            var root = _app.AddComponent<AppRoot>();
            root.Hands = hands;
            root.Head = _camera.transform;
            root.HeadTracked = () => true;
            root.Prefs = prefs;
            root.SocketFactory = url =>
            {
                var s = new FakeSocket(url);
                _sockets.Add(s);
                return s;
            };
            _app.SetActive(true);

            var guard = 0;
            while (root.Current == AppRoot.Phase.Loading && guard++ < 600) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, root.Current);

            // the panel resumes the last address once; the bridge refuses the stale token
            yield return Poke(hands, Texts.BridgeButton);
            Assert.AreEqual(1, _sockets.Count);
            _sockets[0].Open();
            Assert.AreEqual("{\"type\":\"hello\",\"session\":\"stale\"}", _sockets[0].Sent.Single());
            _sockets[0].Drop(1004);
            yield return Frames(2);
            Assert.AreEqual(LinkState.NeedsCode, root.Live.State);
            Assert.AreEqual(Texts.SessionExpired, root.Pairing.Status);

            // (U12a) the stored session's one attempt failed: scanning would start; here (no tracker in the editor) the
            // window shows, with the bridge's words
            yield return Frames(3);
            // five taps on that address: straight to the code pad, no hello {session} (the bridge would lock the address)
            for (var i = 0; i < 5; i++)
            {
                yield return Poke(hands, "192.168.1.20");
                yield return Frames(2);
            }
            Assert.AreEqual(1, _sockets.Count, "no new connection attempt");
            Assert.AreEqual("192.168.1.20", root.Pairing.Host);
            Assert.IsNotNull(Button("1"), "the code pad is up");
            CollectionAssert.IsEmpty(_errors, string.Join("\n", _errors));
        }
    }
}
