#nullable enable

namespace Lotse.App
{
    /// <summary>World positions of the hand joints the stop gesture needs (OpenXR hand skeleton).</summary>
    public readonly struct HandJoints
    {
        public readonly Vec3 Wrist;
        public readonly Vec3 IndexProximal;
        public readonly Vec3 IndexTip;
        public readonly Vec3 MiddleProximal;
        public readonly Vec3 MiddleTip;
        public readonly Vec3 RingProximal;
        public readonly Vec3 RingTip;
        public readonly Vec3 LittleProximal;

        public HandJoints(Vec3 wrist, Vec3 indexProximal, Vec3 indexTip, Vec3 middleProximal, Vec3 middleTip,
            Vec3 ringProximal, Vec3 ringTip, Vec3 littleProximal)
        {
            Wrist = wrist;
            IndexProximal = indexProximal;
            IndexTip = indexTip;
            MiddleProximal = middleProximal;
            MiddleTip = middleTip;
            RingProximal = ringProximal;
            RingTip = ringTip;
            LittleProximal = littleProximal;
        }
    }

    /// <summary>
    /// "Palm forward" for the stop gesture: an open hand whose palm faces away from the head.
    /// Computed from joint positions only, so it does not depend on a bone axis convention.
    /// </summary>
    public static class PalmPose
    {
        /// <summary>The palm normal must point away from the head at least this much (cosine).</summary>
        public const float MinFacing = 0.6f;
        /// <summary>Above this index pinch strength the hand is not open.</summary>
        public const float MaxPinch = 0.3f;
        /// <summary>A finger is extended when wrist-to-tip is this many times wrist-to-knuckle.</summary>
        public const float MinExtension = 1.5f;
        /// <summary>Fingers must point up: the wrist-to-middle-tip direction has at least this much +y.</summary>
        public const float MinFingersUp = 0.5f;
        /// <summary>The palm normal's horizontal part must be this long and face away from the head this much.</summary>
        public const float MinHorizontalFacing = 0.5f;

        /// <summary>Unit normal out of the palm. Left and right hands are mirror images, hence the sign.</summary>
        public static Vec3 PalmNormal(in HandJoints j, bool isLeft)
        {
            var n = Vec3.Cross(j.IndexProximal - j.Wrist, j.LittleProximal - j.Wrist).Normalized;
            return isLeft ? n : -n;
        }

        public static bool FingerExtended(Vec3 wrist, Vec3 proximal, Vec3 tip)
        {
            var knuckle = Vec3.Distance(wrist, proximal);
            return knuckle > 1e-4f && Vec3.Distance(wrist, tip) >= MinExtension * knuckle;
        }

        /// <summary>
        /// A raised "stop" hand: open (index, middle and ring extended, no pinch), fingers pointing up, the palm
        /// facing away from the head, also in the horizontal plane. The last two rule out a hand resting flat
        /// on the desk, palm down, whose normal also points away from a head above it.
        /// </summary>
        public static bool IsPalmForward(in HandJoints j, bool isLeft, Vec3 head, float indexPinchStrength)
        {
            if (indexPinchStrength > MaxPinch) return false;
            if (!FingerExtended(j.Wrist, j.IndexProximal, j.IndexTip)) return false;
            if (!FingerExtended(j.Wrist, j.MiddleProximal, j.MiddleTip)) return false;
            if (!FingerExtended(j.Wrist, j.RingProximal, j.RingTip)) return false;
            if ((j.MiddleTip - j.Wrist).Normalized.Y <= MinFingersUp) return false;

            var normal = PalmNormal(j, isLeft);
            var palmCenter = (j.Wrist + j.MiddleProximal) * 0.5f;
            var away = (palmCenter - head).Normalized;
            if (Vec3.Dot(normal, away) <= MinFacing) return false;

            var normalFlat = new Vec3(normal.X, 0f, normal.Z);
            var awayFlat = new Vec3(away.X, 0f, away.Z).Normalized;
            return normalFlat.Length >= MinHorizontalFacing && Vec3.Dot(normalFlat.Normalized, awayFlat) > MinHorizontalFacing;
        }
    }
}
