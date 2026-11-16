import { createWriteStream, mkdirSync, type WriteStream } from "node:fs";
import { readFile } from "node:fs/promises";
import { dirname } from "node:path";
import { anonymizeEvent, type AnonymizeOpts } from "./anonymize.js";
import { AGENT_EVENT_KINDS, AGENT_SOURCES, type AgentEvent } from "./events.js";
import { relInside } from "./risk/paths.js";

/**
 * Appends anonymised events as NDJSON, with `t` rewritten to ms since the first recorded event.
 * Each session is anonymised against its own root: the first `cwd` seen for its `sid`. Until a session
 * has sent a cwd, and when that cwd is home or above it, `opts.repoRoot` (the CLI's working directory) stands in.
 */
export class Recorder {
  private readonly stream: WriteStream;
  private readonly done: Promise<void>;
  private readonly roots = new Map<string, string>();
  private base: number | null = null;
  private closed = false;

  constructor(
    filePath: string,
    private readonly opts: AnonymizeOpts,
  ) {
    mkdirSync(dirname(filePath), { recursive: true });
    this.stream = createWriteStream(filePath, { encoding: "utf8" });
    this.done = new Promise<void>((resolve, reject) => {
      this.stream.once("finish", resolve);
      this.stream.once("error", reject);
    });
    this.done.catch(() => {}); // surfaced by close()
  }

  write(e: AgentEvent): void {
    if (this.closed) throw new Error("Recorder is closed");
    this.base ??= e.t;
    if (e.cwd && !this.roots.has(e.sid)) {
      // home itself or a directory above it is no project root: home paths must stay `~/...`
      this.roots.set(e.sid, relInside(this.opts.homeDir, e.cwd) === null ? e.cwd : this.opts.repoRoot);
    }
    const rec = anonymizeEvent(e, { ...this.opts, repoRoot: this.roots.get(e.sid) ?? this.opts.repoRoot });
    rec.t = e.t - this.base;
    this.stream.write(JSON.stringify(rec) + "\n");
  }

  close(): Promise<void> {
    if (!this.closed) {
      this.closed = true;
      this.stream.end();
    }
    return this.done;
  }
}

const STRING_FIELDS = ["tool", "path", "command", "notifyType", "summary", "cwd", "title"] as const;
const NUMBER_FIELDS = ["ctxPct", "costUsd"] as const;

const PLAN_STATUSES = new Set(["pending", "in_progress", "done"]);

/** Why a recorded `plan` is malformed, or null. */
function planProblem(p: unknown): string | null {
  if (typeof p !== "object" || p === null || Array.isArray(p)) return "plan must be an object";
  const o = p as Record<string, unknown>;
  if (!Array.isArray(o.steps)) return "plan.steps must be an array";
  for (const s of o.steps) {
    const step = s as Record<string, unknown> | null;
    if (typeof step !== "object" || step === null || typeof step.text !== "string" || typeof step.status !== "string" || !PLAN_STATUSES.has(step.status)) {
      return "plan.steps must be {text, status: pending|in_progress|done}";
    }
  }
  if (!(typeof o.more === "number" && Number.isSafeInteger(o.more) && o.more >= 0)) return "plan.more must be a whole number >= 0";
  return null;
}

/** Why `o` is not an AgentEvent, or null when it is one. */
function eventProblem(o: Record<string, unknown>): string | null {
  if (typeof o.sid !== "string" || typeof o.t !== "number" || typeof o.kind !== "string") return "not an agent event (needs sid, t, kind)";
  for (const [k, v] of Object.entries(o)) if (typeof v === "number" && !Number.isFinite(v)) return `${k} must be a finite number`;
  if (!AGENT_EVENT_KINDS.has(o.kind)) return `kind ${JSON.stringify(o.kind).slice(0, 40)} is unknown`;
  for (const k of STRING_FIELDS) if (o[k] !== undefined && typeof o[k] !== "string") return `${k} must be a string`;
  if (o.source !== undefined && !(typeof o.source === "string" && AGENT_SOURCES.has(o.source))) return "source must be claude, muse, codex or gemini";
  for (const k of NUMBER_FIELDS) {
    if (o[k] !== undefined && !(typeof o[k] === "number" && Number.isFinite(o[k]))) return `${k} must be a number`;
  }
  if (o.plan !== undefined) return planProblem(o.plan);
  return null;
}

/** Reads an NDJSON recording; rejects the first line that is not a valid AgentEvent, with its 1-based number. */
export async function readRecording(filePath: string): Promise<AgentEvent[]> {
  const lines = (await readFile(filePath, "utf8")).split("\n");
  const events: AgentEvent[] = [];
  lines.forEach((line, i) => {
    if (line.trim() === "") return;
    let v: unknown;
    try {
      v = JSON.parse(line);
    } catch (err) {
      throw new Error(`${filePath}: line ${i + 1}: invalid JSON (${(err as Error).message})`);
    }
    const o = v as Record<string, unknown> | null;
    const problem = typeof o !== "object" || o === null || Array.isArray(o) ? "not an agent event (needs sid, t, kind)" : eventProblem(o);
    if (problem) throw new Error(`${filePath}: line ${i + 1}: ${problem}`);
    events.push(o as unknown as AgentEvent);
  });
  return events;
}
