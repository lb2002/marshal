using UnityEngine;
using UnityEngine.Rendering;

namespace Lotse.App
{
    /// <summary>
    /// A ray from each hand along the system's aim pose (HandFrame.Ray*), drawn as a thin line with a dot where it
    /// hits a button. The ray stays on while an approval block is shown (U5a fix 1); RayClickLogic keeps clicks and holds
    /// apart: a click is a short pinch that starts and ends on the same target with the other hand open and no hold in
    /// progress, and fires on release; a pinch that began on a target is masked from the hold (MaskLeft/MaskRight, which
    /// AppRoot applies before the session sees the hands). What the rays may hit follows the RayScope: nothing while
    /// placing, everything otherwise. The ray of the hand that holds the wrist menu is off: its pinch is the system menu's.
    /// A glass panel stops the ray (RayBlocker): what lies behind it cannot be clicked.
    /// </summary>
    public sealed class RayPointer : MonoBehaviour
    {
        const float LineWidth = 0.0015f;
        const float DotSize = 0.01f;
        static readonly Color Idle = new Color(1f, 1f, 1f, 0.35f);
        static readonly Color OnTarget = new Color(0.55f, 0.85f, 1f, 0.9f);

        sealed class Side
        {
            public Transform Line;
            public MeshRenderer LineRenderer;
            public Transform Dot;
            public PokeButton Hovered;
            public Color LineColor;
        }

        readonly Side _left = new Side();
        readonly Side _right = new Side();
        readonly RayClickLogic _clicks = new RayClickLogic();

        public bool LeftShown => _left.Line.gameObject.activeSelf;
        public bool RightShown => _right.Line.gameObject.activeSelf;
        /// <summary>The left hand's pinch began with its ray on a target: the hold must not see it.</summary>
        /// <summary>The control each ray rests on now (its log name), or null (U9: the pinch line's onTarget).</summary>
        public string LeftTarget => _left.Hovered != null ? _left.Hovered.LogName : null;
        public string RightTarget => _right.Hovered != null ? _right.Hovered.LogName : null;
        public bool MaskLeft => _clicks.MaskLeft;
        public bool MaskRight => _clicks.MaskRight;

        public static RayPointer Create()
        {
            var p = new GameObject("RayPointer").AddComponent<RayPointer>();
            p.Build(p._left, "Left");
            p.Build(p._right, "Right");
            return p;
        }

        void Build(Side s, string name)
        {
            var line = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(line.GetComponent<Collider>());
            line.name = name + " Ray";
            line.transform.SetParent(transform, false);
            s.LineRenderer = Setup(line);
            s.Line = line.transform;
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.name = name + " Ray Dot";
            dot.transform.SetParent(transform, false);
            dot.transform.localScale = Vector3.one * DotSize;
            Ui.SetColor(Setup(dot), OnTarget);
            s.Dot = dot.transform;
            line.SetActive(false);
            dot.SetActive(false);
        }

        static MeshRenderer Setup(GameObject go)
        {
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = Ui.TransparentAt(3000);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// <summary>Once per frame, after the buttons are probed. `holding`: a hold is in progress (no clicks then).</summary>
        public void Step(RayScope scope, MenuHand menuHand, in HandFrame left, in HandFrame right, double nowMs, bool holding)
        {
            var l = Aim(_left, scope, menuHand != MenuHand.Left, left);
            var r = Aim(_right, scope, menuHand != MenuHand.Right, right);
            if (scope == RayScope.Off) _clicks.Reset();
            var lc = _clicks.Update(nowMs, left.Sample, Id(l), right.Sample, Id(r), holding, out var rc);
            // both hands may point at one button: mark after both have moved off theirs
            if (_left.Hovered != null) _left.Hovered.RayHover = true;
            if (_right.Hovered != null) _right.Hovered.RayHover = true;
            // a click fires on release, with the ray still on the target it began on
            if (lc != 0 && l != null && Id(l) == lc) l.Click();
            if (rc != 0 && r != null && Id(r) == rc && r != l) r.Click();
        }

        /// <summary>
        /// A target's id: the button, and what it stands for now (a row that shows another agent is another target). The
        /// uid in the high half, the press key in the low half: two targets never share an id (U5a review 2).
        /// </summary>
        static long Id(PokeButton b) => b == null ? 0 : RayClickLogic.TargetId(b.Uid, b.PressKey);

        /// <summary>The button one ray rests on now (drawn), or null.</summary>
        PokeButton Aim(Side s, RayScope scope, bool free, in HandFrame f)
        {
            var on = scope != RayScope.Off && free && f.RayValid && f.Sample.Usable;
            PokeButton button = null;
            var surface = false;
            var distance = Layout.RayIdleMeters;
            if (on) Pick(f.RayOrigin, f.RayDirection, out button, out surface, ref distance);
            if (s.Hovered != null) s.Hovered.RayHover = false;
            s.Hovered = button;
            Draw(s, on, f.RayOrigin, f.RayDirection, distance, button != null, surface);
            return button;
        }

        /// <summary>
        /// The nearest button the ray hits within reach, in front of the nearest panel; `surface` when the ray ends on a
        /// panel instead (a panel stops the ray).
        /// </summary>
        static void Pick(Vector3 origin, Vector3 direction, out PokeButton button, out bool surface, ref float distance)
        {
            button = null;
            surface = false;
            var o = Ui.ToVec(origin);
            var d = Ui.ToVec(direction);
            var best = Layout.RayMaxMeters;
            var blockers = RayBlocker.All;
            for (var i = 0; i < blockers.Count; i++)
            {
                var blocker = blockers[i];
                if (blocker == null || !blocker.isActiveAndEnabled) continue;
                if (RayPick.HitsRect(o, d, blocker.Rect, 0f, best, out var tb) && tb < best)
                {
                    best = tb;
                    surface = true;
                }
            }
            var all = PokeButton.All;
            for (var i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null || !b.isActiveAndEnabled || !b.Interactable) continue;
                // the drawn control and its hit padding; one without padding keeps the small margin
                var padded = b.HitPad.x > 0f || b.HitPad.y > 0f;
                if (RayPick.HitsRect(o, d, b.RayRect, padded ? 0f : Layout.RayButtonMargin, best, out var t) && t < best)
                {
                    best = t;
                    button = b;
                }
            }
            if (button != null) surface = false;
            if (button != null || surface) distance = best;
        }

        static void Draw(Side s, bool on, Vector3 origin, Vector3 direction, float distance, bool hit, bool surface)
        {
            if (s.Line.gameObject.activeSelf != on) s.Line.gameObject.SetActive(on);
            var showDot = on && (hit || surface);
            if (s.Dot.gameObject.activeSelf != showDot) s.Dot.gameObject.SetActive(showDot);
            if (!on) return;
            var end = origin + direction * distance;
            // the cylinder primitive is 2 m tall along its y axis
            s.Line.SetPositionAndRotation((origin + end) * 0.5f, Quaternion.FromToRotation(Vector3.up, direction));
            s.Line.localScale = new Vector3(LineWidth, distance * 0.5f, LineWidth);
            if (showDot) s.Dot.position = end;
            var color = hit ? OnTarget : Idle;
            if (color == s.LineColor) return;
            s.LineColor = color;
            Ui.SetColor(s.LineRenderer, color);
        }
    }
}
