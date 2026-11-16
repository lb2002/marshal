using System;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The notice above the workspace (U5a; it replaces cards that opened by themselves): a quiet pill standing on the
    /// window's top edge, left-aligned with it, for 6 s: a dot (red when an agent needs the user, grey for an incident)
    /// and one line ("Checkout-Summen korrigieren braucht dich: Claude ändert die Datei"). A poke or a ray click on it
    /// selects that agent; nothing else happens, nothing opens. Plain text, cut to one line. Origin: the window's top
    /// left corner; the face looks along the parent's -z.
    /// </summary>
    public sealed class NoticePill : MonoBehaviour
    {
        public const float Height = Theme.H;
        const float Above = Theme.Unit;
        const float Dot = 2f * Theme.S;

        float _maxWidth;
        Transform _content;
        float _until;
        Action<string> _onPress;

        public string Sid { get; private set; }
        public string Line { get; private set; }
        public bool IsShown => gameObject.activeSelf;
        public PokeButton Button { get; private set; }

        public static NoticePill Create(Transform parent, Vector3 localTopLeft, float maxWidth, Action<string> onPress)
        {
            var p = Ui.Child(parent, "NoticePill", localTopLeft).AddComponent<NoticePill>();
            p._maxWidth = maxWidth;
            p._onPress = onPress;
            p.gameObject.SetActive(false);
            return p;
        }

        /// <param name="needsYou">Red dot: an agent needs the user; else a grey one (an incident).</param>
        public void Show(string line, string sid, bool needsYou, float seconds)
        {
            gameObject.SetActive(true);
            if (_content != null)
            {
                _content.gameObject.SetActive(false);
                Destroy(_content.gameObject);
            }
            _content = Ui.Child(transform, "Content", Vector3.zero).transform;
            Sid = sid;
            var textLeft = Theme.Unit + Dot + Theme.Unit;
            var t = Ui.Text(_content, "Line", "", Vector3.zero, _maxWidth, Theme.Body, Theme.Text);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.text = Ui.OneLine(t, line, _maxWidth - textLeft - Theme.Unit);
            Line = t.text;
            var w = Mathf.Min(_maxWidth, textLeft + t.GetPreferredValues(t.text, Mathf.Infinity, Mathf.Infinity).x + Theme.Unit + 0.002f);
            var cy = Above + Height * 0.5f;
            // the pill is graphite like the window (a secondary button's white would read as a control)
            Button = PokeButton.Create(_content, "Hinweis", new Vector3(w * 0.5f, cy, -0.001f), new Vector2(w, Height), Pressed,
                showLabel: false, background: Theme.Card);
            var visual = Button.transform.Find("Visual");
            var dot = Ui.Panel(visual, "Dot", new Vector3(-w * 0.5f + Theme.Unit + Dot * 0.5f, 0f, -0.001f), new Vector2(Dot, Dot),
                needsYou ? Theme.Red : Theme.Muted, Ui.QueueOnPanel);
            Theme.SetRadius(dot, Dot * 0.5f);
            Theme.SetRim(dot, 0f);
            Ui.FitHeight(t);
            t.rectTransform.sizeDelta = new Vector2(w - textLeft - Theme.Unit, t.rectTransform.sizeDelta.y);
            t.transform.SetParent(visual, false);
            t.transform.localPosition = new Vector3(-w * 0.5f + textLeft + t.rectTransform.sizeDelta.x * 0.5f, t.rectTransform.sizeDelta.y * 0.5f, -0.002f);
            _until = Time.time + seconds;
        }

        public void Hide()
        {
            Sid = null;
            Line = null;
            gameObject.SetActive(false);
        }

        void Pressed()
        {
            var sid = Sid;
            Hide();
            if (sid != null) _onPress?.Invoke(sid);
        }

        void Update()
        {
            if (Time.time >= _until) Hide();
        }
    }
}
