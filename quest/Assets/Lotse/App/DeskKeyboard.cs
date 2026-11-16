using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The desk keyboard (U4b): our own QWERTY keyboard lying flat on the desk, 0.30 m wide, its back edge raised so it
    /// tilts 10° toward the user. Its keys are poke buttons that need a short dwell and a rising edge (PokeButton.MakeKey),
    /// go down into the board when pressed and click softly when the sound is on. DeskKeys holds the layout and the
    /// typing; the ReplyController decides what a key does. It never sends by itself: its "Senden" is the explicit
    /// Senden and is only enabled when sending is allowed.
    /// </summary>
    public sealed class DeskKeyboard : MonoBehaviour
    {
        public const float Width = 0.30f;
        /// <summary>The back edge is raised so the board tilts this much toward the user.</summary>
        public const float TiltDegrees = 10f;
        public const float Unit = Width / 12f;
        const float KeyGap = 0.003f;
        const float Pad = 0.008f;
        public const double DwellMs = 60;
        public const double RepeatMs = 220;
        public const float Travel = 0.003f;

        readonly List<(PokeButton button, DeskKey key)> _keys = new List<(PokeButton, DeskKey)>();
        Transform _board;
        bool _numbers;
        bool _sendEnabled = true;

        public event Action<DeskKey> KeyPressed;

        public bool Shown => _board != null && _board.gameObject.activeSelf;
        public IReadOnlyList<(PokeButton button, DeskKey key)> Keys => _keys;

        public static DeskKeyboard Create(Transform parent)
        {
            var k = Ui.Child(parent, "DeskKeyboard", Vector3.zero).AddComponent<DeskKeyboard>();
            return k;
        }

        /// <summary>The key with this label (as shown), or null.</summary>
        public PokeButton Key(string label)
        {
            foreach (var k in _keys)
                if (k.button.Label == label) return k.button;
            return null;
        }

        public bool Owns(PokeButton b) => b != null && b.transform.IsChildOf(transform);

        /// <summary>
        /// Lays the keyboard at `pose`: its position on the desk and its rotation with forward pointing away from the user
        /// (flat on the desk); the tilt is added here.
        /// </summary>
        public void Show(Pose pose, DeskKeys keys)
        {
            var away = Vector3.ProjectOnPlane(pose.rotation * Vector3.forward, Vector3.up);
            if (away.sqrMagnitude < 1e-6f) away = Vector3.forward;
            away.Normalize();
            var tilt = TiltDegrees * Mathf.Deg2Rad;
            // the face looks up and a little toward the user; its top edge (local up) runs away from the user, rising
            var faceNormal = Vector3.up * Mathf.Cos(tilt) - away * Mathf.Sin(tilt);
            var up = away * Mathf.Cos(tilt) + Vector3.up * Mathf.Sin(tilt);
            transform.SetPositionAndRotation(pose.position, Quaternion.LookRotation(-faceNormal, up));
            Build(keys);
            Appear.Play(_board.gameObject);
        }

        public void Hide()
        {
            if (_board == null) return;
            _board.gameObject.SetActive(false); // its keys leave the poke registry at once
            Destroy(_board.gameObject);
            _board = null;
            _keys.Clear();
        }

        /// <summary>The labels after a shift or a layer change (the layer rebuilds the keys).</summary>
        public void Relabel(DeskKeys keys)
        {
            if (!Shown) return;
            if (keys.Numbers != _numbers)
            {
                Build(keys);
                return;
            }
            foreach (var k in _keys)
            {
                k.button.SetLabel(keys.Label(k.key));
                if (k.key.Kind == DeskKeyKind.Shift) k.button.SetChosen(keys.Shift);
            }
        }

        /// <summary>Its "Senden" can be pressed only when sending is allowed (the same rules as the input bar's).</summary>
        public void SetSendEnabled(bool on)
        {
            _sendEnabled = on;
            foreach (var k in _keys)
                if (k.key.Kind == DeskKeyKind.Send) k.button.SetInteractable(on);
        }

        void Build(DeskKeys keys)
        {
            if (_board != null)
            {
                _board.gameObject.SetActive(false);
                Destroy(_board.gameObject);
            }
            _keys.Clear();
            _numbers = keys.Numbers;
            _board = Ui.Child(transform, "Board", Vector3.zero).transform;
            var rows = keys.Rows;
            var depth = rows.Count * Unit;
            Ui.Panel(_board, "Base", new Vector3(0f, 0f, 0.003f), new Vector2(Width + 2f * Pad, depth + 2f * Pad), Theme.KeyboardBase);
            for (var r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var units = 0f;
                foreach (var k in row) units += k.Width;
                var x = -units * Unit * 0.5f;
                var y = depth * 0.5f - (r + 0.5f) * Unit;
                foreach (var k in row)
                {
                    var w = k.Width * Unit;
                    var key = k;
                    var send = k.Kind == DeskKeyKind.Send;
                    var b = PokeButton.Create(_board, keys.Label(k), new Vector3(x + w * 0.5f, y, -0.002f), new Vector2(w - KeyGap, Unit - KeyGap),
                        () => KeyPressed?.Invoke(key), interactable: !send || _sendEnabled, primary: send, cornerRadius: 0.004f);
                    b.MakeKey(DwellMs, RepeatMs, Travel);
                    if (k.Kind == DeskKeyKind.Shift) b.SetChosen(keys.Shift);
                    _keys.Add((b, k));
                    x += w;
                }
            }
        }
    }
}
