// `lotse connect --all` / `lotse disconnect --all`: the Lotse hooks in the user-level files of Claude Code,
// Muse, Codex CLI, Gemini CLI and OpenCode, so every session of the user reaches the headset. Decided on 12 Nov 2026.
// for this explicit mode only; project mode (`--project`) keeps its guards and never writes these files.
//
// - Only Lotse's own entries are added, next to everything else (foreign hooks in the same events stay).
// - Ownership is exact: disconnect removes only handlers that equal Lotse's (deep-equal, this CLI path).
// - Before a file is changed, a byte copy is written next to it (`<file>.lotse-backup-<ts>`); backups stay.
// - Disconnect gives the original bytes back when nothing else changed meanwhile (it restores the matching
//   backup), and removes a file that Lotse itself created.
// - Safe apply: after the question the files are planned again; a file that changed is not written, and a
//   failed write restores the files already written in this run. Writes are plain writes, not atomic renames
//   (see README).
// - Muse is never written into its settings file (a malformed user settings file breaks every Muse session):
//   it gets a user plugin that Muse itself validates first (muse-plugin.ts), or it is skipped.
// - OpenCode has no hook settings: it gets Marshal's plugin file `plugins/marshal/index.js` in its user config
//   folder (opencode-install.ts), planned, listed, backed up and written like the settings files.
// Nothing here reads the real home or env on its own: the CLI passes them in (tests pass a fake HOME).

