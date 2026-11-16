#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// The numbers to tune on the headset, in one place: the hold gate, the wrist menu, the hand ray and the desk
    /// keyboard (the workspace's own pose is WorkspaceDock's). Metres and degrees; a tuning change is one line here.
    /// </summary>
    public static class Layout
    {
        // ---- hold gate (HoldGate) ----

        /// <summary>The head faces the approval block within this angle.</summary>
        public const float GateFacingDegrees = 35f;
        /// <summary>The pinch point lies within this angle of the head's forward axis.</summary>
        public const float GateHandConeDegrees = 70f;
        /// <summary>On a desk: the gate's floor is this far above the desk surface.</summary>
        public const float GateDeskFloorMeters = 0.10f;
        /// <summary>Without a desk: the gate's floor is this far below the eyes.</summary>
        public const float GateBelowEyesMeters = 0.45f;

        // ---- the wrist menu (WristMenuLogic) ----

        /// <summary>Palm up: the palm normal's dot with world up is above this.</summary>
        public const float MenuPalmUpMin = 0.7f;
        /// <summary>The palm is within this angle of the head's view direction.</summary>
        public const float MenuViewDegrees = 45f;
        /// <summary>The palm has to stay up this long before the menu shows.</summary>
        public const double MenuShowMs = 400;
        /// <summary>The menu's centre floats this far above the palm.</summary>
        public const float MenuAbovePalmMeters = 0.1f;

        // ---- the hand ray (RayPick, RayClickLogic) ----

        /// <summary>The ray reaches this far.</summary>
        public const float RayMaxMeters = 5.0f; // U10: the window stands up to 3.5 m away and is used with the ray
        /// <summary>Without a hit the ray is drawn this long.</summary>
        public const float RayIdleMeters = 0.3f;
        /// <summary>Extra margin around a button's face for the ray.</summary>
        public const float RayButtonMargin = 0.005f;

        // ---- the desk keyboard (U4b) ----

        /// <summary>It lies this far in front of the desk anchor (toward the user) ...</summary>
        public const float DeskKeyboardFrontMeters = 0.25f;
        /// <summary>... and this far above the desk.</summary>
        public const float DeskKeyboardAboveDeskMeters = 0.012f;
    }
}
