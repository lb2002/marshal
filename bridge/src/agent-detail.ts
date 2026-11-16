// `agent_detail_request` → `agent_detail`: what one agent is doing, for the headset's detail view.
//
// - recent: the session's last tool starts as rule labels ("Reads x", "Edits y", "Runs tests",
//   "Runs: <command, 40 characters>"), newest first, at most 5 of at most 80 characters. Only these
//   labels are kept, never the raw tool input.
// - waitingFor: the explanation of the agent's pending request, else a notification or a terminal prompt.
// - summary: by rules from the state and `recent`; with the explainer on, haiku summarises the last ~30
//   lines of the session's local transcript (redacted, fenced) and a second `agent_detail` with source "ai"
//   follows. An AI summary is cached per session for 30 s, in memory only, and never recorded or logged.
// - At most one request per session every 3 s. Everything here is display text: it never touches risk,
//   gestures or decisions.

import { redactSecrets, shortenAbsolutePaths } from "./anonymize.js";
import { sanitizeSummary, summaryPrompt } from "./agent-ai.js";
import type { AgentEvent } from "./events.js";
import { cleanText, type Explainer } from "./explain.js";
import type { ServerMessage } from "./protocol.js";
import { shortPath, type AgentState } from "./state.js";
import { readTail, resolveTranscript, transcriptLines } from "./transcript.js";

export type AgentDetailMessage = Extract<ServerMessage, { type: "agent_detail" }>;

export interface DetailDeps {
  agent(sid: string): AgentState | undefined;
  /** Display text of the explanation of this agent's pending request, or null. */
  pendingExplain(sid: string): string | null;
  explainer?: Explainer;
  homeDir: string;
  env: NodeJS.ProcessEnv;
  now(): number;
}

export type DetailAnswer =
  | { error: "unknown-agent" | "rate-limited" }
  /** `now` goes out at once; `later`, when set, resolves to the AI version (or null: nothing more to send). */
  | { now: AgentDetailMessage; later?: Promise<AgentDetailMessage | null> };

const MAX_RECENT = 5;
const MAX_LABEL = 80;
const MAX_COMMAND = 40;
const MAX_SUMMARY = 280;
const MAX_WAITING = 140;
const RATE_MS = 3000;
const CACHE_MS = 30_000;
/** A session silent for longer than this is forgotten. */
const IDLE_MS = 2 * 3600_000;
/** At most this many sessions are remembered (the least recently seen goes first). */
const MAX_SESSIONS = 200;

const READ_TOOLS = new Set(["read", "notebookread"]);
const SEARCH_TOOLS = new Set(["glob", "grep", "ls"]);
const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "notebookedit"]);
const SHELL_TOOLS = new Set(["bash", "powershell"]);
const TEST_RE = /\b(?:npm (?:run )?test|pnpm (?:run )?test|yarn (?:run )?test|pytest|vitest|jest|go test|cargo test)\b/;
const SAFE_TOOL = /^[A-Za-z0-9_.:-]{1,40}$/;

const NOTIFY_TEXT: Record<string, string> = {
  idle_prompt: "Waiting for your next instruction.",
  agent_needs_input: "Needs an answer from you.",
  elicitation_dialog: "Needs an answer from you.",
  permission_prompt: "Waiting for approval in the terminal.",
};

const one = (s: string, max: number) => cleanText(s, max);

/** The rule label of one tool start, or null for any other event. */
export function recentLabel(e: AgentEvent, homeDir: string): string | null {
  if (e.kind !== "tool_start" || !e.tool) return null;
  const tool = e.tool.toLowerCase();
  const target = e.path ? shortPath(redactSecrets(e.path)) : "";
  let label: string;
  if (READ_TOOLS.has(tool)) label = target ? `Reads ${target}` : "Reads a file";
  else if (SEARCH_TOOLS.has(tool)) label = target ? `Searches ${target}` : "Searches the project";
  else if (EDIT_TOOLS.has(tool)) label = target ? `Edits ${target}` : "Edits a file";
  else if (SHELL_TOOLS.has(tool) && e.command && TEST_RE.test(e.command)) label = "Runs tests";
  else if (SHELL_TOOLS.has(tool) && e.command) {
    // Redact and shorten the whole command first, then cut, then redact the cut once more.
    const cmd = redactSecrets(one(shortenAbsolutePaths(redactSecrets(e.command), homeDir), MAX_COMMAND));
    label = `Runs: ${cmd}`;
  } else label = SAFE_TOOL.test(e.tool) ? `Uses ${e.tool}` : "Uses a tool";
  return one(label, MAX_LABEL);
}

/** A few plain sentences from the state and the recent labels, at most 280 characters. */
function rulesSummary(a: AgentState, recent: string[]): string {
  const parts = [`${a.lastAction}.`];
  const before = recent.filter((r) => r !== a.lastAction).slice(0, 3);
  if (before.length > 0) parts.push(`Before that: ${before.join(", ")}.`);
  if (a.files.length > 0) parts.push(`Changed files: ${a.files.slice(-3).join(", ")}.`);
  return one(parts.join(" "), MAX_SUMMARY);
}

export class AgentDetails {
  private readonly recent = new Map<string, string[]>();
  private readonly notify = new Map<string, string>();
  /** The `transcript_path` a session's hooks reported (checked only when it is read). */
  private readonly transcripts = new Map<string, string>();
  private readonly lastAsk = new Map<string, number>();
  /** AI summary per session, for 30 s; null remembers a failure for as long. */
  private readonly cache = new Map<string, { summary: string | null; at: number }>();
  private readonly inflight = new Map<string, Promise<string | null>>();
  /** Sessions whose first prompt was seen (only that one is given to the AI for a title). */
  private readonly titled = new Set<string>();
  /** Last time each session was seen, least recent first; the bound for every per-session map above. */
  private readonly seen = new Map<string, number>();

