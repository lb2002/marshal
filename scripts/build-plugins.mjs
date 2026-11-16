// Builds the installable packages from the bridge, so they always carry the same hooks as `lotse connect`:
//   plugins/marshal/          Claude Code plugin (also what goes to Anthropic's plugin directory): hooks, the
//                             connect skill, the built bridge with `ws` under dist/vendor, the icon
//   plugins/marshal-agents/   the same bridge as a plugin for Muse and Codex, with their own manifests and hooks
//   hooks/hooks.json          Gemini CLI extension hooks (gemini-extension.json sits at the repo root)
// Claude Code reads `.claude-plugin/marketplace.json` (→ plugins/marshal); Codex and Muse read
// `.agents/plugins/marketplace.json` first (→ plugins/marshal-agents).
// Usage, from the repo root, after `npm run build` in bridge/:  node scripts/build-plugins.mjs
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "bridge", "dist");
if (!existsSync(join(dist, "cli.js"))) throw new Error("bridge/dist/cli.js is missing: run `npm run build` in bridge/ first");

const load = (f) => import(pathToFileURL(join(dist, f)).href);
const { buildHookSettings } = await load("install.js");
const { buildToolHooks } = await load("install-tools.js");
const { musePluginManifest } = await load("muse-plugin.js");
const bridge = JSON.parse(readFileSync(join(root, "bridge", "package.json"), "utf8"));
const lock = JSON.parse(readFileSync(join(root, "bridge", "package-lock.json"), "utf8"));
const json = (file, value) => {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(value, null, 2) + "\n");
};
const ws = lock.packages["node_modules/ws"];
const wsSource = join(root, "bridge", "node_modules", "ws");
if (JSON.parse(readFileSync(join(wsSource, "package.json"), "utf8")).version !== ws.version) {
  throw new Error("bridge/node_modules/ws does not match the lock; run npm ci in bridge/");
}

/**
 * The built bridge in `<folder>/dist`, with the one dependency `ws` (unmodified, MIT) copied to dist/vendor/ws and
 * the bare `ws` imports pointed at it: installing the plugin downloads nothing and there is no node_modules folder.
 * A package.json with `type: module` and no dependencies (so no plugin manager runs an install).
 */
function bridgeInto(folder) {
  rmSync(join(folder, "dist"), { recursive: true, force: true });
  rmSync(join(folder, "node_modules"), { recursive: true, force: true });
  rmSync(join(folder, "package-lock.json"), { force: true });
  cpSync(dist, join(folder, "dist"), { recursive: true, filter: (src) => !/\.(d\.ts|map)$/.test(src) });
  const vendor = join(folder, "dist", "vendor", "ws");
  cpSync(wsSource, vendor, { recursive: true });
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const p = join(dir, name);
      if (p.startsWith(join(folder, "dist", "vendor"))) continue;
      if (statSync(p).isDirectory()) walk(p);
      else if (p.endsWith(".js")) {
        const text = readFileSync(p, "utf8");
        const rel = relative(dirname(p), join(vendor, "wrapper.mjs")).replace(/\\/g, "/");
        const spec = rel.startsWith(".") ? rel : `./${rel}`;
        const out = text.replace(/from "ws";/g, `from "${spec}";`);
        if (out !== text) writeFileSync(p, out);
      }
    }
  };
  walk(join(folder, "dist"));
  json(join(folder, "package.json"), {
    name: "marshal-plugin",
    version: bridge.version,
    private: true,
    license: "Apache-2.0",
    type: "module",
    engines: bridge.engines,
    bundledVersions: { ws: ws.version },
  });
  cpSync(join(root, "LICENSE"), join(folder, "LICENSE"));
}

