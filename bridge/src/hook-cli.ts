// `lotse hook [--source <tool>] [--event <name>]`: the process every coding-agent hook starts.
//
// It must never block a tool and never print an allow the headset did not give. It loads only light modules
// up front; the shim (adapters, classifier) is imported while stdin is being read, and the bridge server is
// never loaded. Every failure prints nothing and exits 0, except for a Muse approval: there an empty stdout
// might count as allow, so every path that ends without a headset decision prints MUSE_NO_DECISION_DENY.
//
// - A run is a Muse run when MUSE_SESSION_ID is set (Muse clears the hook environment to an allowlist that
//   keeps it), whatever `--source` says.
// - Approval hooks (`--event` naming the tool's approval event, or a payload that looks like one) wait up to
//   10 s for stdin; observe-only events keep the 500 ms budget, counted from process start.

import { request } from "node:http";
import type { ShimSource } from "./adapters.js";
import { parseArgs } from "./cli-args.js";
import { APPROVAL_TEXT, MUSE_NO_DECISION_DENY, PRE_TOOL_TEXT, isApprovalEvent } from "./hook-fallback.js";
import { PRE_TOOL_EVENT, haltRefusal } from "./halt.js";
import type { HookKind } from "./halt.js";
import { hookToken } from "./token-file.js";
import { PRODUCT_NAME } from "./product.js";
import { haltMarkerLive, haltMarkerPath } from "./halt-marker.js";
import { homedir } from "node:os";
import { cleanReply, transcriptName } from "./replies.js";

export const DEFAULT_HTTP_PORT = 47820;
/** Everything an observe-only hook or the statusline does (stdin read plus POST) shares this deadline. */
export const DEADLINE_MS = 500;
/** How long an approval hook waits for its stdin (a loaded machine can be slow to hand it over). */
export const APPROVAL_STDIN_MS = 10_000;
const SOURCES = new Set(["claude", "muse", "codex", "gemini", "opencode"]);
/**
 * How long a Stop listener waits for a reply, from process start. Claude Code enforces the hook's timeout
 * (900 s) on asyncRewake hooks, so the listener ends a little earlier, on its own and silently.
 */
export const LISTEN_MS = 880_000;

/** How long a Muse approval waits for the headset by default (the bridge holds Muse requests 18 s, broker.ts). */
export const MUSE_PERM_WAIT_MS = 20_000;

/** The Muse approval wait: LOTSE_MUSE_PERM_WAIT_MS (1 ms to 100 s) overrides the 20 s default. */
export function musePermWaitMs(env: NodeJS.ProcessEnv = process.env): number {
  const v = Number(env.LOTSE_MUSE_PERM_WAIT_MS);
  return env.LOTSE_MUSE_PERM_WAIT_MS && Number.isFinite(v) && v > 0 && v <= 100_000 ? v : MUSE_PERM_WAIT_MS;
}

/** The listen budget; LOTSE_LISTEN_MS can shorten it (tests), never lengthen it. */
export function listenMs(env: NodeJS.ProcessEnv = process.env): number {
  const v = Number(env.LOTSE_LISTEN_MS);
  return Number.isInteger(v) && v > 0 && v < LISTEN_MS ? v : LISTEN_MS;
}
/** The line Claude receives as a system reminder (contract 2026-11-12-contract-agent-reply). */
export const REPLY_PREFIX = `Message from the user via the ${PRODUCT_NAME} headset: `;

/** The bridge port; LOTSE_HTTP_PORT overrides it (tests, or a second bridge). */
export function httpPort(env: NodeJS.ProcessEnv = process.env): number {
  const p = Number(env.LOTSE_HTTP_PORT);
  return Number.isInteger(p) && p > 0 && p < 65536 ? p : DEFAULT_HTTP_PORT;
}

/** Wall-clock time at which this process started, so the budgets include node startup. */
export function processStart(): number {
  return Date.now() - Math.round(process.uptime() * 1000);
}

/**
 * Reads stdin until it ends or the deadline passes, and returns what arrived. `longer`, when given, is a
 * later deadline that applies once the text so far satisfies `extend` (an approval payload).
 */
