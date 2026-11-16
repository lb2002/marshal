using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lotse.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// Pairing by looking at the QR code (U11, its two fix rounds, and U12's viewfinder), with a scripted tracker.
    /// "Connect to my PC" opens the viewfinder and scans. A code seen never connects by itself (a question band asks,
    /// only its "Pair" starts the pairing the number pad uses, once); "Cancel" does nothing; a code whose attempt failed
    /// is not asked about again by itself and nothing retries; outside scan mode nothing is read; the address is adopted
    /// only after it paired and is in no log line; without the permission the window with the number pad shows.
    /// </summary>
    public class QrPairPlayTest
    {
        /// <summary>A tracker that would hand out its payloads at any time: what keeps them out is the app.</summary>
        sealed class FakeQr : IQrSource
        {
            public readonly List<byte[]> Payloads = new List<byte[]>();
            public QrSourceState OnBegin = QrSourceState.Scanning;
            public int Begins;
            public int Ends;
            public int Reads;
            /// <summary>Where the code is, when the tracker knows (null: it does not).</summary>
            public Vector3? Place;
            public float Size = 0.04f;
            public QrSourceState State { get; set; } = QrSourceState.Off;

            public void Begin()
            {
                Begins++;
                State = OnBegin;
            }

            public void End()
            {
                Ends++;
                State = QrSourceState.Off;
            }

            public void Read(List<byte[]> payloads)
            {
                Reads++;
                payloads.Clear();
                payloads.AddRange(Payloads);
            }

            public bool TryPlace(byte[] payload, out Vector3 position, out float size)
            {
                position = Place ?? default;
                size = Size;
                return Place.HasValue;
            }
        }

        const string Code = "481216";
        const string Address = "192.168.1.197";
        const string Known = "192.168.1.20";
        const string Failed = "That code did not work. Press Enter in the terminal for a new one, then scan again.";
        static readonly byte[] A = Encoding.UTF8.GetBytes("marshal://pair?v=1&h=" + Address + "&p=47821&c=" + Code);
        static readonly byte[] B = Encoding.UTF8.GetBytes("marshal://pair?v=1&h=" + Address + "&p=47821&c=777777");

        WorkspaceHarness _h;
        FakeQr _qr;
        readonly List<string> _urls = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
            _qr = new FakeQr();
            _urls.Clear();
            _h.Configure = root =>
            {
                root.QrSource = _qr;
                root.SocketFactory = url =>
                {
                    _urls.Add(url);
                    var s = new FakeSocket();
                    _h.Sockets.Add(s);
                    return s;
                };
            };
        }

        [TearDown]
        public void TearDown() => _h.TearDown();

        PairingPanel Panel => _h.Root.Pairing;

        static PokeButton Button(string label) => PokeButton.All.FirstOrDefault(b => b != null && b.isActiveAndEnabled && b.Label == label);

        /// <summary>"Connect to my PC" on the start panel: the viewfinder, scanning (unless a stored session is resumed first).</summary>
        IEnumerator OpenPairing()
        {
            yield return _h.Start();
            Button(Texts.BridgeButton).Click();
            yield return Frames(2);
            Assert.IsNotNull(Panel, "the pairing panel");
        }

        /// <summary>Scanning is on: it is when the panel opens; otherwise the control that starts it is pressed.</summary>
        IEnumerator PressScan()
        {
            if (!Panel.Scanning)
            {
                Assert.IsNotNull(Panel.ScanButton, "a control that starts scanning");
                Panel.ScanButton.Click();
            }
            yield return Frames(2);
            Assert.IsTrue(Panel.Scanning);
        }

        /// <summary>"Type it instead": the window with the number pad.</summary>
        IEnumerator OpenManual()
        {
            Assert.IsNotNull(Panel.TypeButton, "Type it instead");
            Assert.AreEqual("Type it instead", Panel.TypeButton.Label);
            Panel.TypeButton.Click();
            yield return Frames(2);
            Assert.AreEqual(PairView.Manual, Panel.View);
        }

        /// <summary>The question's "Pair" counts only after it has shown for a moment.</summary>
        static IEnumerator Arm() => new WaitForSecondsRealtime(PairingPanel.AskArmSeconds + 0.15f);

        List<string> QrLines => _h.Lotse.Where(l => l.StartsWith(Ev.Prefix + "qr ")).ToList();

        /// <summary>Scan, see `payload`, wait until the question counts, press "Pair".</summary>
        IEnumerator ScanAndPair(byte[] payload)
        {
            _qr.Payloads.Clear();
            _qr.Payloads.Add(payload);
            yield return PressScan();
            yield return Frames(2);
            Assert.IsNotNull(Panel.ScanBand, "the question");
            yield return Arm();
            yield return Frames(2);
            Panel.ScanBand.Buttons[0].Click();
            yield return Frames(2);
        }

        /// <summary>The viewfinder at rest: nothing scans, nothing of the scan was adopted.</summary>
        void AssertAtRest(string hostBefore, string recentBefore)
        {
            Assert.IsFalse(Panel.Scanning, "scan mode does not reopen by itself");
            Assert.IsNull(Panel.ScanBand);
            Assert.AreEqual(QrSourceState.Off, _qr.State, "the tracker is stopped");
            Assert.AreEqual(PairView.Idle, Panel.View, "the viewfinder at rest");
            Assert.IsNotNull(Panel.ScanButton, "a control to scan again");
            Assert.AreEqual("Scan again", Panel.ScanButton.Label);
            Assert.AreEqual(hostBefore, Panel.Host, "the manual address is what it was before the scan");
            Assert.AreEqual(recentBefore, _h.Prefs.Get(HostBook.HostsKey), "the recent addresses are unchanged");
        }

        // ---- U12: the viewfinder ----

        [UnityTest]
        public IEnumerator ConnectToMyPcGoesStraightToTheViewfinderAndAsksForThePermissionThen()
        {
            yield return _h.Start();
            yield return Frames(30);
            Assert.AreEqual(0, _qr.Begins, "on the start panel nothing is asked for and nothing scans");
            Assert.AreEqual(0, _qr.Reads);
            Button(Texts.BridgeButton).Click();
            yield return Frames(2);
            Assert.AreEqual(1, _qr.Begins, "the press is the request to scan: the permission is asked for now");
            Assert.AreEqual(PairView.Scan, Panel.View, "no screen in between");
            Assert.IsTrue(Panel.Scanning);
            Assert.AreEqual("Look at the code in your terminal", Panel.ScanInstructionShown);
            var texts = Panel.Finder.GetComponentsInChildren<TMPro.TextMeshPro>().Select(t => t.GetParsedText()).ToList();
            CollectionAssert.Contains(texts, "Run  lotse connect  on your PC to show it");
            // the rest is not there: no window, no address, no number pad
            Assert.IsNull(Button(Texts.NewHost));
            Assert.IsNull(Button("1"));
            Assert.IsNull(Panel.transform.Find("Content"), "no window");
            // two quiet controls, H high
            CollectionAssert.AreEquivalent(new[] { "Type it instead", Texts.Back },
                Panel.Finder.GetComponentsInChildren<PokeButton>().Select(b => b.Label).ToArray());
            foreach (var b in Panel.Finder.GetComponentsInChildren<PokeButton>()) Assert.AreEqual(Theme.H, b.Size.y, 1e-6f);
            // the brackets: a square of 14 degrees a metre in front of the head, nothing filled in it
            yield return new WaitForSecondsRealtime(0.2f);
            yield return Frames(2);
            var head = _h.Camera.transform;
            Assert.Less(Vector3.Distance(head.position + head.forward * 1f, Panel.Finder.position), 0.02f, "in the middle of the view, a metre away");
            Assert.IsNotNull(Panel.Brackets);
            var side = PairingPanel.Square * Panel.Finder.lossyScale.x;
            Assert.AreEqual(14f, 2f * Mathf.Atan(side * 0.5f / 1f) * Mathf.Rad2Deg, 0.1f, "14 degrees");
            var lines = Panel.Brackets.GetComponentInChildren<MeshFilter>().sharedMesh.bounds.size;
            Assert.AreEqual(PairingPanel.Square, lines.x, 2e-3f);
            Assert.AreEqual(PairingPanel.Square, lines.y, 2e-3f);
            Assert.Less(Panel.Brackets.GetComponentInChildren<MeshFilter>().sharedMesh.triangles.Length, 500, "eight thin strokes and four corner arcs with their dark edges (U13: rounded corners), no surface");
            Assert.LessOrEqual(RenderProbe.Renderers(Panel.Finder.gameObject), 16, "little to draw");
        }

        [UnityTest]
        public IEnumerator TheViewfinderFollowsTheHeadUprightAndFreezesUnderARayAPinchAndTheQuestion()
        {
            yield return OpenPairing();
            var head = _h.Camera.transform;
            yield return new WaitForSecondsRealtime(0.2f);
            // the head turns to the screen with the code: right and down
            head.rotation = Quaternion.Euler(25f, 50f, 0f);
            yield return Frames(2);
            Assert.Greater(Vector3.Distance(head.position + head.forward, Panel.Finder.position), 0.3f, "a soft lag, no jump");
            yield return new WaitForSecondsRealtime(1.6f);
            yield return Frames(2);
            Assert.Less(Vector3.Distance(head.position + head.forward, Panel.Finder.position), 0.03f, "it came along into the middle of the view");
            Assert.Less(Vector3.Angle(Panel.Finder.up, Vector3.up), 0.01f, "upright: no pitch, no roll");
            Assert.AreEqual(50f, Panel.Finder.eulerAngles.y, 1f, "turned with the head");
            Assert.IsFalse(Panel.FinderFrozen);
            // a ray on one of its controls: it stays where it is, so the target does not run away
            var at = Panel.Finder.position;
            _h.Hands.Right = Hand(rayAt: Panel.TypeButton.transform.position);
            yield return Frames(3);
            Assert.IsTrue(Panel.TypeButton.HoverShown);
            head.rotation = Quaternion.Euler(0f, -30f, 0f);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Frames(2);
            Assert.IsTrue(Panel.FinderFrozen, "frozen under the ray");
            Assert.Less(Vector3.Distance(at, Panel.Finder.position), 1e-4f);
            // the ray leaves: it follows again
            _h.Hands.Right = Hand();
            yield return new WaitForSecondsRealtime(1.6f);
            yield return Frames(2);
            Assert.Less(Vector3.Distance(head.position + head.forward, Panel.Finder.position), 0.03f, "released: it follows again");
            // a pinch under way: frozen
            at = Panel.Finder.position;
            _h.Hands.Left = Hand(1f);
            yield return Frames(2);
            head.rotation = Quaternion.Euler(0f, 40f, 0f);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Frames(2);
            Assert.IsTrue(Panel.FinderFrozen, "frozen while a pinch is under way");
            Assert.Less(Vector3.Distance(at, Panel.Finder.position), 1e-4f);
            _h.Hands.Left = Hand();
            yield return new WaitForSecondsRealtime(1.6f);
            yield return Frames(2);
            // the question: it stays where it appeared
            _qr.Payloads.Add(A);
            yield return Frames(3);
            Assert.IsNotNull(Panel.ScanBand);
            at = Panel.Finder.position;
            head.rotation = Quaternion.Euler(0f, -20f, 0f);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Frames(2);
            Assert.IsTrue(Panel.FinderFrozen, "frozen while the question shows");
            Assert.Less(Vector3.Distance(at, Panel.Finder.position), 1e-4f, "its buttons are stable");
        }

        [UnityTest]
        public IEnumerator WhenACodeIsSeenTheBracketsGoToTheCodeOrCloseIn()
        {
            yield return OpenPairing();
            yield return new WaitForSecondsRealtime(0.2f);
            var open = Panel.Brackets.lossyScale.x;
            // the tracker does not say where the code is: the brackets close in
            _qr.Payloads.Add(A);
            yield return Frames(3);
            Assert.IsNotNull(Panel.ScanBand);
            Assert.IsFalse(Panel.Snapped);
            Assert.Less(Panel.Brackets.lossyScale.x, open * 0.9f, "closed in");
            Assert.Less(Vector3.Distance(Panel.Finder.position, Panel.Brackets.position), 1e-4f);
            yield return Arm();
            yield return Frames(2);
            Panel.ScanBand.Buttons[1].Click();
            yield return Frames(2);
            // the tracker says where it is (a 4 cm code on a screen 0.6 m away): the brackets stand on it
            var code = HeadPos + new Vector3(0.1f, -0.25f, 0.55f);
            _qr.Place = code;
            yield return PressScan();
            yield return Frames(2);
            Assert.IsTrue(Panel.Snapped);
            Assert.Less(Vector3.Distance(code, Panel.Brackets.position), 1e-4f, "on the code");
            Assert.AreEqual(0.06f, PairingPanel.Square * Panel.Brackets.lossyScale.x, 1e-3f, "a little larger than the code");
            Assert.Less(Vector3.Angle(Panel.Brackets.up, Vector3.up), 0.01f);
            Assert.IsNotNull(Panel.ScanBand, "and the question under the square");
            // the viewfinder's middle is on the line from the head to the code: the question stands right under the code
            var toCode = (code - HeadPos).normalized;
            Assert.Less(Vector3.Distance(HeadPos + toCode * 1f, Panel.Finder.position), 1e-3f);
            Assert.Less(Panel.ScanBand.transform.position.y, code.y, "under it");
            Assert.AreEqual(0, _urls.Count);
        }

        [UnityTest]
        public IEnumerator TheWindowIsBehindTypeTheCodeInsteadAndWorksAsBefore()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            yield return OpenPairing();
            _qr.Payloads.Add(A); // a code in view; the question shows
            yield return Frames(3);
            Assert.IsNotNull(Panel.ScanBand);
            yield return Arm();
            Panel.ScanBand.Buttons[1].Click();
            yield return Frames(2);
            yield return OpenManual();
            Assert.IsNull(Panel.Finder, "the viewfinder is away");
            Assert.AreEqual(QrSourceState.Off, _qr.State);
            Assert.IsNotNull(Button(Texts.NewHost));
            Assert.IsNotNull(Button(Known));
            Assert.IsNull(Button("Scan code on your monitor"), "no scan button in it");
            Assert.AreEqual("Scan instead", Panel.ScanButton.Label);
            var reads = _qr.Reads;
            yield return Frames(30);
            Assert.AreEqual(reads, _qr.Reads, "nothing is read in the window");
            Assert.IsNull(Panel.ScanBand);
            foreach (var key in new[] { "1", "3", "5", "7", "9", "2", Texts.Ok })
            {
                Button(key).Click();
                yield return null;
            }
            CollectionAssert.AreEqual(new[] { "ws://" + Known + ":47821" }, _urls);
            _h.Sockets.Single().Open();
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"135792\"}", _h.Sockets.Single().Sent.Single());
            Assert.AreEqual(PairView.Manual, Panel.View, "a typed attempt stays in the window");
            _h.Sockets.Single().Drop(4001);
            yield return Frames(4);
            // "Scan instead" leads back to the viewfinder and scans
            var begins = _qr.Begins;
            _qr.Payloads.Clear();
            Panel.ScanButton.Click();
            yield return Frames(2);
            Assert.AreEqual(PairView.Scan, Panel.View);
            Assert.AreEqual(begins + 1, _qr.Begins);
            Assert.IsNull(Button(Texts.NewHost));
        }

        // ---- the rules of U11 and its fix rounds, in the new flow ----

        [UnityTest]
        public IEnumerator ACodeSeenAsksFirstAndOnlyPairStartsThePairingOnceWithTheParsedValues()
        {
            yield return OpenPairing();
            Assert.AreEqual(1, _qr.Begins, "the tracker starts with the press on Connect to my PC");
            _qr.Payloads.Add(A);
            yield return Frames(3);
            // the question, and nothing else: no connection by itself
            var band = Panel.ScanBand;
            Assert.IsNotNull(band, "a question band");
            Assert.AreEqual(PairView.Ask, Panel.View);
            Assert.AreEqual("Code found", band.CaptionShown);
            Assert.AreEqual("Pair with the PC at " + Address + "?", band.TextShown, "the parsed address, in full");
            Assert.AreEqual("Check that your terminal shows the same address.", band.NoteShown);
            CollectionAssert.AreEqual(new[] { Texts.Pair, Texts.Cancel }, band.Buttons.Select(b => b.Label).ToArray());
            Assert.IsTrue(Panel.Asking);
            Assert.AreEqual(QrSourceState.Off, _qr.State, "nothing is read while the question shows");
            yield return Frames(40);
            Assert.AreEqual(0, _urls.Count, "a code seen never connects by itself");
            Assert.AreEqual(LinkState.Idle, _h.Root.Live.State);
            // a press in the moment the question appears is no answer
            Panel.ScanBand.Buttons[0].Click();
            yield return Frames(2);
            Assert.AreEqual(0, _urls.Count, "the question only just appeared");
            yield return Arm();
            yield return Frames(2);
            // "Pair": the same pairing the number pad starts, once
            Panel.ScanBand.Buttons[0].Click();
            yield return Frames(2);
            CollectionAssert.AreEqual(new[] { "ws://" + Address + ":47821" }, _urls, "one connection, to the address and port of the code");
            Assert.IsFalse(Panel.Scanning, "scan mode is closed while the attempt runs");
            Assert.IsNull(Panel.ScanBand);
            var socket = _h.Sockets.Single();
            socket.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"" + Code + "\"}", socket.Sent.Single(), "the code of the QR code, as the pad would send it");
            yield return Frames(30);
            Assert.AreEqual(1, _urls.Count, "once");
            socket.Receive("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}");
            socket.Receive("{\"type\":\"paired\",\"session\":\"tok\"}");
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current, "paired");
            Assert.IsNull(Panel, "the pairing panel is gone");
            Assert.IsNull(GameObject.Find("PairingViewfinder"), "and its viewfinder");
            Assert.AreEqual(QrSourceState.Off, _qr.State, "and the tracker is stopped");
            var reads = _qr.Reads;
            yield return Frames(20);
            Assert.AreEqual(reads, _qr.Reads, "connected: nothing is read any more");
            // the log: phases only, never the payload
            CollectionAssert.AreEqual(new[] { Ev.Prefix + "qr phase=seen", Ev.Prefix + "qr phase=armed", Ev.Prefix + "qr phase=confirmed" }, QrLines);
            Assert.IsFalse(_h.Lotse.Any(l => l.Contains(Code)), "the code is never logged");
            Assert.IsFalse(_h.Lotse.Any(l => l.Contains("marshal:")), "nor the payload");
        }

        [UnityTest]
        public IEnumerator CancelDoesNothing()
        {
            yield return OpenPairing();
            _qr.Payloads.Add(A);
            yield return Frames(3);
            Assert.IsNotNull(Panel.ScanBand);
            yield return Arm();
            Panel.ScanBand.Buttons[1].Click(); // "Cancel"
            yield return Frames(30);
            Assert.AreEqual(0, _urls.Count, "nothing connects");
            Assert.AreEqual(LinkState.Idle, _h.Root.Live.State);
            AssertAtRest(null, null);
            Assert.AreEqual("Scanning is paused.", Panel.Status);
            var reads = _qr.Reads;
            yield return Frames(20);
            Assert.AreEqual(reads, _qr.Reads, "and nothing is read");
            CollectionAssert.AreEqual(new[] { Ev.Prefix + "qr phase=seen", Ev.Prefix + "qr phase=armed", Ev.Prefix + "qr phase=cancelled" }, QrLines);

            // "Back" on the viewfinder while it scans: to the start panel, the tracker stopped
            _qr.Payloads.Clear();
            yield return PressScan();
            Button(Texts.Back).Click();
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Start, _h.Root.Current);
            Assert.AreEqual(QrSourceState.Off, _qr.State);
            Assert.IsNull(GameObject.Find("PairingViewfinder"));
            Assert.AreEqual(0, _urls.Count);
        }

        [UnityTest]
        public IEnumerator ARefusedScannedCodeRestsWithOneLineAndIsNotAskedAboutAgainByItself()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            yield return OpenPairing();
            Assert.AreEqual(Known, Panel.Host);
            yield return ScanAndPair(A);
            Assert.AreEqual(Known, Panel.Host, "the scanned address is not the manual target while the attempt runs");
            var socket = _h.Sockets.Single();
            socket.Open();
            socket.Drop(4001); // the bridge refuses the code
            yield return Frames(4);
            AssertAtRest(Known, Known);
            Assert.AreEqual(Failed, Panel.Status, "one line");
            var begins = _qr.Begins;
            var reads = _qr.Reads;
            yield return Frames(120); // the same code still in view
            Assert.AreEqual(begins, _qr.Begins, "scanning starts again only on a press");
            Assert.AreEqual(reads, _qr.Reads, "nothing is read");
            Assert.AreEqual(1, _urls.Count, "no retry");
            // hidden and shown again: still no scan
            Panel.gameObject.SetActive(false);
            Assert.IsNull(Panel.Finder, "the viewfinder goes with the panel");
            Panel.gameObject.SetActive(true);
            yield return Frames(10);
            Assert.AreEqual(begins, _qr.Begins, "re-enabled: no scan");
            Assert.IsFalse(Panel.Scanning);
            // the number pad pairs with the address it showed before the scan, not the scanned one
            yield return OpenManual();
            Assert.IsNull(Button(Address), "no button for the scanned address");
            Assert.AreEqual(Texts.NeedCode, Panel.Status, "the window says nothing about the scanned attempt");
            foreach (var key in new[] { "1", "3", "5", "7", "9", "2", Texts.Ok })
            {
                Button(key).Click();
                yield return null;
            }
            Assert.AreEqual("ws://" + Known + ":47821", _urls.Last(), "the typed code goes to the address the user chose");
            Assert.AreEqual(2, _urls.Count);
            Assert.IsFalse(_h.Lotse.Any(l => l.Contains(Code) || l.Contains("135792")), "no code in the log");
        }

        [UnityTest]
        public IEnumerator ScanAgainAfterAFailureScansOnlyOnThatPress()
        {
            yield return OpenPairing();
            yield return ScanAndPair(A);
            _h.Sockets.Single().Open();
            _h.Sockets.Single().Drop(4001);
            yield return Frames(4);
            Assert.AreEqual(PairView.Idle, Panel.View);
            var begins = _qr.Begins;
            _qr.Payloads.Clear();
            _qr.Payloads.Add(B); // a new code is on the screen already
            yield return Frames(60);
            Assert.IsNull(Panel.ScanBand, "not seen: nothing scans");
            Assert.AreEqual(begins, _qr.Begins);
            Panel.ScanButton.Click();
            yield return Frames(3);
            Assert.AreEqual(begins + 1, _qr.Begins, "the press starts it");
            Assert.IsNotNull(Panel.ScanBand, "and the new code is asked about");
            Assert.AreEqual(1, _urls.Count, "still nothing connects by itself");
        }

        [UnityTest]
        public IEnumerator AnUnreachableScannedAddressGetsOneAttemptAndNeverConnectsLater()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            yield return OpenPairing();
            yield return ScanAndPair(A);
            Assert.AreEqual(1, _urls.Count);
            _h.Sockets.Single().Drop(1006); // nobody listens at that address (yet)
            yield return Frames(4);
            Assert.AreNotEqual(LinkState.Retrying, _h.Root.Live.State, "a scanned code is one connection attempt");
            Assert.AreNotEqual(LinkState.Connecting, _h.Root.Live.State);
            AssertAtRest(Known, Known);
            Assert.AreEqual(Failed, Panel.Status);
            // well past every backoff: the host may start answering now, nothing connects to it
            yield return new WaitForSeconds(2.5f);
            yield return Frames(30);
            Assert.AreEqual(1, _urls.Count, "it never connects later");
            Assert.AreNotEqual(LinkState.Connecting, _h.Root.Live.State);
            Assert.AreEqual(AppRoot.Phase.Pairing, _h.Root.Current);
        }

        [UnityTest]
        public IEnumerator ACancelledScanLeavesTheManualAddressAndTheRecentOnesAsTheyWere()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            yield return OpenPairing();
            _qr.Payloads.Add(A);
            yield return Frames(3);
            yield return Arm();
            Panel.ScanBand.Buttons[1].Click();
            yield return Frames(3);
            AssertAtRest(Known, Known);
            Assert.AreEqual(0, _urls.Count);
            yield return OpenManual();
            Assert.IsNull(Button(Address), "no button for the scanned address");
            Assert.IsNotNull(Button(Known));
        }

        [UnityTest]
        public IEnumerator TheAddressIsInNoLogLineDuringAScanAFailedAttemptAndASuccessfulOne()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            yield return OpenPairing();
            // a failed attempt
            yield return ScanAndPair(A);
            _h.Sockets[0].Open();
            _h.Sockets[0].Drop(4001);
            yield return Frames(4);
            // a recent-address button pressed in the window: its label is an address too
            yield return OpenManual();
            Button(Known).Click();
            yield return Frames(2);
            // a successful one
            yield return ScanAndPair(B);
            _h.Sockets[1].Open();
            _h.Sockets[1].Receive("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}");
            _h.Sockets[1].Receive("{\"type\":\"paired\",\"session\":\"tok\"}");
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current);
            Assert.AreEqual(Address, _h.Prefs.Get(HostBook.HostsKey).Split('\n')[0], "paired: now it is a recent address");
            Assert.Greater(_h.Lotse.Count, 5, "there is a log");
            foreach (var line in _h.Lotse)
            {
                StringAssert.DoesNotContain(Address, line);
                StringAssert.DoesNotContain(Known, line);
                StringAssert.DoesNotContain(Code, line);
                StringAssert.DoesNotContain("777777", line);
            }
        }

        [UnityTest]
        public IEnumerator APinchBegunBeforeTheQuestionCountedDoesNotPair()
        {
            yield return OpenPairing();
            yield return new WaitForSecondsRealtime(0.2f);
            // the right hand pinches before the question is there and keeps pinching
            _h.Hands.Right = Hand(1f, rayAt: Panel.Finder.position);
            yield return Frames(2);
            _qr.Payloads.Add(A);
            yield return Frames(3);
            var pair = Panel.ScanBand.Buttons[0];
            Assert.AreEqual(Texts.Pair, pair.Label);
            Assert.IsFalse(pair.Interactable, "the question does not count yet");
            _h.Hands.Right = Hand(1f, rayAt: pair.transform.position); // the ray comes to rest on "Pair", still pinching
            yield return Arm();
            yield return Frames(3);
            Assert.IsTrue(pair.Interactable, "(U13) pressable by time alone, also while a pinch is held");
            _h.Hands.Right = Hand(rayAt: pair.transform.position); // released on "Pair"
            yield return Frames(3);
            _h.Hands.Right = Hand();
            yield return Frames(3);
            Assert.AreEqual(0, _urls.Count, "begun before means ignored");
            Assert.IsNotNull(Panel.ScanBand, "the question still shows");
            Assert.IsTrue(Panel.ScanBand.Buttons[0].Interactable, "and stays pressable: no second wait");
            Assert.IsTrue(_h.Lotse.Any(l => l.Contains("ev qr phase=armed")), "the log says when it armed");
            Assert.IsTrue(_h.Lotse.Any(l => l.Contains("ev qr phase=ignored reason=BegunBefore")), "and which pinch it ignored");
            // a new pinch, begun and released on it, is a press
            yield return _h.RayPinch(Panel.ScanBand.Buttons[0].transform.position);
            yield return Frames(2);
            Assert.AreEqual(1, _urls.Count, "a deliberate click pairs");
        }

        [UnityTest]
        public IEnumerator AFingertipThroughPairWhenTheQuestionAppearsDoesNotPair()
        {
            yield return OpenPairing();
            _qr.Payloads.Add(A);
            yield return Frames(3);
            var pair = Panel.ScanBand.Buttons[0];
            // the finger is already through the button's face while it does not count yet, and stays
            _h.Hands.Right = Hand(tip: pair.transform.TransformPoint(new Vector3(0f, 0f, 0.003f)));
            yield return Arm();
            yield return Frames(5);
            Assert.AreEqual(0, _urls.Count);
            _h.Hands.Right = Hand();
            yield return Frames(2);
            Assert.AreEqual(0, _urls.Count, "pulled away: nothing");
            yield return _h.Poke(Panel.ScanBand.Buttons[0]);
            yield return Frames(2);
            Assert.AreEqual(1, _urls.Count, "a deliberate poke pairs");
        }

        [UnityTest]
        public IEnumerator AHandThatReappearsAlreadyPinchedOnPairDoesNotPair()
        {
            yield return OpenPairing();
            _qr.Payloads.Add(A);
            yield return Frames(3);
            // the right hand is not tracked while the question waits; the left one is there, open
            _h.Hands.Right = Hand(tracked: false);
            yield return Arm();
            yield return Frames(3);
            var pair = Panel.ScanBand.Buttons[0];
            Assert.IsTrue(pair.Interactable, "armed with the one hand that is there");
            // the right hand comes back already pinched, its ray on "Pair", and lets go
            _h.Hands.Right = Hand(1f, rayAt: pair.transform.position);
            yield return Frames(3);
            Assert.IsTrue(pair.Interactable, "(U13) the button stays on; the pinch whose beginning was not seen is ignored");
            _h.Hands.Right = Hand(rayAt: pair.transform.position);
            yield return Frames(3);
            Assert.AreEqual(0, _urls.Count, "no pairing");
            Assert.IsNotNull(Panel.ScanBand);
            Assert.IsTrue(_h.Lotse.Any(l => l.Contains("ev qr phase=ignored reason=Unobserved")), "the log names the case");
            // the same when the input focus was lost and comes back with a pinch
            Assert.IsTrue(Panel.ScanBand.Buttons[0].Interactable);
            _h.Focus = false;
            _h.Hands.Right = Hand(1f, rayAt: pair.transform.position);
            yield return Frames(3);
            _h.Focus = true;
            yield return Frames(3);
            _h.Hands.Right = Hand(rayAt: pair.transform.position);
            yield return Frames(3);
            Assert.AreEqual(0, _urls.Count, "no pairing after the focus came back");
            // then a new click, begun in view, pairs at once: no new wait
            _h.Hands.Right = Hand();
            yield return Frames(3);
            yield return _h.RayPinch(Panel.ScanBand.Buttons[0].transform.position);
            yield return Frames(2);
            Assert.AreEqual(1, _urls.Count);
        }

        [UnityTest]
        public IEnumerator HandsFlickeringInAndOutOfTrackingAfterArmingDoNotSwitchPairOff()
        {
            yield return OpenPairing();
            _qr.Payloads.Add(A);
            yield return Frames(3);
            // air pinches before it arms do not delay it
            for (var i = 0; i < 4; i++)
            {
                _h.Hands.Left = Hand(i % 2 == 0 ? 1f : 0f);
                yield return Frames(1);
            }
            _h.Hands.Left = Hand();
            yield return Arm();
            yield return Frames(2);
            var pair = Panel.ScanBand.Buttons[0];
            Assert.IsTrue(pair.Interactable, "armed 0.6 s after the question appeared");
            // the device log's case: the other hand drifts in and out of view, the pointing hand's tracking flickers
            for (var i = 0; i < 12; i++)
            {
                _h.Hands.Left = Hand(tracked: i % 2 == 0);
                _h.Hands.Right = i % 4 == 3 ? Hand(tracked: false) : Hand(rayAt: pair.transform.position);
                yield return Frames(1);
                Assert.IsTrue(pair.Interactable, "still pressable in frame " + i);
            }
            _h.Hands.Left = Hand(tracked: false);
            yield return _h.RayPinch(pair.transform.position);
            yield return Frames(2);
            Assert.AreEqual(1, _urls.Count, "a pinch begun in view after arming pairs");
        }

        [UnityTest]
        public IEnumerator WithOneHandNeverTrackedTheOtherPairsAfterTheDelay()
        {
            yield return OpenPairing();
            _h.Hands.Left = Hand(tracked: false); // never there
            yield return Frames(2);
            _qr.Payloads.Add(A);
            yield return Frames(3);
            Assert.IsFalse(Panel.ScanBand.Buttons[0].Interactable);
            yield return Arm();
            yield return Frames(3);
            Assert.IsTrue(Panel.ScanBand.Buttons[0].Interactable, "the missing hand does not block it");
            yield return _h.RayPinch(Panel.ScanBand.Buttons[0].transform.position);
            yield return Frames(2);
            Assert.AreEqual(1, _urls.Count, "the one hand pairs");
        }

        [UnityTest]
        public IEnumerator WhileAScannedAttemptRunsOnlyConnectingAndBackShow()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            yield return OpenPairing();
            yield return ScanAndPair(A);
            Assert.AreEqual(LinkState.Connecting, _h.Root.Live.State);
            Assert.AreEqual(PairView.Connecting, Panel.View);
            Assert.AreEqual(Texts.Connecting, Panel.Status);
            Assert.IsNull(Panel.transform.Find("Content"), "no window: no address line, no pad");
            Assert.IsNull(Button(Known), "no address buttons");
            Assert.IsNull(Button(Texts.NewHost));
            Assert.IsNull(Button("1"), "no number pad");
            Assert.IsNull(Panel.ScanButton);
            Assert.IsNull(Panel.TypeButton);
            CollectionAssert.AreEqual(new[] { Texts.Back }, Panel.Finder.GetComponentsInChildren<PokeButton>().Select(b => b.Label).ToArray(), "only Back");
            foreach (var t in Panel.Finder.GetComponentsInChildren<TMPro.TextMeshPro>())
            {
                StringAssert.DoesNotContain(Known, t.text);
                StringAssert.DoesNotContain(Address, t.text);
            }
            // it fails: the viewfinder at rest, nothing adopted
            _h.Sockets.Single().Drop(1006);
            yield return Frames(4);
            AssertAtRest(Known, Known);
        }

        [UnityTest]
        public IEnumerator PauseOrHidingWhileTheQuestionShowsClosesItAndStopsTheTracker()
        {
            yield return OpenPairing();
            _qr.Payloads.Add(A);
            yield return Frames(3);
            Assert.IsNotNull(Panel.ScanBand);
            Panel.Paused();
            yield return Frames(2);
            Assert.IsNull(Panel.ScanBand, "paused: the question is gone");
            AssertAtRest(null, null);
            var begins = _qr.Begins;
            yield return Frames(30);
            Assert.AreEqual(begins, _qr.Begins, "nothing scans by itself afterwards");
            Assert.AreEqual(0, _urls.Count);
            // the same when the panel is hidden while the question shows
            yield return PressScan();
            yield return Frames(2);
            Assert.IsNotNull(Panel.ScanBand);
            begins = _qr.Begins;
            Panel.gameObject.SetActive(false);
            Assert.AreEqual(QrSourceState.Off, _qr.State);
            Panel.gameObject.SetActive(true);
            yield return Frames(30);
            Assert.IsNull(Panel.ScanBand, "re-enabled: no question");
            Assert.IsFalse(Panel.Scanning, "and no scan");
            Assert.AreEqual(begins, _qr.Begins);
            Assert.AreEqual(0, _urls.Count);
        }

        [UnityTest]
        public IEnumerator OutsideScanModeNothingIsReadAndNothingHappens()
        {
            yield return OpenPairing();
            // the window: a pairing code in view the whole time, and nothing reads it
            yield return OpenManual();
            var begins = _qr.Begins;
            var reads = _qr.Reads;
            _qr.Payloads.Add(A);
            yield return Frames(60);
            Assert.AreEqual(begins, _qr.Begins, "the tracker never starts by itself");
            Assert.AreEqual(reads, _qr.Reads, "nothing is read");
            Assert.IsNull(Panel.ScanBand, "no question");
            Assert.AreEqual(0, _urls.Count);
            Assert.IsFalse(QrLines.Any(l => l.EndsWith("phase=seen")));
            // the app pauses in scan mode: scan mode closes, the tracker stops
            _qr.Payloads.Clear();
            yield return PressScan();
            Panel.Paused();
            yield return Frames(2);
            Assert.IsFalse(Panel.Scanning, "paused: scan mode is closed");
            Assert.AreEqual(QrSourceState.Off, _qr.State);
            reads = _qr.Reads;
            _qr.Payloads.Add(A);
            yield return Frames(30);
            Assert.AreEqual(reads, _qr.Reads);
            Assert.IsNull(Panel.ScanBand);
            // the panel is hidden (the window is placed again): the same
            _qr.Payloads.Clear();
            yield return PressScan();
            Panel.gameObject.SetActive(false);
            Assert.AreEqual(QrSourceState.Off, _qr.State, "hidden: the tracker stops");
            Panel.gameObject.SetActive(true);
            yield return Frames(2);
            Assert.IsFalse(Panel.Scanning);
            Assert.AreEqual(PairView.Idle, Panel.View);
            Assert.AreEqual(0, _urls.Count);
        }

        [UnityTest]
        public IEnumerator ACodeForAnotherPortOrAnyOtherQrCodeIsIgnoredSilently()
        {
            yield return OpenPairing();
            _qr.Payloads.Add(Encoding.UTF8.GetBytes("https://example.com/"));
            _qr.Payloads.Add(Encoding.UTF8.GetBytes("marshal://pair?v=1&h=8.8.8.8&p=47821&c=481216"));
            _qr.Payloads.Add(Encoding.UTF8.GetBytes("marshal://pair?v=1&h=192.168.1.197&p=47822&c=481216"));
            _qr.Payloads.Add(new byte[] { 0xFF, 0x00, 0x41 });
            yield return Frames(60);
            Assert.IsNull(Panel.ScanBand, "no question");
            Assert.IsTrue(Panel.Scanning, "still looking");
            Assert.AreEqual(Texts.ScanInstruction, Panel.ScanInstructionShown);
            Assert.AreEqual(0, _urls.Count);
            Assert.AreEqual(4, QrLines.Count, "one line per code, no more");
            Assert.IsTrue(QrLines.All(l => l == Ev.Prefix + "qr phase=ignored"));
            Assert.IsFalse(_h.Lotse.Any(l => l.Contains("example") || l.Contains("8.8.8.8")), "nothing of a payload in the log");
        }

        [UnityTest]
        public IEnumerator WithoutThePermissionTheWindowShowsWithOneLineAndWorksAsBefore()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            _qr.OnBegin = QrSourceState.Denied;
            _qr.Payloads.Add(A);
            yield return OpenPairing();
            yield return Frames(2);
            Assert.AreEqual(1, _qr.Begins, "asked when Connect to my PC is pressed");
            Assert.IsFalse(Panel.Scanning, "scan mode closes");
            Assert.AreEqual(PairView.Manual, Panel.View, "the window");
            Assert.IsNull(Panel.Finder);
            Assert.AreEqual("Scanning needs the Spatial data permission. Enter the code instead.", Panel.ScanNote);
            CollectionAssert.Contains(Panel.GetComponentsInChildren<TMPro.TextMeshPro>().Select(t => t.text).ToList(), Panel.ScanNote);
            Assert.IsNull(Panel.ScanBand);
            Assert.AreEqual(0, _urls.Count);
            CollectionAssert.AreEqual(new[] { Ev.Prefix + "qr phase=denied" }, QrLines);
            // manual entry, unchanged
            foreach (var key in new[] { "1", "3", "5", "7", "9", "2", Texts.Ok })
            {
                Button(key).Click();
                yield return null;
            }
            CollectionAssert.AreEqual(new[] { "ws://" + Known + ":47821" }, _urls);
            _h.Sockets.Single().Open();
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"135792\"}", _h.Sockets.Single().Sent.Single());
        }

        [UnityTest]
        public IEnumerator WhereTrackingIsNotAvailableTheWindowShowsWithTheSameLine()
        {
            _qr.OnBegin = QrSourceState.Unavailable;
            yield return OpenPairing();
            yield return Frames(2);
            Assert.IsFalse(Panel.Scanning);
            Assert.AreEqual(PairView.Manual, Panel.View);
            Assert.AreEqual(Texts.ScanDenied, Panel.ScanNote);
            Assert.IsNotNull(Button(Texts.NewHost), "manual entry is there");
        }

        [UnityTest]
        public IEnumerator AStoredSessionGetsOneShortAttemptThenScanningStartsBecauseThePressOnConnectIsTheRequestToScan()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            _h.Prefs.Set(HostBook.SessionPrefix + Known, "tok");
            yield return OpenPairing(); // the stored session is tried first
            Assert.AreEqual(LinkState.Connecting, _h.Root.Live.State);
            Assert.AreEqual(PairView.Connecting, Panel.View);
            Assert.AreEqual("Looking for your PC…", Panel.Status, "not Connecting, and nothing about an address");
            Assert.IsNotNull(Panel.Brackets, "the brackets are there already");
            Assert.AreEqual(0, _qr.Begins, "nothing scans while the attempt runs");
            Assert.IsNull(Panel.ScanButton);
            CollectionAssert.AreEqual(new[] { Texts.Back }, Panel.Finder.GetComponentsInChildren<PokeButton>().Select(b => b.Label).ToArray());
            Assert.AreEqual(1, _urls.Count, "the one attempt");
            // nobody answers at the stored address (an old one)
            _h.Sockets[0].Drop(1006);
            yield return Frames(4);
            Assert.AreEqual(PairView.Scan, Panel.View, "scanning at once: no press needed, the user asked for it with Connect to my PC");
            Assert.IsTrue(Panel.Scanning);
            Assert.AreEqual(1, _qr.Begins);
            Assert.AreNotEqual(LinkState.Retrying, _h.Root.Live.State);
            foreach (var t in Panel.Finder.GetComponentsInChildren<TMPro.TextMeshPro>())
            {
                StringAssert.DoesNotContain("not reachable", t.text);
                StringAssert.DoesNotContain("Trying again", t.text);
            }
            // well past every backoff: no further attempt to the stored address
            yield return new WaitForSeconds(2.5f);
            yield return Frames(30);
            Assert.AreEqual(1, _urls.Count, "no further attempt to the stored address");
            Assert.IsTrue(Panel.Scanning);
            // the scanner works from here as always: a code asks, nothing connects by itself
            _qr.Payloads.Add(A);
            yield return Frames(3);
            Assert.IsNotNull(Panel.ScanBand);
            Assert.AreEqual(1, _urls.Count);
            // the device log says which path ran, without an address
            CollectionAssert.Contains(_h.Lotse, Ev.Prefix + "link phase=resume reason=Stored");
            CollectionAssert.Contains(_h.Lotse, Ev.Prefix + "link phase=resume-failed reason=Closed");
            CollectionAssert.Contains(_h.Lotse, Ev.Prefix + "link phase=scan reason=AfterResume");
            Assert.IsFalse(_h.Lotse.Any(l => l.Contains(Known)));
        }

        [UnityTest]
        public IEnumerator AStoredSessionThatDoesNotAnswerWithinThreeSecondsIsDroppedAndScanningStarts()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            _h.Prefs.Set(HostBook.SessionPrefix + Known, "tok");
            yield return OpenPairing();
            Assert.AreEqual(PairView.Connecting, Panel.View);
            yield return new WaitForSeconds(1.5f);
            Assert.AreEqual(PairView.Connecting, Panel.View, "still inside its three seconds");
            yield return new WaitForSeconds(2.0f);
            yield return Frames(4);
            Assert.AreEqual(PairView.Scan, Panel.View);
            Assert.AreEqual(1, _qr.Begins);
            Assert.AreEqual(1, _urls.Count);
            CollectionAssert.Contains(_h.Lotse, Ev.Prefix + "link phase=resume-failed reason=Timeout");
        }

        [UnityTest]
        public IEnumerator AStoredSessionThatAnswersGoesLiveWithoutScanning()
        {
            _h.Prefs.Set(HostBook.HostsKey, Known);
            _h.Prefs.Set(HostBook.SessionPrefix + Known, "tok");
            _qr.Payloads.Add(A);
            yield return OpenPairing();
            _h.Sockets[0].Open();
            _h.Sockets[0].Receive("{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}");
            yield return Frames(3);
            Assert.AreEqual(AppRoot.Phase.Playing, _h.Root.Current);
            Assert.IsNull(Panel);
            Assert.AreEqual(0, _qr.Begins, "nothing was scanned");
            Assert.AreEqual(0, _qr.Reads);
            CollectionAssert.Contains(_h.Lotse, Ev.Prefix + "link phase=live reason=Stored");
            // it drops later: the usual reconnect, without limit, and no scanner
            _h.Sockets[0].Drop(1006);
            yield return new WaitForSeconds(1.3f);
            yield return Frames(3);
            Assert.AreEqual(2, _urls.Count, "reconnecting");
            _h.Sockets[1].Drop(1006);
            yield return new WaitForSeconds(2.3f);
            yield return Frames(3);
            Assert.AreEqual(3, _urls.Count, "and again");
            Assert.AreEqual(0, _qr.Begins);
        }

        [Test]
        public void ASocketFailureIsLoggedByKindNeverByItsText()
        {
            Assert.AreEqual("SocketException ConnectionRefused", NativeLinkSocket.Kind(new System.Net.Sockets.SocketException(10061)));
            var wrapped = new System.AggregateException(new System.Exception("192.168.1.9:47821 did not answer", new System.Net.Sockets.SocketException(10060)));
            Assert.AreEqual("SocketException TimedOut", NativeLinkSocket.Kind(wrapped));
            Assert.AreEqual("SocketException HostUnreachable", NativeLinkSocket.Kind(new System.IO.IOException("x", new System.Net.Sockets.SocketException(10065))));
            Assert.AreEqual("InvalidOperationException", NativeLinkSocket.Kind(new System.InvalidOperationException("ws://192.168.1.9 is closed")));
            Assert.AreEqual("unknown", NativeLinkSocket.Kind(null));
            foreach (var e in new System.Exception[] { wrapped, new System.InvalidOperationException("ws://192.168.1.9 is closed") })
                StringAssert.DoesNotContain("192.168", NativeLinkSocket.Kind(e));
        }

        [UnityTest]
        public IEnumerator BothHintsNameTheCommandLotseConnect()
        {
            yield return OpenPairing();
            var texts = Panel.Finder.GetComponentsInChildren<TMPro.TextMeshPro>().Select(t => t.GetParsedText()).ToList();
            CollectionAssert.Contains(texts, "Run  lotse connect  on your PC to show it");
            yield return OpenManual();
            var hint = Panel.transform.Find("Content/Hint").GetComponent<TMPro.TextMeshPro>().text;
            StringAssert.Contains("lotse connect", hint);
            StringAssert.DoesNotContain("lotse start", hint);
        }
    }
}
