#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>
    /// The design system's numbers as pure values (U9 tokens, U10 angles), so that tests can compute what they mean at
    /// the default distance: the spacing unit, the one control height, the invisible hit padding, the controls' widths
    /// and the type sizes. Theme hands them to the views. All metres are at the window's built size (0.62 x 0.36 m); the
    /// window on screen is that times DefaultScale.
    /// Rules checked by tests (from Meta's design guidance, 2026-11-15-quest-pro-practice.md):
    /// - every ray target subtends at least 3 degrees at the default distance (the drawn control plus its hit padding);
    /// - neighbouring controls' drawn edges are at least 1 degree apart;
    /// - the cap height of Body text subtends at least 0.5 degrees, of Small text at least 0.4 degrees.
    /// </summary>
    public static class UiMetrics
    {
        /// <summary>The spacing unit, 4 mm.</summary>
        public const float S = 0.004f;
        /// <summary>The one height of every button, tab, text field and menu item.</summary>
        public const float H = 0.027f;
        /// <summary>
        /// The invisible hit area around a drawn control, on every side (for the ray; a fingertip still has to touch the
        /// drawn control). Neighbours are 4 x S apart, so two padded areas meet in the middle and never overlap.
        /// </summary>
        public const float HitPad = 2f * S;
        /// <summary>The gap between neighbouring controls: 4 x S.</summary>
        public const float ControlGap = 4f * S;
        /// <summary>Later, Deny, Keyboard, Send, a band's buttons.</summary>
        public const float ButtonWidth = 16f * S;
        /// <summary>A menu item.</summary>
        public const float MenuButtonWidth = 22f * S;
        /// <summary>"Menu" and "Collapse" in the header; a tab is at least as wide.</summary>
        public const float HeaderButtonWidth = 0.034f;
        /// <summary>The pager's and the feed's arrow buttons.</summary>
        public const float ArrowButtonWidth = 11f * S;
        /// <summary>A row of the agent list, a slot of the rail.</summary>
        public const float RowHeight = 11f * S;

        /// <summary>Type sizes (TextMeshPro font sizes; one unit of size is 0.1 m of em).</summary>
        /// <summary>One unit of the design (U13: the window is 1180 units wide), in metres at the built size.</summary>
        public const float Unit = WorkspaceDock.BuildHeight * WorkspaceDock.Aspect / 1180f;
        /// <summary>A text of `units` in the design as a TextMeshPro font size.</summary>
        public const float SizePerUnit = Unit / EmPerSize;
        public const float Title = 25f * SizePerUnit;
        public const float Body = 19f * SizePerUnit;
        public const float Small = 16f * SizePerUnit;
        /// <summary>The header's sentence, the facts' numerals, a button's label.</summary>
        public const float Headline = 44f * SizePerUnit;
        public const float Facts = 30f * SizePerUnit;
        public const float Label = 17f * SizePerUnit;
        public const float EmPerSize = 0.1f;
        /// <summary>Geist's cap height over its em (710 of 1000 units).</summary>
        public const float CapRatio = 0.71f;

        public const float MinHitDegrees = 3f;
        public const float MinGapDegrees = 1f;
        public const float MinBodyCapDegrees = 0.5f;
        public const float MinSmallCapDegrees = 0.4f;

        /// <summary>The window on screen at the default size and distance, over its built size.</summary>
        public static float DefaultScale => WorkspaceDock.DefaultHeight / WorkspaceDock.BuildHeight;

        /// <summary>The visual angle of `meters` seen straight on from `distance`, in degrees.</summary>
        public static float Degrees(float meters, float distance) => (float)(2.0 * Math.Atan(meters * 0.5 / distance) * 180.0 / Math.PI);

        /// <summary>The smaller side of a control's hit area (drawn size plus padding), at the default size and distance.</summary>
        public static float HitDegrees(float width, float height) =>
            Degrees((Math.Min(width, height) + 2f * HitPad) * DefaultScale, WorkspaceDock.InFrontMeters);

        /// <summary>A gap between two drawn controls at the default size and distance.</summary>
        public static float GapDegrees(float gap) => Degrees(gap * DefaultScale, WorkspaceDock.InFrontMeters);

        /// <summary>The cap height of text of `fontSize` at the default size and distance.</summary>
        public static float CapDegrees(float fontSize) => Degrees(fontSize * EmPerSize * CapRatio * DefaultScale, WorkspaceDock.InFrontMeters);

        /// <summary>The header's, the block's, the input row's, the bands' and the menu's controls: name, drawn width, drawn height.</summary>
        public static readonly (string name, float width, float height)[] Controls =
        {
                        ("header Menu", HeaderButtonWidth, H),
            ("header Fold away", HeaderButtonWidth, H),
            ("block Later", ButtonWidth, H),
            ("block Deny", ButtonWidth, H),
            ("input Keyboard", ButtonWidth, H),
            ("input Send", ButtonWidth, H),
            ("band button", ButtonWidth, H),
            ("menu item", MenuButtonWidth, H),
            ("pager arrow", ArrowButtonWidth, H),
            
        };
    }
}
