using System;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>The outcome of one environment raycast (MRUK's EnvironmentRaycastHitStatus).</summary>
    public enum DeskRayStatus { Hit, NoHit, HitPointOccluded, OutsideView, RayOccluded, NotReady, NotSupported }

    /// <summary>Raycasts against the real room (MRUK EnvironmentRaycast on the Quest; scripted in tests).</summary>
    public interface IDeskRaycaster
    {
        /// <summary>Whether raycasts can be made now; asks for the needed permission in Begin, not here.</summary>
        RaycastAvailability Availability { get; }

        /// <summary>Placement starts: request the permission if needed and switch the raycaster on.</summary>
        void Begin();

        /// <summary>Placement is over: switch the raycaster off (it costs GPU time while on).</summary>
        void End();

        /// <summary>Hit with the hit's point and surface normal (world space), or why there is none.</summary>
        DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal);
    }

    /// <summary>
    /// The persisted desk anchor (OVRSpatialAnchor on the Quest; scripted in tests). Only the anchor's uuid is
    /// stored on our side; the anchor itself holds nothing but its pose.
    /// </summary>
    public interface IDeskAnchors
    {
        /// <summary>A desk anchor was saved in an earlier run.</summary>
        bool HasSaved { get; }

        /// <summary>
        /// Loads and localises the saved anchor; `done` gets a new object whose transform follows the anchor, or null.
        /// Called on the main thread, possibly late: a caller that no longer wants it destroys the object.
        /// </summary>
        void LoadSaved(Action<Transform> done);

        /// <summary>A new anchor object at this pose, saved in place of the previous one (in storage). Returned at once.</summary>
        Transform Create(Vector3 position, Quaternion rotation);
    }
}
