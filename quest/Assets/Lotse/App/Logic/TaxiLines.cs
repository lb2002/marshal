#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>
    /// The "taxi lines" design (U13), its measures in the design's own units: the window is 1180 x 690 units wide and
    /// high, and every size scales with the window. Colours are in Look (Asphalt, Raised, Inset, Ink, Faint, Taxi, Stop, Go).
    /// </summary>
    public static class Taxi
    {
        public const float WindowUnits = 1180f;
        public const float WindowHeightUnits = 690f;

        // type, in units (the font's size); nothing is smaller than 16
        public const float Headline = 44f;
        public const float RowTitle = 25f;
        public const float Facts = 30f;
        public const float Body = 19f;
        public const float Small = 16f;
        public const float Mono = 19f;
        public const float MonoLog = 16f;
        public const float HeadlineLineHeight = 1.04f;
        public const float HeadlineTracking = -0.03f;

        // radii: not one for all
        public const float WindowRadius = 30f;
        public const float RowRadius = 16f;
        public const float ButtonRadius = 12f;
        public const float InsetRadius = 10f;
        public const float LightRadius = 3f;

        // the line
        public const float LineWidth = 3f;
        public const float MarkWidth = 3f;
        public const float MarkHeight = 13f;
        public const float Dot = 18f;
        public const float DotRing = 5f;
        public const float DashPeriod = 25f;
        public const float DashShare = 0.56f;
        public const float PingSeconds = 2.6f;
        public const float TravelSeconds = 1.0f;
        public const float OpenSeconds = 0.6f;

        // the stop bar across a line
        public const int RowLights = 7;
        public const float RowLightWidth = 15f;
        public const float RowLightHeight = 10f;
        public const float RowLightGap = 5f;
        public const float RowBarAhead = 34f;

        public const float ButtonHeight = 50f;

        /// <summary>The design's one ease-out curve, cubic-bezier(.22, .8, .2, 1), for t in 0..1.</summary>
        public static float Ease(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            // solve x(s) = t for the curve's parameter, then return y(s)
            const float x1 = 0.22f, y1 = 0.8f, x2 = 0.2f, y2 = 1f;
            float lo = 0f, hi = 1f, s = t;
            for (var i = 0; i < 24; i++)
            {
                s = (lo + hi) * 0.5f;
                var x = 3f * (1f - s) * (1f - s) * s * x1 + 3f * (1f - s) * s * s * x2 + s * s * s;
                if (x < t) lo = s;
                else hi = s;
            }
            return 3f * (1f - s) * (1f - s) * s * y1 + 3f * (1f - s) * s * s * y2 + s * s * s;
        }
    }

    /// <summary>Where the current step's text stands relative to the dot.</summary>
    public enum NowAnchor
    {
        /// <summary>Its left edge at the dot.</summary>
        Left,
        /// <summary>Its right edge at the dot (the dot is far along, or a stop bar stands ahead of it).</summary>
        Right,
        /// <summary>Behind the stop bar (the dot is near the start and a bar stands ahead).</summary>
        AfterBar,
    }

    /// <summary>
    /// One agent's line (U13): where its marks and its dot are, how the way ahead is dashed, and where the step's text
    /// stands. Pure.
    /// </summary>
    public static class TaxiLine
    {
        /// <summary>The line's first and last mark stand this far (a share of its length) from its ends.</summary>
        public const float Inset = 0.015f;

        /// <summary>Where step `index` of `steps` stands, 0..1 along the line. One step: at the start.</summary>
        public static float At(int index, int steps)
        {
            if (steps <= 1) return Inset;
            var i = index < 0 ? 0 : index > steps - 1 ? steps - 1 : index;
            return Inset + (1f - 2f * Inset) * i / (steps - 1);
        }

        /// <summary>
        /// The dashes of one stretch of the way ahead, from `from` to `to` (any unit): a whole number of equal periods
        /// near `period`, each starting with its dash, so the stretch begins with a dash right at its mark and ends
        /// with a gap before the next mark. `dashes` is cleared first.
        /// </summary>
        public static void Dashes(float from, float to, float period, float share, List<(float from, float to)> dashes)
        {
            dashes.Clear();
            var length = to - from;
            if (length <= 0f || period <= 0f) return;
            var n = Math.Max(1, (int)Math.Round(length / period));
            var each = length / n;
            for (var i = 0; i < n; i++) dashes.Add((from + i * each, from + i * each + each * share));
        }

        /// <summary>
        /// Where the step's text stands: left-anchored at the dot; right-anchored when the dot is past 60 % of the
        /// line; with a stop bar ahead of the dot the text is never under the bar: right-anchored past 30 %, else
        /// behind the bar.
        /// </summary>
        public static NowAnchor Anchor(float at, bool barAhead)
        {
            if (at > 0.6f || (barAhead && at > 0.3f)) return NowAnchor.Right;
            return barAhead ? NowAnchor.AfterBar : NowAnchor.Left;
        }
    }

    /// <summary>
    /// The stop bar of an open request (U13): a row of red lights that shows the hold's progress and nothing else. It is
    /// a display, never a control. Which lights are lit is a pure function of the progress and the kind:
    /// - two hands: 21 lights, going dark from both ends toward the middle;
    /// - one hand: 9 lights, going dark from left to right.
    /// At progress 0 all are lit; at 1 all are dark (the view then shows them green for a moment: "Cleared").
    /// </summary>
    public static class StopBar
    {
        public const int TwoHandLights = 21;
        public const int OneHandLights = 9;

        public static int Lights(bool twoHand) => twoHand ? TwoHandLights : OneHandLights;

        /// <summary>How many lights are dark from each end (two hands) or from the left (one hand).</summary>
        public static int Dark(float progress, bool twoHand)
        {
            var p = progress < 0f || float.IsNaN(progress) ? 0f : progress > 1f ? 1f : progress;
            var steps = twoHand ? (TwoHandLights + 1) / 2 : OneHandLights;
            return (int)Math.Round(p * steps, MidpointRounding.AwayFromZero);
        }

        /// <summary>Whether light `index` (0 at the left) is lit.</summary>
        public static bool Lit(int index, float progress, bool twoHand)
        {
            var n = Lights(twoHand);
            if (index < 0 || index >= n) return false;
            var dark = Dark(progress, twoHand);
            return twoHand ? Math.Min(index, n - 1 - index) >= dark : index >= dark;
        }

        /// <summary>How many lights are lit.</summary>
        public static int LitCount(float progress, bool twoHand)
        {
            var c = 0;
            for (var i = 0; i < Lights(twoHand); i++)
                if (Lit(i, progress, twoHand)) c++;
            return c;
        }
    }

    /// <summary>
    /// The header's one sentence (U13): the single thing that matters now, in this order: halted; one agent holding;
    /// several holding; all landed; else all moving. Vendor names are the app's own table (Texts), never agent text.
    /// </summary>
    public static class Headline
    {
        public const string Stopped = "Everything is stopped.";
        public const string NoAgents = "No agents yet.";
        public const string NoAgentsHint = "Start one in your terminal.";

        static readonly string[] Words = { "zero", "one", "two", "three", "four", "five", "six" };

        /// <summary>The number as a word up to six, as digits above.</summary>
        public static string Number(int n) => n >= 0 && n < Words.Length ? Words[n] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <param name="halted">Everything is stopped.</param>
        /// <param name="total">Agents on the window.</param>
        /// <param name="holding">How many wait for the user.</param>
        /// <param name="holdingVendor">The vendor's name of the one that waits, when exactly one does (null: unknown).</param>
        /// <param name="landed">How many are done.</param>
        public static string Overview(bool halted, int total, int holding, string? holdingVendor, int landed)
        {
            if (halted) return Stopped;
            if (total <= 0) return NoAgents;
            if (holding == 1) return (string.IsNullOrEmpty(holdingVendor) ? "One agent" : holdingVendor) + " is holding for you.";
            if (holding > 1) return Capital(Number(holding)) + " agents are holding for you.";
            if (total == 1) return landed == 1 ? "Your agent has landed." : "Your agent is moving.";
            if (landed >= total) return "All " + Number(total) + " have landed.";
            return "All " + Number(total) + " are moving.";
        }

        /// <summary>An open working agent: "Codex is on step 3 of 5.", without a plan "Codex is working."</summary>
        public static string Working(string? vendor, int step, int steps)
        {
            var who = string.IsNullOrEmpty(vendor) ? "This agent" : vendor!;
            return steps > 0 && step >= 1 ? who + " is on step " + Math.Min(step, steps) + " of " + steps + "." : who + " is working.";
        }

        /// <summary>An open agent that is done: "Codex has landed."</summary>
        public static string Landed(string? vendor) => (string.IsNullOrEmpty(vendor) ? "This agent" : vendor) + " has landed.";

        /// <summary>An open agent that waits for the user's word (no request): "Codex is waiting for you."</summary>
        public static string Waiting(string? vendor) => (string.IsNullOrEmpty(vendor) ? "This agent" : vendor) + " is waiting for you.";

        /// <summary>After an approval: "Cleared. Codex is moving again".</summary>
        public static string Cleared(string? vendor) => "Cleared. " + (string.IsNullOrEmpty(vendor) ? "The agent" : vendor) + " is moving again";
    }
}
