using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The window's header (U13): small "Marshal" in muted, and under it one large sentence that says the single thing
    /// that matters now (Headline: "Codex is holding for you.", the open request's sentence, "Everything is stopped.").
    /// 44 units SemiBold, at most two lines, balanced. At the top right the tools, text only and muted: the pager
    /// ("‹ 1/2 ›", with more than five agents), "All agents" (while a row is open: it closes it), "Menu" and
    /// "Fold away". A tool's hit area is the one control height; under a pointer it gets a faint fill (drawn here, in
    /// one mesh). The header is as high as its sentence needs: Extent. Our own words only, except the request's
    /// sentence, which is plain text. Origin: the window's top left corner; the face looks along the parent's -z.
    /// </summary>
    public sealed class WorkspaceHeader : MonoBehaviour
    {
        const float U = Theme.U;
        /// <summary>The window's padding: top, sides, bottom.</summary>
        public const float TopPad = 34f * U;
        public const float SidePad = 40f * U;
        public const float BottomPad = 26f * U;
        const float MarkRow = 19f * U;
        const float MarkGap = 8f * U;
        const float LinePitch = 44f * 1.04f * U;
        const float MinBlock = 92f * U;
        const float BodyGap = 18f * U;
        const float SubRow = 25f * U;
        /// <summary>The header with a one-line sentence, down to the body's top edge; and with two lines (the menu's sheet starts under that).</summary>
        public const float Height = TopPad + MinBlock + BodyGap;
        public const float MaxHeight = TopPad + MarkRow + MarkGap + 2f * LinePitch + BodyGap;
        public const float RowHeight = Theme.H;
        /// <summary>The sentence is never wider than this (about 20 characters a line).</summary>
        const float SayWidth = 560f * U;
        /// <summary>The tools' line: its middle below the window's top edge.</summary>
        public const float ToolsY = 44f * U;
        const float ToolPad = 12f * U;
        const float ToolGap = 4f * U;
        const float WordGap = 40f * U;
        /// <summary>A long sentence (a request's) may use this much before it is cut.</summary>
        const float SayWide = 760f * U;
        /// <summary>A sentence that still needs a third line is set at this share of the size (36 of 44 units).</summary>
        const float SmallSay = 36f / 44f;
        /// <summary>A tool is at least this wide: with the ray's padding its target is 3 degrees at the default distance.</summary>
        const float MinTool = 78f * U;

        float _width;
        TextMeshPro _mark;
        TextMeshPro _say;
        TextMeshPro _tools;
        FlatMesh _hover;
        string _key;
        int _hoverKey = -1;
        readonly Rect[] _rects = new Rect[5];
        PokeButton[] _areas;

        /// <summary>"All agents": closes the open row. Shown only while a row is open.</summary>
        public PokeButton AllButton { get; private set; }
        public PokeButton MenuButton { get; private set; }
        /// <summary>"Fold away": the rail.</summary>
        public PokeButton CollapseButton { get; private set; }
        public PokeButton PreviousButton { get; private set; }
        public PokeButton NextButton { get; private set; }
        /// <summary>The sentence as shown.</summary>
        public string Said => _say.text;
        /// <summary>"1/2", or null with one page.</summary>
        public string PageShown { get; private set; }
        /// <summary>The header's height now, from the window's top edge to the body's top edge.</summary>
        public float Extent { get; private set; } = Height;
        /// <summary>The sentence's bottom edge below the window's top edge (the halted line stands under it).</summary>
        public float SayBottom { get; private set; }
        public int Version { get; private set; }
        public Action OnMenu { get; set; }

        public static WorkspaceHeader Create(Transform parent, Vector3 localTopLeft, float width, Action onAll, Action onCollapse,
            Action onPrevious, Action onNext, Action onMenu = null)
        {
            var h = Ui.Child(parent, "Header", localTopLeft).AddComponent<WorkspaceHeader>();
            h._width = width;
            h._mark = Ui.Text(h.transform, "Mark", Texts.WorkspaceTitle, Vector3.zero, SayWidth, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            h._mark.font = Fonts.Medium;
            Ui.Middle(h._mark, SayWidth, MarkRow, TextAlignmentOptions.Left);
            h._mark.transform.localPosition = new Vector3(SidePad + SayWidth * 0.5f, -TopPad - MarkRow * 0.5f, -0.001f);
            h._say = Ui.Text(h.transform, "Say", "", Vector3.zero, SayWidth, Theme.Headline, Theme.Text);
            h._say.font = Fonts.SemiBold;
            h._say.characterSpacing = 100f * Taxi.HeadlineTracking;
            var toolsW = width - 2f * SidePad;
            h._tools = Ui.Text(h.transform, "Tools", "", Vector3.zero, toolsW, Theme.Small, Theme.Muted, TextAlignmentOptions.Left, richText: true);
            Ui.Middle(h._tools, toolsW, RowHeight, TextAlignmentOptions.Left);
            h._tools.transform.localPosition = new Vector3(width * 0.5f, -ToolsY, -0.001f);
            h._hover = new FlatMesh(h.transform, "Hover", 0.0005f);
            // its origin on the tools' line: an empty mesh's bounds lie inside the panel
            h._hover.Renderer.transform.localPosition = new Vector3(width * 0.5f, -ToolsY, 0.0005f);
            var size = new Vector2(UiMetrics.HeaderButtonWidth, RowHeight);
            h.PreviousButton = PokeButton.CreateArea(h.transform, "Previous page", Vector3.zero, size, onPrevious);
            h.NextButton = PokeButton.CreateArea(h.transform, "Next page", Vector3.zero, size, onNext);
            h.AllButton = PokeButton.CreateArea(h.transform, Texts.AllAgents, Vector3.zero, size, onAll);
            h.MenuButton = PokeButton.CreateArea(h.transform, Texts.Menu, Vector3.zero, size, () => h.OnMenu?.Invoke());
            h.CollapseButton = PokeButton.CreateArea(h.transform, OverviewText.Collapse, Vector3.zero, size, onCollapse);
            h.OnMenu = onMenu;
            h._areas = new[] { h.PreviousButton, h.NextButton, h.AllButton, h.MenuButton, h.CollapseButton };
            // drawn here (the faint fill under a pointer): no renderer of their own
            foreach (var b in h._areas) b.OwnerDrawsHover = true;
            return h;
        }

        float Wide(string s) => _tools.GetPreferredValues(s, Mathf.Infinity, Mathf.Infinity).x;

        /// <summary>
        /// The header for what is on screen; laid out only when something changes.
        /// </summary>
        /// <param name="sentence">The one sentence (plain; the request's sentence is untrusted).</param>
        /// <param name="open">A row is open: "All agents" shows.</param>
        /// <param name="page">The page (0-based) of `pages`; one page: no pager.</param>
        /// <param name="reserve">Room kept free at the right end of the tools (halted: "Continue").</param>
        /// <param name="sub">A line stands under the sentence (halted): the header is at least that high.</param>
        public void Show(string sentence, bool open, int page, int pages, float reserve = 0f, bool sub = false)
        {
            var key = sentence + "|" + open + "|" + page + "|" + pages + "|" + reserve.ToString("0.0000", CultureInfo.InvariantCulture) + "|" + sub;
            if (key == _key) return;
            _key = key;
            Version++;

            // the sentence: at most two lines, balanced (the narrowest width that still gives that many lines)
            _say.rectTransform.sizeDelta = new Vector2(SayWidth, 0.01f);
            _say.lineSpacing = 0f;
            _say.fontSize = Theme.Headline;
            var pitch = LinePitch;
            Ui.SetPlain(_say, sentence ?? "");
            Ui.FitHeight(_say);
            _say.ForceMeshUpdate();
            if (_say.textInfo.lineCount > 2)
            {
                // a longer sentence (a request's) gets a wider measure, then a smaller size (36 units), before anything is cut
                _say.rectTransform.sizeDelta = new Vector2(SayWide, 0.01f);
                Ui.FitHeight(_say);
                _say.ForceMeshUpdate();
                if (_say.textInfo.lineCount > 2)
                {
                    _say.fontSize = Theme.Headline * SmallSay;
                    pitch = LinePitch * SmallSay;
                    Ui.FitHeight(_say);
                }
            }
            var measure = _say.rectTransform.sizeDelta.x;
            Ui.CapLines(_say, 2);
            var text = _say.text;
            var lines = Mathf.Max(1, _say.textInfo.lineCount);
            if (lines == 2)
            {
                float lo = measure * 0.4f, hi = measure;
                for (var i = 0; i < 8; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    _say.rectTransform.sizeDelta = new Vector2(mid, 0.01f);
                    _say.ForceMeshUpdate();
                    if (_say.textInfo.lineCount <= 2) hi = mid;
                    else lo = mid;
                }
                _say.rectTransform.sizeDelta = new Vector2(hi, 0.01f);
                _say.text = text;
            }
            lines = Ui.Rows(_say, pitch, out var shift);
            var top = -(TopPad + MarkRow + MarkGap);
            _say.transform.localPosition = new Vector3(SidePad + _say.rectTransform.sizeDelta.x * 0.5f, top + shift, -0.001f);
            SayBottom = TopPad + MarkRow + MarkGap + lines * pitch;
            var block = MarkRow + MarkGap + lines * pitch + (sub ? SubRow : 0f);
            Extent = TopPad + Mathf.Max(MinBlock, block) + BodyGap;

            // the tools, from the right: "Fold away", "Menu", "All agents", the pager; x in the text's own frame
            var w = _tools.rectTransform.sizeDelta.x;
            string Pos(float x) => "<pos=" + (100f * x / w).ToString("0.00", CultureInfo.InvariantCulture) + "%>";
            var paged = pages > 1;
            PageShown = paged ? (page + 1) + "/" + pages : null;
            var labels = new[] { "‹", "›", Texts.AllAgents, Texts.Menu, OverviewText.Collapse };
            var on = new[] { paged, paged, open, true, true };
            var sb = new System.Text.StringBuilder();
            // the last tool's text ends where the content ends; its fill hangs into the padding, as in the design
            var x = w - reserve;
            for (var i = labels.Length - 1; i >= 0; i--)
            {
                var b = _areas[i];
                if (b.gameObject.activeSelf != on[i]) b.gameObject.SetActive(on[i]);
                if (!on[i])
                {
                    _rects[i] = default;
                    continue;
                }
                var tw = Wide(labels[i]);
                var bw = Mathf.Max(tw + 2f * ToolPad, MinTool);
                float left;
                if (i >= 2)
                {
                    // the words stand evenly apart (40 units between them); each one's area is centred on its word
                    x -= tw;
                    left = x - (bw - tw) * 0.5f;
                    sb.Insert(0, Pos(x) + labels[i]);
                    x -= WordGap - ToolGap;
                }
                else
                {
                    x -= bw;
                    left = x;
                    sb.Insert(0, Pos(x + (bw - tw) * 0.5f) + labels[i]);
                }
                // in the header's frame: the text's frame starts at the side padding
                var rect = new Rect(SidePad + left, -ToolsY - RowHeight * 0.5f, bw, RowHeight);
                _rects[i] = rect;
                b.transform.localPosition = new Vector3(rect.center.x, rect.center.y, -0.003f);
                b.Resize(rect.size);
                // neighbours stand 4 units apart: the ray's padding reaches up and down only
                b.HitPad = new Vector2(ToolGap * 0.5f, Theme.HitPad + 0.001f); // the tools stand far off the seat's axis: 3 degrees there too
                x -= ToolGap;
                if (i == 1 && paged)
                {
                    // "1/2" between the arrows
                    var pw = Wide(PageShown) + 2f * ToolGap;
                    x -= pw;
                    sb.Insert(0, Pos(x + ToolGap) + PageShown);
                    x -= ToolGap;
                }
            }
            if (paged)
            {
                PreviousButton.SetInteractable(page > 0);
                NextButton.SetInteractable(page < pages - 1);
            }
            _tools.text = sb.ToString();
            _hoverKey = -1;
        }

        /// <summary>The tools' hover and pressed states: a faint fill behind the text (drawn again only when one changes).</summary>
        void LateUpdate()
        {
            var key = 0;
            for (var i = 0; i < _areas.Length; i++) key |= _areas[i].AreaState << (2 * i);
            if (key == _hoverKey) return;
            _hoverKey = key;
            _hover.Clear();
            for (var i = 0; i < _areas.Length; i++)
            {
                var state = (key >> (2 * i)) & 3;
                var r = _rects[i];
                if (r.width <= 0f) continue;
                var fill = new Rect(r.x - _width * 0.5f, r.center.y + ToolsY - 18.5f * U, r.width, 37f * U);
                // at rest the fill is the asphalt itself (invisible): the mesh is always there, so a hover adds no draw call
                _hover.RoundedRect(fill, 8f * U, FlatMesh.Over(Look.Hex(0xFFFFFF), state == 2 ? 0.12f : state == 1 ? 0.05f : 0f, Look.Asphalt));
            }
            _hover.Apply();
        }

        /// <summary>Every text of the header as shown, without its tags (for tests).</summary>
        public string Plain => _mark.text + " " + _say.text + " " + System.Text.RegularExpressions.Regex.Replace(_tools.text, "<[^>]+>", " ");
    }
}