import { copyFileSync, constants as fsConstants, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { buildHookSettings, legacyBridgeCmds, parseSettingsText } from "./install.js";
import { MUSE_EVENTS, buildToolHooks, type JsonHookTool } from "./install-tools.js";
import { looksLikeOurs, opencodeConfigDir, opencodePluginText, pluginEntry, pluginFileIn, readPluginFile, removeEmptyPluginDir } from "./opencode-install.js";
import { entryChanges, formatChanges } from "./settings-diff.js";
import { PRODUCT_NAME } from "./product.js";
import type { InstallTool } from "./install-run.js";
import { MUSE_NO_GLOBAL, MUSE_PLUGIN_ID, installedBundleText, manifestText, musePluginDir, realMuseRunner, samePath, validateInTemp, writeBundle, type MuseRunner } from "./muse-plugin.js";

type Obj = Record<string, unknown>;
const isObj = (v: unknown): v is Obj => typeof v === "object" && v !== null && !Array.isArray(v);

export const GLOBAL_TOOLS: readonly InstallTool[] = ["claude", "muse", "codex", "gemini", "opencode"];
export const TOOL_NAME: Record<InstallTool, string> = { claude: "Claude Code", muse: "Muse", codex: "Codex CLI", gemini: "Gemini CLI", opencode: "OpenCode" };

/** What a session that is already running needs before the new user-level hooks reach it. */
export const GLOBAL_TRUST_STEP: Record<InstallTool, string> = {
  claude: "Claude Code: restart running sessions, or check once with /hooks.",
  muse: `Muse: run "muse plugins approve lotse" once (until then the ${PRODUCT_NAME} hooks show review_needed), then start new sessions.`,
  codex: `Codex CLI: open /hooks once and approve the ${PRODUCT_NAME} hooks.`,
  gemini: `Gemini CLI: work in a trusted folder; for Gemini, ${PRODUCT_NAME} can only deny.`,
  opencode: "OpenCode: it loads plugins only when it starts, so restart OpenCode.",
};

const MUSE_WARNING =
  `Warning for Muse: while the ${PRODUCT_NAME} plugin is installed and ${PRODUCT_NAME} is not running, Muse denies every approval. To switch it off: lotse disconnect --all --tool muse`;

/** The tool's own user config folder (with the env overrides the tools read); a tool counts as found when it exists. */
export function configDir(tool: InstallTool, home: string, env: NodeJS.ProcessEnv): string {
  switch (tool) {
    case "claude":
      return env.CLAUDE_CONFIG_DIR ? resolve(env.CLAUDE_CONFIG_DIR) : join(home, ".claude");
    case "muse":
      // Muse docs: user settings at ~/.config/muse/settings.json; the binary prefers $XDG_CONFIG_HOME/muse.
      return join(env.XDG_CONFIG_HOME ? resolve(env.XDG_CONFIG_HOME) : join(home, ".config"), "muse");
    case "codex":
      return env.CODEX_HOME ? resolve(env.CODEX_HOME) : join(home, ".codex");
    case "gemini":
      return join(env.GEMINI_CLI_HOME ? resolve(env.GEMINI_CLI_HOME) : home, ".gemini");
    case "opencode":
      return opencodeConfigDir(home, env);
  }
}

/** The user-level file that holds the tool's hooks (OpenCode: Marshal's plugin file). */
export function globalFile(tool: InstallTool, home: string, env: NodeJS.ProcessEnv): string {
  if (tool === "opencode") return pluginFileIn(configDir(tool, home, env));
  return join(configDir(tool, home, env), tool === "codex" ? "hooks.json" : "settings.json");
}

/** What Lotse writes for a tool (the same handlers as project mode; Claude with its statusLine). */
function built(tool: "claude" | JsonHookTool, bridgeCmd: string): { hooks: Record<string, Obj[]>; statusLine?: Obj } {
  return (tool === "claude" ? buildHookSettings(bridgeCmd, "command") : buildToolHooks(tool, bridgeCmd)) as { hooks: Record<string, Obj[]>; statusLine?: Obj };
}

/** A file that does not exist yet starts as this (Muse requires `schema_version: 1`). */
const skeleton = (tool: "claude" | JsonHookTool): Obj => (tool === "muse" ? { schema_version: 1 } : {});

/** JSON with sorted keys, for deep equality. */
function canon(v: unknown): string {
  if (Array.isArray(v)) return `[${v.map(canon).join(",")}]`;
  if (isObj(v)) return `{${Object.keys(v).sort().map((k) => `${JSON.stringify(k)}:${canon(v[k])}`).join(",")}}`;
  return JSON.stringify(v);
}
const same = (a: unknown, b: unknown) => canon(a) === canon(b);

/** Every handler Lotse installs for this tool, as canonical JSON (also in the earlier `node "<cli.js>"` spelling). */
function ourHandlers(tool: "claude" | JsonHookTool, bridgeCmd: string): Set<string> {
  const out = new Set<string>();
  for (const cmd of [bridgeCmd, ...legacyBridgeCmds(bridgeCmd)]) {
    for (const groups of Object.values(built(tool, cmd).hooks)) {
      for (const g of groups) for (const h of g.hooks as unknown[]) out.add(canon(h));
    }
  }
  return out;
}

/** Copy of `existing` without Lotse's handlers; only groups, events and `hooks` emptied by that removal go too. */
export function removeGlobal(existing: object, tool: "claude" | JsonHookTool, bridgeCmd: string, keepStatusLine = false): object {
  const ours = ourHandlers(tool, bridgeCmd);
  const out: Obj = { ...(existing as Obj) };
  const statuses = [bridgeCmd, ...legacyBridgeCmds(bridgeCmd)].map((cmd) => built(tool, cmd).statusLine).filter((x) => x !== undefined);
  if (!keepStatusLine && statuses.some((st) => same(out.statusLine, st))) delete out.statusLine;
  if (!isObj(out.hooks)) return out;
  const hooks: Obj = {};
  let removedAny = false;
  for (const [event, groups] of Object.entries(out.hooks)) {
    if (!Array.isArray(groups)) {
      hooks[event] = groups;
      continue;
    }
    const kept: unknown[] = [];
    let removedHere = false;
    for (const g of groups) {
      if (!isObj(g) || !Array.isArray(g.hooks)) {
        kept.push(g);
        continue;
      }
      const left = g.hooks.filter((h) => !ours.has(canon(h)));
      if (left.length === g.hooks.length) kept.push(g);
      else {
        removedHere = true;
        if (left.length > 0) kept.push({ ...g, hooks: left });
      }
    }
    removedAny ||= removedHere;
    if (kept.length > 0 || !removedHere) hooks[event] = kept;
  }
  if (Object.keys(hooks).length > 0 || !removedAny) out.hooks = hooks;
  else delete out.hooks;
  return out;
}

/** True when every Lotse group is already there (and Claude has some statusLine): nothing to write. */
function complete(existing: Obj, tool: "claude" | JsonHookTool, bridgeCmd: string): boolean {
  const b = built(tool, bridgeCmd);
  if (b.statusLine && !("statusLine" in existing)) return false;
  if (!isObj(existing.hooks)) return false;
  const hooks = existing.hooks;
  return Object.entries(b.hooks).every(([event, groups]) => {
    const have = hooks[event];
    return Array.isArray(have) && groups.every((g) => have.some((x) => same(x, g)));
  });
}

/**
 * The settings with Lotse's entries added after the foreign ones of each event (an older copy of ours is
 * replaced). A foreign statusLine stays. Refuses a `hooks` that is not an object of arrays. Pure.
 */
export function mergeGlobal(existing: object | null, tool: "claude" | JsonHookTool, bridgeCmd: string): { settings: object } | { error: string } {
  if (existing !== null) {
    const h = (existing as Obj).hooks;
    if (h !== undefined && !isObj(h)) return { error: "its hooks key is not an object" };
    if (isObj(h)) for (const [event, groups] of Object.entries(h)) if (!Array.isArray(groups)) return { error: `its hooks.${event} is not a list` };
  }
  if (existing !== null && complete(existing as Obj, tool, bridgeCmd)) return { settings: existing };
  const base = existing === null ? skeleton(tool) : (removeGlobal(existing, tool, bridgeCmd) as Obj);
  const b = built(tool, bridgeCmd);
  const hooks: Obj = isObj(base.hooks) ? { ...base.hooks } : {};
  for (const [event, groups] of Object.entries(b.hooks)) hooks[event] = [...((hooks[event] as unknown[] | undefined) ?? []), ...groups];
  const out: Obj = { ...base, hooks };
  if (b.statusLine && !("statusLine" in out)) out.statusLine = b.statusLine;
  return { settings: out };
}

// ---- files ----

interface FileState {
  file: string;
  /** null: the file does not exist. */
  text: string | null;
  value: Obj | null;
}

function readState(file: string): FileState | { error: string } {
  // lstat, not exists: a dangling link must be refused too, never written through.
  const st = lstatSync(file, { throwIfNoEntry: false });
  if (st === undefined) return { file, text: null, value: null };
  if (st.isSymbolicLink()) return { error: `${file} is a link; not touching it` };
  if (!st.isFile()) return { error: `${file} is not a file; not touching it` };
  const text = readFileSync(file, "utf8");
  const parsed = parseSettingsText(text);
  if ("error" in parsed) return { error: `${file} is ${parsed.error}; not touching it` };
  return { file, text, value: parsed.value as Obj };
}

/** JSON in the file's own style: its line ending and BOM are kept. */
function serialize(value: object, like: string | null): string {
  const eol = like !== null && like.includes("\r\n") ? "\r\n" : "\n";
  const bom = like !== null && like.startsWith("﻿") ? "﻿" : "";
  return bom + JSON.stringify(value, null, 2).replace(/\n/g, eol) + eol;
}

const backupPrefix = (file: string) => `${basename(file)}.lotse-backup-`;

/** Byte copy next to the file; never overwrites an older backup. */
function writeBackup(file: string): string {
  const ts = new Date().toISOString().replace(/[-:.]/g, "");
  for (let i = 0; ; i++) {
    const target = join(dirname(file), `${backupPrefix(file)}${ts}${i === 0 ? "" : `-${i}`}`);
    try {
      copyFileSync(file, target, fsConstants.COPYFILE_EXCL);
      return target;
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code !== "EEXIST") throw err;
    }
  }
}

