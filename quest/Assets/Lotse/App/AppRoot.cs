using System.Collections;
using System.Collections.Generic;
using System.IO;
using Lotse.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Lotse.App
{
    /// <summary>
    /// The app (U5a: one calm workspace). Loads the jury pack from StreamingAssets, places the anchor (on the desk the
    /// user taps, see DeskPlacer; in front of the head as the fallback), then stands everything in one place, the
    /// workspace's (WorkspaceDock: 0.42 m right of the desk anchor, 12 cm above the desk, facing the user; without a desk
    /// 0.75 m in front of the head): first the start panel, the pairing panel or the end card, then the workspace window
    /// itself. Nothing else spawns in the room. A DemoSession runs every frame (the offline pack, "Demo ansehen", or the
    /// live bridge, "Mit meinem PC verbinden"); the workspace mirrors it (WorkspaceController): the agent list, the
    /// selected agent's conversation, its inline approval block, the notice pill. Nothing opens by itself.
    /// The block is the only thing a gesture can approve, and only while the conversation is on screen: the settings in
    /// front of it, placing, pairing or the end card hide it (DemoSession.SetViewShown). Buttons and rows are poked, or
    /// clicked with the hand ray and a short pinch (RayPointer, also while a block is shown); a pinch that began on a
    /// target never reaches the hold (RayClickLogic masks it), so a click is never an approval. "Neu platzieren" and "Einstellungen" are in the wrist menu (an
    /// upturned palm); the settings hold the connection ("Trennen", "Neu koppeln"), the sound and "Ruhig". Nothing moves
    /// in "Ruhig" or while a hold runs (Theme.Still). Hands only.
    /// </summary>
    public sealed class AppRoot : MonoBehaviour
    {
        public enum Phase { Loading, Start, Pairing, Playing, End }

        const string DefaultPackFile = "demo/jury.pack.json";
        // head-relative anchor (before and instead of desk placement): in front of and below the eyes
        const float AnchorForward = 0.6f;
        const float AnchorBelowEyes = 0.25f;
        // on the desk the content root sits this far above the tapped point
        const float DeskLift = 0.08f;
        // the settings stand this far in front of the workspace
        const float SettingsInFront = 0.03f;

        string _packJson;
        Transform _anchor;
        // where the workspace stands (its centre, facing the user, unscaled): every panel stands here
        Transform _stage;
        Vector3 _dockLocalCenter;
        Vector3 _dockLocalForward;
        bool _docked;
        GameObject _startPanel;
        GameObject _endCard;
        Banner _banner;
        WristMenu _menu;
        RayPointer _pointer;
        WorkspaceView _workspace;
        WorkspaceController _ctl;
        ReplyController _reply;
        SettingsPanel _settings;
        LookSettings _look;
        bool _confirmRepair;
        bool _hadFocus = true;
        HostBook _hosts;
        LiveSource _live;
        PairingPanel _pairing;
        bool _viaStored;
        bool _liveSession;
        DeskPlacer _placer;
        Transform _deskAnchor;
        string _lastCardLog;
        // docking to a display (U5b): the setup, its view, what is kept, the display's anchor and the side
        readonly ScreenSetupFlow _setup = new ScreenSetupFlow();
        // halt-all (U7): the stop gesture, fed from the same hand frames as everything else
        readonly StopGesture _stop = new StopGesture();
        // the window's distance and size from the header's Menu (U8), kept in prefs
        WindowRecord _windowRecord;
        bool _menuWasOpen;
        ScreenSetupView _setupView;
        ScreenRecord _screenRecord;
        IDeskAnchors _screenAnchors;
        Transform _screenAnchor;
        float _screenWidth;
        float _screenHeight;
        DockSide _screenSide = DockSide.Right;
        // U10: where the head was at setup time (in the display anchor's frame) and the docked window's distance from there
        Vector3? _screenHeadLocal;
        float _screenDistance;
        float _dockBandUntil;
        public const string DockTag = "dock";
        /// <summary>The band after docking ("Docked right of your display") closes by itself after this long without input.</summary>
        public const float DockBandSeconds = 10f;

        /// <summary>The band that says where the window docked, while it shows (U10).</summary>
        public WindowBand DockBand => _workspace != null && _workspace.Question.IsShown && _workspace.Question.Tag == DockTag ? _workspace.Question : null;
        bool _offerPending;
        // the device log (U9): one line per event, see Ev
        readonly PinchLog _pinchLeft = new PinchLog("L");
        readonly PinchLog _pinchRight = new PinchLog("R");
        readonly HoldLog _holdLog = new HoldLog();
        readonly PerfLog _perf = new PerfLog();
        readonly EvRate _stopRate = new EvRate(2);
        ScreenName? _screenLogged;
        (WorkspaceTab, bool)? _modeLogged;
        bool _allowedThisFrame;
        string _holdCard;
        float _stopHeld;
        // the guided test (U9)
        readonly GuidedTest _test = new GuidedTest();
        GuidedSource _guided;
        TestNeed _testNeed;
        bool _startingTest;
        public const string TestTag = "test";
        // the tour (U14): the guided test's machinery for a stranger
        readonly Tour _tour = new Tour();
        int _tourShown = -1;
        public const string TourTag = "tour";
        /// <summary>The width of the tour band's links ("Skip", "Again") and of its "Done".</summary>
        public const float TourLinkWidth = UiMetrics.ButtonWidth;

        /// <summary>The tour: eight steps in the band along the window's bottom edge, each done by the user's own hands.</summary>
        public Tour Tour => _tour;

        /// <summary>The guided test: what to try, step by step, in the band along the window's bottom edge.</summary>
        public GuidedTest Test => _test;
        /// <summary>The guided test's own request source while it runs, or null.</summary>
        public GuidedSource Guided => _guided;
        public const string OfferTag = "offer";
        /// <summary>Restoring a saved display gives up after this long (the anchor did not localise).</summary>
        const float ScreenLoadSeconds = 5f;

        /// <summary>Hand input; set before Start to replace the Quest hands (tests).</summary>
        public IHandSource Hands { get; set; }

        /// <summary>The head (centre eye); defaults to the main camera.</summary>
        public Transform Head { get; set; }

        /// <summary>Whether the head pose is tracked; defaults to the XR head node. Tests replace it.</summary>
        public System.Func<bool> HeadTracked { get; set; }

        /// <summary>Opens one bridge connection per attempt; defaults to NativeWebSocket. Tests replace it.</summary>
        public System.Func<string, ILinkSocket> SocketFactory { get; set; }

        /// <summary>Where addresses, session tokens and the desk anchor's uuid are kept; defaults to PlayerPrefs. Tests replace it.</summary>
        public IKeyValueStore Prefs { get; set; }

        /// <summary>Raycasts against the room for desk placement; defaults to MRUK's EnvironmentRaycast. Tests replace it.</summary>
        public IDeskRaycaster Raycaster { get; set; }
        /// <summary>Reads QR codes for pairing (U11); defaults to MR Utility Kit's trackables. Tests replace it.</summary>
        public IQrSource QrSource { get; set; }

        /// <summary>The persisted desk anchor; defaults to OVRSpatialAnchor. Tests replace it.</summary>
        public IDeskAnchors DeskAnchors { get; set; }

        /// <summary>The persisted display anchor (U5b, at the display's centre); defaults to OVRSpatialAnchor. Tests replace it.</summary>
        public IDeskAnchors ScreenAnchors { get; set; }

        /// <summary>The display setup (three taps, then the side).</summary>
        public ScreenSetupFlow Setup => _setup;
        /// <summary>The stop gesture (both open hands, palms away, 1 s): it halts every agent.</summary>
        public StopGesture Stop => _stop;
        public ScreenSetupView SetupView => _setupView;
        /// <summary>The workspace stands in a slot next to a known display.</summary>
        public bool OnScreen => _screenAnchor != null;
        public DockSide ScreenSide => _screenSide;
        /// <summary>The offer after the first desk placement ("Put Marshal next to a display?"), while it shows.</summary>
        public WindowBand ScreenOffer => _workspace != null && _workspace.Question.IsShown && _workspace.Question.Tag == OfferTag ? _workspace.Question : null;
        /// <summary>The offer waits for the window to be on screen (it is asked in the window's band, U9).</summary>
        public bool ScreenOfferPending => _offerPending;

        /// <summary>The demo pack under StreamingAssets; tests point it at a missing file.</summary>
        public string PackFile { get; set; } = DefaultPackFile;

        public Phase Current { get; private set; } = Phase.Loading;
        public DemoSession Session { get; private set; }
        public Transform Anchor => _anchor;
        public string EndSummary { get; private set; }
        public LiveSource Live => _live;
        public PairingPanel Pairing => _pairing;
        public Banner Banner => _banner;
        public DeskPlacer Placer => _placer;
        /// <summary>The anchor follows a spatial anchor on the desk (not the head-relative fallback).</summary>
        public bool OnDesk => _deskAnchor != null;
        /// <summary>The wrist menu ("Neu platzieren", "Einstellungen").</summary>
        public WristMenu Menu => _menu;
        /// <summary>The hand rays.</summary>
        public RayPointer Pointer => _pointer;
        /// <summary>The workspace window (U5a).</summary>
        public WorkspaceView Workspace => _workspace;
        /// <summary>The workspace's wiring: the list, the selection, the feed, the block.</summary>
        public WorkspaceController Desktop => _ctl;
        /// <summary>The agent whose conversation is shown, or null.</summary>
        public string SelectedAgent => _ctl?.Selected;
        /// <summary>Where the workspace stands and how big it is.</summary>
        public WorkspaceDock Dock => _workspace != null ? _workspace.Dock : default;
        /// <summary>The desk keyboard.</summary>
        public DeskKeyboard Desk => _reply.Desk;
        /// <summary>Replying to an agent (its input bar and state).</summary>
        public ReplyController Reply => _reply;
        /// <summary>"Einstellungen": connection, sound, "Ruhig".</summary>
        public SettingsPanel Settings => _settings;

        /// <summary>Whether the app has the input focus (OVRManager.hasInputFocus on the Quest; tests replace it).</summary>
        public System.Func<bool> InputFocus { get; set; }

        /// <summary>The keyboard for replies; defaults to the Quest system keyboard. Tests replace it.</summary>
        public IReplyKeyboard Keyboard { get; set; }

        IEnumerator Start()
        {
            Ev.Sink = Debug.Log;
            Sounds.Reset();
            Sounds.Warm();
            _test.StepBegan += OnTestStep;
            _tour.StepBegan += OnTourStep;
            _tour.StepDone += OnTourStepDone;
            if (Head == null && Camera.main != null) Head = Camera.main.transform;
            if (Head == null) Head = new GameObject("Head (fallback)").transform;
            // sharp text on the headset: render scale, no foveation, no dynamic resolution, no post-processing (logged)
            DisplayQuality.Apply(Head.GetComponent<Camera>());
            if (Hands == null) Hands = new OvrHandSource();
            if (HeadTracked == null) HeadTracked = XrHeadTracked;
            // the Quest's own hands lose their input while the system keyboard or menu holds the focus
            if (InputFocus == null) InputFocus = Hands is OvrHandSource ? (System.Func<bool>)(() => OVRManager.hasInputFocus) : () => true;
            if (Prefs == null) Prefs = new PrefsStore();
            // the sound and "Ruhig" from the last run, before anything is built
            _look = new LookSettings(Prefs);
            Chime.On = _look.Sound;
            Theme.Calm = _look.Calm;

            _anchor = new GameObject("LotseAnchor").transform;
            _stage = Ui.Child(_anchor, "Stage", Vector3.zero).transform;
            _banner = Banner.Create(_stage, new Vector3(0f, -WorkspaceView.BaseHeight * 0.5f - Theme.Unit, -0.01f));
            _settings = SettingsPanel.Create(_stage);
            _settings.transform.localPosition = new Vector3(0f, 0f, -SettingsInFront);
            _menu = WristMenu.Create(Replace, OpenSettings, BeginScreenSetup, () => TriggerHalt("wrist menu"));
            _screenRecord = new ScreenRecord(Prefs);
            _screenAnchors = ScreenAnchors ?? new SpatialDeskAnchors(Prefs, ScreenRecord.AnchorKey, "LotseScreenAnchor");
            _setupView = ScreenSetupView.Create(() => _setup.Back(), CancelScreenSetup, side => _setup.Pick(side));
            _pointer = RayPointer.Create();
            _reply = new ReplyController(Keyboard ?? new SystemKeyboard(), DeskKeyboard.Create(transform));
            // the pill, a column of the overview and an orb of the rail open that agent's conversation (U6)
            _workspace = WorkspaceView.Create(_anchor, sid => _ctl.OpenRow(sid), OnDeny, OnLater, i => _ctl.OnScroll(i), () => _ctl.OnScrollToEnd(),
                sid => _ctl.OpenAgent(sid), () => _ctl.CloseRow(), () => _ctl.Collapse(),
                () => _ctl.PreviousPage(), () => _ctl.NextPage(), () => _ctl.ExpandFromRail(), sid => _ctl.OpenAgent(sid));
            _ctl = new WorkspaceController(_workspace, _reply) { AutoCollapse = _look.AutoCollapse };
            _workspace.MenuPressed = OnMenuPressed;
            _workspace.MenuItem = OnMenuItem;
            _windowRecord = new WindowRecord(Prefs);
            // what the agent waits for, only while it waits now (the same state its row shows): a detail can be older
            _reply.WaitingFor = sid => WaitsNow(sid) ? _ctl.Details.Get(sid)?.WaitingFor : null;
            _reply.KeyboardPose = DeskKeyboardPose;

            // without a usable pack the app still starts: live mode works, the demo button says it is off
            yield return LoadPack();

            // capture once the head pose is tracked and still (or after 3 s)
            var watch = new AnchorWatch();
            while (!watch.Ready(Time.unscaledTimeAsDouble * 1000.0, Ui.ToVec(Head.position), HeadTracked()))
                yield return null;
            PlaceAnchor();

            // then onto the desk: the saved anchor, or a tap on the desk; the head-relative anchor as the fallback
            var raycaster = Raycaster ?? new MrukDeskRaycaster();
            _placer = new DeskPlacer(raycaster, DeskAnchors ?? new SpatialDeskAnchors(Prefs), Head, PlacementView.Create(transform));
            _placer.Finished += OnPlaced;
            _placer.Begin(Time.timeAsDouble * 1000.0, trySaved: true);
            while (_placer.Active) yield return null;

            // a display docked in an earlier run: its anchor, else the default pose and a banner
            yield return RestoreScreen();
            ShowStart();
            // offered once, after the first desk placement: a question in the window's band, once the window is on screen
            if (OnDesk && !OnScreen && !_screenRecord.Offered) _offerPending = true;
        }

        // ---- docking to a display (U5b) ----

        IEnumerator RestoreScreen()
        {
            if (!_screenRecord.TryLoad(out var w, out var h, out var side, out var headLocal, out var kept) || !_screenAnchors.HasSaved) yield break;
            if (!headLocal.HasValue)
            {
                // (U12) a dock saved by an older build has no seat: it would put the window in the display's plane, at the
                // display's distance. Not docked: the free window at its default distance.
                Ev.Emit("dock", ("phase", "rejected"), ("reason", DockReject.NoSeat));
                Debug.Log("[Lotse] the saved dock has no seat (an older build's): not used");
                yield break;
            }
            Transform found = null;
            var done = false;
            _screenAnchors.LoadSaved(t =>
            {
                if (done)
                {
                    if (t != null) Destroy(t.gameObject); // too late: not used
                    return;
                }
                found = t;
                done = true;
            });
            var until = Time.realtimeSinceStartup + ScreenLoadSeconds;
            while (!done && Time.realtimeSinceStartup < until) yield return null;
            done = true;
            if (found == null)
            {
                Debug.Log("[Lotse] the saved display was not found: default pose");
                _banner.Show(Texts.ScreenLost, Theme.BannerBad, 8f);
                yield break;
            }
            DockToScreen(found, w, h, side, headLocal.HasValue ? Ui.ToVector(headLocal.Value) : (Vector3?)null, kept);
        }

        /// <summary>
        /// "Put Marshal next to a display?" as a band inside the window, under the header (U9: no second window above the
        /// main one). While it shows nothing can be approved, exactly as with the menu open.
        /// </summary>
        void ShowScreenOffer()
        {
            _offerPending = false;
            _screenRecord.Offered = true;
            _workspace.Menu.Hide();
            _workspace.Question.Show(OfferTag, null, Texts.ScreenOffer, true,
                (Texts.Yes, () => { RemoveOffer(); BeginScreenSetup(); }), (Texts.ScreenOfferLater, RemoveOffer));
            _workspace.Refresh();
            Debug.Log("[Lotse] display offer shown");
        }

        void RemoveOffer()
        {
            if (ScreenOffer == null) return;
            _workspace.Question.Hide();
            _workspace.Refresh();
        }

        /// <summary>Any question band goes (a new session, the end): an unanswered offer is asked again later.</summary>
        void DropQuestion()
        {
            if (_workspace == null || !_workspace.Question.IsShown) return;
            if (ScreenOffer != null) _offerPending = true;
            _workspace.Question.Hide();
            _workspace.Refresh();
        }

        /// <summary>
        /// "Set up display" (wrist menu) or "Yes" on the offer: three taps, then the side. The workspace is hidden meanwhile
        /// and nothing can be approved (a running hold is cancelled at once); the previous dock stays until it is done.
        /// </summary>
        void BeginScreenSetup()
        {
            if (_placer != null && _placer.Active) return;
            CloseSettings();
            RemoveOffer();
            _offerPending = false; // the user is setting it up: the question is answered
            _setup.Begin();
            _setupView.PlacePanel(Head.position, Head.forward);
            Session?.SetViewShown(false); // the block goes at once, a hold with it
            Debug.Log("[Lotse] display setup started");
        }

        void CancelScreenSetup()
        {
            _setup.Cancel();
            EndScreenSetup("cancelled: the previous dock stays");
        }

        void EndScreenSetup(string how)
        {
            _setupView.Hide();
            Debug.Log("[Lotse] display setup " + how);
        }

        /// <summary>The setup's frame: the dwell taps (fingertips near a button do not count), the view, the result.</summary>
        void StepScreenSetup(double nowMs, in HandFrame left, in HandFrame right)
        {
            var ev = _setup.Step(nowMs, Tip(left), Tip(right), Ui.ToVec(Head.position));
            if (ev == SetupEvent.Tapped)
            {
                Debug.Log("[Lotse] display corner " + _setup.Points.Count + " tapped");
                Ev.Emit("dock", ("phase", "tap"), ("n", _setup.Points.Count));
            }
            if (ev == SetupEvent.Rejected)
            {
                Debug.Log("[Lotse] display rejected (" + _setup.LastRejection + "): asking again");
                Ev.Emit("dock", ("phase", "rejected"), ("reason", _setup.LastRejection));
            }
            if (ev == SetupEvent.Measured) Debug.Log("[Lotse] " + _setup.Screen);
            switch (_setup.Phase)
            {
                case SetupPhase.Corners:
                    var prompt = _setup.LastRejection != ScreenRejection.None && _setup.Points.Count == 0
                        ? Texts.ScreenRejected + " " + Texts.ScreenCorner(0) : Texts.ScreenCorner(_setup.Points.Count);
                    _setupView.ShowCorners(prompt, _setup.Points, _setup.Progress, _setup.RingAt, Head.position);
                    break;
                case SetupPhase.Side:
                    // U10: no chooser. The dock is made at once on the side where the window stands now, as seen from
                    // the head (right when unclear, never an unavailable slot); a band then offers the other sides.
                    _setup.Pick(DockSlots.SideOf(_setup.Screen, DeskY, Ui.ToVec(Head.position), _workspace.Dock.Center));
                    FinishScreenSetup();
                    break;
                case SetupPhase.Done:
                    FinishScreenSetup();
                    break;
            }
        }

        /// <summary>The desk surface's height when a desk anchor exists (slots below it are unavailable).</summary>
        float? DeskY => _deskAnchor != null ? _deskAnchor.position.y : (float?)null;

        /// <summary>A fingertip for the dwell, or null: untracked, or near a button (a poke is no corner).</summary>
        static Vec3? Tip(in HandFrame f)
        {
            if (!f.TipValid || !f.Sample.Usable) return null;
            var all = PokeButton.All;
            for (var i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null || !b.isActiveAndEnabled) continue;
                if (Vector3.Distance(f.Tip, b.transform.position) < Mathf.Max(b.Size.x, b.Size.y) * 0.5f + 0.04f) return null;
            }
            return Ui.ToVec(f.Tip);
        }

        void FinishScreenSetup()
        {
            var s = _setup.Screen;
            var anchor = _screenAnchors.Create(Ui.ToVector(s.Center), Quaternion.LookRotation(Ui.ToVector(s.Forward), Ui.ToVector(s.Up)));
            // the seat: where the head is now, kept in the display's own frame; the window stands on the rays from there
            var headLocal = Quaternion.Inverse(Quaternion.LookRotation(Ui.ToVector(s.Forward), Ui.ToVector(s.Up))) * (Head.position - Ui.ToVector(s.Center));
            var distance = DockSlots.DefaultDistance(s, Ui.ToVec(Head.position));
            _screenRecord.Save(s.Width, s.Height, _setup.Side, Ui.ToVec(headLocal), distance);
            DockToScreen(anchor, s.Width, s.Height, _setup.Side, headLocal, distance);
            Ev.Emit("dock", ("phase", "saved"), ("side", _setup.Side.ToString().ToLowerInvariant()));
            _setup.Close();
            EndScreenSetup("done: docked " + _setup.Side);
            ShowDockBand();
        }

        /// <summary>
        /// "Docked right of your display" with "Other side" and "Done", in the window's band (U10). It follows the band
        /// rule (nothing can be approved while it shows) and closes by "Done" or after 10 s without input.
        /// </summary>
        void ShowDockBand()
        {
            _workspace.Menu.Hide();
            _workspace.Question.Show(DockTag, null, Texts.Docked(_screenSide), false, (Texts.OtherSide, OtherSide), (Texts.Done, CloseDockBand));
            _workspace.Refresh();
            _dockBandUntil = Time.time + DockBandSeconds;
        }

        void CloseDockBand()
        {
            if (DockBand == null) return;
            _workspace.Question.Hide();
            _workspace.Refresh();
        }

        /// <summary>"Other side": the next available side of the display, docked there at once and kept.</summary>
        void OtherSide()
        {
            if (_screenAnchor == null)
            {
                CloseDockBand();
                return;
            }
            var side = _screenHeadLocal.HasValue
                ? DockSlots.Next(Screen.Value, DeskY, _screenSide, Ui.ToVec(_screenAnchor.TransformPoint(_screenHeadLocal.Value)), _screenDistance)
                : DockSlots.Next(Screen.Value, DeskY, _screenSide);
            _screenSide = side;
            _workspace.RailOnRight = DockSlots.RailOnRight(side);
            _screenRecord.Save(_screenWidth, _screenHeight, side, _screenHeadLocal.HasValue ? Ui.ToVec(_screenHeadLocal.Value) : (Vec3?)null, _screenDistance);
            FollowDock(0f);
            Ev.Emit("dock", ("phase", "saved"), ("side", side.ToString().ToLowerInvariant()));
            LogWindow();
            ShowDockBand();
        }

        /// <summary>The workspace stands in `side`'s slot of the display at `anchor` (its centre, its rotation), w x h metres.</summary>
        void DockToScreen(Transform anchor, float width, float height, DockSide side, Vector3? headLocal = null, float distance = 0f)
        {
            _screenHeadLocal = headLocal;
            _screenDistance = distance;
            if (_screenAnchor != null && _screenAnchor != anchor) Destroy(_screenAnchor.gameObject);
            _screenAnchor = anchor;
            _screenWidth = width;
            _screenHeight = height;
            _screenSide = side;
            _workspace.RailOnRight = DockSlots.RailOnRight(side);
            FollowDock(0f);
            Debug.Log("[Lotse] docked " + side + " of the display (" + width.ToString("0.00") + " x " + height.ToString("0.00") + " m): workspace at " + _workspace.Dock);
            LogWindow();
        }

        /// <summary>The display as it stands now (its anchor may be corrected by the runtime), or null.</summary>
        public ScreenRect? Screen => _screenAnchor == null ? (ScreenRect?)null
            : new ScreenRect(Ui.ToVec(_screenAnchor.position), Ui.ToVec(_screenAnchor.up), Ui.ToVec(_screenAnchor.forward), _screenWidth, _screenHeight);

        void ShowStart()
        {
            _startPanel = Panels.Start(_stage, new Vector3(0f, -WorkspaceView.BaseHeight * 0.5f, 0f), _packJson != null ? StartDemo : (System.Action)null,
                OpenPairing, Replace, _packJson != null ? StartTour : (System.Action)null);
            Current = Phase.Start;
            Debug.Log("[Lotse] start panel shown" + (_packJson == null ? " (demo not available)" : ""));
        }

        IEnumerator LoadPack()
        {
            var path = Path.Combine(Application.streamingAssetsPath, PackFile);
            // on Android StreamingAssets sits inside the APK (jar:file://...), so always go through UnityWebRequest
            var url = path.Contains("://") ? path : new System.Uri(path).AbsoluteUri;
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[Lotse] pack load failed: {url}: {req.error}");
                    yield break;
                }
                try
                {
                    var pack = DemoPack.Parse(req.downloadHandler.text); // validate once here
                    _packJson = req.downloadHandler.text;
                    Debug.Log($"[Lotse] pack loaded: {_packJson.Length} chars, {pack.Messages.Count} messages" +
                              (pack.SkippedUnknown > 0 ? $", {pack.SkippedUnknown} of an unknown type skipped" : ""));
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[Lotse] pack invalid: " + e.Message);
                }
            }
        }

        static bool XrHeadTracked()
        {
            var head = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
            return head.isValid && head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out var tracked) && tracked;
        }

        /// <summary>Fixed fallback placement: 0.6 m in front of the head (yaw only), 0.25 m below the eyes; the workspace in front of the head.</summary>
        void PlaceAnchor()
        {
            var forward = Vector3.ProjectOnPlane(Head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            _anchor.SetPositionAndRotation(Head.position + forward * AnchorForward + Vector3.down * AnchorBelowEyes,
                Quaternion.LookRotation(forward, Vector3.up));
            DockWorkspace();
            Debug.Log($"[Lotse] anchor placed at {_anchor.position} (head {Head.position})");
        }

        /// <summary>
        /// Where the workspace stands, from where the head is now (WorkspaceDock's default, U5b after the first device test):
        /// 0.75 m in front of the head, 5 cm below the eyes, its front facing the head; with or without a desk anchor. Its
        /// offset from the anchor's position is kept (it stays on the desk when the runtime corrects the anchor), its facing
        /// is kept in the world: an anchor's own rotation never turns the window edge-on.
        /// </summary>
        void DockWorkspace()
        {
            // the distance and size the user chose last time (U8), else the default (1.0 m, 1.15 of the U5b size)
            var saved = _windowRecord.TryLoad(out var distance, out var belowEyes, out var apparent);
            var dock = saved
                ? WorkspaceDock.InFrontOfHead(Ui.ToVec(Head.position), Ui.ToVec(Head.forward), apparent * distance, distance, belowEyes)
                : WorkspaceDock.InFrontOfHead(Ui.ToVec(Head.position), Ui.ToVec(Head.forward));
            _dockLocalCenter = Ui.ToVector(dock.Center) - _anchor.position;
            _dockLocalForward = Ui.ToVector(dock.Forward);
            _docked = true;
            FollowDock(dock.Height);
            var front = -_workspace.transform.forward;
            var toHead = Vector3.ProjectOnPlane(Head.position - _workspace.transform.position, Vector3.up);
            Debug.Log($"[Lotse] workspace at {dock}; its front is {Vector3.Angle(Vector3.ProjectOnPlane(front, Vector3.up), toHead):0.0} deg off the direction to the head");
            LogWindow();
        }

        /// <summary>The workspace (and the stage every panel stands on, scaled with it) where the dock says.</summary>
        void FollowDock(float height)
        {
            if (_screenAnchor != null)
            {
                // docked to a display: (U10) on the rays from the seat at the preferred distance, (U11) turned to face
                // the seat (yaw only) and lifted above the desk; in the display's own plane, or with a record without a
                // seat: the slot itself, with the display's rotation (a laptop leaning back tilts the window too)
                var slot = _screenHeadLocal.HasValue
                    ? DockSlots.Projected(Screen.Value, _screenSide, Ui.ToVec(_screenAnchor.TransformPoint(_screenHeadLocal.Value)), _screenDistance, DeskY)
                    : DockSlots.Slot(Screen.Value, _screenSide);
                _workspace.Place(slot);
                _stage.SetPositionAndRotation(Ui.ToVector(slot.Center), Quaternion.LookRotation(Ui.ToVector(slot.Forward), Ui.ToVector(slot.Up)));
                _stage.localScale = Vector3.one * (slot.Height / WorkspaceView.BaseHeight);
                return;
            }
            if (!_docked) return;
            var center = _anchor.position + _dockLocalCenter;
            var forward = _dockLocalForward;
            var dock = new WorkspaceDock(Ui.ToVec(center), Ui.ToVec(forward), new Vec3(0f, 1f, 0f), height, DockSide.Right);
            _workspace.Place(dock);
            _stage.SetPositionAndRotation(center, Quaternion.LookRotation(forward, Vector3.up));
            _stage.localScale = Vector3.one * (height / WorkspaceView.BaseHeight);
        }

        /// <summary>"Neu platzieren": tap the desk again (the content stays where it is until then, and if it fails).</summary>
        void Replace()
        {
            if (_placer == null) return; // the first placement has not been set up yet
            Debug.Log("[Lotse] placing again");
            CloseSettings();
            _placer.Begin(Time.timeAsDouble * 1000.0, trySaved: false);
        }

        void OnPlaced(Transform deskAnchor, PlacementFallback reason)
        {
            if (deskAnchor != null)
            {
                if (_deskAnchor != null && _deskAnchor != deskAnchor) Destroy(_deskAnchor.gameObject);
                _deskAnchor = deskAnchor;
                FollowDesk();
                DockWorkspace();
                Debug.Log($"[Lotse] anchor on the desk at {_anchor.position}");
                return;
            }
            if (_deskAnchor != null)
            {
                // a re-place that failed: the desk anchor that works stays
                _banner.Show(Texts.PlaceKept, Theme.BannerBad, 6f);
                Debug.Log($"[Lotse] re-place failed ({reason}): the desk anchor stays");
                return;
            }
            PlaceAnchor();
            _banner.Show(Texts.PlaceFallback, Theme.BannerBad, 6f);
            Debug.Log($"[Lotse] anchor stays head-relative ({reason})");
        }

        // ---- the settings (from the wrist menu) ----

        /// <summary>"Einstellungen": in front of the workspace (which hides its block meanwhile). Not while placing.</summary>
        void OpenSettings()
        {
            if (_placer != null && _placer.Active) return;
            ShowSettings(appear: true);
            Debug.Log("[Lotse] settings opened");
        }

        SettingsState SettingsNow()
        {
            string line;
            if (_liveSession && _live != null) line = Texts.ConnectionLine(_live.State, _live.Host);
            else if (Session != null) line = Texts.DemoRunning;
            else line = Texts.NotConnected;
            return new SettingsState(line, OffersDisconnect, Chime.On, Theme.Calm, _confirmRepair, _ctl.AutoCollapse);
        }

        void ShowSettings(bool appear) =>
            _settings.Show(SettingsNow(), SettingsDisconnect, AskRepair, Repair, CancelRepair, SetSound, SetCalm, SetAutoCollapse, CloseSettings, appear);

        /// <summary>"Neu koppeln" asks first ("Wirklich neu koppeln?").</summary>
        void AskRepair()
        {
            _confirmRepair = true;
            ShowSettings(appear: false);
        }

        void CancelRepair()
        {
            _confirmRepair = false;
            ShowSettings(appear: false);
        }

        void CloseSettings()
        {
            _confirmRepair = false;
            if (_settings != null && _settings.IsOpen) _settings.Hide();
        }

        void SettingsDisconnect()
        {
            CloseSettings();
            Disconnect();
        }

        /// <summary>"Neu koppeln": no connection, then the pairing panel without trying the stored session.</summary>
        void Repair()
        {
            CloseSettings();
            Debug.Log("[Lotse] pairing again");
            if (_pairing != null) Remove(_pairing.gameObject);
            _pairing = null;
            BeginSession(null, live: false);
            OpenPairing(resume: false);
        }

        void SetSound(bool on)
        {
            Chime.On = on;
            _look.Sound = on;
            Debug.Log("[Lotse] sound " + (on ? "on" : "off"));
            ShowSettings(appear: false);
        }

        /// <summary>"Ruhig": nothing in the workspace moves; the orbs keep their states by colour, ring and badge.</summary>
        void SetCalm(bool on)
        {
            Theme.Calm = on;
            _look.Calm = on;
            Debug.Log("[Lotse] motion " + (on ? "calm" : "normal"));
            ShowSettings(appear: false);
        }

        /// <summary>"Automatisch einklappen": the window folds into the rail after 20 s with the head away (on by default).</summary>
        void SetAutoCollapse(bool on)
        {
            _ctl.AutoCollapse = on;
            _look.AutoCollapse = on;
            Debug.Log("[Lotse] auto collapse " + (on ? "on" : "off"));
            ShowSettings(appear: false);
        }

        /// <summary>Keeps the content on the desk anchor, which the runtime may correct while it tracks.</summary>
        void FollowDesk()
        {
            if (_deskAnchor == null) return;
            var forward = Vector3.ProjectOnPlane(_deskAnchor.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = _anchor.forward;
            _anchor.SetPositionAndRotation(_deskAnchor.position + Vector3.up * DeskLift, Quaternion.LookRotation(forward.normalized, Vector3.up));
        }

        void StartDemo()
        {
            BeginSession(new DemoSession(DemoPack.Parse(_packJson)), live: false);
            Current = Phase.Playing;
            Debug.Log("[Lotse] demo started");
        }

        void OpenPairing() => OpenPairing(resume: true);

        /// <summary>
        /// "Mit meinem PC verbinden": a live session starts at once (it must see the bridge's snapshot), the pairing panel on
        /// top; `resume`: the last bridge with a stored session is tried right away ("Neu koppeln" does not).
        /// </summary>
        void OpenPairing(bool resume)
        {
            if (_hosts == null) _hosts = new HostBook(Prefs);
            if (_live == null)
            {
                _live = new LiveSource(_hosts, SocketFactory ?? NativeLinkSocket.Open);
                _live.StateChanged += OnLinkState;
            }
            BeginSession(new DemoSession(_live), live: true);
            // the last bridge with a stored session: try it right away (before the panel opens: it scans only when
            // no connection is being made)
            var recent = _hosts.Recent;
            // (U12a) one short attempt: the stored address may be an old one; the panel scans when it does not answer
            _viaStored = false;
            if (resume && recent.Count > 0 && _live.Resume(recent[0], once: true))
            {
                _viaStored = true;
                Ev.Emit("link", ("phase", "resume"), ("reason", LinkWhy.Stored));
                Debug.Log("[Lotse] resuming the last bridge");
            }
            ShowPairing();
        }

        void ShowPairing()
        {
            if (_pairing != null) Remove(_pairing.gameObject);
            _pairing = PairingPanel.Create(_stage, new Vector3(0f, -WorkspaceView.BaseHeight * 0.5f, 0f), _hosts, _live, LeavePairing,
                QrSource ?? (QrSource = new MrukQrSource()), Head);
            Current = Phase.Pairing;
            Session?.SetViewShown(false); // the block goes with the window, also in the middle of a session step
            Debug.Log("[Lotse] pairing panel shown");
        }

        /// <summary>
        /// Where the desk keyboard lies: on the desk, Layout.DeskKeyboardFrontMeters in front of the desk anchor (toward
        /// the user); without a desk anchor below the workspace, a little nearer to the user. Forward points away.
        /// </summary>
        Pose DeskKeyboardPose()
        {
            var away = Vector3.ProjectOnPlane(_anchor.forward, Vector3.up).normalized;
            if (_deskAnchor != null)
                return new Pose(_deskAnchor.position - away * Layout.DeskKeyboardFrontMeters + Vector3.up * Layout.DeskKeyboardAboveDeskMeters,
                    Quaternion.LookRotation(away, Vector3.up));
            var view = _workspace.transform;
            var toUser = Vector3.ProjectOnPlane(Head.position - view.position, Vector3.up).normalized;
            return new Pose(view.position + toUser * 0.15f + Vector3.down * (_workspace.Dock.Height * 0.5f + 0.1f),
                Quaternion.LookRotation(-toUser, Vector3.up));
        }

        /// <summary>"Trennen" in the settings while live.</summary>
        void Disconnect()
        {
            Debug.Log("[Lotse] disconnect");
            LeavePairing();
        }

        /// <summary>"Zurück" on the pairing panel, or "Trennen" while live: no connection, back to the start panel.</summary>
        void LeavePairing()
        {
            if (_pairing != null) Remove(_pairing.gameObject);
            _pairing = null;
            BeginSession(null, live: false);
            ShowStart();
        }

        void OnLinkState()
        {
            Debug.Log($"[Lotse] link {_live.State}: {_live.Status}");
            if (!_liveSession) return;
            if (_live.State == LinkState.Live)
            {
                if (Current == Phase.Pairing) Ev.Emit("link", ("phase", "live"), ("reason", _viaStored && (_pairing == null || _pairing.Resuming) ? LinkWhy.Stored : LinkWhy.Code));
                if (Current == Phase.Pairing)
                {
                    if (_pairing != null) Remove(_pairing.gameObject);
                    _pairing = null;
                    Current = Phase.Playing;
                }
                _banner.Show(Texts.Paired, Theme.BannerGood, 3f);
                _ctl.OnReconnected(); // the bridge ended the feeds with the old connection
            }
            else if (_live.State == LinkState.NeedsCode && Current == Phase.Playing)
            {
                ShowPairing();
            }
            else if (_live.State == LinkState.Retrying && Current == Phase.Playing && _banner.Line != Texts.ConnectionLost)
            {
                _banner.Show(_live.Status, Theme.BannerBad, 12f);
            }
        }

        /// <summary>Clears the scene of the previous run and wires `session` (null: none) to it.</summary>
        void BeginSession(DemoSession session, bool live)
        {
            Remove(_startPanel);
            Remove(_endCard);
            _startPanel = null;
            _endCard = null;
            DropQuestion();
            if (!_startingTest) EndTest();
            _ctl.Reset(session);
            _ctl.StripRepo = !live;
            _reply.Reset(session);
            CloseSettings();
            EndSummary = null;

            _liveSession = live;
            _stop.Reset();
            if (!live && _live != null && _live.State != LinkState.Idle) _live.Disconnect();

            Session = session;
            _heard.Clear();
            if (session == null) return;
            Session.CardChanged += OnCardChanged;
            Session.Decided += OnDecided;
            Session.DecisionDropped += reason => Debug.Log("[Lotse] dropped: " + reason);
            Session.ConnectionLost += OnConnectionLost;
            Session.BridgeError += OnBridgeError;
            Session.IncidentRaised += OnIncident;
            Session.NoticeRaised += OnNotice;
            Session.ReviewLine += line => Debug.Log("[Lotse] an agent finished");
        }

        void OnConnectionLost()
        {
            Debug.Log("[Lotse] connection lost: open requests dropped, nothing sent");
            _banner.Show(Texts.ConnectionLost, Theme.BannerBad, 8f);
        }

        void OnBridgeError(string error)
        {
            Debug.LogWarning("[Lotse] bridge error: " + error);
            _banner.Show(Texts.Refused(error), Theme.BannerBad, 4f);
        }

        /// <summary>An incident is only shown: a line in the agents' conversations and the notice. It decides nothing.</summary>
        void OnIncident(Incident incident)
        {
            Debug.Log($"[Lotse] incident {incident.Kind}");
            _ctl.ShowIncident(incident);
        }

        /// <summary>A request wants the user: the notice above the window and the chime. Nothing opens.</summary>
        void OnNotice(PermRequest req)
        {
            Debug.Log($"[Lotse] notice for request {req.Id} ({Texts.LogSid(req.Sid)})");
            _ctl.ShowNotice(req);
        }

        // ---- sounds (U14) ----
        readonly HashSet<string> _heard = new HashSet<string>();

        /// <summary>A request arrives: two soft rising notes, once per request (the mixer makes one sound of a burst).</summary>
        void HearRequests()
        {
            if (Session == null)
            {
                if (_heard.Count > 0) _heard.Clear();
                return;
            }
            var pending = Session.Store.Pending;
            for (var i = 0; i < pending.Count; i++)
                if (_heard.Add(pending[i].Id) && Current == Phase.Playing) Sounds.Play(Sfx.Request, _workspace.transform.position);
        }

        /// <summary>The frame's end: of the sounds asked for in it, at most one plays.</summary>
        void LateUpdate()
        {
            if (_workspace != null) HearRequests();
            Sounds.Flush(Time.timeAsDouble * 1000.0);
        }

        void OnDestroy()
        {
            Theme.Still = false;
            Theme.Calm = false;
            if (_live != null) _live.Disconnect();
            if (_placer != null) _placer.Stop();
            if (_menu != null) Destroy(_menu.gameObject);
            if (_pointer != null) Destroy(_pointer.gameObject);
            if (_deskAnchor != null) Destroy(_deskAnchor.gameObject);
            if (_screenAnchor != null) Destroy(_screenAnchor.gameObject);
            if (_setupView != null) Destroy(_setupView.gameObject);
        }

        static void SetShown(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        /// <summary>Deactivate first, so its buttons leave the poke registry at once; Destroy only runs at frame end.</summary>
        static void Remove(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            Destroy(go);
        }

        void Update()
        {
            if (_anchor == null) return;
            Theme.Still = Theme.Calm || Holding;
            Hands.Read(Head.position, out var left, out var right);
            // no input focus (the system keyboard, the system menu): both hands count as untracked, which cancels a
            // hold, switches the rays off and stops every poke
            var focus = InputFocus();
            if (focus != _hadFocus)
            {
                _hadFocus = focus;
                Debug.Log(focus ? "[Lotse] input focus back" : "[Lotse] input focus lost: hands ignored");
            }
            if (!focus)
            {
                left = HandFrame.Untracked;
                right = HandFrame.Untracked;
            }
            var nowMs = Time.timeAsDouble * 1000.0;
            _perf.Frame(Time.unscaledTimeAsDouble * 1000.0, Time.unscaledDeltaTime * 1000.0);
            // while placing, the hands belong to the placement: no button presses, no gestures on a block
            var placing = _placer != null && _placer.Active;
            if (placing) _placer.Step(nowMs, left, right);
            FollowDesk();
            FollowDock(Dock.Height > 0f ? Dock.Height : WorkspaceDock.DefaultHeight);
            if (Current == Phase.Loading) return;
            // a desk anchor stays on the desk; only the head-relative one is recaptured
            if (_deskAnchor == null && AnchorWatch.NeedsRecapture(Ui.ToVec(_anchor.position), Ui.ToVec(Head.position)))
            {
                Debug.Log("[Lotse] anchor too far from the head: placing it again");
                PlaceAnchor();
            }
            // the wrist menu: not while placing, nor while a block waits for its gesture (its hand's pinch is the menu's)
            var waiting = Session != null && Session.Mode == CardMode.Hold;
            _menu.Step(nowMs, _placer != null && !placing && !waiting, left, right, Head);
            if (!placing)
            {
                // (U11) the pairing question learns about the hands before any button or ray is stepped this frame
                if (_pairing != null)
                    _pairing.Hands(left.Sample.Usable, left.Sample.PinchStrength, right.Sample.Usable, right.Sample.PinchStrength);
                PokeButton.ProbeAll(nowMs, left, right);
                placing = _placer != null && _placer.Active; // "Neu platzieren" may have been pressed just now
            }
            // the ray after the pokes; it stays on while a block is shown (a click never feeds the hold: RayClickLogic)
            _pointer.Step(RayClickLogic.Scope(placing), _menu.Shown, left, right, nowMs, Holding);
            // the device log: each hand's pinch, with what its ray rested on
            _pinchLeft.Step(nowMs, left.Sample.Usable, left.Sample.PinchStrength, _pointer.LeftTarget);
            _pinchRight.Step(nowMs, right.Sample.Usable, right.Sample.PinchStrength, _pointer.RightTarget);
            // the display setup: like placing for the session (no block, no hold), but buttons, slots and rays work
            if (_setup.Active || _setup.Phase == SetupPhase.Done)
            {
                if (placing) CancelScreenSetup();
                else StepScreenSetup(nowMs, left, right);
            }
            var setup = _setup.Active;
            // the settings give way to placing and follow the connection while open
            if (_settings.IsOpen)
            {
                if (placing) CloseSettings();
                else if (!_settings.Shown.Same(SettingsNow())) ShowSettings(appear: false);
            }
            // the banner hangs below the window, and below the guided test's band when that shows (never over its buttons)
            var below = _workspace.TestBand.IsShown && _workspace.IsShown ? WindowBand.CaptionedHeight : 0f;
            _banner.transform.localPosition = new Vector3(0f, -WorkspaceView.BaseHeight * 0.5f - below - Theme.Unit, -0.01f);
            // the start panel, the pairing panel and the end card step aside while the settings are open
            var aside = _settings.IsOpen || setup;
            SetShown(_startPanel, !aside);
            SetShown(_endCard, !aside);
            if (_pairing != null) SetShown(_pairing.gameObject, !aside);

            // the block is on screen only while the workspace is, with nothing in front of it, in Unterhaltung, fully open
            // (U6: never in Übersicht, never collapsed or folding)
            var playing = Current == Phase.Playing && Session != null;
            // the offer is asked in the window's band once the window stands open (never across a running hold)
            if (_offerPending && playing && !placing && !setup && !_settings.IsOpen && _workspace.IsShown && _workspace.Open &&
                !_workspace.Asking && !Holding && !_test.Active && !_tour.Active) ShowScreenOffer();
            // the guided test offers the request its step needs (its own local source; never a bridge)
            if (_test.Active && _guided != null && _testNeed != TestNeed.Nothing && playing &&
                _guided.Offer(_testNeed == TestNeed.DangerousRequest)) _testNeed = TestNeed.Nothing;
            // the tour sees what happened this frame, and keeps the request its step needs on offer (the same source)
            if (_tour.Active && _guided != null && playing) StepTour(left, right);
            // the dock band closes by itself after 10 s without input
            if (DockBand != null && (Time.time >= _dockBandUntil || !playing)) CloseDockBand();
            // "Halt all agents?" goes when there is nothing to halt any more
            if (_workspace.Menu.AskingHalt && (!playing || Session.Halted)) DropQuestion();
            Session?.SetViewShown(playing && !placing && !setup && !_settings.IsOpen && _ctl.ConversationOnScreen);
            // the stop gesture (U7): never while typing, poking, clicking, dragging, pairing, placing or setting up a display
            var stopBlocked = !playing || Session.Halted || placing || setup || _pairing != null || !HeadTracked() || _reply.Typing ||
                              PokeButton.AnyNear || _pointer.MaskLeft || _pointer.MaskRight || _ctl.Dragging;
            var stop = _stop.Step(nowMs, stopBlocked, left.Stop, right.Stop, Ui.ToVec(Head.position), Ui.ToVec(Head.forward));
            LogStop(stop);
            LogStopEv(nowMs, stop, stopBlocked, left.Stop, right.Stop);
            // while it counts, the session sees no hands: a running hold is cancelled the moment it starts to count
            var stopping = _stop.Counting || stop.Kind == StopEventKind.Triggered;
            _ctl.SetStopProgress(_stop.Counting ? Mathf.Max(_stop.Progress, 0.01f) : 0f);
            _menu.HaltOffered = playing && !Session.Halted;
            _workspace.Menu.HaltOffered = playing && !Session.Halted;
            if ((Current == Phase.Playing || Current == Phase.Pairing) && Session != null)
            {
                // (U14b) what changed on screen after the last frame's check (a layout in LateUpdate, an anchor correction)
                // is seen before the session steps: a hold cannot complete in the frame of the change
                GuardVisibleChange();
                var pose = Pose(left, right);
                // the header menu open: no hold can start or continue; opening or closing it resets the gesture like a block change
                // (U9) a question band of the window counts like the open menu
                // (U13) and so does a row that still opens, closes or switches: a request can be approved only when its
                // row is open and the opening motion has finished
                // (U14) and so does the green "Cleared" of the request before, while it stands in the block's place
                var menuOpen = _workspace.Asking || (Session.Card != null && !_ctl.OpenSettled) || _ctl.ClearedShowing;
                if (menuOpen != _menuWasOpen)
                {
                    _menuWasOpen = menuOpen;
                    Session.Rearm();
                }
                if (placing || setup || stopping || menuOpen) Session.Step(Time.deltaTime * 1000.0, HandFrame.Untracked.Sample, HandFrame.Untracked.Sample, pose);
                else Session.Step(Time.deltaTime * 1000.0, Masked(left.Sample, _pointer.MaskLeft), Masked(right.Sample, _pointer.MaskRight), pose);
            }
            // the device log: the hold on the block (it only watches the session), the screen, the window's mode
            var progress = Session != null ? Session.HoldProgress : 0f;
            if (progress > 0f && Session.Card != null) _holdCard = Session.Card.Id;
            _holdLog.Step(nowMs, progress, Session?.Card?.NeedsTwoHand ?? false, _allowedThisFrame,
                () => _changeReason ?? CancelReason(placing, setup, stopping, left, right));
            _changeReason = null;
            _allowedThisFrame = false;
            LogScreen(placing, setup);
            Theme.Still = Theme.Calm || Holding;
            _ctl.Step(nowMs, playing && !setup, placing || setup || !playing || _settings.IsOpen, left, right, Head.position,
                HeadTracked() && !placing && !setup && !_settings.IsOpen ? Head.forward : Vector3.zero, Holding);
            GuardVisibleChange();
            if (stop.Kind == StopEventKind.Triggered) TriggerHalt("stop gesture");
            if (Session != null && Session.Finished && Current == Phase.Playing) ShowEnd();
        }

        // ---- any visible change of the shown request cancels a hold (U14, review of U13) ----
        string _guardCard;
        int _guardBuilds;
        int _guardRows;
        Vector3 _guardPos;
        Quaternion _guardRot = Quaternion.identity;
        float _guardScale = 1f;
        HoldCancel? _changeReason;
        /// <summary>The window moving this far, turning this much or changing its size by this share within one frame cancels a hold.</summary>
        public const float MoveCancelMeters = 0.01f;
        public const float MoveCancelDegrees = 1f;
        public const float MoveCancelScale = 0.01f;

        /// <summary>
        /// Before the session steps and again after everything was drawn: when the request block on show was built again (its "+n more", the room it has),
        /// when the rows were laid out anew (another agent, another order, the headline taking another line: the open
        /// block moved or changed its size) or when the window itself jumped (placed again, docked, resized), the
        /// gesture on the block starts over exactly as for a new block: a running hold is cancelled and the pinch that
        /// is held never counts. A new block is the session's own business (it has reset the gesture already).
        /// </summary>
        void GuardVisibleChange()
        {
            var card = Session?.Card?.Id;
            var builds = _ctl.BlockBuilds;
            var rows = _workspace.Rows.Version;
            var pos = _stage.position;
            var rot = _stage.rotation;
            var scale = _stage.localScale.x;
            HoldCancel? why = null;
            if (card != null && card == _guardCard)
            {
                if (builds != _guardBuilds || rows != _guardRows) why = HoldCancel.BlockChanged;
                else if ((pos - _guardPos).magnitude > MoveCancelMeters || Quaternion.Angle(rot, _guardRot) > MoveCancelDegrees ||
                         Mathf.Abs(scale / Mathf.Max(1e-6f, _guardScale) - 1f) > MoveCancelScale) why = HoldCancel.Moved;
            }
            _guardCard = card;
            _guardBuilds = builds;
            _guardRows = rows;
            _guardPos = pos;
            _guardRot = rot;
            _guardScale = scale;
            if (!why.HasValue || Session.Mode != CardMode.Hold) return;
            if (Holding)
            {
                _changeReason = why;
                Debug.Log("[Lotse] the shown request changed on screen (" + why + "): the running hold is cancelled");
            }
            Session.Rearm();
        }

        // ---- the header's Menu (U8) ----

        /// <summary>"Menu" in the header: opens or closes the menu; never while a hold runs (it cancels nothing).</summary>
        void OnMenuPressed()
        {
            var menu = _workspace.Menu;
            if (menu.IsOpen)
            {
                menu.Hide();
                return;
            }
            // it may stand over the block: opening it cancels a running hold (the session sees no hands while it is open)
            if (Holding) Debug.Log("[Lotse] menu opened: the running hold is cancelled");
            menu.Open();
        }

        void OnMenuItem(WindowMenuItem item)
        {
            var menu = _workspace.Menu;
            switch (item)
            {
                case WindowMenuItem.Further: MoveWindow(WindowPlacement.StepMeters); break;
                case WindowMenuItem.Closer: MoveWindow(-WindowPlacement.StepMeters); break;
                case WindowMenuItem.Larger: PlaceWindow(WindowPlacement.Resize(Dock, Ui.ToVec(Head.position), WindowPlacement.SizeStep)); break;
                case WindowMenuItem.Smaller: PlaceWindow(WindowPlacement.Resize(Dock, Ui.ToVec(Head.position), 1f / WindowPlacement.SizeStep)); break;
                case WindowMenuItem.ScreenSetup: menu.Hide(); BeginScreenSetup(); break;
                case WindowMenuItem.Replace: menu.Hide(); Replace(); break;
                case WindowMenuItem.Settings: menu.Hide(); OpenSettings(); break;
                case WindowMenuItem.Halt: menu.Hide(); TriggerHalt("menu"); break;
                case WindowMenuItem.GuidedTest: menu.Hide(); StartGuidedTest(); break;
                default: menu.Hide(); break;
            }
        }

        /// <summary>
        /// "Further" / "Closer" (U10). Free: 0.25 m along the line from the head, the apparent size kept. Docked: along the
        /// rays from the seat, so it still lines up with the display; the docking stays.
        /// </summary>
        void MoveWindow(float delta)
        {
            if (_screenAnchor == null || !_screenHeadLocal.HasValue)
            {
                PlaceWindow(WindowPlacement.Move(Dock, Ui.ToVec(Head.position), delta));
                return;
            }
            _screenDistance = Mathf.Clamp(_screenDistance + delta, WindowPlacement.MinDistance, WindowPlacement.MaxDistance);
            _screenRecord.Save(_screenWidth, _screenHeight, _screenSide, Ui.ToVec(_screenHeadLocal.Value), _screenDistance);
            FollowDock(0f);
            Debug.Log($"[Lotse] docked window moved by the menu: {_screenDistance:0.00} m from the seat");
            LogWindow();
        }

        /// <summary>
        /// The window freely where `dock` says, kept in prefs. Docked to a display, this ends the docking for good (the
        /// window stays at the new spot) and says "Undocked" once.
        /// </summary>
        void PlaceWindow(WorkspaceDock dock)
        {
            dock = WindowPlacement.Fit(dock, Ui.ToVec(Head.position)); // a display's slot may be outside the menu's bounds: into them
            if (_screenAnchor != null)
            {
                Destroy(_screenAnchor.gameObject);
                _screenAnchor = null;
                _screenRecord.Clear();
                _workspace.RailOnRight = false;
                _banner.Show(Texts.Undocked, Theme.BannerGood, 3f);
                Debug.Log("[Lotse] undocked from the display by the menu");
            }
            _dockLocalCenter = Ui.ToVector(dock.Center) - _anchor.position;
            _dockLocalForward = Ui.ToVector(dock.Forward);
            _docked = true;
            FollowDock(dock.Height);
            var distance = WindowPlacement.Distance(_workspace.Dock, Ui.ToVec(Head.position));
            _windowRecord.Save(distance, Head.position.y - _workspace.Dock.Center.Y, WindowPlacement.Apparent(_workspace.Dock, Ui.ToVec(Head.position)));
            Debug.Log($"[Lotse] window placed by the menu: {distance:0.00} m from the head, scale {WindowPlacement.ScaleOf(_workspace.Dock.Height):0.00}");
            LogWindow();
        }

        // ---- the device log (U9) ----

        /// <summary>Why the hold that just ended ended, from what the app knows this frame. It only names it; nothing here cancels anything.</summary>
        HoldCancel CancelReason(bool placing, bool setup, bool stopping, in HandFrame left, in HandFrame right)
        {
            if (stopping) return HoldCancel.StopGesture;
            if (placing) return HoldCancel.Placing;
            if (setup) return HoldCancel.Setup;
            if (Session == null) return HoldCancel.Unknown;
            if (Session.Halted) return HoldCancel.Halted;
            if (_workspace.Menu.IsOpen) return HoldCancel.Menu;
            if (_workspace.Question.IsShown) return HoldCancel.Question;
            if (!Session.ViewShown) return HoldCancel.ViewHidden;
            if (Session.Card == null || Session.Card.Id != _holdCard || Session.Mode != CardMode.Hold) return HoldCancel.BlockChanged;
            if (!left.Sample.Usable && !right.Sample.Usable) return HoldCancel.TrackingLost;
            if (Session.HoldCue != HoldCue.None) return HoldCancel.Gate;
            var two = Session.Card.NeedsTwoHand;
            var l = left.Sample.Usable && left.Sample.PinchStrength >= GestureMachine.PinchOff;
            var r = right.Sample.Usable && right.Sample.PinchStrength >= GestureMachine.PinchOff;
            if (two ? !(l && r) : !(l || r)) return HoldCancel.Released;
            return HoldCancel.Unknown;
        }

        /// <summary>`screen name=…` on every change of what is on screen.</summary>
        void LogScreen(bool placing, bool setup)
        {
            ScreenName name;
            if (Current == Phase.Pairing) name = ScreenName.Pairing;
            else if (setup) name = ScreenName.Setup;
            else if (placing) name = ScreenName.Placing;
            else if (_settings.IsOpen) name = ScreenName.Settings;
            else if (Current == Phase.Start) name = ScreenName.Start;
            else if (Current == Phase.End) name = ScreenName.End;
            else if (Current != Phase.Playing) name = ScreenName.Loading;
            else if (_workspace.Menu.IsOpen) name = ScreenName.Menu;
            else if (_ctl.Mode.Collapsed) name = ScreenName.Rail;
            else if (Session != null && Session.Halted) name = ScreenName.Halted;
            else name = _ctl.Mode.Tab == WorkspaceTab.Overview ? ScreenName.Overview : ScreenName.Open;
            if (_screenLogged != name)
            {
                _screenLogged = name;
                Ev.Emit("screen", ("name", name.ToString().ToLowerInvariant()));
            }
            var mode = (_ctl.Mode.Tab, _ctl.Mode.Collapsed);
            if (Current == Phase.Playing && (!_modeLogged.HasValue || !_modeLogged.Value.Equals(mode)))
            {
                _modeLogged = mode;
                LogWindow();
            }
        }

        /// <summary>`window dist=… scale=… mode=… docked=…` whenever the window is placed, docked, or changes its mode.</summary>
        void LogWindow()
        {
            if (_workspace == null || _ctl == null || Head == null || _workspace.Dock.Height <= 0f) return;
            var mode = _ctl.Mode.Collapsed ? "rail" : _ctl.Mode.Tab == WorkspaceTab.Overview ? "overview" : "open";
            Ev.Emit("window", ("dist", WindowPlacement.Distance(_workspace.Dock, Ui.ToVec(Head.position))),
                ("scale", WindowPlacement.ScaleOf(_workspace.Dock.Height)), ("mode", mode),
                ("docked", _screenAnchor != null ? _screenSide.ToString().ToLowerInvariant() : Ev.None));
        }

        /// <summary>
        /// `stop phase=arming|reset|fired t=… fail=…` with the measured value that failed. A reset is logged when a count
        /// breaks off, and (at most twice a second in all) while both hands are up and open but a condition fails.
        /// </summary>
        void LogStopEv(double nowMs, in StopEvent e, bool blocked, in StopHand left, in StopHand right)
        {
            var held = _stopHeld;
            _stopHeld = _stop.Counting ? _stop.Progress * (float)(StopGesture.HoldMs / 1000.0) : 0f;
            if (e.Kind == StopEventKind.Started)
            {
                Ev.Emit("stop", ("phase", "arming"), ("t", 0.0));
                return;
            }
            if (e.Kind == StopEventKind.Triggered)
            {
                Ev.Emit("stop", ("phase", "fired"), ("t", StopGesture.HoldMs / 1000.0));
                return;
            }
            var d = StopGesture.Diagnose(left, right, Ui.ToVec(Head.position), Ui.ToVec(Head.forward));
            if (e.Kind == StopEventKind.Reset)
            {
                if (!_stopRate.Allow(nowMs)) return;
                var fail = StopGesture.FailOf(e.Reason);
                if (d.Measure != null && d.Fail == fail) Ev.Emit("stop", ("phase", "reset"), ("t", held), ("fail", fail), (d.Measure, d.Value));
                else Ev.Emit("stop", ("phase", "reset"), ("t", held), ("fail", fail));
                return;
            }
            // not counting, but the user is trying: say what fails
            if (_stop.Counting || blocked || !d.Attempt || d.Fail == StopFail.None || d.Measure == null) return;
            if (_stopRate.Allow(nowMs)) Ev.Emit("stop", ("phase", "reset"), ("t", 0.0), ("fail", d.Fail), (d.Measure, d.Value));
        }

        // ---- the guided test (U9) ----

        /// <summary>
        /// "Guided test" in the menu: the jury pack's agents with the test's own local requests (GuidedSource), and the
        /// band along the window's bottom edge that says what to try. The app behaves as always while it runs.
        /// </summary>
        void StartGuidedTest()
        {
            if (_packJson == null)
            {
                _banner.Show(Texts.DemoUnavailable, Theme.BannerBad, 3f);
                return;
            }
            _guided = new GuidedSource(DemoPack.Parse(_packJson));
            _tour.End();
            _startingTest = true;
            BeginSession(new DemoSession(_guided), live: false);
            _startingTest = false;
            Current = Phase.Playing;
            Debug.Log("[Lotse] guided test started");
            _test.Begin();
        }

        // ---- the tour (U14) ----

        /// <summary>
        /// "Take the tour" on the start panel: the demo's agents with the guided test's local requests (never a bridge)
        /// and the band along the window's bottom edge with one instruction at a time. The tour only watches: every
        /// step ends on what the user really did, and every approval rule holds unchanged while it runs.
        /// </summary>
        void StartTour()
        {
            if (_packJson == null)
            {
                _banner.Show(Texts.DemoUnavailable, Theme.BannerBad, 3f);
                return;
            }
            _test.End();
            _tour.End();
            _guided = new GuidedSource(DemoPack.Parse(_packJson), fromPlans: true);
            _startingTest = true;
            BeginSession(new DemoSession(_guided), live: false);
            _startingTest = false;
            Current = Phase.Playing;
            Debug.Log("[Lotse] tour started");
            _tour.Begin();
            ShowTourBand();
        }

        void OnTourStep(TourStep step)
        {
            // the first steps are about the overview: no row stands open
            if ((step.Id == "lines" || step.Id == "open") && _ctl != null && !_ctl.Mode.Collapsed) _ctl.CloseRow();
        }

        /// <summary>A step was done by the user: the select tick.</summary>
        void OnTourStepDone(TourStep step)
        {
            if (_workspace != null) Sounds.Play(Sfx.Select, _workspace.transform.position);
        }

        void StepTour(in HandFrame left, in HandFrame right)
        {
            var need = _tour.Need;
            if (need != TestNeed.Nothing) _guided.Offer(need == TestNeed.DangerousRequest);
            var open = _guided.Open;
            _tour.Step(Time.deltaTime * 1000.0, new TourFacts
            {
                Pinching = (left.Sample.Usable && left.Sample.PinchStrength >= GestureMachine.PinchOn) ||
                           (right.Sample.Usable && right.Sample.PinchStrength >= GestureMachine.PinchOn),
                RequestRowOpen = open != null && _ctl.OpenSid == open.Sid && _ctl.OpenSettled,
                RequestShown = open != null && _ctl.OpenSid == open.Sid,
                Halted = Session.Halted,
                Folded = _ctl.Mode.Collapsed,
                WindowOpen = _workspace.IsShown && _workspace.Open,
            });
            // the band follows the tour, but nothing on screen changes while a hold runs
            if (_tour.Version != _tourShown && !Holding) ShowTourBand();
        }

        void ShowTourBand()
        {
            _tourShown = _tour.Version;
            if (!_tour.Active) return;
            var band = _workspace.TestBand;
            band.Plain = true;
            band.ButtonWidth = TourLinkWidth;
            band.FirstAtRight = true;
            if (_tour.Phase == TourPhase.End)
                band.Show(TourTag, null, _tour.Text, true, (Tour.Done, FinishGuidedTest), (Tour.Again, StartTour));
            else if (_tour.Phase == TourPhase.Confirm)
                band.Show(TourTag, _tour.Progress, _tour.Text, false);
            else
                band.Show(TourTag, _tour.Progress, _tour.Text, _tour.Note, false, (Tour.Skip, _tour.SkipStep));
            _workspace.Refresh();
        }

        void OnTestStep(TestStep step)
        {
            _testNeed = step.Need;
            // the first steps are about the overview
            if ((step.Id == "read" || step.Id == "open") && _ctl != null && !_ctl.Mode.Collapsed) _ctl.CloseRow();
            ShowTestBand();
        }

        void ShowTestBand()
        {
            var band = _workspace.TestBand;
            band.Plain = false;
            band.ButtonWidth = WorkspaceView.TestButtonWidth;
            band.FirstAtRight = false;
            if (_test.Finished)
                band.Show(TestTag, GuidedTest.MenuItem, _test.Summary, false, (GuidedTest.Done, FinishGuidedTest));
            else
                band.Show(TestTag, _test.Caption, _test.Current.Instruction, false, (GuidedTest.Worked, () => AnswerTest(TestResult.Ok)),
                    (GuidedTest.DidNotWork, () => AnswerTest(TestResult.Fail)), (GuidedTest.Skip, () => AnswerTest(TestResult.Skip)));
            _workspace.Refresh();
        }

        /// <summary>A button of the test's band: it only records the answer and shows the next step. It decides no request.</summary>
        void AnswerTest(TestResult result)
        {
            _test.Answer(result);
            if (_test.Finished) ShowTestBand(); // a next step shows itself (OnTestStep)
        }

        /// <summary>"Done" on the summary: back to the start panel.</summary>
        void FinishGuidedTest()
        {
            EndTest();
            BeginSession(null, live: false);
            ShowStart();
        }

        void EndTest()
        {
            _test.End();
            _tour.End();
            _guided = null;
            _testNeed = TestNeed.Nothing;
            if (_workspace != null && _workspace.TestBand.IsShown)
            {
                _workspace.TestBand.Hide();
                _workspace.Refresh();
            }
        }

        /// <summary>The device log's one line per stop gesture event (and nothing else about the hands).</summary>
        static void LogStop(in StopEvent e)
        {
            switch (e.Kind)
            {
                case StopEventKind.Started: Debug.Log("[Lotse] stop gesture: counting"); break;
                case StopEventKind.Reset: Debug.Log("[Lotse] stop gesture: reset (" + e.Reason + ")"); break;
                case StopEventKind.Triggered: Debug.Log("[Lotse] stop gesture: triggered"); break;
            }
        }

        /// <summary>
        /// Halts every agent: the stop gesture or the wrist menu's confirmed button. Only asks the source; the state shown
        /// follows the bridge's halt_state. Without a connection: "Not connected, nothing halted" for 3 s, nothing else.
        /// </summary>
        void TriggerHalt(string how)
        {
            if (Session == null || Current != Phase.Playing || Session.Halted) return;
            if (Session.RequestHalt())
            {
                Debug.Log("[Lotse] halt sent (" + how + ")");
                Sounds.Play(Sfx.Stop, _workspace.transform.position); // a low, firm double note
                return;
            }
            Debug.Log("[Lotse] halt not sent (" + how + "): not connected");
            _banner.Show(Texts.HaltNotConnected, Theme.BannerBad, 3f);
        }

        bool WaitsNow(string sid)
        {
            if (Session == null || sid == null) return false;
            foreach (var a in Session.Store.Agents)
                if (a.Sid == sid) return a.Status == "waiting" || a.Status == "blocked";
            return false;
        }

        /// <summary>A pinch that began with its ray on a target reaches the session as an open hand: it never feeds the hold.</summary>
        static HandSample Masked(HandSample s, bool mask) => mask ? new HandSample(s.Tracked, s.HighConfidence, 0f, s.PalmForward) : s;

        /// <summary>A hold runs on the block: nothing moves meanwhile.</summary>
        bool Holding => Session != null && Session.Mode == CardMode.Hold && Session.HoldProgress > 0f;

        /// <summary>"Trennen" is offered in the settings while a live session runs (live, connecting or retrying).</summary>
        bool OffersDisconnect => _liveSession && Current == Phase.Playing && _live != null && _live.State != LinkState.Idle &&
                                 _live.State != LinkState.NeedsCode;

        void OnDeny(string id)
        {
            if (_workspace.Asking) return; // the menu or a question band is open: the block is not answerable (U8, U9)
            if (Session != null && Session.Deny(id)) Debug.Log("[Lotse] denied " + id + " (Ablehnen)");
        }

        void OnLater()
        {
            if (_workspace.Asking) return;
            if (Session == null || Session.Card == null) return;
            Debug.Log("[Lotse] request " + Session.Card.Id + " put off (Später): nothing sent");
            Session.Later();
            _ctl.CloseRow(); // "Later" closes the row (U13); opening it again brings the request back
        }

        /// <summary>
        /// What the HoldGate needs: the head, the block (its centre; the window's without one), the floor (the desk +
        /// 0.10 m on a desk, the eyes − 0.45 m without one) and where each hand pinches.
        /// </summary>
        HoldPose Pose(in HandFrame left, in HandFrame right)
        {
            var block = _ctl.BlockShown ? _ctl.Block.Center : _workspace.transform.position;
            return new HoldPose(Ui.ToVec(Head.position), Ui.ToVec(Head.forward), Ui.ToVec(block),
                _deskAnchor != null ? HoldGate.DeskFloor(_deskAnchor.position.y) : HoldGate.FallbackFloor(Head.position.y),
                left.PinchValid ? Ui.ToVec(left.PinchPoint) : (Vec3?)null, right.PinchValid ? Ui.ToVec(right.PinchPoint) : (Vec3?)null);
        }

        void OnCardChanged()
        {
            var req = Session.Card;
            var log = req == null ? "none" : req.Id + "/" + Session.Mode;
            if (log == _lastCardLog) return;
            _lastCardLog = log;
            if (req == null) Debug.Log("[Lotse] no block");
            else Debug.Log($"[Lotse] block {req.Id} risk={req.Risk.Level} twoHand={req.NeedsTwoHand} mode={Session.Mode}");
        }

        void OnDecided(ClientDecision d)
        {
            if (d.Behavior == "allow")
            {
                _allowedThisFrame = true;
                // a completed hold, the bar cleared: one clear higher note (hands give no haptics)
                Sounds.Play(Sfx.Cleared, _workspace.transform.position);
            }
            else Sounds.Play(Sfx.Denied, _workspace.transform.position); // "Deny": one lower, short note
            Debug.Log($"[Lotse] decided {d.Id} {d.Behavior} confirm={d.Confirm ?? "-"}");
            // the tour's approval and deny steps end on the decision itself (a completed gesture, a pressed "Deny")
            if (_tour.Active) _tour.Decided(d.Behavior == "allow", d.Confirm == "two-hand");
        }

        void ShowEnd()
        {
            Current = Phase.End;
            _workspace.Show(false); // the end card stands in its place
            EndSummary = Texts.EndSummary(Stats.TotalWaitingMs(Session.Store.Agents), Session.Stats.DangerousStopped);
            _endCard = Panels.End(_stage, new Vector3(0f, 0f, -0.01f), EndSummary, StartDemo);
            Debug.Log("[Lotse] end: " + EndSummary);
        }
    }
}
