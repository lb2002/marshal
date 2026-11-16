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
    /// U1: pointing with the hand ray and clicking with a pinch, through AppRoot with scripted hands (the demo,
    /// head-relative anchor). Fixed 100 ms frames.
    /// </summary>
    public class PointingTest
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

        // the head is at (0, 1.6, 0) looking ahead; the right hand's aim ray starts low in front of the chest
        static readonly Vector3 HeadPos = new Vector3(0f, 1.6f, 0f);
        static readonly Vector3 Raised = new Vector3(0.1f, 1.45f, 0.35f);
        static readonly Vector3 Low = new Vector3(0.1f, 1.0f, 0.3f); // the lap: the hold gate is closed there
        static readonly Vector3 RayFrom = new Vector3(0.15f, 1.3f, 0.25f);
        static readonly Vector3 Sky = new Vector3(0.15f, 3f, 1f);

        static HandFrame Hand(float pinch = 0f, Vector3? rayAt = null, Vector3? pinchAt = null) => new HandFrame
        {
            Sample = new HandSample(true, true, pinch, false),
            PinchValid = true,
            PinchPoint = pinchAt ?? Raised,
            RayValid = rayAt.HasValue,
            RayOrigin = RayFrom,
            RayDirection = rayAt.HasValue ? (rayAt.Value - RayFrom).normalized : Vector3.zero,
        };

        readonly List<string> _errors = new List<string>();
        readonly List<string> _lotse = new List<string>();
        GameObject _camera;
        GameObject _app;
        ScriptedHands _hands;
        AppRoot _root;

        void OnLog(string message, string stack, LogType type)
        {
            if (message.StartsWith("[Lotse]")) _lotse.Add(message);
            if (type == LogType.Exception || (type == LogType.Error && message.StartsWith("[Lotse]")))
                _errors.Add(type + ": " + message + "\n" + stack);
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true; // the editor's XR setup may log errors with no headset; ours are collected
            _errors.Clear();
            _lotse.Clear(); // NUnit keeps one fixture instance for all tests
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = 0.1f;
        }

        [TearDown]
        public void TearDown()
        {
            Time.captureDeltaTime = 0f;
            Application.logMessageReceived -= OnLog;
            if (_app != null) Object.Destroy(_app);
            if (_camera != null) Object.Destroy(_camera);
            var anchor = GameObject.Find("LotseAnchor");
            if (anchor != null) Object.Destroy(anchor);
            CollectionAssert.IsEmpty(_errors, string.Join("\n", _errors));
        }

        static PokeButton Button(string label) => PokeButton.All.FirstOrDefault(b => b.Label == label);

        static IEnumerator Frames(int n)
        {
            for (var i = 0; i < n; i++) yield return null;
        }

        IEnumerator StartApp()
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
            _app.SetActive(true);
            var guard = 0;
            while (_root.Current == AppRoot.Phase.Loading && guard++ < 600) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
        }

        /// <summary>Aims the right ray at `at` with an open hand, then pinches for `frames` frames.</summary>
        IEnumerator RayPinch(Vector3 at, int frames = 2, Vector3? pinchAt = null)
        {
            _hands.Right = Hand(rayAt: at, pinchAt: pinchAt);
            yield return null;
            _hands.Right = Hand(1f, rayAt: at, pinchAt: pinchAt);
            yield return Frames(frames);
        }

        [UnityTest]
        public IEnumerator ARayPinchClicksAButtonOnceAndAPinchOnNothingDoesNothing()
        {
            yield return StartApp();
            _hands.Right = Hand(rayAt: Sky);
            yield return Frames(2);
            Assert.IsTrue(_root.Pointer.RightShown, "the right ray is drawn");
            Assert.IsFalse(_root.Pointer.LeftShown, "no ray without a valid aim pose");

            // a pinch with the ray on nothing does nothing
            _hands.Right = Hand(1f, rayAt: Sky);
            yield return Frames(5);
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
            Assert.IsNull(_root.Session);

            // the same pinch held while the ray moves onto the button: still nothing
            var start = Button(Texts.StartButton);
            Assert.IsNotNull(start);
            _hands.Right = Hand(1f, rayAt: start.transform.position);
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current, "only a fresh pinch clicks");
            Assert.IsTrue(start.RayHover, "the button shows the ray is on it");

            // released and pinched on the button, released again within 0.6 s: one click, on the release
            yield return RayPinch(start.transform.position, frames: 4);
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current, "nothing on the press");
            _hands.Right = Hand(rayAt: start.transform.position); // released on the button within 0.6 s
            yield return Frames(2);
            Assert.AreEqual(AppRoot.Phase.Playing, _root.Current);
            Assert.AreEqual(1, _lotse.Count(m => m.Contains("button pressed: " + Texts.StartButton)), "fired once");
            Assert.AreEqual(1, _lotse.Count(m => m.Contains("demo started")));
        }
    }
}
