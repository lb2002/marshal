using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// Lazy head-follow for things that float in front of the user: they stay put until they are 15 cm or 25°
    /// off their place, then glide back (4/s) and stop within 1 cm and 2°.
    /// </summary>
    public static class LazyFollow
    {
        const float StartMeters = 0.15f;
        const float StartDegrees = 25f;
        const float StopMeters = 0.01f;
        const float StopDegrees = 2f;
        const float Rate = 4f; // 1/s

        /// <summary>One frame of following `target`; `following` carries the glide between frames.</summary>
        public static void Step(Transform t, Vector3 position, Quaternion rotation, ref bool following, float deltaSeconds)
        {
            var distance = Vector3.Distance(t.position, position);
            var angle = Quaternion.Angle(t.rotation, rotation);
            if (!following && (distance > StartMeters || angle > StartDegrees)) following = true;
            if (!following) return;
            var k = 1f - Mathf.Exp(-Rate * deltaSeconds);
            t.SetPositionAndRotation(Vector3.Lerp(t.position, position, k), Quaternion.Slerp(t.rotation, rotation, k));
            if (distance < StopMeters && angle < StopDegrees) following = false;
        }

        /// <summary>The head's yaw-only forward (straight ahead when looking straight up or down).</summary>
        public static Vector3 Flat(Transform head)
        {
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            return forward.normalized;
        }
    }
}
