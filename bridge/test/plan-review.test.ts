// Fixes from the review of the live-plan task (P1): text cleaning order, `more`, subagents, late events,
// idle expiry, shutdown, and the caps on the wire.
import { request } from "node:http";
import { afterEach, describe, expect, it } from "vitest";
import type { AgentEvent } from "../src/events.js";
import { PlanTracker, SubagentGuard, stepText } from "../src/plan.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset, postTo, settle } from "./live-helpers.js";
import type { Headset } from "./live-helpers.js";

describe("stepText: cleaning before shortening", () => {
  it("a zero-width space inside a home path cannot stop the shortening", () => {
    expect(stepText("Lies /​home/bob/proj/.env", "/home/bob")).toBe("Lies ~/proj/.env");
    expect(stepText("Lies /home/bob/⁠proj/.env", "/home/bob")).toBe("Lies ~/proj/.env");
  });
});

describe("PlanTracker: more", () => {
  const todos = (n: number) => ({ todos: Array.from({ length: n }, (_, i) => ({ content: `Schritt ${i}`, status: "pending" })) });
  it("counts every usable step beyond 12, up to the 200-item input bound", () => {
    const t = new PlanTracker("/home/bob");
    expect(t.observe("a", { tool: "TodoWrite", input: todos(50), phase: "pre" })?.plan?.more).toBe(38);
    // A list longer than 200 is cut there: `more` is then a lower bound (188).
    expect(t.observe("a", { tool: "TodoWrite", input: todos(5000), phase: "pre" })?.plan?.more).toBe(188);
  });
  it("does not count steps that would be empty after cleaning", () => {
    const t = new PlanTracker("/home/bob");
    const input = todos(14);
    input.todos[13].content = "​​";
    expect(t.observe("a", { tool: "TodoWrite", input, phase: "pre" })?.plan?.more).toBe(1);
  });
});