  constructor(private readonly deps: DetailDeps) {}

  /** Number of sessions with anything remembered (tests). */
  trackedSessions(): number {
    return this.seen.size;
  }

  /** True exactly once per session: on its first prompt. */
  firstPrompt(sid: string): boolean {
    this.touch(sid);
    if (this.titled.has(sid)) return false;
    this.titled.add(sid);
    return true;
  }

  /** Marks a session as seen now, and forgets idle sessions and the oldest beyond the cap. */
  private touch(sid: string): void {
    const now = this.deps.now();
    this.seen.delete(sid);
    this.seen.set(sid, now);
    for (const [s, at] of this.seen) {
      if (this.seen.size <= MAX_SESSIONS && now - at <= IDLE_MS) break; // the rest is more recent
      this.forget(s);
    }
  }

  private forget(sid: string): void {
    for (const m of [this.recent, this.notify, this.transcripts, this.lastAsk, this.cache, this.seen]) m.delete(sid);
    this.titled.delete(sid);
  }

  /** Every ingested event: keeps the tool labels and the last notification type; session_end forgets. */
  observe(e: AgentEvent): void {
    if (e.kind === "session_end") {
      this.forget(e.sid);
      return;
    }
    this.touch(e.sid);
    if (e.kind === "notify" && e.notifyType) this.notify.set(e.sid, e.notifyType);
    const label = recentLabel(e, this.deps.homeDir);
    if (label === null) return;
    const list = this.recent.get(e.sid) ?? [];
    if (list[0] !== label) list.unshift(label);
    this.recent.set(e.sid, list.slice(0, MAX_RECENT));
  }

  /** Remembers the transcript path from a hook payload (Claude, Codex, Gemini send one). */
  notePayload(sid: string, payload: unknown): void {
    const p = typeof payload === "object" && payload !== null ? (payload as Record<string, unknown>).transcript_path : undefined;
    if (typeof p !== "string" || p === "" || p.length >= 4096) return;
    this.touch(sid);
    this.transcripts.set(sid, p);
  }

  /** The `transcript_path` this session's hooks reported (unchecked; resolveTranscript checks it). */
  transcriptPath(sid: string): string | undefined {
    return this.transcripts.get(sid);
  }

  request(sid: string): DetailAnswer {
    const a = this.deps.agent(sid);
    if (!a) return { error: "unknown-agent" };
    const t = this.deps.now();
    const last = this.lastAsk.get(sid);
    if (last !== undefined && t - last < RATE_MS) return { error: "rate-limited" };
    this.lastAsk.set(sid, t);

    const rules = this.rulesDetail(a);
    if (!this.deps.explainer) return { now: rules };
    const cached = this.cache.get(sid);
    if (cached && t - cached.at < CACHE_MS) {
      return cached.summary === null ? { now: rules } : { now: { ...rules, summary: cached.summary, source: "ai" } };
    }
    this.cache.delete(sid);
    // Only a transcript inside the tool's own folder is read; without one there is no AI version.
    const file = resolveTranscript({ sid, source: a.source, path: this.transcripts.get(sid), homeDir: this.deps.homeDir, env: this.deps.env });
    if (file === null) return { now: rules };
    const later = this.aiSummary(sid, file, this.deps.explainer).then((summary) => {
      const now = this.deps.agent(sid);
      return summary && now ? { ...this.rulesDetail(now), summary, source: "ai" as const } : null;
    });
    return { now: rules, later };
  }

  private rulesDetail(a: AgentState): AgentDetailMessage {
    const recent = [...(this.recent.get(a.sid) ?? [])];
    return {
      type: "agent_detail",
      sid: a.sid,
      title: a.title ?? "",
      summary: rulesSummary(a, recent),
      recent,
      waitingFor: this.waitingFor(a),
      source: "rules",
    };
  }

  private waitingFor(a: AgentState): string | null {
    const pending = this.deps.pendingExplain(a.sid);
    if (pending) return one(pending, MAX_WAITING);
    if (a.status === "waiting") return NOTIFY_TEXT[this.notify.get(a.sid) ?? ""] ?? "Needs you.";
    if (a.status === "blocked") {
      const tool = /^Waiting for approval: (.+)$/.exec(a.lastAction)?.[1];
      return one(tool ? `Waiting for approval in the terminal: ${tool}` : "Waiting for approval in the terminal.", MAX_WAITING);
    }
    return null;
  }

  /** The AI summary of the transcript tail (one run per session at a time), or null. Never throws. */
  private aiSummary(sid: string, file: string, explainer: Explainer): Promise<string | null> {
    const running = this.inflight.get(sid);
    if (running) return running;
    const run = (async () => {
      try {
        const lines = transcriptLines(await readTail(file));
        const summary = lines.length === 0 ? null : sanitizeSummary(await explainer.ask(summaryPrompt(lines, this.deps.homeDir)));
        // A failure is remembered too, so a broken AI is not asked again for 30 s.
        if (this.seen.has(sid)) this.cache.set(sid, { summary, at: this.deps.now() });
        return summary;
      } catch {
        return null;
      }
    })().finally(() => this.inflight.delete(sid));
    this.inflight.set(sid, run);
    return run;
  }
}
