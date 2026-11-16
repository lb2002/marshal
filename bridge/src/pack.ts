// Offline demo pack: an anonymised recording turned into the exact server message stream the
// Quest app would receive live, with timestamps, plus what happens on allow / deny. The Quest app
// plays it without a PC.
//
// - Messages come from the same pipeline the live server uses; permission events become `perm_open`
//   built like broker.open (same truncation and paths), risk from classifyRisk with the same
//   `<repo>`/`<abs>` mapping as the live demo, so the pack never holds the directory used for classification.
// - `request.command`, `.path` and `.paths` keep the recording's `<repo>/` placeholder by design. The
//   player strips `<repo>/` for display. The explanation text already shows the project-relative path.
// - Every agent message carries `agent.source` (claude, muse, codex or gemini) from the recording's
//   events; a recorded event without `source` is Claude, as live.
// - Ids are deterministic (`p1`, `p2`, … in recording order), createdAt is the event time and
//   expiresAt createdAt + 90 s.
// - No `perm_closed` is emitted: the Quest player synthesises the close when the juror decides
//   (or the hold expires).
// - The recording's own wait is not replayed: from a `perm_open` until that agent's next tool_start,
//   tool_end, stop, prompt or permission event, its agent messages that still show it `blocked` /
//   "Waiting for approval" are left out (they would undo the juror's decision). `waitingMsTotal` in
//   every agent message leaves out the recorded permission waits (time spent `blocked`); the player
//   adds the juror's own waits (step 3 below).
// - `request.command` is shown with secrets redacted (`redactSecrets`). A pack that still contains
//   `C:\`, `C:/`, `/Users/`, `/home/`, `sk-` or `ghp_` anywhere is refused with an error.
// - The explanation is the rule text (`source: "rules"`). An explainer's result (`"ai"`) takes its
//   place exactly as a live `perm_explain` would: for a safe request the AI text replaces the rule text,
//   otherwise the rule text stays and the AI sentence is `aiText`. A hand-written override replaces
//   both (`"manual"`). There is no separate `perm_explain`.
//
// Player contract for `perms[id].onAllow` / `.onDeny`, a `Consequence` `{ sid, status, lastAction }`:
//   1. merge only `status` and `lastAction` into the player's current agent state for `sid`;
//   2. set that agent's `since` to the decision time;
//   3. add `decision time - request.createdAt` to its own running sum of juror waits for `sid`;
//   4. then resume the timeline.
// Displayed waiting time of an agent = `waitingMsTotal` of its latest pack agent message + that sum
// of juror waits for its sid (the pack value never contains the recorded permission waits).
// Default consequence: status `working`, lastAction `Allowed: <tool> <target>` or `Denied: …`.
//
// Agent details (optional, from the overrides file's `agents` key): top-level `agents: { [sid]: { title,
// summary, recent[], waitingFor } }`, hand-written static text with the live caps (title 40, summary 280,
// recent at most 5 of 80, waitingFor 140 or null). The player answers `agent_detail_request` from it, offline,
// with `source: "rules"`. Every agent message of such a sid carries `agent.title` = that title.
//
// Replies (optional, the overrides file's `replies` key): top-level `replies: { [sid]: string }`, one canned
// agent reaction (one line, at most 80 characters) to any reply the juror sends from the headset. The player
// answers `agent_reply` with `agent_reply_ack delivered:true` and shows that text as the agent's reaction.

import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { buildPermRequest } from "./broker.js";
import type { PermRequest } from "./broker.js";
import { redactSecrets } from "./anonymize.js";
import { demoPermission } from "./demo-paths.js";
import type { AgentEvent } from "./events.js";
import { cleanText, forSource, ruleExplanation } from "./explain.js";
import type { Explainer, Explanation } from "./explain.js";
import { COLLISION_WINDOW_MS, createPipeline } from "./pipeline.js";
import type { ServerMessage } from "./protocol.js";
import type { Impact, ImpactKind, Recoverable } from "./impact.js";
import { classifyRisk } from "./risk.js";
import { shortPath } from "./state.js";
import type { AgentStatus } from "./state.js";

