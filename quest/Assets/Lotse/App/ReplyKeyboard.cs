using UnityEngine;

namespace Lotse.App
{
    public enum KeyboardStatus { Closed, Visible, Done, Canceled }

    /// <summary>Where a reply's text is typed or dictated: the Quest system keyboard, or a fake in tests.</summary>
    public interface IReplyKeyboard
    {
        /// <summary>Opens the keyboard holding `text` (the user's own kept draft, else empty); false when there is none.</summary>
        bool Open(string text);
        /// <summary>The text as typed so far.</summary>
        string Text { get; }
        KeyboardStatus Status { get; }
        void Close();
    }

    /// <summary>
    /// The Quest system keyboard: Unity's TouchScreenKeyboard, which Meta's OpenXR runtime shows as the system overlay
    /// keyboard when the manifest declares `oculus.software.overlay_keyboard` (OVRProjectConfig.requiresSystemKeyboard;
    /// see com.meta.xr.sdk.core Editor/OVRManifestPreprocessor.cs and the SDK's own use in
    /// Scripts/ImmersiveDebugger/DevAgent/Panel/LLMDialogPanel.cs). It has a dictation microphone and takes Bluetooth
    /// keyboards. In the Editor and on PC `TouchScreenKeyboard.isSupported` is false: Open returns false.
    /// </summary>
    public sealed class SystemKeyboard : IReplyKeyboard
    {
        TouchScreenKeyboard _keyboard;

        public bool Open(string text)
        {
            if (!TouchScreenKeyboard.isSupported) return false;
            _keyboard = TouchScreenKeyboard.Open(text ?? "", TouchScreenKeyboardType.Default, autocorrection: true, multiline: true,
                secure: false, alert: false, textPlaceholder: Texts.ReplyPlaceholder, characterLimit: ReplyLogic.MaxChars);
            return _keyboard != null;
        }

        public string Text => _keyboard != null ? _keyboard.text ?? "" : "";

        public KeyboardStatus Status
        {
            get
            {
                if (_keyboard == null) return KeyboardStatus.Closed;
                switch (_keyboard.status)
                {
                    case TouchScreenKeyboard.Status.Visible: return KeyboardStatus.Visible;
                    case TouchScreenKeyboard.Status.Done: return KeyboardStatus.Done;
                    case TouchScreenKeyboard.Status.Canceled: return KeyboardStatus.Canceled;
                    default: return KeyboardStatus.Closed; // LostFocus
                }
            }
        }

        public void Close()
        {
            if (_keyboard == null) return;
            if (_keyboard.status == TouchScreenKeyboard.Status.Visible) _keyboard.active = false;
            _keyboard = null;
        }
    }
}
