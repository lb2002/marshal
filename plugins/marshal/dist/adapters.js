// Maps the hook JSON of Muse (Meta), Codex CLI and Gemini CLI onto the Claude Code hook vocabulary the
// bridge already understands, and turns the bridge's answer back into each tool's own output format.
// Claude Code itself runs the same shim as a command hook (`--source claude`): its payload passes through.
// Pure functions: the hook shim (hook-shim.ts) does the I/O.
//
// Safety: shimOutput prints an allow only when the bridge answered exactly allow. Every other reply,
// including a malformed one, prints nothing, so the tool's own prompt takes over.
import { HOOK_EVENT_NAMES } from "./events.js";
import { PRODUCT_NAME } from "./product.js";
import { MUSE_BRIDGE_OFF_DENY, MUSE_NO_DECISION_DENY, UNREACHABLE } from "./hook-fallback.js";
import { ALIAS_CONFLICT, classifyRisk, sameCommand } from "./risk.js";
export { MUSE_NO_DECISION_DENY };
const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
const str = (v) => (typeof v === "string" ? v : undefined);
/** Muse and Gemini run their shell tool in PowerShell on Windows (both say so in their own code). */
function shellTool(platform) {
    return platform === "win32" ? "PowerShell" : "Bash";
}
const GEMINI_EVENTS = {
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
const GEMINI_TOOLS = {
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
function museEvent(name) {
    return name.charAt(0).toUpperCase() + name.slice(1);
}
function mapTool(source, tool, input, platform) {
    if (source === "claude")
        return { tool, input };
    if (source === "muse")
        return { tool: tool === "shell" ? shellTool(platform) : tool, input };
    // Codex apply_patch keeps the raw patch in `command`: risk/patch.ts classifies it, the card shows it.
    // Codex exec_command runs `cmd`: it becomes `command`, which the shell rules read and the card shows. A
    // different `command` next to it is a conflict (the classifier makes that unclear).
    if (source === "codex") {
        if (tool !== "exec_command" || input.cmd === undefined)
            return { tool, input };
        const conflict = input.command !== undefined && !sameCommand(input.cmd, input.command);
        return { tool, input: { ...input, command: input.cmd, ...(conflict ? { [ALIAS_CONFLICT]: true } : {}) } };
    }
    if (tool === "run_shell_command")
        return { tool: shellTool(platform), input };
    const mapped = GEMINI_TOOLS[tool];
    if (mapped === undefined)
        return { tool, input };
    // Older Gemini versions name the target `absolute_path`; current ones use `file_path`, which wins. Both with
    // different values is a conflict (the classifier makes that unclear).
    const file = str(input.file_path);
    const abs = str(input.absolute_path);
    const path = file ?? abs;
    const conflict = input.absolute_path !== undefined && input.file_path !== undefined && file !== abs;
    return { tool: mapped, input: { ...input, ...(path === undefined ? {} : { file_path: path }), ...(conflict ? { [ALIAS_CONFLICT]: true } : {}) } };
}
/** The tool's hook JSON as a bridge request, or null when Lotse has no use for it. */
export function adaptHook(source, raw, platform = process.platform) {
    if (!isObj(raw))
        return null;
    const rawName = str(raw.hook_event_name);
    if (!rawName || !str(raw.session_id))
        return null;
    const event = source === "gemini" ? GEMINI_EVENTS[rawName] : source === "muse" ? museEvent(rawName) : rawName;
    if (event === undefined || !HOOK_EVENT_NAMES.has(event))
        return null;
    // Claude events carry no marker: an absent source means Claude (and keeps recordings as before).
    const body = source === "claude" ? { ...raw } : { ...raw, hook_event_name: event, lotse_source: source };
    const toolName = str(raw.tool_name);
    if (toolName !== undefined) {
        const m = mapTool(source, toolName, isObj(raw.tool_input) ? raw.tool_input : {}, platform);
        body.tool_name = m.tool;
        body.tool_input = m.input;
    }
    if (source === "gemini") {
        if (rawName === "AfterAgent")
            body.last_assistant_message = str(raw.prompt_response);
        if (rawName === "AfterTool" && isObj(raw.tool_response) && raw.tool_response.error)
            body.hook_event_name = "PostToolUseFailure";
        if (rawName === "Notification" && raw.notification_type === "ToolPermission")
            body.notification_type = "permission_prompt";
        // Gemini has no permission hook: a non-safe BeforeTool is held for the headset (deny-only, see shimOutput).
        if (rawName === "BeforeTool" && typeof body.tool_name === "string") {
            const risk = classifyRisk(body.tool_name, body.tool_input, str(raw.cwd) ?? "", "gemini");
            if (risk.risk !== "safe")
                return { route: "/perm", body: { ...body, hook_event_name: "PermissionRequest" } };
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
export function museNoDecision() {
    return MUSE_EMPTY_STDOUT_IS_ALLOW ? MUSE_NO_DECISION_DENY : "";
}
/**
 * True when stdin is (or, if it cannot be parsed, looks like) a Muse permission event, so a cut or
 * malformed approval still fails closed. Matches both spellings of the event name.
 */
export function isMusePermission(raw) {
    try {
        const v = JSON.parse(raw);
        return isObj(v) && typeof v.hook_event_name === "string" && museEvent(v.hook_event_name) === "PermissionRequest";
    }
    catch {
        return /"hook_event_name"\s*:\s*"[Pp]ermissionRequest"/.test(raw);
    }
}
const MAX_MESSAGE = 500;
const DEFAULT_DENY = `Denied in ${PRODUCT_NAME} on the Quest.`;
/** The bridge's decision, only if it has exactly the PermissionRequest shape the broker emits. */
function bridgeDecision(reply) {
    if (!isObj(reply) || !isObj(reply.hookSpecificOutput))
        return null;
    const h = reply.hookSpecificOutput;
    if (h.hookEventName !== "PermissionRequest" || !isObj(h.decision))
        return null;
    if (h.decision.behavior === "allow")
        return { behavior: "allow" };
    if (h.decision.behavior !== "deny")
        return null;
    // Muse rejects a deny without a non-empty reason; Codex and Gemini show it.
    const message = (str(h.decision.message) ?? "").trim().slice(0, MAX_MESSAGE).trim();
    return { behavior: "deny", message: message || DEFAULT_DENY };
}
/**
 * What the shim prints for a `/perm` reply: the tool's own allow/deny JSON, or for no decision "" (Claude,
 * Codex and Gemini then ask the user) and for Muse the no-decision deny (see MUSE_EMPTY_STDOUT_IS_ALLOW).
 * Claude and Codex share the PermissionRequest output format.
 */
export function shimOutput(source, reply) {
    // The bridge is off: Muse is told so (still a deny); everyone else shows their own prompt.
    if (reply === UNREACHABLE)
        return source === "muse" && MUSE_EMPTY_STDOUT_IS_ALLOW ? MUSE_BRIDGE_OFF_DENY : "";
    const d = bridgeDecision(reply);
    if (d === null)
        return source === "muse" ? museNoDecision() : "";
    if (source === "gemini") {
        // Gemini's `allow` does not skip its own confirmation, so Lotse only ever denies for Gemini.
        return d.behavior === "deny" ? JSON.stringify({ decision: "deny", reason: d.message }) : "";
    }
    const decision = d.behavior === "allow" ? { behavior: "allow" } : { behavior: "deny", message: d.message };
    return JSON.stringify({ hookSpecificOutput: { hookEventName: "PermissionRequest", decision } });
}
/**
 * A pre-tool refusal in the tool's own format (halt-all, halt.ts): Claude Code, Codex and Muse take the
 * PreToolUse `permissionDecision: "deny"` with a non-empty reason; Gemini's BeforeTool takes `{decision: "deny"}`.
 */
export function preToolDeny(source, message) {
    if (source === "gemini")
        return JSON.stringify({ decision: "deny", reason: message });
    return JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: message } });
}