// ---- Claude Code plugin ----
const claude = join(root, "plugins", "marshal");
for (const old of [".codex-plugin", ".muse-plugin", "codex", "muse-hooks"]) rmSync(join(claude, old), { recursive: true, force: true });
bridgeInto(claude);
const claudeManifest = JSON.parse(readFileSync(join(claude, ".claude-plugin", "plugin.json"), "utf8"));
// Same handlers as `connect` writes into ~/.claude/settings.json, minus the statusLine (plugins cannot set one),
// in the exec form: `node` plus arguments, no shell line to parse.
const CLAUDE = "@@CLAUDE@@";
const claudeHooks = buildHookSettings(CLAUDE, "command").hooks;
for (const groups of Object.values(claudeHooks)) {
  for (const g of groups) {
    for (const h of g.hooks) {
      if (!h.command.startsWith(`${CLAUDE} hook `)) throw new Error("unexpected Claude hook command");
      h.args = ["${CLAUDE_PLUGIN_ROOT}/dist/cli.js", ...h.command.slice(CLAUDE.length + 1).split(" ")];
      h.command = "node";
    }
  }
}
json(join(claude, "hooks", "hooks.json"), { hooks: claudeHooks });

// ---- Muse and Codex plugin ----
const agents = join(root, "plugins", "marshal-agents");
bridgeInto(agents);

// Muse: a native manifest with Muse's own events. Muse wants a separate source file per hook, so each hook gets a
// two-line entry file calling the shim directly.
const PLACEHOLDER = "@@CLI@@";
const muse = musePluginManifest(`node "${PLACEHOLDER}"`);
rmSync(join(agents, "muse-hooks"), { recursive: true, force: true });
mkdirSync(join(agents, "muse-hooks"), { recursive: true });
for (const h of muse.capabilities.hooks) {
  const args = h.command.slice(2); // after node and the cli: ["hook", "--source", "muse", ...]
  if (h.command[1] !== PLACEHOLDER || args[0] !== "hook") throw new Error(`unexpected Muse hook command for ${h.id}`);
  writeFileSync(join(agents, "muse-hooks", `${h.id}.mjs`),
    `// Muse ${h.event} hook of Marshal (generated by scripts/build-plugins.mjs).\nimport { runHook } from "../dist/hook-cli.js";\nvoid runHook(${JSON.stringify(args.slice(1))});\n`);
  h.command = ["node", `muse-hooks/${h.id}.mjs`];
}
json(join(agents, ".muse-plugin", "plugin.json"), {
  ...muse, name: "marshal", version: claudeManifest.version,
  description: claudeManifest.description.replace("your Claude Code sessions", "your Muse and Codex sessions"),
});

// Codex: passes the plugin folder only as the environment variable PLUGIN_ROOT and runs hooks through cmd.exe on
// Windows, hence the separate `commandWindows`.
const CODEX = "@@CODEX@@";
const codexHooks = buildToolHooks("codex", CODEX);
for (const groups of Object.values(codexHooks.hooks)) {
  for (const g of groups) {
    for (const h of g.hooks) {
      if (!h.command.startsWith(`${CODEX} hook `)) throw new Error("unexpected Codex hook command");
      const args = h.command.slice(CODEX.length);
      h.command = `node "$PLUGIN_ROOT/dist/cli.js"${args}`;
      h.commandWindows = `node "%PLUGIN_ROOT%\\dist\\cli.js"${args}`;
    }
  }
}
json(join(agents, "hooks", "hooks.json"), codexHooks);
json(join(agents, ".codex-plugin", "plugin.json"), {
  name: "marshal",
  version: claudeManifest.version,
  description: claudeManifest.description.replace("your Claude Code sessions", "your Codex sessions"),
  author: claudeManifest.author,
  homepage: claudeManifest.homepage,
  repository: claudeManifest.repository,
  license: claudeManifest.license,
});
json(join(root, ".agents", "plugins", "marketplace.json"), {
  name: "marshal",
  interface: { displayName: "Marshal" },
  plugins: [{
    name: "marshal",
    source: { source: "local", path: "./plugins/marshal-agents" },
    policy: { installation: "AVAILABLE", authentication: "ON_INSTALL" },
    category: "Productivity",
  }],
});

// ---- Gemini CLI extension (the repo root is the extension) ----
const geminiCmd = 'node "${extensionPath}/plugins/marshal/dist/cli.js"';
json(join(root, "hooks", "hooks.json"), buildToolHooks("gemini", geminiCmd));

console.log("written: plugins/marshal, plugins/marshal-agents, .agents/plugins/marketplace.json, hooks/hooks.json");