/** The backups of a file, newest first. */
function backups(file: string): string[] {
  try {
    return readdirSync(dirname(file))
      .filter((f) => f.startsWith(backupPrefix(file)))
      .sort()
      .reverse()
      .map((f) => join(dirname(file), f));
  } catch {
    return [];
  }
}

// ---- the two commands ----

export interface GlobalOpts {
  tools: readonly InstallTool[];
  /** `--tool <one>` given: a missing config folder is created instead of skipped. */
  explicit: boolean;
  home: string;
  env: NodeJS.ProcessEnv;
  /** `node "<cli.js>"` of this CLI. */
  bridgeCmd: string;
  /** `--yes`: no question. */
  yes: boolean;
  /**
   * false: there is no terminal to ask on (stdin is not a TTY). Without `--yes` nothing is changed then, and the
   * run says so. Left out: a terminal (the tests' `ask` stands in for it).
   */
  interactive?: boolean;
  /** Asks one question on the terminal and resolves the answer ("" on EOF). */
  ask(question: string): Promise<string>;
  log(line: string): void;
  /** Refusals and errors (stderr). */
  err(line: string): void;
  /** How Lotse talks to Muse; undefined: the installed binary, null: no Muse binary (tests stub it). */
  muse?: MuseRunner | null;
}

