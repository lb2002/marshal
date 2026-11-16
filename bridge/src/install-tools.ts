// Project hook files for Muse (Meta), Codex CLI and Gemini CLI. Every hook runs the same shim,
// `<bridgeCmd> hook --source <tool>`; the guards and the write come from install.ts.

import { join, resolve } from "node:path";
import type { ShimSource } from "./adapters.js";
import { legacyBridgeCmds, removeHooksWhere, type InstallTarget } from "./install.js";
import { PRODUCT_NAME } from "./product.js";

export type HookTool = Exclude<ShimSource, "claude">;
export const HOOK_TOOLS: readonly HookTool[] = ["muse", "codex", "gemini"];

export const TOOL_TARGETS: Record<HookTool, InstallTarget> = {
  muse: {
    sub: ".muse",
    file: "hooks.json",
    userDirs: (home) => {
      const dirs = [
        { dir: join(home, ".muse"), label: "~/.muse" },
        { dir: join(home, ".config", "muse"), label: "~/.config/muse" },
      ];
      const xdg = process.env.XDG_CONFIG_HOME;
      if (xdg) dirs.push({ dir: join(resolve(xdg), "muse"), label: "XDG_CONFIG_HOME/muse" });
      return dirs;
    },
  },
  codex: {
    sub: ".codex",
    file: "hooks.json",
    userDirs: (home) => {
      const dirs = [{ dir: join(home, ".codex"), label: "~/.codex" }];
      const env = process.env.CODEX_HOME;
      if (env) dirs.push({ dir: resolve(env), label: "CODEX_HOME" });
      return dirs;
    },
  },
  gemini: {
    sub: ".gemini",
    file: "settings.json",
    userDirs: (home) => {
      const dirs = [{ dir: join(home, ".gemini"), label: "~/.gemini" }];
      const env = process.env.GEMINI_CLI_HOME;
      if (env) dirs.push({ dir: join(resolve(env), ".gemini"), label: "$GEMINI_CLI_HOME/.gemini" });
      return dirs;
    },
  },
};

/** The Muse events Lotse hooks, PascalCase (Muse rejects `permissionRequest`); also used by the user plugin. */
export const MUSE_EVENTS: readonly string[] = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUseFailure", "Notification", "Stop", "StopFailure", "SubagentStart", "SubagentStop", "PreCompact", "SessionEnd"];

/** Events per tool, in the tool's own spelling. Muse takes the Claude-compatible PascalCase names. */
const EVENTS: Record<HookTool, readonly string[]> = {
  muse: MUSE_EVENTS,
  codex: ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PreCompact", "SubagentStart", "SubagentStop", "Stop", "SessionEnd"],
  gemini: ["SessionStart", "BeforeAgent", "BeforeTool", "AfterTool", "AfterAgent", "PreCompress", "Notification", "SessionEnd"],
};

/** The event that waits for the headset, and its timeout in the tool's unit (Muse/Codex seconds, Gemini ms). */
const APPROVAL: Record<HookTool, { event: string; timeout: number }> = {
  muse: { event: "PermissionRequest", timeout: 120 },
  codex: { event: "PermissionRequest", timeout: 120 },
  gemini: { event: "BeforeTool", timeout: 120_000 },
};

/** The one-time step the user takes in the tool before project hooks run. */
export const TRUST_STEP: Record<HookTool, string> = {
  muse: "Muse: start muse once in this folder and trust the workspace (and the new hooks) when it asks. Project hooks run only in a trusted folder.",
  codex: `Codex: trust the project when Codex asks, then open /hooks once and trust the ${PRODUCT_NAME} hooks. Codex skips new or changed hooks until they are reviewed.`,
  gemini: `Gemini: trust this folder when Gemini asks (or /permissions), and accept the new project hooks. ${PRODUCT_NAME} can only deny for Gemini, not approve.`,
};

const shimCommand = (tool: HookTool, bridgeCmd: string) => `${bridgeCmd} hook --source ${tool}`;
/** The approval hook names its event, so the shim fails closed (Muse) and waits 10 s for stdin before reading it. */
const approvalCommand = (tool: HookTool, bridgeCmd: string) => `${shimCommand(tool, bridgeCmd)} --event ${APPROVAL[tool].event}`;

/** The pre-tool hook names its event, so a slow or failing run still checks the halt marker (hook-cli.ts). */
const preToolCommand = (tool: HookTool, bridgeCmd: string) => `${shimCommand(tool, bridgeCmd)} --event PreToolUse`;

export function buildToolHooks(tool: HookTool, bridgeCmd: string): object {
  const hooks: Record<string, object[]> = {};
  for (const event of EVENTS[tool]) {
    const approval = event === APPROVAL[tool].event;
    const command = approval ? approvalCommand(tool, bridgeCmd) : event === "PreToolUse" ? preToolCommand(tool, bridgeCmd) : shimCommand(tool, bridgeCmd);
    const h: Record<string, unknown> = { type: "command", command };
    if (tool === "gemini") h.name = "lotse";
    if (approval) h.timeout = APPROVAL[tool].timeout;
    hooks[event] = [{ hooks: [h] }];
  }
  return { hooks };
}

/** Merges only the `hooks` key; an existing one is refused unless forced (then replaced whole). */
export function mergeToolHooks(existing: object | null, built: object, force: boolean): { settings: object } | { error: "hooks-exist" } {
  const base = (existing ?? {}) as Record<string, unknown>;
  if (!force && "hooks" in base) return { error: "hooks-exist" };
  return { settings: { ...base, ...(built as Record<string, unknown>) } };
}

/** Removes exactly the shim hooks of this tool and this CLI (with or without the approval flag); everything else stays. */
export function removeToolHooks(existing: object, bridgeCmd: string, tool: HookTool): object {
  const ours = new Set([bridgeCmd, ...legacyBridgeCmds(bridgeCmd)].flatMap((cmd) => [shimCommand(tool, cmd), approvalCommand(tool, cmd), preToolCommand(tool, cmd)]));
  return removeHooksWhere(existing, (h) => typeof h === "object" && h !== null && (h as Record<string, unknown>).type === "command" && ours.has((h as Record<string, unknown>).command as string));
}
