# Offscreen renders

`Lotse.EditorTools.Renders.All()` renders every screen of the app as a PNG, from a seated user's eye and
against a real photo of the desk, so the UI can be looked at before anyone puts the headset on.
The PNGs are written to this folder and are git-ignored.

## Run

Batch mode **with graphics** (no `-nographics`) and **without `-quit`**. The tool quits the Editor itself when it
is done. From the worktree root, with Git Bash:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe" -batchmode \
  -projectPath "$PWD/quest" -buildTarget Android \
  -executeMethod Lotse.EditorTools.Renders.All \
  -logFile "$TEMP/lotse-renders.log"
```

- It takes about 1 minute when the Library is warm.
- Exit code 0: all 38 PNGs were written, none is black or flat, and no exception was logged.
- Exit code 1: a step failed, a PNG is missing, or a PNG is black. Search the log for `[Renders]`.
- Exit code 2: it was started with `-quit` or `-nographics`.
- Exit code 3: URP drew nothing into the offscreen texture.
- The log lists every PNG with its mean luminance, its share of dark pixels and its spread.

`unity run` does not work here. It always adds `-quit` (a reserved flag the CLI manages itself), and `-quit` ends
the Editor as soon as `All()` returns, before Play mode starts. `All()` detects this and exits with code 2.
No Editor may be open on the project at the same time.

## What it renders

The real `AppRoot` runs in Play mode in an empty scene with the demo pack (`StreamingAssets/demo/jury.pack.json`),
in fixed 100 ms frames. Fakes stand in for the headset: scripted hands, a saved desk anchor, prefs in memory, a
keyboard and a socket that never connects. Nothing goes to the network. Buttons and rows are pressed with
`PokeButton.Click`, holds are real pinches inside the hold gate, the dangerous requests are denied with "Ablehnen".
The app code is unchanged.

The geometry comes from the device logs: the head at 1.23 m, the desk at 0.76 m, the desk anchor 0.45 m forward and
0.4 m right. The workspace stands where `WorkspaceDock` puts it: 0.42 m right of the desk anchor, its bottom edge
0.12 m above the desk, facing the head. The camera is one eye, not stereo: 1600x1000, 60 degrees vertical field of
view (about 85 degrees across). It turns toward each screen but never moves (close-ups excepted).

| File | Screen |
|---|---|
| `00-start` | start panel, in the workspace's place |
| `06-pairing` | pairing panel with the keypad, "481" typed |
| `03-conversation-working` | the workspace, 4 agents; Agent 2 (working) selected: its conversation with 8 feed entries |
| `08-desk-keyboard` | "Tastatur": the desk keyboard on the desk, the workspace beside it (`-closeup`: the keyboard) |
| `07-settings` | "Einstellungen" (connection, sound, "Ruhig") in front of the workspace |
| `04-notice` | the first request (Agent 1's) while Agent 2 is shown: no block, only the notice pill above the window |
| `02-workspace-harmless` | the pill selected Agent 1: its harmless request inline, the pinch ring half full |
| `01-workspace-dangerous` | the first dangerous request (rm -rf ../legacy-site) inline, two-hand hint, countdown |
| `05-orbs-closeup` | the six orb states in a row, with the row's German words: arbeitet, denkt nach, Befehl läuft, wartet auf dich, fertig, Fehler (eye 0.36 m away, so the wider strip fits) |
| `09-six-agents-closeup` | six agents (two added by hand), Agent 1 selected with a block: the draw-call probe's scene |
| `10-overview` | U6 "Übersicht", 4 agents: Agent 1 waits (tinted column, red dot, "Wartet auf dich: …"), one done (its plan all done), one without a plan (its recent activity, a dash per line); the plan state is put into the store for this shot |
| `11-overview-six-closeup` | Übersicht with six agents, page 1 ("1 / 2" and the page buttons) |
| `12-plan-line-closeup` | Unterhaltung, Agent 2: the plan line under the summary line, collapsed ("▼ Plan 0 / 6: …") |
| `13-plan-open-closeup` | the same, open: the checklist (6 lines), the feed shorter |
| `14-rail` | "Einklappen": the rail (56 mm) at the window's left edge, in the photo next to the monitor (`-closeup`: the rail) |
| `15-rail-red` | the rail while Agent 1 asks: its red orb (`-closeup`: the rail) |

Each screen also has a `-closeup` render: the eye 0.6 m straight in front of the panel's centre. The log has the
render probe (`[Renders] render probe`) with the workspace's renderers and shader passes for the two block scenes
(U5a fix 1: 40/41 with four agents and a dangerous block, 39/40 with six agents; the true peak with the pill and a
hold running is measured by the PlayMode test `AtThePeak…`: 44 renderers, 45 passes, budget 48). U6: the probe also
logs the overview with 4 agents (35/36), with 6 agents page 1 (41/42) and the rail (6/7); the PlayMode test
`TheOverviewWithFourFullColumnsAndTheRailStayWithinFortyEightDrawCalls` measures 4 full columns with an incident line
(38/39) and the rail with 7 orbs and two red pulses (12/13). The scenes switch "Automatisch einklappen" off in their
prefs (the camera looks away from the window on purpose), so `07-settings` shows "Aus" there.

## Backdrop

`Assets/Lotse/Editor/RenderBackdrop/desk.png` is a passthrough capture of the user's desk: the left-eye half,
with the overlay of an old build painted out and most of the lens distortion removed. It is built by
`make_backdrop.py` (Python 3 with numpy and opencv-python):

```bash
python quest/Renders/make_backdrop.py <capture.png> quest/Assets/Lotse/Editor/RenderBackdrop/desk.png
```

The photo hangs on a plane 12 m behind the camera and fills the view. It turns with the camera, so it is only a
backdrop and not aligned with the room.

## URP in batch mode

URP renders in batch mode with graphics (Direct3D 12 here) into an offscreen `RenderTexture`. In Play mode the
Android quality level is active (`Mobile_RPAsset`), and its renderer `Mobile_Renderer` has Tile-Only Mode on,
which is meant for the headset's tiled GPU. For the renders, `OffscreenUrp` swaps in an in-memory copy of the
asset whose renderer copy has Tile-Only Mode off. All other settings stay the device's (MSAA 4×, HDR off). Nothing
is saved, and the original asset is restored at the end. Before the first PNG, the start panel is rendered once
without the backdrop as a check that URP draws.

Why the copy is needed: with `-lotseKeepTileOnly` (the asset unchanged), the check finds 0 % of the pixels lit and
the run exits with code 3. For a camera that renders into a `RenderTexture`, URP adds the pass
`BlitFinalToBackBuffer`, which samples `_CameraTargetAttachment`. The On-Tile Validation layer that Tile-Only Mode
turns on forbids that and throws `InvalidOperationException: The On-Tile Validation layer has detected an issue`.
The render graph is dropped and the texture stays black.

## Side effects

Play mode makes TextMesh Pro add glyphs to the dynamic fallback font asset, and the Meta XR SDK writes
`RuntimeActionBindings.json` into the project and `StreamingAssets`. The tool snapshots these files before Play
mode and restores or removes them afterwards, so `git status` stays clean. If the Editor is killed in the
middle of a run, restore them with `git checkout` and delete the json files by hand.

The tooling lives in `Assets/Lotse/Editor/Renders/` (an Editor-only assembly), so none of it is in the APK.

## U5b: docking to a display
- `16-screen-setup`: the three-tap setup after the second corner (two dots, the ring filling at the third corner).
- `17-screen-slots`: the four slot outlines around the display, "right" highlighted, the prompt with Back and Cancel.
- `18-docked-right` (+ close-up): the workspace in the right slot of a 0.6 x 0.34 m monitor, same height, top edges aligned, 3 cm gap. Probe: 42 renderers, 43 passes.
- `19-docked-above-laptop`: above a laptop leaning back 25 degrees, left edges aligned, the window tilted with it (0.42 m wide, the minimum).
- The displays are stand-ins drawn into the scene (a dark screen with a bezel): the backdrop photo's monitor cannot be lined up with the room.
- `20-stop-bar-closeup` (U7): the stop gesture half held, the amber bar along the window's top edge with "Halt all agents".
- `21-halted(-closeup)` (U7): the demo halted: the amber band "Halted for 1 s" with "Continue" under the header, the enforcement line, a pause badge on every orb.
- `01-workspace-dangerous(-closeup)` since U7: the facts row of the rm -rf request: "Checked: ../legacy-site", "214 files · outside the project", the red "Gone for good" chip, the bridge's reason.
- `22-menu-closeup` (U8): the header's Menu open under its right end (Further / Closer, Larger / Smaller, Set up display / Place again, Settings, Halt all agents, Close).
- `23-header-scale-1-closeup`, `23-header-scale-2-closeup` (U8): the header with its 7 mm top padding at 1.0 and 2.0 of the U5b size; nothing clipped.

## U9: the design system pass, the bands, the guided test
46 PNGs. Every render above now shows the U9 look (opaque window, cards, one control height). New or changed:
- `10-overview`: every agent has a plan, the four vendors side by side (Claude Code, Codex, Gemini CLI, Muse).
- `24-question-band-closeup`: "Put Marshal next to a display?" as a band inside the window, under the header.
- `25-halt-question-closeup`: "Halt all agents?" from the menu, in the same band.
- `26-overview-no-plan-closeup`: the one deliberate example without a plan ("no plan", "Recent activity").
- `27-test-band-closeup`, `28-test-two-hand-closeup`, `29-test-summary-closeup`: the guided test's band along the
  bottom edge at its first step, at the two-hand step with the test's own dangerous request, and the summary
  (the eye 0.7 m away, so the band is in the picture).
- `30-plain-overview`, `31-plain-dangerous`: the window alone on a plain mid-grey backdrop, for the submission.
- The probe: the overview with 4 agents 36 renderers / 37 passes, 4 agents with the dangerous block 41 / 42 (budget 48).
  The guided test's band adds 12 while the test runs (54 / 55 with a dangerous block): test mode only.
- The typed draft in the keyboard scenes is "Hello"; the trail lines are English.

## U10: the window 2 m away
47 PNGs. The window stands 2.0 m from the seat (1.57 x 0.91 m), so every close-up now moves the eye back until the whole
panel is in the picture. New or changed:
- `17-docked-band` (and `-closeup`): docked at once after the third corner; the band "Docked right of your display" with
  "Other side" and "Done". It replaces `17-screen-slots` (the four-slot chooser is gone).
- `18-docked-right`, `19-docked-above-laptop`: a docked window stands on the rays from the seat, at least 1.6 m away.
- `22-menu-closeup`: the menu as a centred sheet over a scrim.
- `23-header-scale-1/2-closeup`: the header at the smallest and the largest apparent size.
- `27` to `29`: the guided test has ten steps.
- The probe: the overview with 4 agents 34 renderers / 35 passes, 4 agents with the dangerous block 42 / 43.

## U11: exact text heights, the docked window faces the seat, pairing by QR code
50 PNGs. New or changed:
- Every render: texts take their exact height (TextMeshPro's preferred height is rounded up to whole centimetres), so
  header labels sit in the middle of their controls and no caption carries an empty line under it.
- `10-overview-closeup`, `26-overview-no-plan-closeup`: a wrapped step's second line follows at 1.25 x the text size;
  the vendor directly under the title, the progress row 2 x S under the vendor line of a two-line title.
- `18-docked-right`, `19-docked-above-laptop`: pushed out along the ray, the window faces the seat (yaw only, upright).
- `22-menu-closeup`: the scrim is opaque; nothing behind the sheet is readable.
- `05-orbs-closeup`: the strip alone, nothing of the panel behind it.
- `06-pairing`: "Scan code on your monitor" above the manual fields.
- `32-pair-scan-closeup`: scan mode (one instruction line, "Cancel"). `33-pair-ask-closeup`: the question after a code
  was seen (a scripted tracker; the address is the parsed literal). `34-pair-denied-closeup`: the line without the
  spatial data permission.
- `27` to `29`: the guided test has eleven steps.

## U12: the pairing viewfinder
54 PNGs. New or changed:
- `32-pair-scan` (and `-closeup`): "Connect to my PC" opens the viewfinder: four corner brackets a metre in front of the
  head, the sweep line, the card with the instruction and its two controls.
- `33-pair-ask` (and `-closeup`): a code was seen (a scripted tracker that also says where the code is): the brackets
  stand on the code, the question band under it.
- `35-pair-connecting-closeup`, `36-pair-failed-closeup`: the one attempt runs; it ended without pairing.
- `06-pairing`: the window, now behind "Type the code instead", with "Scan instead". `34-pair-denied-closeup`: the
  window with the one line when the permission is missing.
- `00-start`: the primary button's label in the heaviest face.
