// Halt-all (contract `.superpowers/sdd/2026-11-12-contract-impact-halt.md`, part B): the message every refused
// request and tool call carries, how far each agent can be stopped, and the bridge's pre-tool answer. No imports
// beyond types: the hook shim loads this on every call.
export const HALT_MESSAGE = "The user has halted all agents. Run nothing further and wait for their instruction.";
/** The console line that releases a halt (server.ts); `weiter` is the older German word, still accepted. */
export const RELEASE_WORDS = new Set(["continue", "weiter"]);
/**
 * What the bridge can enforce per source while halted (evidence: docs/research/2026-11-11-other-agents-hooks.md,
 * the installers in install.ts / install-tools.ts / muse-plugin.ts):
 * - claude: PreToolUse is installed for every tool (no matcher) and runs before each tool call; its documented
 *   `permissionDecision: "deny"` refuses the call.
 * - gemini: BeforeTool is installed and fires before every tool, before Gemini's own policy check and prompt;
 *   `{"decision":"deny"}` blocks the tool.
 * - codex: PreToolUse is installed; its documented deny refuses shell, apply_patch and MCP tool calls, which are
 *   every tool that changes something. Its own `update_plan` and web search are not known to reach the hook.
 * - muse: PreToolUse is installed and gets the same refusal, but its deny format comes only from third-party
 *   write-ups and hooks for read tools were seen not to fire; only permission requests are known to be refusable.
 */
export const ENFORCEMENT = {
    claude: "every-step",
    codex: "every-step",
    gemini: "every-step",
    muse: "requests-only",
};
/**
 * The bridge's answer to a pre-tool hook while halted, in Claude Code's own PreToolUse format (so a Claude http
 * hook can use it as it is). The shim turns it into each tool's format.
 */
export function preToolHaltReply(message = HALT_MESSAGE) {
    return { hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: message } };
}
/** The bridge's answer to a permission request while halted (PermissionRequest format, as the broker's deny). */
export function permHaltReply(message = HALT_MESSAGE) {
    return { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message } } };
}
/**
 * The header a hook sends with a pre-tool event, so a halted bridge can refuse it from the headers alone, before
 * (and without) reading the body (rulings task-p2-fix-1.md B1).
 */
export const PRE_TOOL_HEADER = "X-Lotse-Hook";
export const PRE_TOOL_HEADER_VALUE = "PreToolUse";
/** The pre-tool event per tool (Gemini's BeforeTool is also its approval event). */
export const PRE_TOOL_EVENT = { claude: "PreToolUse", codex: "PreToolUse", muse: "PreToolUse", gemini: "BeforeTool" };
/** The halt refusal a hook prints itself, in the tool's own format (the same text the bridge would answer). */
export function haltRefusal(source, kind) {
    if (source === "gemini")
        return JSON.stringify({ decision: "deny", reason: HALT_MESSAGE });
    return JSON.stringify(kind === "pre" ? preToolHaltReply() : permHaltReply());
}
const MAX_MESSAGE = 500;
/** The refusal reason of a bridge reply to a pre-tool hook, when it has exactly the shape above; else null. */
export function preToolRefusal(reply) {
    if (typeof reply !== "object" || reply === null || Array.isArray(reply))
        return null;
    const h = reply.hookSpecificOutput;
    if (typeof h !== "object" || h === null || Array.isArray(h))
        return null;
    const o = h;
    if (o.hookEventName !== "PreToolUse" || o.permissionDecision !== "deny" || typeof o.permissionDecisionReason !== "string")
        return null;
    const m = o.permissionDecisionReason.replace(/[\u0000-\u0008\u000B-\u001F\u007F-\u009F]/g, "").trim().slice(0, MAX_MESSAGE).trim();
    return m === "" ? null : m;
}
