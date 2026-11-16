# Quest input and pairing research (2026-11-12)

Scope: Lotse, Unity 6.6, Meta XR SDK 209 (core, interaction.ovr, mrutilitykit), OpenXR 1.19.0, Quest 3. Read-only research. "Verified" means seen in a local file or a fetched page. "Unverified" is stated explicitly.
Local root: `C:\Users\linus\Documents\MetaHackathon\Lotse-quest\quest\Library\PackageCache` (abbreviated PC).

## 1. OVRVirtualKeyboard

- **Not in our project, and effectively not in SDK 209.** A search of PC for `OVRVirtualKeyboard` in core, interaction, interaction.ovr and mrutilitykit returned zero hits. The feature lived in Meta XR Core SDK (the docs require Core v54 or later), and it is gone from the 209 core package.
- **Meta deprecated it.** The docs say "Virtual Keyboard as a feature has been deprecated in favor of the system keyboard overlay in Unity." https://developers.meta.com/horizon/documentation/unity/VK-unity-custom-integrations
- **For reference only, the old API:**
  - World placement: move the GameObject (uniform scale only) or call `UseSuggestedLocation`. Direct touch with hands is recommended over raycast.
  - Text events: `CommitText`, `Backspace`, `Enter`, `KeyboardShown`, `KeyboardHidden`, plus `ChangeTextContext` for suggestions.
  - The page does not cover manifest flags or a dictation/mic key.
  - Underlying OpenXR extension: `XR_META_virtual_keyboard` (https://registry.khronos.org/OpenXR/specs/1.1/man/html/XR_META_virtual_keyboard.html).
- **Versus our TouchScreenKeyboard overlay:** the overlay is a system panel. The app loses input focus while it is open (`OVRManager.InputFocusLost` / `InputFocusAcquired`). It cannot be positioned by the app, only `TouchScreenKeyboardType.Default` is supported, and the text comes from `TouchScreenKeyboard.text`. The old virtual keyboard was an in-scene object you could pose. That is now unsupported in 209, so a flat desk keyboard has no first-party route. Source: https://developers.meta.com/horizon/documentation/unity/unity-keyboard-overlay/
- **Manifest flag for the overlay (verified locally):** `oculus.software.overlay_keyboard`, written when `requiresSystemKeyboard` is set. See `PC/com.meta.xr.sdk.core@0a6ebbc97c87/Editor/OVRManifestPreprocessor.cs` line 781.
- **Alternative for a custom desk keyboard:** build one from Interaction SDK poke buttons (interaction.ovr is installed). This is custom work, roughly 1 to 2 days for a basic QWERTY. I did not verify a ready-made sample in the 209 package.

## 2. Speech-to-text

- **Android `SpeechRecognizer` on Horizon OS: unverified and unlikely to be reliable.** I found no official statement. Forum reports say Android speech plugins that work on phones did not work on Quest, and the native recognizer's availability is unanswered: https://communityforums.atmeta.com/discussions/dev-unity/android-native-speech-recognizer/843305 and https://communityforums.atmeta.com/discussions/dev-quest/on-device-speech-recognition-on-the-quest-with-unity/777619. Whether it runs offline or online is therefore also unknown. Only a device test (`SpeechRecognizer.isRecognitionAvailable`) settles it.
- **Meta Voice SDK dictation:**
  - It is free, and it uses Wit.ai ("Wit.ai is easy to sign up for, and it's free"): https://developers.meta.com/horizon/documentation/unity/voice-sdk-overview/
  - The Unity tutorials use the wit.ai Server Access Token pasted into the Wit Configuration: https://developers.meta.com/horizon/documentation/unity/unity-tutorial-basic-voice-input/
  - Whether a Meta account is needed was not stated. A wit.ai app does need a sign-in, which is itself an account, so it is not account-free.
  - Embedding the server token in the build is what the Unity flow does. That is a leak risk for a shipped app.
  - Dictation needs internet. No offline mode was documented.
  - It is soft-deprecated in All-in-One v85. It now ships standalone as `com.meta.xr.sdk.voice` (dictation is a separate package). It is not in our PC (no voice package found).
  - There is a history of Unity 6 dictation failures. They were fixed in SDK 74, but re-test.
  - A Voice SDK microphone problem was reported on non-English OS (https://communityforums.atmeta.com/discussions/dev-unity/meta-voice-sdk-on-quest-3-with-non-english-os---no-microphone-input/1317579).
- **System keyboard dictation:** the overlay exposes voice dictation as a system feature. The page describes no app API to show or hide a mic key. Design guidance mentions a mic button. The user must tap it in the overlay. Source: https://developers.meta.com/horizon/documentation/unity/unity-keyboard-overlay/
- **Simplest legitimate path for "hold to talk":**
  - Quickest and zero-account: keep the overlay and tell the user to tap its mic key (no custom button, no push-to-talk).
  - For a real hold-to-talk button: record with `Microphone` (permission `android.permission.RECORD_AUDIO`, constant in `OVRPermissionsRequester.cs` line 102) and send the audio to our own bridge server for transcription. This needs no third-party token on the headset, but it needs a server-side STT. I did not verify a specific STT service.
  - Voice SDK dictation is the fallback if we accept a wit.ai account.

## 3. QR pairing

- **Passthrough Camera API:** Quest 3 and 3S, Horizon OS v74 or later. The permission is `horizonos.permission.HEADSET_CAMERA` (or `android.permission.CAMERA`, which also grants avatar camera). Passthrough must be enabled. Source: https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
- **Local verification (SDK 209):** `PC/com.meta.xr.mrutilitykit@13d1c5253cd5/Core/Scripts/PassthroughCameraAccess.cs` is the supported path, with `GetTexture()`, `GetColors()` returning `NativeArray<Color32>`, `IsPlaying` and `CameraPosition`. The permission constant is in `PC/com.meta.xr.sdk.core@0a6ebbc97c87/Scripts/OVRPermissionsRequester.cs` line 107. The manifest preprocessor adds it when `isPassthroughCameraAccessEnabled` is on (`OVRManifestPreprocessor.cs` line 1151). `WebCamTexture` is the older path.
- **Alternative with no camera:** MRUK has optional QR code tracking (added in MRUK v78). It uses the Spatial Data permission and does not need the headset camera permission. It supports QR versions up to 10 and has a low update rate. Source: https://uploadvr.com/quest-3-mruk-update-decode-track-qr-codes-unity. I did not find the class name, so check the MRUK 209 source. This is the lowest-effort route if it exists in 209.
- **ZXing.Net:** Apache 2.0 (https://github.com/micjahn/ZXing.Net). A Unity install via NuGet for Unity is documented (https://www.vrwiki.cs.brown.edu/unity/using-passthrough-camera-webcam-texture-in-unity/scanning-codes-with-passthrough). The DLL size was not verified. IL2CPP on Quest is unverified, so keep a link.xml ready.
- **Policy:** camera frames count as "Device User Data" under Meta's Developer Data Use Policy. Nothing more specific was found, and I found no competition rules. Camera access does not work in the Editor or the simulator.
- **Effort:** MRUK QR tracking is about half a day if present. PassthroughCameraAccess plus ZXing is about 1 day, plus a device test.
- **LAN discovery alternative:** Android needs `CHANGE_WIFI_MULTICAST_STATE` plus a held `MulticastLock` to receive broadcast and multicast. A Quest forum thread reports multicast not being received even with those: https://communityforums.atmeta.com/discussions/dev-quest/receiving-udp-multicasts-on-oculus-quest/816505. No Quest mDNS source was found. If we use UDP, a typed IP or code is a safer fallback.

## 4. Meta XR Simulator

- **Package:** the docs now say a UPM package is not needed. The simulator is a standalone Windows or macOS installer, and the old `com.meta.xr.simulator` should be removed if present. So there is no UPM install for 209, and no version was stated. https://developers.meta.com/horizon/documentation/unity/xrsim-getting-started/
- **Windowed Editor:** not stated. The documented workflow is Editor Play mode with a simulator window.
- **Command line:** the Meta VR CLI can activate the runtime, launch and quit apps, set the device and read logs. Headless operation and screenshots are not documented. Its release notes mention headless Unity setup.
- **Environments and hands:** three built-in rooms (game room, living room, bedroom) and six feature rooms are available (https://developers.meta.com/horizon/documentation/unity/xrsim-test-rooms-overview/). Custom rooms come from a Unity Synthetic Environment Builder over gRPC on localhost:33792 (https://developers.meta.com/horizon/documentation/unity/xrsim-synthetic-environment-builder/). Hand simulation is documented for the Meta VR Glasses profile. I did not find Quest hand simulation described.
- **EULA:** not found in what I read. The installer was not run, and nothing was accepted. The user must read and accept any licence.

## 5. Batch-mode screenshots

Unverified for our exact setup. Findings:

- Unity's Recorder docs say the graphics pipeline is not initialized with `-batchmode`, so recording fails: https://docs.unity3d.com/Packages/com.unity.recorder@5.1/manual/KnownIssues.html
- A Camera.Render halt bug in headless mode was fixed in 2017.1 (https://issuetracker.unity3d.com/issues/calling-camera-dot-render-in-headless-mode-batchmode-causes-the-scene-slash-time-to-halt).
- A `ReadPixels` issue in batchmode is marked Won't Fix.
- URP has a render-request API for rendering outside the loop: https://docs.unity.com/ko-kr/engine/6000.3/manual/cameras/urp/multiple/user-render-requests. Whether it works in batchmode was not stated.
- The practical answer is a 20-minute test: batchmode without `-nographics`, `RenderPipeline.SubmitRenderRequest` into a RenderTexture, then `AsyncGPUReadback`.

## Recommendation

| Need | Pick | Confidence |
|---|---|---|
| Typing | Keep the system overlay, or build a poke-button keyboard | High |
| Desk keyboard in world space | Not available first-party in 209 | High |
| Dictation | Overlay mic key now, then mic recording to our bridge server | Medium |
| Pairing | MRUK QR tracking if in 209, else PassthroughCameraAccess + ZXing | Medium |
| Simulator | Standalone installer, user decides on EULA | High |
| Screenshots | Quick batchmode test | Low |