export type GlobalResult =
  | { ok: true; changed: string[]; tools: InstallTool[]; museSkipped: boolean }
  | { ok: false; cancelled?: boolean; error?: string };

interface Step {
  tool: InstallTool;
  file: string;
  before: FileState;
  /** The new settings; null: nothing to change. */
  after: object | null;
  /** The text to write; null with `after` set: delete the file (Lotse created it). */
  write: string | null;
}

/** The Muse part of a run: the user plugin `lotse` (muse-plugin.ts), never the settings file. */
type MusePlan =
  | { kind: "absent" }
  | { kind: "skip"; reason: string }
  | { kind: "same" }
  | { kind: "install"; text: string; update: boolean; runner: MuseRunner };

const YES = /^(j|ja|y|yes)$/i;
/** A command that looks like Lotse's own (`… cli.js" hook …` or `… cli.js" statusline`). */
const LOTSE_LOOK = /cli\.js"?\s+(?:hook|statusline)\b/;

/** The newest backup's settings, or null when there is none (or it cannot be read). */
function newestBackup(file: string): Obj | null {
  const b = backups(file)[0];
  if (b === undefined) return null;
  try {
    const p = parseSettingsText(readFileSync(b, "utf8"));
    return "error" in p ? null : (p.value as Obj);
  } catch {
    return null;
  }
}

/** What disconnect writes: the exact bytes of a matching backup if there is one, else the file re-serialised. */
function removalText(file: string, after: object, before: string | null): string {
  for (const b of backups(file)) {
    const t = readFileSync(b, "utf8");
    const p = parseSettingsText(t);
    if (!("error" in p) && same(p.value, after)) return t;
  }
  return serialize(after, before);
}

const runnerOf = (o: GlobalOpts): MuseRunner | null => (o.muse === undefined ? realMuseRunner(o.env) : o.muse);

/**
 * Plans the settings files of every selected tool (Muse: only to remove what an earlier Lotse version wrote
 * there); any refusal stops the whole run before a byte is written.
 */
function plan(o: GlobalOpts, action: "install" | "remove"): { steps: Step[]; skipped: InstallTool[] } | { error: string } {
  const steps: Step[] = [];
  const skipped: InstallTool[] = [];
  for (const tool of o.tools) {
    if (action === "install" && tool === "muse") continue;
    const file = globalFile(tool, o.home, o.env);
    if (action === "install" && !o.explicit && !existsSync(configDir(tool, o.home, o.env))) {
      skipped.push(tool);
      continue;
    }
    if (tool === "opencode") {
      const s = planOpenCode(o, action);
      if ("error" in s) return s;
      steps.push(s);
      continue;
    }
    const st = readState(file);
    if ("error" in st) return { error: st.error };
    if (action === "remove") {
      if (st.value === null) {
        steps.push({ tool, file, before: st, after: null, write: null });
        continue;
      }
      // Lotse's statusLine goes only if the file had none before the install (the newest backup lacked it).
      const nb = newestBackup(file);
      const after = removeGlobal(st.value, tool, o.bridgeCmd, nb !== null && "statusLine" in nb);
      if (same(after, st.value)) {
        steps.push({ tool, file, before: st, after: null, write: null });
        continue;
      }
      // A file Lotse created (never backed up) that now holds nothing else is deleted.
      const created = backups(file).length === 0 && same(after, skeleton(tool));
      steps.push({ tool, file, before: st, after, write: created ? null : removalText(file, after, st.text) });
      continue;
    }
    const merged = mergeGlobal(st.value, tool, o.bridgeCmd);
    if ("error" in merged) return { error: `${file}: ${merged.error}; not touching it` };
    const changed = !(st.value !== null && same(merged.settings, st.value));
    steps.push({ tool, file, before: st, after: changed ? merged.settings : null, write: changed ? serialize(merged.settings, st.text) : null });
  }
  return { steps, skipped };
}

/**
 * OpenCode: Marshal's plugin file. Install: created, or replaced when it is an older Marshal plugin (backed up by
 * applySteps); any other file there is refused. Remove: deleted only when it is exactly this CLI's plugin.
 */
