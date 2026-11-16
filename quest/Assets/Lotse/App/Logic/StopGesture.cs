#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>One hand as the stop gesture reads it, in one frame (world space).</summary>
    public readonly struct StopHand
    {
        /// <summary>Tracked with high confidence, and its joints known (palm and fingers below are valid).</summary>
        public readonly bool Valid;
        public readonly float Pinch;
        public readonly Vec3 Palm;
        /// <summary>Unit normal out of the palm.</summary>
        public readonly Vec3 Normal;
        /// <summary>Per finger (index, middle, ring, little): how straight it is, 1 straight .. below 0 curled back (StopGesture.Straightness).</summary>
        public readonly float Index, Middle, Ring, Little;

        public StopHand(float pinch, Vec3 palm, Vec3 normal, float index, float middle, float ring, float little)
        {
            Valid = true;
            Pinch = pinch;
            Palm = palm;
            Normal = normal;
            Index = index;
            Middle = middle;
            Ring = ring;
            Little = little;
        }

        public static readonly StopHand None = default;
    }

    public enum StopEventKind { None, Started, Reset, Triggered }

    /// <summary>The first condition of the stop pose that fails (the device log's `fail=`).</summary>
    public enum StopFail { None, TrackingLost, Busy, Pinch, PalmNotAway, FingersNotOpen, HandsTooLow, HandsTooFar, Moving }

    /// <summary>What keeps the hands out of the stop pose now, with the measured value that failed (U9, for the log only).</summary>
    public readonly struct StopDiagnosis
    {
        public readonly StopFail Fail;
        /// <summary>The measured value's name with the hand ("palmDotL", "fingerMinR", "pinchL", "belowHeadR", "reachL"), or null.</summary>
        public readonly string? Measure;
        public readonly float Value;
        /// <summary>Both hands are raised, open-ish and roughly facing away: the user is trying the gesture.</summary>
        public readonly bool Attempt;

        public StopDiagnosis(StopFail fail, string? measure, float value, bool attempt)
        {
            Fail = fail;
            Measure = measure;
            Value = value;
            Attempt = attempt;
        }
    }

    public readonly struct StopEvent
    {
        public readonly StopEventKind Kind;
        /// <summary>Why it reset (for the device log), else null.</summary>
        public readonly string? Reason;

        public StopEvent(StopEventKind kind, string? reason = null)
        {
            Kind = kind;
            Reason = reason;
        }

        public static readonly StopEvent Nothing = default;
    }

    /// <summary>
    /// The stop gesture (U7): both hands open, palms facing away from the user, held still for 1.0 s. It halts every
    /// agent (it can only make things stricter). It can never be an approval: it needs both hands open with no pinch,
    /// and the app cancels a running hold the moment it starts to count. Pure, time injected. Every threshold is a
    /// named constant here (none of them has been tried on the device yet).
    ///
    /// Each frame, both hands must be: tracked with high confidence; not pinching; the palm normal pointing away from the
    /// head (its dot product with the head's forward vector above PalmAwayMin); index, middle, ring and little finger
    /// each straighter than FingerStraightMin; above the head's height minus BelowHeadMax and within ReachMax of the
    /// head; moving slower than SpeedMax. All of it continuously for HoldMs; any violation resets the timer. Not while
    /// `blocked` (a keyboard open, a poke or ray click in progress, a drag, the pairing, placement or display setup), nor
    /// for QuietAfterLossMs after either hand's tracking (or the input focus) was lost. After a trigger both hands
    /// have to leave the pose before it can count again.
    /// </summary>
    public sealed class StopGesture
    {
        public const double HoldMs = 1000;
        public const float PalmAwayMin = 0.7f;
        public const float FingerStraightMin = 0.8f;
        /// <summary>A pinch strength at or above this is a pinch (well below GestureMachine.PinchOff, 0.6).</summary>
        public const float PinchMax = 0.3f;
        public const float BelowHeadMax = 0.45f;
        public const float ReachMax = 0.8f;
        public const float SpeedMax = 0.3f;
        public const double QuietAfterLossMs = 500;

        bool _counting;
        bool _armed = true;
        double _since;
        double _lossAt = double.NegativeInfinity;
        double _lastMs = double.NaN;
        bool _havePrev;
        Vec3 _prevLeft, _prevRight;

        /// <summary>It counts: both hands are in the pose (a hold on a block must be cancelled meanwhile).</summary>
        public bool Counting => _counting;
        /// <summary>0..1 while counting (the amber bar), else 0.</summary>
        public float Progress { get; private set; }

        /// <summary>How straight a finger is: the cosine between wrist to knuckle and knuckle to tip (1 straight).</summary>
        public static float Straightness(Vec3 wrist, Vec3 knuckle, Vec3 tip)
        {
            var a = (knuckle - wrist).Normalized;
            var b = (tip - knuckle).Normalized;
            if (a.Length < 0.5f || b.Length < 0.5f) return -1f;
            return Vec3.Dot(a, b);
        }

        /// <summary>One frame. `blocked`: one of the excluded situations (see the class).</summary>
        public StopEvent Step(double nowMs, bool blocked, in StopHand left, in StopHand right, Vec3 head, Vec3 headForward)
        {
            var dt = double.IsNaN(_lastMs) ? 0.0 : (nowMs - _lastMs) / 1000.0;
            _lastMs = nowMs;
            if (!left.Valid || !right.Valid)
            {
                _lossAt = nowMs;
                _havePrev = false;
                return Fail("tracking lost");
            }
            var speedOk = _havePrev && dt > 0 && Speed(left.Palm, _prevLeft, dt) < SpeedMax && Speed(right.Palm, _prevRight, dt) < SpeedMax;
            _prevLeft = left.Palm;
            _prevRight = right.Palm;
            _havePrev = true;
            if (blocked) return Fail("busy");
            if (nowMs - _lossAt < QuietAfterLossMs) return Fail("tracking lost just now");
            var why = Violation(left, head, headForward) ?? Violation(right, head, headForward) ?? (speedOk ? null : "moving");
            if (why != null) return Fail(why);

            if (!_armed) return StopEvent.Nothing; // after a trigger: the hands have to leave the pose first
            if (!_counting)
            {
                _counting = true;
                _since = nowMs;
                Progress = 0f;
                return new StopEvent(StopEventKind.Started);
            }
            var held = nowMs - _since;
            Progress = (float)Math.Min(1.0, held / HoldMs);
            if (held < HoldMs - 1e-6) return StopEvent.Nothing;
            _counting = false;
            _armed = false;
            Progress = 0f;
            return new StopEvent(StopEventKind.Triggered);
        }

        /// <summary>Starts over (a new session): nothing counts, and a pose already held needs to be left first.</summary>
        public void Reset()
        {
            _counting = false;
            _armed = false;
            Progress = 0f;
            _havePrev = false;
            _lastMs = double.NaN;
        }

        static float Speed(Vec3 now, Vec3 prev, double dt) => (float)((now - prev).Length / dt);

        /// <summary>
        /// For the device log only (it changes nothing): the first failing condition of the pose in the order Step checks
        /// them (left hand, then right), with its measured value. "Moving" and "busy" are Step's own and not seen here.
        /// </summary>
        public static StopDiagnosis Diagnose(in StopHand left, in StopHand right, Vec3 head, Vec3 headForward)
        {
            if (!left.Valid || !right.Valid) return new StopDiagnosis(StopFail.TrackingLost, null, 0f, false);
            var forward = headForward.Normalized;
            var attempt = Trying(left, head, forward) && Trying(right, head, forward);
            var l = One(left, "L", head, forward);
            if (l.Fail != StopFail.None) return new StopDiagnosis(l.Fail, l.Measure, l.Value, attempt);
            var r = One(right, "R", head, forward);
            return new StopDiagnosis(r.Fail, r.Measure, r.Value, attempt);
        }

        static bool Trying(in StopHand h, Vec3 head, Vec3 forward) =>
            h.Pinch < PinchMax && h.Palm.Y > head.Y - BelowHeadMax && forward.Length >= 0.5f && Vec3.Dot(h.Normal.Normalized, forward) > 0.3f;

        static StopDiagnosis One(in StopHand h, string side, Vec3 head, Vec3 forward)
        {
            if (h.Pinch >= PinchMax) return new StopDiagnosis(StopFail.Pinch, "pinch" + side, h.Pinch, false);
            var dot = forward.Length < 0.5f ? 0f : Vec3.Dot(h.Normal.Normalized, forward);
            if (dot <= PalmAwayMin) return new StopDiagnosis(StopFail.PalmNotAway, "palmDot" + side, dot, false);
            var min = Math.Min(Math.Min(h.Index, h.Middle), Math.Min(h.Ring, h.Little));
            if (min <= FingerStraightMin) return new StopDiagnosis(StopFail.FingersNotOpen, "fingerMin" + side, min, false);
            if (h.Palm.Y <= head.Y - BelowHeadMax) return new StopDiagnosis(StopFail.HandsTooLow, "belowHead" + side, head.Y - h.Palm.Y, false);
            var reach = (h.Palm - head).Length;
            if (reach > ReachMax) return new StopDiagnosis(StopFail.HandsTooFar, "reach" + side, reach, false);
            return new StopDiagnosis(StopFail.None, null, 0f, false);
        }

        /// <summary>A reset's reason (Step's word) as the log's enum.</summary>
        public static StopFail FailOf(string? reason)
        {
            switch (reason)
            {
                case "tracking lost":
                case "tracking lost just now": return StopFail.TrackingLost;
                case "busy": return StopFail.Busy;
                case "pinch": return StopFail.Pinch;
                case "palm not away": return StopFail.PalmNotAway;
                case "fingers not open": return StopFail.FingersNotOpen;
                case "hands too low": return StopFail.HandsTooLow;
                case "hands too far": return StopFail.HandsTooFar;
                case "moving": return StopFail.Moving;
                default: return StopFail.None;
            }
        }

        /// <summary>What keeps one hand out of the pose, or null when it is in it.</summary>
        static string? Violation(in StopHand h, Vec3 head, Vec3 headForward)
        {
            if (h.Pinch >= PinchMax) return "pinch";
            var forward = headForward.Normalized;
            if (forward.Length < 0.5f || Vec3.Dot(h.Normal.Normalized, forward) <= PalmAwayMin) return "palm not away";
            if (h.Index <= FingerStraightMin || h.Middle <= FingerStraightMin || h.Ring <= FingerStraightMin || h.Little <= FingerStraightMin)
                return "fingers not open";
            if (h.Palm.Y <= head.Y - BelowHeadMax) return "hands too low";
            if ((h.Palm - head).Length > ReachMax) return "hands too far";
            return null;
        }

        StopEvent Fail(string reason)
        {
            _armed = true;
            Progress = 0f;
            if (!_counting) return StopEvent.Nothing;
            _counting = false;
            return new StopEvent(StopEventKind.Reset, reason);
        }
    }
}
