using System;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// Halt-all in the window (U7; in U13's look), all of it part of the window, so it moves, scales and docks with it:
    /// - the stop bar: while the stop gesture counts, a red bar fills along the window's top edge with "Halt all
    ///   agents"; with the window folded into the rail, a thin bar runs down along the rail instead;
    /// - halted: the header says "Everything is stopped." (the window's sentence); here, at the header's right end,
    ///   "Continue" as the primary button; it asks "Really continue?" with "Yes" and "Cancel", and only "Yes" calls
    ///   onRelease (release has no gesture);
    /// - one muted line under the sentence: "Halted for 12 s", then the bridge's enforcement ("Claude Code and Codex
    ///   stop before their next step. …").
    /// There is no band and no amber any more: halted uses the stop red of the lines' stop bars. Our own words only.
    /// </summary>
    public sealed class HaltView : MonoBehaviour
    {
        const float U = Theme.U;
        /// <summary>The window does not grow while halted (U13: "Continue" is in the header).</summary>
        public const float Extra = 0f;
        public const float BarHeight = Theme.Row;
        public const float RailBarWidth = 0.006f;
        const float BarGap = 0.003f;
        const float LineRow = 19f * U;
        static readonly Vector2 ButtonSize = new Vector2(UiMetrics.ButtonWidth, Theme.H);

        Transform _band;
        TextMeshPro _line;
        Transform _bar;
        MeshRenderer _track;
        MeshRenderer _fill;
        TextMeshPro _barLabel;
        /// <summary>"Yes" after "Continue": the release (set by the app).</summary>
        public Action OnRelease { get; set; }
        float _width;
        string _since = "";
        string _enforcement = "";
        string _shownLine;

        public PokeButton ContinueButton { get; private set; }
        public PokeButton YesButton { get; private set; }
        public PokeButton CancelButton { get; private set; }
        /// <summary>"Really continue?" is asked.</summary>
        public bool Asking { get; private set; }
        /// <summary>Halted is shown: "Continue" and the line under the sentence.</summary>
        public bool BandShown => _band.gameObject.activeSelf;
        /// <summary>"Halted for 12 s", or the question while it is asked.</summary>
        public string BandText => Asking ? Texts.ContinueAsk : _since;
        public string EnforcementText => _enforcement;
        /// <summary>The line under the sentence as drawn.</summary>
        public string LineShown => _line.text;
        public bool BarShown => _bar.gameObject.activeSelf;
        public float BarProgress { get; private set; }
        public string BarLabel => _barLabel.text;

        /// <summary>The room the header keeps free at its right end for the buttons shown now.</summary>
        public float Reserve => !BandShown ? 0f : (Asking ? 2f * ButtonSize.x + Theme.ControlGap : ButtonSize.x) + Theme.HitPad + 18f * U;

        static Color Dark => Ui.ToColor(Look.OnPrimary);

        /// <param name="content">The window's content (hidden while folding): the buttons and the line go there.</param>
        /// <param name="root">The window's root: the stop bar goes there (it also shows along the rail).</param>
        /// <param name="width">The window's width at its base size.</param>
        public static HaltView Create(Transform content, Transform root, float width, Action onRelease)
        {
            var v = Ui.Child(content, "Halt", Vector3.zero).AddComponent<HaltView>();
            v.OnRelease = onRelease;
            v._width = width;
            v._band = Ui.Child(v.transform, "Band", Vector3.zero).transform;
            // the buttons: at the header's right end, on the tools' line (the origin is the window's top centre)
            var right = width * 0.5f - WorkspaceHeader.SidePad - ButtonSize.x * 0.5f;
            var y = -WorkspaceHeader.ToolsY;
            v.ContinueButton = PokeButton.Create(v._band, Texts.Continue, new Vector3(right, y, -0.003f), ButtonSize, v.Ask, primary: true);
            v.CancelButton = PokeButton.Create(v._band, Texts.Cancel, new Vector3(right, y, -0.003f), ButtonSize, v.Cancel);
            v.YesButton = PokeButton.Create(v._band, Texts.Yes, new Vector3(right - ButtonSize.x - Theme.ControlGap, y, -0.003f), ButtonSize, v.Yes,
                primary: true);
            var lineW = width - 2f * WorkspaceHeader.SidePad;
            v._line = Ui.Text(v._band, "Line", "", Vector3.zero, lineW, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            Ui.Middle(v._line, lineW, LineRow, TextAlignmentOptions.Left);

            v._bar = Ui.Child(root, "StopBar", new Vector3(0f, 0f, -0.002f)).transform;
            v._track = Ui.Panel(v._bar, "Track", Vector3.zero, Vector2.one, Ui.ToColor(Look.Stop, 0.45f), Ui.QueueOnPanel);
            v._fill = Ui.Panel(v._bar, "Fill", new Vector3(0f, 0f, -0.0005f), Vector2.one, Theme.Stop, Ui.QueueRing);
            v._barLabel = Ui.Text(v._bar, "Label", Texts.HaltAll, new Vector3(0f, 0f, -0.001f), width, Theme.Small, Dark, TextAlignmentOptions.Center);
            v._barLabel.font = Fonts.Medium;
            Ui.Middle(v._barLabel, width, BarHeight, TextAlignmentOptions.Center);
            v._band.gameObject.SetActive(false);
            v._bar.gameObject.SetActive(false);
            v.SetAsking(false);
            return v;
        }

        /// <summary>Where the window's top edge is (window local) and how far below it the header's sentence ends.</summary>
        public void PlaceBand(float top, float sayBottom = 0f)
        {
            transform.localPosition = new Vector3(0f, top, 0f);
            _line.transform.localPosition = new Vector3(0f, -sayBottom - 7f * U - LineRow * 0.5f, -0.001f);
        }

        /// <summary>"Continue" and the line while halted; gone otherwise (and the question with it).</summary>
        public void ShowHalted(bool on, string since, string enforcement)
        {
            if (_band.gameObject.activeSelf != on) _band.gameObject.SetActive(on);
            if (!on)
            {
                if (Asking) SetAsking(false);
                return;
            }
            _since = since ?? "";
            _enforcement = enforcement ?? "";
            Draw();
        }

        void Draw()
        {
            var text = Asking ? Texts.ContinueAsk : _enforcement.Length > 0 ? _since + ". " + _enforcement : _since;
            if (text == _shownLine) return;
            _shownLine = text;
            _line.color = Asking ? Theme.Text : Theme.Muted;
            Ui.SetPlain(_line, Ui.OneLine(_line, text, _width - 2f * WorkspaceHeader.SidePad));
        }

        void Ask() => SetAsking(true);

        void Cancel() => SetAsking(false);

        void Yes()
        {
            if (!Asking) return; // only right after "Continue"
            SetAsking(false);
            OnRelease?.Invoke();
        }

        void SetAsking(bool on)
        {
            Asking = on;
            ContinueButton.gameObject.SetActive(!on);
            YesButton.gameObject.SetActive(on);
            CancelButton.gameObject.SetActive(on);
            if (_line != null && BandShown) Draw();
        }

        /// <summary>
        /// The stop bar at `progress` (0 hides it), along the top edge of the panel's current rectangle `panel` (window
        /// local), or, `folded`, down along the rail on its side away from the display (`railRight`: the rail is at the right).
        /// </summary>
        public void SetBar(float progress, Rect panel, bool folded, bool railRight)
        {
            BarProgress = progress;
            var on = progress > 0f;
            if (_bar.gameObject.activeSelf != on) _bar.gameObject.SetActive(on);
            if (!on) return;
            var p = Mathf.Clamp01(progress);
            if (!folded)
            {
                var y = panel.yMax + BarGap + BarHeight * 0.5f;
                _track.transform.localPosition = new Vector3(panel.center.x, y, 0f);
                _track.transform.localScale = new Vector3(panel.width, BarHeight, 1f);
                _fill.transform.localPosition = new Vector3(panel.xMin + panel.width * p * 0.5f, y, -0.0005f);
                _fill.transform.localScale = new Vector3(Mathf.Max(0.0005f, panel.width * p), BarHeight, 1f);
                _barLabel.gameObject.SetActive(true);
                _barLabel.transform.localPosition = new Vector3(panel.center.x, y, -0.001f);
                return;
            }
            var x = railRight ? panel.xMin - BarGap - RailBarWidth * 0.5f : panel.xMax + BarGap + RailBarWidth * 0.5f;
            _track.transform.localPosition = new Vector3(x, panel.center.y, 0f);
            _track.transform.localScale = new Vector3(RailBarWidth, panel.height, 1f);
            _fill.transform.localPosition = new Vector3(x, panel.yMax - panel.height * p * 0.5f, -0.0005f);
            _fill.transform.localScale = new Vector3(RailBarWidth, Mathf.Max(0.0005f, panel.height * p), 1f);
            _barLabel.gameObject.SetActive(false);
        }
    }
}
