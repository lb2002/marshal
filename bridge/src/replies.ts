// Replies from the headset to an agent (contract 2026-11-12-contract-agent-reply). A Claude Stop hook
// (`hook --source claude --event Stop --listen`, async with asyncRewake) long-polls `GET /reply?sid=`; the
// headset's `agent_reply` is handed to that listener, which wakes Claude with it.
//
// The text is the user's own input. It is held only until the listener takes it, and is never logged,
// recorded or kept anywhere else.

/** Longest reply text, in characters (code points). */
export const MAX_REPLY_TEXT = 1000;
/** Longest wait of one listener: the hook's own timeout is 900 s. */
export const REPLY_WAIT_MS = 15 * 60_000;

/** Newlines kept (CRLF and CR become LF), every other control character removed, trimmed. */
export function cleanReply(text: string): string {
  return text
    .replace(/\r\n?/g, "\n")
    .replace(/[\u0000-\u0009\u000B-\u001F\u007F-\u009F]/g, "")
    .trim();
}

/** Writes the listener's HTTP answer (the text, or null for `{}`); true only when the write succeeded. */
export type ReplyWriter = (text: string | null) => Promise<boolean>;

interface Listener {
  /** Identifies the hook process that listens (its session's cwd or transcript name). */
  key: string;
  write: ReplyWriter;
  timer: ReturnType<typeof setTimeout>;
}

/** At most this many listeners wait at once, over all sessions. */
export const MAX_LISTENERS = 32;

/**
 * At most one waiting listener per session and MAX_LISTENERS in all. A new listener with the same key
 * replaces the old one (which is answered with nothing); one with another key is a conflict and the
 * existing listener stays.
 */
export class ReplyHub {
  private readonly waitMs: number;
  private readonly max: number;
  private readonly listeners = new Map<string, Listener>();

  constructor(opts: { waitMs?: number; max?: number } = {}) {
    this.waitMs = opts.waitMs ?? REPLY_WAIT_MS;
    this.max = opts.max ?? MAX_LISTENERS;
  }

  listen(sid: string, key: string, write: ReplyWriter): "ok" | "conflict" | "full" {
    const old = this.listeners.get(sid);
    if (old && old.key !== key) return "conflict";
    if (!old && this.listeners.size >= this.max) return "full";
    if (old) this.end(sid, old, null);
    const timer = setTimeout(() => {
      const l = this.listeners.get(sid);
      if (l && l.write === write) this.end(sid, l, null);
    }, this.waitMs);
    timer.unref?.();
    this.listeners.set(sid, { key, write, timer });
    return "ok";
  }

  waiting(sid: string): boolean {
    return this.listeners.has(sid);
  }

  size(): number {
    return this.listeners.size;
  }

  /** Hands `text` to the waiting listener; true only when its answer was written successfully. */
  async deliver(sid: string, text: string): Promise<boolean> {
    const l = this.listeners.get(sid);
    if (!l) return false;
    return this.end(sid, l, text);
  }

  /** Withdraws exactly this listener without writing (its connection is gone). */
  cancel(sid: string, write: ReplyWriter): void {
    const l = this.listeners.get(sid);
    if (l && l.write === write) {
      clearTimeout(l.timer);
      this.listeners.delete(sid);
    }
  }

  /** Ends the session's listener with nothing (session end). */
  release(sid: string): void {
    const l = this.listeners.get(sid);
    if (l) void this.end(sid, l, null);
  }

  closeAll(): void {
    for (const [sid, l] of [...this.listeners]) void this.end(sid, l, null);
  }

  private async end(sid: string, l: Listener, text: string | null): Promise<boolean> {
    clearTimeout(l.timer);
    this.listeners.delete(sid);
    try {
      return await l.write(text);
    } catch {
      return false;
    }
  }
}

/**
 * Events that show a session is running: also its Stop (the listener's own, posted right before it listens)
 * and notifications, so a chat-only session can always listen after its turn. Anyone who can post them
 * already holds the ingest token, so this moves no trust boundary.
 */
const ACTIVE_KINDS = new Set(["session_start", "prompt", "notify", "stop", "tool_start", "tool_end", "tool_fail", "permission"]);
/** A session without such an event for this long may not listen. */
export const LISTEN_ACTIVE_MS = 2 * 3600_000;
const MAX_SESSIONS = 200;

interface SessionMark {
  active?: number;
  cwd?: string;
  transcript?: string;
}

/**
 * Who may listen for replies: a session the bridge has seen running in the last 2 h, and only with the cwd
 * or transcript name its own hooks reported. Bounded to 200 sessions (least recently noted first out).
 */
export class ReplySessions {
  private readonly marks = new Map<string, SessionMark>();

  note(sid: string, e: { kind: string; t: number; cwd?: string; transcript?: string }): void {
    const m = this.marks.get(sid) ?? {};
    this.marks.delete(sid);
    if (ACTIVE_KINDS.has(e.kind)) m.active = e.t;
    if (e.cwd) m.cwd = e.cwd;
    if (e.transcript) m.transcript = e.transcript;
    this.marks.set(sid, m);
    while (this.marks.size > MAX_SESSIONS) this.marks.delete(this.marks.keys().next().value as string);
  }

  forget(sid: string): void {
    this.marks.delete(sid);
  }

  size(): number {
    return this.marks.size;
  }

  check(sid: string, who: { cwd?: string; transcript?: string }, now: number): "ok" | "unknown" | "mismatch" {
    const m = this.marks.get(sid);
    if (!m || m.active === undefined || now - m.active > LISTEN_ACTIVE_MS) return "unknown";
    if (who.cwd && m.cwd && who.cwd === m.cwd) return "ok";
    if (who.transcript && m.transcript && who.transcript === m.transcript) return "ok";
    return "mismatch";
  }
}

/** The file name of a transcript path (either separator). */
export function transcriptName(p: unknown): string | undefined {
  if (typeof p !== "string" || p === "") return undefined;
  const name = p.split(/[\\/]/).pop();
  return name ? name : undefined;
}
