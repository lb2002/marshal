// Builds the installable packages from the bridge, so they always carry the same hooks as `lotse connect`:
//   plugins/marshal/   Claude Code plugin (hooks, the connect skill, the built bridge)
//   hooks/hooks.json   Gemini CLI extension hooks (gemini-extension.json sits at the repo root)
// Usage, from the repo root, after `npm run build` in bridge/:  node scripts/build-plugins.mjs
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "bridge", "dist");
if (!existsSync(join(dist, "cli.js"))) throw new Error("bridge/dist/cli.js is missing: run `npm run build` in bridge/ first");

const load = (f) => import(pathToFileURL(join(dist, f)).href);
const { buildHookSettings } = await load("install.js");
const { buildToolHooks } = await load("install-tools.js");
const bridge = JSON.parse(readFileSync(join(root, "bridge", "package.json"), "utf8"));
const lock = JSON.parse(readFileSync(join(root, "bridge", "package-lock.json"), "utf8"));
const json = (file, value) => {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(value, null, 2) + "\n");
};

// ---- Claude Code plugin ----
const plugin = join(root, "plugins", "marshal");
rmSync(join(plugin, "dist"), { recursive: true, force: true });
cpSync(dist, join(plugin, "dist"), { recursive: true, filter: (src) => !/\.(d\.ts|map)$/.test(src) });

// The bridge server needs `ws` (the hooks need nothing). Claude Code installs a plugin's dependencies only when
// package.json and package-lock.json are both there and every version is exact, so pin it from the bridge's lock.
const ws = lock.packages["node_modules/ws"];
json(join(plugin, "package.json"), {
  name: "marshal-plugin",
  version: bridge.version,
  private: true,
  type: "module",
  engines: bridge.engines,
  dependencies: { ws: ws.version },
});
json(join(plugin, "package-lock.json"), {
  name: "marshal-plugin",
  version: bridge.version,
  lockfileVersion: 3,
  requires: true,
  packages: {
    "": { name: "marshal-plugin", version: bridge.version, engines: bridge.engines, dependencies: { ws: ws.version } },
    "node_modules/ws": ws,
  },
});

// Same handlers as `connect` writes into ~/.claude/settings.json, minus the statusLine (plugins cannot set one).
const claudeCmd = 'node "${CLAUDE_PLUGIN_ROOT}/dist/cli.js"';
json(join(plugin, "hooks", "hooks.json"), { hooks: buildHookSettings(claudeCmd, "command").hooks });

// ---- Gemini CLI extension (the repo root is the extension) ----
const geminiCmd = 'node "${extensionPath}/plugins/marshal/dist/cli.js"';
json(join(root, "hooks", "hooks.json"), buildToolHooks("gemini", geminiCmd));

console.log("written: plugins/marshal (dist, package.json, package-lock.json, hooks/hooks.json), hooks/hooks.json");
