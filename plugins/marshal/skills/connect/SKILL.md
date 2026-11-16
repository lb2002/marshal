---
name: connect
description: Start the Marshal bridge on this PC and pair the Marshal headset app (Meta Quest 3). Use when the user wants to connect Marshal, see their agents in the headset, approve requests with their hands, or asks for the Marshal pairing code.
---

# Connect Marshal

The plugin's hooks are already active: every Claude Code session sends its events to the Marshal bridge on
`127.0.0.1:47820`. While no bridge runs, the hooks do nothing and Claude asks for permission as usual. This skill
starts the bridge so the headset can pair.

The plugin root is two folders above this skill's base directory (`<base>/../..`). Call it `ROOT` below and use
its absolute path with forward slashes.

1. **Node.js.** Run `node --version`. Marshal needs 22.12 or newer. If it is older or missing, tell the user and stop.
2. **Dependency.** The plugin bundles its one dependency (`ws`) in `ROOT/node_modules`; nothing needs to be installed.
3. **No double hooks.** If `~/.claude/settings.json` contains hook commands with `cli.js" hook --source claude`, Marshal
   was also set up with `lotse connect` earlier, and every event would reach the bridge twice. Tell the user to run
   `node "<that cli.js>" disconnect --tool claude` once, or to disable this plugin instead. Do not edit the file yourself.
4. **Start the bridge in the user's own terminal**, not in a background shell: it prints a QR code and a 6-digit code,
   and Enter in that terminal gives a new code. Give the user this command to paste:

   ```
   node "ROOT/dist/cli.js" start
   ```

   In the Claude desktop app they can use the Terminal panel. If they ask you to run it yourself, run it in the
   background and show them the lines with the pairing code and the addresses from its output.
5. **In the headset:** open Marshal and look at the QR code, or choose "Connect to my PC" and type the address and
   the code. The code works once, for 5 minutes.

Tell the user what the bridge does: it listens on `127.0.0.1:47820` for the hooks and on port `47821` of the local
network for the headset (unencrypted `ws://`, so only on a network they trust). Ctrl+C in its terminal stops it.
To switch Marshal off completely: `/plugin disable marshal@marshal`.
