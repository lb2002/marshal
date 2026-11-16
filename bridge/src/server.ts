// Network edge of the bridge. HTTP ingest (127.0.0.1 only) receives Claude Code
// hooks; the WebSocket (0.0.0.0) serves the one paired headset. Unpaired sockets
// never see agent data and never influence decisions. Any doubt answers a hook
// with {} (no decision).
//
// Transport is cleartext on the LAN: pair on a trusted network. The 6-digit code
// is single-use; a successful hello trades it for a random session token that is
// the only credential for reconnecting.

import { createServer } from "node:http";
import type { IncomingMessage, Server, ServerResponse } from "node:http";
import { randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import type { AddressInfo } from "node:net";
import { homedir } from "node:os";
import { WebSocketServer, WebSocket } from "ws";
import { normalizeHook, normalizeStatus } from "./events.js";
import type { AgentEvent } from "./events.js";
import { COLLISION_WINDOW_MS, createPipeline } from "./pipeline.js";
import { demoPermission } from "./demo-paths.js";
import { PermissionBroker } from "./broker.js";
import type { OpenContext, PermRequest } from "./broker.js";
import { BUDGET_IMPACT, ImpactScheduler, computeImpact, realImpactDeps } from "./impact.js";
import type { Impact, ImpactDeps, ImpactRequest } from "./impact.js";
import { forSource, ruleExplanation } from "./explain.js";
import type { Explainer, Explanation } from "./explain.js";
import { parseClientMessage } from "./protocol.js";
import { sanitizeTitle, titlePrompt } from "./agent-ai.js";
import { AgentDetails } from "./agent-detail.js";
import { ReplyHub, ReplySessions, transcriptName } from "./replies.js";
import { FeedHub } from "./feed-hub.js";
import { resolveTranscript } from "./transcript.js";
import { PlanGate, PlanTracker, SubagentGuard, planCallFromHook } from "./plan.js";
import type { PlanChange, PlanToolCall } from "./plan.js";
import { PlanLogWatcher } from "./plan-log.js";
import type { HaltState, ServerMessage } from "./protocol.js";
import { ENFORCEMENT, HALT_MESSAGE, RELEASE_WORDS, PRE_TOOL_HEADER, PRE_TOOL_HEADER_VALUE, permHaltReply, preToolHaltReply } from "./halt.js";
import { removeHaltMarker, writeHaltMarker } from "./halt-marker.js";
import type { Enforcement } from "./halt.js";
import { PRODUCT_NAME } from "./product.js";
import { pairingPayload } from "./pair-qr.js";
import { encodeQr, renderQrSvg } from "./qr.js";
import { UI_PAGE } from "./ui-page.js";

export interface ServerOptions {
  httpPort: number;
  wsPort: number;
  /** Initial single-use pairing code, exactly 6 digits. */
  pairingCode: string;
  holdMs: number;
  budgetUsd: number;
  onEvent?: (e: AgentEvent) => void;
  /** Called once, on the first successful hello (the demo starts its replay here). */
  onPaired?: () => void;
  /** Called each time the pairing code is used up by a successful pairing (the terminal then marks it invalid). */
  onCodeUsed?: () => void;
  /** Directory the demo placeholder `<repo>` maps to in inject(); defaults to process.cwd(). */
  demoRoot?: string;
  /** When set, every HTTP request must carry a matching `X-Lotse-Token` header. */
  ingestToken?: string;
  /** WebSocket ping interval; a client silent for one full interval is terminated. Default 10 s. */
  heartbeatMs?: number;
  /** Sockets without a valid hello are closed after this. Default 10 s. */
  helloTimeoutMs?: number;
  /** Optional AI explainer; without it no AI calls are made and only the rule explanation is sent. */
  explainer?: Explainer;
  /**
   * What a destructive request would hit (impact.ts). Default: the real check against this machine's file
   * system (`lstat` and directory listings only); `false` turns it off. Tests inject their own.
   */
  impact?: ((req: ImpactRequest) => Promise<Impact | null>) | false;
  /** Clock for pairing lockouts (tests). */
  now?: () => number;
  /** Home directory for shortening paths in titles and AI prompts; defaults to os.homedir(). */
  homeDir?: string;
  /** Console lines (halt begins and ends, the reminder); default console.log. */
  log?: (line: string) => void;
  /** Reminder interval while halted with no headset connected; default 60 s (contract). */
  haltReminderMs?: number;
  /**
   * The bridge's own console, for releasing a halt: while halted and only when `isTTY`, the line `continue` or `weiter` (Enter)
   * releases it; nothing else typed has any effect. Absent (tests, `pack`): there is no console path.
   */
  console?: { input: NodeJS.ReadableStream; isTTY: boolean };
  /**
   * The halt marker file (`~/.lotse/halt`, halt-marker.ts): written while halted, removed on release and on a clean
   * shutdown; a marker of a dead process is removed at start. Absent (tests, `pack`): no marker.
   */
  haltMarker?: string;
  /** Longest wait of one `GET /reply` listener; default 15 min. */
  replyWaitMs?: number;
  /** Called when a reply listener starts waiting (tests). */
  onReplyListen?: (sid: string) => void;
  /** Live feed poll interval; default 1 s (the contract's maximum). */
  feedIntervalMs?: number;
  /** Plan rate limit per agent; default 500 ms (contract). */
  planIntervalMs?: number;
  /** Poll interval of the Codex plan log reader; default 1 s. */
  planLogIntervalMs?: number;
  /** Plan state (tracker, rate limit, Codex log watch) of a session without events for this long is dropped; default 30 min. */
  planIdleMs?: number;
  /**
   * The local status page (`GET /ui`, `GET /ui/state`, `POST /ui/code`, ui-page.ts): on only when given (`connect`
   * and `start`; never tests by default or `pack`). `key` (the `k` query parameter) stands in for the ingest token,
   * which the browser does not have; `view` adds what only the CLI knows.
   */
  ui?: { key: string; view: () => UiView };
  /** Called when the status page issued a fresh pairing code (the terminal then shows it as on Enter). */
  onCodeReplaced?: (code: string) => void;
}

/** What the CLI adds to the status page's state. */
export interface UiView {
  version: string;
  /** LAN addresses, the QR code's host first. */
  addresses: string[];
  /** The address in the pairing QR code; null: no QR code. */
  qrHost: string | null;
  explain: "on" | "off";
  /** What `connect` set up, for the page. */
  tools?: { name: string; status: string }[];
}

/** `GET /ui/state`. Never carries the ingest token or the session token. */
export interface UiState {
  version: string;
  paired: boolean;
  /** The current pairing code, only while it can still be used. */
  code: string | null;
  codeExpiresAt: number | null;
  wsPort: number;
  addresses: string[];
  qrHost: string | null;
  /** The pairing QR code as SVG markup (payload exactly as pair-qr.ts); null without a code or a host. */
  qrSvg: string | null;
  halted: boolean;
  pendingRequests: number;
  agents: { name: string; source: string; state: string }[];
  explain: "on" | "off";
  tools: { name: string; status: string }[];
}

const UI_PATHS = new Set(["/ui", "/ui/state", "/ui/code"]);
const UI_CSP = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
const UI_HEADERS = {
  "Cache-Control": "no-store",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
  "Content-Security-Policy": UI_CSP,
};

export interface ServerHandle {
  close(): Promise<void>;
  /** Processes an event exactly like POST /ev (demo mode); permission events also open a broker request. */
  inject(e: AgentEvent): void;
  /** Issues a fresh single-use pairing code (the previous one stops working). */
  newPairingCode(): string;
  httpPort: number;
  wsPort: number;
  httpHost: string;
  wsHost: string;
}

const HTTP_HOST = "127.0.0.1";
const WS_HOST = "0.0.0.0";
const MAX_BODY = 1024 * 1024;
const MAX_WS_MESSAGE = 64 * 1024;
const MAX_UNPAIRED = 8;
const MAX_UNPAIRED_PER_ADDR = 3;
export const CODE_TTL_MS = 5 * 60_000;
const LOCK_WINDOW_MS = 60_000;
const ADDR_MAX_FAILURES = 5;
const GLOBAL_MAX_FAILURES = 30;
const CODE_RE = /^\d{6}$/;
const MAX_SID = 200;
/** At most one headset reply per agent in this time. */
const REPLY_RATE_MS = 5000;
/** Plan state of a session without events for this long is dropped (review of P1). */
const PLAN_IDLE_MS = 30 * 60_000;
/** How many ended sessions are remembered, so a late hook cannot bring their plan back. */
const MAX_ENDED = 256;

function safeEqual(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** Closes politely, then drops the socket if the peer never completes the handshake. */
function closeSoon(ws: WebSocket, code: number, reason: string): void {
  ws.close(code, reason);
  setTimeout(() => ws.terminate(), 1000).unref();
}

export async function startServer(opts: ServerOptions): Promise<ServerHandle> {
  if (typeof opts.pairingCode !== "string" || !CODE_RE.test(opts.pairingCode)) {
    throw new Error("pairing code must be exactly 6 digits");
  }
  const now = opts.now ?? Date.now;
  const homeDir = opts.homeDir ?? homedir();
  const demoRoot = opts.demoRoot ?? process.cwd();
  const pipeline = createPipeline({ collisionWindowMs: COLLISION_WINDOW_MS, budgetUsd: opts.budgetUsd });
  let paired: WebSocket | null = null;
  let pairingCode: string | null = opts.pairingCode;
  let codeExpiresAt = now() + CODE_TTL_MS;
  let sessionToken: string | null = null;
  let httpPort = 0;
  let wsPort = 0;
  let pairedOnce = false;
  const broker = new PermissionBroker({
    holdMs: opts.holdMs,
    hasClient: () => paired !== null && paired.readyState === WebSocket.OPEN,
  });

  const send = (ws: WebSocket, m: ServerMessage) => {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(m));
  };
  const broadcast = (m: ServerMessage) => {
    if (paired) send(paired, m);
  };

  const NO_EXPLAIN: Explanation = { text: "No explanation available.", replies: [], source: "rules" };
  const isPending = (id: string) => broker.pending().some((r) => r.id === id);

  /** Latest explanation per pending request, for clients that (re)connect while it is open. */
  const explains = new Map<string, Explanation>();

  /** Latest impact per pending request (impact.ts), for clients that (re)connect while it is open. */
  const impacts = new Map<string, Impact>();
  // The live file system: `lstat` and directory listings only, no process, no file contents (impact.ts).
  const liveImpactDeps = opts.impact === undefined ? realImpactDeps(homeDir) : null;
  const impactFn =
    opts.impact === false ? null : (opts.impact ?? ((req: ImpactRequest) => computeImpact(req, liveImpactDeps as ImpactDeps)));
  /** One computation in the whole bridge at a time, 8 waiting, 2 s of work per 10 s (review I5). */
  const impactScheduler = new ImpactScheduler(now);
  /** True while inject() opens a demo request: a recording is not this machine, so no impact is computed. */
  let injecting = false;

  /** Computes the impact after perm_open went out; sends it only if the request is still open. Never throws. */
  function scheduleImpact(request: PermRequest, ctx: OpenContext): void {
    if (!impactFn || request.risk.risk !== "destructive") return;
    const id = request.id;
    const req: ImpactRequest = { tool: ctx.tool, input: ctx.input, cwd: ctx.cwd, source: ctx.source, risk: request.risk };
    impactScheduler.enqueue(
      async (allowed) => {
        let impact: Impact | null = null;
        try {
          // Past the global budget: unknown, without touching the disk.
          impact = allowed ? await impactFn(req) : BUDGET_IMPACT;
        } catch {
          impact = null;
        }
        if (impact && isPending(id) && !impacts.has(id)) {
          impacts.set(id, impact);
          broadcast({ type: "perm_impact", id, impact });
        }
      },
      () => isPending(id),
    );
  }

  const offOpen = broker.onOpen((request, ctx) => {
    const source = pipeline.agent(request.sid)?.source;
    let explain = NO_EXPLAIN;
    try {
      explain = forSource(ruleExplanation(request), source);
    } catch {
      /* the explanation is display text only; the request must still open */
    }
    explains.set(request.id, explain);
    broadcast({ type: "perm_open", request, explain });
    // After perm_open is out, and off this call stack: the impact never delays the request.
    if (!injecting) setImmediate(() => scheduleImpact(request, ctx));
    const explainer = opts.explainer;
    if (!explainer) return;
    // Fire and forget: nothing here touches the broker, the risk or the hold timer.
    void explainer
      .explain(request, () => isPending(request.id))
      .then((ai) => {
        // A cached or late result can arrive after the request was decided: re-check right before sending.
        if (ai && isPending(request.id)) {
          const shown = forSource(ai, source);
          explains.set(request.id, shown);
          broadcast({ type: "perm_explain", id: request.id, explain: shown });
        }
      })
      .catch(() => {
        /* explain() never throws; belt and braces */
      });
  });
  const offClose = broker.onClose((id, reason) => {
    explains.delete(id);
    impacts.delete(id);
    broadcast({ type: "perm_closed", id, reason });
  });

  const details = new AgentDetails({
    agent: (sid) => pipeline.agent(sid),
    pendingExplain: (sid) => {
      // The newest pending request of this agent.
      const r = broker.pending().filter((x) => x.sid === sid).at(-1);
      return r ? (explains.get(r.id)?.text ?? null) : null;
    },
    explainer: opts.explainer,
    homeDir,
    env: process.env,
    now,
  });

  /**
   * With the explainer on, asks for a short title from the first prompt of a session (display only). The
   * prompt text goes only into the redacted, fenced AI prompt; it is not kept, logged or sent anywhere else.
   */
  function askTitle(sid: string, prompt: unknown): void {
    const explainer = opts.explainer;
    if (!explainer || !details.firstPrompt(sid)) return;
    if (typeof prompt !== "string" || prompt.trim() === "") return;
    void explainer
      .ask(titlePrompt(prompt, homeDir))
      .then((out) => {
        const title = sanitizeTitle(out);
        if (title) for (const m of pipeline.setTitle(sid, title)) broadcast(m);
      })
      .catch(() => {
        /* ask() never throws; belt and braces */
      });
  }

  /** Live feed for the paired headset: display text only, never recorded or logged. */
  const feed = new FeedHub({
    source: (sid) => pipeline.agent(sid)?.source,
    logFile: (sid) => {
      const source = pipeline.agent(sid)?.source;
      return source ? resolveTranscript({ sid, source, path: details.transcriptPath(sid), homeDir, env: process.env }) : null;
    },
    send: (m) => broadcast(m),
    homeDir,
    now,
    intervalMs: opts.feedIntervalMs,
  });

  /**
   * Live plans (plan.ts): tool calls from hook payloads (and, for Codex, from its session log) go through the
   * tracker; a change goes out as a `plan` event, at most one per agent per 500 ms, the last state always.
   */
  const plans = new PlanTracker(homeDir);
  const planGate = new PlanGate({
    now,
    setTimer: (fn, ms) => {
      const h = setTimeout(fn, ms);
      h.unref();
      return h;
    },
    clearTimer: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
    emit: (sid, plan) => ingest(plan ? { sid, t: now(), kind: "plan", plan } : { sid, t: now(), kind: "plan" }),
    intervalMs: opts.planIntervalMs,
  });
  /** Subagent hooks share the main agent's session id: their plan calls are ignored (plan.ts). */
  const subagents = new SubagentGuard();
  /** Sessions that ended recently (bounded): a late hook must not bring their plan or log watch back. */
  const endedSessions = new Set<string>();
  /** Last hook event per session that has plan state; 30 min without one drops that state. */
  const planSeen = new Map<string, number>();
  const planIdleMs = opts.planIdleMs ?? PLAN_IDLE_MS;
  /** Set at the top of close(): an /ev still in flight must not re-arm a plan timer or a log watch. */
  let closed = false;
  function planAllowed(sid: string): boolean {
    return !closed && !endedSessions.has(sid);
  }
  function dropPlanState(sid: string): void {
    plans.forget(sid);
    planGate.drop(sid);
    planLog.forget(sid);
    subagents.forget(sid);
    planSeen.delete(sid);
  }
  /** Drops the plan state of sessions without any event for planIdleMs. */
  function sweepIdlePlans(): void {
    const t = now();
    for (const [sid, at] of planSeen) if (t - at >= planIdleMs) dropPlanState(sid);
  }
  const planSweep = setInterval(sweepIdlePlans, Math.min(60_000, Math.max(10, planIdleMs)));
  planSweep.unref();
  /** Applies plan-tool calls of one agent in order and offers the resulting change, if any. */
  function notePlan(sid: string, calls: PlanToolCall[]): void {
    if (!planAllowed(sid)) return;
    planSeen.set(sid, now());
    let change: PlanChange | null = null;
    for (const call of calls) change = plans.observe(sid, call) ?? change;
    if (change) planGate.offer(sid, change.plan);
  }
  const planLog = new PlanLogWatcher({
    logFile: (sid) => resolveTranscript({ sid, source: "codex", path: details.transcriptPath(sid), homeDir, env: process.env }),
    onCalls: (sid, calls) => {
      if (pipeline.agent(sid) !== undefined) notePlan(sid, calls);
    },
    intervalMs: opts.planLogIntervalMs,
  });

  const replies = new ReplyHub({ waitMs: opts.replyWaitMs });
  /** Which sessions may listen, and with which cwd or transcript name. */
  const replySessions = new ReplySessions();
  /** Last delivered headset reply per agent (rate limit); only delivered replies count. */
  const replyAt = new Map<string, number>();

  /** Notes who a session is (cwd, transcript name) and that it is running, for the reply listeners. */
  function noteSession(e: AgentEvent, payload: unknown): void {
    const transcript = isObject(payload) ? transcriptName(payload.transcript_path) : undefined;
    replySessions.note(e.sid, { kind: e.kind, t: now(), cwd: e.cwd, transcript });
  }

  /** Shared by POST /ev, /perm, /status and inject(). */
  function ingest(e: AgentEvent): void {
    for (const m of pipeline.ingest(e)) broadcast(m);
    details.observe(e);
    if (haltSince !== null && e.kind !== "plan" && JSON.stringify(enforcement()) !== lastEnforcement) sendHaltState();
    if (e.kind !== "plan") {
      if (planSeen.has(e.sid)) planSeen.set(e.sid, now());
      sweepIdlePlans();
    }
    if (e.kind === "session_start") endedSessions.delete(e.sid);
    if (e.kind === "session_end") {
      dropPlanState(e.sid);
      endedSessions.add(e.sid);
      if (endedSessions.size > MAX_ENDED) endedSessions.delete(endedSessions.values().next().value as string);
      replies.release(e.sid);
      replySessions.forget(e.sid);
      replyAt.delete(e.sid);
    }
    try {
      opts.onEvent?.(e);
    } catch {
      /* an observer must not break ingest */
    }
  }

  // ---- halt-all (halt.ts) ----

  const log = opts.log ?? ((line: string) => console.log(line));
  /** Set while halted; the halt lives only in memory, so a bridge restart releases it. */
  let haltSince: number | null = null;
  let lastEnforcement = "";
  let haltReminder: ReturnType<typeof setInterval> | null = null;
  const HALT_DENY = permHaltReply();
  const marker = opts.haltMarker;
  // A starting bridge is not halted: any marker is stale or planted (review 2 M-1).
  if (marker !== undefined) removeHaltMarker(marker, "any");
  /** This process's start time, for the marker. */
  const processStarted = Date.now() - Math.round(process.uptime() * 1000);
  const headsetConnected = () => paired !== null && paired.readyState === WebSocket.OPEN;

  /** The sources that currently have sessions, with what the bridge can enforce for each. */
  function enforcement(): Record<string, Enforcement> {
    const out: Record<string, Enforcement> = {};
    for (const a of pipeline.snapshot().agents) if (!endedSessions.has(a.sid)) out[a.source] = ENFORCEMENT[a.source];
    return Object.fromEntries(Object.entries(out).sort(([a], [b]) => (a < b ? -1 : 1)));
  }
  function haltState(): HaltState {
    const enf = enforcement();
    return haltSince === null ? { on: false, enforcement: enf } : { on: true, since: haltSince, enforcement: enf };
  }
  function sendHaltState(): void {
    const s = haltState();
    lastEnforcement = JSON.stringify(s.enforcement);
    broadcast({ type: "halt_state", ...s });
  }
  const consoleTTY = opts.console?.isTTY === true;
  /** The console line on halt and every reminder: how to release it from here. */
  const haltLine = () =>
    consoleTTY
      ? "Halted. To go on: press Continue in the headset, or type continue here and press Enter."
      : "Halted. To go on: press Continue in the headset, or restart the bridge.";
  let consoleBuf = "";
  const onConsoleData = (chunk: Buffer | string) => {
    consoleBuf = (consoleBuf + chunk.toString()).slice(-4096);
    let nl: number;
    while ((nl = consoleBuf.indexOf("\n")) >= 0) {
      const line = consoleBuf.slice(0, nl).trim();
      consoleBuf = consoleBuf.slice(nl + 1);
      if (RELEASE_WORDS.has(line)) setHalt(false, "console");
    }
  };
  /**
   * Reads the console only while halted and only on a TTY (a pipe or service stdin could be fed by anyone, so it
   * never counts). A TTY keeps out an agent that can only write files or ask for tools; one that can run code as
   * the user can still inject console input (AttachConsole/WriteConsoleInput on Windows, TIOCSTI where enabled,
   * or a PTY it started the bridge in), or simply end the bridge, which releases the halt (review M5).
   */
  function listenConsole(on: boolean): void {
    const c = opts.console;
    if (!c || !c.isTTY) return;
    consoleBuf = "";
    if (on) {
      // Only what is typed after the halt counts: input buffered before it is dropped unread.
      const r = c.input as NodeJS.ReadableStream & { readableFlowing?: boolean | null };
      if (r.readableFlowing !== true) while (r.read() !== null) {
        /* drop type-ahead */
      }
      c.input.on("data", onConsoleData);
    } else c.input.removeListener("data", onConsoleData);
  }

  /** Halt on or off; false when nothing changed. A halt survives the headset disconnecting. */
  function setHalt(on: boolean, by: string): boolean {
    if (on === (haltSince !== null)) return false;
    if (on) {
      haltSince = now();
      if (marker !== undefined && !writeHaltMarker(marker, processStarted)) log(`${PRODUCT_NAME}: the halt marker could not be written.`);
      sendHaltState();
      broker.halt(HALT_MESSAGE);
      log(haltLine());
      haltReminder = setInterval(() => {
        if (haltSince !== null && !headsetConnected()) log(haltLine());
      }, opts.haltReminderMs ?? 60_000);
      haltReminder.unref();
      listenConsole(true);
    } else {
      haltSince = null;
      if (marker !== undefined) removeHaltMarker(marker, "mine");
      broker.resume();
      if (haltReminder) clearInterval(haltReminder);
      haltReminder = null;
      listenConsole(false);
      sendHaltState();
      log(`${PRODUCT_NAME}: halt released (${by}).`);
    }
    return true;
  }

  // ---- HTTP ----

  const reply = (res: ServerResponse, status: number, body: unknown) => {
    const text = JSON.stringify(body);
    res.writeHead(status, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(text) });
    res.end(text);
  };

  /**
   * Resolves the parsed body, or replies 400/413 itself and resolves undefined. With `halted` set and returning an
   * answer, that answer goes out with 200 instead of the 400/413 (a halted pre-tool hook must never get a reply
   * that lets the tool run).
   */
  function readJson(req: IncomingMessage, res: ServerResponse, halted?: () => object | null): Promise<unknown> {
    const fail = (status: number, body: object) => {
      const h = halted?.() ?? null;
      reply(res, h ? 200 : status, h ?? body);
    };
    return new Promise((resolve) => {
      const chunks: Buffer[] = [];
      let size = 0;
      let over = false;
      req.on("data", (c: Buffer) => {
        if (over) return;
        size += c.length;
        if (size > MAX_BODY) {
          over = true;
          chunks.length = 0;
          res.setHeader("Connection", "close");
          fail(413, { error: "too-large" });
          resolve(undefined);
          return;
        }
        chunks.push(c);
      });
      req.on("end", () => {
        if (over) return;
        try {
          resolve(JSON.parse(Buffer.concat(chunks).toString("utf8")));
        } catch {
          fail(400, { error: "bad-json" });
          resolve(undefined);
        }
      });
      req.on("error", () => resolve(undefined));
    });
  }

  async function handlePerm(payload: unknown, hook: { gone: boolean; reqId?: string }): Promise<unknown> {
    // Halted: every permission request is denied, even a malformed one (never "no decision").
    if (broker.isHalted()) return HALT_DENY;
    const e = normalizeHook(payload, now(), homeDir);
    if (hook.gone) return {};
    if (!e || e.kind !== "permission" || !isObject(payload) || typeof payload.tool_name !== "string") return {};
    const input = isObject(payload.tool_input) ? payload.tool_input : {};
    const cwd = typeof payload.cwd === "string" ? payload.cwd : "";
    ingest(e);
    details.notePayload(e.sid, payload);
    noteSession(e, payload);
    const { request, answer } = broker.open(e.sid, payload.tool_name, input, cwd, e.source);
    hook.reqId = request.id;
    return answer;
  }

  /** Gate for browser-reachable abuse: CSRF, DNS rebinding, missing token. Returns the rejecting status or 0. */
  function gate(req: IncomingMessage): number {
    if (req.headers.origin !== undefined) return 403;
    const host = req.headers.host;
    if (host !== `127.0.0.1:${httpPort}` && host !== `localhost:${httpPort}`) return 403;
    if (opts.ingestToken !== undefined) {
      const t = req.headers["x-lotse-token"];
      if (typeof t !== "string" || !safeEqual(t, opts.ingestToken)) return 401;
    }
    if (req.method === "POST" && !(req.headers["content-type"] ?? "").toLowerCase().startsWith("application/json")) return 415;
    return 0;
  }

  // ---- local status page (ui-page.ts) ----

  /** A fresh single-use code; the previous one stops working. Shared by newPairingCode() and POST /ui/code. */
  function issueCode(): string {
    pairingCode = String(randomInt(0, 1_000_000)).padStart(6, "0");
    codeExpiresAt = now() + CODE_TTL_MS;
    return pairingCode;
  }

  function uiState(view: UiView): UiState {
    const code = pairingCode !== null && now() < codeExpiresAt ? pairingCode : null;
    let qrSvg: string | null = null;
    if (code !== null && view.qrHost !== null) {
      try {
        qrSvg = renderQrSvg(encodeQr(pairingPayload(view.qrHost, wsPort, code)));
      } catch {
        qrSvg = null; // not an IPv4 host: no QR code, the address and the code still show
      }
    }
    return {
      version: view.version,
      paired: headsetConnected(),
      code,
      codeExpiresAt: code === null ? null : codeExpiresAt,
      wsPort,
      addresses: [...view.addresses],
      qrHost: view.qrHost,
      qrSvg,
      halted: haltSince !== null,
      pendingRequests: broker.pending().length,
      agents: pipeline.snapshot().agents.map((a) => ({ name: a.name, source: a.source, state: a.status })),
      explain: view.explain,
      tools: (view.tools ?? []).map((t) => ({ name: t.name, status: t.status })),
    };
  }

  /** The status page's session secret (set as a cookie by the one page load with the URL key). */
  const uiSession = randomBytes(32).toString("hex");
  let uiKeyUsed = false;
  /** Cookies are per host, not per port: the name carries the port, so two bridges never mix them up. */
  const uiCookieName = () => `marshal_ui_${httpPort}`;
  function uiCookieValid(req: IncomingMessage): boolean {
    const header = req.headers.cookie;
    if (typeof header !== "string") return false;
    for (const part of header.split(";")) {
      const eq = part.indexOf("=");
      if (eq > 0 && part.slice(0, eq).trim() === uiCookieName() && safeEqual(part.slice(eq + 1).trim(), uiSession)) return true;
    }
    return false;
  }

  /**
   * `/ui`, `/ui/state`, `/ui/code`. A wrong or missing key looks exactly like an unknown path (404). Then the Host must
   * be this bridge by name (DNS rebinding), an Origin, when sent, must be this page's own, and only the one method per
   * route is served. No CORS headers: another site can neither read these answers nor send the key.
   */
  function handleUi(req: IncomingMessage, res: ServerResponse, urlPath: string, ui: NonNullable<ServerOptions["ui"]>): void {
    // Two credentials. The key from the printed/opened URL works once, for loading the page: the browser was started
    // with it on its command line, where other local processes may read it. That load sets an HttpOnly cookie with a
    // second secret, which is what the page's own requests (and any reload in that browser) use from then on.
    const k = new URLSearchParams((req.url ?? "").split("?")[1] ?? "").get("k");
    const byCookie = uiCookieValid(req);
    const byKey = !byCookie && !uiKeyUsed && urlPath === "/ui" && req.method === "GET" && k !== null && safeEqual(k, ui.key);
    if (!byCookie && !byKey) return reply(res, 404, { error: "not-found" });
    const host = req.headers.host;
    if (host !== `127.0.0.1:${httpPort}` && host !== `localhost:${httpPort}`) return reply(res, 403, { error: "rejected" });
    const origin = req.headers.origin;
    if (origin !== undefined && origin !== `http://127.0.0.1:${httpPort}` && origin !== `http://localhost:${httpPort}`) {
      return reply(res, 403, { error: "rejected" });
    }
    const method = urlPath === "/ui/code" ? "POST" : "GET";
    if (req.method !== method) {
      res.writeHead(405, { ...UI_HEADERS, Allow: method, "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "method-not-allowed" }));
      return;
    }
    if (urlPath === "/ui") {
      const cookie: Record<string, string> = {};
      if (byKey) {
        uiKeyUsed = true;
        cookie["Set-Cookie"] = `${uiCookieName()}=${uiSession}; Path=/ui; HttpOnly; SameSite=Strict`;
      }
      res.writeHead(200, { ...UI_HEADERS, ...cookie, "Content-Type": "text/html; charset=utf-8", "Content-Length": Buffer.byteLength(UI_PAGE) });
      res.end(UI_PAGE);
      return;
    }
    if (urlPath === "/ui/code") {
      const code = issueCode();
      try {
        opts.onCodeReplaced?.(code);
      } catch {
        /* an observer must not break the page */
      }
    }
    const body = JSON.stringify(uiState(ui.view()));
    res.writeHead(200, { ...UI_HEADERS, "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
    res.end(body);
  }

  const httpServer: Server = createServer(async (req, res) => {
    const urlPath = (req.url ?? "").split("?")[0];
    try {
      if (opts.ui !== undefined && UI_PATHS.has(urlPath)) {
        // The body of /ui/code is never read; the key is the only credential (no X-Lotse-Token in a browser).
        req.resume();
        handleUi(req, res, urlPath, opts.ui);
        return;
      }
      const isReply = urlPath === "/reply" && req.method === "GET";
      if (!isReply && (req.method !== "POST" || (urlPath !== "/ev" && urlPath !== "/perm" && urlPath !== "/status"))) {
        req.resume();
        reply(res, 404, { error: "not-found" });
        return;
      }
      const rejected = gate(req);
      if (rejected) {
        req.resume();
        reply(res, rejected, { error: "rejected" });
        return;
      }
      if (isReply) {
        req.resume();
        handleReplyPoll(req, res);
        return;
      }
      // Not halted: a marker that exists anyway (planted, or another bridge's) must not refuse a slow hook later.
      if (marker !== undefined && !broker.isHalted()) removeHaltMarker(marker, "any");
      if (urlPath === "/perm" && broker.isHalted()) {
        // Fast and independent of any headset: the deny goes out before the body is even read.
        req.resume();
        return reply(res, 200, HALT_DENY);
      }
      if (urlPath === "/ev" && broker.isHalted() && req.headers[PRE_TOOL_HEADER.toLowerCase()] === PRE_TOOL_HEADER_VALUE) {
        // A pre-tool hook says so in a header: refused from the headers alone, whatever the body (review I3).
        req.resume();
        return reply(res, 200, preToolHaltReply());
      }
      // Armed before the body is read: a hook that vanishes mid-request must not leave a stale prompt.
      const hook: { gone: boolean; reqId?: string } = { gone: false };
      res.on("close", () => {
        hook.gone = true;
        if (hook.reqId !== undefined) broker.cancel(hook.reqId, "hook-gone");
      });
      // Halted and the body cannot be read (too large, not JSON): an /ev still gets the pre-tool refusal.
      const payload = await readJson(req, res, () => (urlPath === "/ev" && broker.isHalted() ? preToolHaltReply() : null));
      if (payload === undefined) return; // 400/413 already sent (valid JSON is never undefined)

      if (urlPath === "/ev") {
        const e = normalizeHook(payload, now(), homeDir);
        if (e?.kind === "tool_start" && broker.isHalted()) {
          // Halted: the pre-tool hook is refused (Claude's PreToolUse format; the shim maps it per tool).
          reply(res, 200, preToolHaltReply());
          return;
        }
        if (e) {
          ingest(e);
          details.notePayload(e.sid, payload);
          if (e.kind !== "session_end") {
            noteSession(e, payload);
            const fromSubagent = subagents.note(e.sid, e.kind, payload);
            const call = planCallFromHook(e.kind, payload);
            if (call && !fromSubagent) notePlan(e.sid, [call]);
            // Codex hooks may not carry update_plan: its session log is read as well.
            if (pipeline.agent(e.sid)?.source === "codex" && planAllowed(e.sid)) {
              planSeen.set(e.sid, now());
              planLog.watch(e.sid);
            }
          }
        }
        if (e?.kind === "prompt") askTitle(e.sid, (payload as Record<string, unknown>).prompt);
        reply(res, 200, {});
      } else if (urlPath === "/status") {
        const e = normalizeStatus(payload, now());
        if (e) ingest(e);
        reply(res, 200, {});
      } else {
        reply(res, 200, await handlePerm(payload, hook));
      }
    } catch {
      if (!res.headersSent) reply(res, 200, !broker.isHalted() ? {} : urlPath === "/perm" ? HALT_DENY : urlPath === "/ev" ? preToolHaltReply() : {});
      else res.end();
    }
  });

  /**
   * `GET /reply?sid=&cwd=` (or `&transcript=`): the long poll of a Claude Stop hook. Answers `{text}` when the
   * headset replies, else `{}` (timeout, a newer listener of the same hook, session end, shutdown).
   * - 403 when the bridge has no ingest token (only tests run without one).
   * - 404 for a session that has not been running in the last 2 h (a session start, prompt or tool event).
   * - 409 when cwd or transcript name is not the session's, or another hook already listens for it.
   * - 429 when 32 listeners already wait.
   * A listener whose connection closes is withdrawn.
   */
  function handleReplyPoll(req: IncomingMessage, res: ServerResponse): void {
    if (opts.ingestToken === undefined) {
      reply(res, 403, { error: "no-token" });
      return;
    }
    const q = new URLSearchParams((req.url ?? "").split("?")[1] ?? "");
    const sid = q.get("sid");
    const who = { cwd: q.get("cwd") ?? undefined, transcript: q.get("transcript") ?? undefined };
    if (!sid || sid.length > MAX_SID || (!who.cwd && !who.transcript)) {
      reply(res, 400, { error: "bad-request" });
      return;
    }
    const check = replySessions.check(sid, who, now());
    if (check !== "ok") {
      reply(res, check === "unknown" ? 404 : 409, { error: check === "unknown" ? "unknown-session" : "not-this-session" });
      return;
    }
    /** Writes the answer; true only when it was written without error. */
    const write = (text: string | null): Promise<boolean> =>
      new Promise((resolve) => {
        if (res.destroyed || res.writableEnded) return resolve(false);
        const body = JSON.stringify(text === null ? {} : { text });
        res.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
        res.end(body, (err?: Error | null) => resolve(!err && res.writableFinished));
        res.once("error", () => resolve(false));
      });
    const key = who.cwd ? `cwd:${who.cwd}` : `transcript:${who.transcript}`;
    const r = replies.listen(sid, key, write);
    if (r !== "ok") {
      reply(res, r === "full" ? 429 : 409, { error: r === "full" ? "too-many-listeners" : "not-this-session" });
      return;
    }
    res.on("close", () => replies.cancel(sid, write));
    try {
      opts.onReplyListen?.(sid);
    } catch {
      /* an observer must not break the listener */
    }
  }

  /**
   * The headset's `agent_reply`: delivered only to a waiting Claude listener of an agent that is done or
   * waiting, and acked `delivered:true` only once the listener's answer was written. Only delivered replies
   * count towards the 5 s rate limit.
   */
  async function handleAgentReply(ws: WebSocket, sid: string, text: string): Promise<void> {
    const agent = pipeline.agent(sid);
    const last = replyAt.get(sid);
    if (last !== undefined && now() - last < REPLY_RATE_MS) {
      send(ws, { type: "error", error: "rate-limited" });
      return;
    }
    const open = agent !== undefined && agent.source === "claude" && (agent.status === "done" || agent.status === "waiting" || replies.waiting(sid));
    const delivered = open && (await replies.deliver(sid, text));
    if (delivered) replyAt.set(sid, now());
    send(ws, { type: "agent_reply_ack", sid, delivered });
    // The recording notes only that a reply was delivered, never its text.
    if (delivered) ingest({ sid, t: now(), kind: "reply" });
  }

  function inject(e: AgentEvent): void {
    ingest(e);
    if (e.kind !== "permission") return;
    const { tool, input, cwd } = demoPermission(e, demoRoot);
    // Demo mode has no waiting hook: the answer is discarded.
    injecting = true;
    try {
      void broker.open(e.sid, tool, input, cwd, e.source).answer;
    } finally {
      injecting = false;
    }
  }

  // ---- WebSocket pairing state ----

  const addrFailures = new Map<string, number[]>();
  const addrLockedUntil = new Map<string, number>();
  const globalFailures: number[] = [];

  function addrLocked(addr: string, t: number): boolean {
    const until = addrLockedUntil.get(addr);
    if (until === undefined) return false;
    if (t >= until) {
      addrLockedUntil.delete(addr);
      return false;
    }
    return true;
  }

  function recordFailure(addr: string, t: number): void {
    const recent = (addrFailures.get(addr) ?? []).filter((x) => t - x < LOCK_WINDOW_MS);
    recent.push(t);
    if (recent.length >= ADDR_MAX_FAILURES) {
      addrLockedUntil.set(addr, t + LOCK_WINDOW_MS);
      addrFailures.delete(addr);
    } else {
      addrFailures.set(addr, recent);
    }
    while (globalFailures.length > 0 && t - globalFailures[0] >= LOCK_WINDOW_MS) globalFailures.shift();
    globalFailures.push(t);
    if (globalFailures.length >= GLOBAL_MAX_FAILURES) {
      // Too many guesses overall: the code is burnt; only newPairingCode() restores pairing.
      pairingCode = null;
      globalFailures.length = 0;
    }
    // Keep the per-address tables from growing without bound.
    for (const [a, list] of addrFailures) if (list.every((x) => t - x >= LOCK_WINDOW_MS)) addrFailures.delete(a);
    for (const [a, until] of addrLockedUntil) if (t >= until) addrLockedUntil.delete(a);
  }

  const wss = new WebSocketServer({
    host: WS_HOST,
    port: opts.wsPort,
    maxPayload: MAX_WS_MESSAGE,
    verifyClient: (info, cb) => {
      // Browsers always send Origin; the headset app does not.
      if (info.req.headers.origin !== undefined) return cb(false, 403, "Forbidden");
      const addr = info.req.socket.remoteAddress ?? "unknown";
      const open: WebSocket[] = []; // unpaired, still-open sockets, oldest first
      for (const c of wss.clients) if (c !== paired && c.readyState === WebSocket.OPEN) open.push(c);
      if (open.filter((c) => addrOf.get(c) === addr).length >= MAX_UNPAIRED_PER_ADDR) {
        return cb(false, 503, "Service Unavailable");
      }
      // At the cap, make room by evicting the oldest idle socket: a flood must not lock the headset out.
      if (open.length >= MAX_UNPAIRED) closeSoon(open[0], 1013, "evicted");
      cb(true);
    },
  });
  const addrOf = new WeakMap<WebSocket, string>();
  const helloTimers = new WeakMap<WebSocket, ReturnType<typeof setTimeout>>();
  const alive = new WeakMap<WebSocket, boolean>();

  wss.on("connection", (ws, req) => {
    const addr = req.socket.remoteAddress ?? "unknown";
    addrOf.set(ws, addr);
    alive.set(ws, true);
    ws.on("pong", () => alive.set(ws, true));
    ws.on("error", () => {
      /* a broken socket must not crash the bridge */
    });
    helloTimers.set(
      ws,
      setTimeout(() => {
        if (ws !== paired) closeSoon(ws, 4008, "hello-timeout");
      }, opts.helloTimeoutMs ?? 10_000),
    );

    const accept = (viaCode: boolean) => {
      const timer = helloTimers.get(ws);
      if (timer) clearTimeout(timer);
      const old = paired;
      // Feed subscriptions belong to the socket that made them.
      if (old !== ws) feed.clear();
      paired = ws;
      if (old && old !== ws) closeSoon(old, 4000, "replaced");
      const pending = broker.pending();
      const snapExplains: Record<string, Explanation> = {};
      for (const r of pending) {
        const e = explains.get(r.id);
        if (e) snapExplains[r.id] = e;
      }
      const snapImpacts: Record<string, Impact> = {};
      for (const r of pending) {
        const i = impacts.get(r.id);
        if (i) snapImpacts[r.id] = i;
      }
      send(ws, { ...pipeline.snapshot(), pending, explains: snapExplains, impacts: snapImpacts, halt: haltState() });
      if (viaCode && sessionToken) send(ws, { type: "paired", session: sessionToken });
      if (!pairedOnce) {
        pairedOnce = true;
        try {
          opts.onPaired?.();
        } catch {
          /* an observer must not break pairing */
        }
      }
    };

    const reject = (t: number) => {
      recordFailure(addr, t);
      closeSoon(ws, 4001, "bad-credentials");
    };

    ws.on("message", (data, isBinary) => {
      // An evicted, rejected or replaced socket is already closing: its frames must not pair or decide.
      if (ws.readyState !== WebSocket.OPEN) return;
      try {
        const msg = isBinary ? null : parseClientMessage(data.toString());
        if (!msg) {
          send(ws, { type: "error", error: "bad-message" });
          return;
        }

        if (msg.type === "hello") {
          const t = now();
          if (msg.session !== undefined) {
            // A valid session is always accepted, even during an attack on the code.
            if (sessionToken !== null && safeEqual(msg.session, sessionToken)) return accept(false);
            if (addrLocked(addr, t)) return closeSoon(ws, 4029, "rate-limited");
            return reject(t);
          }
          if (addrLocked(addr, t)) return closeSoon(ws, 4029, "rate-limited");
          if (pairingCode === null || t >= codeExpiresAt || !safeEqual(msg.code ?? "", pairingCode)) return reject(t);
          pairingCode = null; // single-use
          sessionToken = randomBytes(32).toString("base64url");
          try {
            opts.onCodeUsed?.(); // before the accept, whose onPaired may print
          } catch {
            /* an observer must not break pairing */
          }
          return accept(true);
        }

        if (ws !== paired) {
          send(ws, { type: "error", error: "not-paired" });
          return;
        }
        if (msg.type === "ping") {
          send(ws, { type: "pong" });
          return;
        }
        if (msg.type === "agent_reply") {
          void handleAgentReply(ws, msg.sid, msg.text).catch(() => send(ws, { type: "error", error: "bad-message" }));
          return;
        }
        if (msg.type === "feed_subscribe") {
          const r = feed.subscribe(msg.sid);
          if (r !== "ok") send(ws, { type: "error", error: r });
          return;
        }
        if (msg.type === "feed_unsubscribe") {
          feed.unsubscribe(msg.sid);
          return;
        }
        if (msg.type === "agent_detail_request") {
          const answer = details.request(msg.sid);
          if ("error" in answer) {
            send(ws, { type: "error", error: answer.error });
            return;
          }
          send(ws, answer.now);
          // The AI version only goes to the socket that asked, and only while it is still the paired one.
          void answer.later
            ?.then((m) => {
              if (m && ws === paired) send(ws, m);
            })
            .catch(() => {
              /* later never rejects; belt and braces */
            });
          return;
        }
        if (msg.type === "halt") {
          // On a change every client hears it from setHalt; an unchanged state is repeated to the asker.
          if (!setHalt(msg.on, "headset")) send(ws, { type: "halt_state", ...haltState() });
          return;
        }
        const r = broker.decide(msg.decision);
        if (!r.ok) send(ws, { type: "error", error: r.error });
      } catch {
        send(ws, { type: "error", error: "bad-message" });
      }
    });

    ws.on("close", () => {
      const timer = helloTimers.get(ws);
      if (timer) clearTimeout(timer);
      if (ws !== paired) return;
      paired = null;
      feed.clear();
      broker.closeAll("client-disconnected");
    });
  });

  const listening = await Promise.allSettled([
    new Promise<void>((resolve, reject) => {
      httpServer.once("error", reject);
      httpServer.listen(opts.httpPort, HTTP_HOST, () => resolve());
    }),
    new Promise<void>((resolve, reject) => {
      wss.once("error", reject);
      wss.once("listening", () => resolve());
    }),
  ]);

  /** Stops both servers; tolerant of one never having started. */
  const stopServers = async (): Promise<void> => {
    // Let answered-with-{} hooks flush; force-drop whatever is still open afterwards.
    const force = setTimeout(() => httpServer.closeAllConnections(), 250);
    await Promise.all([
      new Promise<void>((resolve) => wss.close(() => resolve())),
      new Promise<void>((resolve) => {
        httpServer.close(() => resolve());
        httpServer.closeIdleConnections();
      }),
    ]);
    clearTimeout(force);
  };

  const failed = listening.find((r): r is PromiseRejectedResult => r.status === "rejected");
  if (failed) {
    offOpen();
    offClose();
    await stopServers();
    throw failed.reason;
  }
  httpPort = (httpServer.address() as AddressInfo).port;
  wsPort = (wss.address() as AddressInfo).port;
  // After listening, a late server error is logged in one line and never crashes the bridge.
  const logError = (what: string) => (err: Error) => {
    console.error(`lotse: ${what} error: ${String(err?.message ?? err).split(/\r?\n/)[0]}`);
  };
  httpServer.on("error", logError("HTTP server"));
  wss.on("error", logError("WebSocket server"));

  const heartbeat = setInterval(() => {
    for (const c of wss.clients) {
      if (alive.get(c) === false) {
        c.terminate(); // fires close -> pending requests answered {}
        continue;
      }
      alive.set(c, false);
      try {
        c.ping();
      } catch {
        c.terminate();
      }
    }
  }, opts.heartbeatMs ?? 10_000);
  heartbeat.unref();

  return {
    httpPort,
    wsPort,
    httpHost: HTTP_HOST,
    wsHost: WS_HOST,
    inject,
    newPairingCode: issueCode,
    async close() {
      closed = true;
      if (haltReminder) clearInterval(haltReminder);
      if (marker !== undefined) removeHaltMarker(marker, "mine");
      listenConsole(false);
      clearInterval(heartbeat);
      clearInterval(planSweep);
      offOpen();
      offClose();
      broker.closeAll("shutdown");
      replies.closeAll();
      feed.clear();
      planGate.close();
      planLog.clear();
      explains.clear();
      impacts.clear();
      paired = null;
      for (const c of wss.clients) c.terminate();
      await stopServers();
    },
  };
}
