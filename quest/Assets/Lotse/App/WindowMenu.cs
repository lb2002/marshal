using System;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    public enum WindowMenuItem { Further, Closer, Larger, Smaller, ScreenSetup, Replace, Settings, Halt, Close, GuidedTest }

    /// <summary>
    /// The Menu (U8; a sheet since U10, because a panel dropping over the conversation read as clutter on the device): a
    /// centred sheet inside the window. Behind it a scrim covers the window's body, dense enough that the content is not
    /// readable through it, and stops the ray. The sheet has the title "Menu" and a two-column grid of equal, H-high
    /// buttons under two Small captions: "Window" (Further, Closer, Larger, Smaller, Place again, Set up display) and
    /// "Marshal" (Settings, Guided test, Halt all agents, Close). "Halt all agents" is amber and asks first, in the
    /// window's band. Every button is a PokeButton: poked or ray-clicked under the click-versus-hold rule. Nothing on it
    /// approves or denies anything, and nothing can be approved while it is open (U8b, unchanged). Origin: the centre
    /// of the window's body.
    /// </summary>
    public sealed class WindowMenu : MonoBehaviour
    {
        static readonly Vector2 Button = new Vector2(UiMetrics.MenuButtonWidth, Theme.H);
        const float Gap = Theme.ControlGap;
        const float Pad = Theme.Pad;
        public const float Width = 2f * UiMetrics.MenuButtonWidth + Gap + 2f * Pad;
        const float TitleRow = Theme.Row;
        const float CaptionRow = 3f * Theme.S;
        /// <summary>
        /// The scrim's density: the window's own colour, opaque (U11). At 0.975 the white text behind it still came
        /// through as readable grey (blended in linear light, 2.5 % of white is a sixth of the way to white on screen).
        /// </summary>
        public const float ScrimAlpha = 1f;
        public const string Title = "Menu";
        public const string WindowCaption = "Window";
        public const string AppCaption = Texts.ProductName;

        Action<WindowMenuItem> _onItem;
        GameObject _main;
        MeshRenderer _background;

        /// <summary>The window's question band (U9): "Halt all agents?" is asked there, not in the menu.</summary>
        public WindowBand Band { get; set; }
        /// <summary>The band came or went (the window makes room for it).</summary>
        public Action BandChanged { get; set; }
        public const string HaltTag = "halt";

        public bool IsOpen => gameObject.activeSelf;
        public bool AskingHalt => Band != null && Band.IsShown && Band.Tag == HaltTag;
        public PokeButton FurtherButton { get; private set; }
        public PokeButton CloserButton { get; private set; }
        public PokeButton LargerButton { get; private set; }
        public PokeButton SmallerButton { get; private set; }
        public PokeButton ScreenButton { get; private set; }
        public PokeButton ReplaceButton { get; private set; }
        public PokeButton SettingsButton { get; private set; }
        public PokeButton HaltButton { get; private set; }
        /// <summary>"Guided test" (U9).</summary>
        public PokeButton TestButton { get; private set; }
        public PokeButton CloseButton { get; private set; }
        public PokeButton HaltYesButton => AskingHalt ? Band.Buttons[0] : null;
        public PokeButton HaltCancelButton => AskingHalt ? Band.Buttons[1] : null;
        /// <summary>Whether "Halt all agents" is offered (a session plays and is not halted); set by the app.</summary>
        public bool HaltOffered { get; set; } = true;

        public MeshRenderer Scrim { get; private set; }
        /// <summary>The sheet's height.</summary>
        public float Height { get; private set; }

        /// <param name="centre">The centre of the window's body, in the parent's local metres.</param>
        /// <param name="body">The body's size: the scrim covers it.</param>
        public static WindowMenu Create(Transform parent, Vector3 centre, Vector2 body, Action<WindowMenuItem> onItem)
        {
            var m = Ui.Child(parent, "WindowMenu", centre).AddComponent<WindowMenu>();
            m._onItem = onItem;
            var groupA = 3f * Button.y + 2f * Gap;
            var groupB = 2f * Button.y + Gap;
            var height = Pad + TitleRow + 2f * Theme.S + CaptionRow + Theme.S + groupA + 3f * Theme.S + CaptionRow + Theme.S + groupB + Pad;
            m.Height = height;
            var x0 = -(Button.x + Gap) * 0.5f;
            var x1 = (Button.x + Gap) * 0.5f;
            var left = -Width * 0.5f + Pad;
            var textW = Width - 2f * Pad;
            const float z = -0.002f;
            m._main = Ui.Child(m.transform, "Main", Vector3.zero);
            var t = m._main.transform;
            var y = height * 0.5f - Pad;
            var title = Ui.Text(t, "Title", Title, Vector3.zero, textW, Theme.Title, Theme.Text, TextAlignmentOptions.Left);
            Ui.Middle(title, textW, TitleRow, TextAlignmentOptions.Left);
            title.transform.localPosition = new Vector3(left + textW * 0.5f, y - TitleRow * 0.5f, z);
            y -= TitleRow + 2f * Theme.S;
            y = Caption(t, WindowCaption, left, textW, y, z);
            float Row(int i) => y - Button.y * 0.5f - i * (Button.y + Gap);
            m.FurtherButton = PokeButton.Create(t, Texts.Further, new Vector3(x0, Row(0), z), Button, () => m.Fire(WindowMenuItem.Further));
            m.CloserButton = PokeButton.Create(t, Texts.Closer, new Vector3(x1, Row(0), z), Button, () => m.Fire(WindowMenuItem.Closer));
            m.LargerButton = PokeButton.Create(t, Texts.Larger, new Vector3(x0, Row(1), z), Button, () => m.Fire(WindowMenuItem.Larger));
            m.SmallerButton = PokeButton.Create(t, Texts.Smaller, new Vector3(x1, Row(1), z), Button, () => m.Fire(WindowMenuItem.Smaller));
            m.ReplaceButton = PokeButton.Create(t, Texts.Replace, new Vector3(x0, Row(2), z), Button, () => m.Fire(WindowMenuItem.Replace));
            m.ScreenButton = PokeButton.Create(t, Texts.ScreenSetup, new Vector3(x1, Row(2), z), Button, () => m.Fire(WindowMenuItem.ScreenSetup));
            y -= groupA + 3f * Theme.S;
            y = Caption(t, AppCaption, left, textW, y, z);
            m.SettingsButton = PokeButton.Create(t, Texts.Settings, new Vector3(x0, Row(0), z), Button, () => m.Fire(WindowMenuItem.Settings));
            m.TestButton = PokeButton.Create(t, GuidedTest.MenuItem, new Vector3(x1, Row(0), z), Button, () => m.Fire(WindowMenuItem.GuidedTest));
            m.HaltButton = PokeButton.Create(t, Texts.HaltAll, new Vector3(x0, Row(1), z), Button, m.AskHalt, background: Ui.ToColor(Look.Amber, 1f));
            m.HaltButton.GetComponentInChildren<TextMeshPro>().color = Ui.ToColor(Look.OnPrimary); // dark on amber
            m.CloseButton = PokeButton.Create(t, Texts.Close, new Vector3(x1, Row(1), z), Button, () => m.Fire(WindowMenuItem.Close));
            // the scrim stands a little in front of the panel and is 2 x S smaller on each side, so that from the seat it
            // never shows beyond the window's edge (the window's own padding there is empty anyway).
            // the sheet (a panel: it stops the ray), and behind it the scrim over the window's body (drawn after the
            // content's text, so it covers it; the sheet in front of it writes depth and stays clear)
            m._background = Ui.Panel(m.transform, "Background", Vector3.zero, new Vector2(Width, height), Theme.Card);
            m.Scrim = Theme.Shape(m.transform, "Scrim", new Vector3(0f, 0f, 0.003f), body, Ui.ToColor(Look.Graphite, ScrimAlpha), Ui.TransparentAt(ScrimQueue));
            m.Scrim.gameObject.AddComponent<RayBlocker>();
            m.gameObject.SetActive(false);
            return m;
        }

        /// <summary>After every text of the content (TextMeshPro draws at 3000).</summary>
        const int ScrimQueue = 3100;

        static float Caption(Transform t, string caption, float left, float width, float y, float z)
        {
            var c = Ui.Text(t, "Caption " + caption, caption, Vector3.zero, width, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            Ui.Middle(c, width, CaptionRow, TextAlignmentOptions.Left);
            c.transform.localPosition = new Vector3(left + width * 0.5f, y - CaptionRow * 0.5f, z);
            return y - CaptionRow - Theme.S;
        }

        public void Open()
        {
            if (IsOpen) return;
            _main.SetActive(true);
            gameObject.SetActive(true);
            HaltButton.SetInteractable(HaltOffered);
        }

        public void Hide()
        {
            if (!IsOpen) return;
            gameObject.SetActive(false);
        }

        void Fire(WindowMenuItem item) => _onItem?.Invoke(item);

        /// <summary>"Halt all agents": the menu closes and the window asks in its band; only "Yes" there halts.</summary>
        void AskHalt()
        {
            if (!HaltOffered || Band == null) return;
            Hide();
            Band.Show(HaltTag, null, Texts.HaltAsk, false, (Texts.Yes, ConfirmHalt), (Texts.Cancel, CancelHalt));
            BandChanged?.Invoke();
        }

        void ConfirmHalt()
        {
            if (!AskingHalt) return; // only right after "Halt all agents"
            Band.Hide();
            BandChanged?.Invoke();
            Fire(WindowMenuItem.Halt);
        }

        void CancelHalt()
        {
            if (!AskingHalt) return;
            Band.Hide();
            BandChanged?.Invoke();
        }
    }
}
