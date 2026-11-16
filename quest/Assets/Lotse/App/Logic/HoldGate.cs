#nullable enable
using System;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Where the head, the card and the pinching hands are in one frame (world space, metres).</summary>
    public readonly struct HoldPose
    {
        public readonly Vec3 Head;
        public readonly Vec3 HeadForward;
        /// <summary>The centre of the card shown.</summary>
        public readonly Vec3 Card;
        /// <summary>A pinch point has to be above this height (HoldGate.DeskFloor or HoldGate.FallbackFloor).</summary>
        public readonly float FloorY;
        /// <summary>Where each hand pinches (between thumb and index tip); null when the hand's joints are unknown.</summary>
        public readonly Vec3? LeftPinch;
        public readonly Vec3? RightPinch;
        internal readonly bool Ungated;

        public HoldPose(Vec3 head, Vec3 headForward, Vec3 card, float floorY, Vec3? leftPinch, Vec3? rightPinch)
            : this(head, headForward, card, floorY, leftPinch, rightPinch, false)
        {
        }

        HoldPose(Vec3 head, Vec3 headForward, Vec3 card, float floorY, Vec3? leftPinch, Vec3? rightPinch, bool ungated)
        {
            Head = head;
            HeadForward = headForward;
            Card = card;
            FloorY = floorY;
            LeftPinch = leftPinch;
            RightPinch = rightPinch;
            Ungated = ungated;
        }

        /// <summary>Test seam only (internal): every pinch passes, as before the gate existed.</summary>
        internal static readonly HoldPose Open = new HoldPose(default, default, default, 0f, null, null, true);
    }

    /// <summary>What the card tells a user whose pinch the gate holds back.</summary>
    public enum HoldCue
    {
        None,
        /// <summary>A hand pinches outside the gate (too low, off to the side, or the head turned away).</summary>
        RaiseHands,
        /// <summary>A hand came into the gate already pinching: it has to open once first.</summary>
        Release,
    }

    /// <summary>
    /// Keeps pinches the user did not mean away from the gesture machine. A pinch counts only while the head faces
    /// the card and the pinching hand is raised: above the floor and inside a cone around the head's forward axis.
    /// The floor is the desk + 0.10 m when the content stands on a desk (hands typing or resting there stay out),
    /// and the eyes − 0.45 m without one (a relaxed seated pinch counts, the lap does not). Hands on the keyboard,
    /// in the lap or off to the side, or a glance away, give pinch strength 0, so a running hold cancels as a
    /// release. A hand that comes into the gate already pinching, or comes back from a tracking loss still
    /// pinching, is held at 0 until it has opened inside the gate: only a fresh pinch there can start a hold.
    /// Cue says why a pinch is held back, for the card's hint. The palm stop is never gated.
    /// The values are a ruled default, kept in Layout with the other tuning numbers, to be tuned on the headset.
    /// </summary>
    public sealed class HoldGate
    {
        /// <summary>The head faces the card within this angle.</summary>
        public const float FacingDegrees = Layout.GateFacingDegrees;
        /// <summary>The pinch point lies within this angle of the head's forward axis.</summary>
        public const float HandConeDegrees = Layout.GateHandConeDegrees;
        /// <summary>On a desk: the floor is this far above the desk surface.</summary>
        public const float DeskFloorMeters = Layout.GateDeskFloorMeters;
        /// <summary>Without a desk: the floor is this far below the eyes.</summary>
        public const float BelowEyesMeters = Layout.GateBelowEyesMeters;

        bool _leftArmed;
        bool _rightArmed;

        /// <summary>Why a pinch was held back in the last frame (None when nothing was).</summary>
        public HoldCue Cue { get; private set; }

        /// <summary>The floor when the content stands on a desk at `deskY`.</summary>
        public static float DeskFloor(float deskY) => deskY + DeskFloorMeters;

        /// <summary>The floor without a desk (head-relative anchor), from the eye height.</summary>
        public static float FallbackFloor(float eyesY) => eyesY - BelowEyesMeters;

        /// <summary>The head faces the card within FacingDegrees.</summary>
        public static bool Facing(in HoldPose pose) =>
            WithinDegrees(pose.HeadForward, pose.Card - pose.Head, FacingDegrees);

        /// <summary>The pinch point is above the floor and inside the HandConeDegrees cone in front of the head.</summary>
        public static bool HandInside(in HoldPose pose, Vec3? pinch)
        {
            if (pinch == null) return false;
            var p = pinch.Value;
            return p.Y > pose.FloorY && WithinDegrees(pose.HeadForward, p - pose.Head, HandConeDegrees);
        }

        /// <summary>Gates both hands' pinch strength for this frame (palm and tracking pass unchanged); sets Cue.</summary>
        public void Filter(in HoldPose pose, ref HandSample left, ref HandSample right)
        {
            Cue = HoldCue.None;
            if (pose.Ungated) return;
            var facing = Facing(pose);
            left = Gate(left, facing && HandInside(pose, pose.LeftPinch), ref _leftArmed);
            right = Gate(right, facing && HandInside(pose, pose.RightPinch), ref _rightArmed);
        }

        HandSample Gate(HandSample s, bool open, ref bool armed)
        {
            // a hand outside the gate, or not usable (tracking lost, low confidence, the hands busy placing),
            // has to be seen open in the gate again before its pinch counts
            if (!open || !s.Usable) armed = false;
            else if (!armed && s.PinchStrength < GestureMachine.PinchOff) armed = true;
            if (armed) return s;
            if (s.Usable && s.PinchStrength >= GestureMachine.PinchOn)
                Cue = !open ? HoldCue.RaiseHands : Cue == HoldCue.None ? HoldCue.Release : Cue;
            return new HandSample(s.Tracked, s.HighConfidence, 0f, s.PalmForward);
        }

        static bool WithinDegrees(Vec3 forward, Vec3 to, float degrees)
        {
            var f = forward.Normalized;
            var d = to.Normalized;
            if (f.Length < 0.5f || d.Length < 0.5f) return false;
            return Vec3.Dot(f, d) >= (float)Math.Cos(degrees * Math.PI / 180.0);
        }
    }
}
