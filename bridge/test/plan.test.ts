import { describe, expect, it } from "vitest";
import {
  MAX_PLAN_STEPS,
  MAX_STEP_TEXT,
  PlanGate,
  PlanTracker,
  capPlan,
  codexPlanCalls,
  mapStatus,
  planCallFromHook,
  stepText,
} from "../src/plan.js";
import type { PlanSteps } from "../src/plan.js";

const HOME = "/home/alice";
const SID = "s1";

const todo = (content: string, status: string) => ({ content, status, activeForm: content });

function tracker() {
  return new PlanTracker(HOME);
}

describe("mapStatus", () => {
  it("maps the sources' words onto pending, in_progress and done", () => {
    expect(mapStatus("completed")).toBe("done");
    expect(mapStatus("done")).toBe("done");
    expect(mapStatus("in_progress")).toBe("in_progress");
    expect(mapStatus("pending")).toBe("pending");
  });
  it("maps unknown or missing statuses to pending", () => {
    for (const s of ["cancelled", "blocked", "IN PROGRESS", "", undefined, 3, null, {}]) expect(mapStatus(s)).toBe("pending");
  });
});

describe("stepText", () => {
  it("trims, removes control characters and keeps short text", () => {
    expect(stepText("  Read\u0007 Cart.tsx\n ", HOME)).toBe("Read Cart.tsx");
  });
  it("cuts at 80 characters with …", () => {
    const t = stepText("Wort ".repeat(40), HOME) as string;
    expect([...t].length).toBeLessThanOrEqual(MAX_STEP_TEXT);
    expect(t.endsWith("…")).toBe(true);
  });
  it("redacts a secret-looking token and shortens absolute paths", () => {
    const t = stepText("Token sk-abcdefghijklmnopqrstuvwxyz0123 in /home/alice/proj/.env und /etc/hosts prüfen", HOME) as string;
    expect(t).not.toContain("sk-");
    expect(t).not.toContain("/home/alice");
    expect(t).not.toContain("/etc/");
    expect(t).toContain("[REDACTED]");
    expect(t).toContain("~/proj/.env");
    expect(t).toContain("…/hosts");
  });
  it("gives null for non-strings and text that is empty after cleaning", () => {
    for (const v of [undefined, null, 5, {}, [], "", "   ", "\u0000​"]) expect(stepText(v, HOME)).toBeNull();
  });
});

describe("planCallFromHook", () => {
  it("takes tool name, input and result from pre and post tool events only", () => {
    const p = { tool_name: "TodoWrite", tool_input: { todos: [] }, tool_response: { ok: 1 } };
    expect(planCallFromHook("tool_start", p)).toEqual({ tool: "TodoWrite", input: { todos: [] }, result: { ok: 1 }, phase: "pre" });
    expect(planCallFromHook("tool_end", p)).toEqual({ tool: "TodoWrite", input: { todos: [] }, result: { ok: 1 }, phase: "post" });
    expect(planCallFromHook("permission", p)).toBeNull();
    expect(planCallFromHook("tool_fail", p)).toBeNull();
  });
  it("ignores other tools and malformed payloads", () => {
    expect(planCallFromHook("tool_start", { tool_name: "Bash", tool_input: { command: "ls" } })).toBeNull();
    expect(planCallFromHook("tool_start", null)).toBeNull();
    expect(planCallFromHook("tool_start", { tool_name: 5 })).toBeNull();
    expect(planCallFromHook("tool_start", "TodoWrite")).toBeNull();
  });
});

