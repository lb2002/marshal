// Permission broker: holds a Claude Code PermissionRequest until the paired
// headset answers, then yields the exact hook JSON. Fail-safe: anything other
// than an explicit, correctly-gestured allow/deny yields {} (no decision), so
// Claude Code falls back to its own terminal dialog.

import { randomUUID } from "node:crypto";
import { argvMinLength, isStringArray, MAX_RENDER, renderArgv, renderArgvHead, shownText } from "./argv.js";
import { classifyRisk } from "./risk.js";
import type { RiskResult } from "./risk.js";
import type { AgentSource } from "./events.js";

export interface PermRequest {
  id: string;
  sid: string;
  tool: string;
  /** The command as shown: a string as it is, an argument array rendered by argv.ts (each element one word). */
  command?: string;
  /**
   * Full length of the shown command before truncation. For an argument array above MAX_RENDER characters only its
   * head is rendered, and this is a lower bound (every element plus one separator); it is truncated anyway.
   */
  commandLength?: number;
  /** Present (true) when the command came as an argument array; older headsets ignore it. */
  argv?: true;
  /** True when command (or url) was cut for display; allow then needs two-hand. */
  truncated: boolean;
  path?: string;
  /** Every path-like input field the classifier looks at (max 5). */
  paths: string[];
  url?: string;
  risk: RiskResult;
  createdAt: number;
  expiresAt: number;
}

export interface ClientDecision {
  id: string;
  behavior: "allow" | "deny";
  message?: string;
  confirm?: "pinch-hold" | "two-hand";
}

export type HookAnswer =
  | { hookSpecificOutput: { hookEventName: "PermissionRequest"; decision: { behavior: "allow" | "deny"; message?: string } } }
  | Record<string, never>;

/** What a request was opened with (the full input, before display truncation), for listeners such as impact.ts. */
export interface OpenContext {
  tool: string;
  input: Record<string, unknown>;
  cwd: string;
  source?: AgentSource;
}

export type DecideResult = { ok: true } | { ok: false; error: "unknown" | "needs-two-hand" | "needs-pinch-hold" | "halted" };

const MAX_COMMAND = 300;
const MAX_URL = 300;
const MAX_PATHS = 5;
const MAX_PATH = 300;
const MAX_TOOL = 40;
const MAX_MESSAGE = 500;
// Same fields the classifier inspects for paths.
const PATH_FIELDS = ["file_path", "notebook_path", "path", "destination", "dest", "target", "output", "to", "outputPath"];

interface Entry {
  request: PermRequest;
  /** Risk class captured as a primitive; the gate never reads the (shared) request object. */
  riskClass: string;
  resolve: (a: HookAnswer) => void;
  timer: ReturnType<typeof setTimeout>;
}

function answer(behavior: "allow" | "deny", message?: string): HookAnswer {
  const decision: { behavior: "allow" | "deny"; message?: string } = { behavior };
  if (behavior === "deny") {
    const clean = sanitizeMessage(message);
    if (clean) decision.message = clean;
  }
  return { hookSpecificOutput: { hookEventName: "PermissionRequest", decision } };
}

/** Strip control chars (keep newline and tab), trim, cap length; undefined if empty. */
function sanitizeMessage(m: unknown): string | undefined {
  if (typeof m !== "string") return undefined;
  const clean = m.replace(/[\u0000-\u0008\u000B-\u001F\u007F-\u009F]/g, "").trim().slice(0, MAX_MESSAGE).trim();
  return clean === "" ? undefined : clean;
}

function str(v: unknown): string | undefined {
  return typeof v === "string" ? v : undefined;
}

/**
 * Display of a `command` field: a string with invisible and control characters as visible escapes (argv.ts
 * visibleText); an argument array rendered so that every element stays one word (never joined by spaces:
 * `["rm","a b"]` deletes one path; its rendering already escapes them). Shown text cut to MAX_COMMAND after
 * escaping, length of the whole escaped text.
 */
