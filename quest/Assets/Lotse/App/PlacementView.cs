using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// What desk placement shows: the hint in front of the head (it follows the head lazily: it starts moving
    /// once it is 15 cm or 25 degrees off and glides back) and a small reticle lying on the desk under the index
    /// tip. The reticle turns green while the tip touches the desk, and an outer ring fills during the hold.
    /// </summary>
    public sealed class PlacementView : MonoBehaviour
    {
        const float HintWidth = 0.36f;
        const float HintForward = 0.5f;
        const float HintBelowEyes = 0.12f;
        const float RingInner = 0.012f;
        const float RingOuter = 0.017f;
        const float HoldInner = 0.021f;
        const float HoldOuter = 0.026f;

        static readonly Color Searching = new Color(0.85f, 0.88f, 0.95f, 0.7f);
        static readonly Color OnDesk = new Color(0.35f, 0.9f, 0.5f, 0.95f);

        GameObject _hint;
        Transform _reticle;
        MeshRenderer _ring;
        MeshFilter _hold;
        float _shownProgress = -1f;
        Color _ringColor;
        bool _following;

        public bool HintShown => _hint.activeSelf;
        public bool ReticleShown => _reticle.gameObject.activeSelf;
        public Vector3 ReticlePosition => _reticle.position;

        public static PlacementView Create(Transform parent)
        {
            var v = Ui.Child(parent, "Placement", Vector3.zero).AddComponent<PlacementView>();

            v._hint = Ui.Child(v.transform, "Hint", Vector3.zero);
            v.BuildHint();

            v._reticle = Ui.Child(v.transform, "Reticle", Vector3.zero).transform;
            v._ring = Ui.Ring(v._reticle, "Ring", Vector3.zero, RingInner, RingOuter, 1f, Searching).GetComponent<MeshRenderer>();
            v._ringColor = Searching;
            v._hold = Ui.Ring(v._reticle, "Hold", Vector3.zero, HoldInner, HoldOuter, 0f, OnDesk);

            v.Hide();
            return v;
        }

        /// <summary>The hint's text on its panel, in the current look.</summary>
        void BuildHint()
        {
            var content = Ui.Child(_hint.transform, "Content", Vector3.zero).transform;
            var text = Ui.Text(content, "Line", Texts.PlaceHint, Vector3.zero, HintWidth - 0.03f, Ui.BodySize, Ui.TextColor,
                TextAlignmentOptions.Top);
            var h = text.rectTransform.sizeDelta.y;
            text.transform.localPosition = new Vector3(0f, h * 0.5f, -0.001f);
            Ui.Panel(content, "Background", new Vector3(0f, 0f, 0.002f), new Vector2(HintWidth, h + 0.02f), Ui.PanelColor);
        }

        /// <summary>The look changed (Einstellungen): the hint is built again in it.</summary>
        public void Restyle()
        {
            foreach (Transform child in _hint.transform) Destroy(child.gameObject);
            // text is measured on build, which needs an active object
            var shown = _hint.activeSelf;
            var active = gameObject.activeSelf;
            gameObject.SetActive(true);
            _hint.SetActive(true);
            BuildHint();
            _hint.SetActive(shown);
            gameObject.SetActive(active);
        }

        public Vector3 HintPosition => _hint.transform.position;

        /// <summary>The hint 0.5 m in front of the head (yaw only), a little below the eyes, facing the user.</summary>
        public void ShowHint(Transform head)
        {
            HintTarget(head, out var position, out var rotation);
            _hint.transform.SetPositionAndRotation(position, rotation);
            _following = false;
            _hint.SetActive(true);
        }

        /// <summary>Called every frame while placing: the hint glides back in front of the head once it is far off.</summary>
        public void FollowHead(Transform head, float deltaSeconds)
        {
            if (!_hint.activeSelf) return;
            HintTarget(head, out var position, out var rotation);
            LazyFollow.Step(_hint.transform, position, rotation, ref _following, deltaSeconds);
        }

        static void HintTarget(Transform head, out Vector3 position, out Quaternion rotation)
        {
            var forward = LazyFollow.Flat(head);
            position = head.position + forward * HintForward + Vector3.down * HintBelowEyes;
            rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        /// <summary>The reticle flat on the hit surface (its front faces along the normal), 2 mm above it.</summary>
        public void ShowReticle(Vector3 point, Vector3 normal, bool onDesk, float progress)
        {
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
            normal.Normalize();
            var up = Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up;
            _reticle.SetPositionAndRotation(point + normal * 0.002f, Quaternion.LookRotation(-normal, up));
            _reticle.gameObject.SetActive(true);

            var color = onDesk ? OnDesk : Searching;
            if (color != _ringColor)
            {
                Ui.SetColor(_ring, color);
                _ringColor = color;
            }
            progress = onDesk ? Mathf.Clamp01(progress) : 0f;
            if (!Mathf.Approximately(progress, _shownProgress))
            {
                RingMesh.Build(_hold.sharedMesh, HoldInner, HoldOuter, progress);
                _shownProgress = progress;
            }
        }

        public void HideReticle() => _reticle.gameObject.SetActive(false);

        public void Hide()
        {
            _hint.SetActive(false);
            HideReticle();
        }
    }
}
