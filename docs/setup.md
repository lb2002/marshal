# Marshal setup

Marshal shows your coding agents (Claude Code, Codex CLI, Gemini CLI, Muse, OpenCode) in a Meta Quest headset and lets you approve or deny what they ask for. The PC side is a small bridge; the command is `lotse`.

## Three steps

1. **Install the app on the headset.** Install Marshal from the release channel (https://www.meta.com/s/5FzKfTWZQ), or the APK with developer mode (`adb install <the APK file>` or drag it into the Meta Quest Developer Hub). Headset and PC must be on the same network.
2. **Run one command on the PC.** You need Node.js 22.12 or newer.

   ```
   npx marshal-agents
   ```

   It lists what it will change for every agent it finds on this computer and asks once (`y` to go on). Then it starts the bridge, shows a 6-digit code, the PC's address and a QR code, and opens a status page in your browser with the same QR code. (From a clone of this repository instead: `npm install` and `npm run build` once in `bridge/`, then `node dist/cli.js connect`.)
3. **Look at the code.** In the headset: open Marshal and look at the QR code on the status page or in the terminal. Or choose "Connect to my PC" in the headset and type the address and the code.

The code works once, for 5 minutes; "New code" on the page or Enter in the terminal gives a new one. Ctrl+C stops the bridge. Next time, `npx marshal-agents start` starts it again; `npx marshal-agents disconnect` removes everything Marshal added. When run through `npx`, Marshal keeps a copy of itself in `~/.lotse/runtime/<version>` for the hooks, because npm may clear its own cache.

## What is installed where

Only for the agents whose settings folder exists (an agent that is not installed is skipped). Every changed file gets a byte copy next to it first (`<file>.lotse-backup-<time>`).

| Agent | File | What is added |
|---|---|---|
| Claude Code | `~/.claude/settings.json` (or `$CLAUDE_CONFIG_DIR`) | one Marshal hook per event under `hooks`, next to your own; the Marshal `statusLine` only if there is none |
| Codex CLI | `~/.codex/hooks.json` (or `$CODEX_HOME`) | one Marshal hook per event under `hooks` |
| Gemini CLI | `~/.gemini/settings.json` (or `$GEMINI_CLI_HOME/.gemini`) | one Marshal hook per event under `hooks` (Marshal can only deny for Gemini) |
| Muse | no settings file: a user plugin `lotse`, bundle in `~/.lotse/muse-plugin`, checked by Muse before it is installed | one hook per Muse event |
| OpenCode (not checked live) | no settings file: the plugin file `~/.config/opencode/plugins/marshal/index.js` (or under `$XDG_CONFIG_HOME/opencode`) | one small plugin that runs the Marshal hook for sessions, tool calls and permission requests |

Marshal also keeps its own folder `~/.lotse` (the hook token). The command prints the exact paths and entries before it asks. Without a terminal it asks nothing and changes nothing unless you add `--yes`. The confirmation proves that a terminal is attached, not that a human answered; what stops an agent from running these commands itself is that the headset asks for both hands for any call of Marshal's own CLI. `--no-qr` hides the QR code; `--host <address>` picks the LAN address in the QR code if the automatic choice (a private address on a real network adapter) is wrong.

Some agents want one confirmation before new hooks run (the command prints these steps): Codex asks you to approve the hooks once under `/hooks`, Muse needs `muse plugins approve lotse` once, Claude Code sessions that were already running need a restart, OpenCode loads plugins only when it starts (restart it).

## How to remove it

```
node dist/cli.js disconnect
```

It lists what it removes and asks once, like `connect` (`--yes` skips the question). While Marshal is halted it refuses; release the halt in the headset or the bridge console first. It removes exactly Marshal's entries and puts each file back as it was before `connect` (byte for byte, if nothing else changed it in between; files Marshal created are deleted). The backups `*.lotse-backup-*` and `~/.lotse` stay; delete them by hand if you want. The Muse plugin is removed already when the bridge stops, because Muse denies every approval while Marshal is not running (`--keep-muse` keeps it).

For one project folder instead of all sessions: `node dist/cli.js connect --project <dir> [--tool muse|codex|gemini|opencode]`, removed with `uninstall-hooks --project <dir>`. Details: `bridge/README.md`.