describe("PlanTracker: Claude Code TodoWrite", () => {
  it("turns the whole list into steps with mapped statuses", () => {
    const t = tracker();
    const c = t.observe(SID, {
      tool: "TodoWrite",
      phase: "pre",
      input: { todos: [todo("Read Cart.tsx", "completed"), todo("Build the badge component", "in_progress"), todo("Run the tests", "pending")] },
    });
    expect(c).toEqual({
      plan: {
        steps: [
          { text: "Read Cart.tsx", status: "done" },
          { text: "Build the badge component", status: "in_progress" },
          { text: "Run the tests", status: "pending" },
        ],
        more: 0,
      },
    });
  });
  it("reports no change when the same list comes again (pre then post)", () => {
    const t = tracker();
    const input = { todos: [todo("A", "pending")] };
    expect(t.observe(SID, { tool: "TodoWrite", phase: "pre", input })).not.toBeNull();
    expect(t.observe(SID, { tool: "TodoWrite", phase: "post", input, result: {} })).toBeNull();
  });
  it("a new list replaces the old one", () => {
    const t = tracker();
    t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("A", "pending"), todo("B", "pending")] } });
    const c = t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("C", "in_progress")] } });
    expect(c?.plan?.steps).toEqual([{ text: "C", status: "in_progress" }]);
  });
  it("keeps several in_progress steps as reported", () => {
    const c = tracker().observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("A", "in_progress"), todo("B", "in_progress")] } });
    expect(c?.plan?.steps.map((s) => s.status)).toEqual(["in_progress", "in_progress"]);
  });
  it("caps at 12 steps and counts the rest in more", () => {
    const todos = Array.from({ length: 15 }, (_, i) => todo(`Schritt ${i + 1}`, "pending"));
    const c = tracker().observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos } });
    expect(c?.plan?.steps).toHaveLength(MAX_PLAN_STEPS);
    expect(c?.plan?.steps[11].text).toBe("Schritt 12");
    expect(c?.plan?.more).toBe(3);
  });
  it("an empty list clears the plan; a second empty list is no change", () => {
    const t = tracker();
    t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("A", "pending")] } });
    expect(t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [] } })).toEqual({ plan: undefined });
    expect(t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [] } })).toBeNull();
  });
  it("skips malformed items and never throws on malformed input", () => {
    const t = tracker();
    const c = t.observe(SID, {
      tool: "TodoWrite",
      phase: "pre",
      input: { todos: [null, 5, "x", { content: 7 }, { content: "   " }, { status: "completed" }, todo("Gut", "completed")] },
    });
    expect(c?.plan?.steps).toEqual([{ text: "Gut", status: "done" }]);
    for (const input of [undefined, null, 5, "todos", [], { todos: "x" }, { todos: { 0: todo("A", "pending") } }]) {
      expect(() => t.observe(SID, { tool: "TodoWrite", phase: "pre", input })).not.toThrow();
      expect(t.observe(SID, { tool: "TodoWrite", phase: "pre", input })).toBeNull();
    }
  });
  it("a list of only garbage items is no change, not an empty plan", () => {
    const t = tracker();
    t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("A", "pending")] } });
    expect(t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [null, { content: 3 }] } })).toBeNull();
  });
  it("keeps sessions apart and forgets a session", () => {
    const t = tracker();
    t.observe("a", { tool: "TodoWrite", phase: "pre", input: { todos: [todo("A", "pending")] } });
    const b = t.observe("b", { tool: "TodoWrite", phase: "pre", input: { todos: [todo("B", "pending")] } });
    expect(b?.plan?.steps[0].text).toBe("B");
    t.forget("a");
    // after forgetting, the same list is new again
    expect(t.observe("a", { tool: "TodoWrite", phase: "pre", input: { todos: [todo("A", "pending")] } })).not.toBeNull();
  });
  it("redacts step text", () => {
    const c = tracker().observe(SID, {
      tool: "TodoWrite",
      phase: "pre",
      input: { todos: [todo("ghp_abcdefghijklmnopqrstuvwxyz0123456789 in /home/alice/x/key.txt", "pending")] },
    });
    const text = c?.plan?.steps[0].text ?? "";
    expect(text).not.toContain("ghp_");
    expect(text).not.toContain("/home/alice");
  });
});

