// Writing and removing Marshal's OpenCode plugin (opencode-plugin.ts): one file, `plugins/marshal/index.js`, in
// OpenCode's user config folder (all sessions, install-global.ts) or in `<project>/.opencode` (one project,
// install-run.ts). The same rules as for the hook files of the other tools:
// - never through a link (the folders on the way and the file itself), fail closed when something cannot be read;
// - project mode: never the home directory, never a `.opencode` that is OpenCode's user folder (install.ts guards);
// - a file that is exactly the plugin for this CLI is Marshal's: replaced on install, the only thing ever removed;
// - an older Marshal plugin (it starts with OPENCODE_PLUGIN_MARKER) is replaced after a backup next to it;
//   any other file there is refused (project mode: unless --force, then backed up first).

import { createHash } from "node:crypto";
import { copyFileSync, constants as fsConstants, lstatSync, mkdirSync, readFileSync, readdirSync, rmdirSync, statSync, unlinkSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { checkTargetDir, writeTargetFile } from "./install.js";
import { TOOL_TARGETS } from "./install-tools.js";
import { commandParts } from "./muse-plugin.js";
import { OPENCODE_PLUGIN_MARKER, OPENCODE_PLUGIN_NAME, opencodePluginSource } from "./opencode-plugin.js";
import { PRODUCT_NAME } from "./product.js";
import type { HookPlace, InstallResult, UninstallResult } from "./install-run.js";

/** The plugin text for the hook command `"<node>" "<cli.js>"` (or the older `node "<cli.js>"`). */
export function opencodePluginText(bridgeCmd: string): string {
  const { node, cli } = commandParts(bridgeCmd);
  return opencodePluginSource(node, cli);
}

/** OpenCode's user config folder: `$XDG_CONFIG_HOME/opencode`, else `~/.config/opencode` (also on Windows). */
export function opencodeConfigDir(home: string, env: NodeJS.ProcessEnv): string {
  return join(env.XDG_CONFIG_HOME ? resolve(env.XDG_CONFIG_HOME) : join(home, ".config"), "opencode");
}

/** The plugin file below an OpenCode folder (the user config folder or `<project>/.opencode`). */
export const pluginFileIn = (opencodeDir: string): string => join(opencodeDir, "plugins", OPENCODE_PLUGIN_NAME, "index.js");

/** The file looks like a plugin Marshal wrote (maybe for another cli.js). */
export const looksLikeOurs = (text: string): boolean => text.startsWith(OPENCODE_PLUGIN_MARKER);

/**
 * The plugin file's text below `opencodeDir` (null: absent), or why it must not be touched: a folder on the way or
 * the file is a link, something there is not a folder or not a file, or it cannot be read.
 */
export function readPluginFile(opencodeDir: string): { file: string; text: string | null } | { error: string } {
  const file = pluginFileIn(opencodeDir);
  try {
    for (const p of [opencodeDir, dirname(dirname(file)), dirname(file)]) {
      const st = lstatSync(p, { throwIfNoEntry: false });
      if (st === undefined) return { file, text: null };
      if (st.isSymbolicLink()) return { error: `${p} is a link; not touching it` };
      if (!st.isDirectory()) return { error: `${p} is not a folder; not touching it` };
    }
    const st = lstatSync(file, { throwIfNoEntry: false });
    if (st === undefined) return { file, text: null };
    if (st.isSymbolicLink()) return { error: `${file} is a link; not touching it` };
    if (!st.isFile()) return { error: `${file} is not a file; not touching it` };
    return { file, text: readFileSync(file, "utf8") };
  } catch (err) {
    return { error: `${file} cannot be read (${(err as NodeJS.ErrnoException).code ?? (err as Error).message}); not touching it` };
  }
}

/** Removes the plugin's own folder once it is empty (OpenCode would otherwise find a plugin folder without index.js). */
export function removeEmptyPluginDir(file: string): void {
  try {
    if (readdirSync(dirname(file)).length === 0) rmdirSync(dirname(file));
  } catch {
    /* not empty, gone, or not ours to judge: it stays */
  }
}

/** Byte copy next to the file (`<file>.lotse-backup-<time>`); never overwrites an older backup. */
export function backupFile(file: string): string {
  const ts = new Date().toISOString().replace(/[-:.]/g, "");
  for (let i = 0; ; i++) {
    const target = join(dirname(file), `${basename(file)}.lotse-backup-${ts}${i === 0 ? "" : `-${i}`}`);
    try {
      copyFileSync(file, target, fsConstants.COPYFILE_EXCL);
      return target;
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code !== "EEXIST") throw err;
    }
  }
}

