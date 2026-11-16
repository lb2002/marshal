// Maps the hook JSON of Muse (Meta), Codex CLI, Gemini CLI and OpenCode (sent by Marshal's own OpenCode plugin,
// opencode-plugin.ts) onto the Claude Code hook vocabulary the bridge already understands, and turns the bridge's
// answer back into each tool's own output format.
// Claude Code itself runs the same shim as a command hook (`--source claude`): its payload passes through.
// Pure functions: the hook shim (hook-shim.ts) does the I/O.
//
// Safety: shimOutput prints an allow only when the bridge answered exactly allow. Every other reply,
// including a malformed one, prints nothing, so the tool's own prompt takes over.

import { posix, win32 } from "node:path";
import { HOOK_EVENT_NAMES } from "./events.js";
import { PRODUCT_NAME } from "./product.js";
import { MUSE_BRIDGE_OFF_DENY, MUSE_NO_DECISION_DENY, UNREACHABLE } from "./hook-fallback.js";
import { ALIAS_CONFLICT, classifyRisk, sameCommand } from "./risk.js";

export { MUSE_NO_DECISION_DENY };

export type ShimSource = "claude" | "muse" | "codex" | "gemini" | "opencode";

export interface ShimRequest {
  /** `/perm` waits for the headset; `/ev` is observe-only. */
  route: "/ev" | "/perm";
  /** Claude-style hook payload plus `lotse_source`. */
  body: Record<string, unknown>;
}

type Obj = Record<string, unknown>;
const isObj = (v: unknown): v is Obj => typeof v === "object" && v !== null && !Array.isArray(v);
const str = (v: unknown): string | undefined => (typeof v === "string" ? v : undefined);

/**
 * Muse and Gemini run their shell tool in PowerShell on Windows (both say so in their own code); OpenCode's shell is
 * named the same way. On Windows the classifier reads every non-Claude command by both rule sets anyway (risk.ts).
 */
function shellTool(platform: NodeJS.Platform): string {
  return platform === "win32" ? "PowerShell" : "Bash";
}

const GEMINI_EVENTS: Record<string, string> = {
  SessionStart: "SessionStart",
  SessionEnd: "SessionEnd",
  BeforeAgent: "UserPromptSubmit",
  AfterAgent: "Stop",
  BeforeTool: "PreToolUse",
  AfterTool: "PostToolUse",
  PreCompress: "PreCompact",
  Notification: "Notification",
};

/** Gemini built-in tools with a Claude Code counterpart (display and risk tier). */
const GEMINI_TOOLS: Record<string, string> = {
  read_file: "Read",
  read_many_files: "Read",
  write_file: "Write",
  replace: "Edit",
  glob: "Glob",
  search_file_content: "Grep",
  grep_search: "Grep",
  list_directory: "LS",
  web_fetch: "WebFetch",
  google_web_search: "WebSearch",
};

/** Muse spells events camelCase in its own protocol and PascalCase in Claude-compatible hooks: accept both. */
function museEvent(name: string): string {
  return name.charAt(0).toUpperCase() + name.slice(1);
}

function mapTool(source: ShimSource, tool: string, input: Obj, platform: NodeJS.Platform): { tool: string; input: Obj } {
  if (source === "claude") return { tool, input };
  if (source === "muse") return { tool: tool === "shell" ? shellTool(platform) : tool, input };
  // Codex apply_patch keeps the raw patch in `command`: risk/patch.ts classifies it, the card shows it.
  // Codex exec_command runs `cmd`: it becomes `command`, which the shell rules read and the card shows. A
  // different `command` next to it is a conflict (the classifier makes that unclear).
  if (source === "codex") {
    if (tool !== "exec_command" || input.cmd === undefined) return { tool, input };
    const conflict = input.command !== undefined && !sameCommand(input.cmd, input.command);
    return { tool, input: { ...input, command: input.cmd, ...(conflict ? { [ALIAS_CONFLICT]: true } : {}) } };
  }
  if (tool === "run_shell_command") return { tool: shellTool(platform), input };
  const mapped = GEMINI_TOOLS[tool];
  if (mapped === undefined) return { tool, input };
  // Older Gemini versions name the target `absolute_path`; current ones use `file_path`, which wins. Both with
  // different values is a conflict (the classifier makes that unclear).
  const file = str(input.file_path);
  const abs = str(input.absolute_path);
  const path = file ?? abs;
  const conflict = input.absolute_path !== undefined && input.file_path !== undefined && file !== abs;
  return { tool: mapped, input: { ...input, ...(path === undefined ? {} : { file_path: path }), ...(conflict ? { [ALIAS_CONFLICT]: true } : {}) } };
}

