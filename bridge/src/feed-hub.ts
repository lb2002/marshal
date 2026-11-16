// The live feed poller (contract `.superpowers/sdd/2026-11-12-contract-live-feed.md`): the paired headset
// subscribes to at most 5 agents (the main window plus four pulled-out windows); each gets a snapshot of its last 30 entries, then the new entries of its
// session log, polled every second and read only from the last offset, at most 64 KB per read. A line that is
// still incomplete is buffered across polls up to 1 MB (Gemini rewrites its whole message list in one line);
// a longer line is skipped. All of it is display text only, held in memory just long enough to send, and never
// recorded or logged.

import { open } from "node:fs/promises";
import type { AgentSource } from "./events.js";
import { feedEntries, type FeedContext, type FeedEntry } from "./feed.js";

export type FeedMessage =
  | { type: "feed_snapshot"; sid: string; entries: FeedEntry[] }
  | { type: "feed_append"; sid: string; entries: FeedEntry[] };

export const FEED_MAX_SUBS = 5;
/** Entries in a snapshot, and per append message (a larger burst goes out in several). */
export const FEED_SNAPSHOT = 30;
export const FEED_MAX_READ = 64 * 1024;
/** Longest line buffered across polls; a longer one is skipped. */
export const FEED_MAX_LINE = 1024 * 1024;
/** Ids remembered per subscription, so a re-read never sends an entry twice. */
const MAX_SENT_IDS = 2000;

export interface FeedHubDeps {
  /** The agent's source; undefined for a sid the bridge does not know. */
  source(sid: string): AgentSource | undefined;
  /** The session log to read (already checked to lie in the tool's own folder), or null while unknown. */
  logFile(sid: string): string | null;
  send(m: FeedMessage): void;
  homeDir: string;
  now(): number;
  /** Poll interval, at most 1 s by contract (default 1000). */
  intervalMs?: number;
  /** Called with the byte count of every read (tests). */
  onRead?(bytes: number): void;
}

interface Sub {
  sid: string;
  source: AgentSource;
  file: string | null;
  /** Next byte to read. */
  offset: number;
  /** Bytes of an incomplete line read so far, and where they start in the log. */
  pending: Buffer;
  pendingStart: number;
  /** Inside a line that is cut or too long: drop everything up to the next newline. */
  skip: boolean;
  sent: Set<string>;
  /** Parser state across reads (Gemini). */
  state: NonNullable<FeedContext["state"]>;
}

const EMPTY = Buffer.alloc(0);

export class FeedHub {
  private readonly subs = new Map<string, Sub>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private running: Promise<void> = Promise.resolve();
  private inFlight = false;

  constructor(private readonly deps: FeedHubDeps) {}

  /** Subscribed sids, oldest first (tests). */
  subscriptions(): string[] {
    return [...this.subs.keys()];
  }

  /** True while the poll timer runs. */
  active(): boolean {
    return this.timer !== null;
  }

  /** Starts (or restarts) a subscription; the oldest is dropped beyond FEED_MAX_SUBS. The snapshot follows asynchronously. */
  subscribe(sid: string): "ok" | "unknown-agent" {
    const source = this.deps.source(sid);
    if (source === undefined) return "unknown-agent";
    this.subs.delete(sid);
    while (this.subs.size >= FEED_MAX_SUBS) this.subs.delete(this.subs.keys().next().value as string);
    const sub: Sub = { sid, source, file: null, offset: 0, pending: EMPTY, pendingStart: 0, skip: false, sent: new Set(), state: {} };
    this.subs.set(sid, sub);
    void this.queue(() => this.snapshot(sub));
    if (this.timer === null) {
      this.timer = setInterval(() => this.tick(), this.deps.intervalMs ?? 1000);
      this.timer.unref();
    }
    return "ok";
  }

  unsubscribe(sid: string): void {
    this.subs.delete(sid);
    if (this.subs.size === 0) this.stop();
  }

  /** Ends every subscription (the headset disconnected or was replaced, or the bridge stops). */
  clear(): void {
    this.subs.clear();
    this.stop();
  }

  /** One timer tick: skipped while the previous poll is still running. */
  tick(): void {
    if (this.inFlight) return;
    this.inFlight = true;
    void this.pollNow().finally(() => {
      this.inFlight = false;
    });
  }

  /** One poll of every subscription (the timer calls this through tick; tests call it directly). */
  pollNow(): Promise<void> {
    return this.queue(async () => {
      for (const sub of [...this.subs.values()]) await this.poll(sub);
    });
  }

  /** Resolves when the queued snapshots and polls are done (tests). */
  settled(): Promise<void> {
    return this.running;
  }

  private stop(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
  }

  /** Snapshots and polls run one at a time, so offsets never race. Never rejects. */
  private queue(job: () => Promise<void>): Promise<void> {
    this.running = this.running.then(job).catch(() => {
      /* display only: a failed read sends nothing */
    });
    return this.running;
  }

  private current(sub: Sub): boolean {
    return this.subs.get(sub.sid) === sub;
  }

