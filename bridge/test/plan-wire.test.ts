// AgentState.plan end to end: the pipeline, recordings, the live server (hooks and the Codex log) and the pack.
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { anonymizeEvent } from "../src/anonymize.js";
import type { AgentEvent } from "../src/events.js";
import { buildPack } from "../src/pack.js";
import type { DemoPack } from "../src/pack.js";
import { createPipeline } from "../src/pipeline.js";
import { Recorder, readRecording } from "../src/recorder.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset, postTo, settle } from "./live-helpers.js";
import type { Headset, Msg } from "./live-helpers.js";
import { tempDir } from "./tmp.js";
import { WebSocket } from "ws";

const mk = () => createPipeline({ budgetUsd: 10, collisionWindowMs: 30_000 });
const steps = (...texts: [string, "pending" | "in_progress" | "done"][]) => ({ steps: texts.map(([text, status]) => ({ text, status })), more: 0 });

describe("pipeline: plan events", () => {
  it("an agent has no plan field until a plan is known", () => {
    const p = mk();
    const [m] = p.ingest({ sid: "a", t: 1, kind: "session_start" });
    expect(m.type === "agent" && "plan" in m.agent).toBe(false);
    expect("plan" in p.snapshot().agents[0]).toBe(false);
  });

  it("a plan event sets agent.plan with updatedAt = event time, in agent and snapshot", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "session_start" });
    const out = p.ingest({ sid: "a", t: 5, kind: "plan", plan: steps(["Read Cart.tsx", "in_progress"]) });
    expect(out).toEqual([{ type: "agent", agent: expect.objectContaining({ sid: "a", plan: { ...steps(["Read Cart.tsx", "in_progress"]), updatedAt: 5 } }) }]);
    expect(p.snapshot().agents[0].plan?.steps[0].text).toBe("Read Cart.tsx");
    // status and other events keep the plan, and do not change the agent's status
    const [after] = p.ingest({ sid: "a", t: 6, kind: "tool_start", tool: "Read", path: "/p/x.ts" });
    expect(after.type === "agent" && after.agent.plan?.updatedAt).toBe(5);
  });

  it("a plan event does not change status or lastAction", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "tool_start", tool: "Read", path: "/p/x.ts" });
    const [m] = p.ingest({ sid: "a", t: 2, kind: "plan", plan: steps(["A", "pending"]) });
    expect(m.type === "agent" && [m.agent.status, m.agent.lastAction]).toEqual(["reading", "Reads p/x.ts"]);
  });

  it("a plan event without a plan clears it (the field is gone again)", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "plan", plan: steps(["A", "pending"]) });
    p.ingest({ sid: "a", t: 0, kind: "session_start" });
    const [m] = p.ingest({ sid: "a", t: 2, kind: "plan" });
    expect(m.type === "agent" && "plan" in m.agent).toBe(false);
  });

  it("a plan event for an unknown session creates no agent", () => {
    const p = mk();
    expect(p.ingest({ sid: "x", t: 1, kind: "plan", plan: steps(["A", "pending"]) })).toEqual([]);
    expect(p.snapshot().agents).toEqual([]);
  });

  it("a plan whose steps are all done stays; session end drops it", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "session_start" });
    p.ingest({ sid: "a", t: 2, kind: "plan", plan: steps(["A", "done"], ["B", "done"]) });
    const [stop] = p.ingest({ sid: "a", t: 3, kind: "stop", summary: "Fertig" });
    expect(stop.type === "agent" && stop.agent.plan?.steps.length).toBe(2);
    const [end] = p.ingest({ sid: "a", t: 4, kind: "session_end" });
    expect(end.type === "agent" && "plan" in end.agent).toBe(false);
  });

  it("re-applies the caps to a plan event from a file", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "session_start" });
    const long = { steps: Array.from({ length: 13 }, (_, i) => ({ text: `${"x".repeat(100)}${i}`, status: "weird" })), more: 0 };
    const [m] = p.ingest({ sid: "a", t: 2, kind: "plan", plan: long as never });
    const plan = m.type === "agent" ? m.agent.plan : undefined;
    expect(plan?.steps).toHaveLength(12);
    expect(plan?.more).toBe(1);
    expect(plan?.steps.every((s) => [...s.text].length <= 80 && s.status === "pending")).toBe(true);
  });

  it("messages keep their own copy of the plan", () => {
    const p = mk();
    p.ingest({ sid: "a", t: 1, kind: "session_start" });
    const [m] = p.ingest({ sid: "a", t: 2, kind: "plan", plan: steps(["A", "pending"]) });
    p.ingest({ sid: "a", t: 3, kind: "plan", plan: steps(["B", "done"]) });
    expect(m.type === "agent" && m.agent.plan?.steps[0].text).toBe("A");
  });
});