export interface DemoPack {
  version: 1;
  budgetUsd: number;
  durationMs: number;
  messages: { t: number; msg: ServerMessage }[];
  perms: Record<string, { onAllow: Consequence; onDeny: Consequence }>;
  /** Static agent details, keyed by sid; left out when the overrides have none. */
  agents?: PackAgents;
  /** Canned agent reaction to a reply, keyed by sid; left out when the overrides have none. */
  replies?: PackReplies;
}

export type PackReplies = Record<string, string>;

/** What the player answers to `agent_detail_request` for one agent (same caps as the live `agent_detail`). */
export interface PackAgentDetail {
  title: string;
  summary: string;
  recent: string[];
  waitingFor: string | null;
}

export type PackAgents = Record<string, PackAgentDetail>;

/** A patch for one agent, applied by the player when the juror decides (see the player contract above). */
export interface Consequence {
  sid: string;
  status: AgentStatus;
  lastAction: string;
}

export interface PackOverride {
  onAllow?: string;
  onDeny?: string;
  explain?: { text: string; replies: string[] };
  /** Hand-written impact: emitted as `perm_impact` right after the request's `perm_open` (same caps as live). */
  impact?: Impact;
}

/** Keyed by request id (`p<n>`). */
export type PackOverrides = Record<string, PackOverride>;

export interface PackOptions {
  budgetUsd: number;
  /** Directory `<repo>` maps to for classification only; it never appears in the pack. */
  demoRoot: string;
  explainer?: Explainer;
  overrides?: PackOverrides;
  /** Static agent details (see the header); every sid must be in the recording. */
  agents?: PackAgents;
  /** Canned reactions to a reply (see the header); every sid must be in the recording. */
  replies?: PackReplies;
  /** Called after each request's explanation is settled (done, total). */
  onProgress?: (done: number, total: number) => void;
}

/** Same hold as the live bridge (the CLI starts the server with 90 s). */
const HOLD_MS = 90_000;
/** Same cap as AgentState.lastAction in state.ts. */
const MAX_ACTION = 80;
const MAX_TEXT = 140;
const MAX_REPLY = 70;
const MAX_REPLIES = 3;
const MAX_TITLE = 40;
const MAX_SUMMARY = 280;
const MAX_RECENT = 5;
const MAX_WAITING = 140;
const ID_RE = /^p[1-9]\d*$/;
const SID_RE = /^[A-Za-z0-9_.:-]{1,200}$/;
/** The overrides file's key for agent details; every other key is a request id. */
const AGENTS_KEY = "agents";
/** The overrides file's key for canned reply reactions. */
const REPLIES_KEY = "replies";
/** Strings that must never be in a pack: personal paths and token prefixes. */
const FORBIDDEN = ["C:\\", "C:/", "/Users/", "/home/", "sk-", "ghp_"];
/** Events after which the agent has moved on from a permission request in the recording. */
const ACTIVITY = new Set<AgentEvent["kind"]>(["tool_start", "tool_end", "stop", "prompt", "permission"]);

const singleLine = (s: string) => s.replace(/\s+/g, " ").trim();
const clip = (s: string, max: number) => {
  const chars = [...s];
  return chars.length > max ? chars.slice(0, max - 1).join("") + "…" : s;
};

/** `<tool> <target>`: the command (secrets redacted), else the short path, else the url. */
function actionLabel(req: PermRequest): string {
  const target = req.command !== undefined ? singleLine(redactSecrets(req.command)) : req.path !== undefined ? shortPath(req.path) : (req.url ?? "");
  return singleLine(`${req.tool} ${target}`);
}

function consequence(sid: string, lastAction: string): Consequence {
  return { sid, status: "working", lastAction: clip(lastAction, MAX_ACTION) };
}