export function readStdin(deadline: number, longer?: { deadline: number; extend: (text: string) => boolean }): Promise<string> {
  return new Promise((res) => {
    let s = "";
    let timer: ReturnType<typeof setTimeout>;
    const finish = () => {
      clearTimeout(timer);
      res(s);
    };
    const arm = (at: number, onFire: () => void) => {
      clearTimeout(timer);
      timer = setTimeout(onFire, Math.max(0, at - Date.now()));
    };
    arm(deadline, () => {
      if (longer && longer.extend(s)) arm(longer.deadline, finish);
      else finish();
    });
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (s += c));
    process.stdin.on("end", finish);
    process.stdin.on("error", finish);
  });
}

function postJson(path: string, body: string, token: string | null, ms: number): Promise<void> {
  return new Promise((res) => {
    const headers: Record<string, string | number> = { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) };
    if (token) headers["X-Lotse-Token"] = token;
    const req = request({ host: "127.0.0.1", port: httpPort(), path, method: "POST", headers, timeout: ms }, (r) => {
      r.resume();
      r.on("end", () => res());
      r.on("error", () => res());
    });
    req.on("timeout", () => req.destroy());
    req.on("error", () => res());
    req.end(body);
  });
}

/** Sends to the bridge, giving up at the shared deadline. */
export async function sendBounded(path: string, body: string, deadline: number): Promise<void> {
  const left = deadline - Date.now();
  if (left <= 0) return;
  await Promise.race([postJson(path, body, hookToken(), left), new Promise<void>((r) => setTimeout(r, left))]);
}

/** `--name=value` becomes `--name value` (an empty value stays an empty string). */
export function splitEqualsFlags(args: string[]): string[] {
  return args.flatMap((a) => {
    const m = /^(--[A-Za-z][\w-]*)=(.*)$/s.exec(a);
    return m ? [m[1], m[2]] : [a];
  });
}

/** `--name` in (split) raw argv, even when the rest does not parse: whether it is there, and its value. */
function rawFlag(args: string[], name: string): { present: boolean; value: string | undefined } {
  const i = args.indexOf(`--${name}`);
  if (i < 0) return { present: false, value: undefined };
  const v = args[i + 1];
  return { present: true, value: v === undefined || v.startsWith("--") || v === "" ? undefined : v };
}

/** Prints `out` (if any) and exits 0 once it is written. */
function finish(out: string): void {
  if (out === "") process.exit(0);
  process.stdout.on("error", () => process.exit(0));
  process.stdout.write(out + "\n", () => process.exit(0));
}

/**
 * Long-polls the bridge for a reply to this session. A reply goes to stderr with REPLY_PREFIX and the process
 * exits 2, which wakes Claude (asyncRewake). Anything else exits 0 silently. Never prints to stdout.
 */
async function listen(raw: string, start: number, env: NodeJS.ProcessEnv): Promise<void> {
  let payload: Record<string, unknown> = {};
  try {
    const v: unknown = JSON.parse(raw);
    if (typeof v === "object" && v !== null && !Array.isArray(v)) payload = v as Record<string, unknown>;
  } catch {
    /* not JSON: no session to listen for */
  }
  const sid = payload.session_id;
  if (typeof sid !== "string" || sid === "" || sid.length > 200) return finish("");
  // The bridge hands a reply only to the hook of that very session: it checks the cwd or transcript name.
  const who = { cwd: typeof payload.cwd === "string" ? payload.cwd : undefined, transcript: transcriptName(payload.transcript_path) };
  const { getReply } = await import("./hook-shim.js");
  const text = await getReply(httpPort(env), sid, hookToken(env), start + listenMs(env) - Date.now(), who);
  const clean = text === null ? "" : cleanReply(text);
  if (clean === "") return finish("");
  process.stderr.on("error", () => process.exit(0));
  process.stderr.write(REPLY_PREFIX + clean + "\n", () => process.exit(2));
}

