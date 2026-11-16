#nullable enable
using System;
using System.Globalization;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>The facts row's verdict chip. There is no positive verdict: the bridge never says something is safe.</summary>
    public enum ImpactVerdict { Checking, Unknown, GoneForGood }

    /// <summary>
    /// What the facts row of a two-hand request shows (U7, contract 2026-11-12-contract-impact-halt.md A): above it
    /// "Checked: ~/code/legacy-site" when the impact names a target; the counts ("214 files, 38 MB", "at least 20,000
    /// files" when capped; none without counts); where ("outside the project" / "inside the project"; none when unknown);
    /// the verdict ("Gone for good" only for `recoverable: "no"`, "Effects unknown" for anything else, "Checking effects"
    /// for at most 2 s while no impact has come); the bridge's reason sentence below. Display only: it never changes the
    /// risk word, the gesture or what approving needs. Pure.
    /// </summary>
    /// <summary>One fact as U13 draws it: a large value over a small caption ("214" over "files"); red only for "Gone".</summary>
    public readonly struct FactCell
    {
        public readonly string Value;
        public readonly string Caption;
        public readonly bool Red;

        public FactCell(string value, string caption, bool red = false)
        {
            Value = value;
            Caption = caption;
            Red = red;
        }

        public override string ToString() => Value + " " + Caption;
    }

    public readonly struct ImpactFacts
    {
        /// <summary>How long "Checking effects" may stand after the block appeared without an impact.</summary>
        public const double CheckingMs = 2000;

        public readonly string? Checked;
        public readonly string? Counts;
        public readonly string? Where;
        public readonly ImpactVerdict Verdict;
        public readonly string? Reason;

        /// <summary>
        /// The facts as cells, left to right: the files, the size, inside or outside the project, the verdict. A fact
        /// that is not known has no cell (nothing is invented); the verdict always has one: "Gone / no way back" only
        /// for a loss the bridge is sure of, else "Unknown / effects" (or "Checking / effects" for the first 2 s).
        /// </summary>
        public readonly System.Collections.Generic.IReadOnlyList<FactCell> Cells;

        ImpactFacts(string? checkedLine, string? counts, string? where, ImpactVerdict verdict, string? reason, Impact? impact = null)
        {
            var cells = new System.Collections.Generic.List<FactCell>(4);
            if (impact != null)
            {
                if (impact.Files.HasValue)
                    cells.Add(new FactCell(Number(impact.Files.Value),
                        impact.FilesCapped ? Texts.FactFilesAtLeast : impact.Files.Value == 1 ? Texts.FactFile : Texts.FactFiles));
                if (impact.Bytes.HasValue) cells.Add(new FactCell(Size(impact.Bytes.Value), impact.FilesCapped ? Texts.FactSizeAtLeast : Texts.FactSize));
                if (impact.OutsideProject.HasValue)
                    cells.Add(new FactCell(impact.OutsideProject.Value ? Texts.FactOutside : Texts.FactInside, Texts.FactProject));
            }
            cells.Add(verdict == ImpactVerdict.GoneForGood ? new FactCell(Texts.FactGone, Texts.FactGoneCaption, true)
                : new FactCell(verdict == ImpactVerdict.Checking ? Texts.FactChecking : Texts.FactUnknown, Texts.FactEffects));
            Cells = cells;
            Checked = checkedLine;
            Counts = counts;
            Where = where;
            Verdict = verdict;
            Reason = reason;
        }

        public string VerdictText => Verdict == ImpactVerdict.GoneForGood ? Texts.GoneForGood
            : Verdict == ImpactVerdict.Checking ? Texts.CheckingEffects : Texts.EffectsUnknown;

        /// <summary>A key of everything shown (the block is drawn again, and a hold cancelled, when it changes).</summary>
        public string Key => Verdict + "|" + Checked + "|" + Counts + "|" + Where + "|" + Reason;

        /// <summary>The row for `impact` (null: none came) on a block shown for `shownMs`.</summary>
        public static ImpactFacts For(Impact? impact, double shownMs)
        {
            if (impact == null)
                return new ImpactFacts(null, null, null, shownMs < CheckingMs ? ImpactVerdict.Checking : ImpactVerdict.Unknown, null);
            var verdict = impact.Recoverable == "no" ? ImpactVerdict.GoneForGood : ImpactVerdict.Unknown;
            var target = string.IsNullOrWhiteSpace(impact.Target) ? null : Texts.CheckedPrefix + impact.Target;
            string? where = impact.OutsideProject == true ? Texts.OutsideProject : impact.OutsideProject == false ? Texts.InsideProject : null;
            var reason = string.IsNullOrWhiteSpace(impact.Reason) ? null : impact.Reason;
            return new ImpactFacts(target, CountsOf(impact), where, verdict, reason, impact);
        }

        /// <summary>"214 files, 38 MB"; "at least 20,000 files" when the count stopped at a limit; null without counts.</summary>
        static string? CountsOf(Impact i)
        {
            var atLeast = i.FilesCapped ? "at least " : "";
            string? files = i.Files.HasValue ? atLeast + Number(i.Files.Value) + (i.Files.Value == 1 ? " file" : " files") : null;
            string? bytes = i.Bytes.HasValue ? atLeast + Size(i.Bytes.Value) : null;
            if (files != null && bytes != null) return files + ", " + bytes;
            return files ?? bytes;
        }

        static string Number(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

        /// <summary>"38 MB", "512 KB", "1.2 GB", "900 bytes" (decimal units).</summary>
        public static string Size(double bytes)
        {
            if (bytes < 1000) return Number((long)bytes) + (bytes == 1 ? " byte" : " bytes");
            if (bytes < 1e6) return Number((long)Math.Round(bytes / 1e3)) + " KB";
            if (bytes < 1e9) return Number((long)Math.Round(bytes / 1e6)) + " MB";
            return (bytes / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }
    }
}
