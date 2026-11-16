# Marshal plugin (Claude Code, Muse, Codex)

Marshal shows your coding agents in a Meta Quest 3 headset and lets you answer their permission requests with your hands: a short pinch for a harmless request, both hands held for a dangerous one, both palms forward to stop every agent. This folder is one plugin that Claude Code, Muse and Codex each install from the same marketplace in this repository. The headset app and the full source are at https://github.com/lb2002/marshal.

The quickest setup for every agent at once is one command: `npx marshal-agents`. The plugin is the alternative for people who prefer their agent's own plugin system.

## Install

**Claude Code** (CLI, IDE extensions, the Code tab of the Claude desktop app):

```
/plugin marketplace add lb2002/marshal
/plugin install marshal@marshal
```

Then say "connect Marshal" or run `/marshal:connect`. Switch it off with `/plugin disable marshal@marshal`.

**Muse** (1.4.15 or newer):

```
muse plugins marketplace add marshal https://github.com/lb2002/marshal
muse plugins install marshal@marshal
muse plugins approve marshal
```

Muse runs third-party hooks only after `approve`. Muse uses `.muse-plugin/plugin.json` with its own events.

**Codex** (not tried with a live Codex session yet):

```
codex plugin marketplace add lb2002/marshal
codex plugin add marshal@marshal
```

Then trust the hooks once with `/hooks` in Codex. OpenAI's docs say plugin hooks run only for plugins installed in the Codex desktop app; if they do not run for you, use `npx marshal-agents`, which writes `~/.codex/hooks.json` instead.

In every case the bridge has to run while you use Marshal: `npx marshal-agents start`, or `node "<this folder>/dist/cli.js" start`. It prints a QR code and a 6-digit code for the headset and opens a local status page. You need Node.js 22.12 or newer.

## What this plugin runs, sends and stores

Everything stays on your computer and your local network, with one optional exception: if the `claude` CLI is on your PATH, the bridge asks it (`claude -p`, your own Claude Code account) to phrase a short plain-language explanation of a permission request, which sends that request's tool name and command to Anthropic like any Claude Code prompt. Start the bridge with `--explain off` to turn this off; fixed rule texts are used instead. The explanation is display text only and never decides anything.

- **Hooks.** For each hook event of the agent (Claude Code: SessionStart, UserPromptSubmit, PreToolUse, PermissionRequest, PostToolUse, PostToolUseFailure, Notification, Stop, StopFailure, SubagentStart, SubagentStop, PreCompact, SessionEnd; Muse and Codex: their equivalents) the agent runs `node dist/cli.js hook --source <agent>` from this folder. The hook reads the event JSON from stdin and posts it to the Marshal bridge at `http://127.0.0.1:47820` (loopback only), with the token from `~/.lotse/token`. The event contains what the agent passes to hooks: session id, working folder, tool name and tool input (for example the shell command), and notification text.
- **Permission requests.** The PermissionRequest hook waits up to 100 seconds for the headset's answer and prints the agent's documented `allow` or `deny` decision. If there is no answer (no bridge running, no headset paired, time limit, any error), it prints nothing for Claude Code and Codex, which then show their normal permission dialog. For Muse it prints `deny`, because Muse may treat an empty answer as allow. The plugin never changes your permission settings or any settings file.
- **Halt.** While you have stopped all agents from the headset, the PreToolUse hook blocks each new tool call until you continue.
- **Replies** (Claude Code only). The Stop hook can wait in the background (up to 15 minutes) for a text reply typed in the headset and hands it to Claude as a message from the user. The text is never logged or stored.
- **The bridge** (`dist/cli.js start`, started by you, never by the hooks) listens on `127.0.0.1:47820` for the hooks and a local status page, and on port `47821` of your local network for the headset. The headset pairs with a single-use 6-digit code that expires after 5 minutes. The link to the headset is unencrypted `ws://`: use it only on a network you trust. Before an approval, the bridge may read file metadata (counts, sizes, whether a path is inside the project) to show what a delete would hit, and it reads the tail of the session's transcript file (the path the agent passes to hooks) to show the agent's latest step in the headset.
- **Files.** `~/.lotse/token` (a random token, created on first use) and, while all agents are halted, a halt marker in `~/.lotse`. Nothing else is written.
- **Code.** `dist/` is the compiled JavaScript of the bridge (readable, not minified; the TypeScript source is in `bridge/src` of the repository). `muse-hooks/` holds one two-line entry file per Muse hook, because Muse wants a separate file per hook. `node_modules/ws` is the unmodified `ws` 8.22.2 WebSocket library (MIT), bundled so that installing the plugin downloads nothing.

## Licence

Apache License 2.0, see `LICENSE`. Copyright 2026 Linus Baumberger.
