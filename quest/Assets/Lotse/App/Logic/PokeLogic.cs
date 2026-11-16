#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// Press detection for one fingertip against one flat button. Pure, time injected.
    /// Coordinates are in the button's plane: x, y from its centre; depth is how far the tip has pushed
    /// through the face (negative = still in front of it, on the user's side).
    ///
    /// A press needs the tip seen clear first (absent, outside the rectangle or more than ArmDistance in
    /// front: a rising edge, so a button that appears under a finger does not fire), then just in front of
    /// the face (armed), then crossing it. A tip that arrives from behind or from the side never presses. After a press the tip has to pull back (or
    /// leave the button) before it can press again, and a cooldown stops double taps.
    /// A key of the desk keyboard (U4b) also needs a short dwell: the tip must stay armed for ArmDwellMs before it
    /// crosses the face, so a finger brushing over the keys types nothing; and it has no margin, so neighbours never
    /// both take one tip.
    /// </summary>
    public sealed class PokeLogic
    {
        /// <summary>Tip within this distance in front of the face arms the button.</summary>
        public const float ArmDistance = 0.04f;
        /// <summary>Deeper than this the tip is behind the button, not pressing it.</summary>
        public const float MaxDepth = 0.04f;
        /// <summary>After a press the tip must come back this far in front of the face.</summary>
        public const float ReleaseDistance = 0.012f;
        public const double CooldownMs = 500;
        /// <summary>Extra margin around the button's rectangle.</summary>
        public const float Margin = 0.005f;

        readonly double _cooldownMs;
        readonly float _margin;
        readonly double _dwellMs;
        double _armedSince = double.NaN;
        bool _armed;
        bool _latched;
        /// <summary>Rising edge: the tip has been seen absent, outside, or far in front since the button appeared.</summary>
        bool _seenClear;
        double _lastPress = double.NegativeInfinity;

        public PokeLogic() : this(CooldownMs, Margin, 0) { }

        /// <param name="cooldownMs">Between two presses.</param>
        /// <param name="margin">Extra margin around the rectangle.</param>
        /// <param name="armDwellMs">How long the tip must be armed in front of the face before a press counts.</param>
        public PokeLogic(double cooldownMs, float margin, double armDwellMs)
        {
            _cooldownMs = cooldownMs;
            _margin = margin;
            _dwellMs = armDwellMs;
        }

        /// <summary>The tip is over the button and close to its face.</summary>
        public bool Hover { get; private set; }

        /// <returns>True on the frame the button is pressed.</returns>
        public bool Update(double nowMs, bool tipPresent, float x, float y, float depth, float halfWidth, float halfHeight)
        {
            var inside = tipPresent
                         && x >= -halfWidth - _margin && x <= halfWidth + _margin
                         && y >= -halfHeight - _margin && y <= halfHeight + _margin;
            if (!inside)
            {
                Hover = false;
                _armed = false;
                _armedSince = double.NaN;
                _latched = false;
                _seenClear = true;
                return false;
            }
            if (depth <= -ArmDistance) _seenClear = true;

            Hover = depth > -ArmDistance && depth < MaxDepth;

            if (_latched)
            {
                if (depth >= -ReleaseDistance) return false;
                _latched = false; // pulled back: released, and it may arm again right away
            }

            if (depth < 0f)
            {
                _armed = _seenClear && depth > -ArmDistance;
                if (!_armed) _armedSince = double.NaN;
                else if (double.IsNaN(_armedSince)) _armedSince = nowMs;
                return false;
            }

            // depth >= 0: at or through the face (after the dwell, for a key)
            var dwelt = _dwellMs <= 0 || (!double.IsNaN(_armedSince) && nowMs - _armedSince >= _dwellMs - 1e-6);
            if (_armed && !dwelt)
            {
                _armed = false; // crossed too fast: it has to come back and arm again
                _armedSince = double.NaN;
                return false;
            }
            if (_armed && depth < MaxDepth && nowMs - _lastPress >= _cooldownMs)
            {
                _armed = false;
                _armedSince = double.NaN;
                _latched = true;
                _lastPress = nowMs;
                return true;
            }
            if (depth >= MaxDepth) _armed = false;
            return false;
        }

        /// <summary>Forget everything, including the rising edge: the tip must be seen clear again.</summary>
        public void Reset()
        {
            Hover = false;
            _armed = false;
            _armedSince = double.NaN;
            _latched = false;
            _seenClear = false;
        }
    }
}
