using UnityEngine;

namespace Lotse
{
    // Puts this object once in front of the user's head (yaw only), facing them.
    // Waits a few frames so the head pose is valid after startup.
    public class PlaceInFront : MonoBehaviour
    {
        [SerializeField] float distance = 0.8f;
        [SerializeField] float heightOffset = 0f;
        [SerializeField] int waitFrames = 10;

        Transform head;
        int frames;

        void Start()
        {
            head = Camera.main != null ? Camera.main.transform : null;
        }

        void LateUpdate()
        {
            if (head == null || ++frames < waitFrames)
                return;

            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            forward.Normalize();

            transform.position = head.position + forward * distance + Vector3.up * heightOffset;
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            Debug.Log($"[Lotse] text placed at {transform.position} (head {head.position})");
            enabled = false;
        }
    }
}