describe("recordings: plan events", () => {
  const HOME = "/home/alice";
  const REPO = "/home/alice/shop";

  it("anonymises step text like other agent text", () => {
    const e: AgentEvent = {
      sid: "a",
      t: 1,
      kind: "plan",
      plan: steps(["/home/alice/shop/src/Cart.tsx lesen", "done"], ["Token sk-abcdefghijklmnopqrstuvwxyz0123 prüfen", "pending"]),
    };
    const out = anonymizeEvent(e, { repoRoot: REPO, homeDir: HOME });
    expect(out.plan?.steps.map((s) => s.text)).toEqual(["<repo>/src/Cart.tsx lesen", "Token [REDACTED] prüfen"]);
    expect(e.plan?.steps[0].text).toBe("/home/alice/shop/src/Cart.tsx lesen"); // a copy
  });

  it("round-trips through the recorder and rejects malformed plans", async () => {
    const dir = tempDir("lotse-plan-");
    const file = join(dir, "rec.ndjson");
    const rec = new Recorder(file, { repoRoot: REPO, homeDir: HOME });
    rec.write({ sid: "a", t: 100, kind: "session_start", cwd: REPO });
    rec.write({ sid: "a", t: 150, kind: "plan", plan: steps(["A", "in_progress"]) });
    rec.write({ sid: "a", t: 160, kind: "plan" });
    await rec.close();
    const got = await readRecording(file);
    expect(got[1]).toEqual({ sid: "a", t: 50, kind: "plan", plan: steps(["A", "in_progress"]) });
    expect(got[2]).toEqual({ sid: "a", t: 60, kind: "plan" });

    for (const plan of [5, "x", { steps: "x", more: 0 }, { steps: [{ text: 1, status: "done" }], more: 0 }, { steps: [{ text: "a", status: "nope" }], more: 0 }, { steps: [], more: -1 }]) {
      writeFileSync(file, JSON.stringify({ sid: "a", t: 1, kind: "plan", plan }) + "\n");
      await expect(readRecording(file)).rejects.toThrow(/line 1: plan/);
    }
  });
});

// ---- live server ----

let handle: ServerHandle | undefined;
let headset: Headset | undefined;
afterEach(async () => {
  headset?.close();
  headset = undefined;
  await handle?.close();
  handle = undefined;
});

const todo = (content: string, status: string) => ({ content, status, activeForm: content });
const hook = (sid: string, name: string, extra: Record<string, unknown> = {}) => ({ session_id: sid, hook_event_name: name, cwd: "/tmp/shop", ...extra });
const todoWrite = (sid: string, todos: unknown[], name = "PreToolUse") => hook(sid, name, { tool_name: "TodoWrite", tool_input: { todos } });
const planMsgs = (h: Headset, sid: string) => h.msgs.filter((m) => m.type === "agent" && m.agent.sid === sid && m.agent.plan);

