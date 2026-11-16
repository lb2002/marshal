using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Lotse.App.Tests
{
    /// <summary>
    /// Desk placement through AppRoot with scripted hands, a flat desk at 0.75 m standing in for the
    /// environment raycast, and fake spatial anchors; with it the wrist menu ("Neu platzieren") and where the
    /// workspace stands (U5a). Fixed 100 ms frames.
    /// </summary>
    public class DeskPlacementTest
    {
        const float DeskY = 0.75f;
        const float DeskLift = 0.08f;
        static readonly Vector3 HeadPos = new Vector3(0f, 1.2f, 0f);
        static readonly Vector3 Spot = new Vector3(0.1f, DeskY, 0.45f);

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

        /// <summary>The room is one horizontal desk at DeskY.</summary>
        sealed class DeskRoom : IDeskRaycaster
        {
            public RaycastAvailability Availability { get; set; } = RaycastAvailability.Ready;
            public int Begins;
            public int Ends;
            public int Casts;
            public readonly List<Vector3> Origins = new List<Vector3>();
            /// <summary>Rays land on four levels this far apart, two rays each (clutter that never forms a plane).</summary>
            public float Jitter;

            public void Begin() => Begins++;
            public void End() => Ends++;

            public DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
            {
                Casts++;
                Origins.Add(origin);
                point = normal = Vector3.zero;
                if (Availability != RaycastAvailability.Ready) return DeskRayStatus.NotReady;
                if (direction.y > -1e-4f) return DeskRayStatus.NoHit;
                var t = (DeskY - origin.y) / direction.y;
                if (t < 0f || t > maxDistance) return DeskRayStatus.NoHit;
                point = origin + direction * t;
                point.y -= (Casts % 4) * Jitter;
                normal = Vector3.up;
                return DeskRayStatus.Hit;
            }
        }

        sealed class FakeAnchors : IDeskAnchors
        {
            public bool HasSaved { get; set; }
            public Action<Transform> PendingLoad;
            public int Loads;
            public readonly List<Transform> Created = new List<Transform>();

            public void LoadSaved(Action<Transform> done)
            {
                Loads++;
                PendingLoad = done;
            }

            public Transform Create(Vector3 position, Quaternion rotation)
            {
                var t = NewAnchor(position, rotation);
                Created.Add(t);
                return t;
            }
        }

        sealed class MemoryPrefs : IKeyValueStore
        {
            readonly Dictionary<string, string> _d = new Dictionary<string, string>();
            public string Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => _d[key] = value;
            public void Delete(string key) => _d.Remove(key);
        }

        static Transform NewAnchor(Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("FakeDeskAnchor");
            go.transform.SetPositionAndRotation(position, rotation);
            return go.transform;
        }

        static HandFrame Hand(Vector3? tip = null, bool palm = false, float pinch = 0f, Vector3? pinchAt = null) => new HandFrame
        {
            Sample = new HandSample(true, true, pinch, palm),
            TipValid = tip.HasValue,
            Tip = tip ?? Vector3.zero,
            PinchValid = pinchAt.HasValue,
            PinchPoint = pinchAt ?? Vector3.zero,
        };

        // the left palm, low in front of the seated user (head at 1.2 m): turned up it brings the wrist menu
        static readonly Vector3 PalmAt = HeadPos + new Vector3(-0.1f, -0.2f, 0.4f);

        static HandFrame Palm(bool up) => new HandFrame
        {
            Sample = new HandSample(true, true, 0f, false),
            PalmValid = true,
            PalmCenter = PalmAt,
            PalmNormal = up ? Vector3.up : Vector3.down,
        };

        /// <summary>U8: the wrist menu is off; "Place again" is on the start panel (and in the header's Menu once the window shows).</summary>
        IEnumerator OpenMenu()
        {
            yield return null;
            Assert.IsNotNull(MenuButton(Texts.Replace), "Place again is offered");
        }

        readonly List<string> _errors = new List<string>();
        readonly List<string> _lotse = new List<string>();
        GameObject _camera;
        GameObject _app;
        ScriptedHands _hands;
        DeskRoom _room;
        FakeAnchors _anchors;
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
            foreach (var t in Object.FindObjectsByType<Transform>().Where(t => t != null && (t.name == "LotseAnchor" || t.name == "FakeDeskAnchor")))
                Object.Destroy(t.gameObject);
            CollectionAssert.IsEmpty(_errors, string.Join("\n", _errors));
        }

        IEnumerator StartApp(bool hasSaved)
        {
            _camera = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            _camera.transform.position = HeadPos;
            _hands = new ScriptedHands();
            _room = new DeskRoom();
            _anchors = new FakeAnchors { HasSaved = hasSaved };
            _app = new GameObject("LotseApp");
            _app.SetActive(false);
            _root = _app.AddComponent<AppRoot>();
            _root.Hands = _hands;
            _root.Head = _camera.transform;
            _root.HeadTracked = () => true;
            _root.Prefs = new MemoryPrefs();
            _root.Raycaster = _room;
            _root.DeskAnchors = _anchors;
            _app.SetActive(true);
            var guard = 0;
            while (_root.Placer == null && guard++ < 100) yield return null;
            Assert.IsNotNull(_root.Placer, "placement started");
        }

        /// <summary>Starts the app and taps the desk at Spot; returns when the start panel is up.</summary>
        IEnumerator StartOnDesk()
        {
            yield return StartApp(hasSaved: false);
            _hands.Right = Hand(Spot + Vector3.up * 0.015f);
            var guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 20) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
            Assert.IsTrue(_root.OnDesk);
            _hands.Right = HandFrame.Untracked;
            yield return null;
        }

        static IEnumerator Frames(int n)
        {
            for (var i = 0; i < n; i++) yield return null;
        }

        static void Near(Vector3 expected, Vector3 actual, string what) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"{what}: expected {expected} but was {actual}");

        static PokeButton Button(string label) => PokeButton.All.FirstOrDefault(b => b.Label == label);

        /// <summary>U8: "Place again" from the start panel or the header's Menu (the wrist menu is off).</summary>
        static PokeButton MenuButton(string label) =>
            PokeButton.All.FirstOrDefault(b => b.Label == label && b.isActiveAndEnabled &&
                                               (b.GetComponentInParent<WindowMenu>() != null || b.transform.root.name == "LotseAnchor" && IsUnder(b.transform, "StartPanel")));

        static bool IsUnder(Transform t, string name)
        {
            for (; t != null; t = t.parent)
                if (t.name == name) return true;
            return false;
        }

        /// <summary>The tip comes from 8 cm in front of the button, stops 2 cm in front, then goes through.</summary>
        IEnumerator Poke(PokeButton b, bool release = true)
        {
            Assert.IsNotNull(b, "button exists");
            var t = b.transform;
            _hands.Right = Hand(t.TransformPoint(new Vector3(0f, 0f, -0.08f)));
            yield return null;
            _hands.Right = Hand(t.TransformPoint(new Vector3(0f, 0f, -0.02f)));
            yield return null;
            _hands.Right = Hand(t.TransformPoint(new Vector3(0f, 0f, 0.004f)));
            yield return null;
            if (!release) yield break;
            _hands.Right = HandFrame.Untracked;
            yield return null;
        }

        [UnityTest]
        public IEnumerator TappingTheDeskPutsTheContentThere()
        {
            yield return StartApp(hasSaved: false);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State);
            Assert.AreEqual(AppRoot.Phase.Loading, _root.Current, "the start panel waits for the placement");
            Assert.IsTrue(_root.Placer.View.HintShown);
            Assert.AreEqual(1, _room.Begins, "the raycaster was switched on");

            // the finger hovers 20 cm above the desk: the reticle shows on the desk below it, nothing is placed
            _hands.Right = Hand(Spot + Vector3.up * 0.2f);
            yield return Frames(10);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State);
            Assert.IsTrue(_root.Placer.View.ReticleShown);
            Near(Spot + Vector3.up * 0.002f, _root.Placer.View.ReticlePosition, "reticle on the desk under the tip");

            // the tip rests 1.5 cm above the desk: accepted after 0.4 s
            _hands.Right = Hand(Spot + Vector3.up * 0.015f);
            yield return Frames(3);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State, "0.3 s is not enough");
            var guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 10) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
            Assert.AreEqual(PlacementState.Placed, _root.Placer.State);
            Assert.AreEqual(1, _anchors.Created.Count, "one spatial anchor");
            var desk = _anchors.Created[0];
            Near(Spot, desk.position, "the anchor sits on the desk under the tip");
            Near(new Vector3(0.1f, 0f, 0.45f).normalized, desk.forward, "the anchor faces away from the user");
            Assert.IsTrue(_root.OnDesk);
            Near(Spot + Vector3.up * DeskLift, _root.Anchor.position, "the content sits on the desk");
            Assert.IsFalse(_root.Placer.View.HintShown);
            Assert.IsFalse(_root.Placer.View.ReticleShown);
            Assert.AreEqual(1, _room.Ends, "the raycaster was switched off");
            Assert.IsNotNull(Button(Texts.StartButton), "the start panel is up");
        }

        [UnityTest]
        public IEnumerator TheDiagnosticRayIsCastFromHeadHeightBeforeAnyTipIsSeen()
        {
            yield return StartApp(hasSaved: false);
            yield return Frames(2);
            var line = _lotse.FirstOrDefault(m => m.Contains("placement: diagnostic ray"));
            Assert.IsNotNull(line, "logged without a tip");
            StringAssert.Contains("diagnostic ray from head height Hit", line);
            StringAssert.Contains("head y 1.200", line);
            StringAssert.Contains("hit y 0.750", line);
            Assert.AreEqual(1, _lotse.Count(m => m.Contains("placement: diagnostic ray")), "once per placement");
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State);
        }

        [UnityTest]
        public IEnumerator AHandUnderTheRaysIsNotTheDesk()
        {
            yield return StartApp(hasSaved: false);
            // the left hand lies on the desk right where the eight rays land
            var origins = DeskSurface.RayOrigins(Ui.ToVec(Spot + Vector3.up * 0.015f), Ui.ToVec(HeadPos), new Vec3(0f, 0f, 0f));
            var joints = origins.Select(o => new Vector3(o.X, DeskY + 0.01f, o.Z)).ToArray();
            _hands.Left = new HandFrame { Sample = new HandSample(true, true, 0f, false), Joints = joints };
            _hands.Right = Hand(Spot + Vector3.up * 0.015f);
            yield return Frames(10);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State, "1 s on the hand: nothing placed");
            Assert.IsFalse(_root.Placer.View.ReticleShown, "no desk found");
            Assert.AreEqual(0, _anchors.Created.Count);

            _hands.Left = HandFrame.Untracked;
            var guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 10) yield return null;
            Assert.AreEqual(PlacementState.Placed, _root.Placer.State, "the hand gone: the desk counts");
        }

        [UnityTest]
        public IEnumerator TwoRaysGoAheadOfTheFingertipAlongTheFinger()
        {
            yield return StartApp(hasSaved: false);
            var tip = Spot + Vector3.up * 0.2f;
            _hands.Right = Hand(tip);
            _hands.Right.TipDir = new Vector3(0.7f, -0.7f, 0f).normalized; // to the right and down, not away from the user
            yield return Frames(2);
            var top = tip + Vector3.up * DeskSurface.RayStartAbove;
            foreach (var ahead in new[] { DeskSurface.AheadNear, DeskSurface.AheadFar })
            {
                var expected = top + Vector3.right * ahead;
                Assert.IsTrue(_room.Origins.Any(o => Vector3.Distance(o, expected) < 1e-4f), $"a ray {ahead} m to the right of the tip");
            }
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State);
        }

        [UnityTest]
        public IEnumerator HitsThatNeverFormAPlaneFallBackAfterEightSeconds()
        {
            yield return StartApp(hasSaved: false);
            _room.Jitter = 0.02f; // levels 2 cm apart, two rays on each: hits every frame, never three together
            _hands.Right = Hand(Spot + Vector3.up * 0.015f);
            yield return Frames(75);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State, "7.5 s");
            Assert.Greater(_room.Casts, 400, "the rays did hit something all along");
            var guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 10) yield return null;
            Assert.AreEqual(PlacementState.Fallback, _root.Placer.State);
            Assert.IsTrue(_lotse.Any(m => m.Contains("fallback (NoHit)")), "raw hits do not keep placement alive");
            Assert.AreEqual(0, _anchors.Created.Count);
        }

        [UnityTest]
        public IEnumerator WithoutAUsableTipNothingIsCastShownOrPlaced()
        {
            yield return StartApp(hasSaved: false);
            _hands.Right = new HandFrame { Sample = new HandSample(true, false, 0f, false), TipValid = false, Tip = Spot + Vector3.up * 0.01f };
            yield return Frames(15);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State);
            Assert.AreEqual(1, _room.Casts, "only the diagnostic ray, no placement rays without a usable tip");
            Assert.IsFalse(_root.Placer.View.ReticleShown);
            Assert.AreEqual(0, _anchors.Created.Count);
        }

        [UnityTest]
        public IEnumerator TheContentFollowsTheAnchor()
        {
            yield return StartOnDesk();
            var desk = _anchors.Created[0];
            desk.position += new Vector3(0f, 0f, 0.02f);
            yield return null;
            Near(Spot + new Vector3(0f, DeskLift, 0.02f), _root.Anchor.position, "the content follows the anchor");
        }

        [UnityTest]
        public IEnumerator TheDeskAnchorIsNeverRecaptured()
        {
            yield return StartOnDesk();
            _camera.transform.position = new Vector3(0f, 1.6f, 3f);
            yield return Frames(2);
            Assert.IsTrue(_root.OnDesk);
            Near(Spot + Vector3.up * DeskLift, _root.Anchor.position, "walking away does not pull the desk content along");
        }

        [UnityTest]
        public IEnumerator AFailedReplaceKeepsTheDeskAnchor()
        {
            yield return StartOnDesk();
            var desk = _anchors.Created[0];
            _room.Availability = RaycastAvailability.Unsupported;
            yield return OpenMenu();
            yield return Poke(MenuButton(Texts.Replace));
            yield return null;
            Assert.AreEqual(PlacementState.Fallback, _root.Placer.State);
            Assert.IsTrue(_root.OnDesk, "still on the desk");
            Assert.IsTrue(desk != null, "the desk anchor object is kept");
            Near(Spot + Vector3.up * DeskLift, _root.Anchor.position, "the content did not move");
            Assert.AreEqual(Texts.PlaceKept, _root.Banner.Line, "only the banner says so");
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
        }

        [UnityTest]
        public IEnumerator AReplaceIgnoresTheFingerThatPressedTheButton()
        {
            yield return StartOnDesk();
            yield return OpenMenu();
            var replace = MenuButton(Texts.Replace);
            yield return Poke(replace, release: false);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State, "re-placing");

            // the button floats above the wrist, well above the desk: the finger that pressed it is nowhere near the desk
            Assert.Greater(replace.transform.position.y, DeskY + 0.07f); // U10: the larger window at 2 m reaches lower; U13: the start panel is lower, "Place again" a link on its button row
            var atButton = replace.transform.position;
            _hands.Right = Hand(atButton);
            yield return Frames(5);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State, "0.5 s at the button: nothing placed");
            Assert.AreEqual(1, _anchors.Created.Count);
            Assert.AreEqual(MenuHand.None, _root.Menu.Shown, "U8: no wrist menu");

            // a real tap on the desk
            var elsewhere = new Vector3(Spot.x - 0.12f, DeskY + 0.015f, Spot.z);
            _hands.Right = Hand(elsewhere);
            var guard = 0;
            while (_root.Placer.State == PlacementState.Placing && guard++ < 10) yield return null;
            Assert.AreEqual(PlacementState.Placed, _root.Placer.State);
            Assert.AreEqual(2, _anchors.Created.Count);
            Near(new Vector3(elsewhere.x, DeskY, elsewhere.z), _anchors.Created[1].position, "placed where the finger tapped");
            yield return null;
            Assert.IsTrue(_anchors.Created[0] == null, "the old desk anchor object is gone");
        }

        [UnityTest]
        public IEnumerator AButtonCannotFireWhilePlacing()
        {
            yield return StartOnDesk();
            yield return OpenMenu();
            yield return Poke(MenuButton(Texts.Replace));
            Assert.IsTrue(_root.Placer.Active, "placing");
            var start = Button(Texts.StartButton);
            Assert.IsNotNull(start, "the start panel's button is there");
            yield return Poke(start);
            yield return Frames(2);
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current, "no button press while placing");
            Assert.IsNull(_root.Session);
            Assert.IsTrue(_root.Placer.Active, "still placing");
        }

        [UnityTest]
        public IEnumerator TheWristMenuNeverShowsWhileABlockWaits()
        {
            yield return StartOnDesk();
            Assert.AreEqual(MenuHand.None, _root.Menu.Shown, "nothing always in view");
            yield return Poke(Button(Texts.StartButton));
            var s = _root.Session;
            var guard = 0;
            while (s.Store.Pending.Count == 0 && guard++ < 300) yield return null;
            // its row (U6: the window opens on Übersicht, so into Unterhaltung with its agent). The frames are the unfold:
            // with the head on the desk the window may have folded during the wait, in Übersicht before any block was
            // shown (a shown block keeps it open: OverviewTest.ABlockWaitingForItsDecisionKeepsTheWindowOpenWithTheHeadAway)
            _root.Desktop.OpenAgent(s.Store.Pending[0].Sid);
            yield return Frames(10); // the row opens (0.6 s)
            Assert.IsNotNull(s.Card, "its block");
            _hands.Left = Palm(false);
            yield return null;
            _hands.Left = Palm(true);
            yield return Frames(15);
            Assert.AreEqual(MenuHand.None, _root.Menu.Shown);
            Assert.IsFalse(PokeButton.All.Any(b => b.isActiveAndEnabled && b.GetComponentInParent<WristMenu>() != null), "no wrist menu while a block waits");
        }


        [UnityTest]
        public IEnumerator TheWristMenuStaysOffWithThePalmUp()
        {
            // U8: palm up plus a pinch is the Quest's system gesture; nothing in the app depends on a palm-up pose
            yield return StartOnDesk();
            _hands.Left = Palm(false);
            yield return null;
            _hands.Left = Palm(true);
            yield return Frames(15);
            Assert.AreEqual(MenuHand.None, _root.Menu.Shown);
            Assert.IsFalse(PokeButton.All.Any(b => b.GetComponentInParent<WristMenu>() != null && b.isActiveAndEnabled));
        }

        [UnityTest]
        public IEnumerator TheWorkspaceStandsInFrontOfTheHeadFacingItAndDoesNotFollowTheHead()
        {
            // U5b, from the first device test: the window stood edge-on beside the desk anchor; now 0.75 m in front of the
            // head, 5 cm below the eyes, its readable front facing the head
            yield return StartOnDesk();
            var dock = _root.Dock;
            Assert.AreEqual(2.0f, new Vector2(dock.Center.X - HeadPos.x, dock.Center.Z - HeadPos.z).magnitude, 1e-3f, "U10: 2.0 m ahead");
            Assert.AreEqual(HeadPos.y - 0.05f, dock.Center.Y, 1e-3f, "5 cm below the eyes");
            Assert.AreEqual(WorkspaceDock.DefaultHeight, dock.Height, 1e-3f, "U10: 0.455 of the distance");
            var w = _root.Workspace.transform;
            Near(Ui.ToVector(dock.Center), w.position, "the window stands where the dock says");
            AssertFacesTheHead(w, HeadPos);
            Assert.IsTrue(Button(Texts.StartButton).transform.IsChildOf(_root.Anchor), "the start panel stands in the window's place");
            Assert.Less(Vector3.Distance(Button(Texts.StartButton).transform.position, w.position), 0.5f); // U13: the button stands at the panel's left

            // the head moves: the window stays
            var at = w.position;
            _camera.transform.position = HeadPos + new Vector3(0.1f, 0f, 0f);
            yield return Poke(Button(Texts.StartButton));
            yield return Frames(3);
            Near(at, w.position, "it does not follow the head");
            Assert.IsTrue(_root.Workspace.IsShown, "after placement the workspace appears");
        }

        [UnityTest]
        public IEnumerator TheDisplayOfferComesOnceAfterTheFirstDeskPlacementAndYesStartsTheSetup()
        {
            // U5b: "Put Marshal next to a display?" Yes / Later, once. U9: asked in the window's own band, under its header,
            // as soon as the window is on screen (no second window above the main one)
            yield return StartOnDesk();
            Assert.IsNull(_root.ScreenOffer, "nothing floats above the start panel");
            Assert.IsTrue(_root.ScreenOfferPending, "the offer waits for the window");
            Assert.IsNotNull(Button(Texts.StartButton), "the start panel is there");
            yield return Poke(Button(Texts.StartButton));
            yield return Frames(3);
            Assert.IsNotNull(_root.ScreenOffer, "offered in the window's band after the first desk placement");
            Assert.AreEqual(Texts.ScreenOffer, _root.ScreenOffer.TextShown);
            Assert.IsTrue(_root.ScreenOffer.transform.IsChildOf(_root.Workspace.transform), "inside the window");
            Assert.AreEqual("1", _root.Prefs.Get(ScreenRecord.OfferedKey), "offered once: remembered");
            yield return Poke(_root.ScreenOffer.Buttons[0]);
            yield return Frames(2);
            Assert.IsNull(_root.ScreenOffer);
            Assert.IsTrue(_root.Setup.Active, "Yes starts the display setup");
            yield return Poke(_root.SetupView.CancelButton);
            yield return Frames(2);
            Assert.IsFalse(_root.Setup.Active);
            Assert.IsNull(_root.ScreenOffer, "not asked again");
            Assert.IsFalse(_root.ScreenOfferPending);
        }

        /// <summary>The window's readable side (its content looks along -forward) and the direction to the head agree within 5 degrees.</summary>
        static void AssertFacesTheHead(Transform window, Vector3 head)
        {
            var front = Vector3.ProjectOnPlane(-window.forward, Vector3.up);
            var toHead = Vector3.ProjectOnPlane(head - window.position, Vector3.up);
            Assert.Less(Vector3.Angle(front, toHead), 5f, "the readable front faces the head");
        }

        [UnityTest]
        public IEnumerator ASavedDeskAnchorWithAnOddRotationNeverTurnsTheWindowEdgeOn()
        {
            yield return StartApp(hasSaved: true);
            _camera.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
            var guard = 0;
            while (_anchors.Loads == 0 && guard++ < 100) yield return null;
            // the runtime hands back an anchor whose forward points up (rotated 90 degrees about x and turned)
            _anchors.PendingLoad(NewAnchor(Spot, Quaternion.Euler(-90f, 75f, 0f)));
            guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 100) yield return null;
            Assert.IsTrue(_root.OnDesk);
            yield return Frames(3);
            AssertFacesTheHead(_root.Workspace.transform, HeadPos);
            Assert.AreEqual(30f, Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(_root.Workspace.transform.forward, Vector3.up), Vector3.up), 2f,
                "ahead of where the head looked at placement");
        }


        [UnityTest]
        public IEnumerator ASavedAnchorOutOfReachIsIgnored()
        {
            yield return StartApp(hasSaved: true);
            var guard = 0;
            while (_anchors.Loads == 0 && guard++ < 100) yield return null;
            var far = NewAnchor(new Vector3(0f, 0.74f, 2.5f), Quaternion.identity); // another table across the room
            _anchors.PendingLoad(far);
            yield return Frames(2);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State, "asks for a tap instead");
            Assert.IsFalse(_root.OnDesk);
            Assert.IsTrue(far == null, "the far anchor object is dropped");
            Assert.AreEqual(1, _room.Begins);

            _hands.Right = Hand(Spot + Vector3.up * 0.015f);
            guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 20) yield return null;
            Assert.IsTrue(_root.OnDesk);
            Near(Spot + Vector3.up * DeskLift, _root.Anchor.position, "placed where tapped");
        }

        [UnityTest]
        public IEnumerator ASavedAnchorBehindTheUserIsIgnored()
        {
            yield return StartApp(hasSaved: true);
            var guard = 0;
            while (_anchors.Loads == 0 && guard++ < 100) yield return null;
            _anchors.PendingLoad(NewAnchor(new Vector3(0.2f, 0.74f, -0.5f), Quaternion.identity));
            yield return Frames(2);
            Assert.AreEqual(PlacementState.Placing, _root.Placer.State);
        }

        [UnityTest]
        public IEnumerator ASavedAnchorIsUsedWithoutATap()
        {
            yield return StartApp(hasSaved: true);
            var guard = 0;
            while (_anchors.Loads == 0 && guard++ < 100) yield return null;
            Assert.AreEqual(1, _anchors.Loads);
            Assert.AreEqual(PlacementState.LoadingSaved, _root.Placer.State);
            yield return Frames(5);
            var saved = NewAnchor(new Vector3(-0.2f, 0.74f, 0.5f), Quaternion.LookRotation(new Vector3(-0.2f, 0f, 0.5f)));
            _anchors.PendingLoad(saved);
            guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 10) yield return null;
            Assert.AreEqual(AppRoot.Phase.Start, _root.Current);
            Assert.AreEqual(PlacementState.LoadedSaved, _root.Placer.State);
            Assert.IsTrue(_root.OnDesk);
            Near(saved.position + Vector3.up * DeskLift, _root.Anchor.position, "the content sits on the saved anchor");
            Assert.AreEqual(0, _room.Begins, "no tap was needed: the raycaster stays off (no wall search since U5a)");
            Assert.IsNull(_root.Banner.Line);
        }

        [UnityTest]
        public IEnumerator ASavedAnchorThatDoesNotLocaliseWithinThreeSecondsLeadsToTheTap()
        {
            yield return StartApp(hasSaved: true);
            _room.Availability = RaycastAvailability.Unsupported;
            var guard = 0;
            while (_anchors.Loads == 0 && guard++ < 100) yield return null;
            yield return Frames(25);
            Assert.AreEqual(PlacementState.LoadingSaved, _root.Placer.State, "2.5 s");
            Assert.AreEqual(0, _room.Begins);
            guard = 0;
            while (_root.Current != AppRoot.Phase.Start && guard++ < 20) yield return null;
            Assert.AreEqual(1, _room.Begins, "placing started after 3 s (and fell back)");
            Assert.AreEqual(PlacementState.Fallback, _root.Placer.State, "and fell back: no raycast here");
            Assert.AreEqual(Texts.PlaceFallback, _root.Banner.Line);
            Assert.IsFalse(_root.OnDesk);
            Near(new Vector3(0f, HeadPos.y - 0.25f, 0.6f), _root.Anchor.position, "head-relative anchor");

            // the load answers late: it is not used
            var late = NewAnchor(new Vector3(0.3f, 0.74f, 0.5f), Quaternion.identity);
            _anchors.PendingLoad(late);
            yield return null;
            Assert.IsTrue(late == null, "the late anchor object is destroyed");
            Assert.IsFalse(_root.OnDesk);
        }
    }
}