const relative = (p: string): string => (p === "<repo>" ? "." : p.startsWith("<repo>/") ? p.slice("<repo>/".length) : p);

/** The request as the explanation sees it: project-relative paths instead of `<repo>/…`. */
function explainView(req: PermRequest): PermRequest {
  return { ...req, path: req.path === undefined ? undefined : relative(req.path), paths: req.paths.map(relative) };
}

export async function buildPack(events: AgentEvent[], opts: PackOptions): Promise<DemoPack> {
  const overrides = opts.overrides ?? {};
  const agentDetails = opts.agents ?? {};
  const strangers = Object.keys(agentDetails).filter((sid) => !events.some((e) => e.sid === sid));
  if (strangers.length > 0) throw new Error(`agents: details for sessions that are not in the recording: ${strangers.join(", ")}`);
  const replyTexts = opts.replies ?? {};
  const noSession = Object.keys(replyTexts).filter((sid) => !events.some((e) => e.sid === sid));
  if (noSession.length > 0) throw new Error(`replies: for sessions that are not in the recording: ${noSession.join(", ")}`);
  const pipeline = createPipeline({ budgetUsd: opts.budgetUsd, collisionWindowMs: COLLISION_WINDOW_MS });
  const ordered = events.map((e, i) => ({ e, i })).sort((a, b) => a.e.t - b.e.t || a.i - b.i).map((x) => x.e);
  const messages: { t: number; msg: ServerMessage }[] = [{ t: 0, msg: pipeline.snapshot() }];
  const opened: { request: PermRequest; rules: Explanation; slot: { t: number; msg: ServerMessage } }[] = [];
  /** Agents between a perm_open and their next activity: the juror, not the recording, ends that wait. */
  const awaitingJuror = new Set<string>();
  /** Per sid: the previous recorded state, and the recorded permission waits so far. */
  const previous = new Map<string, { status: AgentStatus; waitingMsTotal: number }>();
  const recordedWait = new Map<string, number>();

  for (const e of ordered) {
    if (ACTIVITY.has(e.kind)) awaitingJuror.delete(e.sid);
    for (const msg of pipeline.ingest(e)) {
      if (msg.type === "agent") {
        const a = msg.agent; // a copy, safe to change
        const prev = previous.get(a.sid);
        // Only a permission makes an agent "blocked": what leaving it adds is a recorded permission wait.
        if (prev?.status === "blocked") recordedWait.set(a.sid, (recordedWait.get(a.sid) ?? 0) + a.waitingMsTotal - prev.waitingMsTotal);
        previous.set(a.sid, { status: a.status, waitingMsTotal: a.waitingMsTotal });
        if (awaitingJuror.has(a.sid) && (a.status === "blocked" || a.lastAction.startsWith("Waiting for approval"))) continue;
        a.waitingMsTotal -= recordedWait.get(a.sid) ?? 0;
        if (Object.hasOwn(agentDetails, a.sid)) a.title = agentDetails[a.sid].title;
      }
      messages.push({ t: e.t, msg });
    }
    if (e.kind !== "permission") continue;
    // Classify against demoRoot like the live demo; show the recording's placeholders.
    const live = demoPermission(e, opts.demoRoot);
    const display: Record<string, unknown> = {};
    if (e.command !== undefined) display.command = redactSecrets(e.command);
    if (e.path !== undefined) display.file_path = e.path;
    const request = buildPermRequest({
      id: `p${opened.length + 1}`,
      sid: e.sid,
      tool: live.tool,
      input: display,
      risk: classifyRisk(live.tool, live.input, live.cwd, e.source),
      createdAt: e.t,
      holdMs: HOLD_MS,
    });
    const rules = forSource(ruleExplanation(explainView(request)), e.source);
    const slot: { t: number; msg: ServerMessage } = { t: e.t, msg: { type: "perm_open", request, explain: rules } };
    messages.push(slot);
    opened.push({ request, rules, slot });
    awaitingJuror.add(e.sid);
  }

  const unknown = Object.keys(overrides).filter((id) => !opened.some((o) => o.request.id === id));
  if (unknown.length > 0) throw new Error(`overrides for requests that are not in the pack: ${unknown.join(", ")}`);

  /** The coding agent of each session (first event that names one; absent means Claude). */
  const sourceOf = new Map<string, AgentEvent["source"]>();
  for (const e of ordered) if (!sourceOf.has(e.sid)) sourceOf.set(e.sid, e.source);
  const perms: DemoPack["perms"] = {};
  for (const [i, { request, rules, slot }] of opened.entries()) {
    const o: PackOverride = Object.hasOwn(overrides, request.id) ? overrides[request.id] : {};
    let explain = rules;
    if (o.explain) {
      explain = { text: o.explain.text, replies: [...o.explain.replies], source: "manual" };
    } else if (opts.explainer) {
      // One at a time; nothing closes offline, so the request counts as open throughout.
      explain = (await opts.explainer.explain(explainView(request), () => true)) ?? explain;
    }
    slot.msg = { type: "perm_open", request, explain: forSource(explain, sourceOf.get(request.sid)) };
    if (o.impact) messages.splice(messages.indexOf(slot) + 1, 0, { t: slot.t, msg: { type: "perm_impact", id: request.id, impact: { ...o.impact } } });
    const label = actionLabel(request);
    perms[request.id] = {
      onAllow: consequence(request.sid, o.onAllow ?? `Allowed: ${label}`),
      onDeny: consequence(request.sid, o.onDeny ?? `Denied: ${label}`),
    };
    opts.onProgress?.(i + 1, opened.length);
  }

  messages.sort((a, b) => a.t - b.t); // stable: same-t messages keep their send order
  const pack: DemoPack = {
    version: 1,
    budgetUsd: opts.budgetUsd,
    durationMs: Math.max(0, ordered.length > 0 ? ordered[ordered.length - 1].t : 0),
    messages,
    perms,
  };
  if (Object.keys(agentDetails).length > 0) pack.agents = agentDetails;
  if (Object.keys(replyTexts).length > 0) pack.replies = replyTexts;
  const json = JSON.stringify(pack);
  for (const leak of new Set([opts.demoRoot, opts.demoRoot.replace(/\\/g, "/"), JSON.stringify(opts.demoRoot).slice(1, -1)])) {
    if (leak !== "" && json.includes(leak)) throw new Error("pack would contain the demo root directory");
  }
  refuseLeaks(pack);
  return pack;
}

