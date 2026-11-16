using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// A pill-shaped button pressed by poking it with an index finger tip (PokeLogic per hand). AppRoot feeds the
    /// tips to every enabled button each frame through ProbeAll. The hand ray (RayPointer) can click it too.
    /// A finger or a ray near it brightens it and scales it to 1.04; a press flashes it and pushes it in. An icon
    /// (one of Icons, our own) can sit left of the label. Only the look moves: the pressable face (Rect) stays put.
    /// </summary>
    /// <summary>
    /// The three kinds of button (U13), and the text-only control: quiet (white at 7 %, ink label), primary (ink fill,
    /// asphalt label), deny (no fill, a red outline at about half strength, a light red label), text (no fill, a muted
    /// label; a faint fill under a pointer: header tools and text links).
    /// </summary>
    public enum ButtonKind { Quiet, Primary, Deny, Text }

    public sealed class PokeButton : MonoBehaviour
    {
        static readonly List<PokeButton> Enabled = new List<PokeButton>();
        static readonly List<PokeButton> Scratch = new List<PokeButton>();

        const float FlashSeconds = 0.18f;
        // how fast the look scales toward its target (per second)
        const float ScaleSpeed = 0.8f;

        PokeLogic _left = new PokeLogic();
        PokeLogic _right = new PokeLogic();
        // a key of the desk keyboard: how far its look goes down while pressed, and its soft click
        float _travel;
        bool _key;
        Vector2 _size;
        Transform _visual;
        MeshRenderer _background;
        float _flashUntil;
        Color _base;
        Color _lastColor;
        float _scale = 1f;
        bool _primary;
        ButtonKind _kind;
        Color? _background0;

        public ButtonKind Kind => _primary ? ButtonKind.Primary : _kind;
        TextMeshPro _labelText;
        TextMeshPro _iconText;

        public string Label { get; private set; }
        /// <summary>A key whose label is what the user types (desk keyboard, pairing and address pads): never logged.</summary>
        public bool Secret { get; private set; }
        public bool Interactable { get; private set; }
        public event Action Pressed;


        /// <summary>The currently enabled buttons (tests use it to find and poke them).</summary>
        public static IReadOnlyList<PokeButton> All => Enabled;

        public Vector2 Size => _size;

        /// <summary>
        /// The invisible hit area around the drawn control, per side (U10: a ray target is at least 3 degrees). Only the
        /// ray uses it; a fingertip still has to touch the drawn control. Everything else about a press is the same: a
        /// pinch that begins on the padded area is a click on this control and is masked from holds like any other.
        /// </summary>
        public Vector2 HitPad { get; set; }

        /// <summary>The face the hand ray hits: the drawn control and its hit padding, in world space.</summary>
        public PokeRect RayRect => new PokeRect(Ui.ToVec(transform.position), Ui.ToVec(transform.right), Ui.ToVec(transform.up),
            (_size.x * 0.5f + HitPad.x) * transform.lossyScale.x, (_size.y * 0.5f + HitPad.y) * transform.lossyScale.y);

        /// <summary>It is drawn hovered or pressed now (every control shows both, U10).</summary>
        public bool HoverShown { get; private set; }
        public bool PressShown { get; private set; }

        /// <summary>The look's current scale (1 at rest, 1.04 under a finger or a ray).</summary>
        public float VisualScale => _scale;

        /// <summary>The pressable face in world space (for keeping buttons apart).</summary>
        public PokeRect Rect => new PokeRect(Ui.ToVec(transform.position), Ui.ToVec(transform.right), Ui.ToVec(transform.up),
            _size.x * 0.5f * transform.lossyScale.x, _size.y * 0.5f * transform.lossyScale.y);

        /// <param name="icon">One of Icons, drawn left of the label; null for none.</param>
        /// <param name="primary">The panel's main action: drawn in the accent colour.</param>
        /// <param name="showLabel">False: the label only names the button (logs, tests); something else is drawn on it.</param>
        /// <param name="cornerRadius">A rounded box with this corner radius instead of a pill (a text field).</param>
        public static PokeButton Create(Transform parent, string label, Vector3 localCenter, Vector2 size, Action onPress,
            bool interactable = true, string icon = null, bool primary = false, bool showLabel = true, float cornerRadius = -1f,
            Color? background = null, ButtonKind kind = ButtonKind.Quiet)
        {
            if (kind == ButtonKind.Primary) primary = true;
            var go = Ui.Child(parent, "Button " + label, localCenter);
            var b = go.AddComponent<PokeButton>();
            b._size = size;
            b.Label = label;
            b.Interactable = interactable;
            b._primary = primary;
            b._kind = primary ? ButtonKind.Quiet : kind;
            b._background0 = background;
            b._base = !interactable ? Theme.Disabled : b.Rest;
            b._visual = Ui.Child(go.transform, "Visual", Vector3.zero).transform;
            b._background = Ui.Panel(b._visual, "Background", Vector3.zero, size, b._base, Ui.QueueOnPanel);
            if (cornerRadius >= 0f) Theme.SetRadius(b._background, cornerRadius);
            if (b._kind == ButtonKind.Deny)
            {
                // the deny button: no fill, an outline in stop red at about half strength (mixed with the raised fill)
                Theme.SetRim(b._background, 1f);
                Theme.SetRimColor(b._background, DenyRim);
            }

            // one label style on buttons (U13: 17 units, Medium); a text-only control's is Small, Regular, muted
            var fontSize = b._kind == ButtonKind.Text ? Theme.Small : Theme.Label;
            var iconW = icon != null ? fontSize * 0.16f : 0f;
            var iconGap = icon != null ? fontSize * 0.07f : 0f;
            var room = Mathf.Max(0.01f, size.x - 2f * Theme.S - iconW - iconGap);
            var textColor = !interactable ? Theme.DisabledText : b.LabelRest;
            var text = Ui.Text(b._visual, "Label", showLabel ? label : "", Vector3.zero, room, fontSize, textColor, TextAlignmentOptions.Top);
            b._labelText = text;
            if (b._kind != ButtonKind.Text) text.font = Fonts.Medium;
            if (primary)
            {
                // dark strokes on the light primary colour thin out when blended in linear light (on the headset the label
                // read as light grey on white): the heaviest face
                text.font = Fonts.SemiBold;
                text.fontSharedMaterial = PrimaryLabelMaterial;
            }
            // a label that does not fit on one line is cut with an ellipsis (no second size)
            text.textWrappingMode = TextWrappingModes.NoWrap;
            var wide = text.GetPreferredValues(text.text, Mathf.Infinity, Mathf.Infinity).x;
            if (wide > room && showLabel) text.text = Ui.OneLine(text, label, room);
            var labelW = Mathf.Min(room, text.GetPreferredValues(text.text, Mathf.Infinity, Mathf.Infinity).x + 0.002f);
            var left = -(iconW + iconGap + labelW) * 0.5f;
            // the label's line box centred on the button's middle, a hair in front of the background
            Ui.Middle(text, labelW + 0.002f, size.y, TextAlignmentOptions.Center);
            text.transform.localPosition = new Vector3(left + iconW + iconGap + labelW * 0.5f, 0f, -0.001f);
            if (icon != null)
            {
                var i = Ui.Text(b._visual, "Icon", icon, Vector3.zero, iconW * 1.6f, fontSize * 1.15f,
                    textColor, TextAlignmentOptions.Top);
                i.transform.localPosition = new Vector3(left + iconW * 0.5f, i.rectTransform.sizeDelta.y * 0.5f, -0.001f);
                b._iconText = i;
            }
            b._lastColor = b._base;
            // a control of the one height gets the hit padding (its neighbours are 4 x S away); larger ones need none
            if (size.y <= Theme.H + 1e-5f) b.HitPad = new Vector2(Theme.HitPad, Theme.HitPad);
            if (onPress != null) b.Pressed += onPress;
            return b;
        }

        /// <summary>
        /// A pressable area without a look of its own (U5a: a row of the agent list, which draws itself): poked and
        /// ray-clicked like any button, with the same rising-edge rules; nothing on it moves or lights up.
        /// </summary>
        public static PokeButton CreateArea(Transform parent, string label, Vector3 localCenter, Vector2 size, Action onPress)
        {
            var go = Ui.Child(parent, "Area " + label, localCenter);
            var b = go.AddComponent<PokeButton>();
            b._size = size;
            b.Label = label;
            b.Interactable = true;
            b._visual = Ui.Child(go.transform, "Visual", Vector3.zero).transform;
            if (onPress != null) b.Pressed += onPress;
            return b;
        }

        /// <summary>A pressable area's new size (U6: the header's tabs follow their labels). Areas only: a button's look keeps its size.</summary>
        public void Resize(Vector2 size)
        {
            _size = size;
            if (_hover != null) _hover.transform.localScale = new Vector3(size.x, size.y, 1f);
        }

        MeshRenderer _hover;
        int _hoverState = -1;

        /// <summary>
        /// The area's owner draws its hover and pressed states itself, by the colour of what it already draws (U11: at
        /// the workspace's peak there is no draw call to spare for a hover surface). Read AreaState.
        /// </summary>
        public bool OwnerDrawsHover { get; set; }

        /// <summary>An area's state: 0 at rest, 1 hovered, 2 pressed. 0 while it is off.</summary>
        public int AreaState => _background == null && isActiveAndEnabled && _hoverState > 0 ? _hoverState : 0;
        static readonly Color HoverTint = new Color(1f, 1f, 1f, 0.07f);
        static readonly Color PressTint = new Color(1f, 1f, 1f, 0.18f);

        /// <summary>
        /// An area's hover and pressed states (U10): a faint light surface over its face, just behind its parent's text,
        /// lighter while pressed. Made when first needed, hidden at rest; nothing moves.
        /// </summary>
        void PaintArea(bool hovered, bool pressed)
        {
            var state = pressed ? 2 : hovered ? 1 : 0;
            if (state == _hoverState) return;
            _hoverState = state;
            if (OwnerDrawsHover) return; // drawn by the owner in its own mesh: no renderer for it (U11)
            if (state == 0)
            {
                if (_hover != null) _hover.gameObject.SetActive(false);
                return;
            }
            if (_hover == null)
            {
                _hover = Theme.Shape(_visual, "Hover", Vector3.zero, _size, HoverTint, Theme.ChipMaterial);
                Theme.SetRim(_hover, 0f);
            }
            // in the parent's plane, a hair behind its text (which stands 1 mm in front of the plane)
            _hover.transform.localPosition = new Vector3(0f, 0f, -0.0004f - transform.localPosition.z);
            _hover.transform.localScale = new Vector3(_size.x, _size.y, 1f);
            _hover.gameObject.SetActive(true);
            Ui.SetColor(_hover, pressed ? PressTint : HoverTint);
        }

        /// <summary>
        /// A key of the desk keyboard (U4b): a short dwell before a press counts, no margin, a quicker repeat, and on a
        /// press its look goes `travel` metres down into the board with a soft click (when the sound is on).
        /// </summary>
        public void MakeKey(double dwellMs, double cooldownMs, float travel)
        {
            _left = new PokeLogic(cooldownMs, 0f, dwellMs);
            _right = new PokeLogic(cooldownMs, 0f, dwellMs);
            _travel = travel;
            HitPad = Vector2.zero;
            _key = true;
            Secret = true;
        }

        /// <summary>Its label is typed text (a pad digit): a press is logged without it.</summary>
        public void MarkSecret() => Secret = true;

        /// <summary>The key's look is down (pressed) now.</summary>
        public bool KeyDown => _key && Time.time < _flashUntil;

        /// <summary>Drawn as the chosen option (in the primary colour) or not (a toggle such as "Tastatur").</summary>
        public void SetChosen(bool chosen)
        {
            if (chosen == _primary || _background == null) return;
            _primary = chosen;
            if (!Interactable) return;
            _base = Rest;
            var textColor = LabelRest;
            _labelText.color = textColor;
            if (_iconText != null) _iconText.color = textColor;
            _lastColor = _base;
            Ui.SetColor(_background, _base);
        }

        /// <summary>Changes the label (a desk key's capital or lower case letter).</summary>
        public void SetLabel(string label)
        {
            if (label == Label) return;
            Label = label;
            if (_labelText != null) _labelText.text = label;
        }

        /// <summary>Switches the button on or off (a disabled one is greyed out and cannot be pressed).</summary>
        public void SetInteractable(bool on)
        {
            if (on == Interactable) return;
            Interactable = on;
            _left.Reset();
            _right.Reset();
            if (_background == null) return;
            _base = !on ? Theme.Disabled : Rest;
            var textColor = !on ? Theme.DisabledText : LabelRest;
            _labelText.color = textColor;
            if (_iconText != null) _iconText.color = textColor;
            _left.Reset();
            _right.Reset();
            _lastColor = _base;
            Ui.SetColor(_background, _base);
            Theme.SetHighlight(_background, 0f);
        }

        static Material _primaryLabel;

        /// <summary>One shared material for the labels of primary buttons: Geist Medium at the SDF shader's default sharpness (no dilate: sharp text on the headset).</summary>
        static Material PrimaryLabelMaterial
        {
            get
            {
                if (_primaryLabel != null) return _primaryLabel;
                _primaryLabel = new Material(Fonts.SemiBold.material) { name = "Lotse Primary Label" };
                return _primaryLabel;
            }
        }

        /// <summary>Its colour at rest: the primary colour, its own background, or the secondary colour.</summary>
        Color Rest => _primary ? Theme.Primary
            : _kind == ButtonKind.Deny ? new Color(Look.Stop.R, Look.Stop.G, Look.Stop.B, 0f)
            : _kind == ButtonKind.Text ? new Color(1f, 1f, 1f, 0f)
            : _background0 ?? Theme.Secondary;

        /// <summary>The label's colour at rest for its kind.</summary>
        Color LabelRest => _primary ? Theme.OnPrimary : _kind == ButtonKind.Deny ? Theme.DenyText : _kind == ButtonKind.Text ? Theme.Muted : Theme.Text;

        /// <summary>The deny button's outline: stop red at 53 % over the raised fill (the shader's rim is opaque).</summary>
        static Color DenyRim => new Color(0.53f * Look.Stop.R + 0.47f * Look.Raised.R, 0.53f * Look.Stop.G + 0.47f * Look.Raised.G,
            0.53f * Look.Stop.B + 0.47f * Look.Raised.B, 1f);

        /// <summary>
        /// What the button stands for changed (a row now shows another agent): a press in progress is dropped, so a press
        /// always means what it was aimed at. PressKey changes too, which ends a ray click in progress.
        /// </summary>
        public void ResetPress()
        {
            _left.Reset();
            _right.Reset();
            PressKey++;
        }

        /// <summary>Its name in the device log: its label as one token; a key whose label is typed text is just "key".</summary>
        public string LogName => Secret ? "key" : LogAs ?? Ev.Token(Label);

        /// <summary>A fixed name for the device log instead of the label (a label that is an address is never logged).</summary>
        public string LogAs { get; set; }

        /// <summary>Changes whenever a press in progress must not count any more (see ResetPress).</summary>
        public int PressKey { get; private set; }

        static int _nextUid;

        /// <summary>A number of its own (the ray's target id).</summary>
        public int Uid { get; } = ++_nextUid;

        /// <summary>A hand ray points at this button (drawn like a hovering fingertip).</summary>
        public bool RayHover { get; set; }

        /// <summary>A click with the hand ray (RayPointer): presses the button like a poke.</summary>
        public void Click()
        {
            if (!Interactable || !isActiveAndEnabled) return;
            Fire("ray");
        }

        void OnEnable() => Enabled.Add(this);

        void OnDisable()
        {
            Enabled.Remove(this);
            _left.Reset();
            _right.Reset();
            if (_background == null)
            {
                // an area that goes away is neither hovered nor pressed
                HoverShown = false;
                PressShown = false;
                PaintArea(false, false);
            }
        }

        /// <summary>Feeds both tips to every enabled button. Handlers may destroy buttons; a snapshot is iterated.</summary>
        public static void ProbeAll(double nowMs, in HandFrame left, in HandFrame right)
        {
            Scratch.Clear();
            Scratch.AddRange(Enabled);
            AnyNear = false;
            foreach (var b in Scratch)
                if (b != null && b.isActiveAndEnabled)
                {
                    b.Probe(nowMs, left, right);
                    AnyNear |= b.Interactable && (b._left.Hover || b._right.Hover);
                }
        }

        /// <summary>After ProbeAll: a fingertip is at some button (a poke may be in progress; the stop gesture waits).</summary>
        public static bool AnyNear { get; private set; }

        void Probe(double nowMs, in HandFrame left, in HandFrame right)
        {
            if (!Interactable) return;
            var hw = _size.x * 0.5f;
            var hh = _size.y * 0.5f;
            var pl = transform.InverseTransformPoint(left.Tip);
            var pr = transform.InverseTransformPoint(right.Tip);
            var pressed = _left.Update(nowMs, left.TipValid, pl.x, pl.y, pl.z, hw, hh);
            pressed |= _right.Update(nowMs, right.TipValid, pr.x, pr.y, pr.z, hw, hh);

            if (pressed) Fire("poke");
            else Paint();
        }

        bool Hovered => _left.Hover || _right.Hover || RayHover;

        /// <summary>A drawn button's fill at rest (0), hovered (1) and pressed (2): its own colour, lighter.</summary>
        public static Color StateColor(Color rest, int state) => state == 2 ? Brighter(rest, 0.3f) : state == 1 ? Brighter(rest, 0.08f) : rest;

        /// <summary>The label's colour on a button that is on: dark on the light primary fill in every state, light otherwise.</summary>
        public static Color LabelColor(bool primary) => primary ? Theme.OnPrimary : Theme.Text;

        /// <summary>Colour and scale: pressed (flash, pushed in), hovered (brighter, 1.04) or at rest.</summary>
        void Paint()
        {
            var flashing = Time.time < _flashUntil;
            HoverShown = Interactable && Hovered;
            PressShown = flashing;
            if (_background == null)
            {
                PaintArea(HoverShown, flashing); // an area's own look is its owner's; this is its hover and pressed state
                return;
            }
            var c = StateColor(_base, flashing ? 2 : Hovered ? 1 : 0);
            if (c != _lastColor)
            {
                Ui.SetColor(_background, c);
                Theme.SetHighlight(_background, Hovered || flashing ? 1f : 0f);
                // a text-only control's label goes from muted to ink under a pointer
                if (_kind == ButtonKind.Text && !_primary && Interactable) _labelText.color = Hovered || flashing ? Theme.Text : Theme.Muted;
                _lastColor = c;
            }
            if (Theme.Still) return; // no motion while a hold runs
            if (_key)
            {
                // a key goes down into the board while pressed, and comes back up
                var z = flashing ? _travel : 0f;
                if (!Mathf.Approximately(_visual.localPosition.z, z)) _visual.localPosition = new Vector3(0f, 0f, z);
                return;
            }
            // U10: nothing moves on hover or press; the colour and the border say it
        }

        /// <summary>A quiet see-through button gets a little denser; a solid one a little lighter.</summary>
        static Color Brighter(Color c, float k) => c.a < 0.5f
            ? new Color(c.r, c.g, c.b, Mathf.Min(1f, c.a + k * 0.5f))
            : new Color(Mathf.Lerp(c.r, 1f, k), Mathf.Lerp(c.g, 1f, k), Mathf.Lerp(c.b, 1f, k), Mathf.Max(c.a, 0.97f));

        void Fire(string how)
        {
            _flashUntil = Time.time + FlashSeconds;
            Paint();
            // a key's label is what the user types (a letter, a code digit): it never goes to the log
            Debug.Log(Secret ? $"[Lotse] key pressed ({how})" : $"[Lotse] button pressed: {LogAs ?? Label} ({how})");
            Ev.Emit("click", ("via", how), ("target", LogName));
            // not on this object: a press often destroys its own button
            Sounds.Play(_key ? Sfx.Key : Sfx.Select, transform.position);
            Pressed?.Invoke();
        }
    }
}
