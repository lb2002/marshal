# Marshal for Muse and Codex

The same Marshal bridge as `plugins/marshal`, packaged for Muse and Codex, which read this repository's `.agents/plugins/marketplace.json`. See [`plugins/marshal/README.md`](../marshal/README.md) for what the plugin runs, sends and stores; the hooks here call `dist/cli.js hook --source muse` or `--source codex`. The quickest setup for every agent at once is `npx marshal-agents`.

**Muse** (1.4.15 or newer):

```
muse plugins marketplace add marshal https://github.com/lb2002/marshal
muse plugins install marshal@marshal
muse plugins approve marshal
```

Muse runs third-party hooks only after `approve`. Without a decision from the headset the Muse permission hook answers `deny`, because Muse may treat an empty answer as allow. `muse-hooks/` holds one two-line entry file per hook, because Muse wants a separate file per hook.

**Codex** (not tried with a live Codex session yet):

```
codex plugin marketplace add lb2002/marshal
codex plugin add marshal@marshal
```

Then trust the hooks once with `/hooks` in Codex. OpenAI's docs say plugin hooks run only for plugins installed in the Codex desktop app; if they do not run for you, use `npx marshal-agents`, which writes `~/.codex/hooks.json` instead.

Start the bridge while you use Marshal: `npx marshal-agents start`.

Apache License 2.0, see `LICENSE`. Copyright 2026 Linus Baumberger.