/** What a listing shows for a plugin file: where it is and a hash of its text. */
export function pluginEntry(file: string, text: string): { file: string; sha256: string } {
  return { file, sha256: createHash("sha256").update(text).digest("hex").slice(0, 16) };
}

// ---- project mode (`install-hooks` / `uninstall-hooks` / `connect --project … --tool opencode`) ----

const isDir = (p: string): boolean => {
  try {
    return statSync(p).isDirectory();
  } catch {
    return false;
  }
};

/** The guards of every tool (install.ts) plus no link on the way to the file; then the file's text (null: absent). */
function prepare(p: HookPlace): { dir: string; file: string; text: string | null } | { error: string } {
  const dir = resolve(p.dir);
  const problem = checkTargetDir(dir, p.homeDir, isDir, TOOL_TARGETS.opencode);
  if (problem) return { error: problem };
  const r = readPluginFile(join(dir, ".opencode"));
  if ("error" in r) return { error: `refusing: ${r.error}` };
  return { dir, ...r };
}

/** Creates the folders on the way one by one, each a real folder (never a link), then writes with the install.ts guards. */
function writeProjectPlugin(p: HookPlace, dir: string, text: string): string | null {
  let at = join(dir, ".opencode");
  for (const part of ["", "plugins", OPENCODE_PLUGIN_NAME]) {
    at = part === "" ? at : join(at, part);
    const st = lstatSync(at, { throwIfNoEntry: false });
    if (st === undefined) mkdirSync(at);
    else if (st.isSymbolicLink() || !st.isDirectory()) return `refusing: ${at} is not a real folder`;
  }
  return writeTargetFile(dir, p.homeDir, text, TOOL_TARGETS.opencode);
}

export function installOpenCodeProject(p: HookPlace, force: boolean): InstallResult {
  const prep = prepare(p);
  if ("error" in prep) return { ok: false, error: prep.error };
  const wanted = opencodePluginText(p.bridgeCmd);
  if (prep.text === wanted) return { ok: true, file: prep.file, changed: false };
  if (prep.text !== null && !looksLikeOurs(prep.text) && !force) {
    return { ok: false, error: `${prep.file} already exists and is not ${PRODUCT_NAME}'s plugin; use --force to replace it` };
  }
  try {
    if (prep.text !== null) backupFile(prep.file);
    const problem = writeProjectPlugin(p, prep.dir, wanted);
    if (problem) return { ok: false, error: problem };
  } catch (err) {
    return { ok: false, error: `${prep.file} could not be written (${(err as NodeJS.ErrnoException).code ?? (err as Error).message})` };
  }
  return { ok: true, file: prep.file, changed: true };
}

/** What uninstall would do, as before/after objects for the listing (settings-diff.ts): the plugin goes, or nothing. */
export function uninstallOpenCodePlan(p: HookPlace): { ok: true; file: string; before: object | null; after: object | null } | { ok: false; error: string } {
  const prep = prepare(p);
  if ("error" in prep) return { ok: false, error: prep.error };
  if (prep.text === null) return { ok: true, file: prep.file, before: null, after: null };
  const before = { plugin: pluginEntry(prep.file, prep.text) };
  return { ok: true, file: prep.file, before, after: prep.text === opencodePluginText(p.bridgeCmd) ? {} : before };
}

export function uninstallOpenCodeProject(p: HookPlace): UninstallResult {
  const prep = prepare(p);
  if ("error" in prep) return { ok: false, error: prep.error };
  if (prep.text === null) return { ok: true, file: prep.file, changed: false, existed: false };
  if (prep.text !== opencodePluginText(p.bridgeCmd)) return { ok: true, file: prep.file, changed: false, existed: true };
  try {
    unlinkSync(prep.file);
  } catch (err) {
    return { ok: false, error: `${prep.file} could not be removed (${(err as NodeJS.ErrnoException).code ?? (err as Error).message})` };
  }
  removeEmptyPluginDir(prep.file);
  return { ok: true, file: prep.file, changed: true, existed: true };
}