function planOpenCode(o: GlobalOpts, action: "install" | "remove"): Step | { error: string } {
  const r = readPluginFile(configDir("opencode", o.home, o.env));
  if ("error" in r) return { error: r.error };
  const before: FileState = { file: r.file, text: r.text, value: null };
  const wanted = opencodePluginText(o.bridgeCmd);
  const none: Step = { tool: "opencode", file: r.file, before, after: null, write: null };
  if (action === "remove") return r.text === wanted ? { ...none, after: {} } : none;
  if (r.text === wanted) return none;
  if (r.text !== null && !looksLikeOurs(r.text)) return { error: `${r.file} exists and is not ${PRODUCT_NAME}'s plugin; not touching it` };
  return { ...none, after: { plugin: pluginEntry(r.file, wanted) }, write: wanted };
}

/**
 * Muse: the plugin bundle is validated by Muse itself in a temp copy (offline) before anything is installed.
 * Without a Muse binary, with a foreign plugin of the same name, or when Muse rejects the bundle, Muse is
 * skipped (project mode stays the way to use it).
 */
function planMuse(o: GlobalOpts): MusePlan {
  if (!o.tools.includes("muse")) return { kind: "absent" };
  if (!o.explicit && !existsSync(dirname(globalFile("muse", o.home, o.env)))) return { kind: "absent" };
  const runner = runnerOf(o);
  if (runner === null) return { kind: "skip", reason: "no muse-bin-*.exe found" };
  const text = manifestText(o.bridgeCmd);
  const dir = musePluginDir(o.home);
  let ours: { id: string; source: string | null } | undefined;
  try {
    ours = runner.list().find((p) => p.id === MUSE_PLUGIN_ID);
  } catch (err) {
    return { kind: "skip", reason: `Muse plugins not readable (${(err as Error).message})` };
  }
  if (ours && !samePath(ours.source, dir)) return { kind: "skip", reason: `another Muse plugin "${MUSE_PLUGIN_ID}" is installed (${ours.source ?? "unknown source"})` };
  if (ours && installedBundleText(o.home) === text) return { kind: "same" };
  const v = validateInTemp(runner, text);
  if (!v.valid) return { kind: "skip", reason: `Muse refuses the ${PRODUCT_NAME} plugin: ${v.message}` };
  return { kind: "install", text, update: ours !== undefined, runner };
}

/** The file's text now, null when it does not exist (a link counts as a change). */
function currentText(file: string): string | null | undefined {
  const st = lstatSync(file, { throwIfNoEntry: false });
  if (st === undefined) return null;
  if (!st.isFile()) return undefined;
  return readFileSync(file, "utf8");
}

interface Written {
  file: string;
  before: string | null;
  backup: string | null;
}

/**
 * Writes the steps one by one, then runs `last` (the Muse plugin). Each file must still hold exactly what was
 * planned; a change or a failure stops the run, and the files already written are restored (from the backup
 * just taken, else the planned text, or removed when Lotse created them). Reports to stderr; false on failure.
 */
function applySteps(steps: Step[], o: GlobalOpts, last?: { what: string; run: () => void }): boolean {
  const done: Written[] = [];
  for (const s of steps) {
    try {
      if (currentText(s.file) !== s.before.text) {
        o.err(`lotse: ${s.file}: changed during the question, nothing written`);
        throw null;
      }
      const w: Written = { file: s.file, before: s.before.text, backup: null };
      // A file that is about to be deleted was created by Lotse and holds nothing else: no backup.
      if (s.before.text !== null && s.write !== null) w.backup = writeBackup(s.file);
      done.push(w);
      if (s.write === null) rmSync(s.file);
      else if (s.before.text === null) {
        mkdirSync(dirname(s.file), { recursive: true });
        writeFileSync(s.file, s.write, { flag: "wx" });
      } else writeFileSync(s.file, s.write);
    } catch (err) {
      if (err !== null) o.err(`lotse: ${s.file} could not be written (${(err as NodeJS.ErrnoException).code ?? (err as Error).message})`);
      rollback(done, o);
      return false;
    }
  }
  if (last) {
    try {
      last.run();
    } catch (err) {
      o.err(`lotse: ${last.what} failed (${(err as Error).message})`);
      rollback(done, o);
      return false;
    }
  }
  return true;
}

