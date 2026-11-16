// Installing and removing the Lotse hooks of one tool in one project folder, shared by `install-hooks`,
// `uninstall-hooks` and `connect`. The guards and the guarded write live in install.ts.
//
// Installing is idempotent: hooks that are exactly Lotse's own (of this CLI, either Claude transport) are
// replaced; any other existing hooks (or a foreign Claude statusLine) are refused unless forced, as before.
// OpenCode has a plugin file instead of hooks; its project install lives in opencode-install.ts.

import { existsSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import {
  buildHookSettings,
  checkTargetDir,
  claudeTarget,
  mergeIntoProjectSettings,
  parseSettingsText,
  removeHooksWhere,
  removeLotseHooks,
  writeTargetFile,
  type ClaudeTransport,
  type InstallTarget,
} from "./install.js";
import { TOOL_TARGETS, buildToolHooks, mergeToolHooks, removeToolHooks, type HookTool, type JsonHookTool } from "./install-tools.js";
import { installOpenCodeProject, uninstallOpenCodePlan, uninstallOpenCodeProject } from "./opencode-install.js";

export type InstallTool = "claude" | HookTool;

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

export interface HookPlace {
  /** Project folder (resolved to an absolute path). */
  dir: string;
  tool: InstallTool;
  /** `node "<cli.js>"` of this CLI. */
  bridgeCmd: string;
  homeDir: string;
}

export type InstallResult = { ok: true; file: string; changed: boolean } | { ok: false; error: string };
export type UninstallResult = { ok: true; file: string; changed: boolean; existed: boolean } | { ok: false; error: string };

export function targetFor(tool: InstallTool): InstallTarget {
  return tool === "claude" ? claudeTarget() : TOOL_TARGETS[tool];
}

/** Removes every Lotse hook of this CLI for `tool` (Claude: both transports and the statusLine). */
function withoutOurs(existing: object, tool: "claude" | JsonHookTool, bridgeCmd: string): object {
  return tool === "claude" ? removeLotseHooks(existing, bridgeCmd) : removeToolHooks(existing, bridgeCmd, tool);
}

/**
 * The settings to write: our hooks replace our old ones; anything else under `hooks` (even an empty entry)
 * is refused unless `force`, exactly as before. Pure.
 */
export function planHooks(
  existing: object | null,
  tool: "claude" | JsonHookTool,
  bridgeCmd: string,
  transport: ClaudeTransport,
  force: boolean,
): { settings: object } | { error: "hooks-exist" } {
  let base = existing;
  if (existing !== null) {
    const cleaned = withoutOurs(existing, tool, bridgeCmd);
    // Only a file that held some of our hooks is cleaned; otherwise the old refusal applies unchanged.
    // (Compared with a removal of nothing, which drops empty groups the same way.)
    if (!same(cleaned, removeHooksWhere(existing, () => false))) base = cleaned;
  }
  return tool === "claude"
    ? mergeIntoProjectSettings(base, buildHookSettings(bridgeCmd, transport), force)
    : mergeToolHooks(base, buildToolHooks(tool, bridgeCmd), force);
}

const isDir = (p: string) => existsSync(p) && statSync(p).isDirectory();

/** Guard checks, then the existing settings file (null when absent). */
function prepare(p: HookPlace): { file: string; target: InstallTarget; existing: object | null } | { error: string } {
  const dir = resolve(p.dir);
  const target = targetFor(p.tool);
  const problem = checkTargetDir(dir, p.homeDir, isDir, target);
  if (problem) return { error: problem };
  const file = join(dir, target.sub, target.file);
  if (!existsSync(file)) return { file, target, existing: null };
  const parsed = parseSettingsText(readFileSync(file, "utf8"));
  if ("error" in parsed) return { error: `${file} is ${parsed.error}; not touching it` };
  return { file, target, existing: parsed.value };
}

export function installHooks(p: HookPlace, transport: ClaudeTransport, force: boolean): InstallResult {
  if (p.tool === "opencode") return installOpenCodeProject(p, force);
  const tool = p.tool;
  const prep = prepare(p);
  if ("error" in prep) return { ok: false, error: prep.error };
  const plan = planHooks(prep.existing, tool, p.bridgeCmd, transport, force);
  if ("error" in plan) {
    const what = p.tool === "claude" ? "hooks or a statusLine" : "hooks";
    return { ok: false, error: `${prep.file} already has ${what}; use --force to replace them` };
  }
  if (prep.existing !== null && same(plan.settings, prep.existing)) return { ok: true, file: prep.file, changed: false };
  const problem = writeTargetFile(resolve(p.dir), p.homeDir, JSON.stringify(plan.settings, null, 2) + "\n", prep.target);
  if (problem) return { ok: false, error: problem };
  return { ok: true, file: prep.file, changed: true };
}

/** What uninstallHooks would write, without writing: the file, its settings now and after (null: no file). */
export function uninstallPlan(p: HookPlace): { ok: true; file: string; before: object | null; after: object | null } | { ok: false; error: string } {
  if (p.tool === "opencode") return uninstallOpenCodePlan(p);
  const tool = p.tool;
  const prep = prepare(p);
  if ("error" in prep) return { ok: false, error: prep.error };
  if (prep.existing === null) return { ok: true, file: prep.file, before: null, after: null };
  return { ok: true, file: prep.file, before: prep.existing, after: withoutOurs(prep.existing, tool, p.bridgeCmd) };
}

export function uninstallHooks(p: HookPlace): UninstallResult {
  if (p.tool === "opencode") return uninstallOpenCodeProject(p);
  const tool = p.tool;
  const prep = prepare(p);
  if ("error" in prep) return { ok: false, error: prep.error };
  if (prep.existing === null) return { ok: true, file: prep.file, changed: false, existed: false };
  const cleaned = withoutOurs(prep.existing, tool, p.bridgeCmd);
  if (same(cleaned, prep.existing)) return { ok: true, file: prep.file, changed: false, existed: true };
  const problem = writeTargetFile(resolve(p.dir), p.homeDir, JSON.stringify(cleaned, null, 2) + "\n", prep.target);
  if (problem) return { ok: false, error: problem };
  return { ok: true, file: prep.file, changed: true, existed: true };
}
