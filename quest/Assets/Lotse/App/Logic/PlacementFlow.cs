#nullable enable

namespace Lotse.App
{
    public enum PlacementState { Idle, LoadingSaved, Placing, Placed, LoadedSaved, Fallback }

    /// <summary>Can the environment raycast be used right now? Starting: support is still being checked.</summary>
    public enum RaycastAvailability { Unsupported, PermissionDenied, PermissionDismissed, Starting, AskingPermission, Ready }

    public enum PlacementFallback { None, Unsupported, PermissionDenied, PermissionDismissed, NoPermissionAnswer, NoHit, TimedOut }

    /// <summary>
    /// Where the anchor comes from. With a saved desk anchor it first tries to load and localise that one; if
    /// that does not succeed within 3 s it asks for a tap on the desk (Placing). Placing falls back to the
    /// head-relative anchor when the environment raycast cannot be used (unsupported, permission denied or the
    /// request dismissed), when there has been no raycast hit for 8 s, or after 30 s in any case, so the start
    /// never hangs. Neither clock runs while the raycast is starting or the permission dialog is up; the dialog
    /// itself gets 20 s in all, then placement falls back too. Pure: the caller feeds the time and what the raycast saw.
    /// </summary>
    public sealed class PlacementFlow
    {
        public const double LoadMs = 3000;
        public const double NoHitMs = 8000;
        public const double MaxPlacingMs = 30000;
        public const double MaxAskingMs = 20000;

        double _loadStart;
        double _placingStart;
        double _noHitSince;
        double _lastTick;
        double _askingMs;

        public PlacementState State { get; private set; } = PlacementState.Idle;
        public PlacementFallback Fallback { get; private set; }

        /// <summary>Still deciding (loading the saved anchor or waiting for the tap).</summary>
        public bool Active => State == PlacementState.LoadingSaved || State == PlacementState.Placing;

        /// <summary>Starts over, from the saved anchor if `loadSaved` (the caller has one), otherwise with the tap.</summary>
        public void Begin(double nowMs, bool loadSaved)
        {
            Fallback = PlacementFallback.None;
            if (loadSaved)
            {
                State = PlacementState.LoadingSaved;
                _loadStart = nowMs;
            }
            else
            {
                StartPlacing(nowMs);
            }
        }

        /// <summary>The saved anchor's load ended. Ignored unless it is still awaited (a late answer after the 3 s).</summary>
        public void SavedLoaded(double nowMs, bool localized)
        {
            if (State != PlacementState.LoadingSaved) return;
            if (localized) State = PlacementState.LoadedSaved;
            else StartPlacing(nowMs);
        }

        /// <summary>One frame. `sawHit`: any raycast hit at all; `accepted`: DeskTouch accepted a desk touch.</summary>
        public void Tick(double nowMs, RaycastAvailability availability, bool sawHit, bool accepted)
        {
            if (State == PlacementState.LoadingSaved)
            {
                if (nowMs - _loadStart >= LoadMs) StartPlacing(nowMs);
                return;
            }
            if (State != PlacementState.Placing) return;

            var sinceLast = nowMs - _lastTick;
            _lastTick = nowMs;
            switch (availability)
            {
                case RaycastAvailability.Unsupported:
                    Fail(PlacementFallback.Unsupported);
                    return;
                case RaycastAvailability.PermissionDenied:
                    Fail(PlacementFallback.PermissionDenied);
                    return;
                case RaycastAvailability.PermissionDismissed:
                    Fail(PlacementFallback.PermissionDismissed);
                    return;
                case RaycastAvailability.AskingPermission:
                    // the dialog gets 20 s in all; no answer by then is no answer
                    _askingMs += sinceLast;
                    if (_askingMs >= MaxAskingMs)
                    {
                        Fail(PlacementFallback.NoPermissionAnswer);
                        return;
                    }
                    _noHitSince = nowMs;
                    _placingStart += sinceLast;
                    break;
                case RaycastAvailability.Starting:
                    // neither clock runs while the raycast starts or the permission dialog is up
                    _noHitSince = nowMs;
                    _placingStart += sinceLast;
                    break;
                case RaycastAvailability.Ready:
                    if (accepted)
                    {
                        State = PlacementState.Placed;
                        return;
                    }
                    if (sawHit) _noHitSince = nowMs;
                    else if (nowMs - _noHitSince >= NoHitMs)
                    {
                        Fail(PlacementFallback.NoHit);
                        return;
                    }
                    break;
            }
            if (nowMs - _placingStart >= MaxPlacingMs) Fail(PlacementFallback.TimedOut);
        }

        void StartPlacing(double nowMs)
        {
            State = PlacementState.Placing;
            _askingMs = 0;
            _placingStart = nowMs;
            _noHitSince = nowMs;
            _lastTick = nowMs;
        }

        void Fail(PlacementFallback reason)
        {
            State = PlacementState.Fallback;
            Fallback = reason;
        }
    }
}
