// What a hook prints when nothing else works. The product name (PRODUCT_NAME in product.ts) is spelled out
// below, kept in step by test/product-name.test.ts. No imports on purpose: the CLI loads this before anything
// else, so even a broken or missing module elsewhere cannot keep a Muse approval from failing closed.

/**
 * Muse's answer to an approval without a headset decision (see MUSE_EMPTY_STDOUT_IS_ALLOW in adapters.ts):
 * an empty stdout might count as allow there, so the hook denies instead.
 */
export const MUSE_NO_DECISION_DENY = JSON.stringify({
  hookSpecificOutput: {
    hookEventName: "PermissionRequest",
    decision: { behavior: "deny", message: "Marshal: no decision from the headset, so this is denied. The user can decide again." },
  },
});

/**
 * Muse's answer when the bridge is not running at all (the connection was refused): still a deny, but one that
 * says how to get going again. Only ECONNREFUSED counts; a timeout or any other failure keeps the deny above.
 */
export const MUSE_BRIDGE_OFF_DENY = JSON.stringify({
  hookSpecificOutput: {
    hookEventName: "PermissionRequest",
    decision: { behavior: "deny", message: "Marshal is not running, so this is denied. The user decides how to go on." },
  },
});

/** What the shim's transport resolves when the bridge refused the connection (nothing listens on the port). */
export const UNREACHABLE: unique symbol = Symbol("lotse-unreachable");

/** The event each tool's approval hook is installed for (`hook --source <tool> --event <name>`). */
export const APPROVAL_EVENT: Record<string, string> = {
  claude: "PermissionRequest",
  muse: "PermissionRequest",
  codex: "PermissionRequest",
  gemini: "BeforeTool",
};

/** True when `event` (the `--event` flag) is the approval event of `source`; Muse also spells it camelCase. */
export function isApprovalEvent(source: string, event: string | undefined): boolean {
  if (event === undefined) return false;
  const want = APPROVAL_EVENT[source];
  return want !== undefined && (event === want || (source === "muse" && event === "permissionRequest"));
}

/** Raw stdin text that looks like a pre-tool event, even when cut (a hook installed without `--event`). */
export const PRE_TOOL_TEXT = /"hook_event_name"\s*:\s*"(?:[Pp]reToolUse|BeforeTool)"/;

/** Raw stdin text that looks like an approval event, even when cut: it gets the long stdin deadline. */
export const APPROVAL_TEXT = /"hook_event_name"\s*:\s*"(?:[Pp]ermissionRequest|BeforeTool)"/;

/** What the shim's transport resolves when the bridge accepted the connection but did not answer in time. */
export const TIMEOUT: unique symbol = Symbol("lotse-timeout");
