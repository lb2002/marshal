#nullable enable
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// What the headset's hand data means for the gestures. Pure, so the rules are tested without a device:
    /// during the system gesture a hand counts as untracked (the gesture stays the system's), an untracked
    /// hand has no pinch, and palm-forward needs a tracked, confident hand. Only the wrist menu's palm ignores the
    /// system gesture: a palm turned up toward the face starts it, and the menu must still see that palm (the system
    /// gesture itself is never mapped to any action).
    /// </summary>
    public static class HandMapping
    {
        public static HandSample ToSample(bool isTracked, bool systemGesture, bool highConfidence, float indexPinch, bool palmForward)
        {
            var tracked = isTracked && !systemGesture;
            var high = tracked && highConfidence;
            return new HandSample(tracked, high, tracked ? indexPinch : 0f, high && palmForward);
        }

        /// <summary>The index tip may poke only from a tracked, confident hand whose joints are ready.</summary>
        public static bool TipUsable(HandSample sample, bool bonesReady) => sample.Usable && bonesReady;

        /// <summary>The palm for the wrist menu: tracked, confident, joints ready; the system gesture flag is ignored.</summary>
        public static bool PalmUsable(bool isTracked, bool highConfidence, bool bonesReady) => isTracked && highConfidence && bonesReady;

        /// <summary>The hand ray only from a tracked, confident hand whose system aim pose is valid.</summary>
        public static bool RayUsable(HandSample sample, bool pointerPoseValid) => sample.Usable && pointerPoseValid;
    }
}
