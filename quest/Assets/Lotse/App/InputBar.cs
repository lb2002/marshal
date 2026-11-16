using System;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// A reusable input bar (U4b; one row of equal controls since U9) of the given width under a parent transform (its
    /// origin is its top centre, its face looks along the parent's -z): the draft in a text field (plain, the latest part
    /// when long, a placeholder when empty; a tap on it is "Edit"), "Keyboard" and "Send", all Theme.H high on one line
    /// with equal gaps, and under it one helper line in the Small style. It only draws and reports presses: the
    /// ReplyController decides (Send only when allowed, never on its own). The draft is the user's own text, shown like
    /// untrusted text.
    /// </summary>
    public sealed class InputBar : MonoBehaviour
    {
        const float RowHeight = Theme.H;
        const float StatusHeight = 27f * Theme.U;
        const float Gap = Theme.ControlGap;
        const float TextPad = 2f * Theme.S;
        const int DraftTail = 160;
        static readonly Vector2 ButtonSize = new Vector2(UiMetrics.ButtonWidth, Theme.H);

        /// <summary>Its height, from its top edge.</summary>
        public const float Height = RowHeight + StatusHeight;

        TextMeshPro _draft;
        TextMeshPro _status;
        string _placeholder = Texts.ReplyPlaceholder;
        string _draftText = "";
        float _width;
        bool _only;

        /// <summary>The agent cannot take replies: the field and the buttons are gone, one muted line says why in their place.</summary>
        public bool ExplanationOnly => _only;

        public PokeButton SendButton { get; private set; }
        /// <summary>"Edit": the text field itself (a tap opens the system keyboard with the draft).</summary>
        public PokeButton EditButton => TextButton;
        public PokeButton KeyboardButton { get; private set; }
        /// <summary>The text field itself: a tap opens the system keyboard with the draft.</summary>
        public PokeButton TextButton { get; private set; }
        public string TextShown => _draft.text;
        public string StatusShown => _status.text;

        /// <param name="localTop">The bar's top centre in the parent's frame.</param>
        public static InputBar Create(Transform parent, Vector3 localTop, float width, Action onSend, Action onEdit, Action onKeyboard)
        {
            var b = Ui.Child(parent, "InputBar", localTop).AddComponent<InputBar>();
            b._width = width;
            var c = b.transform;
            var left = -width * 0.5f;
            var right = width * 0.5f;
            var rowY = -RowHeight * 0.5f;
            var sendX = right - ButtonSize.x * 0.5f;
            var keysX = sendX - ButtonSize.x - Gap;
            var fieldW = keysX - ButtonSize.x * 0.5f - Gap - left;
            b.TextButton = PokeButton.Create(c, Texts.ReplyEdit, new Vector3(left + fieldW * 0.5f, rowY, -0.0015f), new Vector2(fieldW, RowHeight),
                onEdit, showLabel: false, cornerRadius: Theme.ButtonRadius, background: Theme.Well);
            b._draft = Ui.Text(c, "Draft", "", Vector3.zero, fieldW - 2f * TextPad, Theme.Label, Theme.Text, TextAlignmentOptions.Left);
            Ui.Middle(b._draft, fieldW - 2f * TextPad, RowHeight, TextAlignmentOptions.Left);
            b._draft.overflowMode = TextOverflowModes.Ellipsis;
            b._draft.transform.localPosition = new Vector3(left + fieldW * 0.5f, rowY, -0.003f);
            b.KeyboardButton = PokeButton.Create(c, Texts.KeyboardToggle, new Vector3(keysX, rowY, -0.002f), ButtonSize, onKeyboard);
            b.SendButton = PokeButton.Create(c, Texts.ReplySend, new Vector3(sendX, rowY, -0.002f), ButtonSize, onSend, primary: true);
            b._status = Ui.Text(c, "Status", "", Vector3.zero, width, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            Ui.Middle(b._status, width, StatusHeight, TextAlignmentOptions.Left);
            b._status.overflowMode = TextOverflowModes.Ellipsis;
            b._status.transform.localPosition = new Vector3(0f, -RowHeight - StatusHeight * 0.5f, -0.001f);
            b.SetDraft("");
            return b;
        }

        /// <summary>The draft as typed: plain, its line breaks and controls visible, the latest part when long; a placeholder when empty.</summary>
        /// <summary>What the empty field says ("Tell Codex something"; ours).</summary>
        public void SetPlaceholder(string text)
        {
            _placeholder = text;
            SetDraft(_draftText);
        }

        public void SetDraft(string draft)
        {
            _draftText = draft ?? "";
            if (string.IsNullOrEmpty(draft))
            {
                _draft.text = _placeholder; // ours
                _draft.color = Theme.Muted;
            }
            else
            {
                Ui.SetPlain(_draft, Untrusted.Tail(Untrusted.Visible(draft), DraftTail)); // never cuts a ‹U+…› marker
                _draft.color = Theme.Text;
            }
        }

        /// <summary>The status line (ours), and which buttons can be pressed.</summary>
        public void SetStatus(string line, Color color, bool canSend, bool canEdit)
        {
            _status.text = line;
            _status.color = color;
            // an agent that cannot take replies: the explanation stands where the field would be, nothing to press
            var only = !string.IsNullOrEmpty(line) && line.EndsWith(Texts.ReplyNotSupported, StringComparison.Ordinal);
            if (only != _only)
            {
                _only = only;
                TextButton.gameObject.SetActive(!only);
                KeyboardButton.gameObject.SetActive(!only);
                SendButton.gameObject.SetActive(!only);
                _draft.gameObject.SetActive(!only);
                _status.transform.localPosition = new Vector3(0f, only ? -RowHeight * 0.5f : -RowHeight - StatusHeight * 0.5f, -0.001f);
            }
            SendButton.SetInteractable(canSend);
            TextButton.SetInteractable(canEdit);
            KeyboardButton.SetInteractable(canEdit);
        }

        /// <summary>The desk keyboard is out: "Keyboard" is drawn chosen.</summary>
        public void SetKeyboardOn(bool on) => KeyboardButton.SetChosen(on);
    }
}
