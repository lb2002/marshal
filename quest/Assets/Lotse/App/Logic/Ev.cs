#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lotse.App
{
    /// <summary>
    /// The machine-readable device log (U9): one line per event, `[Lotse] ev ` and then `key=value` pairs, so the
    /// developer reads from the device log exactly what the hands and the gesture logic did. Always on.
    /// Nothing an agent supplies can reach a line: values are numbers, booleans, enum names, or tokens of our own
    /// (control names, step ids), and every text value goes through Token, which keeps only letters, digits, '_', '-'
    /// and '.', so no space, bracket, quote or line break ever gets in. Command text and reply text are never passed
    /// here at all. Pure; the app sets the sink (Debug.Log), tests read it.
    /// </summary>
    public static class Ev
    {
        public const string Prefix = "[Lotse] ev ";
        public const int TokenMax = 40;
        public const string None = "none";

        /// <summary>Where the lines go (the app: Debug.Log); null: nowhere.</summary>
        public static Action<string>? Sink { get; set; }

        /// <summary>A text value as one safe token: only [A-Za-z0-9_.-], anything else becomes '_'; cut at TokenMax; "none" when empty.</summary>
        public static string Token(string? s)
        {
            if (string.IsNullOrEmpty(s)) return None;
            var sb = new StringBuilder(Math.Min(s!.Length, TokenMax));
            foreach (var c in s)
            {
                if (sb.Length >= TokenMax) break;
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                sb.Append(ok ? c : '_');
            }
            return sb.Length == 0 ? None : sb.ToString();
        }

        static string Value(object? v)
        {
            switch (v)
            {
                case null: return None;
                case bool b: return b ? "true" : "false";
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case float f: return Number(f);
                case double d: return Number(d);
                case Enum e: return Token(e.ToString());
                default: return Token(v.ToString());
            }
        }

        static string Number(double d) =>
            double.IsNaN(d) || double.IsInfinity(d) ? None : d.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>The line for `name` and its pairs: "[Lotse] ev hold kind=two phase=progress p=0.63".</summary>
        public static string Line(string name, params (string key, object? value)[] pairs)
        {
            // a name of two words ("row open", "row close") keeps its space; each word is a token
            var sb = new StringBuilder(Prefix);
            var words = (name ?? "").Split(' ');
            for (var i = 0; i < words.Length; i++) sb.Append(i > 0 ? " " : "").Append(Token(words[i]));
            foreach (var (key, value) in pairs) sb.Append(' ').Append(Token(key)).Append('=').Append(Value(value));
            return sb.ToString();
        }

        /// <summary>Writes the line to the sink.</summary>
        public static void Emit(string name, params (string key, object? value)[] pairs) => Sink?.Invoke(Line(name, pairs));
    }

    /// <summary>At most `perSecond` lines per second (hold progress 4, stop resets 2): the first passes, then one per interval.</summary>
    public sealed class EvRate
    {
        readonly double _intervalMs;
        double _lastMs = double.NegativeInfinity;

        public EvRate(double perSecond) => _intervalMs = 1000.0 / perSecond;

        public bool Allow(double nowMs)
        {
            if (nowMs - _lastMs < _intervalMs - 1e-6) return false;
            _lastMs = nowMs;
            return true;
        }

        public void Reset() => _lastMs = double.NegativeInfinity;
    }

    /// <summary>Why a running hold ended without an approval (the device log's `reason`).</summary>
    public enum HoldCancel { Released, Menu, Question, StopGesture, Placing, Setup, ViewHidden, BlockChanged, Halted, TrackingLost, Gate, Unknown, Moved }

    /// <summary>Why a saved dock was not used (the device log's `dock phase=rejected reason=`).</summary>
    public enum DockReject
    {
        /// <summary>A record of an older build without the seat: the window would stand in the display's plane, too near.</summary>
        NoSeat,
    }

    /// <summary>
    /// Which way a connection went (the device log's `link phase=… reason=`; never an address or a code).
    /// Phases: resume (a stored session is tried), resume-failed, scan (scan mode opens), attempt (a code goes out), live.
    /// </summary>
    public enum LinkWhy
    {
        /// <summary>A stored session.</summary>
        Stored,
        /// <summary>The user pressed for it ("Connect to my PC", "Scan again", "Scan instead").</summary>
        Press,
        /// <summary>The stored session's one attempt failed: scanning, as the press on "Connect to my PC" asked.</summary>
        AfterResume,
        /// <summary>A scanned code.</summary>
        Scanned,
        /// <summary>A typed code.</summary>
        Typed,
        /// <summary>A code (scanned or typed) paired.</summary>
        Code,
        Timeout,
        Closed,
        Refused,
        Unknown,
    }

    /// <summary>What is on screen (the device log's `screen name=`).</summary>
    public enum ScreenName { Loading, Start, Pairing, Setup, Placing, Settings, Menu, Halted, Rail, Overview, Open, End }

    /// <summary>
    /// One hand's pinch for the log: start when its strength reaches the gesture machine's "on", end when it falls
    /// below "off", with how long it lasted. Pure.
    /// </summary>
    public sealed class PinchLog
    {
        readonly string _hand;
        bool _down;
        double _since;

        public PinchLog(string hand) => _hand = hand;

        /// <param name="target">What the hand's ray or fingertip was on when the pinch began (a control's name), or null.</param>
        public void Step(double nowMs, bool usable, float strength, string? target)
        {
            if (!_down && usable && strength >= Lotse.Core.GestureMachine.PinchOn)
            {
                _down = true;
                _since = nowMs;
                Ev.Emit("pinch", ("hand", _hand), ("phase", "start"), ("dur", 0.0), ("onTarget", target));
            }
            else if (_down && (!usable || strength < Lotse.Core.GestureMachine.PinchOff))
            {
                _down = false;
                Ev.Emit("pinch", ("hand", _hand), ("phase", "end"), ("dur", (nowMs - _since) / 1000.0), ("onTarget", target));
            }
        }
    }

    /// <summary>
    /// A hold on the block for the log: start, progress (at most 4 lines a second), cancel with its reason, complete.
    /// It only watches the session's progress; it decides nothing. Pure.
    /// </summary>
    public sealed class HoldLog
    {
        readonly EvRate _rate = new EvRate(4);
        bool _running;
        string _kind = "one";
        float _last;

        public bool Running => _running;

        /// <param name="progress">The session's hold progress (0: no hold).</param>
        /// <param name="twoHand">The block needs both hands.</param>
        /// <param name="decided">A decision was sent for the block this frame (the hold completed).</param>
        /// <param name="reason">Why a hold that ended now ended (asked only then).</param>
        public void Step(double nowMs, float progress, bool twoHand, bool decided, Func<HoldCancel> reason)
        {
            if (!_running)
            {
                if (progress <= 0f) return;
                _running = true;
                _kind = twoHand ? "two" : "one";
                _rate.Reset();
                _rate.Allow(nowMs);
                _last = progress;
                Ev.Emit("hold", ("kind", _kind), ("phase", "start"), ("p", progress));
                return;
            }
            if (decided)
            {
                _running = false;
                Ev.Emit("hold", ("kind", _kind), ("phase", "complete"), ("p", 1.0));
                return;
            }
            if (progress <= 0f)
            {
                _running = false;
                Ev.Emit("hold", ("kind", _kind), ("phase", "cancel"), ("p", _last), ("reason", reason()));
                return;
            }
            _last = progress;
            if (_rate.Allow(nowMs)) Ev.Emit("hold", ("kind", _kind), ("phase", "progress"), ("p", progress));
        }
    }

    /// <summary>The frame rate for the log: every 5 s the mean fps and the worst frame's milliseconds. Pure.</summary>
    public sealed class PerfLog
    {
        public const double EveryMs = 5000;
        double _startMs = double.NaN;
        int _frames;
        double _worstMs;

        public void Frame(double nowMs, double frameMs)
        {
            if (double.IsNaN(_startMs)) _startMs = nowMs;
            _frames++;
            if (frameMs > _worstMs) _worstMs = frameMs;
            var span = nowMs - _startMs;
            if (span < EveryMs) return;
            Ev.Emit("perf", ("fps", (int)Math.Round(_frames * 1000.0 / span)), ("worst_ms", (int)Math.Round(_worstMs)));
            _startMs = nowMs;
            _frames = 0;
            _worstMs = 0;
        }
    }
}
