# Quest MR panel design and workflow: research notes (2026-11-15)

Method: official Meta pages fetched directly; gaps marked UNVERIFIED. Prior knowledge was not relied on.

## A. Panel design

Distances (https://developers.meta.com/horizon/design/hands-ui-best-practices/ and https://developers.meta.com/horizon/design/mr-design-guideline):
- Touch-encouraging panel distance 42-46 cm. Direct touch under about 46 cm.
- Indirect (ray) comfortable range about 0.8 m to 3 m. Avoid indirect UI closer than 0.8 m.
- Avoid the middle distance 0.5-0.8 m: either bring the panel into direct range or push it to 1 m or more.
- MR guideline page: 2D window with direct hand use about 45 cm; large-screen apps with indirect input about 1 m; hands+controllers about 70 cm with a move handle.
- Hybrid model: ray when hand is far, switch to direct poke when hand is near (hands page, and https://developers.meta.com/horizon/design/interactions-input-hierarchy/).
- Keep angular size constant if the panel moves in z; animate scale changes; clamp min/max size (mr-design-guideline).

Panel size (https://developers.meta.com/horizon/design/panels/ and /design/styles_layouts/): 2D app panel default 1024x640 dp, min 384x500, max 1440x1000. These are Android-panel numbers; no physical meters given. Meta says the system scales a window so 48 dp subtends about 3 deg at typical panel distance (eyes-best-practices page). So 1 dp is about 0.0625 deg (derived).

Text (https://developers.meta.com/horizon/design/styles_typography/): min 14 px (dp), comfortable 18 px or larger; Inter is the only typeface; avoid Light/Thin, use Medium/Bold, enlarge if lighter. No dmm and no degree value from Meta. Derived (my arithmetic, not Meta): 14 dp is about 0.9 deg, 18 dp about 1.1 deg of text box height. Non-Meta reference (HoloLens, https://learn.microsoft.com/en-us/windows/mixed-reality/text-in-unity): legible text about 0.35-0.4 deg at 2 m. UNVERIFIED for Quest. Panel sizing page (https://developers.meta.com/horizon/essentials/horizon-os-panel-sizing/): use larger fonts and higher contrast than mobile. Meta passthrough guidance (https://developers.meta.com/horizon/resources/bp-vision/, search snippet only, not fetched): avoid requiring fine text read through passthrough.

Hit targets:
- Minimum 48x48 dp at panel scale; primary controls 60x60 dp for hands (https://developers.meta.com/horizon/design/styles_inputs_hit_targets/). Visual button may be smaller with invisible hit slop (hands page: 4-6 dp padding inside collider).
- Angular: at least 3.0 deg for gaze (eyes page, https://developers.meta.com/horizon/design/eyes-best-practices/); hands page says about 2.5-3 deg for touch and ray. 3 deg is about 22 mm at touch distance (hands page).
- Unity/Unreal formula (eyes page): size = 2*d*tan(1.5 deg) = 5.2 cm at 1 m, so 10.5 cm at 2 m.
- Spacing: at least 12 mm between adjacent interactables (hands page); eyes page: at least 1 deg between visually dominant parts (1.7 cm at 1 m, 3.5 cm at 2 m), 16 dp for 2D Android. Prefer a single row or column.
- Poke: button travel 7 mm, touch limiting so hand cannot pass through, index finger only, frequent items in lower half of panel, do not change layout on hover.

Corner radii and elevation: UNVERIFIED. Fetched Panels, Layouts and Typography pages give none. My guessed UI Set URL returned 404; the UI set ships as Interaction SDK Unity components and Figma, not read.

Colour and contrast: no numeric rules found. Passthrough guidance (bp-vision snippet): UI must stay readable against varying real-world backgrounds; dark text on a transparent panel can vanish against a dark wall. Implication: use an opaque or near-opaque backplate. https://developers.meta.com/horizon/design/mr-design-passthrough/ says partly transparent black over passthrough reads as dimming; no UI contrast numbers. Panels page: theater panels darken surroundings so content gets priority. MR guideline: dim or hide virtual content under a modal overlay; comfort page: avoid depth on text.

Comfort zones: Meta comfort page (https://developers.meta.com/horizon/design/comfort/) is qualitative only: avoid head-locked content, avoid peripheral content needing repeated head/neck turns, keep main controls in one panel, do not place direct-touch items too close. No vertical or horizontal degrees from Meta. Non-Meta, UNVERIFIED for Quest: about 10-15 deg below horizon and a 30x30 deg optimal content area (Magic Leap, https://developer-docs.magicleap.cloud/docs/guides/best-practices/comfort-content-placement/); about 6 deg (Mike Alger, via dummies.com snippet). Meta: keep panels facing user in pitch and yaw, constrain roll, keep level (hands page).

## B. Hand interaction

- Pinch is Tier 1; palm-up pinch is a Tier 1 system gesture: dominant hand opens the system menu, non-dominant hand the app/context menu (https://developers.meta.com/horizon/design/interactions-input-hierarchy/). WebXR docs say palm pinch is reserved for system use on both hands (https://developers.meta.com/horizon/documentation/web/webxr-hands/, snippet). Hands page recommends palm-up index pinch with the non-dominant hand for the app options menu. Never bind palm-up pinch to anything else.
- Tier 2: microgesture, palm grab. Tier 3 custom poses: the app must teach them; use few simple poses, face the cameras (avoid self-occlusion), gate with proximity/hover, test with many users; too many gestures causes misrecognition (https://developers.meta.com/horizon/design/hands-gesture-design/). Index-finger pinch is most precise; middle/ring/pinky worse.
- Ray: use Interaction SDK ray interactor (reduces pinch jitter) or aimPose from XR_FB_hand_tracking_aim; hand aim is imprecise. Never run gaze and ray targeting simultaneously. Ray turns off and poke takes over when hand nears the panel.
- Gaze/dwell: the Meta pages fetched do not mention dwell. The eyes page warns against Midas touch (activating what the user merely looks at) and says commit with an explicit gesture such as pinch. So Meta advises against look-only activation; dwell timing itself is UNVERIFIED.
- Feedback: hover state before selection; visual change on press (colour, depression, glow); short distinct sound on every successful select since hands give no haptics. Scrolling: no scrollbars, scroll from anywhere, clip content at the edge.

## C. Workflow tools (https://developers.meta.com/horizon/resources/developer-tools/ unless noted; Meta rarely states accounts, so marked)

- Meta XR Simulator: OpenXR runtime on the PC with device profiles (Quest 3 default, 3S, Pro), simulated hands (subset of profiles), passthrough, synthetic rooms with scene data and anchors, record/replay, multiplayer test (https://developers.meta.com/horizon/documentation/unity/xrsim-intro). Download package for Windows/macOS; account not stated. Vulkan/D3D/Metal only. Does not support Passthrough Camera API (PCA docs).
- Link / Play mode: Unity Play mode streaming to the headset over USB-C or Wi-Fi via the Meta Horizon Link app on the PC (Link is Windows-only per MQDH media page). Needs the Link app and the headset signed in; the fetched page only confirmed Windows-only. Some features (QR) are flaky under Link (QR docs).
- Immersive Debugger: in-headset console, inspector and watches for Unity objects; enabled in Project Settings; since v77 no Development Build needed (https://developers.meta.com/horizon/documentation/unity/immersivedebugger-overview/, snippet). Part of Meta XR SDK; no extra account. Newer version has an AI voice assistant (build-faster blog below).
- Meta Quest Developer Hub (called Meta VR Developer Hub on some pages): desktop app for Windows/macOS; deploy APKs, device logs, Perfetto traces, screenshots, recording, casting, disable proximity sensor/boundary. Account: Meta developer account and developer mode on the headset (general knowledge, not on fetched page).
- Logcat: Unity Android Logcat package (Window > Analysis) or MQDH log view; no account.
- OVR Metrics Tool: on-headset overlay of FPS, thermals, stale frames, CPU/GPU level; exportable reports. Download from Meta; account not stated.
- RenderDoc Meta Fork: Quest-capable RenderDoc with draw-call and GPU data; download from Meta; account not stated.
- On-device automation: Scriptable Testing Services (disables dialogs, sleep, boundary), AutoDriver (record/replay headset and hand input), XR Simulator record/replay; third-party Appium+adb route (https://helpmetest.com/blog/meta-quest-testing-automation/, unverified).
- Inner loop (https://developers.meta.com/horizon/blog/build-faster-and-earn-more-accelerate-unity-vr-workflow/): Unity 6.3+ with Unity OpenXR as the "golden path"; iterate in XR Simulator at the desk; Immersive Debugger for in-headset tweaks; Perfetto/OVR Metrics for performance (blog example uses 72 fps target). Deploy APK only for device-only things (hand tracking quality, PCA, real passthrough, depth).

## D. QR codes and Passthrough Camera API

- MRUK QR detection: https://developers.meta.com/horizon/documentation/unity/unity-mr-utility-kit-qrcode-detection. Quest 3/3S; Meta XR Core SDK v83+ and MRUK v83+ per this page. UploadVR says it first appeared in MRUK v78 (https://uploadvr.com/quest-3-mruk-update-decode-track-qr-codes-unity); treat v83 as safe. Quest OS version not stated. A search result called it experimental; not confirmed on the current page.
- Permission: Spatial Data Permission (required for all tracking APIs); OVRManager Scene Support Required, Anchor Support Enabled.
- Payload readable: MRUKTrackable.MarkerPayloadString (null unless bytes are valid UTF-8) and MarkerPayloadBytes. Pose is 6-DoF Transform; bounds MRUKAnchor.PlaneRect / PlaneBoundary2D. QR versions up to 10; no logos, no Micro QR; best in good light, large and close; pose updates are low frequency, not for moving objects. Disable when not needed.
- Minimal outline: MRUK component > Scene Settings > Tracker Configuration > QR Code Tracking Enabled; subscribe to TrackableAdded / TrackableRemoved (https://developers.meta.com/horizon/documentation/unity/unity-mr-utility-kit-trackables); in the handler check trackable.TrackableType == OVRAnchor.TrackableType.QRCode and read MarkerPayloadString. Exact accessor path to the events is UNVERIFIED. Low-level alternative: OVRAnchor.Tracker.
- Passthrough Camera API (https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/): alternative that gives camera frames so you run your own decoder. Horizon OS v74+, Quest 3/3S, permission horizonos.permission.HEADSET_CAMERA (passthrough camera only) or android.permission.CAMERA (also avatar camera); Unity side is MRUK PassthroughCameraAccess. Resolutions 1280x960, 1280x1280, 1728x1728 (1728 needs v209); latency 20-40 ms; about 45 MB memory. Not supported in XR Simulator.

## E. Recording

- MQDH Device Manager > Record: MP4, max 3 min, default 1024p@36 fps 1:1, 1080p@36 16:9 option, bitrate 5-40 Mbps, mono or stereo (https://developers.meta.com/horizon/documentation/unity/ts-mqdh-media/). Page says no audio.
- MQDH cast recording (camcorder button): encodes at source resolution, includes system/app audio, aspect Original 1:1, Cropped 16:9, Cinematic 16:9 (restarts the app), Target Bitrate / Max Resolution / Max Framerate settings, "Pause when recording" holds 60 fps; Capture Format MAX near-lossless, about 1.2 GB per minute for MR (same page).
- Meta max quality (https://developers.meta.com/horizon/resources/video-capture-mr-vr/): 2160p, 60 fps variable, 40 Mbps, lower if app drops frames. In-headset camera app for YouTube: 1920x1080, 36 fps, 20 Mbps. Use Quest 3 for MR capture. Enable "Download Large Device Files" for 500 MB+ files.
- adb setprop (https://developers.meta.com/horizon/documentation/unity/ts-systemproperties/): debug.oculus.capture.width/height (default 1024), debug.oculus.capture.bitrate (default 5000000), debug.oculus.fullRateCapture (default 0 = half rate), debug.oculus.enableVideoCapture (start/stop). Not persistent across reboot. An fps property appears only in community posts (UNVERIFIED).
- Passthrough in recordings: fetched Meta pages do not say. A community post says MQDH cast with passthrough works (UNVERIFIED). Test a short clip first.
- Demo advice (Meta capture guide): storyboard key moments; slow head movements for POV; clean set; entry-level LED lighting on hands; colour-correct, light stabilization, check aspect ratio; export MP4. Third-person MR needs compositing. For a desk app, my suggestion: POV cast at 1080p/60 plus a short phone shot of the desk.

## Unverified / not found
Corner radii, elevation, dwell timing, Meta vertical/horizontal comfort angles, numeric UI contrast rules, text in dmm, passthrough inclusion in recordings, account requirements for most tools, Quest OS version for QR.
