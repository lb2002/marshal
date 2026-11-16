using System;
using System.Collections.Generic;
using System.Text;
using Lotse.Core;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// An agent's latest activity (U4b's feed list; U13: the open row's log): the last lines of its feed, newest at the
    /// bottom, in an area of the given width and height under a parent transform (its origin is the list's top centre,
    /// its face looks along the parent's -z). A line is its verb in muted ("read", "wrote", "runs") in a narrow column
    /// and then its object: a tool's in the mono face on one line, what the agent says in the body face on up to two
    /// lines. Everything is muted except the newest line, which is in ink (errors in light red). It draws what
    /// FeedScroll says (laid out upward from its Bottom), so a drag still brings older lines in; there are no scroll
    /// buttons. It knows nothing of agents or panels: the owner feeds it entries (FeedLog) and the scroll. Every entry
    /// text is untrusted and plain; the verbs are ours.
    /// </summary>
    public sealed class FeedListView : MonoBehaviour
    {
        const float U = Theme.U;
        const int MaxLines = 6;
        const float Row = 20f * U;
        const float RowGap = 11f * U;
        const float LabelMin = 62f * U;
        const float LabelGap = 12f * U;

        float _width;
        float _height;
        readonly TextMeshPro[] _labels = new TextMeshPro[MaxLines];
        readonly TextMeshPro[] _texts = new TextMeshPro[MaxLines];
        TextMeshPro _measure;
        readonly Dictionary<string, float> _labelWidths = new Dictionary<string, float>();
        readonly StringBuilder _sb = new StringBuilder();
        readonly List<(FeedEntry e, string text, int lines)> _lines = new List<(FeedEntry, string, int)>();

        public float Width => _width;
        public float Height => _height;
        /// <summary>There are no scroll buttons any more (U13); a drag scrolls.</summary>
        public PokeButton OlderButton => null;
        public PokeButton NewerButton => null;
        public PokeButton NewBelowButton => null;
        /// <summary>The entries drawn now, oldest first: their texts as they came, and their ids.</summary>
        public List<string> Shown { get; } = new List<string>();
        public List<string> Ids { get; } = new List<string>();
        /// <summary>Everything drawn, one entry per line: its verb, a space, its text.</summary>
        public string TextShown { get; private set; } = "";

        /// <summary>About how tall one entry is: a drag of this much scrolls one entry.</summary>
        public static float EntryStep => Row + RowGap;

        /// <summary>How many one-line entries fit in `height`.</summary>
        public static int LinesIn(float height) => Mathf.Max(1, Mathf.FloorToInt((height + RowGap + 1e-5f) / (Row + RowGap)));

        /// <summary>The list's face in world space (a drag there scrolls it).</summary>
        public PokeRect Rect
        {
            get
            {
                var centre = transform.TransformPoint(new Vector3(0f, -_height * 0.5f, 0f));
                return new PokeRect(Ui.ToVec(centre), Ui.ToVec(transform.right), Ui.ToVec(transform.up),
                    _width * 0.5f * transform.lossyScale.x, _height * 0.5f * transform.lossyScale.y);
            }
        }

        /// <summary>A world point in the list's plane, as metres up from its bottom edge.</summary>
        public float LocalY(Vector3 world) => transform.InverseTransformPoint(world).y + _height;

        /// <param name="localTop">The list's top centre in the parent's frame.</param>
        public static FeedListView Create(Transform parent, Vector3 localTop, float width, float height, Action<int> onScroll, Action onScrollToEnd)
        {
            var v = Ui.Child(parent, "FeedList", localTop).AddComponent<FeedListView>();
            v._width = width;
            v._height = height;
            for (var i = 0; i < MaxLines; i++)
            {
                v._labels[i] = Ui.Text(v.transform, "Verb " + i, "", Vector3.zero, LabelMin, Theme.Label, Theme.Muted, TextAlignmentOptions.TopLeft);
                v._labels[i].font = Fonts.Regular;
                v._labels[i].textWrappingMode = TextWrappingModes.NoWrap;
                v._texts[i] = Ui.Text(v.transform, "Line " + i, "", Vector3.zero, width - LabelMin, Theme.Label, Theme.Muted, TextAlignmentOptions.TopLeft);
                v._labels[i].gameObject.SetActive(false);
                v._texts[i].gameObject.SetActive(false);
            }
            // never drawn: it measures labels
            v._measure = Ui.Text(v.transform, "Measure", "", Vector3.zero, width, Theme.Label, Theme.Text);
            v._measure.font = Fonts.Regular;
            v._measure.GetComponent<MeshRenderer>().enabled = false;
            return v;
        }

        /// <summary>Kept for the owner (U5a); there are no buttons to switch.</summary>
        public bool ScrollButtons { get; set; } = true;

        /// <summary>The list's height changes; the owner draws it again.</summary>
        public void SetHeight(float height) => _height = height;

        /// <summary>
        /// Draws `entries` as `scroll` says (laid out upward from its Bottom until the area is full). Without entries:
        /// "Loading history …", "No history available" or "Nothing has happened yet", by `state`.
        /// </summary>
        public void Show(IReadOnlyList<FeedEntry> entries, FeedState state, FeedScroll scroll)
        {
            Shown.Clear();
            Ids.Clear();
            _lines.Clear();
            for (var i = 0; i < MaxLines; i++)
            {
                if (_labels[i].gameObject.activeSelf) _labels[i].gameObject.SetActive(false);
                if (_texts[i].gameObject.activeSelf) _texts[i].gameObject.SetActive(false);
            }
            if (entries.Count == 0)
            {
                var word = state == FeedState.Loading ? Texts.FeedLoading : state == FeedState.Unavailable ? Texts.FeedUnavailable : Texts.FeedEmpty;
                var t = _texts[0];
                t.gameObject.SetActive(true);
                Style(t, false, Theme.Muted);
                t.rectTransform.sizeDelta = new Vector2(_width, 0.01f);
                t.text = word;
                var lines = Ui.Rows(t, Row, out var shift);
                t.transform.localPosition = new Vector3(0f, shift, -0.001f);
                Shown.Add(word);
                TextShown = word;
                return;
            }

            // the verb column: as wide as the widest verb in the feed, so every object starts at the same x
            var column = LabelMin;
            for (var i = 0; i < entries.Count; i++) column = Mathf.Max(column, LabelWidth(Texts.FeedLabel(entries[i].Kind, entries[i].Label)) + LabelGap);
            column = Mathf.Min(column, _width * 0.3f);
            var textW = _width - column;

            // newest first, upward, while they fit
            var used = 0f;
            var probe = _texts[0];
            probe.gameObject.SetActive(true);
            for (var i = scroll.Bottom - 1; i >= 0 && _lines.Count < MaxLines; i--)
            {
                var e = entries[i];
                var label = Texts.FeedLabel(e.Kind, e.Label);
                var mono = e.Kind == "tool";
                Style(probe, mono, Theme.Muted);
                probe.rectTransform.sizeDelta = new Vector2(textW, 0.01f);
                var body = Texts.Plain(Texts.WithoutLabel(e.Text, label)).Replace('\n', ' ');
                string shown;
                int n;
                if (mono)
                {
                    shown = Ui.OneLine(probe, body, textW);
                    n = 1;
                }
                else
                {
                    shown = Ui.TwoLines(probe, body);
                    probe.text = shown;
                    probe.ForceMeshUpdate();
                    n = Mathf.Clamp(probe.textInfo.lineCount, 1, 2);
                }
                var h = n * Row + (_lines.Count > 0 ? RowGap : 0f);
                if (used + h > _height + 1e-5f && _lines.Count > 0) break;
                used += h;
                _lines.Insert(0, (e, shown, n));
            }

            // from the top when everything fits (calmer than a gap above a short log); else the newest sits at the bottom
            var all = _lines.Count == scroll.Bottom && scroll.Bottom == entries.Count;
            // always from the top: the log's first line stands level with the plan's first step beside it
            var y = 0f;
            var newest = entries[entries.Count - 1];
            _sb.Clear();
            for (var i = 0; i < _lines.Count; i++)
            {
                var (e, shown, n) = _lines[i];
                var label = Texts.Plain(Texts.FeedLabel(e.Kind, e.Label));
                var error = e.Kind == "error";
                var color = error ? Theme.ErrorText : ReferenceEquals(e, newest) ? Theme.Text : Theme.Muted;
                var lt = _labels[i];
                var tt = _texts[i];
                lt.gameObject.SetActive(true);
                tt.gameObject.SetActive(true);
                lt.color = color;
                lt.rectTransform.sizeDelta = new Vector2(column, 0.01f);
                lt.text = Ui.OneLine(lt, label, column - LabelGap * 0.5f);
                Ui.Rows(lt, Row, out var ls);
                lt.transform.localPosition = new Vector3(-_width * 0.5f + column * 0.5f, y + ls, -0.001f);
                Style(tt, e.Kind == "tool", color);
                tt.rectTransform.sizeDelta = new Vector2(textW, 0.01f);
                tt.text = shown;
                Ui.Rows(tt, Row, out var ts);
                tt.transform.localPosition = new Vector3(-_width * 0.5f + column + textW * 0.5f, y + ts, -0.001f);
                y -= n * Row + RowGap;
                Ids.Add(e.Id);
                Shown.Add(e.Text);
                if (i > 0) _sb.Append('\n');
                _sb.Append(label).Append(' ').Append(shown);
            }
            TextShown = _sb.ToString();
        }

        /// <summary>A tool's object in the mono face (16 units), anything else in the body face (17 units).</summary>
        static void Style(TextMeshPro t, bool mono, Color color)
        {
            t.font = mono ? Fonts.Mono : Fonts.Regular;
            t.fontSize = mono ? Theme.Small : Theme.Label;
            t.color = color;
            t.lineSpacing = 0f;
            t.textWrappingMode = TextWrappingModes.Normal;
        }

        float LabelWidth(string label)
        {
            if (_labelWidths.TryGetValue(label, out var w)) return w;
            w = _measure.GetPreferredValues(Texts.Plain(label), Mathf.Infinity, Mathf.Infinity).x;
            if (_labelWidths.Count < 64) _labelWidths[label] = w;
            return w;
        }
    }
}