function rollback(done: Written[], o: GlobalOpts): void {
  const restored: string[] = [];
  const left: string[] = [];
  for (const w of [...done].reverse()) {
    try {
      // Untouched after all (the write failed before changing it): nothing to restore.
      if (currentText(w.file) === w.before) {
        restored.push(w.file);
        continue;
      }
      if (w.before === null) rmSync(w.file, { force: true });
      else if (w.backup !== null) copyFileSync(w.backup, w.file);
      else writeFileSync(w.file, w.before);
      restored.push(w.file);
    } catch {
      left.push(w.file);
    }
  }
  o.err(`Restored: ${restored.length > 0 ? restored.join(", ") : "none"}`);
  o.err(`Left changed: ${left.length > 0 ? left.join(", ") : "none"}`);
}

/**
 * Installs (or updates) the Muse plugin from Lotse's bundle folder. A failure (or a timeout) puts the old bundle
 * back and, when Muse now lists our plugin although it was not there before, removes it again (best effort).
 */
function installMuse(p: Extract<MusePlan, { kind: "install" }>, home: string, o: GlobalOpts): void {
  const dir = musePluginDir(home);
  const before = installedBundleText(home);
  writeBundle(dir, p.text);
  try {
    if (p.update) p.runner.update(MUSE_PLUGIN_ID);
    else p.runner.install(dir);
  } catch (err) {
    if (!p.update) {
      try {
        const now = p.runner.list().find((x) => x.id === MUSE_PLUGIN_ID);
        if (now && samePath(now.source, dir)) p.runner.remove(MUSE_PLUGIN_ID);
      } catch (cleanup) {
        o.err(`lotse: Muse plugin "${MUSE_PLUGIN_ID}" could not be removed again (${(cleanup as Error).message}); please run "muse plugins remove ${MUSE_PLUGIN_ID}"`);
      }
    }
    if (before === null) rmSync(dir, { recursive: true, force: true });
    else writeBundle(dir, before);
    throw err;
  }
}

/** Handlers or a statusLine that look like Lotse's but are not exactly this CLI's (a moved repo, another timeout). */
function lookAlikes(value: Obj | null): boolean {
  if (value === null) return false;
  const cmds: unknown[] = [isObj(value.statusLine) ? value.statusLine.command : undefined];
  if (isObj(value.hooks)) {
    for (const groups of Object.values(value.hooks)) {
      if (!Array.isArray(groups)) continue;
      for (const g of groups) if (isObj(g) && Array.isArray(g.hooks)) for (const h of g.hooks) cmds.push(isObj(h) ? h.command : undefined);
    }
  }
  return cmds.some((c) => typeof c === "string" && LOTSE_LOOK.test(c));
}

/**
 * Lists one step exactly as it will be written: the file, then every entry removed (`-`, older Marshal handlers
 * and an old status line included) and added (`+`), as canonical JSON (settings-diff.ts).
 */
function listStep(o: GlobalOpts, s: Step): void {
  if (s.tool === "opencode") {
    const what = s.write === null ? "removed" : s.before.text === null ? "added" : `replaced (an older ${PRODUCT_NAME} plugin)`;
    o.log(`  ${s.file}${s.write === null ? " (deleted)" : ""}`);
    o.log(`    OpenCode: the ${PRODUCT_NAME} plugin is ${what}`);
    if (s.before.text !== null) o.log(`      - plugin: ${JSON.stringify(pluginEntry(s.file, s.before.text))}`);
    if (s.write !== null) o.log(`      + plugin: ${JSON.stringify(pluginEntry(s.file, s.write))}`);
    return;
  }
  const written = s.write === null ? null : s.after; // no text with a plan: the file is deleted
  const changes = entryChanges(s.before.value, written);
  o.log(`  ${s.file}${written === null ? ` (deleted: ${PRODUCT_NAME} created it and nothing else is in it)` : ""}`);
  o.log(`    ${TOOL_NAME[s.tool]}: ${changes.filter((c) => c.op === "-").length} entries removed, ${changes.filter((c) => c.op === "+").length} added`);
  for (const l of formatChanges(changes)) o.log(l);
}

/** Without --yes: the one question on a terminal, or (no terminal) nothing at all. true: go on. */
async function confirm(o: GlobalOpts, question: string, again: string): Promise<boolean> {
  if (o.yes) return true;
  if (o.interactive === false) {
    o.log(`No terminal to ask on, so nothing was changed. ${again}`);
    return false;
  }
  if (!YES.test((await o.ask(question)).trim())) {
    o.log("Cancelled. Nothing changed.");
    return false;
  }
  return true;
}

