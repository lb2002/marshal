// Event → server messages, without any network: the session store and the detectors.
// The live server broadcasts what ingest() returns; the demo pack records it with a timestamp.

import type { AgentEvent } from "./events.js";
import { SessionStore } from "./state.js";
import type { AgentState } from "./state.js";
import { Detectors } from "./detectors.js";
import type { ServerMessage } from "./protocol.js";

/** Two edits of the same file by different agents within this window are a collision. */
export const COLLISION_WINDOW_MS = 30_000;

export type SnapshotMessage = Extract<ServerMessage, { type: "snapshot" }>;

export interface Pipeline {
  /** Messages for one event, in send order: the agent update, then any incidents. */
  ingest(e: AgentEvent): ServerMessage[];
  /** Current agents; `pending` and `explains` are empty (the broker owns open requests). */
  snapshot(): SnapshotMessage;
  /** The current state of one agent (a copy), or undefined. */
  agent(sid: string): AgentState | undefined;
  /** Replaces an agent's title; the agent message to send, or nothing for an unknown session. */
  setTitle(sid: string, title: string): ServerMessage[];
}

/** A copy, so a message kept for later (the pack) does not change when the agent moves on. */
const copyAgent = (a: AgentState): AgentState => {
  const c: AgentState = { ...a, files: [...a.files] };
  if (a.plan) c.plan = { ...a.plan, steps: a.plan.steps.map((s) => ({ ...s })) };
  return c;
};

export function createPipeline(opts: { budgetUsd: number; collisionWindowMs: number }): Pipeline {
  const store = new SessionStore();
  const detectors = new Detectors({ collisionWindowMs: opts.collisionWindowMs, budgetUsd: opts.budgetUsd });
  return {
    ingest(e) {
      const out: ServerMessage[] = [];
      const agent = store.apply(e);
      if (agent) out.push({ type: "agent", agent: copyAgent(agent) });
      for (const incident of detectors.check(e, store)) out.push({ type: "incident", incident });
      return out;
    },
    snapshot() {
      return { type: "snapshot", agents: store.all().map(copyAgent), pending: [], explains: {} };
    },
    agent(sid) {
      const a = store.get(sid);
      return a ? copyAgent(a) : undefined;
    },
    setTitle(sid, title) {
      const a = store.setTitle(sid, title);
      return a ? [{ type: "agent", agent: copyAgent(a) }] : [];
    },
  };
}