describe("PlanTracker: Claude Code TaskCreate / TaskUpdate / TaskList", () => {
  const create = (t: PlanTracker, subject: string, id: unknown) =>
    t.observe(SID, { tool: "TaskCreate", phase: "post", input: { subject, description: "…" }, result: { task: { id, subject } } });

  it("ignores TaskCreate before the tool ran (pre), it has no id yet", () => {
    expect(tracker().observe(SID, { tool: "TaskCreate", phase: "pre", input: { subject: "A", description: "x" } })).toBeNull();
  });
  it("adds created tasks in order as pending", () => {
    const t = tracker();
    expect(create(t, "Read Cart.tsx", "1")?.plan?.steps).toEqual([{ text: "Read Cart.tsx", status: "pending" }]);
    expect(create(t, "Badge bauen", "2")?.plan?.steps.map((s) => s.text)).toEqual(["Read Cart.tsx", "Badge bauen"]);
  });
  it("updates status and subject by id, deletes with status deleted", () => {
    const t = tracker();
    create(t, "A", "1");
    create(t, "B", "2");
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "1", status: "completed" }, result: { success: true, taskId: "1", updatedFields: ["status"] } })?.plan?.steps).toEqual([
      { text: "A", status: "done" },
      { text: "B", status: "pending" },
    ]);
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "2", status: "in_progress", subject: "B neu" } })?.plan?.steps[1]).toEqual({
      text: "B neu",
      status: "in_progress",
    });
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "1", status: "deleted" } })?.plan?.steps).toEqual([{ text: "B neu", status: "in_progress" }]);
  });
  it("deleting the last task clears the plan", () => {
    const t = tracker();
    create(t, "A", "1");
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "1", status: "deleted" } })).toEqual({ plan: undefined });
  });
  it("accepts a numeric id and the result as a JSON string or a text with #id", () => {
    const t = tracker();
    t.observe(SID, { tool: "TaskCreate", phase: "post", input: { subject: "A" }, result: JSON.stringify({ task: { id: "7", subject: "A" } }) });
    t.observe(SID, { tool: "TaskCreate", phase: "post", input: { subject: "B" }, result: "Task #8 created successfully: B" });
    t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: 7, status: "completed" } });
    const c = t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "8", status: "in_progress" } });
    expect(c?.plan?.steps).toEqual([
      { text: "A", status: "done" },
      { text: "B", status: "in_progress" },
    ]);
  });
  it("a TaskUpdate that failed, before it ran, or for an unknown id changes nothing", () => {
    const t = tracker();
    create(t, "A", "1");
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "1", status: "completed" }, result: { success: false, error: "no" } })).toBeNull();
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "pre", input: { taskId: "1", status: "completed" } })).toBeNull();
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "99", status: "completed" } })).toBeNull();
  });
  it("tolerates unknown shapes: no change, no crash", () => {
    const t = tracker();
    for (const [input, result] of [
      [{}, {}],
      [{ subject: 5 }, { task: { id: "1" } }],
      [null, null],
      ["x", "y"],
      [{ subject: "   " }, { task: { id: "1" } }],
    ] as [unknown, unknown][]) {
      expect(() => t.observe(SID, { tool: "TaskCreate", phase: "post", input, result })).not.toThrow();
      expect(t.observe(SID, { tool: "TaskCreate", phase: "post", input, result })).toBeNull();
    }
    for (const input of [{}, { taskId: {} }, { taskId: "1", status: 5 }, null]) {
      expect(() => t.observe(SID, { tool: "TaskUpdate", phase: "post", input, result: { success: true } })).not.toThrow();
    }
  });
  it("a created task whose id is unknown still shows (it just cannot be updated)", () => {
    const c = tracker().observe(SID, { tool: "TaskCreate", phase: "post", input: { subject: "A" }, result: { weird: true } });
    expect(c?.plan?.steps).toEqual([{ text: "A", status: "pending" }]);
  });
  it("TaskList's result replaces the task list", () => {
    const t = tracker();
    create(t, "A", "1");
    const c = t.observe(SID, {
      tool: "TaskList",
      phase: "post",
      input: {},
      result: { tasks: [{ id: "1", subject: "A", status: "completed", blockedBy: [] }, { id: "2", subject: "B", status: "in_progress", blockedBy: [] }] },
    });
    expect(c?.plan?.steps).toEqual([
      { text: "A", status: "done" },
      { text: "B", status: "in_progress" },
    ]);
    expect(t.observe(SID, { tool: "TaskUpdate", phase: "post", input: { taskId: "2", status: "completed" } })?.plan?.steps[1].status).toBe("done");
  });
  it("tasks after a TodoWrite list start a new plan, and a TodoWrite after tasks replaces them", () => {
    const t = tracker();
    t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("X", "pending")] } });
    expect(create(t, "A", "1")?.plan?.steps).toEqual([{ text: "A", status: "pending" }]);
    expect(t.observe(SID, { tool: "TodoWrite", phase: "pre", input: { todos: [todo("Y", "pending")] } })?.plan?.steps).toEqual([{ text: "Y", status: "pending" }]);
  });
});

