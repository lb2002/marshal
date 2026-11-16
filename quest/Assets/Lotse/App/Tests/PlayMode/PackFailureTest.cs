using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Lotse.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lotse.App.Tests
{
    /// <summary>F7: without a usable demo pack the app still starts; live mode stays, the demo button says why it is off.</summary>
    public class PackFailureTest
    {
        sealed class NoHands : IHandSource
        {
            public void Read(Vector3 head, out HandFrame left, out HandFrame right)
            {
                left = HandFrame.Untracked;
                right = HandFrame.Untracked;
            }
        }

        readonly List<string> _exceptions = new List<string>();
        GameObject _camera;
        GameObject _app;

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Exception) _exceptions.Add(message + "\n" + stack);
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true; // the pack error is logged on purpose; editor XR noise too
            Application.logMessageReceived += OnLog;
            Time.captureDeltaTime = 0.1f;
            _exceptions.Clear();
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
        }

        [UnityTest]
        public IEnumerator AMissingPackLeavesLiveModeAndSaysTheDemoIsOff()
        {
            _camera = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            _camera.transform.position = new Vector3(0f, 1.6f, 0f);
            _app = new GameObject("LotseApp");
            _app.SetActive(false);
            var root = _app.AddComponent<AppRoot>();
            root.Hands = new NoHands();
            root.Head = _camera.transform;
            root.HeadTracked = () => true;
            root.PackFile = "demo/does-not-exist.pack.json";
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Lotse\] pack load failed"));
            _app.SetActive(true);

            var guard = 0;
            while (root.Current == AppRoot.Phase.Loading && guard++ < 600) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, root.Current, "the start panel comes up anyway");

            var demo = PokeButton.All.FirstOrDefault(b => b.Label == Texts.DemoUnavailable);
            Assert.IsNotNull(demo, "the demo button says the demo is not available");
            Assert.IsFalse(demo.Interactable);
            Assert.IsNull(PokeButton.All.FirstOrDefault(b => b.Label == Texts.StartButton));
            var bridge = PokeButton.All.FirstOrDefault(b => b.Label == Texts.BridgeButton);
            Assert.IsNotNull(bridge);
            Assert.IsTrue(bridge.Interactable, "live mode stays available");
            CollectionAssert.IsEmpty(_exceptions, string.Join("\n", _exceptions));
        }
    }
}
