// Wire protocol between the bridge and the headset (WebSocket, JSON text frames).

import type { AgentState } from "./state.js";
import type { Incident } from "./detectors.js";
import type { PermRequest, ClientDecision } from "./broker.js";
import type { Explanation } from "./explain.js";
import { MAX_REPLY_TEXT, cleanReply } from "./replies.js";
import type { FeedEntry } from "./feed.js";
import type { Impact, Recoverable } from "./impact.js";
import type { Enforcement } from "./halt.js";

/**
 * Halt-all state (halt.ts): `since` (ms, bridge clock) while on. `enforcement` lists the sources that currently have
 * sessions: "every-step" (each tool call is refused), "requests-only" (only permission requests), "none".
 */
export interface HaltState {
  on: boolean;
  since?: number;
  enforcement: Record<string, Enforcement>;
}

export type { ClientDecision, Explanation, FeedEntry, Impact };

// Compile-time guard (ruling after the security review): an impact only states a sure loss or nothing known.
// The build fails if a reassuring value is ever added to the type.
type Reassuring = "yes" | "partly";
const onlySureLosses: [Extract<Recoverable, Reassuring>] extends [never] ? true : never = true;
void onlySureLosses;

export type ServerMessage =
  // `explains` maps each pending request id to its latest explanation (AI if it already arrived, else rules).
  // `impacts` maps each pending request id to its impact, when one is known (live server only; a demo pack
  // carries its impacts as `perm_impact` messages).
  | { type: "snapshot"; agents: AgentState[]; pending: PermRequest[]; explains: Record<string, Explanation>; impacts?: Record<string, Impact>; halt?: HaltState }
  // `agent.source` names the coding agent behind a session: "claude" (Claude Code), "muse" (Meta Muse),
  // "codex" (OpenAI Codex CLI), "gemini" (Google Gemini CLI) or "opencode" (OpenCode). It is fixed by the session's
  // first event. `agent.name` follows it: "Agent N" for Claude, "Muse N", "Codex N", "Gemini N", "OpenCode N"
  // (counted per source).
  // `agent.title` (optional, at most 40 characters): what the agent works on. First the rule title from its
  // first prompt (first sentence, redacted, absolute paths shortened); with the AI on, a later `agent`
  // message may replace it with the AI's title (at most 6 words). `agent.project` (optional, at most 30
  // characters): the basename of the session cwd. Both are untrusted display text and never affect risk,
  // gestures or decisions.
  // `agent.plan` (optional): the agent's live to-do list, `{steps: [{text, status: "pending"|"in_progress"|"done"}]
  // (at most 12), more (steps cut beyond 12), updatedAt (ms, bridge clock)}`. From Claude Code TodoWrite and
  // TaskCreate/TaskUpdate/TaskList, Codex update_plan (hooks or its session log), Gemini write_todos. Absent while no
  // plan is known (never an empty list) and after the session ended. A plan change sends an `agent` message, at
  // most one per agent per 500 ms; the last state always goes out. Step text is untrusted display text (redacted,
  // absolute paths shortened, one line, at most 80 characters); a headset must accept `agent` without the field.
  // A Gemini request (`perm_open`) can only be denied from the headset: an allow sends no answer to
  // Gemini, which then shows its own confirmation in the terminal.
  | { type: "agent"; agent: AgentState }
  | { type: "incident"; incident: Incident }
  // `explain` is the rule-based explanation, always present, sent with the request (it is a sibling of
  // `request`, not part of it). Display text only: it never changes risk, gesture or timer.
  // `explain.source` is "rules" or "ai" live; an offline demo pack (pack.ts) may also carry "manual",
  // a hand-written text for a scripted request. All three are display text with the same caps
  // (`aiText` has the same 140-character cap as `text`).
  // `request.command` is the command as it will run. A command that came as an argument array is rendered so that
  // every element stays one word (plain when it is only letters, digits and `_ - . / : = + , @ %`, otherwise in
  // single quotes, or as `$'...'` with `\n`-style escapes when it holds control or invisible characters), and the
  // request then carries `argv: true` (optional; absent for a string command; older headsets ignore it).
  | { type: "perm_open"; request: PermRequest; explain: Explanation }
  // Optional AI explanation for the same id, sent later and only while the request is still pending;
  // the client replaces its `explain` with it. Never sent for a truncated request (`request.truncated`).
  // - risk "safe": `text` is the AI text.
  // - any other risk: `text` is still the rule text, unchanged. The AI's own sentence is in the optional
  //   `aiText`, display only, shown below `text` and labelled "KI". It is left out when it plays the risk down.
  // `replies` come from the AI (the rule replies when the AI's play the risk down). A reply may only ever
  // be sent back as the `message` of a deny.
  | { type: "perm_explain"; id: string; explain: Explanation }
  // What a destructive request would hit (impact.ts, contract 2026-11-12-contract-impact-halt.md): sent after
  // `perm_open` (never delaying it), at most once per request and only while it is still open. Facts for display
  // only: they never change the risk class or the gesture. `recoverable` is "no" (a sure loss) or "unknown", never
  // a reassurance. No `perm_impact` means "Folgen unbekannt".
  | { type: "perm_impact"; id: string; impact: Impact }
  // Halt-all, on every change (on/off, and while on when the set of sources changes); also `snapshot.halt`. While on,
  // every open request is closed (`perm_closed` reason "halt") and every new one is denied without a `perm_open`;
  // pre-tool hooks of "every-step" sources are refused. Released only by `halt` on:false from the paired headset,
  // the line `continue` (or `weiter`) on the bridge's own terminal, or a bridge restart (no HTTP route can release it).
  | ({ type: "halt_state" } & HaltState)
  | { type: "perm_closed"; id: string; reason: string }
  // Answer to `agent_detail_request` (paired socket only, at most one per sid every 3 s, else an `error`
  // "rate-limited"; an unknown sid gives "unknown-agent"). `title` is `agent.title` or "". `summary` at most
  // 280 characters; `recent` at most 5 labels of at most 80 characters, newest first ("Reads src/cart.ts",
  // "Edits …", "Runs tests", "Runs: <command, 40 characters>"); `waitingFor` at most 140 characters:
  // the explanation of the agent's pending request, a notification ("Waiting for your next instruction.")
  // or a terminal prompt, else null. `source` "rules" first; with the AI on, a second `agent_detail` for the
  // same sid with source "ai" (summary from the local transcript) may follow: show the latest one. All of it
  // is untrusted display text and never affects risk, gestures or decisions.
  | {
      type: "agent_detail";
      sid: string;
      title: string;
      summary: string;
      recent: string[];
      waitingFor: string | null;
      source: "ai" | "rules";
    }
  // Answer to `agent_reply`: delivered is true when the agent's reply listener (a Claude Stop hook) took the
  // text, false otherwise (no listener, the agent is not done or waiting, an unknown agent, or Muse, Codex and
  // Gemini, which cannot take replies yet). A second reply within 5 s gives `error` "rate-limited".
  | { type: "agent_reply_ack"; sid: string; delivered: boolean }
  // Live feed (answers to `feed_subscribe`, paired socket only): first the last at most 30 entries of the
  // agent's session log, oldest first, then new entries as they appear (polled every second, batched).
  // `FeedEntry = {id, t, kind, label, text, tool?}`: id stable per entry (de-duplicate by it), t in ms,
  // kind "prompt"|"thinking"|"text"|"tool"|"result"|"error"|"system", label at most 40 characters, text at
  // most 400 (redacted, absolute paths shortened). Untrusted display text: it never affects risk, gestures or
  // decisions, and the bridge never records or logs it. An unknown sid gives `error` "unknown-agent".
  | { type: "feed_snapshot"; sid: string; entries: FeedEntry[] }
  | { type: "feed_append"; sid: string; entries: FeedEntry[] }
  | { type: "paired"; session: string }
  // Answer to a client `ping` on the paired socket (application-level liveness check).
  | { type: "pong" }
  | { type: "error"; error: string };

