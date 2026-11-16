#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>What a pairing QR code names: the bridge's address, its port and the one-time code. Only ever made by PairLink.</summary>
    public readonly struct PairPayload : IEquatable<PairPayload>
    {
        /// <summary>A dotted private IPv4 literal, in its one canonical spelling (no leading zeros).</summary>
        public readonly string Host;
        public readonly int Port;
        /// <summary>Six ASCII digits. Never logged, never shown.</summary>
        public readonly string Code;

        public PairPayload(string host, int port, string code)
        {
            Host = host;
            Port = port;
            Code = code;
        }

        public bool Equals(PairPayload o) => Host == o.Host && Port == o.Port && Code == o.Code;
        public override bool Equals(object? obj) => obj is PairPayload o && Equals(o);
        public override int GetHashCode() => (Host, Port, Code).GetHashCode();
        /// <summary>Without the code.</summary>
        public override string ToString() => Host + ":" + Port;
    }

    /// <summary>
    /// The pairing QR code's payload (U11), exactly as the bridge prints it:
    /// `marshal://pair?v=1&amp;h=&lt;host&gt;&amp;p=&lt;ws port&gt;&amp;c=&lt;6-digit code&gt;`.
    /// A QR code in the room is untrusted input, so the parser is strict and silent: anything that is not exactly this
    /// is simply not a pairing code (no error, no prompt).
    /// - at most 96 bytes, every one a printable ASCII character (so: valid UTF-8, no whitespace, no control characters);
    /// - the scheme exactly `marshal`, the host exactly `pair`, no path, no fragment, no percent-encoding;
    /// - exactly the keys v, h, p, c, once each (in any order), every value non-empty;
    /// - v is `1`;
    /// - h is a dotted IPv4 literal (four numbers 0..255 without leading zeros) in 10/8, 172.16/12 or 192.168/16;
    /// - p is a number 1024..65535 without leading zeros;
    /// - c is exactly six ASCII digits.
    /// Pure.
    /// </summary>
    public static class PairLink
    {
        public const int MaxLength = 96;
        public const string Prefix = "marshal://pair?";
        public const int MinPort = 1024;
        public const int MaxPort = 65535;
        public const int CodeLength = 6;

        /// <summary>The payload's bytes as the tracker read them.</summary>
        public static bool TryParse(byte[]? payload, out PairPayload result)
        {
            result = default;
            if (payload == null || payload.Length == 0 || payload.Length > MaxLength) return false;
            var chars = new char[payload.Length];
            for (var i = 0; i < payload.Length; i++)
            {
                var b = payload[i];
                if (b < 0x21 || b > 0x7E) return false; // not printable ASCII: whitespace, control, or part of a longer (or broken) UTF-8 sequence
                chars[i] = (char)b;
            }
            return TryParse(new string(chars), out result);
        }

        public static bool TryParse(string? text, out PairPayload result)
        {
            result = default;
            if (text == null || text.Length <= Prefix.Length || text.Length > MaxLength) return false;
            foreach (var ch in text)
                if (ch < 0x21 || ch > 0x7E) return false;
            if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            string? v = null, h = null, p = null, c = null;
            var parts = text.Substring(Prefix.Length).Split('&');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (part.Length < 3 || part[1] != '=') return false;
                var value = part.Substring(2);
                switch (part[0])
                {
                    case 'v' when v == null: v = value; break;
                    case 'h' when h == null: h = value; break;
                    case 'p' when p == null: p = value; break;
                    case 'c' when c == null: c = value; break;
                    default: return false; // another key, or one twice
                }
            }
            if (v != "1" || h == null || p == null || c == null) return false;
            if (!IsPrivateHost(h) || !TryPort(p, out var port) || !IsCode(c)) return false;
            result = new PairPayload(h, port, c);
            return true;
        }

        /// <summary>A dotted IPv4 literal without leading zeros in 10/8, 172.16/12 or 192.168/16 (RFC 1918).</summary>
        public static bool IsPrivateHost(string? host)
        {
            if (string.IsNullOrEmpty(host) || host!.Length > 15) return false;
            var parts = host.Split('.');
            if (parts.Length != 4) return false;
            var n = new int[4];
            for (var i = 0; i < 4; i++)
                if (!TryNumber(parts[i], 3, out n[i]) || n[i] > 255) return false;
            return n[0] == 10 || (n[0] == 172 && n[1] >= 16 && n[1] <= 31) || (n[0] == 192 && n[1] == 168);
        }

        static bool TryPort(string s, out int port) => TryNumber(s, 5, out port) && port >= MinPort && port <= MaxPort;

        static bool IsCode(string s)
        {
            if (s.Length != CodeLength) return false;
            foreach (var ch in s)
                if (ch < '0' || ch > '9') return false;
            return true;
        }

        /// <summary>ASCII digits only, at most `maxDigits`, no leading zero (except "0" itself), no sign.</summary>
        static bool TryNumber(string s, int maxDigits, out int value)
        {
            value = 0;
            if (s.Length == 0 || s.Length > maxDigits) return false;
            if (s.Length > 1 && s[0] == '0') return false;
            foreach (var ch in s)
            {
                if (ch < '0' || ch > '9') return false;
                value = value * 10 + (ch - '0');
            }
            return true;
        }
    }

    public enum QrPhase
    {
        /// <summary>Scan mode is closed: QR codes are ignored completely.</summary>
        Off,
        /// <summary>Scan mode is open and the tracker runs.</summary>
        Scanning,
        /// <summary>A code was seen: the question shows. Nothing connects until "Pair".</summary>
        Asking,
        /// <summary>"Pair" was pressed: the one attempt for this code is under way.</summary>
        Attempting,
    }

    /// <summary>
    /// Pairing by looking at the QR code (U11): what a sighting may do. The rules, all of them here:
    /// 1. A scanned code never connects by itself: a sighting only leads to the question (Asking); only Confirm, which
    ///    the "Pair" button calls, hands out what to pair with.
    /// 2. One attempt per scanned code: after a failed attempt scan mode is closed and that exact payload is ignored
    ///    until the user starts scanning again (or, in an open scan, a different one is seen). Nothing here ever retries.
    /// 3. Outside scan mode (Off, and while an attempt runs) every sighting is ignored.
    /// 4. While connected, scan mode does not start.
    /// 5. What is asked about is what is used: while the question shows, further sightings change nothing, and Confirm
    ///    hands out exactly the payload the question showed.
    /// A payload for another port than the app's own is not for this app: ignored. Pure.
    /// </summary>
    public sealed class QrPairing
    {
        readonly int _port;
        PairPayload? _blocked;
        PairPayload _attempt;

        /// <param name="port">The one port the app connects to (LiveSource.Port).</param>
        public QrPairing(int port)
        {
            _port = port;
        }

        public QrPhase Phase { get; private set; } = QrPhase.Off;
        /// <summary>What the question is about, while it shows.</summary>
        public PairPayload? Pending { get; private set; }
        /// <summary>Scan mode is open (the scan screen or its question shows).</summary>
        public bool Open => Phase == QrPhase.Scanning || Phase == QrPhase.Asking;
        /// <summary>The last attempt with a scanned code failed, and that code is still the one being ignored.</summary>
        public bool Refused => _blocked.HasValue;

        /// <summary>The user pressed "Scan code": scan mode opens, unless connected. An earlier refusal is forgotten.</summary>
        public bool Start(bool connected)
        {
            if (connected || Phase != QrPhase.Off) return false;
            _blocked = null;
            Pending = null;
            Phase = QrPhase.Scanning;
            return true;
        }

        /// <summary>The tracker read a QR code's payload. True: it is a pairing code to ask about (the question shows now).</summary>
        public bool Seen(byte[]? payload)
        {
            if (Phase != QrPhase.Scanning) return false;
            if (!PairLink.TryParse(payload, out var p) || p.Port != _port) return false;
            if (_blocked.HasValue && _blocked.Value.Equals(p)) return false; // its one attempt is used up
            _blocked = null; // a different code: the refused one is history
            Pending = p;
            Phase = QrPhase.Asking;
            return true;
        }

        /// <summary>"Pair": what to pair with, exactly what the question showed. Null when no question shows.</summary>
        public PairPayload? Confirm()
        {
            if (Phase != QrPhase.Asking || !Pending.HasValue) return null;
            _attempt = Pending.Value;
            Pending = null;
            Phase = QrPhase.Attempting;
            return _attempt;
        }

        /// <summary>"Cancel", on the scan screen or on the question: scan mode closes, nothing else happens.</summary>
        public void Cancel()
        {
            if (!Open) return;
            Pending = null;
            Phase = QrPhase.Off;
        }

        /// <summary>The screen closed or the app paused: scan mode closes. An attempt under way keeps its outcome.</summary>
        public void Stop() => Cancel();

        /// <summary>
        /// The attempt failed: scan mode stays closed (it opens only when the user presses scan again), and its code is
        /// remembered as failed until then.
        /// </summary>
        public void AttemptFailed()
        {
            if (Phase != QrPhase.Attempting) return;
            _blocked = _attempt;
            Phase = QrPhase.Off;
        }

        /// <summary>Paired.</summary>
        public void AttemptSucceeded()
        {
            if (Phase != QrPhase.Attempting) return;
            _blocked = null;
            Phase = QrPhase.Off;
        }
    }

    /// <summary>
    /// When the question's "Pair" counts, and which pinch may press it (U13; it replaces U11 fix round 2's rule, which
    /// real hand tracking never let stay armed). The rule, exactly:
    /// - "Pair" becomes pressable 0.6 s after the question appeared, by time alone. It then stays pressable until the
    ///   question closes: tracking loss, a hand reappearing or pinches in the air never switch it off again, and
    ///   pinches before that moment do not delay it.
    /// - What may press it: only a pinch whose beginning was observed after that moment, with the hand usable and
    ///   open in the frame before the pinch began. A pinch that began before the 0.6 s (BegunBefore), and a hand that
    ///   becomes usable while already pinching (Unobserved: its beginning was not seen), are ignored until that pinch
    ///   is released.
    /// - A poke needs no pinch: it presses once "Pair" is pressable (the button's own rule makes a fingertip that is
    ///   already through the face come out first).
    /// Pure.
    /// </summary>
    public sealed class AskArming
    {
        public const float Seconds = 0.6f;
        public const string BegunBefore = "BegunBefore";
        public const string Unobserved = "Unobserved";

        struct Hand
        {
            public bool Usable;
            public bool Pinching;
            /// <summary>The pinch under way may press "Pair".</summary>
            public bool Good;
            /// <summary>A pinch ended in this step, and whether it was a good one (a ray click fires on the release).</summary>
            public bool Ended;
            public bool EndedGood;
        }

        float _shown;
        bool _started;
        Hand _left;
        Hand _right;

        /// <summary>"Pair" is pressable (0.6 s have passed since the question appeared). It never goes back.</summary>
        public bool Armed { get; private set; }
        /// <summary>It became pressable in the last step.</summary>
        public bool JustArmed { get; private set; }
        /// <summary>A pinch began in the last step that cannot press "Pair": why (BegunBefore, Unobserved), else null.</summary>
        public string? Ignored { get; private set; }

        /// <summary>A new question: nothing counts yet.</summary>
        public void Reset()
        {
            _shown = 0f;
            _started = false;
            Armed = false;
            JustArmed = false;
            Ignored = null;
            _left = default;
            _right = default;
        }

        /// <summary>
        /// A hand counts as pinching from this strength on (U14, review of U13): the ray click's own release threshold.
        /// With the higher start threshold a release that passed through the band between the two was seen as ended
        /// one frame before the click fired, and the click then met "no pinch at all".
        /// </summary>
        public const float PinchingFrom = Lotse.Core.GestureMachine.PinchOff;

        /// <summary>The same from the hands' pinch strengths: a hand pinches while its strength is at least PinchingFrom.</summary>
        public bool Step(float dt, bool leftUsable, float leftPinch, bool rightUsable, float rightPinch)
            => Step(dt, leftUsable, leftPinch >= PinchingFrom, rightUsable, rightPinch >= PinchingFrom);

        /// <summary>Once a frame while the question shows, before buttons and rays are stepped. Returns whether "Pair" is pressable now.</summary>
        public bool Step(float dt, bool leftUsable, bool leftPinching, bool rightUsable, bool rightPinching)
        {
            var armedBefore = Armed;
            Ignored = null;
            Track(ref _left, leftUsable, leftPinching, armedBefore);
            Track(ref _right, rightUsable, rightPinching, armedBefore);
            _started = true;
            _shown += dt;
            Armed = Armed || _shown >= Seconds - 1e-4f;
            JustArmed = Armed && !armedBefore;
            return Armed;
        }

        void Track(ref Hand h, bool usable, bool pinching, bool armed)
        {
            h.Ended = false;
            var pinch = usable && pinching;
            var was = h.Usable && h.Pinching;
            if (pinch && !was)
            {
                // a pinch begins: it counts only when its beginning was seen (the hand usable and open the frame
                // before) and "Pair" was pressable by then
                if (!_started || !h.Usable) { h.Good = false; Ignored = _started ? Unobserved : BegunBefore; }
                else if (!armed) { h.Good = false; Ignored = BegunBefore; }
                else h.Good = true;
            }
            else if (!pinch && was)
            {
                h.Ended = true;
                h.EndedGood = h.Good;
                h.Good = false;
            }
            h.Usable = usable;
            h.Pinching = pinching;
        }

        /// <summary>
        /// A press on "Pair" counts now: it is pressable, and when a pinch is under way or has just ended (a ray click),
        /// at least one such pinch is a good one. With no pinch at all (a poke) it counts.
        /// </summary>
        public bool PressAllowed
        {
            get
            {
                if (!Armed) return false;
                var lp = _left.Usable && _left.Pinching;
                var rp = _right.Usable && _right.Pinching;
                var any = lp || rp || _left.Ended || _right.Ended;
                if (!any) return true;
                return (lp && _left.Good) || (rp && _right.Good) || (_left.Ended && _left.EndedGood) || (_right.Ended && _right.EndedGood);
            }
        }
    }
}