/** Throws, naming the marker and where it is, when any string in the pack holds a forbidden one. */
function refuseLeaks(pack: DemoPack): void {
  const parts: [string, unknown][] = [
    ...pack.messages.map((m, i): [string, unknown] => [`messages[${i}] (${m.msg.type})`, m.msg]),
    ...Object.entries(pack.perms).map(([id, p]): [string, unknown] => [`perms.${id}`, p]),
    ...Object.entries(pack.agents ?? {}).map(([sid, d]): [string, unknown] => [`agents.${sid}`, d]),
    ...Object.entries(pack.replies ?? {}).map(([sid, r]): [string, unknown] => [`replies.${sid}`, r]),
  ];
  for (const [where, value] of parts) {
    const strings: string[] = [];
    JSON.stringify(value, (_k, v: unknown) => {
      if (typeof v === "string") strings.push(v);
      return v;
    });
    const marker = FORBIDDEN.find((m) => strings.some((s) => s.includes(m)));
    if (marker !== undefined) {
      throw new Error(`pack would contain ${JSON.stringify(marker)} (a personal path or a secret) in ${where}; not writing it. Anonymise the recording first.`);
    }
  }
}

// ---------------------------------------------------------------- overrides file

const fail = (where: string, why: string): never => {
  throw new Error(`overrides: ${where}: ${why}`);
};

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function onlyKeys(o: Record<string, unknown>, keys: string[], where: string): void {
  for (const k of Object.keys(o)) if (!keys.includes(k)) fail(where, `unknown field ${JSON.stringify(k)}`);
}

