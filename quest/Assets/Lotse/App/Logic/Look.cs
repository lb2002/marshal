#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>An sRGB colour, 0..1, so the pure logic needs no engine types.</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        public readonly float R;
        public readonly float G;
        public readonly float B;

        public Rgb(float r, float g, float b)
        {
            R = r;
            G = g;
            B = b;
        }

        public bool Equals(Rgb o) => R == o.R && G == o.G && B == o.B;
        public override bool Equals(object? obj) => obj is Rgb o && Equals(o);
        public override int GetHashCode() => (R, G, B).GetHashCode();
        public override string ToString() => $"({R:0.##}, {G:0.##}, {B:0.##})";
    }

    /// <summary>The app's colours as pure values (Theme turns them into engine colours). One place, so it all looks the same.</summary>
    public static class Look
    {
        /// <summary>An sRGB colour from 0xRRGGBB.</summary>
        public static Rgb Hex(int rgb) => new Rgb(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        // ---- the dark, strict system (U5a) ----

        /// <summary>The panel: graphite #1A1F24 (at 90 %).</summary>
        // ---- U13, the "taxi lines" design. The older names below now carry the same palette (Graphite is the asphalt,
        // Card the raised fill, Well the inset, Amber the stop red: there is no amber any more). ----
        /// <summary>The window: one opaque surface, no cards.</summary>
        public static readonly Rgb Asphalt = Hex(0x17191C);
        /// <summary>Only what needs the user is raised.</summary>
        public static readonly Rgb Raised = Hex(0x212428);
        /// <summary>The command and the reply field.</summary>
        public static readonly Rgb Inset = Hex(0x0E0F11);
        public static readonly Rgb Ink = Hex(0xF3F1EA);
        public static readonly Rgb TaxiMuted = Hex(0xAFB2B6);
        public static readonly Rgb Faint = Hex(0x3B3F45);
        /// <summary>The painted centreline.</summary>
        public static readonly Rgb Taxi = Hex(0xF6CB3C);
        /// <summary>Stop bar lights; halted too (no amber).</summary>
        public static readonly Rgb Stop = Hex(0xFF4A3A);
        public static readonly Rgb Go = Hex(0x55E0A2);
        /// <summary>The deny button's label.</summary>
        public static readonly Rgb StopText = Hex(0xFF9C92);

        /// <summary>A vendor's colour: only the 9-unit dot before its name.</summary>
        public static Rgb Vendor(string? source)
        {
            switch (source)
            {
                case "claude": return Hex(0xE8875B);
                case "codex": return Hex(0x8F7CF7);
                case "gemini": return Hex(0x5C9DF5);
                case "muse": return Hex(0x55E0A2);
                default: return TaxiMuted;
            }
        }

        public static readonly Rgb Graphite = Hex(0x17191C);
        /// <summary>A card on the panel: one step lighter (U9).</summary>
        public static readonly Rgb Card = Hex(0x212428);
        /// <summary>A well inside a card (the command inset): one step darker than the panel.</summary>
        public static readonly Rgb Well = Hex(0x0E0F11);
        /// <summary>A control at rest, and a disabled one.</summary>
        public static readonly Rgb Control = Hex(0x27292C);
        public static readonly Rgb ControlOff = Hex(0x1F2124);
        /// <summary>The one hairline colour: borders and dividers.</summary>
        public static readonly Rgb Hairline = Hex(0x3B3F45);
        /// <summary>Primary text #ECEFF1 (also the one primary button per view).</summary>
        public static readonly Rgb TextPrimary = Hex(0xF3F1EA);
        /// <summary>Secondary text #8A949C.</summary>
        public static readonly Rgb TextSecondary = Hex(0xAFB2B6);
        /// <summary>Commands, paths and URLs #C9D0D5.</summary>
        public static readonly Rgb TextMono = Hex(0xF3F1EA);
        /// <summary>Red #FF5A4E: only "needs you" and dangerous requests (as text on dark: #FF8178).</summary>
        public static readonly Rgb Red = Hex(0xFF4A3A);
        public static readonly Rgb RedText = Hex(0xFF9C92);
        /// <summary>Green #46C98B: only "done".</summary>
        public static readonly Rgb Green = Hex(0x55E0A2);
        /// <summary>Amber #FFB23E: only halt-all (U7): the stop bar, the halted band, the pause badges.</summary>
        public static readonly Rgb Amber = Hex(0xFF4A3A);
        /// <summary>Text on a disabled control (U10: still at least 3:1 against it).</summary>
        public static readonly Rgb TextDisabled = Hex(0x7C7F84);

        /// <summary>WCAG relative luminance of an sRGB colour.</summary>
        public static double Luminance(Rgb c)
        {
            double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        /// <summary>WCAG contrast ratio of two colours (1 .. 21).</summary>
        public static double Contrast(Rgb a, Rgb b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>The dark content of the primary button.</summary>
        public static readonly Rgb OnPrimary = Hex(0x17191C);

        /// <summary>Chime pitch per occasion: a request that needs the user, an agent done, an error, anything else.</summary>
        public static float ChimeHz(string status)
        {
            switch (status)
            {
                case "blocked":
                case "waiting": return 523.25f; // C5: needs you
                case "done": return 1046.5f;    // C6: finished
                case "failed": return 392f;     // G4
                default: return 783.99f;        // G5
            }
        }
    }
}