describe("PlanTracker: Codex update_plan", () => {
  it("maps plan[{step, status}]", () => {
    const c = tracker().observe(SID, {
      tool: "update_plan",
      phase: "pre",
      input: { explanation: "x", plan: [{ step: "Checkout lesen", status: "completed" }, { step: "Summen fixen", status: "in_progress" }, { step: "Tests", status: "pending" }] },
    });
    expect(c?.plan?.steps).toEqual([
      { text: "Checkout lesen", status: "done" },
      { text: "Summen fixen", status: "in_progress" },
      { text: "Tests", status: "pending" },
    ]);
  });
  it("tolerates a JSON string as input and malformed entries", () => {
    const t = tracker();
    const c = t.observe(SID, { tool: "update_plan", phase: "post", input: JSON.stringify({ plan: [{ step: "A", status: "completed" }, { step: 1 }] }) });
    expect(c?.plan?.steps).toEqual([{ text: "A", status: "done" }]);
    expect(t.observe(SID, { tool: "update_plan", phase: "post", input: "{not json" })).toBeNull();
    expect(t.observe(SID, { tool: "update_plan", phase: "post", input: { plan: "x" } })).toBeNull();
  });
});

describe("PlanTracker: Gemini write_todos", () => {
  it("maps todos[{description, status}], cancelled and blocked become pending", () => {
    const c = tracker().observe(SID, {
      tool: "write_todos",
      phase: "pre",
      input: {
        todos: [
          { description: "Suche bauen", status: "completed" },
          { description: "Filter", status: "in_progress" },
          { description: "Alt", status: "cancelled" },
          { description: "Warten", status: "blocked" },
        ],
      },
    });
    expect(c?.plan?.steps).toEqual([
      { text: "Suche bauen", status: "done" },
      { text: "Filter", status: "in_progress" },
      { text: "Alt", status: "pending" },
      { text: "Warten", status: "pending" },
    ]);
  });
});

describe("PlanTracker: other tools", () => {
  it("ignores tools that are not plan tools", () => {
    expect(tracker().observe(SID, { tool: "Bash", phase: "pre", input: { todos: [todo("A", "pending")] } })).toBeNull();
  });
});

describe("capPlan", () => {
  it("re-applies the caps to a plan from a file and drops bad steps", () => {
    const steps = Array.from({ length: 14 }, (_, i) => ({ text: `S${i}`, status: "pending" as const }));
    const p = capPlan({ steps: [...steps, { text: 5, status: "done" } as never, { text: "x".repeat(200), status: "weird" } as never], more: 2 });
    expect(p?.steps).toHaveLength(12);
    expect(p?.more).toBe(2 + 2 + 1); // 2 recorded + 2 cut here + the long one (bad step dropped)
  });
  it("gives undefined for no steps", () => {
    expect(capPlan({ steps: [], more: 0 })).toBeUndefined();
    expect(capPlan(undefined)).toBeUndefined();
  });
});

