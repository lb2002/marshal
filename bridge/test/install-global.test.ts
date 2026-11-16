// `connect --all` / `disconnect --all`: the user-level hook files of all four tools, always in a fake HOME.
// Nothing here may ever touch the real home: every call gets `home` and `env` explicitly.
import { describe, expect, it } from "vitest";
import { chmodSync, existsSync, mkdirSync, readFileSync, readdirSync, symlinkSync, unlinkSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { connectAll, disconnectAll, globalFile, mergeGlobal, removeGlobal, type GlobalOpts } from "../src/install-global.js";
import { MUSE_NO_GLOBAL, findMuseBin, manifestText, musePluginDir, realMuseRunner, validateInTemp, type MusePluginRecord, type MuseRunner } from "../src/muse-plugin.js";
import { buildHookSettings } from "../src/install.js";
import { buildToolHooks } from "../src/install-tools.js";
import type { InstallTool } from "../src/install-run.js";
import { entryChanges, parseChangeLine } from "../src/settings-diff.js";
import { tempDir } from "./tmp.js";

const CMD = 'node "/x/dist/cli.js"';
const TOOLS: InstallTool[] = ["claude", "muse", "codex", "gemini"];
/** The tools whose user settings file Lotse writes (Muse gets a user plugin instead). */
const SETTINGS: InstallTool[] = ["claude", "codex", "gemini"];

/** A stand-in for the Muse binary: records the calls, keeps a plugin list, answers validate with `verdict`. */
interface StubMuse extends MuseRunner {
  calls: string[];
  plugins: MusePluginRecord[];
  verdict: { valid: true } | { valid: false; message: string };
  validated: { dir: string; text: string }[];
  failInstall: boolean;
}
function stubMuse(): StubMuse {
  const m: StubMuse = {
    calls: [],
    plugins: [],
    verdict: { valid: true },
    validated: [],
    failInstall: false,
    validate(dir) {
      m.calls.push("validate");
      m.validated.push({ dir, text: readFileSync(join(dir, ".muse-plugin", "plugin.json"), "utf8") });
      return m.verdict;
    },
    install(dir) {
      m.calls.push("install");
      if (m.failInstall) throw new Error("install refused");
      m.plugins.push({ id: "lotse", source: dir });
    },
    update() {
      m.calls.push("update");
    },
    list() {
      m.calls.push("list");
      return [...m.plugins];
    },
    remove(id) {
      m.calls.push("remove");
      m.plugins = m.plugins.filter((p) => p.id !== id);
    },
  };
  return m;
}

/** A fake HOME with every tool's config folder, and an env that points nowhere else. */
function fakeHome(): { home: string; env: NodeJS.ProcessEnv } {
  const home = tempDir("lotse-global-");
  for (const d of [".claude", join(".config", "muse"), ".codex", ".gemini"]) mkdirSync(join(home, d), { recursive: true });
  expect(home).not.toBe(homedir());
  return { home, env: {} };
}

function opts(home: string, env: NodeJS.ProcessEnv, over: Partial<GlobalOpts> = {}): GlobalOpts & { lines: string[]; asked: string[]; errs: string[] } {
  const lines: string[] = [];
  const asked: string[] = [];
  const errs: string[] = [];
  return {
    tools: TOOLS,
    explicit: false,
    home,
    env,
    bridgeCmd: CMD,
    yes: true,
    ask: async (q) => {
      asked.push(q);
      return "j";
    },
    log: (l) => lines.push(l),
    err: (l) => errs.push(l),
    muse: stubMuse(),
    lines,
    asked,
    errs,
    ...over,
  };
}

const ours = (tool: InstallTool) => (tool === "claude" ? buildHookSettings(CMD, "command") : buildToolHooks(tool, CMD)) as { hooks: Record<string, object[]>; statusLine?: object };
const FOREIGN = { type: "command", command: "my-own-guard.sh" };
/** A foreign event Lotse does not use (Muse only accepts its own event names, so a real one there). */
const OTHER = (_tool: InstallTool) => "CustomEvent";

/** Foreign content per tool, with a foreign hook in an event Lotse uses too. */
function foreignText(tool: InstallTool): string {
  const event = tool === "gemini" ? "BeforeTool" : "PreToolUse";
  const base: Record<string, unknown> = { theme: "dark", hooks: { [event]: [{ matcher: "Bash", hooks: [FOREIGN] }], [OTHER(tool)]: [{ hooks: [FOREIGN] }] } };
  if (tool === "muse") base.schema_version = 1;
  if (tool === "claude") base.statusLine = { type: "command", command: "my-status" };
  // Unusual formatting on purpose: disconnect must give these exact bytes back.
  return JSON.stringify(base, null, 4).replace(/\n/g, "\r\n") + "\r\n";
}

describe("globalFile (user-level targets)", () => {
  it("names each tool's user file, with the tools' own env overrides", () => {
    const h = join("/h");
    expect(globalFile("claude", h, {})).toBe(join(h, ".claude", "settings.json"));
    expect(globalFile("muse", h, {})).toBe(join(h, ".config", "muse", "settings.json"));
    expect(globalFile("codex", h, {})).toBe(join(h, ".codex", "hooks.json"));
    expect(globalFile("gemini", h, {})).toBe(join(h, ".gemini", "settings.json"));
    expect(globalFile("claude", h, { CLAUDE_CONFIG_DIR: "/c" })).toBe(join(resolve("/c"), "settings.json"));
    expect(globalFile("muse", h, { XDG_CONFIG_HOME: "/x" })).toBe(join(resolve("/x"), "muse", "settings.json"));
    expect(globalFile("codex", h, { CODEX_HOME: "/k" })).toBe(join(resolve("/k"), "hooks.json"));
    expect(globalFile("gemini", h, { GEMINI_CLI_HOME: "/g" })).toBe(join(resolve("/g"), ".gemini", "settings.json"));
  });
});

describe("mergeGlobal / removeGlobal (pure)", () => {
  for (const tool of TOOLS) {
    it(`${tool}: adds Lotse's entries next to foreign ones, and removes exactly them again`, () => {
      const foreign = JSON.parse(foreignText(tool));
      const merged = mergeGlobal(foreign, tool, CMD);
      if ("error" in merged) throw new Error(merged.error);
      const m = merged.settings as { hooks: Record<string, unknown[]>; theme: string; statusLine?: unknown };
      expect(m.theme).toBe("dark");
      for (const [event, groups] of Object.entries(ours(tool).hooks)) expect(m.hooks[event]).toEqual(expect.arrayContaining(groups));
      const event = tool === "gemini" ? "BeforeTool" : "PreToolUse";
      expect(m.hooks[event][0]).toEqual({ matcher: "Bash", hooks: [FOREIGN] });
      expect(m.hooks[OTHER(tool)]).toEqual([{ hooks: [FOREIGN] }]);
      // A foreign statusLine stays; Lotse's is only added where there is none.
      if (tool === "claude") expect(m.statusLine).toEqual({ type: "command", command: "my-status" });
      expect(removeGlobal(merged.settings, tool, CMD)).toEqual(foreign);
    });
  }
  it("starts from nothing (Muse needs schema_version 1) and removal leaves the skeleton", () => {
    const c = mergeGlobal(null, "claude", CMD) as { settings: Record<string, unknown> };
    expect(c.settings).toEqual(buildHookSettings(CMD, "command"));
    const m = mergeGlobal(null, "muse", CMD) as { settings: Record<string, unknown> };
    expect(m.settings).toEqual({ schema_version: 1, ...(buildToolHooks("muse", CMD) as object) });
    expect(removeGlobal(m.settings, "muse", CMD)).toEqual({ schema_version: 1 });
  });
  it("ownership is exact: a look-alike (other timeout, other path, extra key) stays", () => {
    const looks = [
      { type: "command", command: `${CMD} hook --source codex` , timeout: 5 },
      { type: "command", command: 'node "/other/cli.js" hook --source codex' },
      { type: "command", command: `${CMD} hook --source codex`, extra: true },
    ];
    const s = { hooks: { Stop: [{ hooks: looks }] } };
    const merged = mergeGlobal(s, "codex", CMD) as { settings: object };
    expect(removeGlobal(merged.settings, "codex", CMD)).toEqual(s);
  });
  it("keeps a foreign empty group that was there before", () => {
    const s = { hooks: { Stop: [{ hooks: [] }] } };
    const merged = mergeGlobal(s, "claude", CMD) as { settings: object };
    expect(removeGlobal(merged.settings, "claude", CMD)).toEqual(s);
  });
  it("refuses a file whose hooks are not the documented shape", () => {
    expect(mergeGlobal({ hooks: [] }, "claude", CMD)).toEqual({ error: expect.stringMatching(/hooks/) });
    expect(mergeGlobal({ hooks: { Stop: { x: 1 } } }, "codex", CMD)).toEqual({ error: expect.stringMatching(/Stop/) });
  });
});

describe("connectAll / disconnectAll on disk (fake HOME)", () => {
  for (const tool of SETTINGS) {
    it(`${tool}: install, idempotent re-install, foreign hooks kept, backup written, disconnect gives the original bytes back`, async () => {
      const { home, env } = fakeHome();
      const file = globalFile(tool, home, env);
      const original = foreignText(tool);
      writeFileSync(file, original);

      const o = opts(home, env, { tools: [tool], explicit: true });
      expect(await connectAll(o)).toMatchObject({ ok: true, changed: [file] });
      expect(o.lines).toContain(`  ${file}`);
      const installed = readFileSync(file, "utf8");
      expect(installed).toContain("my-own-guard.sh");
      expect(installed).toContain(JSON.stringify(`${CMD} hook --source ${tool}`).slice(1, -1));
      const backups = readdirSync(dirname(file)).filter((f) => f.startsWith(`${file.split(/[\\/]/).pop()}.lotse-backup-`));
      expect(backups).toHaveLength(1);
      expect(readFileSync(join(dirname(file), backups[0]), "utf8")).toBe(original);

      // Second run: nothing changes, nothing is written, no new backup.
      const again = opts(home, env, { tools: [tool], explicit: true });
      expect(await connectAll(again)).toMatchObject({ ok: true, changed: [] });
      expect(readFileSync(file, "utf8")).toBe(installed);
      expect(readdirSync(dirname(file)).filter((f) => f.includes(".lotse-backup-"))).toHaveLength(1);

      const d = opts(home, env, { tools: [tool], explicit: true });
      expect(await disconnectAll(d)).toMatchObject({ ok: true, changed: [file] });
      expect(d.lines).toContain(`  ${file}`);
      expect(readFileSync(file, "utf8")).toBe(original);
      // The backup stays.
      expect(readdirSync(dirname(file)).filter((f) => f.includes(".lotse-backup-")).length).toBeGreaterThanOrEqual(1);
      // A second disconnect finds nothing.
      expect(await disconnectAll(opts(home, env, { tools: [tool], explicit: true }))).toMatchObject({ ok: true, changed: [] });
    });
  }

  it("a file Lotse created is removed again on disconnect; no backup is needed for it", async () => {
    const { home, env } = fakeHome();
    const muse = stubMuse();
    expect(await connectAll(opts(home, env, { muse }))).toMatchObject({ ok: true });
    for (const tool of SETTINGS) expect(existsSync(globalFile(tool, home, env))).toBe(true);
    expect(await disconnectAll(opts(home, env, { muse }))).toMatchObject({ ok: true });
    expect(muse.plugins).toEqual([]);
    for (const tool of SETTINGS) {
      const f = globalFile(tool, home, env);
      expect(existsSync(f)).toBe(false);
      expect(readdirSync(dirname(f))).toEqual([]);
    }
  });

  it("keeps other changes made after the install (re-serialised, foreign keys intact)", async () => {
    const { home, env } = fakeHome();
    const file = globalFile("gemini", home, env);
    writeFileSync(file, foreignText("gemini"));
    await connectAll(opts(home, env, { tools: ["gemini"], explicit: true }));
    const now = JSON.parse(readFileSync(file, "utf8"));
    now.model = "other";
    writeFileSync(file, JSON.stringify(now, null, 2));
    await disconnectAll(opts(home, env, { tools: ["gemini"], explicit: true }));
    expect(JSON.parse(readFileSync(file, "utf8"))).toEqual({ ...JSON.parse(foreignText("gemini")), model: "other" });
  });

  it("asks once, lists the files first, and writes nothing on no", async () => {
    const { home, env } = fakeHome();
    const o = opts(home, env, { yes: false, ask: async (q) => (o.asked.push(q), "n") });
    expect(await connectAll(o)).toMatchObject({ ok: false, cancelled: true });
    expect(o.asked).toEqual(["Install Marshal hooks globally for: Claude Code, Muse, Codex CLI, Gemini CLI ? [y/N] "]);
    for (const tool of SETTINGS) {
      expect(o.lines).toContain(`  ${globalFile(tool, home, env)}`);
      expect(existsSync(globalFile(tool, home, env))).toBe(false);
    }
    expect(o.lines.join("\n")).toContain(`Muse plugin "lotse" (user), bundle in ${musePluginDir(home)}`);
    expect(existsSync(musePluginDir(home))).toBe(false);
    for (const no of ["", "nein", "N", "jein"]) {
      const p = opts(home, env, { yes: false, ask: async () => no });
      expect(await connectAll(p)).toMatchObject({ ok: false, cancelled: true });
    }
  });

  it("goes on with j, ja, y or yes; --yes asks nothing", async () => {
    for (const yes of ["j", "J", "ja", "y", "yes"]) {
      const { home, env } = fakeHome();
      const o = opts(home, env, { yes: false, ask: async (q) => (o.asked.push(q), yes) });
      expect(await connectAll(o)).toMatchObject({ ok: true });
      expect(o.asked).toHaveLength(1);
    }
    const { home, env } = fakeHome();
    const o = opts(home, env, { yes: true });
    expect(await connectAll(o)).toMatchObject({ ok: true });
    expect(o.asked).toEqual([]);
  });

  it("does not ask when everything is already installed", async () => {
    const { home, env } = fakeHome();
    const muse = stubMuse();
    await connectAll(opts(home, env, { muse }));
    const o = opts(home, env, { yes: false, muse });
    expect(await connectAll(o)).toMatchObject({ ok: true, changed: [] });
    expect(o.asked).toEqual([]);
  });

  it("prints the trust step for each tool, and the Muse fail-closed warning", async () => {
    const { home, env } = fakeHome();
    const o = opts(home, env);
    await connectAll(o);
    const text = o.lines.join("\n");
    expect(text).toMatch(/Claude Code:.*\/hooks/);
    expect(text).toMatch(/Muse:.*muse plugins approve lotse/);
    expect(text).toMatch(/Codex CLI:.*\/hooks/);
    expect(text).toMatch(/Gemini CLI:.*trusted folder/i);
    expect(text).toMatch(/disconnect --all/);
  });

  it("with --tool all skips a tool whose config folder is missing; an explicit --tool creates it", async () => {
    const home = tempDir("lotse-global-");
    mkdirSync(join(home, ".claude"));
    const o = opts(home, {});
    expect(await connectAll(o)).toMatchObject({ ok: true, changed: [globalFile("claude", home, {})] });
    expect(existsSync(join(home, ".gemini"))).toBe(false);
    expect(o.lines.join("\n")).toMatch(/Gemini CLI: .*skipped/);
    expect(await connectAll(opts(home, {}, { tools: ["gemini"], explicit: true }))).toMatchObject({ ok: true });
    expect(existsSync(globalFile("gemini", home, {}))).toBe(true);
  });

  it("refuses broken JSON or a symlink and then writes nothing at all", async () => {
    const { home, env } = fakeHome();
    writeFileSync(globalFile("codex", home, env), "{ not json");
    const o = opts(home, env);
    expect(await connectAll(o)).toMatchObject({ ok: false });
    for (const tool of ["claude", "muse", "gemini"] as const) expect(existsSync(globalFile(tool, home, env))).toBe(false);
    expect(existsSync(musePluginDir(home))).toBe(false);
    expect(readFileSync(globalFile("codex", home, env), "utf8")).toBe("{ not json");
  });

  it("follows the env overrides inside the fake HOME", async () => {
    const { home } = fakeHome();
    const env = { CLAUDE_CONFIG_DIR: join(home, "alt-claude"), XDG_CONFIG_HOME: join(home, "xdg") };
    mkdirSync(env.CLAUDE_CONFIG_DIR);
    mkdirSync(join(env.XDG_CONFIG_HOME, "muse"), { recursive: true });
    const muse = stubMuse();
    await connectAll(opts(home, env, { tools: ["claude", "muse"], muse }));
    expect(existsSync(join(env.CLAUDE_CONFIG_DIR, "settings.json"))).toBe(true);
    // Muse is found by its XDG folder and gets the plugin; its settings file stays untouched.
    expect(muse.calls).toContain("install");
    expect(existsSync(join(env.XDG_CONFIG_HOME, "muse", "settings.json"))).toBe(false);
    expect(existsSync(join(home, ".claude", "settings.json"))).toBe(false);
  });
});

/** File symlinks need a privilege on Windows; without it the symlink test is skipped (and reported). */
const canSymlink = (() => {
  const d = tempDir("lotse-symcheck-");
  try {
    symlinkSync(join(d, "nowhere"), join(d, "link"), "file");
    unlinkSync(join(d, "link"));
    return true;
  } catch {
    return false;
  }
})();

const escapeRe = (s: string) => s.replace(/[\\^$.*+?()[\]{}|]/g, "\\$&");

describe("Muse: a user plugin that Muse itself validates, never the settings file", () => {
  it("validates the bundle in a temp copy first, then installs it; the Muse settings file stays byte-equal", async () => {
    const { home, env } = fakeHome();
    const settings = globalFile("muse", home, env);
    const original = JSON.stringify({ schema_version: 1, theme: "x" });
    writeFileSync(settings, original);
    const muse = stubMuse();
    const r = await connectAll(opts(home, env, { tools: ["muse"], explicit: true, muse }));
    expect(r).toMatchObject({ ok: true, tools: ["muse"], museSkipped: false, changed: [musePluginDir(home)] });
    expect(muse.calls.filter((c) => c !== "list")).toEqual(["validate", "install"]);
    expect(muse.validated[0].dir).not.toBe(musePluginDir(home));
    expect(muse.validated[0].text).toBe(manifestText(CMD));
    expect(readFileSync(join(musePluginDir(home), ".muse-plugin", "plugin.json"), "utf8")).toBe(manifestText(CMD));
    expect(readFileSync(settings, "utf8")).toBe(original);
    expect(readdirSync(dirname(settings))).toEqual(["settings.json"]);
  });
  it("the manifest has one PascalCase hook per Muse event; only the approval hook names its event", () => {
    const m = JSON.parse(manifestText(CMD)) as { capabilities: { hooks: { id: string; event: string; command: string[] }[] } };
    const hooks = m.capabilities.hooks;
    expect(hooks.map((h) => h.event)).toEqual(Object.keys(ours("muse").hooks));
    for (const h of hooks) expect(h.event[0]).toBe(h.event[0].toUpperCase());
    expect(hooks.find((h) => h.event === "PermissionRequest")!.command).toEqual(["node", "/x/dist/cli.js", "hook", "--source", "muse", "--event", "PermissionRequest"]);
    expect(hooks.find((h) => h.event === "Stop")!.command).toEqual(["node", "/x/dist/cli.js", "hook", "--source", "muse"]);
  });
  it("is idempotent: an installed, unchanged plugin is neither validated, installed nor asked about again", async () => {
    const { home, env } = fakeHome();
    const muse = stubMuse();
    await connectAll(opts(home, env, { tools: ["muse"], explicit: true, muse }));
    muse.calls.length = 0;
    const o = opts(home, env, { tools: ["muse"], explicit: true, muse, yes: false });
    expect(await connectAll(o)).toMatchObject({ ok: true, changed: [], tools: ["muse"] });
    expect(muse.calls).toEqual(["list"]);
    expect(o.asked).toEqual([]);
  });
  it("Muse rejects the bundle: Muse is skipped with the message, nothing Muse is written, the others still install", async () => {
    const { home, env } = fakeHome();
    const muse = stubMuse();
    muse.verdict = { valid: false, message: "hook capability `perm` event `permissionRequest` is unsupported" };
    const o = opts(home, env, { muse });
    const r = await connectAll(o);
    expect(r).toMatchObject({ ok: true, museSkipped: true, tools: ["claude", "codex", "gemini"] });
    expect(o.lines).toContain(MUSE_NO_GLOBAL);
    expect(MUSE_NO_GLOBAL).toBe("Muse: no valid global hook location found. Please connect Muse per project (lotse connect --project … --tool muse)");
    expect(o.errs.join("\n")).toContain("permissionRequest");
    expect(muse.calls).not.toContain("install");
    expect(existsSync(musePluginDir(home))).toBe(false);
    expect(existsSync(globalFile("muse", home, env))).toBe(false);
    for (const tool of SETTINGS) expect(existsSync(globalFile(tool, home, env))).toBe(true);
  });
  it("no Muse binary: Muse is skipped with the same message", async () => {
    const { home, env } = fakeHome();
    const o = opts(home, env, { muse: null });
    expect(await connectAll(o)).toMatchObject({ ok: true, museSkipped: true });
    expect(o.lines).toContain(MUSE_NO_GLOBAL);
    expect(existsSync(musePluginDir(home))).toBe(false);
  });
  it("a foreign plugin named lotse is left alone: Muse is skipped, and disconnect does not remove it", async () => {
    const { home, env } = fakeHome();
    const muse = stubMuse();
    muse.plugins = [{ id: "lotse", source: join(home, "someone-else") }];
    const o = opts(home, env, { tools: ["muse"], explicit: true, muse });
    expect(await connectAll(o)).toMatchObject({ ok: true, museSkipped: true });
    expect(o.errs.join("\n")).toMatch(/another Muse plugin/);
    const d = opts(home, env, { tools: ["muse"], explicit: true, muse });
    await disconnectAll(d);
    expect(muse.calls).not.toContain("remove");
    expect(muse.plugins).toHaveLength(1);
    expect(d.lines.join("\n")).toMatch(/does not come from/);
  });
  it("a failed plugin install rolls back the settings files written in this run; ok is false", async () => {
    const { home, env } = fakeHome();
    const claude = globalFile("claude", home, env);
    writeFileSync(claude, foreignText("claude"));
    const muse = stubMuse();
    muse.failInstall = true;
    const o = opts(home, env, { muse });
    expect(await connectAll(o)).toMatchObject({ ok: false });
    expect(readFileSync(claude, "utf8")).toBe(foreignText("claude"));
    expect(existsSync(globalFile("codex", home, env))).toBe(false);
    expect(existsSync(musePluginDir(home))).toBe(false);
    expect(o.errs.join("\n")).toMatch(/installing Muse plugin "lotse" failed.*install refused/);
    expect(o.errs.join("\n")).toMatch(/Left changed: none/);
  });
  it("disconnect removes Lotse's plugin and the entries an earlier Lotse version wrote into the Muse settings file", async () => {
    const { home, env } = fakeHome();
    const settings = globalFile("muse", home, env);
    const foreign = { schema_version: 1, hooks: { PostToolBatch: [{ hooks: [FOREIGN] }] } };
    writeFileSync(settings, JSON.stringify((mergeGlobal(foreign, "muse", CMD) as { settings: object }).settings));
    const muse = stubMuse();
    await connectAll(opts(home, env, { tools: ["muse"], explicit: true, muse }));
    const d = opts(home, env, { tools: ["muse"], explicit: true, muse });
    expect(await disconnectAll(d)).toMatchObject({ ok: true, changed: [settings, musePluginDir(home)] });
    expect(muse.plugins).toEqual([]);
    expect(JSON.parse(readFileSync(settings, "utf8"))).toEqual(foreign);
    expect(d.lines).toContain('  Muse plugin "lotse"');
  });
  const bin = findMuseBin(process.env);
  it.skipIf(bin === null)("the installed Muse binary itself accepts the Lotse plugin (plugins validate, offline, temp copy)", () => {
    // Muse writes trace logs into its data folder: give it a temp home so nothing lands in the real one.
    const t = tempDir("lotse-muse-env-");
    const env = { ...process.env, HOME: t, USERPROFILE: t, XDG_CONFIG_HOME: join(t, "config"), XDG_DATA_HOME: join(t, "data") };
    const runner = realMuseRunner(env) as MuseRunner;
    const text = manifestText('node "C:/lotse/bridge/dist/cli.js"');
    expect(validateInTemp(runner, text)).toEqual({ valid: true });
    const camel = text.replace('"event": "PermissionRequest"', '"event": "permissionRequest"');
    expect(validateInTemp(runner, camel)).toMatchObject({ valid: false, message: expect.stringMatching(/permissionRequest/) });
  });
});

describe("safe apply", () => {
  it("re-plans after the question: a file changed meanwhile aborts the run and nothing is written", async () => {
    const { home, env } = fakeHome();
    const claude = globalFile("claude", home, env);
    writeFileSync(claude, '{"theme":"dark"}');
    const o = opts(home, env, {
      yes: false,
      ask: async () => {
        writeFileSync(claude, '{"theme":"light"}');
        return "j";
      },
    });
    expect(await connectAll(o)).toMatchObject({ ok: false });
    expect(o.errs.join("\n")).toContain(`${claude}: changed during the question, nothing written`);
    expect(readFileSync(claude, "utf8")).toBe('{"theme":"light"}');
    for (const tool of ["muse", "codex", "gemini"] as const) expect(existsSync(globalFile(tool, home, env))).toBe(false);
    expect(existsSync(musePluginDir(home))).toBe(false);
    expect(readdirSync(dirname(claude)).filter((f) => f.includes("lotse-backup"))).toEqual([]);
  });

  it("a failed write rolls back the files already written in this run and reports it; ok is false", async () => {
    const { home, env } = fakeHome();
    const claude = globalFile("claude", home, env);
    const gemini = globalFile("gemini", home, env);
    const original = foreignText("claude");
    writeFileSync(claude, original);
    writeFileSync(gemini, foreignText("gemini"));
    const o = opts(home, env, {
      yes: false,
      ask: async () => {
        chmodSync(gemini, 0o444); // the last file cannot be written
        return "j";
      },
    });
    try {
      const r = await connectAll(o);
      expect(r).toMatchObject({ ok: false });
      expect(readFileSync(claude, "utf8")).toBe(original);
      expect(existsSync(globalFile("muse", home, env))).toBe(false);
      expect(existsSync(musePluginDir(home))).toBe(false);
      expect(existsSync(globalFile("codex", home, env))).toBe(false);
      expect(readFileSync(gemini, "utf8")).toBe(foreignText("gemini"));
      const err = o.errs.join("\n");
      expect(err).toContain(gemini);
      expect(err).toMatch(/restored/i);
      expect(err).toMatch(/Left changed: none/);
    } finally {
      for (const f of readdirSync(dirname(gemini))) chmodSync(join(dirname(gemini), f), 0o644);
    }
  });

  it.skipIf(!canSymlink)("refuses a symlink, even a dangling one, instead of writing through it", async () => {
    const { home, env } = fakeHome();
    const target = join(home, "elsewhere.json");
    symlinkSync(target, globalFile("codex", home, env), "file");
    const o = opts(home, env, { tools: ["codex"], explicit: true });
    expect(await connectAll(o)).toMatchObject({ ok: false });
    expect(existsSync(target)).toBe(false);
  });
});

describe("one-command setup (lotse connect with no flags)", () => {
  /** Every settings file of every tool, by path: its bytes, or null when it does not exist. */
  const snapshot = (home: string, env: NodeJS.ProcessEnv) =>
    Object.fromEntries([...TOOLS.map((t) => globalFile(t, home, env)), join(home, ".config", "muse", "settings.json")].map((f) => [f, existsSync(f) ? readFileSync(f).toString("base64") : null]));

  /** The entries listed under each file (`  <file>` lines start a block; `+`/`-` lines belong to it). */
  function listed(lines: string[], files: string[]): Map<string, string[]> {
    const out = new Map<string, string[]>(files.map((f) => [f, []]));
    let cur: string | null = null;
    for (const l of lines) {
      const f = files.find((x) => l === `  ${x}` || l.startsWith(`  ${x} (`));
      if (f !== undefined) {
        cur = f;
        continue;
      }
      const c = parseChangeLine(l);
      if (c && cur !== null && /^ {6}[+-] /.test(l)) out.get(cur)!.push(`${c.op} ${c.key} ${c.value}`);
      else if (!l.startsWith("    ")) cur = null;
    }
    return out;
  }
  const parsed = (f: string): object | null => (existsSync(f) ? JSON.parse(readFileSync(f, "utf8").replace(/^\uFEFF/, "")) : null);
  const asSet = (cs: { op: string; key: string; value: string }[]) => cs.map((c) => `${c.op} ${c.key} ${c.value}`).sort();

  it("the list before the question is exactly what is written and removed, legacy handlers and an old status line included", async () => {
    const { home, env } = fakeHome();
    const NEW = '"C:/node/node.exe" "/x/dist/cli.js"'; // its legacy spelling is CMD, `node "/x/dist/cli.js"`
    // Claude: the legacy handlers of every event and the legacy status line, plus a foreign hook; Codex: legacy
    // handlers next to a foreign one; Gemini: nothing yet.
    const legacyClaude = { ...ours("claude"), theme: "dark" } as { hooks: Record<string, object[]>; statusLine?: object; theme: string };
    legacyClaude.hooks.PreToolUse = [{ matcher: "Bash", hooks: [FOREIGN] }, ...legacyClaude.hooks.PreToolUse];
    writeFileSync(globalFile("claude", home, env), JSON.stringify(legacyClaude, null, 2));
    const legacyCodex = ours("codex");
    legacyCodex.hooks.Stop = [{ hooks: [FOREIGN] }, ...legacyCodex.hooks.Stop];
    writeFileSync(globalFile("codex", home, env), JSON.stringify(legacyCodex));
    const files = SETTINGS.map((t) => globalFile(t, home, env));
    const before = new Map(files.map((f) => [f, parsed(f)]));

    const c = opts(home, env, { bridgeCmd: NEW, tools: SETTINGS, yes: true });
    expect(await connectAll(c)).toMatchObject({ ok: true });
    const shown = listed(c.lines, files);
    for (const f of files) expect(shown.get(f)!.sort(), f).toEqual(asSet(entryChanges(before.get(f), parsed(f))));
    // The legacy handlers and the old status line are among the listed removals.
    const claude = shown.get(files[0])!;
    expect(claude.filter((x) => x.startsWith("- ") && x.includes('node \\"/x/dist/cli.js\\"')).length).toBeGreaterThan(10);
    expect(claude.some((x) => x.startsWith("- statusLine "))).toBe(true);
    expect(claude.some((x) => x.startsWith("+ statusLine "))).toBe(true);
    expect(claude.some((x) => x.includes("my-own-guard.sh"))).toBe(false);

    // And the way back: disconnect lists exactly what it takes out.
    const mid = new Map(files.map((f) => [f, parsed(f)]));
    const d = opts(home, env, { bridgeCmd: NEW, tools: SETTINGS, yes: true });
    expect(await disconnectAll(d)).toMatchObject({ ok: true });
    const removed = listed(d.lines, files);
    for (const f of files) expect(removed.get(f)!.sort(), `disconnect ${f}`).toEqual(asSet(entryChanges(mid.get(f), parsed(f))));
  });

  it("lists every entry before the one question and keeps a foreign status line", async () => {
    const { home, env } = fakeHome();
    writeFileSync(globalFile("claude", home, env), foreignText("claude")); // has a statusLine already
    const o = opts(home, env, { yes: false, ask: async (q) => (o.asked.push(q), "n") });
    await connectAll(o);
    const at = (needle: string) => o.lines.findIndex((l) => l.includes(needle));
    expect(at(globalFile("codex", home, env))).toBeLessThan(at("Codex CLI: 0 entries removed, 10 added"));
    expect(o.lines[at("Codex CLI: 0 entries removed, 10 added") + 1]).toMatch(/^ {6}\+ hooks\.SessionStart: \{/);
    expect(o.lines.some((l) => l.includes("statusLine"))).toBe(false);
    expect(o.lines.join(" ")).toMatch(/Muse: one Marshal hook each for SessionStart, /);
    expect(o.asked).toHaveLength(1);
  });

  it("disconnect lists, asks once, and removes nothing on no or without a terminal", async () => {
    const { home, env } = fakeHome();
    const muse = stubMuse();
    await connectAll(opts(home, env, { muse }));
    const installed = snapshot(home, env);
    const no = opts(home, env, { yes: false, muse, ask: async (q) => (no.asked.push(q), "n") });
    expect(await disconnectAll(no)).toMatchObject({ ok: false, cancelled: true });
    expect(no.asked).toEqual(["Remove Marshal hooks for: Claude Code, Muse, Codex CLI, Gemini CLI ? [y/N] "]);
    expect(no.lines.some((l) => /^ {6}- hooks\.SessionStart: /.test(l))).toBe(true);
    expect(snapshot(home, env)).toEqual(installed);
    expect(muse.plugins).toHaveLength(1);
    const pipe = opts(home, env, { yes: false, interactive: false, muse });
    expect(await disconnectAll(pipe)).toMatchObject({ ok: false, cancelled: true });
    expect(pipe.asked).toEqual([]);
    expect(pipe.lines.join(" ")).toContain("No terminal to ask on, so nothing was changed. To remove them without the question: lotse disconnect --yes");
    expect(snapshot(home, env)).toEqual(installed);
    const yes = opts(home, env, { yes: false, muse, ask: async () => "y" });
    expect(await disconnectAll(yes)).toMatchObject({ ok: true });
    expect(muse.plugins).toEqual([]);
  });

  it("without a terminal and without --yes it changes nothing, does not ask, and says so", async () => {
    const { home, env } = fakeHome();
    writeFileSync(globalFile("gemini", home, env), foreignText("gemini"));
    const before = snapshot(home, env);
    const muse = stubMuse();
    const o = opts(home, env, { yes: false, interactive: false, muse });
    expect(await connectAll(o)).toMatchObject({ ok: false, cancelled: true });
    expect(o.asked).toEqual([]);
    expect(o.lines.join("\n")).toContain("No terminal to ask on, so nothing was changed. To install without the question: lotse connect --yes");
    expect(snapshot(home, env)).toEqual(before);
    expect(existsSync(musePluginDir(home))).toBe(false);
    expect(muse.calls.filter((c) => c === "install")).toEqual([]);
    // With --yes it installs without a terminal.
    expect(await connectAll(opts(home, env, { yes: true, interactive: false, muse }))).toMatchObject({ ok: true });
    expect(readFileSync(globalFile("gemini", home, env), "utf8")).toContain("hook --source gemini");
  });

  it("disconnect gives every settings file back byte for byte (present ones with odd formatting, absent ones absent)", async () => {
    const { home, env } = fakeHome();
    // Claude and Gemini have files (CRLF, 4-space indent, foreign hooks, BOM on Gemini), Codex has none, Muse has a settings file Marshal must never touch.
    writeFileSync(globalFile("claude", home, env), foreignText("claude"));
    writeFileSync(globalFile("gemini", home, env), "\uFEFF" + foreignText("gemini"));
    writeFileSync(join(home, ".config", "muse", "settings.json"), '{ "schema_version": 1, "theme": "x" }\n');
    const before = snapshot(home, env);
    const muse = stubMuse();
    const c = opts(home, env, { yes: false, interactive: true, muse });
    expect(await connectAll(c)).toMatchObject({ ok: true });
    expect(c.asked).toHaveLength(1);
    expect(snapshot(home, env)).not.toEqual(before);
    expect(muse.plugins).toHaveLength(1);
    expect(await disconnectAll(opts(home, env, { muse }))).toMatchObject({ ok: true });
    expect(snapshot(home, env)).toEqual(before);
    expect(muse.plugins).toEqual([]);
  });
});

describe("disconnect details", () => {
  it("keeps a statusLine that the newest backup already had", async () => {
    const { home, env } = fakeHome();
    const claude = globalFile("claude", home, env);
    const original = JSON.stringify({ statusLine: ours("claude").statusLine }) + "\n";
    writeFileSync(claude, original);
    await connectAll(opts(home, env, { tools: ["claude"], explicit: true }));
    await disconnectAll(opts(home, env, { tools: ["claude"], explicit: true }));
    expect(readFileSync(claude, "utf8")).toBe(original);
  });
  it("hints at Lotse-looking entries it does not own (another path or timeout)", async () => {
    const { home, env } = fakeHome();
    const codex = globalFile("codex", home, env);
    writeFileSync(codex, JSON.stringify({ hooks: { Stop: [{ hooks: [{ type: "command", command: 'node "/old/dist/cli.js" hook --source codex' }] }] } }));
    await connectAll(opts(home, env, { tools: ["codex"], explicit: true }));
    const d = opts(home, env, { tools: ["codex"], explicit: true });
    await disconnectAll(d);
    expect(d.lines.join("\n")).toMatch(new RegExp(`Note:.*${escapeRe(codex)}`));
  });
  it("sends refusals to stderr, not stdout", async () => {
    const { home, env } = fakeHome();
    writeFileSync(globalFile("codex", home, env), "{ not json");
    const o = opts(home, env);
    await connectAll(o);
    expect(o.errs.join("\n")).toMatch(/not valid JSON/);
    expect(o.lines.join("\n")).not.toMatch(/not valid JSON/);
  });
  it("reports the tools it planned (for the banner)", async () => {
    const home = tempDir("lotse-global-");
    mkdirSync(join(home, ".codex"));
    expect(await connectAll(opts(home, {}))).toMatchObject({ ok: true, tools: ["codex"] });
  });
});