export type ClientMessage =
  | { type: "hello"; code?: string; session?: string }
  | { type: "perm_decision"; decision: ClientDecision }
  | { type: "agent_detail_request"; sid: string }
  // The user's own reply to an agent (keyboard or dictation on the headset): at most 1000 characters after
  // cleaning (newlines kept, other control characters removed). Never generated by AI.
  | { type: "agent_reply"; sid: string; text: string }
  // Live feed of one agent: at most 5 subscriptions at once (a sixth drops the oldest); all end when the
  // headset disconnects.
  | { type: "feed_subscribe"; sid: string }
  | { type: "feed_unsubscribe"; sid: string }
  // Halt-all on or off (paired socket only).
  | { type: "halt"; on: boolean }
  | { type: "ping" };

const MAX_SID = 200;

/** Parses and validates one client frame; null for anything that is not a well-formed message. */
export function parseClientMessage(raw: string): ClientMessage | null {
  let v: unknown;
  try {
    v = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof v !== "object" || v === null || Array.isArray(v)) return null;
  const o = v as Record<string, unknown>;
  if (o.type === "hello") {
    if (o.code !== undefined && typeof o.code !== "string") return null;
    if (o.session !== undefined && typeof o.session !== "string") return null;
    if (o.code === undefined && o.session === undefined) return null;
    const hello: { type: "hello"; code?: string; session?: string } = { type: "hello" };
    if (o.code !== undefined) hello.code = o.code;
    if (o.session !== undefined) hello.session = o.session;
    return hello;
  }
  if (o.type === "ping") return { type: "ping" };
  if (o.type === "halt") return typeof o.on === "boolean" ? { type: "halt", on: o.on } : null;
  if (o.type === "agent_reply") {
    if (typeof o.sid !== "string" || o.sid === "" || o.sid.length > MAX_SID || typeof o.text !== "string") return null;
    const text = cleanReply(o.text);
    if (text === "" || [...text].length > MAX_REPLY_TEXT) return null;
    return { type: "agent_reply", sid: o.sid, text };
  }
  if (o.type === "feed_subscribe" || o.type === "feed_unsubscribe") {
    if (typeof o.sid !== "string" || o.sid === "" || o.sid.length > MAX_SID) return null;
    return { type: o.type, sid: o.sid };
  }
  if (o.type === "agent_detail_request") {
    if (typeof o.sid !== "string" || o.sid === "" || o.sid.length > MAX_SID) return null;
    return { type: "agent_detail_request", sid: o.sid };
  }
  if (o.type === "perm_decision") {
    const d = o.decision;
    if (typeof d !== "object" || d === null || Array.isArray(d)) return null;
    const x = d as Record<string, unknown>;
    if (typeof x.id !== "string") return null;
    if (x.behavior !== "allow" && x.behavior !== "deny") return null;
    if (x.confirm !== undefined && x.confirm !== "pinch-hold" && x.confirm !== "two-hand") return null;
    if (x.message !== undefined && typeof x.message !== "string") return null;
    const decision: ClientDecision = { id: x.id, behavior: x.behavior };
    if (x.confirm !== undefined) decision.confirm = x.confirm;
    if (x.message !== undefined) decision.message = x.message;
    return { type: "perm_decision", decision };
  }
  return null;
}
