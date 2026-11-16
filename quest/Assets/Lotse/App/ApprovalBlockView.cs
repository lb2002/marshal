using System;
using Lotse.Core;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The request in an open row (U5a's approval block; U13's layout), shown only for the open agent's oldest open
    /// request. The sentence (what the agent wants) is the window's headline; here, on the raised block's left column:
    /// 1. the command, path or URL in the mono inset, plain, two lines at most, "(shortened)" when cut (a cut the
    ///    bridge made, or one made here: then the session asks for both hands);
    /// 2. for a two-hand request the facts as large values over small captions ("214 / files", "Outside / this
    ///    project", "Gone / no way back" in red): only what the bridge is sure of, a fact that is not known has no
    ///    cell; under them one muted line, the reason sentence and "Checked: path". For any other request the rest of
    ///    the explanation in muted, and no facts: the risk shows in the bar's length and in the facts being there.
    /// 3. along the bottom the stop bar and under it the instruction; at the right "Later" (quiet) and "Deny" (deny).
    /// The stop bar is a display of the hold's progress and nothing else (StopBar): 21 lights going dark from both
    /// ends toward the middle for a two-hand request, 9 going dark from left to right for a one-hand one. It is never
    /// a control and nothing is pressable on it. There is no button for allow: allow stays a gesture. While holding
    /// the instruction reads "Keep holding"; when a hold breaks off the lights come back on quickly, outer ones last;
    /// once allowed all lights are green for a moment and it reads "Cleared. Codex is moving again". A deny-only
    /// request (Gemini) has no bar and "Deny" only. A thin line along the bottom edge shows the time left until the
    /// bridge gives up. Its place can also hold one line after the request closed. It never grows past MaxHeight:
    /// what does not fit is capped to fewer lines (the reason first, then the command, whose cut asks for both hands).
    /// Origin: its top left corner; the face looks along the parent's -z. Every agent text is plain and capped.
    /// </summary>
    public sealed class ApprovalBlockView : MonoBehaviour
    {
        const float U = Theme.U;
        const float Gap = 22f * U;
        /// <summary>The plan column at the block's right and the gap before it (the conversation draws the plan).</summary>
        public const float PlanWidth = 250f * U;
        public const float PlanGap = 44f * U;
        const float MonoPitch = 25f * U;
        const float InsetPadX = 18f * U;
        const float InsetPadY = 15f * U;
        const float FactValue = 34f * U;
        const float FactCaption = 19f * U;
        const float FactGap = 40f * U;
        const float LinePitch = 22f * U;
        const float BarHeight = 15f * U;
        const float LightGap = 6f * U;
        /// <summary>The bottom zone: the bar, the instruction under it, and their padding.</summary>
        const float Zone = 57f * U;
        const float CountdownHeight = 2f * U;
        /// <summary>The countdown line shows from this share of the time on.</summary>
        public const float CountdownFrom = 0.3f;
        /// <summary>How fast the lights come back on when a hold breaks off (the whole bar in this many seconds).</summary>
        const float RelightSeconds = 0.22f;
        static readonly Vector2 ButtonSize = new Vector2(UiMetrics.ButtonWidth, Theme.H);

        /// <summary>The room it has (the conversation sets it): a taller block is capped to fewer lines.</summary>
        public float MaxHeight { get; set; } = float.PositiveInfinity;
        /// <summary>The height its top part (command and facts) has at least: the plan next to it needs that much.</summary>
        public float MinTop { get; set; }

        float _width;
        bool _denyOnly;
        bool _twoHand;
        Transform _content;
        FlatMesh _barMesh;
        Rect _barRect;
        float _lightWidth;
        MeshRenderer _countdown;
        TextMeshPro _hint;
        LineColors _hintColors;
        string _hintBase;
        HoldCue _cue;
        float _shownProgress = -1f;
        float _drawnProgress;
        int _drawnDark = -1;
        bool _cleared;
        double? _shownTime = -1;

        public float Height { get; private set; }
        public bool IsShown => _content != null && gameObject.activeSelf;
        /// <summary>The request shown (null for a note, the cleared state or nothing).</summary>
        public string RequestId { get; private set; }
        public PokeButton DenyButton { get; private set; }
        public PokeButton LaterButton { get; private set; }
        /// <summary>The request's sentence (the window's headline shows it; kept here for the owner).</summary>
        public string HeadlineShown { get; private set; } = "";
        /// <summary>The risk word of the request (not drawn in U13: the bar's length and the facts say it).</summary>
        public string RiskShown { get; private set; } = "";
        public string SubjectShown { get; private set; } = "";
        public string DetailShown { get; private set; } = "";
        /// <summary>The facts (U7): "Checked: …", the cells before the verdict, the verdict's cell, the reason; empty when none.</summary>
        public string CheckedShown { get; private set; } = "";
        public string FactsShown { get; private set; } = "";
        public string VerdictShown { get; private set; } = "";
        public string ReasonShown { get; private set; } = "";
        /// <summary>The verdict's colour (red only for "Gone").</summary>
        public Color VerdictColor { get; private set; }
        public string HintShown => _hint != null ? _hint.text : "";
        /// <summary>The one line in the block's place after a request closed (or the cleared line), or null.</summary>
        public string NoteShown { get; private set; }
        public float ProgressShown => _shownProgress;
        /// <summary>The stop bar's lights (0: no bar), how many of them are lit as drawn, and whether they are green.</summary>
        public int LightsShown { get; private set; }
        public int LitShown { get; private set; }
        public bool ClearedShown => _cleared;
        /// <summary>The header menu stands over it (U8): dimmed, "Close the menu to answer", Deny and Later not pressable.</summary>
        public bool Covered { get; private set; }
        readonly System.Collections.Generic.List<(TextMeshPro t, Color c)> _undimmed = new System.Collections.Generic.List<(TextMeshPro, Color)>();
        string _hintBeforeCover;
        /// <summary>The countdown line's share of the width (null: none).</summary>
        public float? TimeShown { get; private set; }

        /// <summary>The block's centre in world space (the hold gate checks the head faces it).</summary>
        public Vector3 Center => transform.TransformPoint(new Vector3(_width * 0.5f, -Height * 0.5f, 0f));

        Action<string> _onDeny;
        Action _onLater;

        public static ApprovalBlockView Create(Transform parent, float width, Action<string> onDeny, Action onLater)
        {
            var v = Ui.Child(parent, "ApprovalBlock", Vector3.zero).AddComponent<ApprovalBlockView>();
            v._width = width;
            v._onDeny = onDeny;
            v._onLater = onLater;
            v.gameObject.SetActive(false);
            return v;
        }

        public void Hide()
        {
            Clear();
            RequestId = null;
            NoteShown = null;
            Height = 0f;
            gameObject.SetActive(false);
        }

        /// <summary>The block for `req` in `mode`; returns whether its subject had to be cut here.</summary>
        /// <param name="denyOnly">The agent can only be denied from here (Gemini): no bar, "Deny" only.</param>
        public bool Show(PermRequest req, Explanation explain, CardMode mode, int more, bool stripRepo, ImpactFacts? facts = null, bool denyOnly = false)
        {
            _denyOnly = denyOnly;
            gameObject.SetActive(true);
            RequestId = req.Id;
            NoteShown = null;
            // the full block first; when it is taller than its room: the reason and the detail on one line, then the
            // command on one line too (cut here: the session then asks for both hands)
            var cut = Build(req, explain, mode, more, stripRepo, facts, 2, 2);
            if (Height > MaxHeight + 1e-5f) cut = Build(req, explain, mode, more, stripRepo, facts, 2, 1);
            if (Height > MaxHeight + 1e-5f) cut = Build(req, explain, mode, more, stripRepo, facts, 1, 1);
            return cut;
        }

        bool Build(PermRequest req, Explanation explain, CardMode mode, int more, bool stripRepo, ImpactFacts? facts, int subjectLines, int restLines)
        {
            Clear();
            var c = _content;
            _twoHand = req.NeedsTwoHand;
            var hold = mode == CardMode.Hold;
            var leftW = _width - PlanGap - PlanWidth;
            var y = 0f;
            RiskShown = Texts.RiskWord(req);
            HeadlineShown = Texts.Headline(explain, req);

            // 1. the subject in the mono inset over the left column, plain
            var subject = Ui.Mono(c, "Subject", Vector3.zero, leftW - 2f * InsetPadX);
            Ui.SetPlain(subject, Texts.Subject(req, stripRepo));
            Ui.FitHeight(subject);
            var cut = Ui.CapLines(subject, subjectLines);
            SubjectShown = subject.text;
            var rows = Ui.Rows(subject, MonoPitch, out var shift);
            var insetH = rows * MonoPitch + 2f * InsetPadY;
            Place(subject, InsetPadX, y - InsetPadY + shift);
            var inset = Ui.Panel(c, "Inset", new Vector3(leftW * 0.5f, y - insetH * 0.5f, -0.0003f), new Vector2(leftW, insetH), Theme.Well, Ui.QueueOnPanel);
            Theme.SetRadius(inset, Theme.InsetRadius);
            y -= insetH + Gap;

            // 2. the facts of a two-hand request (U7): what it would hit, the verdict, and one muted line with the
            // reason and what the bridge checked. They stand for the rest of the explanation, drawn only without them.
            CheckedShown = FactsShown = VerdictShown = ReasonShown = "";
            DetailShown = "";
            if (facts.HasValue) y = FactsRow(c, facts.Value, leftW, y);
            else
            {
                var detail = Texts.Detail(explain);
                if (detail.Length > 0)
                {
                    var d = Ui.Text(c, "Detail", detail, Vector3.zero, leftW, Theme.Small, Theme.Muted);
                    Ui.CapLines(d, restLines);
                    DetailShown = d.text;
                    rows = Ui.Rows(d, LinePitch, out shift);
                    Place(d, 0f, y + shift);
                    y -= rows * LinePitch + Gap;
                }
            }
            if (more > 0)
            {
                // "+2 more": under the plan, at the block's right edge
                var m = Ui.Text(c, "More", Texts.MoreRequests(more), Vector3.zero, PlanWidth, Theme.Small, Theme.Muted, TextAlignmentOptions.Right);
                Ui.Middle(m, PlanWidth, LinePitch, TextAlignmentOptions.Right);
                m.transform.localPosition = new Vector3(_width - PlanWidth * 0.5f, Mathf.Min(y + Gap + LinePitch * 0.5f, -MinTop + LinePitch * 0.5f), -0.001f);
            }
            // the top part is at least as high as the plan beside it
            y = Mathf.Min(y, -(MinTop + Gap));

            // 3. the bottom zone: the stop bar and the instruction at the left, "Later" and "Deny" at the right
            var zoneMid = y - Zone * 0.5f;
            _hint = null;
            _barMesh = null;
            DenyButton = null;
            LaterButton = null;
            LightsShown = LitShown = 0;
            {
                // (once sent the bar stays, dark, and the line says "Sent …" until the bridge closes the request)
                var buttons = _denyOnly ? ButtonSize.x : 2f * ButtonSize.x + Theme.ControlGap;
                var barRoom = _width - buttons - 28f * U;
                var hintTop = y - 6f * U;
                if (!_denyOnly)
                {
                    // every light is as wide as a two-hand bar's: a one-hand bar is shorter, and that is its risk
                    _lightWidth = (barRoom - (StopBar.TwoHandLights - 1) * LightGap) / StopBar.TwoHandLights;
                    var n = StopBar.Lights(_twoHand);
                    _barRect = new Rect(0f, y - 6f * U - BarHeight, n * _lightWidth + (n - 1) * LightGap, BarHeight);
                    _barMesh = new FlatMesh(c, "StopBar", -0.0004f);
                    LightsShown = n;
                    _shownProgress = hold ? 0f : 1f;
                    _drawnProgress = _shownProgress;
                    _drawnDark = -1;
                    DrawBar();
                    hintTop = _barRect.yMin - 11f * U;
                }
                else hintTop = zoneMid + LinePitch * 0.5f;
                var reason = Texts.TwoHandReason(req);
                _hintBase = !hold ? Texts.SentWaiting : _denyOnly ? Texts.DenyOnlyHint(req) : Texts.GestureHint(req) + (reason != null ? ". " + reason : "");
                _hint = Ui.Text(c, "Hint", "", Vector3.zero, barRoom, Theme.Small, Theme.Muted);
                _hint.textWrappingMode = TextWrappingModes.NoWrap;
                _hintColors = LineColors.On(_hint);
                _hintBase = Ui.OneLine(_hint, _hintBase, barRoom);
                SetHint((hold ? Texts.Cue(_cue) : null) ?? _hintBase);
                Ui.Middle(_hint, barRoom, LinePitch, TextAlignmentOptions.Left);
                _hint.transform.localPosition = new Vector3(barRoom * 0.5f, hintTop - LinePitch * 0.5f, -0.001f);
                var x = _width - ButtonSize.x * 0.5f;
                var id = req.Id;
                if (hold) DenyButton = PokeButton.Create(c, Texts.Deny, new Vector3(x, zoneMid, -0.002f), ButtonSize, () => _onDeny?.Invoke(id), kind: ButtonKind.Deny);
                if (hold && !_denyOnly)
                    LaterButton = PokeButton.Create(c, Texts.Later, new Vector3(x - ButtonSize.x - Theme.ControlGap, zoneMid, -0.002f), ButtonSize,
                        () => _onLater?.Invoke());
            }
            y -= Zone;
            Height = -y;

            // the countdown: a thin line under the block, in the row's bottom padding
            _countdown = Ui.Panel(c, "TimeLeft", new Vector3(_width * 0.5f, -Height - 9f * U, -0.001f), new Vector2(_width, CountdownHeight),
                Theme.Stop, Ui.QueueOnPanel);
            Theme.SetRadius(_countdown, CountdownHeight * 0.5f);
            _countdown.gameObject.SetActive(false);
            _shownTime = -1;
            TimeShown = null;
            return cut;
        }

        /// <summary>
        /// The cells ("214" over "files", "Gone" over "no way back" in red), then one muted line: the bridge's reason
        /// and "Checked: ~/code/legacy-site". No green, no check mark: nothing here reassures. Returns the new top.
        /// </summary>
        float FactsRow(Transform c, ImpactFacts f, float leftW, float y)
        {
            var x = 0f;
            var cells = f.Cells;
            var before = new System.Text.StringBuilder();
            // (U14, review of U13) the verdict is the last cell and is never the one left out: its width is kept free
            // first, and the cells before it are shown only as far as they fit beside it
            var reserved = 0f;
            for (var i = 0; cells != null && i < cells.Count; i++)
            {
                var cell = cells[i];
                var last = i == cells.Count - 1;
                var v = Ui.Text(c, "Fact", cell.Value, Vector3.zero, leftW, Theme.Facts, cell.Red ? Theme.Stop : Theme.Text);
                v.font = Fonts.SemiBold;
                v.characterSpacing = -2f;
                v.textWrappingMode = TextWrappingModes.NoWrap;
                var k = Ui.Text(c, "FactCaption", cell.Caption, Vector3.zero, leftW, Theme.Small, Theme.Muted);
                k.textWrappingMode = TextWrappingModes.NoWrap;
                var w = Mathf.Max(v.GetPreferredValues(v.text, Mathf.Infinity, Mathf.Infinity).x, k.GetPreferredValues(k.text, Mathf.Infinity, Mathf.Infinity).x) + 0.001f;
                if (i == 0 && cells.Count > 1)
                {
                    var verdict = cells[cells.Count - 1];
                    reserved = FactGap + Mathf.Max(v.GetPreferredValues(verdict.Value, Mathf.Infinity, Mathf.Infinity).x,
                        k.GetPreferredValues(verdict.Caption, Mathf.Infinity, Mathf.Infinity).x) + 0.001f;
                }
                if (!last && x + w + reserved > leftW + 1e-5f) // no room for this cell beside the verdict: it is left out rather than squeezed
                {
                    Destroy(v.gameObject);
                    Destroy(k.gameObject);
                    continue;
                }
                Ui.Middle(v, w, FactValue, TextAlignmentOptions.Left);
                v.transform.localPosition = new Vector3(x + w * 0.5f, y - FactValue * 0.5f, -0.001f);
                Ui.Middle(k, w, FactCaption, TextAlignmentOptions.Left);
                k.transform.localPosition = new Vector3(x + w * 0.5f, y - FactValue - FactCaption * 0.5f, -0.001f);
                x += w + FactGap;
                if (last)
                {
                    VerdictShown = cell.ToString();
                    VerdictColor = cell.Red ? Theme.Stop : Theme.Text;
                }
                else before.Append(before.Length > 0 ? " · " : "").Append(cell.ToString());
            }
            FactsShown = before.ToString();
            y -= FactValue + FactCaption;
            var line = f.Reason != null && f.Checked != null ? f.Reason + "  ·  " + f.Checked : f.Reason ?? f.Checked;
            if (line != null)
            {
                var r = Ui.Text(c, "Reason", "", Vector3.zero, leftW, Theme.Small, Theme.Muted);
                r.textWrappingMode = TextWrappingModes.NoWrap;
                Ui.SetPlain(r, Ui.OneLine(r, line, leftW));
                Ui.Middle(r, leftW, LinePitch, TextAlignmentOptions.Left);
                r.transform.localPosition = new Vector3(leftW * 0.5f, y - 8f * U - LinePitch * 0.5f, -0.001f);
                ReasonShown = f.Reason ?? "";
                CheckedShown = f.Checked ?? "";
                y -= 8f * U + LinePitch;
            }
            return y - Gap;
        }

        /// <summary>The instruction line; "both hands" in ink.</summary>
        void SetHint(string text)
        {
            if (_hint == null) return;
            _hintColors.Clear();
            var at = text.IndexOf(Texts.HintTwoHandStrong, StringComparison.Ordinal);
            if (at >= 0 && text.StartsWith(Texts.HintTwoHand, StringComparison.Ordinal))
                _hintColors.Add(at, at + Texts.HintTwoHandStrong.Length, Theme.Text);
            _hintColors.Set(text);
        }

        /// <summary>The lights for the progress drawn now: lit in stop red with one glow around them, dark in faint; all green once cleared.</summary>
        void DrawBar()
        {
            if (_barMesh == null) return;
            var n = LightsShown;
            var dark = _cleared ? 0 : StopBar.Dark(_drawnProgress, _twoHand);
            if (dark == _drawnDark) return;
            _drawnDark = dark;
            var m = _barMesh;
            m.Clear();
            var on = _cleared ? Look.Go : Look.Stop;
            int first = -1, last = -1, lit = 0;
            for (var i = 0; i < n; i++)
            {
                var isLit = _cleared || StopBar.Lit(i, _drawnProgress, _twoHand);
                if (isLit)
                {
                    if (first < 0) first = i;
                    last = i;
                    lit++;
                }
                var r = new Rect(_barRect.x + i * (_lightWidth + LightGap), _barRect.y, _lightWidth, BarHeight);
                m.RoundedRect(r, 4f * U, FlatMesh.Solid(isLit ? on : Look.Faint));
            }
            LitShown = lit;
            if (first >= 0)
            {
                // the glow: one soft halo around the lit lights, behind them (a dark light does not glow)
                var x0 = _barRect.x + first * (_lightWidth + LightGap);
                var x1 = _barRect.x + last * (_lightWidth + LightGap) + _lightWidth;
                var run = new Rect(x0, _barRect.y, x1 - x0, BarHeight);
                var glow = FlatMesh.Over(on, 0.22f, Look.Raised);
                m.RoundedRect(run, 4f * U, glow, 2f * FlatMesh.Layer);
                m.Halo(run, 4f * U, 10f * U, glow, FlatMesh.Solid(Look.Raised), 2f * FlatMesh.Layer);
            }
            m.Apply();
        }

        /// <summary>One line in the block's place after its request closed ("Denied", "Timed out. …").</summary>
        public void ShowNote(string text, Color color)
        {
            gameObject.SetActive(true);
            Clear();
            RequestId = null;
            NoteShown = text;
            var t = Ui.Text(_content, "Note", text, Vector3.zero, _width, Theme.Small, color);
            Ui.CapLines(t, 1);
            Ui.Middle(t, _width, LinePitch, TextAlignmentOptions.Left);
            t.transform.localPosition = new Vector3(_width * 0.5f, -LinePitch * 0.5f, -0.001f);
            Height = LinePitch;
        }

        /// <summary>
        /// The request shown was allowed: every light green for a moment, the line reads `text` ("Cleared. Codex is
        /// moving again"), nothing is pressable and nothing is asked any more. False when no request's bar is shown.
        /// </summary>
        public bool ShowCleared(string text)
        {
            if (!IsShown || RequestId == null) return false;
            RequestId = null;
            NoteShown = text;
            _cleared = true;
            _drawnDark = -1;
            DrawBar();
            if (DenyButton != null) DenyButton.gameObject.SetActive(false);
            if (LaterButton != null) LaterButton.gameObject.SetActive(false);
            if (_countdown != null) _countdown.gameObject.SetActive(false);
            if (_hint != null)
            {
                _hintColors.Clear();
                var dot = text.IndexOf('.');
                if (dot > 0) _hintColors.Add(0, dot + 1, Theme.Text);
                _hintColors.Set(text);
            }
            return true;
        }

        /// <summary>
        /// Covered by the header menu (U8): every text dimmed, the hint says "Close the menu to answer", Deny and Later are
        /// not pressable. Uncovered: as it was. Nothing here decides; the app keeps the hands from the session meanwhile.
        /// </summary>
        public void SetCovered(bool on, string line = null)
        {
            line = line ?? Texts.CloseMenuToAnswer;
            if (on && Covered && _hint != null && _hint.text != line) SetHint(line);
            if (on == Covered || _content == null) return;
            Covered = on;
            if (on)
            {
                _undimmed.Clear();
                foreach (var t in _content.GetComponentsInChildren<TextMeshPro>(true))
                {
                    _undimmed.Add((t, t.color));
                    t.color = new Color(t.color.r, t.color.g, t.color.b, t.color.a * 0.35f);
                }
                if (_hint != null)
                {
                    _hintBeforeCover = _hint.text;
                    SetHint(line);
                    _hint.color = Theme.Text;
                }
            }
            else
            {
                foreach (var (t, c) in _undimmed)
                    if (t != null) t.color = c;
                _undimmed.Clear();
                if (_hint != null && _hintBeforeCover != null) SetHint(_hintBeforeCover);
            }
            if (DenyButton != null) DenyButton.SetInteractable(!on);
            if (LaterButton != null) LaterButton.SetInteractable(!on);
        }

        /// <summary>The hint's text now (the menu's line while covered).</summary>
        public string CoverLine => Covered && _hint != null ? _hint.text : null;

        /// <summary>The hold's progress (Hold mode only): the bar shows it, and nothing else.</summary>
        public void SetProgress(float progress)
        {
            if (_cleared || Mathf.Approximately(progress, _shownProgress)) return;
            var was = _shownProgress;
            _shownProgress = progress;
            // going dark follows the hold exactly; coming back on is quick but not at once (see Update)
            if (progress >= _drawnProgress || Theme.Still)
            {
                _drawnProgress = progress;
                DrawBar();
            }
            if ((progress > 0f) != (was > 0f)) ShowInstruction();
        }

        /// <summary>The lights come back on after a hold broke off: the middle first, the outer ones last.</summary>
        void Update()
        {
            if (_barMesh == null || _cleared || _drawnProgress <= _shownProgress) return;
            _drawnProgress = Mathf.Max(_shownProgress, _drawnProgress - Time.deltaTime / RelightSeconds);
            DrawBar();
        }

        /// <summary>Every frame in Hold mode: the gate's cue in place of the hint, or the hint again.</summary>
        public void SetCue(HoldCue cue)
        {
            if (cue == _cue) return;
            _cue = cue;
            ShowInstruction();
        }

        /// <summary>The instruction: the gate's cue when it holds a pinch back, "Keep holding" while a hold runs, else the hint.</summary>
        void ShowInstruction()
        {
            if (_hint == null || Covered || _cleared) return;
            var text = _hintBase == Texts.SentWaiting ? _hintBase : Texts.Cue(_cue) ?? (_shownProgress > 0f && !_denyOnly ? Texts.KeepHolding : _hintBase);
            if (_hint.text != text) SetHint(text);
        }

        /// <summary>The countdown line at `fraction` of its width (null: no countdown); moves in 1 % steps.</summary>
        public void SetTimeLeft(double? fraction)
        {
            if (_countdown == null || _cleared) return;
            var stepped = fraction.HasValue ? Math.Round(fraction.Value * 100.0) / 100.0 : (double?)null;
            if (stepped == _shownTime) return;
            _shownTime = stepped;
            TimeShown = stepped.HasValue ? (float)stepped.Value : (float?)null;
            // drawn only for the last 30 % of the time, in stop red, shrinking to nothing: a full-width line under the
            // block would read as a border
            _countdown.gameObject.SetActive(stepped.HasValue && stepped.Value > 0 && stepped.Value <= CountdownFrom);
            if (!stepped.HasValue) return;
            var w = Mathf.Max(CountdownHeight, _width * 0.3f * Mathf.Clamp01((float)stepped.Value / CountdownFrom));
            _countdown.transform.localScale = new Vector3(w, CountdownHeight, 1f);
            _countdown.transform.localPosition = new Vector3(w * 0.5f, _countdown.transform.localPosition.y, _countdown.transform.localPosition.z);
        }

        void Clear()
        {
            if (_content != null)
            {
                _content.gameObject.SetActive(false); // its buttons leave the poke registry now
                Destroy(_content.gameObject);
            }
            _content = Ui.Child(transform, "Content", Vector3.zero).transform;
            _barMesh = null;
            _hint = null;
            _countdown = null;
            Covered = false;
            _cleared = false;
            _undimmed.Clear();
            _hintBeforeCover = null;
            DenyButton = null;
            LaterButton = null;
            LightsShown = LitShown = 0;
            _shownProgress = -1f;
            _drawnProgress = 0f;
            _drawnDark = -1;
        }

        static void Place(TextMeshPro t, float left, float top) =>
            t.transform.localPosition = new Vector3(left + t.rectTransform.sizeDelta.x * 0.5f, top, -0.001f);
    }
}