describe("live server: plans from hooks", () => {
  it("a TodoWrite hook gives an agent message with the plan, and a reconnecting headset gets it in the snapshot", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", hook("c1", "SessionStart"));
    await postTo(handle, "/ev", todoWrite("c1", [todo("Read Cart.tsx", "completed"), todo("Badge bauen", "in_progress")]));
    const m = await headset.next((x) => x.type === "agent" && x.agent.plan !== undefined);
    expect(m.agent.plan).toMatchObject({ steps: [{ text: "Read Cart.tsx", status: "done" }, { text: "Badge bauen", status: "in_progress" }], more: 0 });
    expect(typeof m.agent.plan.updatedAt).toBe("number");
    const session = (headset.msgs.find((x) => x.type === "paired") as Msg).session as string;
    headset.close();
    const snap = await resumeSnapshot(handle, session);
    expect(snap.agents[0].plan.steps).toHaveLength(2);
  });

  it("redacts a secret-looking token and an absolute home path in a step on the wire", async () => {
    const home = tempDir("lotse-plan-");
    handle = await bootLive({ homeDir: home });
    headset = await pairHeadset(handle);
    const secret = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
    const homePath = join(home, "shop", "secret.env");
    await postTo(handle, "/ev", todoWrite("c1", [todo(`${homePath} mit ${secret} lesen`, "pending"), todo("ghp_abcdefghijklmnopqrstuvwxyz0123456789 /etc/hosts", "pending")]));
    const m = await headset.next((x) => x.type === "agent" && x.agent.plan !== undefined);
    const wire = JSON.stringify(m);
    expect(wire).not.toContain(secret);
    expect(wire).not.toContain("sk-");
    expect(wire).not.toContain("ghp_");
    expect(wire).not.toContain(home);
    expect(wire).not.toContain(home.replace(/\\/g, "\\\\"));
    expect(wire).not.toContain("/etc/");
    expect(m.agent.plan.steps[0].text).toBe("~/shop/secret.env mit [REDACTED] lesen");
  });

  it("the same list again (PostToolUse) sends no second plan update", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    const todos = [todo("A", "pending")];
    await postTo(handle, "/ev", todoWrite("c1", todos));
    await postTo(handle, "/ev", { ...todoWrite("c1", todos, "PostToolUse"), tool_response: { oldTodos: [], newTodos: todos } });
    await new Promise((r) => setTimeout(r, 700));
    await settle(headset);
    // two hook events, one plan event: three agent messages, the plan first appears once
    const agentMsgs = headset.msgs.filter((m) => m.type === "agent");
    expect(agentMsgs).toHaveLength(3);
  });

  it("rate-limits plan updates to one per agent per 500 ms and always delivers the last state", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    const t0 = Date.now();
    await postTo(handle, "/ev", todoWrite("c1", [todo("A", "in_progress")]));
    await postTo(handle, "/ev", todoWrite("c1", [todo("A", "done"), todo("B", "in_progress")]));
    await postTo(handle, "/ev", todoWrite("c1", [todo("A", "done"), todo("B", "done"), todo("C", "in_progress")]));
    const last = await headset.next((x) => x.type === "agent" && x.agent.plan?.steps.length === 3, 3000);
    const firstAt = planMsgs(headset, "c1")[0];
    expect(firstAt.agent.plan.steps).toEqual([{ text: "A", status: "in_progress" }]);
    // the middle state was replaced while it waited
    expect(planMsgs(headset, "c1").some((m) => m.agent.plan.steps.length === 2)).toBe(false);
    expect(last.agent.plan.updatedAt - firstAt.agent.plan.updatedAt).toBeGreaterThanOrEqual(490);
    expect(Date.now() - t0).toBeGreaterThanOrEqual(490);
  });

  it("drops the plan at session end, and a waiting update never arrives afterwards", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", hook("c1", "SessionStart"));
    await postTo(handle, "/ev", todoWrite("c1", [todo("A", "in_progress")]));
    await postTo(handle, "/ev", todoWrite("c1", [todo("A", "done")]));
    await postTo(handle, "/ev", hook("c1", "SessionEnd"));
    const end = await headset.next((x) => x.type === "agent" && x.agent.lastAction === "Session ended");
    expect("plan" in end.agent).toBe(false);
    await new Promise((r) => setTimeout(r, 700));
    await settle(headset);
    const after = headset.msgs.slice(headset.msgs.indexOf(end) + 1).filter((m) => m.type === "agent");
    expect(after).toEqual([]);
  });

  it("Claude TaskCreate/TaskUpdate through PostToolUse hooks", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    const post = (tool: string, input: unknown, response: unknown) => postTo(handle as ServerHandle, "/ev", hook("c1", "PostToolUse", { tool_name: tool, tool_input: input, tool_response: response }));
    await post("TaskCreate", { subject: "Read Cart.tsx", description: "x" }, { task: { id: "1", subject: "Read Cart.tsx" } });
    await headset.next((x) => x.type === "agent" && x.agent.plan?.steps.length === 1);
    await new Promise((r) => setTimeout(r, 550));
    await post("TaskUpdate", { taskId: "1", status: "completed" }, { success: true, taskId: "1", updatedFields: ["status"] });
    const m = await headset.next((x) => x.type === "agent" && x.agent.plan?.steps[0].status === "done");
    expect(m.agent.plan.steps).toEqual([{ text: "Read Cart.tsx", status: "done" }]);
  });

  it("Codex update_plan and Gemini write_todos hooks give plans too", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", { ...hook("x1", "PreToolUse", { tool_name: "update_plan", tool_input: { plan: [{ step: "Summen fixen", status: "in_progress" }] } }), lotse_source: "codex" });
    await postTo(handle, "/ev", { ...hook("g1", "PreToolUse", { tool_name: "write_todos", tool_input: { todos: [{ description: "Suche bauen", status: "pending" }] } }), lotse_source: "gemini" });
    const codex = await headset.next((x) => x.type === "agent" && x.agent.sid === "x1" && x.agent.plan);
    const gemini = await headset.next((x) => x.type === "agent" && x.agent.sid === "g1" && x.agent.plan);
    expect(codex.agent).toMatchObject({ source: "codex", plan: { steps: [{ text: "Summen fixen", status: "in_progress" }] } });
    expect(gemini.agent).toMatchObject({ source: "gemini", plan: { steps: [{ text: "Suche bauen", status: "pending" }] } });
  });

  it("an agent without a plan tool never gets a plan field", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", hook("c1", "PreToolUse", { tool_name: "Read", tool_input: { file_path: "/tmp/shop/a.ts" } }));
    await postTo(handle, "/ev", hook("c1", "PreToolUse", { tool_name: "TodoWrite", tool_input: { todos: "garbage" } }));
    await new Promise((r) => setTimeout(r, 50));
    await settle(headset);
    expect(headset.msgs.filter((m) => m.type === "agent").every((m) => !("plan" in m.agent))).toBe(true);
  });
});

