using System.Collections.Generic;
using Lotse.Core;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// One hand in one frame: what the gesture machine needs, plus the index tip for poking and desk placement,
    /// the palm for the wrist menu and the system aim ray for pointing.
    /// </summary>
    public struct HandFrame
    {
        public HandSample Sample;
        public bool TipValid;
        public Vector3 Tip;
        /// <summary>Where the index finger points (proximal joint to tip), unit length; zero when unknown.</summary>
        public Vector3 TipDir;
        /// <summary>Every joint of the hand while it is tracked (null otherwise); valid for this frame only.</summary>
        public IReadOnlyList<Vector3> Joints;
        /// <summary>Whether PinchPoint is known (the hand's joints are tracked).</summary>
        public bool PinchValid;
        /// <summary>Where thumb and index meet (midway between their tips): the HoldGate checks it is raised in front.</summary>
        public Vector3 PinchPoint;
        /// <summary>Whether PalmCenter and PalmNormal are known (the hand's joints are tracked).</summary>
        public bool PalmValid;
        /// <summary>The middle of the palm (midway between the wrist and the middle knuckle): the wrist menu floats above it.</summary>
        public Vector3 PalmCenter;
        /// <summary>Unit normal out of the palm (up when the palm is turned up).</summary>
        public Vector3 PalmNormal;
        /// <summary>Whether the system's aim pose is valid (OVRHand.IsPointerPoseValid on a usable hand).</summary>
        public bool RayValid;
        /// <summary>Where the hand ray starts and where it points (unit length), from OVRHand.PointerPose, world space.</summary>
        public Vector3 RayOrigin;
        public Vector3 RayDirection;
        /// <summary>Whether the finger straightness below is known (the hand's joints are tracked; false in old test frames).</summary>
        public bool FingersValid;
        /// <summary>Per finger, how straight it is (StopGesture.Straightness: 1 straight, lower curled): for the stop gesture.</summary>
        public float IndexStraight, MiddleStraight, RingStraight, LittleStraight;

        /// <summary>The hand as the stop gesture reads it: invalid unless tracked with high confidence, palm and fingers known.</summary>
        public StopHand Stop => Sample.Tracked && Sample.HighConfidence && PalmValid && FingersValid
            ? new StopHand(Sample.PinchStrength, Ui.ToVec(PalmCenter), Ui.ToVec(PalmNormal), IndexStraight, MiddleStraight, RingStraight, LittleStraight)
            : StopHand.None;

        public static HandFrame Untracked => new HandFrame { Sample = new HandSample(false, false, 0f, false) };
    }

    /// <summary>Where the hands come from: the Quest's hand tracking, or a script in tests.</summary>
    public interface IHandSource
    {
        void Read(Vector3 head, out HandFrame left, out HandFrame right);
    }
}
