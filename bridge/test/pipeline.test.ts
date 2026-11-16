import { describe, expect, it } from "vitest";
import { createPipeline } from "../src/pipeline.js";
import type { ServerMessage } from "../src/protocol.js";

const mk = () => createPipeline({ budgetUsd: 10, collisionWindowMs: 30_000 });

describe("createPipeline", () => {
  it("starts with the empty snapshot shape", () => {
    expect(mk().snapshot()).toEqual({ type: "snapshot", agents: [], pending: [], explains: {} });
  });

  it("returns the agent update before the incidents of the same event", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "tool_start", tool: "Edit", path: "/p/x.ts" });
    const out = p.ingest({ sid: "b", t: 2, kind: "tool_start", tool: "Edit", path: "/p/x.ts" });
    expect(out.map((m) => m.type)).toEqual(["agent", "incident"]);
    expect(out[0]).toMatchObject({ agent: { sid: "b", status: "editing" } });
    expect(out[1]).toMatchObject({ incident: { kind: "collision", sids: ["b", "a"] } });
    const budget = p.ingest({ sid: "a", t: 3, kind: "status", costUsd: 7 });
    expect(budget.map((m) => m.type)).toEqual(["agent", "incident"]);
  });

  it("snapshot lists the agents with empty pending and explains", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "session_start" });
    const snap = p.snapshot();
    expect(snap.agents.map((a) => a.sid)).toEqual(["a"]);
    expect(snap.pending).toEqual([]);
    expect(snap.explains).toEqual({});
  });

  it("returns copies: kept messages do not follow later events, and mutating them does not touch the store", () => {
    const p = mk();
    const [first] = p.ingest({ sid: "a", t: 1, kind: "tool_start", tool: "Edit", path: "/p/x.ts" }) as Extract<ServerMessage, { type: "agent" }>[];
    p.ingest({ sid: "a", t: 2, kind: "stop", summary: "fertig" });
    expect(first.agent).toMatchObject({ status: "editing", files: ["p/x.ts"] });
    first.agent.files.push("evil");
    first.agent.status = "failed";
    const snap = p.snapshot();
    expect(snap.agents[0]).toMatchObject({ status: "done", files: ["p/x.ts"] });
    snap.agents[0].files.push("evil");
    expect(p.snapshot().agents[0].files).toEqual(["p/x.ts"]);
  });
});
