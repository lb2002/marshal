#nullable enable
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>What the hand rays may click right now.</summary>
    public enum RayScope
    {
        /// <summary>No ray: placing (the hands belong to the placement).</summary>
        Off,
        /// <summary>Every button and every row of the agent list, also while an approval block is shown.</summary>
        All,
    }

    /// <summary>
    /// The ray clicks of both hands, kept apart from the hold gesture (U5a fix 1, section C). The ray stays on while an
    /// approval block is shown, so a click and a hold must never be mistaken for each other:
    /// - A click: one hand pinches while its ray rests on a target, and releases within ClickMs while still on that same
    ///   target, and the other hand did not pinch at any time in between, and no hold was in progress. It fires on release.
    /// - A pinch that began with that hand's ray on any target is masked from the hold gesture for its whole duration
    ///   (MaskLeft/MaskRight: the app feeds the session an open hand instead). So a hold needs pinches that began off
    ///   every target, and while a hold is in progress ray clicks are ignored.
    /// - Losing tracking mid-pinch spoils the click but keeps the pinch, and with it the mask, until the hand is seen open
    ///   again (U5a review 2): the rule holds here on its own, not only because the HoldGate disarms a lost hand.
    /// Targets are ids (0: none), 64-bit so a button's id and what it stands for never collide. Pure, time injected.
    /// </summary>
    public sealed class RayClickLogic
    {
        /// <summary>A click is a pinch released within this time.</summary>
        public const double ClickMs = 600;

        struct Hand
        {
            public bool Pinching;
            public long Target;
            public double Since;
            public bool Spoiled;
        }

        Hand _left;
        Hand _right;

        /// <summary>The left hand's pinch began on a target: the session must not see it.</summary>
        public bool MaskLeft => _left.Pinching && _left.Target != 0;
        public bool MaskRight => _right.Pinching && _right.Target != 0;

        /// <summary>A button's target id: its uid in the high half, its press key in the low half (never 0, never shared).</summary>
        public static long TargetId(int uid, int pressKey) => ((long)uid << 32) | (uint)(pressKey + 1);

        /// <summary>Which targets the rays may click: none while placing.</summary>
        public static RayScope Scope(bool placing) => placing ? RayScope.Off : RayScope.All;

        /// <summary>
        /// One frame. `leftTarget`/`rightTarget`: what each ray rests on now (0: nothing; a ray that is off rests on nothing).
        /// `holding`: a hold is in progress. Returns the target the left hand clicked (0: none); `rightClick` the right's.
        /// </summary>
        public long Update(double nowMs, HandSample left, long leftTarget, HandSample right, long rightTarget, bool holding, out long rightClick)
        {
            // the other hand's pinch, seen in this frame before either changes: it spoils a click in progress
            var leftWas = _left.Pinching || Starts(left);
            var rightWas = _right.Pinching || Starts(right);
            var leftClick = Step(ref _left, nowMs, left, leftTarget, holding, rightWas);
            rightClick = Step(ref _right, nowMs, right, rightTarget, holding, leftWas);
            return leftClick;
        }

        static bool Starts(HandSample h) => h.Usable && h.PinchStrength >= GestureMachine.PinchOn;

        static long Step(ref Hand h, double nowMs, HandSample s, long target, bool holding, bool otherPinches)
        {
            if (!h.Pinching)
            {
                if (!Starts(s)) return 0;
                h.Pinching = true;
                h.Target = target;
                h.Since = nowMs;
                h.Spoiled = holding || otherPinches;
                return 0;
            }
            // still pinching: anything that is not a clean click spoils it
            if (holding || otherPinches || target != h.Target) h.Spoiled = true;
            // a lost hand is not an open hand: the pinch (and its mask) lasts until the hand is seen open again
            if (!s.Usable)
            {
                h.Spoiled = true;
                return 0;
            }
            var released = s.PinchStrength < GestureMachine.PinchOff;
            if (released)
            {
                var click = !h.Spoiled && h.Target != 0 && target == h.Target && nowMs - h.Since <= ClickMs ? h.Target : 0;
                h = default;
                return click;
            }
            return 0;
        }

        /// <summary>The rays went off (placing): no pinch in progress clicks; a masked one stays masked until it is released.</summary>
        public void Reset()
        {
            _left.Spoiled = true;
            _right.Spoiled = true;
        }
    }
}
