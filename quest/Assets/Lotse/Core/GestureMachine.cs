#nullable enable

namespace Lotse.Core
{
    public readonly struct HandSample
    {
        public readonly bool Tracked;
        public readonly bool HighConfidence;
        public readonly float PinchStrength;
        public readonly bool PalmForward;

        public HandSample(bool tracked, bool highConfidence, float pinchStrength, bool palmForward)
        {
            Tracked = tracked;
            HighConfidence = highConfidence;
            PinchStrength = pinchStrength;
            PalmForward = palmForward;
        }

        /// <summary>Only a tracked, high-confidence hand counts for any gesture.</summary>
        public bool Usable => Tracked && HighConfidence;
    }

    public enum GestureState { Idle, PinchHolding, TwoHandHolding, Stopped }

    public enum GestureEventKind { None, Confirmed, Stop }

    public readonly struct GestureEvent
    {
        public static readonly GestureEvent None = new GestureEvent(GestureEventKind.None, null, null);
        public static readonly GestureEvent Stop = new GestureEvent(GestureEventKind.Stop, null, null);

        public readonly GestureEventKind Kind;
        /// <summary>"pinch-hold" or "two-hand" for Confirmed.</summary>
        public readonly string? Confirm;
        /// <summary>For Confirmed: the id of the request whose card was open when the hold began.</summary>
        public readonly string? Id;

        GestureEvent(GestureEventKind kind, string? confirm, string? id)
        {
            Kind = kind;
            Confirm = confirm;
            Id = id;
        }

        public static GestureEvent Confirmed(string confirm, string id) => new GestureEvent(GestureEventKind.Confirmed, confirm, id);
    }

    /// <summary>
    /// The decision gestures for the open card. Pure, time injected. A card needs a pinch-hold, or
    /// the two-hand hold when `PermRequest.NeedsTwoHand` (destructive, truncated, unknown or missing risk).
    /// A palm held forward stops everything. A hold that loses its hand (tracking, confidence, release),
    /// a frame gap or a Reset sends nothing.
    ///
    /// Rising edge: after OpenCard every hand has to be seen released (below PinchOff) for a frame before
    /// it may start a hold, so a pinch that was already there when the card appeared never confirms.
    /// </summary>
    public sealed class GestureMachine
    {
        public const double PinchHoldMs = 800;
        public const double TwoHandHoldMs = 1500;
        public const double PalmStopMs = 300;
        /// <summary>A longer gap between frames (or time going backwards) cancels the hold.</summary>
        public const double MaxFrameGapMs = 200;
        /// <summary>A pinch starts at this strength and holds until it drops below the release level.</summary>
        public const float PinchOn = 0.8f;
        public const float PinchOff = 0.6f;

        enum Card { None, PinchHold, TwoHand }

        enum Hand { None, Left, Right }

        Card _card = Card.None;
        string _cardId = "";
        Hand _holdHand = Hand.None;
        double _holdStart;
        bool _leftReleased;
        bool _rightReleased;
        double _lastNow = double.NaN;
        double _palmStart = double.NaN;
        bool _palmFired;

        public GestureState State { get; private set; } = GestureState.Idle;

        /// <summary>0..1 while holding, else 0.</summary>
        public float Progress { get; private set; }

        /// <summary>Id of the open card's request, or null when no card is open.</summary>
        public string? CardId => _card == Card.None ? null : _cardId;

        /// <summary>
        /// A card is open. Cancels any running hold; both hands must be released before a new one. The palm timer
        /// starts again too: a palm raised just before the card appeared needs a full 300 ms on this card.
        /// </summary>
        public void OpenCard(PermRequest request)
        {
            CancelHold();
            _palmStart = double.NaN;
            _card = request.NeedsTwoHand ? Card.TwoHand : Card.PinchHold;
            _cardId = request.Id;
            _leftReleased = false;
            _rightReleased = false;
        }

        /// <summary>
        /// Called on perm_closed: any hold is cancelled, no card, nothing emitted. A stop is not undone:
        /// while the palm is still raised the state stays Stopped (no hold can start, Stop does not fire
        /// again) until the palm has been lowered once; only then does it go back to Idle.
        /// </summary>
        public void Reset()
        {
            CancelHold();
            _card = Card.None;
            _cardId = "";
        }