describe("live server: Codex plan from its session log", () => {
  it("reads update_plan from the rollout file when the hooks do not carry it", async () => {
    const home = tempDir("lotse-plan-");
    const dir = join(home, ".codex", "sessions", "2026", "11", "12");
    mkdirSync(dir, { recursive: true });
    const file = join(dir, "rollout-2026-11-12T10-00-00-abc.jsonl");
    const line = (plan: unknown) =>
      JSON.stringify({ timestamp: "2026-11-12T10:00:01Z", type: "response_item", payload: { type: "function_call", name: "update_plan", arguments: JSON.stringify({ plan }), call_id: "c" } }) + "\n";
    writeFileSync(file, JSON.stringify({ type: "session_meta", payload: { id: "x1" } }) + "\n" + line([{ step: "Alt", status: "pending" }]) + line([{ step: "Checkout lesen", status: "in_progress" }]));
    handle = await bootLive({ homeDir: home, planLogIntervalMs: 50 });
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", { ...hook("x1", "SessionStart", { transcript_path: file }), lotse_source: "codex" });
    const first = await headset.next((x) => x.type === "agent" && x.agent.plan);
    // only the latest plan of the existing log goes out
    expect(first.agent.plan.steps).toEqual([{ text: "Checkout lesen", status: "in_progress" }]);
    appendFileSync(file, line([{ step: "Checkout lesen", status: "completed" }, { step: "Summen fixen", status: "in_progress" }]));
    const next = await headset.next((x) => x.type === "agent" && x.agent.plan?.steps.length === 2, 3000);
    expect(next.agent.plan.steps[0]).toEqual({ text: "Checkout lesen", status: "done" });
  });

  it("never reads a log outside the tool's own folder", async () => {
    const home = tempDir("lotse-plan-");
    const outside = join(tempDir("lotse-plan-"), "rollout-x.jsonl");
    writeFileSync(outside, JSON.stringify({ type: "response_item", payload: { type: "function_call", name: "update_plan", arguments: JSON.stringify({ plan: [{ step: "X", status: "pending" }] }) } }) + "\n");
    handle = await bootLive({ homeDir: home, planLogIntervalMs: 20 });
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", { ...hook("x1", "SessionStart", { transcript_path: outside }), lotse_source: "codex" });
    await new Promise((r) => setTimeout(r, 200));
    await settle(headset);
    expect(headset.msgs.some((m) => m.type === "agent" && m.agent.plan)).toBe(false);
  });
});

