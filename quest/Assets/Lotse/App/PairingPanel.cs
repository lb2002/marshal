using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Which of its screens the pairing panel shows.</summary>
    public enum PairView
    {
        /// <summary>The viewfinder, scanning.</summary>
        Scan,
        /// <summary>The viewfinder with the question after a code was seen.</summary>
        Ask,
        /// <summary>The viewfinder with "Connecting…" (a scanned attempt, or a stored session being resumed).</summary>
        Connecting,
        /// <summary>The viewfinder at rest: one line and "Scan again".</summary>
        Idle,
        /// <summary>The window with the address, the recent addresses and the number pad.</summary>
        Manual,
    }

    /// <summary>
    /// "Connect to my PC", hands only (U12: scanning first).
    ///
    /// The viewfinder is what opens: four corner brackets in the middle of the view, a metre away, following the head
    /// with a soft lag (ViewFollow), and under them a small card with one instruction, how to show the code, and two
    /// quiet controls ("Type the code instead", "Back"). It freezes while a ray rests on one of its controls, while a
    /// pinch is under way, and while the question shows. The press on "Connect to my PC" is the request to scan: the
    /// spatial data permission is asked for then.
    /// A pairing code seen never connects by itself: the question band asks "Pair with the PC at &lt;address&gt;?", and only
    /// its "Pair" starts the same PairWithCode the number pad uses, once (QrPairing has the rules, AskArming says when
    /// "Pair" counts). Then only "Connecting…" and "Back". A failed attempt returns to the viewfinder at rest with one
    /// line and "Scan again": scanning starts again only on that press.
    ///
    /// The window is behind "Type the code instead" (and is where a denied permission leads): left the last three
    /// bridge addresses and "New address", right a poke number pad for the 6-digit code (or for a new address), below
    /// one status line, "Scan instead" and "Back". Picking an address with a stored session resumes it at once, unless
    /// the bridge has refused that session already; OK with a full code pairs and clears the pad (the code is
    /// single-use). While an attempt is connecting, the address buttons do nothing and OK only says so.
    /// </summary>
    public sealed class PairingPanel : MonoBehaviour
    {
        const float Width = 0.38f;
        const float ColumnWidth = 0.17f;
        const float Key = 0.05f;
        const float KeyHeight = 0.042f;
        const float KeyGap = 0.008f;
        const float HostButtonHeight = Theme.H;

        enum Pad { Code, Host }

        HostBook _hosts;
        LiveSource _live;
        Action _onBack;
        readonly KeypadEntry _code = KeypadEntry.ForCode();
        readonly KeypadEntry _newHost = KeypadEntry.ForHost();
        Pad _pad = Pad.Code;
        string _error;
        LinkState _seenState;
        Transform _content;
        TextMeshPro _hostLine;
        TextMeshPro _entryLine;
        TextMeshPro _statusLine;

        // ---- the question ----
        /// <summary>
        /// "Pair" is switched off for the first 0.6 s of the question and then on for good (AskArming has the rule: by
        /// time alone; only a pinch whose beginning was seen after that moment may press it). A ray click belongs to the
        /// target it began on and fires only on a button that is on; a fingertip already through the face has to come
        /// out first.
        /// </summary>
        public const float AskArmSeconds = AskArming.Seconds;
        readonly AskArming _arming = new AskArming();
        bool _pinching;

        /// <summary>
        /// The hands this frame, from the app, before buttons and rays are stepped: usable (tracked, with the input
        /// focus) and pinching, per hand.
        /// </summary>
        public void Hands(bool leftUsable, float leftPinch, bool rightUsable, float rightPinch)
        {
            _pinching = (leftUsable && leftPinch >= GestureMachine.PinchOn) || (rightUsable && rightPinch >= GestureMachine.PinchOn);
            var band = ScanBand;
            if (band == null) return;
            // the strengths, not "pinching": the arming rule sees a pinch for as long as the ray click does (AskArming.PinchingFrom)
            _arming.Step(Time.unscaledDeltaTime, leftUsable, leftPinch, rightUsable, rightPinch);
            if (_arming.JustArmed)
            {
                // on for good: from now it looks and hovers like any other button
                band.Buttons[0].SetInteractable(true);
                LogQr("armed");
            }
            if (_arming.Ignored != null) Ev.Emit("qr", ("phase", "ignored"), ("reason", _arming.Ignored));
        }
        const float BandButtonWidth = 14f * Theme.S;

        IQrSource _qr;
        readonly QrPairing _scan = new QrPairing(LiveSource.Port);
        readonly List<byte[]> _read = new List<byte[]>();
        readonly HashSet<string> _logged = new HashSet<string>();
        string _scanNote;
        bool _scanOffered;
        WindowBand _band;

        // ---- the viewfinder ----
        /// <summary>The square's size as seen from the head, and the scale that gives its texts the window's size in angle at 1 m.</summary>
        public const float SquareDegrees = 14f;
        public const float FinderScale = 1.265f;
        /// <summary>The square's side in the viewfinder's own metres.</summary>
        public static readonly float Square = 2f * Mathf.Tan(SquareDegrees * 0.5f * Mathf.Deg2Rad) * ViewFollow.Distance / FinderScale;
        const float CardWidth = 0.36f;
        const float BracketArm = 0.034f;
        const float BracketLine = 0.0016f;
        const float BracketEdge = 0.0007f;
        const float SweepSeconds = 2.6f;

        bool _manual;
        bool _resuming;
        bool _rebuild;

        /// <summary>The stored session's one attempt is running ("Looking for your PC…").</summary>
        public bool Resuming => _resuming;
        PairView _built;
        Transform _head;
        Transform _finder;
        Transform _finderContent;
        Transform _brackets;
        Transform _sweep;
        MeshRenderer _sweepRenderer;
        readonly ViewFollow _follow = new ViewFollow();
        readonly List<PokeButton> _finderButtons = new List<PokeButton>();
        bool _snap;
        Vector3 _snapAt;
        float _snapSize;

        /// <summary>Scan mode is open (the viewfinder scans, or its question shows).</summary>
        public bool Scanning => _scan.Open;
        public QrPhase ScanPhase => _scan.Phase;
        /// <summary>The screen on show.</summary>
        public PairView View => _built;
        /// <summary>The question band, while it shows.</summary>
        public WindowBand ScanBand => _band != null && _band.IsShown ? _band : null;
        /// <summary>A question band is open (the app treats it like an open menu: nothing can be approved).</summary>
        public bool Asking => ScanBand != null;
        /// <summary>The one line in the window when the permission was denied or scanning is not available.</summary>
        public string ScanNote => _scanNote;
        /// <summary>The control that starts scanning ("Scan again" at rest, "Scan instead" in the window), or null.</summary>
        public PokeButton ScanButton { get; private set; }
        /// <summary>"Type the code instead", or null.</summary>
        public PokeButton TypeButton { get; private set; }
        /// <summary>The instruction line while scanning, or null.</summary>
        public string ScanInstructionShown { get; private set; }
        /// <summary>The viewfinder (null while the window shows).</summary>
        public Transform Finder => !_manual && _finder != null && _finder.gameObject.activeSelf ? _finder : null;
        /// <summary>The corner brackets, while the viewfinder shows.</summary>
        public Transform Brackets => _brackets;
        /// <summary>The viewfinder does not follow right now (a ray on a control, a pinch, the question).</summary>
        public bool FinderFrozen { get; private set; }
        /// <summary>The brackets stand on the code the tracker found.</summary>
        public bool Snapped => _snap && _scan.Phase == QrPhase.Asking;

        /// <summary>Scan mode is offered: a source exists and no connection exists or is being made.</summary>
        bool ScanOffered => _qr != null && (_live.State == LinkState.Idle || _live.State == LinkState.NeedsCode);

        /// <summary>The address OK pairs with, or null.</summary>
        public string Host { get; private set; }
        /// <summary>The status line as shown.</summary>
        public string Status { get; private set; }

        /// <param name="head">The head: the viewfinder follows it.</param>
        public static PairingPanel Create(Transform parent, Vector3 localPos, HostBook hosts, LiveSource live, Action onBack, IQrSource qr = null,
            Transform head = null)
        {
            var go = Ui.Child(parent, "PairingPanel", localPos);
            var p = go.AddComponent<PairingPanel>();
            p._qr = qr;
            p._head = head;
            p._hosts = hosts;
            p._live = live;
            p._onBack = onBack;
            p._seenState = live.State;
            var recent = hosts.Recent;
            p.Host = live.Host ?? (recent.Count > 0 ? recent[0] : null);
            p._manual = qr == null;
            // (U12a) a stored session is being tried (one short attempt): scanning starts when it does not answer
            p._resuming = !p._manual && live.State == LinkState.Connecting;
            // "Connect to my PC" was the request to scan: straight into scan mode (the permission is asked for now)
            if (!p._manual && p.ScanOffered) p.StartScan(LinkWhy.Press);
            else p.Build();
            return p;
        }

        /// <summary>The screen the state calls for.</summary>
        PairView Wanted
        {
            get
            {
                if (_manual) return PairView.Manual;
                if (_scan.Phase == QrPhase.Scanning) return PairView.Scan;
                if (_scan.Phase == QrPhase.Asking) return PairView.Ask;
                if (_scan.Phase == QrPhase.Attempting || _live.State == LinkState.Connecting || _live.State == LinkState.Retrying)
                    return PairView.Connecting;
                return PairView.Idle;
            }
        }

        void Build()
        {
            if (_content != null)
            {
                _content.gameObject.SetActive(false); // its buttons leave the poke registry now
                Destroy(_content.gameObject);
                _content = null;
            }
            if (_finderContent != null)
            {
                _finderContent.gameObject.SetActive(false);
                Destroy(_finderContent.gameObject);
                _finderContent = null;
            }
            _band = null; // it went with the old content
            _brackets = null;
            _sweep = null;
            _sweepRenderer = null;
            _statusLine = null;
            _finderButtons.Clear();
            ScanButton = null;
            TypeButton = null;
            ScanInstructionShown = null;
            Status = null;
            _scanOffered = ScanOffered;
            _built = Wanted;
            if (_built == PairView.Manual)
            {
                if (_finder != null) _finder.gameObject.SetActive(false);
                BuildManual();
            }
            else BuildFinder(_built);
        }

        /// <summary>The window: title, hint, the two columns, the status line, "Scan instead" and "Back".</summary>
        void BuildManual()
        {
            _content = Ui.Child(transform, "Content", Vector3.zero).transform;
            var c = _content;
            var y = -0.014f;
            var title = Ui.Text(c, "Title", Texts.PairTitle, new Vector3(0f, y, -0.001f), Width, Ui.TitleSize,
                Ui.TextColor, TextAlignmentOptions.Top, richText: true);
            y -= title.rectTransform.sizeDelta.y + 0.004f;
            var hint = Ui.Text(c, "Hint", Texts.PairHint, new Vector3(0f, y, -0.001f), Width, Ui.SmallSize, Ui.DimText,
                TextAlignmentOptions.Top);
            y -= hint.rectTransform.sizeDelta.y;
            if (_scanNote != null)
            {
                // why the window shows instead of the viewfinder
                y -= 2f * Theme.S;
                var note = Ui.Text(c, "ScanNote", _scanNote, new Vector3(0f, y, -0.001f), Width, Ui.SmallSize, Theme.WarnText,
                    TextAlignmentOptions.Top);
                y -= note.rectTransform.sizeDelta.y;
            }
            y -= 0.01f;

            var left = -Width * 0.5f + ColumnWidth * 0.5f;
            var right = Width * 0.5f - (3f * Key + 2f * KeyGap) * 0.5f;
            var bottom = Mathf.Min(BuildHosts(c, left, y), BuildPad(c, right, y));

            _statusLine = Ui.Text(c, "Status", "", new Vector3(0f, bottom - 0.01f, -0.001f), Width, Ui.BodySize,
                Theme.WarnText, TextAlignmentOptions.Top);
            RefreshStatus();
            y = bottom - 0.01f - 0.026f;
            const float w = 0.14f;
            if (_qr != null)
            {
                // (U12) the way back to the viewfinder; it is the screen that opens, so it needs no more than this
                ScanButton = PokeButton.Create(c, Texts.ScanInstead, new Vector3(-(w + Theme.ControlGap) * 0.5f, y - Theme.H * 0.5f, -0.002f),
                    new Vector2(w, Theme.H), StartScan, interactable: _scanOffered);
                PokeButton.Create(c, Texts.Back, new Vector3((w + Theme.ControlGap) * 0.5f, y - Theme.H * 0.5f, -0.002f), new Vector2(w, Theme.H), Back);
            }
            else PokeButton.Create(c, Texts.Back, new Vector3(0f, y - Theme.H * 0.5f, -0.002f), new Vector2(w, Theme.H), Back);
            y -= Theme.H + 0.014f;

            var h = -y;
            Ui.Panel(c, "Background", new Vector3(0f, -h * 0.5f, 0.002f), new Vector2(Width + 0.03f, h), Ui.PanelColor);
            c.localPosition = new Vector3(0f, h, 0f); // stands on its origin: the banner below never overlaps it
        }

        // ---- the viewfinder ----

        /// <summary>
        /// The viewfinder for `view`: the brackets (nothing filled: the real screen shows through), and under them either
        /// the card (an instruction or a status line, and its controls) or the question band.
        /// </summary>
        void BuildFinder(PairView view)
        {
            if (_finder == null)
            {
                _finder = new GameObject("PairingViewfinder").transform;
                _finder.localScale = Vector3.one * FinderScale;
                _follow.Reset();
            }
            _finder.gameObject.SetActive(isActiveAndEnabled);
            _finderContent = Ui.Child(_finder, "Content", Vector3.zero).transform;
            var c = _finderContent;
            DrawBrackets(c, view);
            const float U = Theme.U;
            var y = -(Square * 0.5f + 24f * U);
            if (view == PairView.Ask && _scan.Pending.HasValue)
            {
                // the card: "Pair with the PC at <address>?", the line to check, "Cancel" (quiet) and "Pair" (primary)
                var bandHeight = WindowBand.CaptionedHeight + 3f * Theme.S;
                _band = WindowBand.Create(c, "ScanBand", CardWidth + 4f * Theme.S, bandHeight, BandButtonWidth, Theme.GlassDense);
                _band.FirstAtRight = true;
                _band.transform.localPosition = new Vector3(0f, y, 0f);
                // the address is the parsed literal (digits and dots), shown as plain text
                _band.Show("qr", Texts.ScanFound, Texts.ScanAsk(_scan.Pending.Value.Host), Texts.ScanCheck, true,
                    (Texts.Pair, ConfirmScan), (Texts.Cancel, CancelScan));
                _band.Buttons[0].SetInteractable(false); // "Pair" is not pressable yet (see AskArming)
                _arming.Reset();
                return;
            }
            // the pill: one line that says what to do (or what happens), and text links at its right end
            var padX = 22f * U;
            var padY = 14f * U;
            var links = new List<(string label, float width, Action press)>();
            if (view != PairView.Connecting) links.Add((Texts.TypeInstead, 0.068f, TypeInstead));
            links.Add((Texts.Back, 0.042f, LeaveFinder));
            var linksW = 0f;
            foreach (var l in links) linksW += l.width;
            var again = view == PairView.Idle;
            var top = y;
            string words;
            if (view == PairView.Scan)
            {
                words = Texts.ScanInstruction;
                ScanInstructionShown = Texts.ScanInstruction;
            }
            else
            {
                words = StatusWords();
                Status = words;
            }
            var line = Ui.Text(c, view == PairView.Scan ? "ScanInstruction" : "Status", "", Vector3.zero, CardWidth, Ui.BodySize, Ui.TextColor);
            if (view != PairView.Scan) _statusLine = line;
            line.textWrappingMode = TextWrappingModes.NoWrap;
            line.text = words;
            var wordsW = line.GetPreferredValues(words, Mathf.Infinity, Mathf.Infinity).x + 0.002f;
            // short words: one row, the links after them; long ones (a failure's line): the words above, the controls below
            var oneRow = !again && wordsW + 22f * U + linksW <= CardWidth - 2f * padX + 0.06f;
            float width, bottom;
            if (oneRow)
            {
                width = padX + wordsW + 22f * U + linksW + padX - 12f * U; // a link's own padding hangs into the pill's
                var rowMid = top - padY - Theme.H * 0.5f;
                Ui.Middle(line, wordsW, Theme.H, TextAlignmentOptions.Left);
                line.transform.localPosition = new Vector3(-width * 0.5f + padX + wordsW * 0.5f, rowMid, -0.001f);
                var x = width * 0.5f - padX + 12f * U;
                for (var i = links.Count - 1; i >= 0; i--)
                {
                    x -= links[i].width;
                    AddLink(c, links[i].label, new Vector3(x + links[i].width * 0.5f, rowMid, -0.002f), links[i].width, links[i].press);
                }
                bottom = rowMid - Theme.H * 0.5f;
                if (view == PairView.Scan)
                {
                    // how to show the code: our own words, the command in the mono face
                    var how = Ui.Text(c, "ScanHow", Texts.ScanHowBefore + "<font=\"" + Fonts.Mono.name + "\"><color=#" +
                        ColorUtility.ToHtmlStringRGB(Theme.Mono) + ">" + Texts.ScanHowCommand + "</color></font>" + Texts.ScanHowAfter,
                        Vector3.zero, width - 2f * padX, Ui.SmallSize, Ui.DimText, TextAlignmentOptions.Left, richText: true);
                    Ui.Middle(how, width - 2f * padX, 19f * U, TextAlignmentOptions.Left);
                    how.transform.localPosition = new Vector3(0f, bottom - 9.5f * U + 4f * U, -0.001f);
                    bottom -= 19f * U - 4f * U;
                }
                bottom -= padY;
            }
            else
            {
                width = CardWidth;
                var inner = width - 2f * padX;
                line.textWrappingMode = TextWrappingModes.Normal;
                line.rectTransform.sizeDelta = new Vector2(inner, 0.01f);
                line.text = words;
                var rows = Ui.Rows(line, Theme.Row, out var shift);
                line.transform.localPosition = new Vector3(0f, top - padY + shift, -0.001f);
                var rowMid = top - padY - rows * Theme.Row - 8f * U - Theme.H * 0.5f;
                var x = width * 0.5f - padX + 12f * U;
                for (var i = links.Count - 1; i >= 0; i--)
                {
                    x -= links[i].width;
                    AddLink(c, links[i].label, new Vector3(x + links[i].width * 0.5f, rowMid, -0.002f), links[i].width, links[i].press);
                }
                if (again)
                {
                    var w = 20f * Theme.S;
                    ScanButton = PokeButton.Create(c, Texts.ScanAgain, new Vector3(-width * 0.5f + padX + w * 0.5f, rowMid, -0.002f), new Vector2(w, Theme.H),
                        StartScan, interactable: _scanOffered, primary: true);
                    _finderButtons.Add(ScanButton);
                }
                bottom = rowMid - Theme.H * 0.5f - padY;
            }
            var card = Theme.Shape(c, "Card", new Vector3(0f, (top + bottom) * 0.5f, 0.002f), new Vector2(width, top - bottom), Theme.GlassDense, Theme.CardMaterial);
            Theme.SetRadius(card, 14f * U);
            card.gameObject.AddComponent<RayBlocker>();
        }

        /// <summary>A text link on the pill ("Type it instead", "Back"): muted, no fill; the one control height.</summary>
        void AddLink(Transform c, string label, Vector3 at, float width, Action press)
        {
            var b = PokeButton.Create(c, label, at, new Vector2(width, Theme.H), press, kind: ButtonKind.Text);
            _finderButtons.Add(b);
            if (label == Texts.TypeInstead) TypeButton = b;
        }

        /// <summary>
        /// Four corner brackets around the square with rounded outer corners, one mesh; while scanning a yellow line
        /// sweeps inside them. With the question they turn yellow and stand on the code when the tracker gave its place,
        /// else they close in a little.
        /// </summary>
        void DrawBrackets(Transform c, PairView view)
        {
            _brackets = Ui.Child(c, "Brackets", Vector3.zero).transform;
            var m = new FlatMesh(_brackets, "Lines", 0f);
            var color = FlatMesh.Solid(view == PairView.Ask ? Look.Taxi : view == PairView.Scan || _resuming ? Look.TextPrimary : Look.TextSecondary);
            var edge = FlatMesh.Solid(Look.Well);
            var h = Square * 0.5f;
            var r = BracketArm * 0.25f; // the design: an arm of 40 units, a corner radius of 10
            for (var k = 0; k < 4; k++)
            {
                // clockwise from the top left corner; an arc's start is measured clockwise from 12 o'clock
                var sx = k == 0 || k == 3 ? -1f : 1f;
                var sy = k < 2 ? 1f : -1f;
                var corner = new Vector2(sx * h, sy * h);
                var centre = corner - new Vector2(sx * r, sy * r);
                var start = k == 0 ? 270f : k == 1 ? 0f : k == 2 ? 90f : 180f;
                // the horizontal and the vertical arm, each from the arc's end along the square's edge
                var hx0 = Mathf.Min(centre.x, corner.x - sx * BracketArm);
                var hx1 = Mathf.Max(centre.x, corner.x - sx * BracketArm);
                var hy = sy > 0f ? corner.y - BracketLine : corner.y;
                var vy0 = Mathf.Min(centre.y, corner.y - sy * BracketArm);
                var vy1 = Mathf.Max(centre.y, corner.y - sy * BracketArm);
                var vx = sx > 0f ? corner.x - BracketLine : corner.x;
                var arm = new Rect(hx0, hy, hx1 - hx0, BracketLine);
                var leg = new Rect(vx, vy0, BracketLine, vy1 - vy0);
                // a dark edge behind the light strokes: the brackets stay visible over a bright screen or a white desk
                m.Arc(centre, r - BracketLine - BracketEdge, r + BracketEdge, start, 90f, edge, 2f * FlatMesh.Layer);
                m.Rect(Grown(arm), edge, 2f * FlatMesh.Layer);
                m.Rect(Grown(leg), edge, 2f * FlatMesh.Layer);
                m.Arc(centre, r - BracketLine, r, start, 90f, color);
                m.Rect(arm, color);
                m.Rect(leg, color);
            }
            m.Apply();
            if (view == PairView.Ask)
            {
                if (_snap)
                {
                    // on the code itself, facing the head; a little larger than the code
                    var headAt = HeadPosition;
                    var away = Vector3.ProjectOnPlane(_snapAt - headAt, Vector3.up);
                    _brackets.SetPositionAndRotation(_snapAt, away.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(away, Vector3.up) : _finder.rotation);
                    var side = Mathf.Clamp(_snapSize * 1.5f, 0.05f, 0.4f);
                    var parent = _brackets.parent.lossyScale.x;
                    _brackets.localScale = Vector3.one * (side / (Square * Mathf.Max(1e-4f, parent)));
                }
                else _brackets.localScale = Vector3.one * 0.82f; // closed in on what it found
            }
            if (view != PairView.Scan) return;
            var sweep = Theme.Shape(_brackets, "Sweep", Vector3.zero, new Vector2(Square - 4f * Theme.S, BracketLine), SweepColor(0f), Theme.ChipMaterial);
            Theme.SetRim(sweep, 0f);
            _sweep = sweep.transform;
            _sweepRenderer = sweep;
            Animate();
        }

        /// <summary>A stroke with its dark edge around it.</summary>
        static Rect Grown(Rect r) => new Rect(r.x - BracketEdge, r.y - BracketEdge, r.width + 2f * BracketEdge, r.height + 2f * BracketEdge);

        static Color SweepColor(float alpha) => Ui.ToColor(Look.Taxi, alpha);

        /// <summary>The scanning cue: the line travels down the square and fades at both ends. "Calm": it rests in the middle.</summary>
        void Animate()
        {
            if (_sweep == null) return;
            var travel = Square * 0.5f - 3f * Theme.S;
            var t = Theme.Still ? 0.5f : Mathf.Repeat(Time.unscaledTime / SweepSeconds, 1f);
            _sweep.localPosition = new Vector3(0f, Mathf.Lerp(travel, -travel, Mathf.SmoothStep(0f, 1f, t)), 0f);
            Ui.SetColor(_sweepRenderer, SweepColor(Mathf.Min(1f, 1.6f * Mathf.Sin(Mathf.PI * t))));
        }

        Vector3 HeadPosition => _head != null ? _head.position : Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        Vector3 HeadForward => _head != null ? _head.forward : Camera.main != null ? Camera.main.transform.forward : Vector3.forward;

        /// <summary>
        /// After the app's frame: the viewfinder eases toward the middle of the view. It stays put while a ray rests on
        /// one of its controls, while a pinch is under way (so the target does not run away), and while the question shows.
        /// </summary>
        void LateUpdate()
        {
            if (_manual || _finder == null || !_finder.gameObject.activeSelf) return;
            var hovered = false;
            foreach (var b in _finderButtons) hovered |= b != null && b.HoverShown;
            FinderFrozen = _built == PairView.Ask || _pinching || hovered;
            _follow.Step(Time.unscaledDeltaTime, Ui.ToVec(HeadPosition), Ui.ToVec(HeadForward), FinderFrozen);
            _finder.SetPositionAndRotation(Ui.ToVector(_follow.Position), Quaternion.Euler(0f, _follow.YawDegrees, 0f));
            Animate();
        }

        static void LogQr(string phase) => Ev.Emit("qr", ("phase", phase));

        /// <summary>
        /// Scan mode opens: by "Connect to my PC" (the panel opens with it), "Scan again" or "Scan instead". The
        /// permission is asked for now, not earlier.
        /// </summary>
        void StartScan() => StartScan(LinkWhy.Press);

        void StartScan(LinkWhy why)
        {
            if (!ScanOffered || !_scan.Start(connected: _live.State == LinkState.Live)) return;
            Ev.Emit("link", ("phase", "scan"), ("reason", why));
            _manual = false;
            _error = null;
            _scanNote = null;
            _snap = false;
            _logged.Clear();
            _qr.Begin();
            Build();
        }

        /// <summary>"Cancel" on the question: the viewfinder at rest, nothing else. Scanning starts again on a press.</summary>
        void CancelScan()
        {
            if (!_scan.Open) return;
            _scan.Cancel();
            _qr.End();
            LogQr("cancelled");
            _error = null;
            Build();
        }

        /// <summary>"Type the code instead": the window. Scan mode closes.</summary>
        void TypeInstead()
        {
            if (_scan.Open)
            {
                _scan.Cancel();
                _qr.End();
                LogQr("cancelled");
            }
            _error = null;
            _manual = true;
            Build();
        }

        /// <summary>"Back" on the viewfinder: to the start panel.</summary>
        void LeaveFinder()
        {
            StopScan();
            _onBack?.Invoke();
        }

        /// <summary>
        /// "Pair" on the question, by a deliberate poke or ray click: the one connection attempt for this code, through
        /// the same PairWithCode the number pad uses, with exactly the address the question showed. The manual address
        /// (Host) is not touched: the scanned one becomes it, and a recent address, only when the attempt pairs.
        /// </summary>
        void ConfirmScan()
        {
            if (!_arming.PressAllowed) return; // not pressable yet, or pressed by a pinch whose beginning does not count
            var p = _scan.Confirm();
            if (!p.HasValue) return;
            LogQr("confirmed"); // nothing of the payload is logged, the address included
            Ev.Emit("link", ("phase", "attempt"), ("reason", LinkWhy.Scanned));
            _error = null;
            if (!_live.PairWithCode(p.Value.Host, p.Value.Code, single: true)) ScanAttemptFailed();
            else
            {
                _seenState = _live.State;
                Build(); // only "Connecting…" and "Back"
            }
        }

        /// <summary>
        /// The scanned code's one attempt did not pair: the connection is dropped for good (nothing retries), the code is
        /// remembered as failed, and the viewfinder rests with one line and "Scan again". Scanning starts again only on
        /// that press.
        /// </summary>
        void ScanAttemptFailed()
        {
            _scan.AttemptFailed();
            // dropped for good; this also clears the link's own words about the scanned attempt, so the window's
            // status line does not speak of an address the user never chose
            if (_live.State != LinkState.Live) _live.Disconnect();
            _seenState = _live.State;
            _error = Texts.ScanFailed;
            Build();
        }

        /// <summary>Scan mode closes without a word (the screen is hidden, the app pauses).</summary>
        void StopScan()
        {
            if (!_scan.Open) return;
            _scan.Stop();
            _qr.End();
        }

        /// <summary>The app pauses (the headset comes off, the system menu opens): scan mode closes; the viewfinder rests.</summary>
        public void Paused()
        {
            if (!_scan.Open) return;
            StopScan();
            Build();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) Paused();
        }

        /// <summary>Hidden (the window is being placed again): scan mode closes and the viewfinder goes with the panel.</summary>
        void OnDisable()
        {
            if (_finder != null) _finder.gameObject.SetActive(false);
            if (!_scan.Open) return;
            StopScan();
            _rebuild = true;
        }

        void OnEnable()
        {
            if (_finder != null && !_manual) _finder.gameObject.SetActive(true);
            if (!_rebuild) return;
            _rebuild = false;
            Build();
        }

        void OnDestroy()
        {
            if (_qr != null && _qr.State != QrSourceState.Off) _qr.End();
            if (_finder != null) Destroy(_finder.gameObject);
        }

        /// <summary>While scan mode is open: the permission's answer, then what the tracker sees.</summary>
        void PollScan()
        {
            if (_scan.Phase != QrPhase.Scanning) return;
            _qr.Read(_read);
            var state = _qr.State;
            if (state == QrSourceState.Denied || state == QrSourceState.Unavailable)
            {
                // no scanning here: the window, with one line that says why
                _scan.Stop();
                _qr.End();
                _scanNote = Texts.ScanDenied;
                _manual = true;
                LogQr("denied");
                Build();
                return;
            }
            foreach (var payload in _read)
            {
                var ask = _scan.Seen(payload);
                // one line per payload and scan, never its content
                if (ask || _logged.Count < 32 && _logged.Add(Convert.ToBase64String(payload))) LogQr(ask ? "seen" : "ignored");
                if (!ask) continue;
                // where the code is, if the tracker says so: the brackets go there
                _snap = _qr.TryPlace(payload, out _snapAt, out _snapSize);
                if (_snap && (_snapAt - HeadPosition).sqrMagnitude > 1e-4f)
                {
                    // the viewfinder's middle on the line from the head to the code, at once: the question then stands
                    // right under the code as the user sees it, and stays there
                    _follow.Reset();
                    _follow.Step(0f, Ui.ToVec(HeadPosition), Ui.ToVec(_snapAt - HeadPosition), false);
                    if (_finder != null) _finder.SetPositionAndRotation(Ui.ToVector(_follow.Position), Quaternion.Euler(0f, _follow.YawDegrees, 0f));
                }
                _qr.End(); // nothing is read while the question shows
                Sounds.Play(Sfx.Select, HeadPosition + HeadForward * 0.5f);
                Build();
                return;
            }
        }

        /// <summary>The address column: current address, the recent ones as buttons, "Neue Adresse".</summary>
        float BuildHosts(Transform c, float x, float y)
        {
            _hostLine = Ui.Text(c, "HostLine", Texts.HostLine(Host), new Vector3(x, y, -0.001f), ColumnWidth, Ui.BodySize,
                Ui.TextColor, TextAlignmentOptions.Top);
            y -= _hostLine.rectTransform.sizeDelta.y + 0.008f;
            foreach (var host in _hosts.Recent)
            {
                var chosen = host;
                PokeButton.Create(c, host, new Vector3(x, y - HostButtonHeight * 0.5f, -0.002f),
                    new Vector2(ColumnWidth, HostButtonHeight), () => ChooseHost(chosen)).LogAs = "recent-host"; // never the address
                y -= HostButtonHeight + Theme.ControlGap;
            }
            PokeButton.Create(c, Texts.NewHost, new Vector3(x, y - HostButtonHeight * 0.5f, -0.002f),
                new Vector2(ColumnWidth, HostButtonHeight), StartNewHost);
            return y - HostButtonHeight;
        }

        /// <summary>The pad column: what is typed, then 1-9 and a last row (back 0 OK, or . 0 back + OK).</summary>
        float BuildPad(Transform c, float x, float y)
        {
            _entryLine = Ui.Text(c, "Entry", EntryText(), new Vector3(x, y, -0.001f), 3f * Key + 2f * KeyGap, Ui.BodySize * 1.2f,
                Ui.TextColor, TextAlignmentOptions.Top);
            y -= _entryLine.rectTransform.sizeDelta.y + 0.008f;
            string[] keys = _pad == Pad.Code
                ? new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", Texts.Backspace, "0", Texts.Ok }
                : new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", Texts.Backspace };
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var kx = x + (i % 3 - 1) * (Key + KeyGap);
                var ky = y - (i / 3) * (KeyHeight + KeyGap) - KeyHeight * 0.5f;
                // digits of the code or the address: never logged
                PokeButton.Create(c, key, new Vector3(kx, ky, -0.002f), new Vector2(Key, KeyHeight), () => Press(key)).MarkSecret();
            }
            y -= 4f * (KeyHeight + KeyGap);
            if (_pad == Pad.Host)
            {
                PokeButton.Create(c, Texts.Ok, new Vector3(x, y - KeyHeight * 0.5f, -0.002f),
                    new Vector2(3f * Key + 2f * KeyGap, KeyHeight), () => Press(Texts.Ok), primary: true);
                y -= KeyHeight + KeyGap;
            }
            return y + KeyGap;
        }

        string EntryText() => _pad == Pad.Code ? Texts.CodeLine(_code.Text) : Texts.HostLine(_newHost.Text + "_");

        bool Connecting => _live.State == LinkState.Connecting;

        void Press(string key)
        {
            if (key == Texts.Ok && _pad == Pad.Code && Connecting)
            {
                // one attempt at a time; say so instead of ignoring the press
                _error = Texts.ConnectingWait;
                RefreshStatus();
                return;
            }
            _error = null;
            var entry = _pad == Pad.Code ? _code : _newHost;
            if (key == Texts.Backspace) entry.Backspace();
            else if (key == Texts.Ok) Submit();
            else entry.Type(key[0]);
            if (_entryLine != null) Ui.SetPlain(_entryLine, EntryText());
            RefreshStatus();
        }

        void Submit()
        {
            if (_pad == Pad.Host)
            {
                if (!HostBook.IsValidHost(_newHost.Text))
                {
                    _error = Texts.BadHost;
                    return;
                }
                Host = _newHost.Text;
                _pad = Pad.Code;
                Build();
                return;
            }
            if (Host == null)
            {
                _error = Texts.NeedHost;
                return;
            }
            if (LiveSource.IsPairingCode(_code.Text))
            {
                Debug.Log("[Lotse] pairing with a typed code");
                Ev.Emit("link", ("phase", "attempt"), ("reason", LinkWhy.Typed));
                _live.PairWithCode(Host, _code.Text);
                _code.Clear(); // single-use: never sent again from this pad
            }
            else if (_code.Text.Length == 0 && _live.Resume(Host))
            {
                Debug.Log("[Lotse] resuming");
            }
            else
            {
                _error = Texts.NeedCode;
            }
        }

        void ChooseHost(string host)
        {
            if (Connecting) return;
            _error = null;
            Host = host;
            Ui.SetPlain(_hostLine, Texts.HostLine(Host));
            if (_hosts.SessionFor(host) != null && _live.Resume(host)) Debug.Log("[Lotse] resuming");
            RefreshStatus();
        }

        void StartNewHost()
        {
            if (Connecting) return;
            _error = null;
            _newHost.Clear();
            _pad = Pad.Host;
            Build();
        }

        void Back()
        {
            if (_pad == Pad.Host)
            {
                _pad = Pad.Code;
                _error = null;
                Build();
                return;
            }
            _onBack?.Invoke();
        }

        void Update()
        {
            PollScan();
            if (_live.State != _seenState)
            {
                _seenState = _live.State;
                // (U12a) the stored session's one attempt did not pair: scanning, at once. The press on "Connect to my
                // PC" was the request to scan, so this needs no further press.
                if (_resuming && _seenState != LinkState.Connecting)
                {
                    _resuming = false;
                    if (_seenState != LinkState.Live)
                    {
                        var why = _live.LastFailure == LinkFailure.Timeout ? LinkWhy.Timeout : _live.LastFailure == LinkFailure.Closed ? LinkWhy.Closed :
                            _live.LastFailure == LinkFailure.Refused ? LinkWhy.Refused : LinkWhy.Unknown;
                        Ev.Emit("link", ("phase", "resume-failed"), ("reason", why));
                        if (_live.State == LinkState.Retrying) _live.Disconnect();
                        _error = null;
                        StartScan(LinkWhy.AfterResume);
                        if (!_scan.Open) Build();
                        return;
                    }
                }
                // the outcome of the one attempt a scanned code gets
                // anything but Live ends it: Retrying too (an address that does not answer is not waited for)
                if (_scan.Phase == QrPhase.Attempting && _seenState != LinkState.Connecting)
                {
                    if (_seenState == LinkState.Live) _scan.AttemptSucceeded();
                    else
                    {
                        ScanAttemptFailed();
                        return;
                    }
                }
                if (_error == Texts.ConnectingWait) _error = null; // the attempt has an outcome now
                // a refused code is used up: start the next one from empty
                if (_seenState == LinkState.NeedsCode)
                {
                    _code.Clear();
                    if (_manual && _pad == Pad.Code && _entryLine != null) Ui.SetPlain(_entryLine, EntryText());
                }
            }
            // a connection is being made (a stored session resumed): nothing scans meanwhile
            if (_scan.Open && !ScanOffered) StopScan();
            // the screen follows the state; scanning is offered only while no connection exists or is being made
            if (Wanted != _built || _scanOffered != ScanOffered) Build();
            RefreshStatus();
        }

        /// <summary>What the status line says now.</summary>
        string StatusWords()
        {
            if (_manual)
            {
                var text = _error ?? _live.Status;
                return string.IsNullOrEmpty(text) ? (Host == null ? Texts.NeedHost : Texts.NeedCode) : text;
            }
            // the viewfinder speaks in its own words only: never the link's status about an address the user
            // cannot see ("Bridge not reachable. Trying again in N s")
            return _built == PairView.Connecting ? (_resuming ? Texts.Looking : Texts.Connecting) : _error ?? Texts.ScanIdle;
        }

        void RefreshStatus()
        {
            var text = StatusWords();
            if (text == Status || _statusLine == null) return;
            Status = text;
            Ui.SetPlain(_statusLine, text);
            if (_manual) Ui.FitHeight(_statusLine); // the pill's line keeps the box it was laid out in
        }
    }
}
