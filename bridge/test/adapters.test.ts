import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { adaptHook, MUSE_EMPTY_STDOUT_IS_ALLOW, MUSE_NO_DECISION_DENY, shimOutput } from "../src/adapters.js";
import { normalizeHook } from "../src/events.js";

const fixture = (name: string): unknown => JSON.parse(readFileSync(join(import.meta.dirname, "fixtures", "agents", `${name}.json`), "utf8"));

const ALLOW = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };
const DENY = (message?: string) => ({ hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", ...(message === undefined ? {} : { message }) } } });

const notAllow: unknown[] = [
  {},
  null,
  undefined,
  "allow",
  { behavior: "allow" },
  { decision: { behavior: "allow" } },
  { hookSpecificOutput: { decision: { behavior: "allow" } } },
  { hookSpecificOutput: { hookEventName: "PreToolUse", decision: { behavior: "allow" } } },
  { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "Allow" } } },
  { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: "allow" } },
  { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: ["allow"] } } },
  { hookSpecificOutput: { hookEventName: "PermissionRequest" } },
];

describe("adaptHook: Muse", () => {
  it("maps camelCase preToolUse with the shell tool to an observed PreToolUse (Bash off Windows)", () => {
    const r = adaptHook("muse", fixture("muse-pre-tool-use-shell"), "linux");
    expect(r?.route).toBe("/ev");
    expect(r?.body).toMatchObject({ hook_event_name: "PreToolUse", session_id: "m-1111", tool_name: "Bash", tool_input: { command: "npm test" }, lotse_source: "muse" });
  });
  it("maps the shell tool to PowerShell on Windows (Muse runs PowerShell there)", () => {
    expect(adaptHook("muse", fixture("muse-pre-tool-use-shell"), "win32")?.body.tool_name).toBe("PowerShell");
  });
  it("sends permissionRequest to /perm as PermissionRequest", () => {
    const r = adaptHook("muse", fixture("muse-permission-request-shell"), "linux");
    expect(r?.route).toBe("/perm");
    expect(r?.body).toMatchObject({ hook_event_name: "PermissionRequest", tool_name: "Bash", tool_input: { command: "rm -rf build" }, cwd: "C:/work/proj" });
  });
  it("accepts PascalCase event names as well", () => {
    expect(adaptHook("muse", fixture("muse-notification"), "linux")?.body).toMatchObject({ hook_event_name: "Notification", notification_type: "permission_prompt" });
  });
  it("carries the last assistant message of stop", () => {
    expect(adaptHook("muse", fixture("muse-stop"), "linux")?.body).toMatchObject({ hook_event_name: "Stop", last_assistant_message: "Done: tests pass." });
  });
  it("ignores events Lotse does not use", () => {
    expect(adaptHook("muse", fixture("muse-post-tool-batch"), "linux")).toBeNull();
  });
  it("normalises into an event carrying source muse", () => {
    const r = adaptHook("muse", fixture("muse-permission-request-shell"), "linux");
    expect(normalizeHook(r?.body, 5)).toEqual({ sid: "m-1111", t: 5, kind: "permission", tool: "Bash", command: "rm -rf build", cwd: "C:/work/proj", source: "muse" });
  });
});

describe("adaptHook: Codex", () => {
  it("sends PermissionRequest Bash to /perm with tool_input.command", () => {
    const r = adaptHook("codex", fixture("codex-permission-request-bash"), "win32");
    expect(r?.route).toBe("/perm");
    expect(r?.body).toMatchObject({ hook_event_name: "PermissionRequest", tool_name: "Bash", tool_input: { command: "git push --force origin main" }, lotse_source: "codex" });
  });
  it("observes PreToolUse and Stop", () => {
    expect(adaptHook("codex", fixture("codex-pre-tool-use-bash"), "linux")).toMatchObject({ route: "/ev", body: { hook_event_name: "PreToolUse", tool_name: "Bash" } });
    expect(adaptHook("codex", fixture("codex-stop"), "linux")).toMatchObject({ route: "/ev", body: { hook_event_name: "Stop", last_assistant_message: "All done." } });
  });
  it("passes an apply_patch through with the raw patch in command (the classifier reads it, the card shows it)", () => {
    const f = fixture("codex-permission-request-apply-patch") as { tool_input: { command: string } };
    const r = adaptHook("codex", f, "linux");
    expect(r?.body.tool_name).toBe("apply_patch");
    expect(r?.body.tool_input).toEqual({ command: f.tool_input.command });
  });
});