/** Reconnects with the session token and returns the snapshot it gets. */
async function resumeSnapshot(h: ServerHandle, session: string): Promise<Msg> {
  const ws = new WebSocket(`ws://127.0.0.1:${h.wsPort}`);
  try {
    return await new Promise<Msg>((res, rej) => {
      ws.on("message", (d) => {
        const m = JSON.parse(d.toString()) as Msg;
        if (m.type === "snapshot") res(m);
      });
      ws.once("open", () => ws.send(JSON.stringify({ type: "hello", session })));
      ws.once("error", rej);
      setTimeout(() => rej(new Error("no snapshot")), 3000);
    });
  } finally {
    ws.terminate();
  }
}

// ---- pack ----

const DEMO = resolve(import.meta.dirname, "../../demo");

describe("pack: plans", () => {
  it("the jury pack carries changing plans for all four demo agents, 4 to 6 steps each", () => {
    const pack = JSON.parse(readFileSync(join(DEMO, "jury.pack.json"), "utf8")) as DemoPack;
    for (const sid of ["demo-a1", "demo-a2", "demo-a3", "demo-a4"]) {
      const plans = pack.messages.filter((m) => m.msg.type === "agent" && m.msg.agent.sid === sid && m.msg.agent.plan).map((m) => (m.msg as { agent: { plan: { steps: { text: string; status: string }[] } } }).agent.plan);
      expect(plans.length, sid).toBeGreaterThan(0);
      const distinct = new Set(plans.map((p) => JSON.stringify(p.steps)));
      expect(distinct.size, sid).toBeGreaterThanOrEqual(3);
      for (const p of plans) {
        expect(p.steps.length, sid).toBeGreaterThanOrEqual(4);
        expect(p.steps.length, sid).toBeLessThanOrEqual(6);
        expect(p.steps.filter((s) => s.status === "in_progress").length, sid).toBeLessThanOrEqual(1);
      }
    }
  });

  it("the pack's leak check covers plan steps", async () => {
    const events: AgentEvent[] = [
      { sid: "a", t: 0, kind: "session_start", cwd: "<repo>" },
      { sid: "a", t: 1, kind: "plan", plan: steps(["/home/bob/shop/Cart.tsx lesen", "pending"]) },
    ];
    await expect(buildPack(events, { budgetUsd: 5, demoRoot: tempDir("lotse-plan-") })).rejects.toThrow(/\/home\/.*agent/);
  });
});

describe("PlanLogWatcher", () => {
  it("buffers an incomplete line, follows a shrunk file from its new end, and stops at forget", async () => {
    const { PlanLogWatcher } = await import("../src/plan-log.js");
    const file = join(tempDir("lotse-planlog-"), "rollout.jsonl");
    const line = (step: string) =>
      JSON.stringify({ type: "response_item", payload: { type: "function_call", name: "update_plan", arguments: JSON.stringify({ plan: [{ step, status: "pending" }] }) } }) + "\n";
    writeFileSync(file, "");
    const got: string[] = [];
    const w = new PlanLogWatcher({
      logFile: () => file,
      onCalls: (_sid, calls) => got.push(...calls.map((c) => JSON.parse(c.input as string).plan[0].step)),
      intervalMs: 60_000,
    });
    w.watch("x");
    await w.pollNow();
    const full = line("A");
    appendFileSync(file, full.slice(0, 30));
    await w.pollNow();
    expect(got).toEqual([]);
    appendFileSync(file, full.slice(30));
    await w.pollNow();
    expect(got).toEqual(["A"]);
    writeFileSync(file, ""); // cut
    await w.pollNow();
    appendFileSync(file, line("B"));
    await w.pollNow();
    expect(got).toEqual(["A", "B"]);
    w.forget("x");
    appendFileSync(file, line("C"));
    await w.pollNow();
    expect(got).toEqual(["A", "B"]);
    expect(w.watched()).toEqual([]);
  });

  it("watches at most 16 sessions", async () => {
    const { PlanLogWatcher, PLAN_LOG_MAX_SESSIONS } = await import("../src/plan-log.js");
    const w = new PlanLogWatcher({ logFile: () => null, onCalls: () => {}, intervalMs: 60_000 });
    for (let i = 0; i < 20; i++) w.watch(`s${i}`);
    expect(w.watched()).toHaveLength(PLAN_LOG_MAX_SESSIONS);
    expect(w.watched()[0]).toBe("s4");
    w.clear();
  });
});