describe("codexPlanCalls", () => {
  const line = (o: unknown) => JSON.stringify(o);
  it("reads update_plan function calls from rollout lines", () => {
    const text = [
      line({ timestamp: "2026-11-12T10:00:00Z", type: "session_meta", payload: { id: "x" } }),
      line({ timestamp: "2026-11-12T10:00:01Z", type: "response_item", payload: { type: "function_call", name: "update_plan", arguments: JSON.stringify({ plan: [{ step: "A", status: "in_progress" }] }), call_id: "c1" } }),
      line({ timestamp: "2026-11-12T10:00:02Z", type: "response_item", payload: { type: "function_call", name: "shell", arguments: "{}" } }),
      "{broken",
      line({ type: "response_item", payload: { type: "function_call", name: "update_plan", arguments: 5 } }),
    ].join("\n");
    const calls = codexPlanCalls(text);
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({ tool: "update_plan", phase: "post" });
    const c = tracker().observe(SID, calls[0]);
    expect(c?.plan?.steps).toEqual([{ text: "A", status: "in_progress" }]);
  });
  it("never throws", () => {
    expect(codexPlanCalls("")).toEqual([]);
    expect(codexPlanCalls("update_plan\n[1,2]\nnull\n\"update_plan\"")).toEqual([]);
  });
});

describe("PlanGate", () => {
  function gate() {
    let t = 1000;
    const timers: { at: number; fn: () => void; id: number }[] = [];
    let nextId = 1;
    const sent: { sid: string; plan: PlanSteps | undefined; t: number }[] = [];
    const g = new PlanGate({
      now: () => t,
      setTimer: (fn, ms) => {
        const id = nextId++;
        timers.push({ at: t + ms, fn, id });
        return id;
      },
      clearTimer: (h) => {
        const i = timers.findIndex((x) => x.id === h);
        if (i >= 0) timers.splice(i, 1);
      },
      emit: (sid, plan) => sent.push({ sid, plan, t }),
    });
    const advance = (ms: number) => {
      t += ms;
      for (;;) {
        const due = timers.filter((x) => x.at <= t).sort((a, b) => a.at - b.at)[0];
        if (!due) break;
        timers.splice(timers.indexOf(due), 1);
        due.fn();
      }
    };
    return { g, sent, advance, timers };
  }
  const p = (text: string): PlanSteps => ({ steps: [{ text, status: "pending" }], more: 0 });

  it("sends the first change at once", () => {
    const { g, sent } = gate();
    g.offer("a", p("1"));
    expect(sent).toEqual([{ sid: "a", plan: p("1"), t: 1000 }]);
  });
  it("sends at most one per agent per 500 ms, and the last state always goes out", () => {
    const { g, sent, advance } = gate();
    g.offer("a", p("1"));
    advance(100);
    g.offer("a", p("2"));
    advance(100);
    g.offer("a", p("3"));
    expect(sent.map((s) => s.plan?.steps[0].text)).toEqual(["1"]);
    advance(299);
    expect(sent).toHaveLength(1);
    advance(1);
    expect(sent.map((s) => [s.plan?.steps[0].text, s.t])).toEqual([
      ["1", 1000],
      ["3", 1500],
    ]);
    advance(10_000);
    expect(sent).toHaveLength(2);
  });
  it("a change after a quiet period goes out at once again", () => {
    const { g, sent, advance } = gate();
    g.offer("a", p("1"));
    advance(600);
    g.offer("a", p("2"));
    expect(sent.map((s) => s.t)).toEqual([1000, 1600]);
  });
  it("limits each agent on its own", () => {
    const { g, sent } = gate();
    g.offer("a", p("1"));
    g.offer("b", p("1"));
    expect(sent.map((s) => s.sid)).toEqual(["a", "b"]);
  });
  it("passes a cleared plan (undefined) like any other state", () => {
    const { g, sent, advance } = gate();
    g.offer("a", p("1"));
    g.offer("a", undefined);
    advance(500);
    expect(sent.at(-1)).toEqual({ sid: "a", plan: undefined, t: 1500 });
  });
  it("drop cancels a pending send and resets the window", () => {
    const { g, sent, advance, timers } = gate();
    g.offer("a", p("1"));
    g.offer("a", p("2"));
    g.drop("a");
    expect(timers).toHaveLength(0);
    advance(1000);
    expect(sent).toHaveLength(1);
    g.offer("a", p("3"));
    expect(sent).toHaveLength(2);
  });
  it("close cancels everything", () => {
    const { g, sent, advance, timers } = gate();
    g.offer("a", p("1"));
    g.offer("a", p("2"));
    g.close();
    expect(timers).toHaveLength(0);
    advance(1000);
    expect(sent).toHaveLength(1);
  });
});