/**
 * True when `s` is already what the AI sanitiser would produce: one line, no control, bidi or
 * zero-width characters, nothing to cut. Hand-written text gets the same rules as AI text.
 */
function isClean(s: string, max: number): boolean {
  if (s.trim() === "" || [...s].length > max) return false;
  return cleanText(s, max) === s;
}

function text(v: unknown, max: number, where: string): string {
  if (typeof v !== "string") return fail(where, "must be a string");
  if (!isClean(v, max)) return fail(where, `must be one non-empty line of at most ${max} characters without control or invisible characters`);
  return v;
}

/** Reads and validates an overrides file (request overrides and agent details); every error names the file. */
export function readOverridesFile(file: string): { overrides: PackOverrides; agents: PackAgents; replies: PackReplies } {
  const full = resolve(file);
  try {
    const raw: unknown = JSON.parse(readFileSync(full, "utf8"));
    return { overrides: parseOverrides(raw), agents: parsePackAgents(raw), replies: parsePackReplies(raw) };
  } catch (err) {
    throw new Error(`${full}: ${(err as Error).message}`);
  }
}

/** Validates a hand-written overrides file (parsed JSON). Throws with the offending field. */
export function parseOverrides(raw: unknown): PackOverrides {
  if (!isPlainObject(raw)) return fail("file", "must be a JSON object keyed by request id (p1, p2, …)");
  const out: PackOverrides = {};
  for (const [id, v] of Object.entries(raw)) {
    if (id === AGENTS_KEY || id === REPLIES_KEY) continue; // parsePackAgents, parsePackReplies
    if (!ID_RE.test(id)) fail(id, "key must be a request id like p1");
    if (!isPlainObject(v)) fail(id, "must be an object");
    const entry = v as Record<string, unknown>;
    onlyKeys(entry, ["onAllow", "onDeny", "explain", "impact"], id);
    const o: PackOverride = {};
    if (entry.onAllow !== undefined) o.onAllow = text(entry.onAllow, MAX_ACTION, `${id}.onAllow`);
    if (entry.onDeny !== undefined) o.onDeny = text(entry.onDeny, MAX_ACTION, `${id}.onDeny`);
    if (entry.explain !== undefined) {
      const ex = entry.explain;
      if (!isPlainObject(ex)) return fail(`${id}.explain`, "must be an object");
      onlyKeys(ex, ["text", "replies"], `${id}.explain`);
      if (!Array.isArray(ex.replies) || ex.replies.length < 1 || ex.replies.length > MAX_REPLIES) {
        fail(`${id}.explain.replies`, `must be 1 to ${MAX_REPLIES} strings`);
      }
      o.explain = {
        text: text(ex.text, MAX_TEXT, `${id}.explain.text`),
        replies: (ex.replies as unknown[]).map((r, i) => text(r, MAX_REPLY, `${id}.explain.replies[${i}]`)),
      };
    }
    if (entry.impact !== undefined) o.impact = parseImpact(entry.impact, `${id}.impact`);
    out[id] = o;
  }
  return out;
}

const IMPACT_KINDS: readonly ImpactKind[] = ["delete", "overwrite", "force_push", "reset", "clean", "other"];
/** Only a sure loss or nothing known, as live (impact.ts): never a reassurance. */
const RECOVERABLE: readonly Recoverable[] = ["no", "unknown"];

