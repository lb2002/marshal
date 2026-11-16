// `lotse hook --source claude|muse|codex|gemini|opencode`: the command hook those tools run. It forwards the hook JSON
// to the bridge and, for an approval, prints the tool's own answer. Every failure prints nothing, so the tool
// falls back to its own prompt (Muse: the no-decision deny). It never prints an allow the bridge did not give
// (see adapters.ts).

import { request } from "node:http";
import { adaptHook, isMusePermission, museNoDecision, preToolDeny, shimOutput, type ShimSource } from "./adapters.js";
import { APPROVAL_TEXT, PRE_TOOL_TEXT, TIMEOUT, UNREACHABLE } from "./hook-fallback.js";
import { PRE_TOOL_HEADER, PRE_TOOL_HEADER_VALUE, haltRefusal, preToolRefusal } from "./halt.js";
import type { HookKind } from "./halt.js";

/**
 * POSTs `body` (with extra `headers`) and resolves the parsed JSON of a 200 answer, UNREACHABLE when the connection
 * was refused (the bridge is off), TIMEOUT when it was accepted but not answered in time, undefined for anything
 * else. Never rejects.
 */
export type ShimPost = (path: string, body: string, ms: number, headers?: Record<string, string>) => Promise<unknown>;

/** The bridge holds a request up to 90 s; the shim waits a little longer, the hook timeout (120 s) longer still. */
export const PERM_WAIT_MS = 100_000;

export interface ShimOptions {
  /** Time left for an observe-only event (the shared 500 ms hook deadline minus what stdin took). */
  evMs: number;
  /** Time an approval may wait for the headset. */
  permMs: number;
  platform?: NodeJS.Platform;
  /** The hook was installed for the tool's approval event (`--event`): Muse then denies on every failure path. */
  approval?: boolean;
  /**
   * Whether a live halt marker exists (halt-marker.ts). Asked only for a pre-tool or permission event that is about
   * to end without a decision and without an answer from the bridge (stdin slow or empty, budget spent, timeout,
   * an error); never when the bridge answered (its answer wins) or refused the connection (the marker is stale).
   */
  haltMarker?: () => boolean;
  /** What the hook is, from its arguments (`--event`); absent: read from the (partial) stdin text. */
  kind?: HookKind;
}

/** The kind of a hook from its stdin text, for hooks installed without `--event`. */
export function kindFromText(raw: string): HookKind {
  return PRE_TOOL_TEXT.test(raw) ? "pre" : APPROVAL_TEXT.test(raw) ? "perm" : "other";
}

/**
 * Runs one hook: returns what to print on stdout. Never throws. Without a headset decision it is ""
 * (the tool's own prompt), except for a Muse permission event, which gets the no-decision deny.
 */
export async function runShim(source: ShimSource, raw: string, post: ShimPost, opts: ShimOptions): Promise<string> {
  const museApproval = source === "muse" && (opts.approval === true || isMusePermission(raw));
  const fallback = () => (museApproval ? museNoDecision() : "");
  const kind = opts.kind ?? kindFromText(raw);
  // A live bridge's answer (any parsed 200 body) wins over a marker; a refused connection means the marker is stale.
  let answered = false;
  let refused = false;
  const note = (reply: unknown): unknown => {
    if (reply === UNREACHABLE) refused = true;
    else if (typeof reply === "object" && reply !== null) answered = true;
    return reply;
  };
  /** A pre-tool or permission event ending without a decision and without an answer: a live marker means halted. */
  const markerSaysHalted = () => kind !== "other" && !answered && !refused && opts.haltMarker?.() === true;
  const silent = () => (markerSaysHalted() ? haltRefusal(source, kind as "pre" | "perm") : fallback());
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return silent(); // stdin slow, empty or cut
  }
  try {
    const req = adaptHook(source, parsed, opts.platform);
    if (!req) return silent();
    const body = JSON.stringify(req.body);
    if (req.route === "/ev") {
      // The same single POST as always; only a halted bridge answers a pre-tool hook with a refusal (halt.ts). The
      // header lets it refuse before reading the body. A budget already spent counts as a timeout.
      const pre = req.body.hook_event_name === "PreToolUse";
      const reply = note(opts.evMs > 0 ? await post("/ev", body, opts.evMs, pre ? { [PRE_TOOL_HEADER]: PRE_TOOL_HEADER_VALUE } : undefined) : TIMEOUT);
      if (pre) {
        const refusal = preToolRefusal(reply);
        if (refusal !== null) return preToolDeny(source, refusal);
      }
      // An approval hook that got something else is not understood: Muse fails closed.
      return silent();
    }
    const reply = note(await post("/perm", body, opts.permMs));
    return markerSaysHalted() ? haltRefusal(source, kind as "pre" | "perm") : shimOutput(source, reply);
  } catch {
    return silent();
  }
}

/**
 * `GET /reply?sid=`: waits for the headset's reply to this session. The text, or null for anything else
 * (no reply in time, bridge down, wrong token, a bad answer). Never rejects.
 */
export function getReply(port: number, sid: string, token: string | null, ms: number, who: { cwd?: string; transcript?: string } = {}): Promise<string | null> {
  return new Promise((resolve) => {
    let settled = false;
    const done = (v: string | null) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(v);
    };
    const timer = setTimeout(() => {
      req.destroy();
      done(null);
    }, ms);
    const headers: Record<string, string> = {};
    if (token) headers["X-Lotse-Token"] = token;
    const q = new URLSearchParams({ sid });
    if (who.cwd) q.set("cwd", who.cwd);
    if (who.transcript) q.set("transcript", who.transcript);
    const req = request({ host: "127.0.0.1", port, path: `/reply?${q.toString()}`, method: "GET", headers }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (c: string) => (text += c));
      res.on("error", () => done(null));
      res.on("close", () => done(null));
      res.on("end", () => {
        if (res.statusCode !== 200) return done(null);
        try {
          const v: unknown = JSON.parse(text);
          const t = typeof v === "object" && v !== null ? (v as Record<string, unknown>).text : undefined;
          done(typeof t === "string" && t.trim() !== "" ? t : null);
        } catch {
          done(null);
        }
      });
    });
    req.on("error", () => done(null));
    req.end();
  });
}

/**
 * The real transport: 127.0.0.1 only, token header when known, one overall deadline. ECONNREFUSED gives UNREACHABLE,
 * the deadline TIMEOUT.
 */
export function postForReply(port: number, path: string, body: string, token: string | null, ms: number, extra: Record<string, string> = {}): Promise<unknown> {
  return new Promise((resolve) => {
    let settled = false;
    const done = (v: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(v);
    };
    const timer = setTimeout(() => {
      done(TIMEOUT);
      req.destroy();
    }, ms);
    const headers: Record<string, string | number> = { ...extra, "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) };
    if (token) headers["X-Lotse-Token"] = token;
    const req = request({ host: "127.0.0.1", port, path, method: "POST", headers }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (c: string) => (text += c));
      res.on("error", () => done(undefined));
      res.on("close", () => done(undefined)); // after "end" this is a no-op; before it, the answer was cut
      res.on("end", () => {
        if (res.statusCode !== 200) return done(undefined);
        try {
          done(JSON.parse(text));
        } catch {
          done(undefined);
        }
      });
    });
    req.on("error", (err: NodeJS.ErrnoException) => done(err.code === "ECONNREFUSED" ? UNREACHABLE : undefined));
    req.end(body);
  });
}
