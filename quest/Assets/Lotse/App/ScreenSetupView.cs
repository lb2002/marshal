using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// What the display setup shows (U5b): a small panel with the prompt, "Back" and "Cancel"; a ring at the fingertip that
    /// fills during the 0.8 s dwell; a dot at each tapped corner; then the display's outline and the workspace's four slots
    /// as outlines (the highlighted one brighter, an unavailable one faint), each a pressable area (a poke, or a ray click:
    /// a pinch that begins on it, so it never reaches the hold). Only hairline white at low opacity, no new colours.
    /// Nothing here follows the head: the panel stands where the setup began, and below the display while the side is
    /// picked.
    /// </summary>
    public sealed class ScreenSetupView : MonoBehaviour
    {
        const float PanelWidth = 0.30f;
        const float RingInner = 0.009f;
        const float RingOuter = 0.012f;
        const float DotRadius = 0.005f;
        const float Line = 0.002f;
        static readonly Vector2 ButtonSize = new Vector2(0.11f, Theme.H);

        Transform _panel;
        MeshRenderer _panelBackground;
        TextMeshPro _prompt;
        Transform _ringRoot;
        MeshFilter _ring;
        float _ringShown = -1f;
        readonly List<(Transform t, FlatMesh mesh)> _dots = new List<(Transform, FlatMesh)>();
        Transform _screenOutline;
        FlatMesh _screenMesh;
        readonly Dictionary<DockSide, (Transform t, FlatMesh mesh, PokeButton area)> _slots = new Dictionary<DockSide, (Transform, FlatMesh, PokeButton)>();
        string _slotsKey;

        public PokeButton BackButton { get; private set; }
        public PokeButton CancelButton { get; private set; }
        public string PromptShown => _panel.gameObject.activeSelf ? _prompt.text : null;
        public int DotsShown { get; private set; }
        public float RingShown => _ringRoot.gameObject.activeSelf ? Mathf.Max(0f, _ringShown) : 0f;
        public bool SlotsShown => _screenOutline.gameObject.activeSelf;
        public DockSide? Highlighted { get; private set; }
        public PokeButton SlotArea(DockSide side) => _slots[side].area;
        public bool SlotAvailable(DockSide side) => _slots[side].area.Interactable;

        public static ScreenSetupView Create(Action onBack, Action onCancel, Action<DockSide> onPick)
        {
            var v = new GameObject("ScreenSetup").AddComponent<ScreenSetupView>();
            v._panel = Ui.Child(v.transform, "Panel", Vector3.zero).transform;
            v._panelBackground = Ui.Panel(v._panel, "Background", new Vector3(0f, 0f, 0.002f), Vector2.one, Theme.GlassDense);
            v._prompt = Ui.Text(v._panel, "Prompt", "", Vector3.zero, PanelWidth - 2f * Theme.Unit, Theme.Body, Theme.Text, TextAlignmentOptions.Top);
            v.BackButton = PokeButton.Create(v._panel, Texts.Back, Vector3.zero, ButtonSize, onBack);
            v.CancelButton = PokeButton.Create(v._panel, Texts.ReplyCancel, Vector3.zero, ButtonSize, onCancel);
            v._ringRoot = Ui.Child(v.transform, "Ring", Vector3.zero).transform;
            Ui.Ring(v._ringRoot, "Track", Vector3.zero, RingInner, RingOuter, 1f, Theme.Track);
            v._ring = Ui.Ring(v._ringRoot, "Fill", new Vector3(0f, 0f, -0.0005f), RingInner, RingOuter, 0f, Theme.Fill);
            for (var i = 0; i < 3; i++)
            {
                var t = Ui.Child(v.transform, "Dot " + (i + 1), Vector3.zero).transform;
                var m = new FlatMesh(t, "Mark", 0f);
                m.Disc(Vector2.zero, DotRadius, FlatMesh.Solid(Look.TextPrimary));
                m.Apply();
                t.gameObject.SetActive(false);
                v._dots.Add((t, m));
            }
            v._screenOutline = Ui.Child(v.transform, "Display", Vector3.zero).transform;
            v._screenMesh = new FlatMesh(v._screenOutline, "Outline", 0f);
            foreach (var side in DockSlots.Sides)
            {
                var t = Ui.Child(v._screenOutline.parent, "Slot " + side, Vector3.zero).transform;
                var m = new FlatMesh(t, "Outline", 0f);
                var s = side;
                var area = PokeButton.CreateArea(t, "Slot " + side, Vector3.zero, Vector2.one * 0.1f, () => onPick(s));
                v._slots[side] = (t, m, area);
            }
            v.Hide();
            return v;
        }

        /// <summary>The panel in front of `head` (0.45 m, 12 cm below the eyes), facing it; it stays there.</summary>
        public void PlacePanel(Vector3 head, Vector3 headForward)
        {
            var flat = Vector3.ProjectOnPlane(headForward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            var at = head + flat * 0.45f + Vector3.down * 0.12f;
            _panel.SetPositionAndRotation(at, Quaternion.LookRotation(flat, Vector3.up));
        }

        /// <summary>The corner phase: the prompt, a dot per tapped corner, the dwell ring at the resting fingertip.</summary>
        public void ShowCorners(string prompt, IReadOnlyList<Vec3> points, float progress, Vec3? ringAt, Vector3 head)
        {
            SetPrompt(prompt, back: points.Count > 0);
            for (var i = 0; i < _dots.Count; i++)
            {
                var on = i < points.Count;
                if (_dots[i].t.gameObject.activeSelf != on) _dots[i].t.gameObject.SetActive(on);
                if (on) Face(_dots[i].t, Ui.ToVector(points[i]), head);
            }
            DotsShown = points.Count;
            var ring = ringAt.HasValue && progress > 0f;
            if (_ringRoot.gameObject.activeSelf != ring) _ringRoot.gameObject.SetActive(ring);
            if (ring)
            {
                Face(_ringRoot, Ui.ToVector(ringAt.Value), head);
                if (Mathf.Abs(progress - _ringShown) > 0.01f)
                {
                    _ringShown = progress;
                    RingMesh.Build(_ring.sharedMesh, RingInner, RingOuter, progress);
                }
            }
            SetSlots(false);
        }

        /// <summary>The side phase: the display's outline and the four slots, `highlight` brighter, unavailable ones faint.</summary>
        public void ShowSides(string prompt, in ScreenRect screen, float? deskY, DockSide? highlight, Vector3 head)
        {
            SetPrompt(prompt, back: true);
            foreach (var d in _dots) if (d.t.gameObject.activeSelf) d.t.gameObject.SetActive(false);
            DotsShown = 0;
            if (_ringRoot.gameObject.activeSelf) _ringRoot.gameObject.SetActive(false);
            SetSlots(true);
            Highlighted = highlight;
            var key = screen.Center + "|" + screen.Forward + "|" + screen.Width + "|" + deskY + "|" + highlight;
            if (key == _slotsKey) return;
            _slotsKey = key;
            var rot = Quaternion.LookRotation(Ui.ToVector(screen.Forward), Ui.ToVector(screen.Up));
            _screenOutline.SetPositionAndRotation(Ui.ToVector(screen.Center), rot);
            Outline(_screenMesh, screen.Width, screen.Height, FlatMesh.OnPanel(Look.TextPrimary, 0.25f));
            foreach (var side in DockSlots.Sides)
            {
                var slot = DockSlots.Slot(screen, side);
                var available = DockSlots.Available(slot, deskY);
                var (t, mesh, area) = _slots[side];
                t.SetPositionAndRotation(Ui.ToVector(slot.Center), rot);
                var a = !available ? 0.08f : side == highlight ? 0.85f : 0.3f;
                Outline(mesh, slot.Width, slot.Height, FlatMesh.OnPanel(Look.TextPrimary, a));
                area.Resize(new Vector2(slot.Width, slot.Height));
                area.transform.localPosition = new Vector3(0f, 0f, -0.002f);
                area.SetInteractable(available);
            }
            // the panel under the display, facing the head
            var below = Ui.ToVector(screen.Center - screen.Up * (screen.Height * 0.5f + 0.12f)) - Ui.ToVector(screen.Forward) * 0.05f;
            Face(_panel, below, head);
        }

        public void Hide()
        {
            if (_panel.gameObject.activeSelf) _panel.gameObject.SetActive(false);
            if (_ringRoot.gameObject.activeSelf) _ringRoot.gameObject.SetActive(false);
            foreach (var d in _dots) if (d.t.gameObject.activeSelf) d.t.gameObject.SetActive(false);
            DotsShown = 0;
            SetSlots(false);
            _slotsKey = null;
        }

        void SetSlots(bool on)
        {
            if (_screenOutline.gameObject.activeSelf != on) _screenOutline.gameObject.SetActive(on);
            foreach (var s in _slots.Values)
                if (s.t.gameObject.activeSelf != on) s.t.gameObject.SetActive(on);
        }

        void SetPrompt(string prompt, bool back)
        {
            if (!_panel.gameObject.activeSelf) _panel.gameObject.SetActive(true);
            if (BackButton.gameObject.activeSelf != back) BackButton.gameObject.SetActive(back);
            if (_prompt.text == prompt) return;
            Ui.SetPlain(_prompt, prompt);
            var h = Ui.FitHeight(_prompt);
            var total = Theme.Unit + h + Theme.Unit + ButtonSize.y + Theme.Unit;
            _prompt.transform.localPosition = new Vector3(0f, total * 0.5f - Theme.Unit, -0.001f);
            var rowY = -total * 0.5f + Theme.Unit + ButtonSize.y * 0.5f;
            BackButton.transform.localPosition = new Vector3(-ButtonSize.x * 0.5f - Theme.Unit * 0.5f, rowY, -0.002f);
            CancelButton.transform.localPosition = new Vector3(ButtonSize.x * 0.5f + Theme.Unit * 0.5f, rowY, -0.002f);
            _panelBackground.transform.localScale = new Vector3(PanelWidth, total, 1f);
        }

        /// <summary>A rectangle outline of hairlines, centred on the owner, w by h metres.</summary>
        static void Outline(FlatMesh m, float w, float h, Color c)
        {
            m.Clear();
            var x = w * 0.5f;
            var y = h * 0.5f;
            m.Stroke(new Vector2(-x, y), new Vector2(x, y), Line, c);
            m.Stroke(new Vector2(x, y), new Vector2(x, -y), Line, c);
            m.Stroke(new Vector2(x, -y), new Vector2(-x, -y), Line, c);
            m.Stroke(new Vector2(-x, -y), new Vector2(-x, y), Line, c);
            m.Apply();
        }

        /// <summary>Puts `t` at `at`, its face (-z) toward the head, upright.</summary>
        static void Face(Transform t, Vector3 at, Vector3 head)
        {
            var away = at - head;
            if (away.sqrMagnitude < 1e-6f) away = Vector3.forward;
            t.SetPositionAndRotation(at, Quaternion.LookRotation(away, Vector3.up));
        }
    }
}