/** A hand-written impact with the live caps (target and reason one clean line of at most 120 characters). */
function parseImpact(v: unknown, where: string): Impact {
  if (!isPlainObject(v)) return fail(where, "must be an object");
  onlyKeys(v, ["kind", "target", "files", "filesCapped", "bytes", "outsideProject", "recoverable", "reason"], where);
  if (typeof v.kind !== "string" || !(IMPACT_KINDS as readonly string[]).includes(v.kind)) fail(`${where}.kind`, `must be one of ${IMPACT_KINDS.join(", ")}`);
  if (typeof v.recoverable !== "string" || !(RECOVERABLE as readonly string[]).includes(v.recoverable)) fail(`${where}.recoverable`, `must be one of ${RECOVERABLE.join(", ")}`);
  const out: Impact = { kind: v.kind as Impact["kind"], recoverable: v.recoverable as Impact["recoverable"], reason: "" };
  if (v.target !== undefined) out.target = text(v.target, 120, `${where}.target`);
  for (const k of ["files", "bytes"] as const) {
    const n = v[k];
    if (n === undefined) continue;
    if (typeof n !== "number" || !Number.isSafeInteger(n) || n < 0) fail(`${where}.${k}`, "must be a whole number of at least 0");
    out[k] = n as number;
  }
  for (const k of ["filesCapped", "outsideProject"] as const) {
    if (v[k] === undefined) continue;
    if (typeof v[k] !== "boolean") fail(`${where}.${k}`, "must be true or false");
    out[k] = v[k] as boolean;
  }
  out.reason = text(v.reason, 120, `${where}.reason`);
  // Same key order as the live impact.
  const ordered: Impact = { kind: out.kind } as Impact;
  for (const k of ["target", "files", "filesCapped", "bytes", "outsideProject", "recoverable", "reason"] as const) if (out[k] !== undefined) (ordered as unknown as Record<string, unknown>)[k] = out[k];
  return ordered;
}

/** Validates the overrides file's `agents` key (parsed JSON): hand-written text, same rules as AI text. */
export function parsePackAgents(raw: unknown): PackAgents {
  if (!isPlainObject(raw) || raw[AGENTS_KEY] === undefined) return {};
  const agents = raw[AGENTS_KEY];
  if (!isPlainObject(agents)) return fail(AGENTS_KEY, "must be an object keyed by session id");
  const out: PackAgents = {};
  for (const [sid, v] of Object.entries(agents)) {
    const where = `${AGENTS_KEY}.${sid}`;
    if (!SID_RE.test(sid)) fail(where, "key must be a session id");
    if (!isPlainObject(v)) fail(where, "must be an object");
    const d = v as Record<string, unknown>;
    onlyKeys(d, ["title", "summary", "recent", "waitingFor"], where);
    if (!Array.isArray(d.recent) || d.recent.length > MAX_RECENT) fail(`${where}.recent`, `must be 0 to ${MAX_RECENT} strings`);
    if (!("waitingFor" in d)) fail(`${where}.waitingFor`, "must be a string or null");
    out[sid] = {
      title: text(d.title, MAX_TITLE, `${where}.title`),
      summary: text(d.summary, MAX_SUMMARY, `${where}.summary`),
      recent: (d.recent as unknown[]).map((r, i) => text(r, MAX_ACTION, `${where}.recent[${i}]`)),
      waitingFor: d.waitingFor === null ? null : text(d.waitingFor, MAX_WAITING, `${where}.waitingFor`),
    };
  }
  return out;
}

/** Validates the overrides file's `replies` key: one clean line of at most 80 characters per session. */
export function parsePackReplies(raw: unknown): PackReplies {
  if (!isPlainObject(raw) || raw[REPLIES_KEY] === undefined) return {};
  const replies = raw[REPLIES_KEY];
  if (!isPlainObject(replies)) return fail(REPLIES_KEY, "must be an object keyed by session id");
  const out: PackReplies = {};
  for (const [sid, v] of Object.entries(replies)) {
    const where = `${REPLIES_KEY}.${sid}`;
    if (!SID_RE.test(sid)) fail(where, "key must be a session id");
    out[sid] = text(v, MAX_ACTION, where);
  }
  return out;
}
