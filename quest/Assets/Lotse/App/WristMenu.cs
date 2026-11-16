using System;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The wrist menu: a small panel above the palm the user turns up toward the head (WristMenuLogic), with
    /// "Neu platzieren" and "Einstellungen" (the connection with "Trennen", the look, the sound: SettingsPanel). The
    /// other hand pokes it (or points at it with its ray). Nothing of it is in view otherwise. Its buttons are switched off while it is hidden, so they leave the
    /// poke registry and arm again only with a fresh press (PokeLogic's rising edge) once it shows.
    /// </summary>
    public sealed class WristMenu : MonoBehaviour
    {
        /// <summary>
        /// U8: switched off. Palm up plus a pinch is the Quest's system gesture, so nothing in the app may depend on a palm-up
        /// pose; everything the wrist menu offered is in the header's Menu (WindowMenu). The code stays for a later revival.
        /// </summary>
        public const bool Enabled = false;

        static readonly Vector2 ButtonSize = new Vector2(0.15f, 0.034f);
        const float ButtonGap = 0.014f;
        const float Pad = 0.01f;

        readonly WristMenuLogic _logic = new WristMenuLogic();
        GameObject _panel;
        MeshRenderer _background;
        TMPro.TextMeshPro _question;
        Action _onHalt;
        bool _haltOffered;

        public MenuHand Shown => _logic.Shown;
        public PokeButton ReplaceButton { get; private set; }
        public PokeButton SettingsButton { get; private set; }
        /// <summary>"Set up display" (U5b): the three-tap display setup.</summary>
        public PokeButton ScreenButton { get; private set; }
        /// <summary>"Halt all agents" (U7), for when the stop gesture is not practical: it asks once before it halts.</summary>
        public PokeButton HaltButton { get; private set; }
        public PokeButton HaltYesButton { get; private set; }
        public PokeButton HaltCancelButton { get; private set; }
        /// <summary>"Halt all agents? Yes / Cancel" is asked.</summary>
        public bool AskingHalt { get; private set; }

        /// <summary>Whether "Halt all agents" is offered (a session runs and is not halted); set by the app each frame.</summary>
        public bool HaltOffered
        {
            get => _haltOffered;
            set
            {
                if (value == _haltOffered) return;
                _haltOffered = value;
                if (!value) AskingHalt = false;
                Arrange();
            }
        }

        public static WristMenu Create(Action onReplace, Action onSettings, Action onScreen = null, Action onHalt = null)
        {
            var menu = new GameObject("WristMenu").AddComponent<WristMenu>();
            menu._panel = Ui.Child(menu.transform, "Panel", Vector3.zero);
            var p = menu._panel.transform;
            menu._background = Ui.Panel(p, "Background", new Vector3(0f, 0f, 0.002f), Vector2.one, Ui.PanelColor);
            menu.ReplaceButton = PokeButton.Create(p, Texts.Replace, Vector3.zero, ButtonSize, onReplace);
            menu.SettingsButton = PokeButton.Create(p, Texts.Settings, Vector3.zero, ButtonSize, onSettings);
            menu.ScreenButton = PokeButton.Create(p, Texts.ScreenSetup, Vector3.zero, ButtonSize, onScreen);
            menu._onHalt = onHalt;
            menu.HaltButton = PokeButton.Create(p, Texts.HaltAll, Vector3.zero, ButtonSize, menu.AskHalt,
                background: Ui.ToColor(Look.Amber, 0.9f));
            menu.HaltButton.GetComponentInChildren<TMPro.TextMeshPro>().color = Ui.ToColor(Look.OnPrimary); // dark on amber
            var half = new Vector2((ButtonSize.x - ButtonGap) * 0.5f, ButtonSize.y);
            menu.HaltYesButton = PokeButton.Create(p, Texts.Yes, Vector3.zero, half, menu.ConfirmHalt);
            menu.HaltCancelButton = PokeButton.Create(p, Texts.Cancel, Vector3.zero, half, menu.CancelHalt);
            menu._question = Ui.Text(p, "Question", Texts.HaltAsk, Vector3.zero, ButtonSize.x, Theme.Body, Theme.Text, TMPro.TextAlignmentOptions.Top);
            menu.Arrange();
            menu._panel.SetActive(false);
            return menu;
        }

        /// <summary>Once per frame, before the buttons are probed. `allowed`: no card open and not placing.</summary>
        public void Step(double nowMs, bool allowed, in HandFrame left, in HandFrame right, Transform head)
        {
            var shown = _logic.Update(nowMs, allowed && Enabled, PalmOf(left), PalmOf(right), Ui.ToVec(head.position), Ui.ToVec(head.forward));
            if (shown == MenuHand.None)
            {
                if (_panel.activeSelf) _panel.SetActive(false);
                if (AskingHalt) CancelHalt(); // the question never waits in a hidden menu
                return;
            }
            var palm = shown == MenuHand.Left ? left.PalmCenter : right.PalmCenter;
            var at = palm + Vector3.up * Layout.MenuAbovePalmMeters;
            // facing the head, upright: the poking finger comes from the user's side
            var away = at - head.position;
            transform.SetPositionAndRotation(at, Quaternion.LookRotation(away.sqrMagnitude > 1e-6f ? away : Vector3.forward, Vector3.up));
            if (!_panel.activeSelf)
            {
                _panel.SetActive(true);
                Appear.Play(_panel);
            }
        }

        static Palm PalmOf(in HandFrame f) => f.PalmValid ? new Palm(Ui.ToVec(f.PalmCenter), Ui.ToVec(f.PalmNormal)) : Palm.None;

        void AskHalt()
        {
            if (!_haltOffered) return;
            AskingHalt = true;
            Arrange();
        }

        void ConfirmHalt()
        {
            if (!AskingHalt) return; // only right after "Halt all agents"
            AskingHalt = false;
            Arrange();
            _onHalt?.Invoke();
        }

        void CancelHalt()
        {
            AskingHalt = false;
            Arrange();
        }

        /// <summary>
        /// "Halt all agents" on top (while offered), then "Neu platzieren", "Set up display", "Einstellungen"; while the halt
        /// question is asked, only it and "Yes" / "Cancel". The background around them.
        /// </summary>
        void Arrange()
        {
            var asking = AskingHalt;
            Show(_question.gameObject, asking);
            Show(HaltYesButton.gameObject, asking);
            Show(HaltCancelButton.gameObject, asking);
            Show(HaltButton.gameObject, !asking && _haltOffered);
            Show(ReplaceButton.gameObject, !asking);
            Show(ScreenButton.gameObject, !asking);
            Show(SettingsButton.gameObject, !asking);
            float height;
            if (asking)
            {
                var qh = _question.rectTransform.sizeDelta.y;
                height = qh + ButtonGap + ButtonSize.y;
                var top0 = height * 0.5f;
                _question.transform.localPosition = new Vector3(0f, top0, -0.002f);
                var y = top0 - qh - ButtonGap - ButtonSize.y * 0.5f;
                var dx = (ButtonSize.x + ButtonGap) * 0.25f;
                HaltYesButton.transform.localPosition = new Vector3(-dx, y, -0.002f);
                HaltCancelButton.transform.localPosition = new Vector3(dx, y, -0.002f);
            }
            else
            {
                var rows = _haltOffered ? 4 : 3;
                height = rows * ButtonSize.y + (rows - 1) * ButtonGap;
                var top = height * 0.5f;
                Vector3 Row(int k) => new Vector3(0f, top - ButtonSize.y * (k + 0.5f) - k * ButtonGap, -0.002f);
                var first = _haltOffered ? 1 : 0;
                if (_haltOffered) HaltButton.transform.localPosition = Row(0);
                ReplaceButton.transform.localPosition = Row(first);
                ScreenButton.transform.localPosition = Row(first + 1);
                SettingsButton.transform.localPosition = Row(first + 2);
            }
            _background.transform.localScale = new Vector3(ButtonSize.x + 2f * Pad, height + 2f * Pad, 1f);
        }

        static void Show(GameObject go, bool on)
        {
            if (go.activeSelf != on) go.SetActive(on);
        }
    }
}
