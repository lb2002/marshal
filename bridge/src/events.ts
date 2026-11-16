export type AgentEventKind =
  | "session_start"
  | "prompt"
  | "tool_start"
  | "tool_end"
  | "tool_fail"
  | "permission"
  | "notify"
  | "stop"
  | "stop_fail"
  | "subagent_start"
  | "subagent_stop"
  | "compact"
  | "session_end"
  | "status"
  /** The headset's reply reached the agent's listener. Carries no text: recordings note only that it was sent. */
  | "reply"
  /** The agent's plan changed (plan.ts): `plan` is the new one, absent when the plan is gone. Changes nothing else. */
  | "plan";

/** Which coding agent sent the event. Absent means "claude" (Claude Code, and every older recording). */
export type AgentSource = "claude" | "muse" | "codex" | "gemini" | "opencode";

export const AGENT_SOURCES: ReadonlySet<string> = new Set<AgentSource>(["claude", "muse", "codex", "gemini", "opencode"]);

import { homedir } from "node:os";
import type { PlanSteps } from "./plan.js";
import { ruleTitle } from "./title.js";

export interface AgentEvent {
  sid: string;
  t: number;
  kind: AgentEventKind;
  tool?: string;
  path?: string;
  command?: string;
  notifyType?: string;
  summary?: string;
  ctxPct?: number;
  costUsd?: number;
  cwd?: string;
  /** Set only for the other tools (`lotse hook --source <tool>`); Claude events leave it out. */
  source?: AgentSource;
  /** Prompt events: a short rule title from the prompt (redacted, capped). The prompt text itself is never kept. */
  title?: string;
  /** Plan events: the agent's plan, already redacted and capped (plan.ts); absent when the plan is gone. */
  plan?: PlanSteps;
}

const EVENT_KINDS: Record<string, AgentEventKind> = {
  SessionStart: "session_start",
  UserPromptSubmit: "prompt",
  PreToolUse: "tool_start",
  PostToolUse: "tool_end",
  PostToolUseFailure: "tool_fail",
  PermissionRequest: "permission",
  Notification: "notify",
  Stop: "stop",
  StopFailure: "stop_fail",
  SubagentStart: "subagent_start",
  SubagentStop: "subagent_stop",
  PreCompact: "compact",
  SessionEnd: "session_end",
};

/** Every AgentEventKind, for validating events that come from a file. */
export const AGENT_EVENT_KINDS: ReadonlySet<string> = new Set<AgentEventKind>([...Object.values(EVENT_KINDS), "status", "reply", "plan"]);

const MAX_COMMAND = 500;
const MAX_SUMMARY = 280;

type Obj = Record<string, unknown>;

function asObject(v: unknown): Obj | null {
  return typeof v === "object" && v !== null && !Array.isArray(v) ? (v as Obj) : null;
}

function str(v: unknown): string | undefined {
  return typeof v === "string" ? v : undefined;
}

function num(v: unknown): number | undefined {
  return typeof v === "number" && Number.isFinite(v) ? v : undefined;
}

function sessionId(o: Obj): string | null {
  const sid = str(o.session_id);
  return sid ? sid : null;
}

/** Copies only defined values so events stay free of `undefined` keys. */
function compact(ev: AgentEvent): AgentEvent {
  for (const k of Object.keys(ev) as (keyof AgentEvent)[]) {
    if (ev[k] === undefined) delete ev[k];
  }
  return ev;
}

/** The hook shim marks the payloads of the other tools with `lotse_source`; anything else counts as Claude. */
function sourceOf(o: Obj): AgentSource | undefined {
  const s = o.lotse_source;
  return s === "muse" || s === "codex" || s === "gemini" || s === "opencode" ? s : undefined;
}

/** `homeDir` shortens absolute paths in the title (`~`, `…/name`). */
export function normalizeHook(payload: unknown, now: number, homeDir: string = homedir()): AgentEvent | null {
  const o = asObject(payload);
  if (!o) return null;
  const name = str(o.hook_event_name);
  const kind = name !== undefined && Object.hasOwn(EVENT_KINDS, name) ? EVENT_KINDS[name] : undefined;
  const sid = sessionId(o);
  if (!kind || !sid) return null;

  const input = asObject(o.tool_input);
  const command = str(input?.command);
  const summary = str(o.last_assistant_message);

  return compact({
    sid,
    t: now,
    kind,
    tool: str(o.tool_name),
    path: str(input?.file_path),
    command: command?.slice(0, MAX_COMMAND),
    notifyType: str(o.notification_type),
    summary: summary?.slice(0, MAX_SUMMARY),
    cwd: str(o.cwd),
    source: sourceOf(o),
    title: kind === "prompt" ? ruleTitle(o.prompt, homeDir) : undefined,
  });
}

/** Claude Code hook event names, the vocabulary the hook shim maps the other tools onto. */
export const HOOK_EVENT_NAMES: ReadonlySet<string> = new Set(Object.keys(EVENT_KINDS));

export function normalizeStatus(payload: unknown, now: number): AgentEvent | null {
  const o = asObject(payload);
  if (!o) return null;
  const sid = sessionId(o);
  if (!sid) return null;
  return compact({
    sid,
    t: now,
    kind: "status",
    ctxPct: num(asObject(o.context_window)?.used_percentage),
    costUsd: num(asObject(o.cost)?.total_cost_usd),
    cwd: str(o.cwd),
  });
}