// ---- OpenCode ----
// Marshal's OpenCode plugin (opencode-plugin.ts) sends `{event, sessionID, cwd, tool?, input?, status?, action?,
// resources?, error?}` with `event` already in Claude's names. OpenCode's tools: shell {command, workdir?}, edit
// {path, oldString, newString}, write {path, content}, read {path}, patch {patchText}.

const OPENCODE_EVENTS: ReadonlySet<string> = new Set(["SessionStart", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Stop", "StopFailure", "SessionEnd"]);
const OPENCODE_FILE_TOOLS: Record<string, string> = { edit: "Edit", write: "Write", read: "Read" };
/** The permission action each OpenCode tool asks under (edit also covers write and patch). */
const OPENCODE_ACTION: Record<string, string> = { shell: "shell", edit: "edit", write: "edit", patch: "edit", apply_patch: "edit", read: "read" };

/** `workdir` names the session folder itself (by the rules of the platform OpenCode runs on). */
function sameDir(cwd: string, workdir: string, platform: NodeJS.Platform): boolean {
  const p = platform === "win32" ? win32 : posix;
  const a = p.resolve(cwd);
  const b = p.resolve(cwd, workdir);
  return platform === "win32" ? a.toLowerCase() === b.toLowerCase() : a === b;
}

/** One OpenCode tool call in Claude's names. An alias that disagrees with what runs makes the call unclear. */
function mapOpenCodeTool(tool: string, input: Obj, cwd: string, platform: NodeJS.Platform): { tool: string; input: Obj } {
  if (tool === "shell") {
    // The command runs in `workdir`; the classifier reads relative paths against the session folder, so another
    // folder makes it unclear (two hands) instead of judging it against the wrong place.
    const wd = input.workdir;
    const elsewhere = wd !== undefined && !(typeof wd === "string" && cwd !== "" && sameDir(cwd, wd, platform));
    return { tool: shellTool(platform), input: { ...input, ...(elsewhere ? { [ALIAS_CONFLICT]: true } : {}) } };
  }
  const file = OPENCODE_FILE_TOOLS[tool];
  if (file !== undefined) {
    // `path` becomes `file_path`; a different `file_path` next to it, or a `path` that is not text, is a conflict.
    const { path, ...rest } = input;
    const bad = path !== undefined && (typeof path !== "string" || (rest.file_path !== undefined && rest.file_path !== path));
    return { tool: file, input: { ...rest, ...(typeof path === "string" ? { file_path: path } : {}), ...(bad ? { [ALIAS_CONFLICT]: true } : {}) } };
  }
  if (tool === "patch" || tool === "apply_patch") {
    // Read by the Codex patch parser (risk/patch.ts), which fails closed: any other input field, a missing text or
    // an unknown `***` line makes it unclear, a delete or a target outside the project destructive.
    const { patchText, ...rest } = input;
    const bad = rest.command !== undefined && rest.command !== patchText;
    return { tool: "apply_patch", input: { ...rest, ...(patchText === undefined ? {} : { command: patchText }), ...(bad ? { [ALIAS_CONFLICT]: true } : {}) } };
  }
  return { tool, input };
}

/**
 * An OpenCode permission request. With the tool call the plugin joined to it (same call id), and of the kind the
 * action names, that call is what the headset shows and rates. Without it only the action and its resources are
 * known: a read of one file is a Read (rated exactly as one); anything else is shown but marked unclear (two
 * hands), because OpenCode's parsed sub-commands or file list can look safer than the call that runs.
 */
function openCodePermission(raw: Obj, cwd: string, platform: NodeJS.Platform): { tool: string; input: Obj } {
  const action = str(raw.action) ?? "";
  const tool = str(raw.tool);
  if (tool !== undefined && isObj(raw.input) && OPENCODE_ACTION[tool] === action) return mapOpenCodeTool(tool, raw.input, cwd, platform);
  const resources = Array.isArray(raw.resources) && raw.resources.every((r) => typeof r === "string") ? (raw.resources as string[]) : [];
  if (action === "read" && resources.length === 1) return { tool: "Read", input: { file_path: resources[0] } };
  const unclear = { [ALIAS_CONFLICT]: true };
  if (action === "edit" && resources.length > 0) return { tool: "Edit", input: { file_path: resources[0], ...unclear } };
  const listed = resources.length > 0 ? { command: resources.join("\n") } : {};
  const name = action === "shell" ? shellTool(platform) : action === "" ? "permission" : action;
  return { tool: name, input: { ...listed, ...unclear } };
}

/** The plugin's event JSON as a Claude-style hook payload, or null. */
function adaptOpenCode(raw: Obj, platform: NodeJS.Platform): ShimRequest | null {
  const event = str(raw.event);
  const sid = str(raw.sessionID);
  if (event === undefined || !OPENCODE_EVENTS.has(event) || !sid) return null;
  const cwd = str(raw.cwd) ?? "";
  const body: Obj = { session_id: sid, cwd, hook_event_name: event, lotse_source: "opencode" };
  if (event === "PermissionRequest") {
    const m = openCodePermission(raw, cwd, platform);
    return { route: "/perm", body: { ...body, tool_name: m.tool, tool_input: m.input } };
  }
  const tool = str(raw.tool);
  if (tool !== undefined) {
    const m = mapOpenCodeTool(tool, isObj(raw.input) ? raw.input : {}, cwd, platform);
    body.tool_name = m.tool;
    body.tool_input = m.input;
  }
  if ((event === "PostToolUseFailure" || event === "StopFailure") && raw.error !== undefined) {
    body.error = typeof raw.error === "string" ? raw.error : JSON.stringify(raw.error);
  }
  return { route: "/ev", body };
}

/** The tool's hook JSON as a bridge request, or null when Lotse has no use for it. */
export function adaptHook(source: ShimSource, raw: unknown, platform: NodeJS.Platform = process.platform): ShimRequest | null {
  if (!isObj(raw)) return null;
  if (source === "opencode") return adaptOpenCode(raw, platform);
  const rawName = str(raw.hook_event_name);
  if (!rawName || !str(raw.session_id)) return null;
  const event = source === "gemini" ? GEMINI_EVENTS[rawName] : source === "muse" ? museEvent(rawName) : rawName;
  if (event === undefined || !HOOK_EVENT_NAMES.has(event)) return null;

  // Claude events carry no marker: an absent source means Claude (and keeps recordings as before).
  const body: Obj = source === "claude" ? { ...raw } : { ...raw, hook_event_name: event, lotse_source: source };
  const toolName = str(raw.tool_name);
  if (toolName !== undefined) {
    const m = mapTool(source, toolName, isObj(raw.tool_input) ? raw.tool_input : {}, platform);
    body.tool_name = m.tool;
    body.tool_input = m.input;
  }

  if (source === "gemini") {
    if (rawName === "AfterAgent") body.last_assistant_message = str(raw.prompt_response);
    if (rawName === "AfterTool" && isObj(raw.tool_response) && raw.tool_response.error) body.hook_event_name = "PostToolUseFailure";
    if (rawName === "Notification" && raw.notification_type === "ToolPermission") body.notification_type = "permission_prompt";
    // Gemini has no permission hook: a non-safe BeforeTool is held for the headset (deny-only, see shimOutput).
    if (rawName === "BeforeTool" && typeof body.tool_name === "string") {
      const risk = classifyRisk(body.tool_name, body.tool_input as Obj, str(raw.cwd) ?? "", "gemini");
      if (risk.risk !== "safe") return { route: "/perm", body: { ...body, hook_event_name: "PermissionRequest" } };
    }
    return { route: "/ev", body };
  }
  return { route: event === "PermissionRequest" ? "/perm" : "/ev", body };
}

/**
 * MUST BE VERIFIED LIVE. A third-party write-up of Muse says an empty stdout on PermissionRequest counts as
 * allow. Until a live Muse session shows that empty stdout means "no decision" (as in Claude Code and Codex),
 * every Muse permission event that ends without a headset decision is answered with MUSE_NO_DECISION_DENY:
 * the hook may fall back to deny, never to allow.
 */
export const MUSE_EMPTY_STDOUT_IS_ALLOW = true;

/** What a Muse permission event prints when there is no headset decision. */
export function museNoDecision(): string {
  return MUSE_EMPTY_STDOUT_IS_ALLOW ? MUSE_NO_DECISION_DENY : "";
}

/**
 * True when stdin is (or, if it cannot be parsed, looks like) a Muse permission event, so a cut or
 * malformed approval still fails closed. Matches both spellings of the event name.
 */
export function isMusePermission(raw: string): boolean {
  try {
    const v: unknown = JSON.parse(raw);
    return isObj(v) && typeof v.hook_event_name === "string" && museEvent(v.hook_event_name) === "PermissionRequest";
  } catch {
    return /"hook_event_name"\s*:\s*"[Pp]ermissionRequest"/.test(raw);
  }
}

const MAX_MESSAGE = 500;
const DEFAULT_DENY = `Denied in ${PRODUCT_NAME} on the Quest.`;

/** The bridge's decision, only if it has exactly the PermissionRequest shape the broker emits. */
function bridgeDecision(reply: unknown): { behavior: "allow" } | { behavior: "deny"; message: string } | null {
  if (!isObj(reply) || !isObj(reply.hookSpecificOutput)) return null;
  const h = reply.hookSpecificOutput;
  if (h.hookEventName !== "PermissionRequest" || !isObj(h.decision)) return null;
  if (h.decision.behavior === "allow") return { behavior: "allow" };
  if (h.decision.behavior !== "deny") return null;
  // Muse rejects a deny without a non-empty reason; Codex and Gemini show it.
  const message = (str(h.decision.message) ?? "").trim().slice(0, MAX_MESSAGE).trim();
  return { behavior: "deny", message: message || DEFAULT_DENY };
}

/**
 * What the shim prints for a `/perm` reply: the tool's own allow/deny JSON, or for no decision "" (Claude,
 * Codex, Gemini and OpenCode then ask the user) and for Muse the no-decision deny (see MUSE_EMPTY_STDOUT_IS_ALLOW).
 * Claude and Codex share the PermissionRequest output format; the OpenCode plugin reads `{decision, message?}`.
 */
export function shimOutput(source: ShimSource, reply: unknown): string {
  // The bridge is off: Muse is told so (still a deny); everyone else shows their own prompt.
  if (reply === UNREACHABLE) return source === "muse" && MUSE_EMPTY_STDOUT_IS_ALLOW ? MUSE_BRIDGE_OFF_DENY : "";
  const d = bridgeDecision(reply);
  if (d === null) return source === "muse" ? museNoDecision() : "";
  if (source === "gemini") {
    // Gemini's `allow` does not skip its own confirmation, so Lotse only ever denies for Gemini.
    return d.behavior === "deny" ? JSON.stringify({ decision: "deny", reason: d.message }) : "";
  }
  // The Marshal OpenCode plugin sets the permission's effect from this (allow only on exactly this allow).
  if (source === "opencode") return JSON.stringify(d.behavior === "allow" ? { decision: "allow" } : { decision: "deny", message: d.message });
  const decision = d.behavior === "allow" ? { behavior: "allow" } : { behavior: "deny", message: d.message };
  return JSON.stringify({ hookSpecificOutput: { hookEventName: "PermissionRequest", decision } });
}

/**
 * A pre-tool refusal in the tool's own format (halt-all, halt.ts): Claude Code, Codex and Muse take the
 * PreToolUse `permissionDecision: "deny"` with a non-empty reason; Gemini's BeforeTool takes `{decision: "deny"}`;
 * the OpenCode plugin `{decision: "deny", message}`.
 */
export function preToolDeny(source: ShimSource, message: string): string {
  if (source === "gemini") return JSON.stringify({ decision: "deny", reason: message });
  // The Marshal OpenCode plugin throws on this in `tool.execute.before`, which blocks the call.
  if (source === "opencode") return JSON.stringify({ decision: "deny", message });
  return JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: message } });
}
