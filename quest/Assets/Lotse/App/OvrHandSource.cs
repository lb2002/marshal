using System.Collections.Generic;
using Lotse.Core;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// Reads the scene's OVRHand / OVRSkeleton pair per hand. The skeleton must be the OpenXR hand skeleton
    /// (XRHandLeft / XRHandRight, set by SliceSetup): the joint ids below exist only there. Index pinch strength,
    /// tracking and confidence come from OVRHand; palm-forward, the index tip, the pinch point (midway between
    /// thumb and index tip, for the HoldGate) and the palm (for the wrist menu) from the joint positions. The hand
    /// ray is the system's aim pose, OVRHand.PointerPose, which OVRHand keeps in tracking space: it is turned into
    /// world space through the camera rig's tracking space.
    /// The mapping rules live in HandMapping (tested).
    /// </summary>
    public sealed class OvrHandSource : IHandSource
    {
        /// <summary>Tracked this long without usable bones: say so once in the log.</summary>
        const float BonesWarnSeconds = 3f;

        sealed class Side
        {
            public OVRHand Hand;
            public OVRSkeleton Skeleton;
            public readonly Dictionary<OVRSkeleton.BoneId, Transform> Bones = new Dictionary<OVRSkeleton.BoneId, Transform>();
            public int SkeletonChangedCount = -1;
            public float TrackedSince = -1f;
            public bool WarnedType;
            public bool WarnedBones;
            public bool PalmUpLogged;
            public readonly List<Vector3> Joints = new List<Vector3>(32);
        }

        static readonly OVRSkeleton.BoneId[] Needed =
        {
            OVRSkeleton.BoneId.XRHand_Wrist,
            OVRSkeleton.BoneId.XRHand_IndexProximal, OVRSkeleton.BoneId.XRHand_IndexTip,
            OVRSkeleton.BoneId.XRHand_MiddleProximal, OVRSkeleton.BoneId.XRHand_MiddleTip,
            OVRSkeleton.BoneId.XRHand_RingProximal, OVRSkeleton.BoneId.XRHand_RingTip,
            OVRSkeleton.BoneId.XRHand_LittleProximal, OVRSkeleton.BoneId.XRHand_LittleTip,
            OVRSkeleton.BoneId.XRHand_ThumbTip,
        };

        readonly Side _left = new Side();
        readonly Side _right = new Side();
        float _nextSearch;
        Transform _trackingSpace;

        public void Read(Vector3 head, out HandFrame left, out HandFrame right)
        {
            if ((_left.Hand == null || _right.Hand == null || _trackingSpace == null) && Time.unscaledTime >= _nextSearch) Find();
            left = ReadSide(_left, true, head, _trackingSpace);
            right = ReadSide(_right, false, head, _trackingSpace);
        }

        void Find()
        {
            _nextSearch = Time.unscaledTime + 1f;
            if (_trackingSpace == null)
            {
                var rig = Object.FindAnyObjectByType<OVRCameraRig>();
                if (rig != null) _trackingSpace = rig.trackingSpace;
            }
            foreach (var hand in Object.FindObjectsByType<OVRHand>())
            {
                var side = hand.GetHand() == OVRPlugin.Hand.HandLeft ? _left
                    : hand.GetHand() == OVRPlugin.Hand.HandRight ? _right : null;
                if (side == null || side.Hand != null) continue;
                side.Hand = hand;
                side.Skeleton = hand.GetComponent<OVRSkeleton>();
                Debug.Log($"[Lotse] hand found: {hand.GetHand()} skeleton={(side.Skeleton != null ? side.Skeleton.GetSkeletonType().ToString() : "none")}");
            }
        }

        static HandFrame ReadSide(Side s, bool isLeft, Vector3 head, Transform trackingSpace)
        {
            if (s.Hand == null) return HandFrame.Untracked;
            var isTracked = s.Hand.IsTracked;
            var systemGesture = s.Hand.IsSystemGestureInProgress;
            var pinch = s.Hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            var bonesReady = isTracked && BonesReady(s, isLeft);
            WatchBones(s, isLeft, isTracked, bonesReady);

            var palm = false;
            var tip = Vector3.zero;
            var tipDir = Vector3.zero;
            var pinchPoint = Vector3.zero;
            var palmCenter = Vector3.zero;
            var palmNormal = Vector3.zero;
            float index = 0f, middle = 0f, ring = 0f, little = 0f;
            if (bonesReady)
            {
                var j = new HandJoints(
                    P(s, OVRSkeleton.BoneId.XRHand_Wrist),
                    P(s, OVRSkeleton.BoneId.XRHand_IndexProximal), P(s, OVRSkeleton.BoneId.XRHand_IndexTip),
                    P(s, OVRSkeleton.BoneId.XRHand_MiddleProximal), P(s, OVRSkeleton.BoneId.XRHand_MiddleTip),
                    P(s, OVRSkeleton.BoneId.XRHand_RingProximal), P(s, OVRSkeleton.BoneId.XRHand_RingTip),
                    P(s, OVRSkeleton.BoneId.XRHand_LittleProximal));
                palm = PalmPose.IsPalmForward(j, isLeft, Ui.ToVec(head), pinch);
                tip = s.Bones[OVRSkeleton.BoneId.XRHand_IndexTip].position;
                tipDir = (tip - s.Bones[OVRSkeleton.BoneId.XRHand_IndexProximal].position).normalized;
                pinchPoint = (tip + s.Bones[OVRSkeleton.BoneId.XRHand_ThumbTip].position) * 0.5f;
                palmCenter = Ui.ToVector((j.Wrist + j.MiddleProximal) * 0.5f);
                palmNormal = Ui.ToVector(PalmPose.PalmNormal(j, isLeft));
                // the stop gesture's open fingers (U7)
                index = StopGesture.Straightness(j.Wrist, j.IndexProximal, j.IndexTip);
                middle = StopGesture.Straightness(j.Wrist, j.MiddleProximal, j.MiddleTip);
                ring = StopGesture.Straightness(j.Wrist, j.RingProximal, j.RingTip);
                little = StopGesture.Straightness(j.Wrist, j.LittleProximal, P(s, OVRSkeleton.BoneId.XRHand_LittleTip));
            }

            var sample = HandMapping.ToSample(isTracked, systemGesture, s.Hand.IsDataHighConfidence, pinch, palm);
            // the aim pose: in tracking space while OVRHand has no pointer pose root (as in our rig)
            var rayValid = HandMapping.RayUsable(sample, s.Hand.IsPointerPoseValid);
            var pose = s.Hand.PointerPose;
            var rayOrigin = pose.position;
            var rayDirection = pose.forward;
            if (pose.parent == null && trackingSpace != null)
            {
                rayOrigin = trackingSpace.TransformPoint(rayOrigin);
                rayDirection = trackingSpace.TransformDirection(rayDirection);
            }
            // the wrist menu's palm ignores the system gesture: a palm turned up toward the face starts it
            var palmValid = HandMapping.PalmUsable(isTracked, s.Hand.IsDataHighConfidence, bonesReady);
            if (palmValid && !s.PalmUpLogged && palmNormal.y > Layout.MenuPalmUpMin)
            {
                s.PalmUpLogged = true;
                Debug.Log($"[Lotse] palm up first seen ({(isLeft ? "left" : "right")}): systemGesture={systemGesture}, " +
                          $"palm {Vector3.Angle(palmNormal, Vector3.up):0}\u00B0 from up");
            }
            return new HandFrame
            {
                Sample = sample, TipValid = HandMapping.TipUsable(sample, bonesReady), Tip = tip, TipDir = tipDir,
                Joints = bonesReady ? AllJoints(s) : null,
                PinchValid = bonesReady, PinchPoint = pinchPoint,
                PalmValid = palmValid, PalmCenter = palmCenter, PalmNormal = palmNormal,
                RayValid = rayValid, RayOrigin = rayOrigin, RayDirection = rayValid ? rayDirection.normalized : Vector3.zero,
                FingersValid = bonesReady, IndexStraight = index, MiddleStraight = middle, RingStraight = ring, LittleStraight = little,
            };
        }

        /// <summary>Every bone position of the skeleton (desk placement keeps its rays off the hands).</summary>
        static IReadOnlyList<Vector3> AllJoints(Side s)
        {
            s.Joints.Clear();
            foreach (var bone in s.Skeleton.Bones)
                if (bone.Transform != null) s.Joints.Add(bone.Transform.position);
            return s.Joints;
        }

        static Vec3 P(Side s, OVRSkeleton.BoneId id) => Ui.ToVec(s.Bones[id].position);

        static bool IsOpenXr(OVRSkeleton.SkeletonType type) =>
            type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;

        static bool BonesReady(Side s, bool isLeft)
        {
            if (s.Skeleton == null) return false;
            var type = s.Skeleton.GetSkeletonType();
            if (!IsOpenXr(type))
            {
                if (!s.WarnedType)
                {
                    s.WarnedType = true;
                    Debug.LogWarning($"[Lotse] {(isLeft ? "left" : "right")} hand skeleton is {type}, not the OpenXR hand skeleton: no palm stop, no poking with this hand");
                }
                return false;
            }
            if (!s.Skeleton.IsInitialized || !s.Skeleton.IsDataValid) return false;

            // the runtime may rebuild the skeleton: the cached bone transforms are stale then
            if (s.Skeleton.SkeletonChangedCount != s.SkeletonChangedCount)
            {
                s.Bones.Clear();
                s.SkeletonChangedCount = s.Skeleton.SkeletonChangedCount;
            }
            if (s.Bones.Count == Needed.Length) return true;
            foreach (var bone in s.Skeleton.Bones)
                foreach (var id in Needed)
                    if (bone.Id == id && bone.Transform != null) s.Bones[id] = bone.Transform;
            return s.Bones.Count == Needed.Length;
        }

        static void WatchBones(Side s, bool isLeft, bool tracked, bool bonesReady)
        {
            if (!tracked)
            {
                s.TrackedSince = -1f;
                return;
            }
            if (s.TrackedSince < 0f) s.TrackedSince = Time.unscaledTime;
            if (!bonesReady && !s.WarnedBones && Time.unscaledTime - s.TrackedSince > BonesWarnSeconds)
            {
                s.WarnedBones = true;
                Debug.LogWarning($"[Lotse] {(isLeft ? "left" : "right")} hand tracked for {BonesWarnSeconds} s but its joints are not ready " +
                                 $"(skeleton {(s.Skeleton != null ? s.Skeleton.GetSkeletonType().ToString() : "missing")}, bones {s.Bones.Count}/{Needed.Length})");
            }
        }
    }
}
