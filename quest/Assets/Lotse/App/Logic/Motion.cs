#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// The interface's motion, as numbers: panels scale and fade in over 180 ms with an ease-out, a card rises
    /// 3 cm while it appears, a button under a finger or a ray grows to 1.04. Nothing moves while a hold is in
    /// progress (`still`): everything stands at its end. Pure.
    /// </summary>
    public static class Motion
    {
        public const double AppearMs = 180;
        /// <summary>A panel starts at this scale and grows to 1.</summary>
        public const float AppearFromScale = 0.92f;
        /// <summary>A card starts this far below its place and rises into it.</summary>
        public const float CardRiseMeters = 0.03f;
        public const float HoverScale = 1.04f;
        public const float PressScale = 0.97f;

        /// <summary>Cubic ease-out of t in 0..1 (clamped).</summary>
        public static float EaseOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            var u = 1f - t;
            return 1f - u * u * u;
        }

        /// <summary>0..1 eased progress of an appearance `elapsedMs` in; 1 when `still`.</summary>
        public static float Appear(double elapsedMs, bool still) => still ? 1f : EaseOut((float)(elapsedMs / AppearMs));

        public static float Scale(float progress) => AppearFromScale + (1f - AppearFromScale) * progress;

        public static float Alpha(float progress) => progress;

        /// <summary>The card's offset (negative: below its place) at this progress.</summary>
        public static float Rise(float progress) => -CardRiseMeters * (1f - progress);
    }
}
