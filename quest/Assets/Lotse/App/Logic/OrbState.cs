#nullable enable
using System;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>What an agent's orb shows (U5a).</summary>
    public enum OrbMode { Working, Thinking, Command, NeedsYou, Done, Error }

    /// <summary>The two stops of an orb's conic gradient.</summary>
    public readonly struct OrbColors
    {
        public readonly Rgb A;
        public readonly Rgb B;

        public OrbColors(Rgb a, Rgb b)
        {
            A = a;
            B = b;
        }
    }

    /// <summary>How an orb stands in one frame.</summary>
    public readonly struct OrbPose
    {
        /// <summary>How far its gradient has turned, clockwise, in degrees.</summary>
        public readonly float TurnDegrees;
        /// <summary>The disc's scale (breathing between 1.00 and 1.05).</summary>
        public readonly float Scale;
        /// <summary>The "needs you" ring: shown, its scale (1: the disc's edge) and its alpha.</summary>
        public readonly bool Pulse;
        public readonly float PulseScale;
        public readonly float PulseAlpha;
        /// <summary>The dashed ring of a running command, and how far it has turned (degrees, clockwise).</summary>
        public readonly bool DashedRing;
        public readonly float RingDegrees;

        public OrbPose(float turn, float scale, bool pulse, float pulseScale, float pulseAlpha, bool dashed, float ring)
        {
            TurnDegrees = turn;
            Scale = scale;
            Pulse = pulse;
            PulseScale = pulseScale;
            PulseAlpha = pulseAlpha;
            DashedRing = dashed;
            RingDegrees = ring;
        }
    }

    /// <summary>
    /// The orb of an agent (U5a; it replaces the worker figures): which state it shows, from the agent's status and its
    /// last action or feed kind, its two colours (a palette entry by the agent's index, stable for the session; red
    /// while it needs the user; grey after an error), and how it moves. Working: the gradient turns once in 7 s and the
    /// disc breathes between 1.00 and 1.05 over 3.4 s. Thinking: it turns once in 2.6 s. Command: as working, with a
    /// dashed ring turning once in 4 s. Needs you: red, a ring expanding and fading every 1.6 s. Done: still, a green
    /// check badge. Error: still and grey, a red badge. With `still` (Theme.Still, the "Ruhig" setting) nothing moves:
    /// the colours, the rings (standing) and the badges keep the states apart. Pure.
    /// </summary>
    public static class OrbState
    {
        public const double WorkingTurnSeconds = 7;
        public const double ThinkingTurnSeconds = 7;
        public const double BreathSeconds = 3.4;
        public const double RingTurnSeconds = 4;
        public const double PulseSeconds = 1.6;
        public const float BreathScale = 1.05f;
        /// <summary>The pulse ring grows from the disc's edge to this scale while it fades out.</summary>
        public const float PulseMaxScale = 1.3f;
        public const float PulseStartAlpha = 1f;
        /// <summary>Still: the pulse ring stands at this scale and alpha.</summary>
        public const float PulseStillScale = 1.2f;
        public const float PulseStillAlpha = 1f;

        /// <summary>The palette, two stops each: blue, violet, teal, amber, pink, slate.</summary>
        public static readonly OrbColors[] Palette =
        {
            new OrbColors(Look.Hex(0x7FD3FF), Look.Hex(0x5B6CFF)),
            new OrbColors(Look.Hex(0xC6A7FF), Look.Hex(0x6E4BFF)),
            new OrbColors(Look.Hex(0x7FF0D0), Look.Hex(0x1FA37A)),
            new OrbColors(Look.Hex(0xFFD27F), Look.Hex(0xF08A2E)),
            new OrbColors(Look.Hex(0xFF9AD5), Look.Hex(0xC74BD8)),
            new OrbColors(Look.Hex(0xB8C4CF), Look.Hex(0x5F7183)),
        };

        /// <summary>"Needs you": the red pair.</summary>
        public static readonly OrbColors Red = new OrbColors(Look.Hex(0xFF9A6B), Look.Hex(0xFF4E6A));
        /// <summary>After an error: grey.</summary>
        public static readonly OrbColors Grey = new OrbColors(Look.Hex(0x8A949C), Look.Hex(0x4A525A));
        /// <summary>The done badge (green) and the error badge (red).</summary>
        public static readonly Rgb DoneBadge = Look.Hex(0x46C98B);
        public static readonly Rgb ErrorBadge = Look.Hex(0xFF5A4E);

        /// <param name="needsYou">It has an open request.</param>
        /// <param name="lastFeedKind">The kind of its newest feed entry when its feed is known, else null.</param>
        public static OrbMode For(AgentState agent, bool needsYou, string? lastFeedKind)
        {
            if (needsYou || agent.Status == "blocked" || agent.Status == "waiting") return OrbMode.NeedsYou;
            if (agent.Status == "failed") return OrbMode.Error;
            if (agent.Status == "done") return OrbMode.Done;
            var action = agent.LastAction ?? "";
            if (agent.Status == "testing" || StatusLine.Starts(action, "Bash:") || StatusLine.Starts(action, "Führt ") ||
                StatusLine.Starts(action, "Testet") || StatusLine.Starts(action, "Runs "))
                return OrbMode.Command;
            if (lastFeedKind == "thinking") return OrbMode.Thinking;
            if (agent.Status == "working" && (action.Length == 0 || action == "Arbeitet" || action == "Sitzung gestartet" ||
                                              StatusLine.Starts(action, "Denkt") || action == "Working" || action == "Session started" ||
                                              StatusLine.Starts(action, "Thinking")))
                return OrbMode.Thinking;
            return OrbMode.Working;
        }

        /// <summary>The colours: red while it needs the user, grey after an error, else its palette entry by index.</summary>
        public static OrbColors ColorsFor(OrbMode mode, int index)
        {
            if (mode == OrbMode.NeedsYou) return Red;
            if (mode == OrbMode.Error) return Grey;
            var n = Palette.Length;
            return Palette[((index % n) + n) % n];
        }

        /// <summary>
        /// The square slot an orb of `diameter` needs: its widest ring (the pulse at its largest) with 8 mm clear on every
        /// side (U5a fix 1). Text starts after it.
        /// </summary>
        public static float Slot(float diameter) => diameter * PulseMaxScale + 2f * 0.008f;

        /// <summary>
        /// U9: the breath is a soft pulse of the orb's own brightness, not of its size: 0 at rest .. 1 at the top of a
        /// breath, from a pose's Scale (1 .. BreathScale).
        /// </summary>
        public static float Glow(in OrbPose pose) => Math.Max(0f, Math.Min(1f, (pose.Scale - 1f) / (BreathScale - 1f)));
        /// <summary>How far the brightest moment of a breath lifts the colours toward white.</summary>
        public const float GlowLift = 0.14f;

        /// <summary>Whether the orb moves at all in this state (no Update needed otherwise).</summary>
        public static bool Moves(OrbMode mode, bool still) =>
            !still && (mode == OrbMode.Working || mode == OrbMode.Thinking || mode == OrbMode.Command);

        /// <summary>The pose `seconds` into the state (any clock); with `still` nothing moves.</summary>
        public static OrbPose At(OrbMode mode, double seconds, bool still)
        {
            // U9: "needs you" is one thin solid red ring, standing; a running command has no ring of its own
            var pulse = mode == OrbMode.NeedsYou;
            const bool dashed = false;
            if (still || pulse)
                return new OrbPose(0f, 1f, pulse, pulse ? PulseStillScale : 1f, pulse ? PulseStillAlpha : 0f, dashed, 0f);
            switch (mode)
            {
                case OrbMode.Working:
                case OrbMode.Command:
                    return new OrbPose(Turn(seconds, WorkingTurnSeconds), Breath(seconds), false, 1f, 0f, dashed,
                        dashed ? Turn(seconds, RingTurnSeconds) : 0f);
                case OrbMode.Thinking:
                    return new OrbPose(Turn(seconds, ThinkingTurnSeconds), Breath(seconds), false, 1f, 0f, false, 0f);
                default:
                    return new OrbPose(0f, 1f, false, 1f, 0f, false, 0f);
            }
        }

        static float Turn(double seconds, double period) => (float)(360.0 * Phase(seconds, period));

        static float Breath(double seconds) =>
            1f + (BreathScale - 1f) * (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * Phase(seconds, BreathSeconds)));

        static double Phase(double seconds, double period)
        {
            var p = seconds / period;
            p -= Math.Floor(p);
            return p;
        }

        /// <summary>
        /// The conic gradient at `degrees` (clockwise from 12 o'clock), turned by `turn`: stop A at 0°, stop B opposite,
        /// a smooth cosine between them, so there is no seam.
        /// </summary>
        public static Rgb Conic(OrbColors c, float degrees, float turn)
        {
            var t = 0.5f - 0.5f * (float)Math.Cos((degrees - turn) * Math.PI / 180.0);
            return new Rgb(c.A.R + (c.B.R - c.A.R) * t, c.A.G + (c.B.G - c.A.G) * t, c.A.B + (c.B.B - c.A.B) * t);
        }
    }
}