describe("SubagentGuard", () => {
  it("ignores plan calls of a payload with a non-empty agent_id", () => {
    const g = new SubagentGuard();
    expect(g.note("s", "tool_start", { agent_id: "a1", agent_type: "Explore" })).toBe(true);
    expect(g.note("s", "tool_start", { agent_id: "" })).toBe(false);
    expect(g.note("s", "tool_start", {})).toBe(false);
  });
  it("without agent_id: ignores plan calls between SubagentStart and SubagentStop; a turn boundary resets", () => {
    const g = new SubagentGuard();
    expect(g.note("s", "subagent_start", {})).toBe(true);
    expect(g.note("s", "tool_start", {})).toBe(true);
    expect(g.note("s", "subagent_stop", {})).toBe(false);
    expect(g.note("s", "tool_start", {})).toBe(false);
    g.note("s", "subagent_start", {});
    g.note("s", "stop", {});
    expect(g.note("s", "tool_start", {})).toBe(false);
  });
  it("once a session showed agent_id, only agent_id counts (a background subagent does not freeze the main plan)", () => {
    const g = new SubagentGuard();
    g.note("s", "subagent_start", { agent_id: "a1" });
    expect(g.note("s", "tool_start", {})).toBe(false);
    expect(g.note("s", "tool_start", { agent_id: "a1" })).toBe(true);
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

const todo = (content: string, status = "pending") => ({ content, status, activeForm: content });
const hook = (sid: string, name: string, extra: Record<string, unknown> = {}) => ({ session_id: sid, hook_event_name: name, cwd: "/tmp/shop", ...extra });
const todoWrite = (sid: string, todos: unknown[], extra: Record<string, unknown> = {}) => hook(sid, "PreToolUse", { tool_name: "TodoWrite", tool_input: { todos }, ...extra });
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
const plansOf = (h: Headset, sid: string) => h.msgs.filter((m) => m.type === "agent" && m.agent.sid === sid && m.agent.plan);
/** Distinct plan updates of one agent (every agent message carries the current plan; updatedAt marks a new one). */
const planUpdates = (h: Headset, sid: string) => new Set(plansOf(h, sid).map((m) => m.agent.plan.updatedAt)).size;

describe("live server: plan review fixes", () => {
  it("the 12-step and 80-character caps hold on the wire", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", todoWrite("c1", Array.from({ length: 15 }, (_, i) => todo(`${"x".repeat(100)} ${i}`))));
    const m = await headset.next((x) => x.type === "agent" && x.agent.plan);
    expect(m.agent.plan.steps).toHaveLength(12);
    expect(m.agent.plan.more).toBe(3);
    expect(m.agent.plan.steps.every((s: { text: string }) => [...s.text].length <= 80)).toBe(true);
  });

  it("a very large list and a huge non-object input: bounded plan, no crash", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", todoWrite("c1", Array.from({ length: 5000 }, (_, i) => todo(`Schritt ${i}`))));
    const m = await headset.next((x) => x.type === "agent" && x.agent.plan);
    expect(m.agent.plan.steps).toHaveLength(12);
    expect(m.agent.plan.more).toBe(188);
    const r = await postTo(handle, "/ev", hook("c2", "PreToolUse", { tool_name: "TodoWrite", tool_input: "y".repeat(900_000) }));
    expect(r.status).toBe(200);
    await settle(headset);
    expect(plansOf(headset, "c2")).toEqual([]);
  });

  it("a subagent's TodoWrite (agent_id set) does not replace the main agent's plan", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", todoWrite("c1", [todo("Haupt A"), todo("Haupt B")]));
    await headset.next((x) => x.type === "agent" && x.agent.plan);
    await wait(550);
    await postTo(handle, "/ev", todoWrite("c1", [todo("Sub X")], { agent_id: "sub-1", agent_type: "general-purpose" }));
    await wait(600);
    await settle(headset);
    expect(plansOf(headset, "c1").every((m) => m.agent.plan.steps[0].text === "Haupt A")).toBe(true);
  });

  it("a hook arriving after SessionEnd brings no plan back; a new SessionStart allows plans again", async () => {
    handle = await bootLive();
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", hook("c1", "SessionStart"));
    await postTo(handle, "/ev", hook("c1", "SessionEnd"));
    await postTo(handle, "/ev", todoWrite("c1", [todo("Spät")]));
    await wait(600);
    await settle(headset);
    expect(plansOf(headset, "c1")).toEqual([]);
    await postTo(handle, "/ev", hook("c1", "SessionStart", { source: "resume" }));
    await postTo(handle, "/ev", todoWrite("c1", [todo("Neu")]));
    const m = await headset.next((x) => x.type === "agent" && x.agent.plan);
    expect(m.agent.plan.steps[0].text).toBe("Neu");
  });

  it("idle sessions lose their tracker state after the idle time", async () => {
    handle = await bootLive({ planIdleMs: 400 });
    headset = await pairHeadset(handle);
    await postTo(handle, "/ev", todoWrite("c1", [todo("A")]));
    await headset.next((x) => x.type === "agent" && x.agent.plan);
    await wait(100);
    // Within the idle time the same list is no change.
    await postTo(handle, "/ev", todoWrite("c1", [todo("A")]));
    await wait(50);
    await settle(headset);
    const before = planUpdates(headset, "c1");
    expect(before).toBe(1);
    await wait(500);
    await postTo(handle, "/ev", hook("c2", "SessionStart")); // any event sweeps
    await postTo(handle, "/ev", todoWrite("c1", [todo("A")]));
    await headset.next(() => planUpdates(headset as Headset, "c1") > before);
  });

  it("shutdown with a pending rate-limit timer, and an /ev in flight during close(), emit no plan afterwards", async () => {
    const events: AgentEvent[] = [];
    handle = await bootLive({ onEvent: (e) => events.push(e) });
    await postTo(handle, "/ev", todoWrite("c1", [todo("A")]));
    await postTo(handle, "/ev", todoWrite("c1", [todo("A", "completed")])); // waits for the rate limit
    // An /ev whose body is still arriving when close() starts.
    const port = handle.httpPort;
    const body = JSON.stringify({ ...todoWrite("x1", [todo("Codex")]), lotse_source: "codex" });
    const req = request({ host: "127.0.0.1", port, path: "/ev", method: "POST", headers: { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) } });
    req.on("error", () => {});
    req.write(body.slice(0, 10));
    await wait(30);
    const closing = handle.close();
    const closedAt = events.length;
    req.end(body.slice(10));
    await closing;
    handle = undefined;
    await wait(700);
    expect(events.slice(closedAt).filter((e) => e.kind === "plan")).toEqual([]);
  });
});
