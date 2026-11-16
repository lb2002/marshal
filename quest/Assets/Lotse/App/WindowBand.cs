using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// A band inside the window (U9), full width: one sentence at the left (with an optional small caption above it) and
    /// its buttons at the right, each Theme.H high. Two uses:
    /// - a question the app asks ("Put Marshal next to a display?", "Halt all agents?"), directly under the header. While
    ///   it shows nothing can be approved: the app treats it like the open menu (AppRoot);
    /// - the guided test's band along the bottom edge, which is not a question: the app behaves normally while it shows.
    /// The window makes room for it (WorkspaceView), so it never covers the body. Our own words only. Every button is a
    /// PokeButton: poked or ray-clicked under the click-versus-hold rule; none of them approves or denies a request.
    /// Origin: its top centre; the face looks along the parent's -z.
    /// </summary>
    public sealed class WindowBand : MonoBehaviour
    {
        /// <summary>A question band: H with 2 x S above and below.</summary>
        public const float QuestionHeight = Theme.Band;
        /// <summary>A band with a caption line above its sentence.</summary>
        public const float CaptionedHeight = Theme.Band + Theme.Row;
        /// <summary>The band's distance from the panel's left and right edges.</summary>
        public const float Inset = 2f * Theme.S;

        float _width;
        float _height;
        float _buttonWidth;
        Transform _content;
        MeshRenderer _background;
        readonly List<PokeButton> _buttons = new List<PokeButton>();

        public bool IsShown => gameObject.activeSelf;
        /// <summary>What it is about ("offer", "halt", "test"): the owner's own tag.</summary>
        public string Tag { get; private set; }
        public string TextShown { get; private set; } = "";
        public string CaptionShown { get; private set; } = "";
        /// <summary>The small line under the sentence, when the band has one.</summary>
        public string NoteShown { get; private set; } = "";
        public IReadOnlyList<PokeButton> Buttons => _buttons;
        public float Height => _height;
        /// <summary>Higher than a question band: room for a caption over the sentence, or for a sentence of two lines.</summary>
        bool Tall => _height > QuestionHeight + 1e-5f;
        /// <summary>Counts what it showed (a new question, a new step).</summary>
        public int Version { get; private set; }

        /// <param name="width">The window's width at its base size.</param>
        /// <param name="height">QuestionHeight or CaptionedHeight.</param>
        /// <param name="buttonWidth">The one width of its buttons.</param>
        /// <summary>The first button stands at the right end (the others before it): "Cancel", then "Pair".</summary>
        public bool FirstAtRight { get; set; }
        /// <summary>The one width of the buttons of the next Show.</summary>
        public float ButtonWidth { get => _buttonWidth; set => _buttonWidth = value; }
        /// <summary>
        /// The tour's look (U14): no fill of its own (the band is the window's asphalt), and its buttons are text links
        /// except a primary first one.
        /// </summary>
        public bool Plain
        {
            get => _plain;
            set
            {
                _plain = value;
                if (_background != null) _background.enabled = !value;
            }
        }
        bool _plain;

        /// <param name="fill">The card's colour when it stands free (outside the window): asphalt; in the window the raised fill.</param>
        public static WindowBand Create(Transform parent, string name, float width, float height, float buttonWidth, Color? fill = null)
        {
            var b = Ui.Child(parent, name, Vector3.zero).AddComponent<WindowBand>();
            b._width = width;
            b._height = height;
            b._buttonWidth = buttonWidth;
            b._background = Theme.Shape(b.transform, "Background", new Vector3(0f, -height * 0.5f, 0f),
                new Vector2(width - 2f * Inset, height - 2f * Theme.S), fill ?? Theme.Card, Theme.CardMaterial);
            if (fill.HasValue) b._background.gameObject.AddComponent<RayBlocker>(); // free-standing: it stops the ray like a panel
            b.gameObject.SetActive(false);
            return b;
        }

        /// <summary>
        /// Shows `text` (and `caption` above it, when the band has the room) with `buttons` at the right, in the order
        /// given; the first is drawn as the primary one when `primaryFirst`.
        /// </summary>
        public void Show(string tag, string caption, string text, bool primaryFirst, params (string label, Action onPress)[] buttons)
            => Show(tag, caption, text, null, primaryFirst, buttons);

        /// <summary>The same with `note`: one more line in the Small style under the sentence (a captioned band, made 3 x S higher).</summary>
        public void Show(string tag, string caption, string text, string note, bool primaryFirst, params (string label, Action onPress)[] buttons)
        {
            Clear();
            Tag = tag;
            Version++;
            gameObject.SetActive(true);
            _content = Ui.Child(transform, "Content", Vector3.zero).transform;
            var left = -_width * 0.5f + Inset + 2f * Theme.S;
            var right = _width * 0.5f - Inset - 2f * Theme.S;
            var cy = -_height * 0.5f;
            var x = right;
            for (var k = buttons.Length - 1; k >= 0; k--)
            {
                // placed from the right: the last button first, or the first one when it belongs at the right end
                var i = FirstAtRight ? buttons.Length - 1 - k : k;
                var press = buttons[i].onPress;
                var b = PokeButton.Create(_content, buttons[i].label, new Vector3(x - _buttonWidth * 0.5f, cy, -0.002f),
                    new Vector2(_buttonWidth, Theme.H), press, primary: primaryFirst && i == 0,
                    kind: _plain && !(primaryFirst && i == 0) ? ButtonKind.Text : ButtonKind.Quiet);
                if (FirstAtRight) _buttons.Add(b);
                else _buttons.Insert(0, b);
                x -= _buttonWidth + Theme.ControlGap;
            }
            var textW = x + Theme.ControlGap - 4f * Theme.S - left;
            var captioned = !string.IsNullOrEmpty(caption) && Tall;
            CaptionShown = captioned ? caption : "";
            var noted = captioned && !string.IsNullOrEmpty(note);
            NoteShown = noted ? note : "";
            var t = Ui.Text(_content, "Text", "", Vector3.zero, textW, Theme.Body, Theme.Text);
            if (captioned)
            {
                // a band with a caption has the room for a sentence of two lines
                t.text = text;
                Ui.FitHeight(t);
                Ui.CapLines(t, 2);
            }
            else if (Tall)
            {
                // a high band without a caption (the tour's end, U14b): the sentence has the room for two lines
                t.text = text;
                Ui.FitHeight(t);
                Ui.CapLines(t, 2);
            }
            else
            {
                t.textWrappingMode = TextWrappingModes.NoWrap;
                t.text = Ui.OneLine(t, text, textW);
            }
            TextShown = t.text;
            if (captioned)
            {
                var c = Ui.Text(_content, "Caption", caption, Vector3.zero, textW, Theme.Small, Theme.Muted);
                c.textWrappingMode = TextWrappingModes.NoWrap;
                // the caption's row over the sentence's row, the pair centred in the band
                Ui.Middle(c, textW, 3f * Theme.S, TextAlignmentOptions.Left);
                var rows = Ui.Rows(t, Theme.Row, out var shift);
                var top = cy + (3f * Theme.S + rows * Theme.Row + (noted ? 3f * Theme.S : 0f)) * 0.5f;
                c.transform.localPosition = new Vector3(left + textW * 0.5f, top - 1.5f * Theme.S, -0.001f);
                t.transform.localPosition = new Vector3(left + textW * 0.5f, top - 3f * Theme.S + shift, -0.001f);
                if (noted)
                {
                    var n = Ui.Text(_content, "Note", note, Vector3.zero, textW, Theme.Small, Theme.Muted);
                    n.textWrappingMode = TextWrappingModes.NoWrap;
                    Ui.Middle(n, textW, 3f * Theme.S, TextAlignmentOptions.Left);
                    n.transform.localPosition = new Vector3(left + textW * 0.5f, top - 3f * Theme.S - rows * Theme.Row - 1.5f * Theme.S, -0.001f);
                }
            }
            else if (Tall)
            {
                // its rows centred in the band
                var rows = Ui.Rows(t, Theme.Row, out var shift);
                t.transform.localPosition = new Vector3(left + textW * 0.5f, cy + rows * Theme.Row * 0.5f + shift, -0.001f);
            }
            else
            {
                Ui.Middle(t, textW, Theme.H, TextAlignmentOptions.Left);
                t.transform.localPosition = new Vector3(left + textW * 0.5f, cy, -0.001f);
            }
        }

        public void Hide()
        {
            if (!IsShown) return;
            Clear();
            Tag = null;
            TextShown = "";
            CaptionShown = "";
            NoteShown = "";
            gameObject.SetActive(false);
        }

        void Clear()
        {
            _buttons.Clear();
            if (_content == null) return;
            _content.gameObject.SetActive(false); // its buttons leave the poke registry now
            Destroy(_content.gameObject);
            _content = null;
        }
    }
}