describe("adaptHook: Gemini", () => {
  it("sends a non-safe BeforeTool to /perm as a PermissionRequest for the shell tool", () => {
    const r = adaptHook("gemini", fixture("gemini-before-tool-shell"), "linux");
    expect(r?.route).toBe("/perm");
    expect(r?.body).toMatchObject({ hook_event_name: "PermissionRequest", tool_name: "Bash", tool_input: { command: "rm -rf build" }, lotse_source: "gemini" });
  });
  it("uses PowerShell for run_shell_command on Windows", () => {
    expect(adaptHook("gemini", fixture("gemini-before-tool-shell"), "win32")?.body.tool_name).toBe("PowerShell");
  });
  it("only observes a safe BeforeTool (as PreToolUse) and maps read_file to Read with file_path", () => {
    const r = adaptHook("gemini", fixture("gemini-before-tool-read"), "linux");
    expect(r?.route).toBe("/ev");
    expect(r?.body).toMatchObject({ hook_event_name: "PreToolUse", tool_name: "Read", tool_input: { file_path: "C:/work/proj/src/a.ts" } });
  });
  it("renames AfterTool, BeforeAgent, AfterAgent and Notification", () => {
    expect(adaptHook("gemini", fixture("gemini-after-tool-write"), "linux")?.body).toMatchObject({ hook_event_name: "PostToolUse", tool_name: "Write" });
    expect(adaptHook("gemini", fixture("gemini-before-agent"), "linux")?.body).toMatchObject({ hook_event_name: "UserPromptSubmit" });
    expect(adaptHook("gemini", fixture("gemini-after-agent"), "linux")?.body).toMatchObject({ hook_event_name: "Stop", last_assistant_message: "Fixed it." });
    expect(adaptHook("gemini", fixture("gemini-notification"), "linux")?.body).toMatchObject({ hook_event_name: "Notification", notification_type: "permission_prompt" });
  });
  it("maps a failed AfterTool to PostToolUseFailure", () => {
    const raw = { ...(fixture("gemini-after-tool-write") as object), tool_response: { error: "disk full" } };
    expect(adaptHook("gemini", raw, "linux")?.body.hook_event_name).toBe("PostToolUseFailure");
  });
});

describe("adaptHook: bad input", () => {
  it.each([null, 1, "x", [], {}, { hook_event_name: "PreToolUse" }, { session_id: "s", hook_event_name: 3 }])("returns null for %j", (raw) => {
    for (const s of ["muse", "codex", "gemini"] as const) expect(adaptHook(s, raw, "linux")).toBeNull();
  });
});

describe("shimOutput", () => {
  it("Muse and Codex: prints the PermissionRequest allow only for a bridge allow", () => {
    for (const s of ["muse", "codex"] as const) {
      expect(JSON.parse(shimOutput(s, ALLOW))).toEqual(ALLOW);
    }
  });
  it("Muse and Codex: a deny always carries a non-empty message", () => {
    for (const s of ["muse", "codex"] as const) {
      expect(JSON.parse(shimOutput(s, DENY("nope")))).toEqual(DENY("nope"));
      const def = JSON.parse(shimOutput(s, DENY()));
      expect(def.hookSpecificOutput.decision.behavior).toBe("deny");
      expect(def.hookSpecificOutput.decision.message.length).toBeGreaterThan(0);
      expect(JSON.parse(shimOutput(s, DENY("   "))).hookSpecificOutput.decision.message.trim().length).toBeGreaterThan(0);
    }
  });
  it("Gemini: a bridge deny prints decision deny with a reason; allow prints nothing (deny-only)", () => {
    expect(JSON.parse(shimOutput("gemini", DENY("zu riskant")))).toEqual({ decision: "deny", reason: "zu riskant" });
    expect(JSON.parse(shimOutput("gemini", DENY())).reason.length).toBeGreaterThan(0);
    expect(shimOutput("gemini", ALLOW)).toBe("");
  });
  it.each(notAllow)("Codex and Gemini print nothing for a reply that is not exactly allow or deny: %j", (reply) => {
    for (const s of ["codex", "gemini"] as const) expect(shimOutput(s, reply)).toBe("");
  });
  it.each(notAllow)("Muse fails closed to the no-decision deny for a reply that is not exactly allow or deny: %j", (reply) => {
    expect(JSON.parse(shimOutput("muse", reply))).toEqual(JSON.parse(MUSE_NO_DECISION_DENY));
  });
  it("assumes Muse treats an empty PermissionRequest stdout as allow until a live test shows otherwise", () => {
    expect(MUSE_EMPTY_STDOUT_IS_ALLOW).toBe(true);
    expect(JSON.parse(MUSE_NO_DECISION_DENY)).toEqual({
      hookSpecificOutput: {
        hookEventName: "PermissionRequest",
        decision: { behavior: "deny", message: "Marshal: no decision from the headset, so this is denied. The user can decide again." },
      },
    });
  });
});

describe("Claude Code through the shim (command hooks, no env var needed)", () => {
  it("passes a Claude payload through unchanged, without lotse_source, and sends PermissionRequest to /perm", () => {
    const perm = fixture("claude-permission-request-bash") as Record<string, unknown>;
    expect(adaptHook("claude", perm, "win32")).toEqual({ route: "/perm", body: perm });
    const read = fixture("claude-pre-tool-use-read") as Record<string, unknown>;
    expect(adaptHook("claude", read, "win32")).toEqual({ route: "/ev", body: read });
    expect(normalizeHook(adaptHook("claude", read)!.body, 1)).not.toHaveProperty("source");
  });
  it("keeps Bash as Bash (Claude names its own tools) and ignores events the bridge does not know", () => {
    expect(adaptHook("claude", fixture("claude-permission-request-bash"), "win32")?.body.tool_name).toBe("Bash");
    expect(adaptHook("claude", { session_id: "s", hook_event_name: "PostToolBatch" }, "linux")).toBeNull();
    expect(adaptHook("claude", { session_id: "s", hook_event_name: "permissionRequest" }, "linux")).toBeNull();
  });
  it("prints Claude's allow only for a bridge allow, a deny with its message, and nothing otherwise (the normal dialog)", () => {
    expect(JSON.parse(shimOutput("claude", ALLOW))).toEqual(ALLOW);
    expect(JSON.parse(shimOutput("claude", DENY("nope")))).toEqual(DENY("nope"));
    for (const reply of notAllow) expect(shimOutput("claude", reply)).toBe("");
  });
});