function commandDisplay(v: unknown): { text: string; length: number; argv: boolean } | undefined {
  if (typeof v === "string") return { ...shownText(v, MAX_COMMAND), argv: false };
  if (!isStringArray(v)) return undefined;
  const min = argvMinLength(v);
  if (min > MAX_RENDER) return { text: renderArgvHead(v, MAX_COMMAND), length: min, argv: true };
  const full = renderArgv(v);
  return { text: full.slice(0, MAX_COMMAND), length: full.length, argv: true };
}

function subscribe<T>(list: T[], cb: T): () => void {
  list.push(cb);
  return () => {
    const i = list.indexOf(cb);
    if (i >= 0) list.splice(i, 1);
  };
}

/**
 * The display fields of a request exactly as the broker builds them (truncation, paths, frozen copies).
 * Shared by the live broker and the offline demo pack; `risk` comes from classifyRisk.
 */
export function buildPermRequest(p: {
  id: string;
  sid: string;
  tool: string;
  input: Record<string, unknown>;
  risk: RiskResult;
  createdAt: number;
  holdMs: number;
}): PermRequest {
  const input: Record<string, unknown> = p.input && typeof p.input === "object" ? p.input : {};
  const shown = commandDisplay(input.command);
  // Every display field escaped first, then cut: a field that grows past its cap by escaping counts as cut.
  const rawUrl = str(input.url);
  const url = rawUrl === undefined ? undefined : shownText(rawUrl, MAX_URL);
  const shownPaths = PATH_FIELDS.map((f) => str(input[f])).filter((x): x is string => x !== undefined).slice(0, MAX_PATHS).map((x) => shownText(x, MAX_PATH));
  // A path cut for display hides part of the target, so it needs two-hand like a cut command.
  const pathCut = shownPaths.some((x) => x.length > MAX_PATH);
  const paths = shownPaths.map((x) => x.text);
  const tool = shownText(p.tool, MAX_TOOL);
  const toolCut = tool.length > MAX_TOOL;
  return Object.freeze({
    id: p.id,
    sid: p.sid,
    tool: tool.text,
    command: shown?.text,
    commandLength: shown?.length,
    ...(shown?.argv ? { argv: true as const } : {}),
    truncated: (shown?.length ?? 0) > MAX_COMMAND || (url?.length ?? 0) > MAX_URL || pathCut || toolCut,
    path: paths[0],
    paths: Object.freeze(paths) as string[],
    url: url?.text,
    // Copy + freeze: classifier results are shared module constants.
    risk: Object.freeze({ ...p.risk }),
    createdAt: p.createdAt,
    expiresAt: p.createdAt + p.holdMs,
  });
}

/**
 * Muse kills a hook after a timeout nobody has measured yet (plugin hooks cannot declare one). Until a live probe
 * says more, the bridge holds a Muse request at most 18 s and the Muse shim waits 20 s (hook-cli.ts), so the
 * headset's answer or the bridge's no-decision always arrives before the shim gives up.
 */
export const MUSE_HOLD_MS = 18_000;

export class PermissionBroker {
  private readonly holdMs: number;
  private readonly now: () => number;
  private readonly hasClient: () => boolean;
  private readonly entries = new Map<string, Entry>();
  private readonly openCbs: Array<(r: PermRequest, ctx: OpenContext) => void> = [];
  private readonly closeCbs: Array<(id: string, reason: string) => void> = [];
  /** Set while halted (halt-all): the deny message every request gets. */
  private haltMessage: string | null = null;

  constructor(opts: { holdMs: number; now?: () => number; hasClient: () => boolean }) {
    this.holdMs = opts.holdMs;
    this.now = opts.now ?? Date.now;
    this.hasClient = opts.hasClient;
  }