  /** A snapshot sends the last 30 new entries; appends send every new entry, 30 per message. */
  private send(sub: Sub, type: FeedMessage["type"], entries: FeedEntry[]): void {
    let fresh = entries.filter((e) => !sub.sent.has(e.id));
    if (type === "feed_snapshot") fresh = fresh.slice(-FEED_SNAPSHOT);
    for (const e of fresh) {
      sub.sent.add(e.id);
      if (sub.sent.size > MAX_SENT_IDS) sub.sent.delete(sub.sent.values().next().value as string);
    }
    if (!this.current(sub)) return;
    if (type === "feed_snapshot") {
      this.deps.send({ type, sid: sub.sid, entries: fresh });
      return;
    }
    for (let i = 0; i < fresh.length; i += FEED_SNAPSHOT) this.deps.send({ type, sid: sub.sid, entries: fresh.slice(i, i + FEED_SNAPSHOT) });
  }

  private parse(sub: Sub, buf: Buffer, start: number): FeedEntry[] {
    return feedEntries(buf, start, { sid: sub.sid, source: sub.source, homeDir: this.deps.homeDir, now: this.deps.now(), state: sub.state });
  }

  private async read(file: string, pos: number, len: number): Promise<Buffer> {
    const buf = await readAt(file, pos, len);
    this.deps.onRead?.(buf.length);
    return buf;
  }

  /**
   * Finds the log and reads its tail backwards in 64 KB steps until at least one complete line is found (at most
   * 1 MB, for Gemini's long lines). Returns the entries of the complete lines; a trailing partial line is kept.
   */
  private async locate(sub: Sub): Promise<FeedEntry[] | null> {
    const file = this.deps.logFile(sub.sid);
    if (file === null) return null;
    const size = await fileSize(file);
    if (size === null) return null;
    sub.file = file;
    sub.offset = size;
    let start = size;
    let buf = EMPTY;
    for (;;) {
      const lastNl = buf.lastIndexOf(10);
      const enough = lastNl >= 0 && (start === 0 || buf.lastIndexOf(10, lastNl - 1) >= 0);
      if (enough || start === 0 || buf.length >= FEED_MAX_LINE) break;
      const from = Math.max(0, start - FEED_MAX_READ);
      buf = Buffer.concat([await this.read(file, from, start - from), buf]);
      start = from;
    }
    let first = 0;
    if (start > 0) {
      const nl = buf.indexOf(10);
      if (nl < 0) {
        // Inside one line longer than 1 MB: skip to its end.
        sub.skip = true;
        return [];
      }
      first = nl + 1;
    }
    const last = buf.lastIndexOf(10);
    if (last < first) {
      sub.pending = Buffer.from(buf.subarray(first));
      sub.pendingStart = start + first;
      return [];
    }
    sub.pending = Buffer.from(buf.subarray(last + 1));
    sub.pendingStart = start + last + 1;
    return this.parse(sub, buf.subarray(first, last + 1), start + first);
  }

  private async snapshot(sub: Sub): Promise<void> {
    if (!this.current(sub)) return;
    this.send(sub, "feed_snapshot", (await this.locate(sub)) ?? []);
  }

  private async poll(sub: Sub): Promise<void> {
    if (!this.current(sub)) return;
    if (sub.file === null) {
      // A log that appears later: its tail arrives as an append.
      const found = await this.locate(sub);
      if (found !== null) this.send(sub, "feed_append", found);
      return;
    }
    this.send(sub, "feed_append", await this.readNew(sub));
  }

  /** Reads at most 64 KB after the offset and returns the entries of the lines it completes. */
  private async readNew(sub: Sub): Promise<FeedEntry[]> {
    const file = sub.file as string;
    const size = await fileSize(file);
    if (size === null) return [];
    if (size < sub.offset) {
      // The log was cut or replaced: go on from its new end.
      sub.offset = size;
      sub.pending = EMPTY;
      sub.skip = false;
      return [];
    }
    const len = Math.min(size - sub.offset, FEED_MAX_READ);
    if (len === 0) return [];
    let chunk = await this.read(file, sub.offset, len);
    let chunkStart = sub.offset;
    sub.offset += chunk.length;
    if (sub.skip) {
      const nl = chunk.indexOf(10);
      if (nl < 0) return [];
      chunk = chunk.subarray(nl + 1);
      chunkStart += nl + 1;
      sub.skip = false;
      sub.pending = EMPTY;
    }
    if (sub.pending.length > 0) {
      // The buffered line ends in this chunk (or not): longer than 1 MB in total means it is skipped.
      const nl = chunk.indexOf(10);
      if (sub.pending.length + (nl < 0 ? chunk.length : nl) > FEED_MAX_LINE) {
        sub.pending = EMPTY;
        if (nl < 0) {
          sub.skip = true;
          return [];
        }
        chunk = chunk.subarray(nl + 1);
        chunkStart += nl + 1;
      }
    }
    const data = sub.pending.length > 0 ? Buffer.concat([sub.pending, chunk]) : chunk;
    const dataStart = sub.pending.length > 0 ? sub.pendingStart : chunkStart;
    const last = data.lastIndexOf(10);
    if (last < 0) {
      if (data.length > FEED_MAX_LINE) {
        sub.pending = EMPTY;
        sub.skip = true;
      } else {
        sub.pending = Buffer.from(data);
        sub.pendingStart = dataStart;
      }
      return [];
    }
    sub.pending = Buffer.from(data.subarray(last + 1));
    sub.pendingStart = dataStart + last + 1;
    return this.parse(sub, data.subarray(0, last + 1), dataStart);
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
