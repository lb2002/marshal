using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Android;

namespace Lotse.App
{
    public enum QrSourceState
    {
        /// <summary>Not scanning.</summary>
        Off,
        /// <summary>The system asks the user for the spatial data permission.</summary>
        Asking,
        /// <summary>The tracker runs (or is starting).</summary>
        Scanning,
        /// <summary>The user said no (or closed the question).</summary>
        Denied,
        /// <summary>This device or runtime cannot track QR codes.</summary>
        Unavailable,
    }

    /// <summary>
    /// What reads QR codes in the room (U11): MR Utility Kit's trackables on the headset, a script in tests. It only
    /// hands out raw payload bytes; what they mean is PairLink's business, what happens is QrPairing's.
    /// </summary>
    public interface IQrSource
    {
        QrSourceState State { get; }
        /// <summary>Scan mode opened: ask for the permission if it is missing (only now), then start the tracker.</summary>
        void Begin();
        /// <summary>Scan mode closed: stop the tracker.</summary>
        void End();
        /// <summary>The payloads of the QR codes tracked right now (cleared first). Empty unless Scanning.</summary>
        void Read(List<byte[]> payloads);
        /// <summary>Where the code with this payload (one of the last Read) is, and its side in metres, when the tracker knows.</summary>
        bool TryPlace(byte[] payload, out Vector3 position, out float size);
    }

    /// <summary>
    /// IQrSource over MR Utility Kit 209 (MRUK.SceneSettings.TrackerConfiguration.QRCodeTrackingEnabled, MRUKTrackable's
    /// MarkerPayloadBytes). Needs com.oculus.permission.USE_SCENE (the "Spatial data" permission), which the manifest
    /// declares already for desk placement; it is requested here only when the user opens scan mode.
    /// MRUK is a scene singleton the app does not otherwise use: one is made here on first use, with scene loading and
    /// world lock off (it must not ask for the permission by itself at start, nor move the tracking space). It is
    /// switched off whenever scan mode closes: MRUK.OnDisable stops every tracker at once.
    /// </summary>
    public sealed class MrukQrSource : IQrSource
    {
        const string ScenePermission = OVRPermissionsRequester.ScenePermission;
        /// <summary>The tracker has this long to start before it counts as unavailable.</summary>
        const float StartSeconds = 8f;

        enum Answer { None, Granted, Denied, Dismissed }

        GameObject _host;
        int _answer = (int)Answer.None;
        float _beganAt;
        readonly List<MRUKTrackable> _trackables = new List<MRUKTrackable>();
        readonly List<(byte[] payload, MRUKTrackable trackable)> _seen = new List<(byte[], MRUKTrackable)>();

        public bool TryPlace(byte[] payload, out Vector3 position, out float size)
        {
            position = default;
            size = 0f;
            foreach (var (bytes, t) in _seen)
            {
                if (!ReferenceEquals(bytes, payload) || t == null) continue;
                position = t.transform.position;
                var rect = t.PlaneRect;
                size = rect.HasValue ? Mathf.Max(rect.Value.width, rect.Value.height) : 0f;
                return true;
            }
            return false;
        }

        public QrSourceState State { get; private set; } = QrSourceState.Off;

        public void Begin()
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                State = QrSourceState.Unavailable;
                return;
            }
            System.Threading.Interlocked.Exchange(ref _answer, (int)Answer.None);
            if (Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                StartTracker();
                return;
            }
            Debug.Log("[Lotse] asking for the spatial data permission (" + ScenePermission + ") to scan the pairing code");
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => System.Threading.Interlocked.Exchange(ref _answer, (int)Answer.Granted);
            callbacks.PermissionDenied += _ => System.Threading.Interlocked.Exchange(ref _answer, (int)Answer.Denied);
            callbacks.PermissionRequestDismissed += _ => System.Threading.Interlocked.Exchange(ref _answer, (int)Answer.Dismissed);
            State = QrSourceState.Asking;
            Permission.RequestUserPermission(ScenePermission, callbacks);
        }

        void StartTracker()
        {
            if (_host == null && MRUK.Instance == null)
            {
                _host = new GameObject("LotseQrTracker");
                _host.SetActive(false); // MRUK.Awake reads its settings: they are set first
                var mruk = _host.AddComponent<MRUK>();
                mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false };
                mruk.EnableWorldLock = false;
            }
            if (_host != null) _host.SetActive(true);
            var instance = MRUK.Instance;
            if (instance == null || instance.SceneSettings == null || !instance.QRCodeTrackingSupported)
            {
                Debug.Log("[Lotse] QR code tracking is not available here");
                if (_host != null) _host.SetActive(false);
                State = QrSourceState.Unavailable;
                return;
            }
            var config = instance.SceneSettings.TrackerConfiguration;
            config.QRCodeTrackingEnabled = true;
            instance.SceneSettings.TrackerConfiguration = config;
            _beganAt = Time.realtimeSinceStartup;
            State = QrSourceState.Scanning;
        }

        public void End()
        {
            var instance = MRUK.Instance;
            if (instance != null && instance.SceneSettings != null)
            {
                var config = instance.SceneSettings.TrackerConfiguration;
                config.QRCodeTrackingEnabled = false;
                instance.SceneSettings.TrackerConfiguration = config;
            }
            if (_host != null) _host.SetActive(false); // MRUK.OnDisable: every tracker stops now
            State = QrSourceState.Off;
        }

        public void Read(List<byte[]> payloads)
        {
            payloads.Clear();
            _seen.Clear();
            if (State == QrSourceState.Asking)
            {
                var answer = (Answer)System.Threading.Interlocked.Exchange(ref _answer, (int)Answer.None);
                if (answer == Answer.Granted || (answer == Answer.None && Permission.HasUserAuthorizedPermission(ScenePermission))) StartTracker();
                else if (answer != Answer.None) State = QrSourceState.Denied;
                return;
            }
            if (State != QrSourceState.Scanning) return;
            var instance = MRUK.Instance;
            if (instance == null)
            {
                State = QrSourceState.Unavailable;
                return;
            }
            if (!instance.TrackerConfiguration.QRCodeTrackingEnabled)
            {
                // requested, not (yet) running: MRUK tries once; when that fails the runtime cannot do it
                if (Time.realtimeSinceStartup - _beganAt > StartSeconds)
                {
                    Debug.Log("[Lotse] the QR code tracker did not start");
                    End();
                    State = QrSourceState.Unavailable;
                }
                return;
            }
            instance.GetTrackables(_trackables);
            foreach (var t in _trackables)
            {
#pragma warning disable 0618 // the type moved to MRUK, the enum value is the one MRUKTrackable reports
                if (t == null || !t.IsTracked || t.TrackableType != OVRAnchor.TrackableType.QRCode) continue;
#pragma warning restore 0618
                var bytes = t.MarkerPayloadBytes;
                if (bytes == null || bytes.Length == 0) continue;
                // a string payload arrives with its terminating zero (MRUK strips it for MarkerPayloadString too)
                if (bytes[bytes.Length - 1] == 0)
                {
                    var cut = new byte[bytes.Length - 1];
                    System.Array.Copy(bytes, cut, cut.Length);
                    bytes = cut;
                }
                payloads.Add(bytes);
                _seen.Add((bytes, t));
            }
        }
    }
}
