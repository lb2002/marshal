using Meta.XR;
using UnityEngine;
using UnityEngine.Android;

namespace Lotse.App
{
    /// <summary>
    /// IDeskRaycaster over MRUK's EnvironmentRaycastManager (Depth API, no Space Setup needed).
    /// Support: MRUK's EnvironmentRaycastManager.IsSupported caches its first answer forever, and right after
    /// start the runtime may not answer yet. So support is asked here (the same OVRPlugin call MRUK's OpenXR
    /// provider makes) again every frame for up to 3 s after the first placement starts; the manager, which
    /// reads the cached value in Awake, is only added once support is known.
    /// Permission: MRUK's default OpenXR raycaster needs the spatial data permission
    /// com.oculus.permission.USE_SCENE and only waits for it, so it is requested here through Unity's Android
    /// permission API (what OVRPermissionsRequester recommends when callbacks are needed), once per placement
    /// (PermissionAsk). A denial means PermissionDenied and stands for the run; a dismissed request (the empty
    /// result) means PermissionDismissed and is asked again on the next placement. Either way the caller falls
    /// back; so does PlacementFlow when the dialog gets no answer within 20 s.
    /// </summary>
    public sealed class MrukDeskRaycaster : IDeskRaycaster
    {
        const string ScenePermission = OVRPermissionsRequester.ScenePermission;
        const float SupportCheckSeconds = 3f;

        EnvironmentRaycastManager _manager;
        readonly PermissionAsk _ask = new PermissionAsk();
        bool _active;
        float _supportCheckStart = -1f;
        bool? _supported;
        // set from the permission callbacks, which may arrive on another thread; taken on the main thread
        int _answer = (int)PermissionAnswer.None;

        public RaycastAvailability Availability
        {
            get
            {
                var supported = Supported();
                if (supported == null) return RaycastAvailability.Starting;
                if (supported == false) return RaycastAvailability.Unsupported;
                var answer = (PermissionAnswer)System.Threading.Interlocked.Exchange(ref _answer, (int)PermissionAnswer.None);
                if (answer != PermissionAnswer.None)
                {
                    Debug.Log($"[Lotse] spatial data permission answer: {answer}");
                    _ask.Answered(answer);
                }
                var granted = Permission.HasUserAuthorizedPermission(ScenePermission);
                if (!granted && _active && _ask.ShouldAsk) Ask();
                var availability = _ask.Availability(granted);
                if (availability == RaycastAvailability.Ready && _active) EnsureManager();
                return availability;
            }
        }

        public void Begin()
        {
            _active = true;
            // an answer to the previous placement's dialog that came in after it ended is taken first
            var stale = (PermissionAnswer)System.Threading.Interlocked.Exchange(ref _answer, (int)PermissionAnswer.None);
            if (stale != PermissionAnswer.None) Debug.Log($"[Lotse] spatial data permission answer from the last placement: {stale}");
            _ask.BeginPlacement(stale);
            if (_supportCheckStart < 0f) _supportCheckStart = Time.time;
        }

        public void End()
        {
            _active = false;
            if (_manager != null) _manager.enabled = false;
        }

        public DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            if (_manager == null || !_manager.isActiveAndEnabled) return DeskRayStatus.NotReady;
            _manager.Raycast(new Ray(origin, direction), out var hit, maxDistance);
            point = hit.point;
            normal = hit.normal;
            switch (hit.status)
            {
                case EnvironmentRaycastHitStatus.Hit: return DeskRayStatus.Hit;
                case EnvironmentRaycastHitStatus.HitPointOccluded: return DeskRayStatus.HitPointOccluded;
                case EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum: return DeskRayStatus.OutsideView;
                case EnvironmentRaycastHitStatus.RayOccluded: return DeskRayStatus.RayOccluded;
                case EnvironmentRaycastHitStatus.NotReady: return DeskRayStatus.NotReady;
                case EnvironmentRaycastHitStatus.NotSupported: return DeskRayStatus.NotSupported;
                default: return DeskRayStatus.NoHit;
            }
        }

        /// <summary>True or false once known; null while still asking (at most 3 s).</summary>
        bool? Supported()
        {
            if (_supported.HasValue || _supportCheckStart < 0f) return _supported;
            if (OVRPlugin.GetEnvironmentRaycastSupported(out var value).IsSuccess() && value) _supported = true;
            else if (Time.time - _supportCheckStart >= SupportCheckSeconds) _supported = false;
            if (_supported.HasValue)
                Debug.Log($"[Lotse] environment raycast supported: {_supported.Value} (after {Time.time - _supportCheckStart:0.0} s)");
            return _supported;
        }

        void Ask()
        {
            _ask.MarkAsked();
            Debug.Log("[Lotse] asking for the spatial data permission (" + ScenePermission + ")");
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => System.Threading.Interlocked.Exchange(ref _answer, (int)PermissionAnswer.Granted);
            callbacks.PermissionDenied += _ => System.Threading.Interlocked.Exchange(ref _answer, (int)PermissionAnswer.Denied);
            callbacks.PermissionRequestDismissed += _ => System.Threading.Interlocked.Exchange(ref _answer, (int)PermissionAnswer.Dismissed);
            Permission.RequestUserPermission(ScenePermission, callbacks);
        }

        void EnsureManager()
        {
            if (_manager == null)
            {
                _manager = new GameObject("LotseEnvironmentRaycast").AddComponent<EnvironmentRaycastManager>();
                Debug.Log("[Lotse] environment raycast manager created");
            }
            else if (!_manager.enabled)
            {
                _manager.enabled = true;
            }
        }
    }
}
