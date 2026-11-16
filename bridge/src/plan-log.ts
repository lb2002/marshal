// Codex plans from its session log. Codex's hooks are documented for shell, apply_patch and MCP tools; whether
// they fire for its built-in `update_plan` is not established, so the bridge also tails the rollout file of each
// running Codex session (the same file the live feed reads, checked by resolveTranscript to lie in Codex's own
// sessions folder) and hands every `update_plan` call to the plan tracker.
//
// - First read: the last 1 MB, so a plan written before the bridge saw the session is found.
// - Then every second (default) only the bytes after the last offset, at most 256 KB per read; an incomplete
//   line is buffered up to 1 MB, a longer one is skipped. A file that shrinks is followed from its new end.
// - Only lines containing `update_plan` are parsed; nothing else from the log is kept, sent or logged.
// - At most 16 sessions are watched (the oldest is dropped); a session is dropped at its end.

import { open } from "node:fs/promises";
import { codexPlanCalls } from "./plan.js";
import type { PlanToolCall } from "./plan.js";

export const PLAN_LOG_MAX_SESSIONS = 16;
const FIRST_READ = 1024 * 1024;
const MAX_READ = 256 * 1024;
const MAX_LINE = 1024 * 1024;
const EMPTY = Buffer.alloc(0);

export interface PlanLogDeps {
  /** The session log (already checked to lie in the tool's own folder), or null while unknown. */
  logFile(sid: string): string | null;
  /** The plan-tool calls found in newly read lines, oldest first (never empty). */
  onCalls(sid: string, calls: PlanToolCall[]): void;
  intervalMs?: number;
}

interface Watch {
  file: string | null;
  offset: number;
  pending: Buffer;
  /** Inside a line longer than MAX_LINE: drop everything up to the next newline. */
  skip: boolean;
}

export class PlanLogWatcher {
  private readonly watches = new Map<string, Watch>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private running: Promise<void> = Promise.resolve();
  private inFlight = false;

  constructor(private readonly deps: PlanLogDeps) {}

  /** Starts watching a session (idempotent). */
  watch(sid: string): void {
    if (this.watches.has(sid)) return;
    while (this.watches.size >= PLAN_LOG_MAX_SESSIONS) this.watches.delete(this.watches.keys().next().value as string);
    this.watches.set(sid, { file: null, offset: 0, pending: EMPTY, skip: false });
    if (this.timer === null) {
      this.timer = setInterval(() => this.tick(), this.deps.intervalMs ?? 1000);
      this.timer.unref();
    }
    void this.pollNow();
  }

  forget(sid: string): void {
    this.watches.delete(sid);
    if (this.watches.size === 0) this.stop();
  }

  clear(): void {
    this.watches.clear();
    this.stop();
  }

  /** Watched sids (tests). */
  watched(): string[] {
    return [...this.watches.keys()];
  }

  /** One poll of every watched session, after any running one. Never rejects. */
  pollNow(): Promise<void> {
    this.running = this.running
      .then(async () => {
        for (const [sid, w] of [...this.watches]) await this.poll(sid, w);
      })
      .catch(() => {
        /* display only: a failed read changes nothing */
      });
    return this.running;
  }

  private tick(): void {
    if (this.inFlight) return;
    this.inFlight = true;
    void this.pollNow().finally(() => {
      this.inFlight = false;
    });
  }

  private stop(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
  }

  private async poll(sid: string, w: Watch): Promise<void> {
    if (this.watches.get(sid) !== w) return;
    if (w.file === null) {
      const file = this.deps.logFile(sid);
      if (file === null) return;
      const size = await fileSize(file);
      if (size === null) return;
      w.file = file;
      const start = Math.max(0, size - FIRST_READ);
      let buf = await readAt(file, start, size - start);
      w.offset = start + buf.length;
      if (start > 0) {
        const nl = buf.indexOf(10);
        buf = nl < 0 ? EMPTY : buf.subarray(nl + 1);
      }
      this.lines(sid, w, buf);
      return;
    }
    const size = await fileSize(w.file);
    if (size === null) return;
    if (size < w.offset) {
      w.offset = size;
      w.pending = EMPTY;
      w.skip = false;
      return;
    }
    const len = Math.min(size - w.offset, MAX_READ);
    if (len === 0) return;
    let chunk = await readAt(w.file, w.offset, len);
    w.offset += chunk.length;
    if (w.skip) {
      const nl = chunk.indexOf(10);
      if (nl < 0) return;
      chunk = chunk.subarray(nl + 1);
      w.skip = false;
    }
    this.lines(sid, w, w.pending.length > 0 ? Buffer.concat([w.pending, chunk]) : chunk);
  }

  /** Parses the complete lines of `data`, keeps the incomplete rest (or skips it when too long). */
  private lines(sid: string, w: Watch, data: Buffer): void {
    const last = data.lastIndexOf(10);
    const rest = data.subarray(last + 1);
    if (rest.length > MAX_LINE) {
      w.pending = EMPTY;
      w.skip = true;
    } else {
      w.pending = Buffer.from(rest);
    }
    if (last < 0 || this.watches.get(sid) !== w) return;
    const calls = codexPlanCalls(data.subarray(0, last).toString("utf8"));
    if (calls.length > 0) this.deps.onCalls(sid, calls);
  }
}

async function fileSize(file: string): Promise<number | null> {
  try {
    const fh = await open(file, "r");
    try {
      return (await fh.stat()).size;
    } finally {
      await fh.close();
    }
  } catch {
    return null;
  }
}

async function readAt(file: string, pos: number, len: number): Promise<Buffer> {
  const fh = await open(file, "r");
  try {
    const buf = Buffer.alloc(len);
    const { bytesRead } = await fh.read(buf, 0, len, pos);
    return buf.subarray(0, bytesRead);
  } finally {
    await fh.close();
  }
}
