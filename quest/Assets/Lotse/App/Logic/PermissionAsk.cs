#nullable enable

namespace Lotse.App
{
    /// <summary>The answer to the spatial data permission request; Dismissed is Android's empty result.</summary>
    public enum PermissionAnswer { None, Granted, Denied, Dismissed }

    /// <summary>
    /// Asking for the spatial data permission (desk placement), once per placement. A dismissed request (Android
    /// gives an empty result, e.g. when the dialog was interrupted) is not a denial, and neither is one that got
    /// no answer: both are asked again on the next placement. A denial stands for the run: no nagging after a no.
    /// A permission granted later in the system settings counts at once.
    /// </summary>
    public sealed class PermissionAsk
    {
        bool _asked;

        public PermissionAnswer Answer { get; private set; }

        /// <summary>Not asked yet in this placement, and not denied.</summary>
        public bool ShouldAsk => !_asked && Answer != PermissionAnswer.Denied;

        public void BeginPlacement()
        {
            if (Answer == PermissionAnswer.Denied) return;
            _asked = false;
            Answer = PermissionAnswer.None;
        }

        /// <summary>
        /// A new placement, with the answer that came in since the last one was taken (the previous dialog may
        /// have been answered after that placement fell back): a late denial stands, anything else is asked again.
        /// </summary>
        public void BeginPlacement(PermissionAnswer pending)
        {
            if (pending != PermissionAnswer.None) Answered(pending);
            BeginPlacement();
        }

        public void MarkAsked() => _asked = true;

        public void Answered(PermissionAnswer answer) => Answer = answer;

        public RaycastAvailability Availability(bool granted)
        {
            if (granted) return RaycastAvailability.Ready;
            switch (Answer)
            {
                case PermissionAnswer.Denied: return RaycastAvailability.PermissionDenied;
                case PermissionAnswer.Dismissed: return RaycastAvailability.PermissionDismissed;
                default: return RaycastAvailability.AskingPermission;
            }
        }
    }
}