/**
 * What the hook is, from its arguments alone (fix round 2 C): the tool's pre-tool event, its approval event, or
 * anything else; undefined without `--event` (hooks installed before it existed: then read from stdin).
 */
export function hookKind(source: string, event: string | undefined): HookKind | undefined {
  if (event === undefined) return undefined;
  if (event === PRE_TOOL_EVENT[source] || (source === "muse" && event === "preToolUse")) return "pre";
  return isApprovalEvent(source, event) ? "perm" : "other";
}

/** A live halt marker (halt-marker.ts). Only asked on a path that would otherwise end without a decision. */
const haltLive = () => haltMarkerLive(haltMarkerPath(homedir()));

export async function runHook(rawArgs: string[], env: NodeJS.ProcessEnv = process.env): Promise<void> {
  const args = splitEqualsFlags(rawArgs);
  const museEnv = typeof env.MUSE_SESSION_ID === "string" && env.MUSE_SESSION_ID !== "";
  let source = museEnv ? "muse" : (rawFlag(args, "source").value ?? "claude");
  const eventFlag = rawFlag(args, "event");
  let event = eventFlag.value;
  // Fail closed for Muse as soon as anything looks like its approval hook, even before the args parse:
  // the approval event, or an `--event` whose value is missing.
  let museApproval =
    source === "muse" && (isApprovalEvent("muse", event) || args.includes("PermissionRequest") || (eventFlag.present && event === undefined));
  // A Muse hook installed before `--event` existed: it may be the approval hook, so empty stdin counts as one.
  const museLegacy = source === "muse" && !eventFlag.present;
  let raw = "";
  try {
    const { pos, flags } = parseArgs(args, ["source", "event"], ["listen"]);
    if (pos.length > 0) throw new Error("unexpected argument");
    if (!museEnv && typeof flags.source === "string") source = flags.source;
    event = typeof flags.event === "string" ? flags.event : undefined;
    if (!SOURCES.has(source)) throw new Error("unknown source");
    const approval = isApprovalEvent(source, event);
    museApproval ||= source === "muse" && approval;

    const start = processStart();
    const longer = { deadline: start + APPROVAL_STDIN_MS, extend: (text: string) => APPROVAL_TEXT.test(text) || (museLegacy && text === "") };
    // Start reading at once; the shim loads meanwhile.
    const stdin = approval ? readStdin(start + APPROVAL_STDIN_MS) : readStdin(start + DEADLINE_MS, longer);
    const [{ runShim, postForReply, PERM_WAIT_MS }, text] = await Promise.all([import("./hook-shim.js"), stdin]);
    raw = text;
    // Legacy Muse: nothing arrived within 10 s (or stdin was empty) is an approval without a decision.
    if (museLegacy && raw.trim() === "") museApproval = true;
    const port = httpPort(env);
    const post = (path: string, body: string, ms: number, headers?: Record<string, string>) => postForReply(port, path, body, hookToken(env), ms, headers);
    const evMs = start + DEADLINE_MS - Date.now();
    const permMs = source === "muse" ? musePermWaitMs(env) : PERM_WAIT_MS;
    // Asked only on a path without a decision and without an answer from the bridge (hook-shim.ts).
    const out = await runShim(source as ShimSource, raw, post, { evMs, permMs, approval, haltMarker: haltLive, kind: hookKind(source, event) });
    // Claude's Stop hook (async, asyncRewake) then waits for a reply from the headset.
    if (flags.listen === true && source === "claude" && event === "Stop" && out === "") return await listen(raw, start, env);
    finish(out === "" && museApproval ? MUSE_NO_DECISION_DENY : out);
  } catch {
    // Every silent exit of a pre-tool or permission hook passes the marker check (fix round 2 C).
    const kind = hookKind(source, eventFlag.value) ?? (PRE_TOOL_TEXT.test(raw) ? "pre" : APPROVAL_TEXT.test(raw) ? "perm" : "other");
    if (kind !== "other" && haltLive()) return finish(haltRefusal(source, kind));
    finish(museApproval || (source === "muse" && APPROVAL_TEXT.test(raw)) ? MUSE_NO_DECISION_DENY : "");
  }
}