        public GestureEvent Update(double nowMs, HandSample left, HandSample right)
        {
            if (!double.IsNaN(_lastNow) && (nowMs < _lastNow || nowMs - _lastNow > MaxFrameGapMs))
            {
                // unknown what happened in between: drop the hold, the palm timer and the "seen released" flags
                CancelHold();
                _palmStart = double.NaN;
                _palmFired = false;
                _leftReleased = false;
                _rightReleased = false;
            }
            _lastNow = nowMs;

            if (UpdatePalm(nowMs, left, right)) return GestureEvent.Stop;
            if (State == GestureState.Stopped) return GestureEvent.None;

            if (left.Usable && left.PinchStrength < PinchOff) _leftReleased = true;
            if (right.Usable && right.PinchStrength < PinchOff) _rightReleased = true;

            switch (_card)
            {
                case Card.PinchHold: return UpdatePinchHold(nowMs, left, right);
                case Card.TwoHand: return UpdateTwoHand(nowMs, left, right);
                default:
                    CancelHold();
                    return GestureEvent.None;
            }
        }

        // ---- palm stop ----

        bool UpdatePalm(double nowMs, HandSample left, HandSample right)
        {
            var palm = (left.Usable && left.PalmForward) || (right.Usable && right.PalmForward);
            if (!palm)
            {
                _palmStart = double.NaN;
                _palmFired = false;
                if (State == GestureState.Stopped) State = GestureState.Idle;
                return false;
            }
            if (double.IsNaN(_palmStart)) _palmStart = nowMs;
            if (_palmFired || nowMs - _palmStart < PalmStopMs) return false;

            _palmFired = true;
            CancelHold();
            State = GestureState.Stopped;
            return true;
        }

        // ---- holds ----

        GestureEvent UpdatePinchHold(double nowMs, HandSample left, HandSample right)
        {
            if (State == GestureState.PinchHolding)
            {
                var hand = _holdHand == Hand.Left ? left : right;
                if (!StillPinching(hand)) return CancelHold();
            }
            else
            {
                // right hand first; either hand may confirm a non-destructive card
                if (_rightReleased && StartsPinching(right)) Begin(GestureState.PinchHolding, Hand.Right, nowMs);
                else if (_leftReleased && StartsPinching(left)) Begin(GestureState.PinchHolding, Hand.Left, nowMs);
                else return GestureEvent.None;
            }

            Progress = Clamp01((float)((nowMs - _holdStart) / PinchHoldMs));
            if (nowMs - _holdStart < PinchHoldMs) return GestureEvent.None;
            return Finish("pinch-hold");
        }

        GestureEvent UpdateTwoHand(double nowMs, HandSample left, HandSample right)
        {
            if (State == GestureState.TwoHandHolding)
            {
                if (!StillPinching(left) || !StillPinching(right)) return CancelHold();
            }
            else
            {
                // a single hand never starts it; both must have been released since the card opened
                if (_leftReleased && _rightReleased && StartsPinching(left) && StartsPinching(right))
                    Begin(GestureState.TwoHandHolding, Hand.None, nowMs);
                else return GestureEvent.None;
            }

            Progress = Clamp01((float)((nowMs - _holdStart) / TwoHandHoldMs));
            if (nowMs - _holdStart < TwoHandHoldMs) return GestureEvent.None;
            return Finish("two-hand");
        }

        void Begin(GestureState state, Hand hand, double nowMs)
        {
            State = state;
            _holdHand = hand;
            _holdStart = nowMs;
            Progress = 0f;
        }

        GestureEvent Finish(string confirm)
        {
            var id = _cardId;
            CancelHold();
            _card = Card.None; // one decision per card
            _cardId = "";
            return GestureEvent.Confirmed(confirm, id);
        }

        /// <summary>Back to Idle without an event.</summary>
        GestureEvent CancelHold()
        {
            if (State != GestureState.Stopped) State = GestureState.Idle;
            _holdHand = Hand.None;
            Progress = 0f;
            return GestureEvent.None;
        }

        static bool StartsPinching(HandSample h) => h.Usable && h.PinchStrength >= PinchOn;

        static bool StillPinching(HandSample h) => h.Usable && h.PinchStrength >= PinchOff;

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