/** `connect --all`: plans, lists the changes, asks once (unless --yes), re-plans, writes, then prints the trust steps. */
export async function connectAll(o: GlobalOpts): Promise<GlobalResult> {
  const p = plan(o, "install");
  if ("error" in p) {
    o.err(`lotse: ${p.error}`);
    return { ok: false, error: p.error };
  }
  let muse = planMuse(o);
  if (o.tools.includes("muse") && muse.kind === "absent") p.skipped.push("muse");
  for (const t of p.skipped) o.log(`${TOOL_NAME[t]}: not found (${configDir(t, o.home, o.env)} is missing), skipped.`);
  if (muse.kind === "skip") {
    o.err(`lotse: Muse: ${muse.reason}`);
    o.log(MUSE_NO_GLOBAL);
  }
  let steps = p.steps;
  const todo = steps.filter((s) => s.after !== null);
  for (const s of steps) if (s.after === null) o.log(`${TOOL_NAME[s.tool]}: ${PRODUCT_NAME} hooks already installed in ${s.file}`);
  if (muse.kind === "same") o.log(`Muse: ${PRODUCT_NAME} plugin "${MUSE_PLUGIN_ID}" already installed (${musePluginDir(o.home)})`);
  const museTodo = muse.kind === "install";
  if (todo.length > 0 || museTodo) {
    o.log(`${PRODUCT_NAME} changes (with a backup next to each as *.lotse-backup-<time>):`);
    for (const s of todo) listStep(o, s);
    if (museTodo) {
      const m = muse as Extract<MusePlan, { kind: "install" }>;
      o.log(`  Muse plugin "${MUSE_PLUGIN_ID}" (user), bundle in ${musePluginDir(o.home)}, checked by Muse${m.update ? ": replaces the installed bundle" : ""}`);
      o.log(`    Muse: one ${PRODUCT_NAME} hook each for ${MUSE_EVENTS.join(", ")}`);
    }
    const names = [...todo.map((s) => s.tool), ...(museTodo ? (["muse"] as InstallTool[]) : [])].sort((a, b) => GLOBAL_TOOLS.indexOf(a) - GLOBAL_TOOLS.indexOf(b));
    if (!(await confirm(o, `Install ${PRODUCT_NAME} hooks globally for: ${names.map((t) => TOOL_NAME[t]).join(", ")} ? [y/N] `, "To install without the question: lotse connect --yes"))) return { ok: false, cancelled: true };
    if (!o.yes) {
      // Re-plan: the answer covers the changes as they were shown.
      const again = plan(o, "install");
      if ("error" in again) {
        o.err(`lotse: ${again.error}`);
        return { ok: false, error: again.error };
      }
      const shown = new Map(steps.map((s) => [s.file, s.before.text]));
      const moved = again.steps.filter((s) => shown.get(s.file) !== s.before.text || (s.after !== null && !todo.some((t) => t.file === s.file)));
      const museAgain = planMuse(o);
      if (museAgain.kind !== muse.kind) moved.push({ tool: "muse", file: musePluginDir(o.home) } as Step);
      if (moved.length > 0) {
        for (const s of moved) o.err(`lotse: ${s.file}: changed during the question, nothing written`);
        return { ok: false, error: "changed" };
      }
      steps = again.steps;
      muse = museAgain;
    }
    const m = muse;
    const last = m.kind === "install" ? { what: `installing Muse plugin "${MUSE_PLUGIN_ID}"`, run: () => installMuse(m, o.home, o) } : undefined;
    if (!applySteps(steps.filter((s) => s.after !== null), o, last)) return { ok: false, error: "write failed" };
  }
  const tools = [...steps.map((s) => s.tool), ...(muse.kind === "install" || muse.kind === "same" ? (["muse"] as InstallTool[]) : [])].sort(
    (a, b) => GLOBAL_TOOLS.indexOf(a) - GLOBAL_TOOLS.indexOf(b),
  );
  if (tools.length > 0) {
    o.log("For sessions that are already running:");
    for (const t of tools) o.log(`  ${GLOBAL_TRUST_STEP[t]}`);
    if (tools.includes("muse")) o.log(MUSE_WARNING);
    o.log(`To remove all ${PRODUCT_NAME} hooks again: lotse disconnect --all`);
  }
  const changed = [...steps.filter((s) => s.after !== null).map((s) => s.file), ...(muse.kind === "install" ? [musePluginDir(o.home)] : [])];
  return { ok: true, changed, tools, museSkipped: muse.kind === "skip" };
}

