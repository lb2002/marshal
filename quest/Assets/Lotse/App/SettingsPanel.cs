using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>What the settings panel shows.</summary>
    public readonly struct SettingsState
    {
        public readonly string Connection;
        public readonly bool CanDisconnect;
        public readonly bool Sound;
        /// <summary>"Ruhig": nothing moves.</summary>
        public readonly bool Calm;
        /// <summary>"Neu koppeln" was pressed: the panel asks "Wirklich neu koppeln?" first.</summary>
        public readonly bool ConfirmRepair;
        /// <summary>"Automatisch einklappen" (U6).</summary>
        public readonly bool AutoCollapse;

        public SettingsState(string connection, bool canDisconnect, bool sound, bool calm, bool confirmRepair = false, bool autoCollapse = true)
        {
            AutoCollapse = autoCollapse;
            Connection = connection;
            CanDisconnect = canDisconnect;
            Sound = sound;
            Calm = calm;
            ConfirmRepair = confirmRepair;
        }

        public bool Same(in SettingsState o) =>
            Connection == o.Connection && CanDisconnect == o.CanDisconnect && Sound == o.Sound && Calm == o.Calm &&
            ConfirmRepair == o.ConfirmRepair && AutoCollapse == o.AutoCollapse;
    }

    /// <summary>
    /// "Einstellungen", opened from the wrist menu: the connection (its state, "Trennen", "Neu koppeln"), the sound ("An" /
    /// "Aus"), the motion ("Normal" / "Ruhig": with Ruhig nothing moves) and "Automatisch einklappen" ("An" / "Aus", U6); the chosen option is drawn in the primary
    /// colour. U5a removed the look switch: there is one dark look. Stands in front of the workspace, centred on its
    /// origin; built again on each Show. Nothing here decides anything about a request.
    /// </summary>
    public sealed class SettingsPanel : MonoBehaviour
    {
        const float Width = 0.34f;
        const float Pad = Theme.Pad;
        const float SectionGap = 3f * Theme.S; // with the caption below it, more than the gap between controls
        static readonly Vector2 Half = new Vector2(0.162f, Theme.H);
        static readonly Vector2 CloseSize = new Vector2(0.162f, Theme.H);

        GameObject _root;
        Transform _motion;
        Transform _content;
        SettingsState _shown;

        public bool IsOpen => _root.activeSelf;
        public SettingsState Shown => _shown;
        public PokeButton DisconnectButton { get; private set; }
        public PokeButton RepairButton { get; private set; }
        public PokeButton NormalButton { get; private set; }
        public PokeButton CalmButton { get; private set; }
        public PokeButton SoundOnButton { get; private set; }
        public PokeButton SoundOffButton { get; private set; }
        public PokeButton CloseButton { get; private set; }
        /// <summary>"Automatisch einklappen": An / Aus.</summary>
        public PokeButton AutoCollapseOnButton { get; private set; }
        public PokeButton AutoCollapseOffButton { get; private set; }
        /// <summary>"Ja" and "Abbrechen" while "Neu koppeln" asks first.</summary>
        public PokeButton ConfirmRepairButton { get; private set; }
        public PokeButton CancelRepairButton { get; private set; }

        /// <summary>Every text on the panel, as shown (for tests).</summary>
        public List<string> Lines
        {
            get
            {
                var list = new List<string>();
                if (_content != null && IsOpen)
                    foreach (var t in _content.GetComponentsInChildren<TextMeshPro>(true)) list.Add(t.text);
                return list;
            }
        }

        public static SettingsPanel Create(Transform parent)
        {
            var p = Ui.Child(parent, "SettingsPanel", Vector3.zero).AddComponent<SettingsPanel>();
            p._root = Ui.Child(p.transform, "Panel", Vector3.zero);
            p._motion = Ui.Child(p._root.transform, "Motion", Vector3.zero).transform;
            p._root.SetActive(false);
            return p;
        }

        public void Show(in SettingsState state, Action onDisconnect, Action onRepair, Action onRepairConfirmed, Action onRepairCancelled,
            Action<bool> onSound, Action<bool> onCalm, Action<bool> onAutoCollapse, Action onClose, bool appear)
        {
            _shown = state;
            _root.SetActive(true);
            if (_content != null)
            {
                _content.gameObject.SetActive(false); // its buttons leave the poke registry now
                Destroy(_content.gameObject);
            }
            _content = Ui.Child(_motion, "Content", Vector3.zero).transform;
            var c = _content;
            var y = -Pad;
            var title = Ui.Text(c, "Title", Texts.Settings, new Vector3(0f, y, -0.001f), Width, Theme.Title, Theme.Text,
                TextAlignmentOptions.TopLeft);
            y -= title.rectTransform.sizeDelta.y + 0.006f;

            y = Section(c, Texts.SettingsConnection, y);
            var line = Ui.Text(c, "Connection", state.Connection, new Vector3(0f, y, -0.001f), Width, Theme.Body, Theme.Text,
                TextAlignmentOptions.TopLeft);
            Ui.CapLines(line, 2);
            y -= line.rectTransform.sizeDelta.y + 0.006f;
            if (state.ConfirmRepair)
            {
                // "Neu koppeln" asks first: the connection ends and the pairing panel opens
                var ask = Ui.Text(c, "ConfirmRepair", Texts.RepairConfirm, new Vector3(0f, y, -0.001f), Width, Theme.Body, Theme.WarnText,
                    TextAlignmentOptions.TopLeft);
                y -= ask.rectTransform.sizeDelta.y + 0.004f;
                ConfirmRepairButton = Pair(c, ref y, Texts.Yes, null, onRepairConfirmed, true, true, Texts.ReplyCancel, null, onRepairCancelled,
                    true, false, out var no);
                CancelRepairButton = no;
                DisconnectButton = null;
                RepairButton = null;
            }
            else
            {
                DisconnectButton = Pair(c, ref y, Texts.Disconnect, null, onDisconnect, state.CanDisconnect, false,
                    Texts.Repair, null, onRepair, true, false, out var repair);
                RepairButton = repair;
                ConfirmRepairButton = null;
                CancelRepairButton = null;
            }

            y -= SectionGap;
            y = Section(c, Texts.SettingsSound, y);
            SoundOnButton = Pair(c, ref y, Texts.SoundOn, null, () => onSound?.Invoke(true), true, state.Sound,
                Texts.SoundOff, null, () => onSound?.Invoke(false), true, !state.Sound, out var off);
            SoundOffButton = off;

            y -= SectionGap;
            y = Section(c, Texts.SettingsMotion, y);
            NormalButton = Pair(c, ref y, Texts.MotionNormal, null, () => onCalm?.Invoke(false), true, !state.Calm,
                Texts.MotionCalm, null, () => onCalm?.Invoke(true), true, state.Calm, out var calm);
            CalmButton = calm;

            y -= SectionGap;
            y = Section(c, OverviewText.AutoCollapse, y);
            AutoCollapseOnButton = Pair(c, ref y, Texts.SoundOn, null, () => onAutoCollapse?.Invoke(true), true, state.AutoCollapse,
                Texts.SoundOff, null, () => onAutoCollapse?.Invoke(false), true, !state.AutoCollapse, out var autoOff);
            AutoCollapseOffButton = autoOff;

            y -= Theme.ControlGap; // a row of controls above: the hit areas meet in the middle
            CloseButton = PokeButton.Create(c, Texts.CloseDetail, new Vector3(0f, y - CloseSize.y * 0.5f, -0.002f), CloseSize, onClose);
            y -= CloseSize.y + Pad;

            var h = -y;
            Ui.Panel(c, "Background", new Vector3(0f, -h * 0.5f, 0.002f), new Vector2(Width + 2f * Pad, h), Theme.GlassDense);
            c.localPosition = new Vector3(0f, h * 0.5f, 0f);
            if (appear) Appear.Play(_motion.gameObject);
        }

        public void Hide() => _root.SetActive(false);

        /// <summary>A section's caption ("Verbindung", "Ton", "Bewegung"), sentence case; returns the y below it.</summary>
        static float Section(Transform c, string caption, float y)
        {
            var t = Ui.Text(c, "Section " + caption, caption, new Vector3(0f, y, -0.001f), Width, Theme.Small, Theme.Muted,
                TextAlignmentOptions.TopLeft);
            return y - t.rectTransform.sizeDelta.y - 0.004f;
        }

        /// <summary>Two buttons side by side; a chosen one is drawn in the primary colour. Returns the left one.</summary>
        static PokeButton Pair(Transform c, ref float y, string leftLabel, string leftIcon, Action onLeft, bool leftOn, bool leftChosen,
            string rightLabel, string rightIcon, Action onRight, bool rightOn, bool rightChosen, out PokeButton right)
        {
            var cy = y - Half.y * 0.5f;
            var dx = (Width - Half.x) * 0.5f;
            var left = PokeButton.Create(c, leftLabel, new Vector3(-dx, cy, -0.002f), Half, onLeft, interactable: leftOn, icon: leftIcon,
                primary: leftChosen);
            right = PokeButton.Create(c, rightLabel, new Vector3(dx, cy, -0.002f), Half, onRight, interactable: rightOn, icon: rightIcon,
                primary: rightChosen);
            y -= Half.y;
            return left;
        }
    }
}
