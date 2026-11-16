using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// A status line by the card ("Gekoppelt", "Verbindung verloren …", an incident's detail), shown for a
    /// while. It hangs from its origin (the top edge) and grows downward with the text, or, made with
    /// `standsOnOrigin`, stands on it (the bottom edge) and grows upward. Plain text only.
    /// </summary>
    public sealed class Banner : MonoBehaviour
    {
        const float Width = 0.44f;

        TextMeshPro _text;
        MeshRenderer _background;
        GameObject _root;
        float _until;
        bool _standsOnOrigin;

        /// <summary>The line on show, or null.</summary>
        public string Line { get; private set; }

        public static Banner Create(Transform parent, Vector3 localPos, bool standsOnOrigin = false)
        {
            var go = Ui.Child(parent, "Banner", localPos);
            var b = go.AddComponent<Banner>();
            b._standsOnOrigin = standsOnOrigin;
            b._root = Ui.Child(go.transform, "Panel", Vector3.zero);
            b._background = Ui.Panel(b._root.transform, "Background", new Vector3(0f, 0f, 0.002f), new Vector2(Width, 0.03f), Ui.PanelColor);
            b._text = Ui.Text(b._root.transform, "Line", "", new Vector3(0f, 0f, -0.001f), Width - 0.02f, Ui.BodySize, Ui.TextColor,
                TextAlignmentOptions.Top);
            b._root.SetActive(false);
            return b;
        }

        public void Show(string line, Color background, float seconds)
        {
            Line = line;
            Ui.SetPlain(_text, line);
            var h = Ui.FitHeight(_text);
            _text.transform.localPosition = new Vector3(0f, -0.007f, -0.001f);
            _background.transform.localScale = new Vector3(Width, h + 0.014f, 1f);
            _background.transform.localPosition = new Vector3(0f, -(h + 0.014f) * 0.5f, 0.002f);
            Ui.SetColor(_background, background);
            _root.transform.localPosition = _standsOnOrigin ? new Vector3(0f, h + 0.014f, 0f) : Vector3.zero;
            _root.SetActive(true);
            _until = Time.time + seconds;
        }

        public void Hide()
        {
            Line = null;
            _root.SetActive(false);
        }

        void Update()
        {
            if (_root.activeSelf && Time.time >= _until) Hide();
        }
    }
}
