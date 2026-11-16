# Unity 6 + Quest 3/3S MR setup research (2026-11-11)
[V] = verified on the cited page. [U] = unverified or my inference.

## 1. Install
- Meta requires Unity 6000.0.66f2 or higher; 6.1 or later is recommended. Modules: Android Build Support, OpenJDK, Android SDK & NDK Tools. [V] https://developers.meta.com/horizon/documentation/unity/unity-development-requirements/ and https://developers.meta.com/horizon/documentation/unity/unity-xr-plugin/
- Meta names no single "recommended LTS patch". [U] Unity 6.3 LTS (6000.3.x) is the current LTS line (https://docs.unity.com/en-us/engine/6000.7/manual/whats-new/unity63), but I found no Meta statement that SDK 209 is tested on 6000.3. Safest pick: the newest 6000.3.x LTS patch from the Hub, falling back to 6000.0.66f2 or later if the Meta SDK complains. Check Meta's SDK 209 release notes.
- Unity Hub via winget: `winget install --id Unity.UnityHub -e` [V that the id exists, via catalogs] https://winstall.app/apps/Unity.UnityHub . Whether it needs admin rights: [U]. The default Hub path is C:\Program Files\Unity Hub, so expect a UAC prompt.
- Hub CLI on Windows [V], https://docs.unity.com/en-us/hub/use-hub-cli.md :
  `"C:\Program Files\Unity Hub\Unity Hub.exe" -- --headless install --version 6000.3.7f1 --module android android-sdk-ndk-tools android-open-jdk`
  (6000.3.7f1 is the docs' example; use `-- --headless editors -r` to list real releases. `--changeset <hash>` is needed if the version is not in the list.) Modules can be added later with `install-modules --version X -m ...`, but only on editors installed through the Hub. The page marks the CLI as experimental.
- Unity login and licence: not stated on that page. [U] The Personal licence normally requires signing in to the Hub and activating a licence before the editor opens projects, so do this once in the GUI. Unity also has a separate Unity CLI that installs without the Hub: https://docs.unity.com/zh-cn/unity-cli/unity-cli-reference (winget id `Unity.CLI` appears at https://winstall.app/apps/Unity.CLI).

## 2. Meta XR SDK packages
- All-in-One SDK (UPM) is at **209.0, dated Nov 9 2026**, minimum Unity 6000.0.66f2. [V] https://developers.meta.com/horizon/downloads/package/meta-xr-sdk-all-in-one-upm/
- It bundles Core, Audio, Haptics, Interaction SDK Essentials, Interaction SDK, Platform, Voice, and Meta Mixed Reality Utility Kit. It does not include the Simulator's Synthetic Environment Builder. [V] same page.
- Per-package version numbers: Core 207.0 is confirmed (Sep 22 2026). I assume Core, Interaction SDK and MRUK are also at 209 because the wrapper tracks them. [U] https://developers.meta.com/horizon/downloads/package/meta-xr-core-sdk/
- Plugin: use **OpenXR (Unity OpenXR Plugin 1.15.1 recommended)** with the Meta XR feature group. The Oculus XR plugin is deprecated. [V] https://developers.meta.com/horizon/documentation/unity/unity-xr-plugin/ and https://developers.meta.com/horizon/documentation/unity/unity-project-setup/
  - Optional: Unity OpenXR: Meta (`com.unity.xr.meta-openxr` 2.1.0) is only needed for Depth API occlusion. Interaction SDK Essentials depends on `com.unity.xr.openxr` and `com.unity.xr.hands`. [V] https://developers.meta.com/horizon/documentation/unity/unity-isdk-packages-and-requirements/
  - A later Meta Operator page mentions OpenXR Plugin 1.17.0 or later. [U] Check what the Package Manager offers.
- Setup steps [V], https://developers.meta.com/horizon/documentation/unity/unity-project-setup/ :
  1. Create the project, then add the All-in-One package.
  2. Project Settings > XR Plug-in Management > OpenXR: enable the Meta XR feature group on both the Standalone and Android tabs.
  3. Window > Meta > Tools > Project Setup Tool: run Fix All on Outstanding Issues for both tabs, handle Manually Fixable items by hand.
- Minimal passthrough scene [V], https://developers.meta.com/horizon/documentation/unity/unity-passthrough-gs/ :
  1. Delete Main Camera and add the OVRCameraRig prefab.
  2. On OVRManager > Quest Features, set Passthrough Support to Required or Supported, and check Enable Passthrough under Insight Passthrough.
  3. Add an empty GameObject with the OVR Passthrough Layer component.
  4. Set the Lighting skybox to None, and the camera clear flags to SolidColor with transparent black.
  - Building Blocks (Passthrough, Hand Tracking) do the same through the Building Blocks window. I did not verify block names. [U]
  - Hand tracking: enable Hand Tracking Support (Hands Only) on OVRManager and add the ISDK hand building block. [U] The Meta pages I fetched do not state this.

## 3. Interaction SDK gestures
Main reference: https://developers.meta.com/horizon/documentation/unity/unity-isdk-hand-pose-detection/ [V]
- Components: ShapeRecognizerActiveState, TransformRecognizerActiveState, ActiveStateGroup (AND), ActiveStateSelector (events), HandRef, JointRotationActiveState, SequenceActiveState. Sample scenes: PoseExamples and GestureExamples.
- (a) Pinch-and-hold with progress: IHand `GetFingerPinchStrength(HandFinger.Index)` gives 0..1, `GetFingerIsPinching` gives a bool. [U for the exact method name; IHand reference: https://developers.meta.com/vr/reference/interaction/v207/interface_oculus_interaction_input_i_hand]. Do the hold timing in your own script.
- (b) Palm forward / stop: use the StopPose prefab, or a TransformRecognizerActiveState with PalmAwayFromFace plus FingersUp, wrapped in an ActiveStateSelector. [V]
- (c) Two-hand confirm: put the left and right pose ActiveStates in one ActiveStateGroup; optionally add a SequenceActiveState for time-ordered poses. [V components; the combination is my inference]
- (d) Swipe: JointVelocityActiveState, which the page says can detect "swiping forward (relative to the palm)". [V]
- Tracking loss: IHand `IsConnected` and `IsHighConfidence` (high confidence requires IsConnected and IsTrackedDataValid). [V] https://developers.meta.com/vr/reference/interaction/v207/interface_oculus_interaction_input_i_hand . HandTrackingConfidenceProvider also exists: https://developers.meta.com/horizon/reference/interaction/v77/class_oculus_interaction_hand_tracking_confidence_provider

## 4. Content relative to desk / laptop
- MRUK offers MRUKRoom, anchors with semantic labels, EffectMesh, and EnvironmentRaycast (places content from depth, no scene setup needed). [V] https://developers.meta.com/horizon/documentation/unity/unity-mr-utility-kit-overview/
- Scene Model needs the `com.oculus.permission.USE_SCENE` permission, plus the user having run Space Setup. Without the permission, LoadSceneFromDevice returns NoScenePermission and anchors are empty, with no exception. [V] https://developers.meta.com/horizon/documentation/unity/unity-spatial-data-perm/ . Declare it by enabling Scene Support on OVRManager; MRUK auto-requests it at startup when the data source is Device.
- Spatial anchors need `com.oculus.permission.USE_ANCHOR_API`, or Anchor Support on OVRManager. [V] https://developers.meta.com/horizon/documentation/unity/unity-spatial-anchors-persist-content/
- Recommended approach for an unknown room [U, my inference]: do not depend on Space Setup. Use a "place here" step: the user pinches or pokes the desk or laptop, you raycast with EnvironmentRaycast (or use an MRUK table anchor if present, via DeviceWithPrefabFallback), then pin an OVRSpatialAnchor. Keep a head-relative fallback position if the raycast fails.
- Boundaryless: add `<uses-feature android:name="com.oculus.feature.BOUNDARYLESS_APP" android:required="true"/>`. It only takes effect in a standalone APK, not over Link. [V] https://developers.meta.com/horizon/documentation/unity/unity-boundaryless/ . No separate runtime permission for passthrough was found [U]; it is a manifest feature set by Passthrough Support.

## 5. Networking
- ClientWebSocket on Quest IL2CPP: no authoritative source found. [U] Anecdotal: a Unity forum post reports ws:// streaming to Quest 3 over WLAN working; a Meta forum thread reports a Quest 3 WebSocket failure with no root cause (https://communityforums.atmeta.com/discussions/dev-unity/websocket-connection-not-working-on-meta-quest-3/1260843). NativeWebSocket (https://github.com/endel/NativeWebSocket, UPM `com.endel.nativewebsocket`) lists Android support and is the common choice. Plan: NativeWebSocket as primary, test ClientWebSocket early on-device. Call `DispatchMessageQueue()` in Update on non-WebGL builds.
- Cleartext: Android 9+ blocks cleartext by default when targetSdk is 28 or higher (Java stack). Set `android:usesCleartextTraffic="true"` on `<application>` through a custom main manifest (Player Settings > Publishing Settings > Custom Main Manifest). [V general behaviour] https://issuetracker.unity3d.com/issues/android-videos-are-not-played-from-url . [U] Whether raw managed sockets are subject to this policy at all; set it anyway. A networkSecurityConfig limited to the PC is cleaner, but I found no verified Unity recipe, and it cannot whitelist a bare IP range.
- INTERNET permission: `android.permission.INTERNET`. [U] Unity adds it when Player Settings > Internet Access = Require. Verify in the built manifest.

## 6. Build and deploy
- Settings [V], https://developers.meta.com/horizon/documentation/unity/unity-prepare-for-publish/ : ARM64 only (Store accepts only 64-bit), IL2CPP, min API 29 (Android 10) or higher, unique package name `com.Company.App` (Override Default Package Name), Bundle Version Code incremented each upload.
- Target API: apps created in the Dashboard after Mar 1 2026 must target API 34 or higher. [V] https://developers.meta.com/vr/blog/meta-quest-apps-android-14-march-1/ . A Meta bug report says tooling conflicts for targets above 34 (marked fixed, date unknown). https://developers.meta.com/horizon/feedback/vr/investigations/1434307618054253 . Use Automatic (highest installed) and check the upload is accepted.
- Install over USB: `adb install -r app.apk`, or Build And Run (Windows or macOS, data-capable USB-C cable). [V] https://developers.meta.com/horizon/documentation/unity/unity-env-device-setup/ . Developer Mode must be on. MQDH steps: [U].
- Upload [V], https://developers.meta.com/horizon/resources/publish-reference-platform-command-line-utility/ :
  `ovr-platform-util upload-quest-build --age-group TEENS_AND_ADULTS --app-id <ID> --token <token> --apk app.apk --channel Competition --notes "..."`
  - Auth is `--app-secret` or `--token` (generate the token in Dashboard Account settings). Channel names are case-insensitive.
  - Windows binary: https://developers.meta.com/horizon/downloads/package/platform-utils-cli-pc/ . Run `self-update` first.
  - The page does not say how to create the "Competition" channel; create it in the Developer Dashboard release channels. [U]
  - The app must already exist in the Dashboard with the same package name. APK only, not AAB.

## 7. Simulator
- Meta XR Simulator runs on Windows or Apple Silicon macOS with no headset. It simulates hands via mouse and keyboard (aim, poke, pinch, grab) or webcam camera tracking. Passthrough is provided by synthetic environments (the Synthetic Environment Builder is a separate package). [V] https://developers.meta.com/horizon/documentation/unity/xrsim-intro/ , https://developers.meta.com/horizon/documentation/unity/xrsim-camera-tracking/ . Hand tracking and Depth are available only on some profiles. Final tests must be on a headset.