  open(
    sid: string,
    tool: string,
    input: Record<string, unknown>,
    cwd: string,
    source?: AgentSource,
  ): { request: PermRequest; answer: Promise<HookAnswer> } {
    const safeInput: Record<string, unknown> = input && typeof input === "object" ? input : {};
    const createdAt = this.now();
    const risk = classifyRisk(tool, safeInput, cwd, source);
    const holdMs = source === "muse" ? Math.min(this.holdMs, MUSE_HOLD_MS) : this.holdMs;
    const request = buildPermRequest({ id: randomUUID(), sid, tool, input: safeInput, risk, createdAt, holdMs });

    // Halted: denied at once, never opened, whether or not a headset is connected (a stop stays a stop).
    if (this.haltMessage !== null) return { request, answer: Promise.resolve(answer("deny", this.haltMessage)) };
    if (!this.hasClient()) return { request, answer: Promise.resolve({}) };

    const promise = new Promise<HookAnswer>((resolve) => {
      const timer = setTimeout(() => this.finish(request.id, {}, "timeout"), holdMs);
      this.entries.set(request.id, { request, riskClass: request.risk.risk, resolve, timer });
    });
    const ctx: OpenContext = Object.freeze({ tool, input: safeInput, cwd, source });
    for (const cb of [...this.openCbs]) {
      try { cb(request, ctx); } catch { /* a listener must not break the broker */ }
    }
    return { request, answer: promise };
  }

  decide(d: ClientDecision): DecideResult {
    // While halted nothing may be allowed, whatever the id (the open requests were already denied).
    if (this.haltMessage !== null && d?.behavior !== "deny") return { ok: false, error: "halted" };
    const entry = typeof d?.id === "string" ? this.entries.get(d.id) : undefined;
    if (!entry) return { ok: false, error: "unknown" };

    if (d.behavior === "deny") {
      this.finish(d.id, answer("deny", d.message), "deny");
      return { ok: true };
    }
    if (d.behavior !== "allow") return { ok: false, error: "unknown" };

    // Allow only with the gesture the risk class demands; request stays pending otherwise.
    // Fail closed: only exactly safe/network on a fully shown request may use pinch-hold.
    const lowRisk = (entry.riskClass === "safe" || entry.riskClass === "network") && !entry.request.truncated;
    if (!lowRisk) {
      if (d.confirm !== "two-hand") return { ok: false, error: "needs-two-hand" };
    } else if (d.confirm !== "pinch-hold" && d.confirm !== "two-hand") {
      return { ok: false, error: "needs-pinch-hold" };
    }
    this.finish(d.id, answer("allow"), "allow");
    return { ok: true };
  }

  /** Halt-all: every open request is denied with `message` (reason "halt"); new ones get the same deny at once. */
  halt(message: string): void {
    this.haltMessage = message;
    for (const id of [...this.entries.keys()]) this.finish(id, answer("deny", message), "halt");
  }

  /** Ends a halt: requests open and decide as before. */
  resume(): void {
    this.haltMessage = null;
  }

  isHalted(): boolean {
    return this.haltMessage !== null;
  }

  closeAll(reason: string): void {
    for (const id of [...this.entries.keys()]) this.finish(id, {}, reason);
  }

  /** Withdraws one request (answer {}); false when it is not pending. */
  cancel(id: string, reason: string): boolean {
    if (!this.entries.has(id)) return false;
    this.finish(id, {}, reason);
    return true;
  }

  pending(): PermRequest[] {
    return [...this.entries.values()].map((e) => e.request);
  }

  onOpen(cb: (r: PermRequest, ctx: OpenContext) => void): () => void {
    return subscribe(this.openCbs, cb);
  }

  onClose(cb: (id: string, reason: string) => void): () => void {
    return subscribe(this.closeCbs, cb);
  }

  /** Resolves a request exactly once; later calls find no entry. */
  private finish(id: string, a: HookAnswer, reason: string): void {
    const entry = this.entries.get(id);
    if (!entry) return;
    this.entries.delete(id);
    clearTimeout(entry.timer);
    entry.resolve(a);
    for (const cb of [...this.closeCbs]) {
      try { cb(id, reason); } catch { /* a listener must not break the broker */ }
    }
  }
}