/**
 * `disconnect --all`: removes exactly Lotse's entries and prints what it changed. For Muse: the user plugin
 * `lotse` when it was installed from Lotse's bundle folder, plus any entries an earlier Lotse version wrote
 * into the Muse settings file.
 */
export async function disconnectAll(o: GlobalOpts): Promise<GlobalResult> {
  const p = plan(o, "remove");
  if ("error" in p) {
    o.err(`lotse: ${p.error}`);
    return { ok: false, error: p.error };
  }
  let steps = p.steps;
  const todo = steps.filter((s) => s.after !== null);
  let removePlugin: MuseRunner | null = null;
  if (o.tools.includes("muse")) {
    const runner = runnerOf(o);
    try {
      const rec = runner?.list().find((x) => x.id === MUSE_PLUGIN_ID);
      if (rec && samePath(rec.source, musePluginDir(o.home))) removePlugin = runner;
      else if (rec) o.log(`Note: Muse plugin "${MUSE_PLUGIN_ID}" does not come from ${musePluginDir(o.home)} and stays.`);
    } catch (err) {
      o.err(`lotse: Muse plugins not readable (${(err as Error).message})`);
    }
  }
  if (todo.length === 0 && removePlugin === null) o.log(`No ${PRODUCT_NAME} hooks found; nothing changed.`);
  else {
    o.log(`${PRODUCT_NAME} removes (each changed file keeps a backup next to it as *.lotse-backup-<time>):`);
    for (const s of todo) listStep(o, s);
    if (removePlugin) o.log(`  Muse plugin "${MUSE_PLUGIN_ID}" (user), installed from ${musePluginDir(o.home)}`);
    const names = [...todo.map((s) => s.tool), ...(removePlugin ? (["muse"] as InstallTool[]) : [])].sort((a, b) => GLOBAL_TOOLS.indexOf(a) - GLOBAL_TOOLS.indexOf(b));
    if (!(await confirm(o, `Remove ${PRODUCT_NAME} hooks for: ${names.map((t) => TOOL_NAME[t]).join(", ")} ? [y/N] `, "To remove them without the question: lotse disconnect --yes"))) return { ok: false, cancelled: true };
    if (!o.yes) {
      // Re-plan: the answer covers the removals as they were shown.
      const again = plan(o, "remove");
      if ("error" in again) {
        o.err(`lotse: ${again.error}`);
        return { ok: false, error: again.error };
      }
      const shown = new Map(steps.map((s) => [s.file, s.before.text]));
      const moved = again.steps.filter((s) => shown.get(s.file) !== s.before.text);
      if (moved.length > 0) {
        for (const s of moved) o.err(`lotse: ${s.file}: changed during the question, nothing written`);
        return { ok: false, error: "changed" };
      }
      steps = again.steps;
    }
    const r = removePlugin;
    const last = r ? { what: `removing Muse plugin "${MUSE_PLUGIN_ID}"`, run: () => r.remove(MUSE_PLUGIN_ID) } : undefined;
    if (!applySteps(steps.filter((s) => s.after !== null), o, last)) return { ok: false, error: "write failed" };
    for (const s of steps) if (s.tool === "opencode" && s.after !== null) removeEmptyPluginDir(s.file);
    o.log(`${PRODUCT_NAME} hooks removed from:`);
    for (const s of steps.filter((x) => x.after !== null)) o.log(`  ${s.file}`);
    if (r) o.log(`  Muse plugin "${MUSE_PLUGIN_ID}"`);
  }
  for (const s of steps) {
    const ocLeft = s.tool === "opencode" && s.after === null && s.before.text !== null && looksLikeOurs(s.before.text);
    if (ocLeft || lookAlikes((s.after ?? s.before.value) as Obj | null)) {
      o.log(`Note: ${s.file} still holds entries that look like ${PRODUCT_NAME}'s but do not exactly belong to this CLI (another path or another time limit?). Please check them yourself.`);
    }
  }
  const changed = [...steps.filter((s) => s.after !== null).map((s) => s.file), ...(removePlugin ? [musePluginDir(o.home)] : [])];
  return { ok: true, changed, tools: steps.map((s) => s.tool), museSkipped: false };
}
