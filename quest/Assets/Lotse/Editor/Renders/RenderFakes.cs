using System;
using System.Collections.Generic;
using Lotse.App;
using Lotse.Core;
using UnityEngine;

namespace Lotse.EditorTools
{
    /// <summary>
    /// Stand-ins for the headset, used only by the offscreen renders: scripted hands, a desk anchor that is already
    /// saved (so the app lands on the desk at once), a room with nothing to hit, prefs in memory, a keyboard the
    /// script types into, and a socket that never connects. Nothing here touches the network or the device.
    /// </summary>
    static class RenderFakes
    {
        /// <summary>Both hands as the script sets them, read by AppRoot once per frame.</summary>
        public sealed class Hands : IHandSource
        {
            public HandFrame Left = HandFrame.Untracked;
            public HandFrame Right = HandFrame.Untracked;

            public void Read(Vector3 head, out HandFrame left, out HandFrame right)
            {
                left = Left;
                right = Right;
            }
        }

        /// <summary>A desk anchor saved in an earlier run, localised at once at the given pose.</summary>
        public sealed class SavedDesk : IDeskAnchors
        {
            readonly Vector3 _position;
            readonly Quaternion _rotation;

            public SavedDesk(Vector3 position, Quaternion rotation)
            {
                _position = position;
                _rotation = rotation;
            }

            public bool HasSaved => true;
            public void LoadSaved(Action<Transform> done) => done(Create(_position, _rotation));

            public Transform Create(Vector3 position, Quaternion rotation)
            {
                var go = new GameObject("RenderDeskAnchor");
                go.transform.SetPositionAndRotation(position, rotation);
                return go.transform;
            }
        }

        /// <summary>Raycasts never hit: placement is never needed, the saved anchor is used.</summary>
        public sealed class EmptyRoom : IDeskRaycaster
        {
            public RaycastAvailability Availability => RaycastAvailability.Ready;
            public void Begin() { }
            public void End() { }

            public DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
            {
                point = normal = Vector3.zero;
                return DeskRayStatus.NoHit;
            }
        }

        /// <summary>
        /// A wall plane `distance` in front of the eye, square to the flat direction `toward` (the renders' stand-in for
        /// the wall behind the desk; it is not drawn): rays that reach it hit, everything else misses.
        /// </summary>
        public sealed class WallRoom : IDeskRaycaster
        {
            readonly Vector3 _point;
            readonly Vector3 _normal;

            public WallRoom(Vector3 eye, Vector3 toward, float distance)
            {
                var flat = Vector3.ProjectOnPlane(toward, Vector3.up).normalized;
                _normal = -flat;
                _point = eye + flat * distance;
            }

            public RaycastAvailability Availability => RaycastAvailability.Ready;
            public void Begin() { }
            public void End() { }

            public DeskRayStatus Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
            {
                point = normal = Vector3.zero;
                var denom = Vector3.Dot(direction, _normal);
                if (denom > -1e-4f) return DeskRayStatus.NoHit;
                var t = Vector3.Dot(_point - origin, _normal) / denom;
                if (t < 0f || t > maxDistance) return DeskRayStatus.NoHit;
                point = origin + direction * t;
                normal = _normal;
                return DeskRayStatus.Hit;
            }
        }

        public sealed class MemoryPrefs : IKeyValueStore
        {
            readonly Dictionary<string, string> _values = new Dictionary<string, string>();
            public string Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => _values[key] = value;
            public void Delete(string key) => _values.Remove(key);
        }

        /// <summary>The system keyboard as the script drives it: what is "typed" is set on Text.</summary>
        public sealed class Keyboard : IReplyKeyboard
        {
            public string Text { get; set; } = "";
            public KeyboardStatus Status { get; set; } = KeyboardStatus.Closed;

            public bool Open(string text)
            {
                Text = text;
                Status = KeyboardStatus.Visible;
                return true;
            }

            public void Close() => Status = KeyboardStatus.Closed;
        }

        /// <summary>A QR tracker that sees what the scene scripts (U11).</summary>
        public sealed class Qr : IQrSource
        {
            public readonly System.Collections.Generic.List<byte[]> Payloads = new System.Collections.Generic.List<byte[]>();
            public QrSourceState OnBegin = QrSourceState.Scanning;
            public QrSourceState State { get; private set; } = QrSourceState.Off;
            public void Begin() => State = OnBegin;
            public void End() => State = QrSourceState.Off;

            public void Read(System.Collections.Generic.List<byte[]> payloads)
            {
                payloads.Clear();
                if (State == QrSourceState.Scanning) payloads.AddRange(Payloads);
            }

            /// <summary>Where the code is in the scene, when the scene says so.</summary>
            public UnityEngine.Vector3? Place;
            public float Size = 0.05f;

            public bool TryPlace(byte[] payload, out UnityEngine.Vector3 position, out float size)
            {
                position = Place ?? default;
                size = Size;
                return Place.HasValue;
            }
        }

        /// <summary>A bridge connection that never opens (the renders never pair).</summary>
        public sealed class DeadSocket : ILinkSocket
        {
#pragma warning disable 67
            public event Action Opened;
            public event Action<string> Received;
            public event Action<int> Closed;
#pragma warning restore 67
            public void Connect() { }
            public bool Send(string text) => false;
            public void Close() { }
            public void Pump() { }
        }
    }
}
