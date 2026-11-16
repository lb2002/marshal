# Privacy

Marshal (the headset app, the PC bridge `marshal-agents`, and the plugins in this repository) is made by Linus Baumberger. This page says what Marshal does with data. Last updated 16 November 2026.

## What the author receives

Nothing. Marshal has no server, no account, no analytics, no telemetry and no crash reporting. The author receives no data from you or your computer.

## What stays on your computer and your network

- **Agent events.** The hooks pass what your coding agents give them (session id, working folder, tool name and input such as shell commands or file paths, notification text) to the Marshal bridge on your own computer (`127.0.0.1`). The bridge keeps this in memory while it runs and sends what the headset needs to show to your paired headset on your local network (unencrypted WebSocket on port 47821; use it only on a network you trust).
- **Files read.** To show what a delete would hit, the bridge reads file metadata (counts, sizes, locations), never file contents. To show an agent's latest step, it reads the end of that session's transcript file, which the agent names in its hook event.
- **Files written.** `~/.lotse/token` (a random token for the local connection between hooks and bridge), while all agents are halted a halt marker in `~/.lotse`, and the hook entries that `npx marshal-agents` adds to your agents' settings after asking you (with a backup of each file it changes). Nothing else.
- **Replies** you type in the headset go only to the agent waiting for them; they are not logged or stored.
- **Recordings** are made only when you start the bridge with `--record <file>`, and are anonymised (paths, user names and secrets replaced) before they are written.

## The one optional exception

If the `claude` command line tool is installed, the bridge asks it (`claude -p`, with your own Claude Code account) to phrase a one-line plain-language explanation of a permission request. That sends the request's tool name and command to Anthropic, under Anthropic's terms, like any other Claude Code prompt. Start the bridge with `--explain off` to turn this off; fixed rule texts are shown instead.

## The headset app

The Quest app uses hand tracking, passthrough and spatial anchors on the headset itself. It sends nothing anywhere except to the bridge you pair it with on your local network. Meta's own terms apply to the headset and the Meta Horizon Store.

## Questions

Open an issue at https://github.com/lb2002/marshal/issues.
