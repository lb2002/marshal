import { describe, expect, it } from "vitest";
import { normalizeHook, normalizeStatus } from "../src/events.js";

const NOW = 1000;

describe("normalizeHook", () => {
  it("maps PreToolUse Bash", () => {
    const ev = normalizeHook(
      { hook_event_name: "PreToolUse", session_id: "s1", cwd: "/repo", tool_name: "Bash", tool_input: { command: "npm test" } },
      NOW,
    );
    expect(ev).toEqual({ sid: "s1", t: NOW, kind: "tool_start", tool: "Bash", command: "npm test", cwd: "/repo" });
  });

  it("maps Edit file_path", () => {
    const ev = normalizeHook(
      { hook_event_name: "PreToolUse", session_id: "s1", cwd: "/repo", tool_name: "Edit", tool_input: { file_path: "/repo/src/a.ts" } },
      NOW,
    );
    expect(ev?.path).toBe("/repo/src/a.ts");
    expect(ev?.tool).toBe("Edit");
  });

  it("maps PermissionRequest", () => {
    const ev = normalizeHook(
      { hook_event_name: "PermissionRequest", session_id: "s1", tool_name: "Bash", tool_input: { command: "rm -rf x" } },
      NOW,
    );
    expect(ev).toMatchObject({ kind: "permission", tool: "Bash", command: "rm -rf x" });
  });

  it("maps Notification", () => {
    const ev = normalizeHook(
      { hook_event_name: "Notification", session_id: "s1", notification_type: "idle_prompt", message: "waiting" },
      NOW,
    );
    expect(ev).toMatchObject({ kind: "notify", notifyType: "idle_prompt" });
  });

  it("maps Stop and truncates summary to 280 chars", () => {
    const ev = normalizeHook({ hook_event_name: "Stop", session_id: "s1", last_assistant_message: "Fixed it" }, NOW);
    expect(ev).toMatchObject({ kind: "stop", summary: "Fixed it" });
    const long = normalizeHook({ hook_event_name: "Stop", session_id: "s1", last_assistant_message: "x".repeat(500) }, NOW);
    expect(long?.summary).toHaveLength(280);
  });

  it("truncates command to 500 chars", () => {
    const ev = normalizeHook(
      { hook_event_name: "PreToolUse", session_id: "s1", tool_name: "Bash", tool_input: { command: "y".repeat(900) } },
      NOW,
    );
    expect(ev?.command).toHaveLength(500);
  });

  it("maps the remaining event names", () => {
    const names: Record<string, string> = {
      SessionStart: "session_start",
      UserPromptSubmit: "prompt",
      PostToolUse: "tool_end",
      PostToolUseFailure: "tool_fail",
      StopFailure: "stop_fail",
      SubagentStart: "subagent_start",
      SubagentStop: "subagent_stop",
      PreCompact: "compact",
      SessionEnd: "session_end",
    };
    for (const [hook, kind] of Object.entries(names)) {
      expect(normalizeHook({ hook_event_name: hook, session_id: "s1" }, NOW)?.kind).toBe(kind);
    }
  });

  it("ignores fields of the wrong type", () => {
    const ev = normalizeHook(
      { hook_event_name: "PreToolUse", session_id: "s1", cwd: 5, tool_name: 7, tool_input: "nope" },
      NOW,
    );
    expect(ev).toEqual({ sid: "s1", t: NOW, kind: "tool_start" });
  });

  it("returns null for unknown event", () => {
    expect(normalizeHook({ hook_event_name: "SomethingNew", session_id: "s1" }, NOW)).toBeNull();
  });

  it("returns null for missing session_id", () => {
    expect(normalizeHook({ hook_event_name: "Stop" }, NOW)).toBeNull();
    expect(normalizeHook({ hook_event_name: "Stop", session_id: "" }, NOW)).toBeNull();
  });

  it("returns null for non-object", () => {
    expect(normalizeHook(null, NOW)).toBeNull();
    expect(normalizeHook("x", NOW)).toBeNull();
    expect(normalizeHook([1], NOW)).toBeNull();
  });
});

describe("normalizeStatus", () => {
  it("maps statusline JSON", () => {
    const ev = normalizeStatus(
      { session_id: "s1", context_window: { used_percentage: 83 }, cost: { total_cost_usd: 11.2 } },
      NOW,
    );
    expect(ev).toMatchObject({ sid: "s1", t: NOW, kind: "status", ctxPct: 83, costUsd: 11.2 });
  });

  it("returns null without session_id or on non-object", () => {
    expect(normalizeStatus({ cost: {} }, NOW)).toBeNull();
    expect(normalizeStatus(undefined, NOW)).toBeNull();
  });

  it("omits non-numeric values", () => {
    const ev = normalizeStatus({ session_id: "s1", context_window: { used_percentage: "x" } }, NOW);
    expect(ev).toEqual({ sid: "s1", t: NOW, kind: "status" });
  });
});
