import { describe, expect, it } from "vitest";
import { SessionStore } from "../src/state.js";
import { normalizePath } from "../src/risk/paths.js";
import { Detectors } from "../src/detectors.js";
import type { AgentEvent } from "../src/events.js";

function setup() {
  const store = new SessionStore();
  const det = new Detectors({ collisionWindowMs: 30000, budgetUsd: 5 });
  const feed = (e: AgentEvent) => {
    store.apply(e);
    return det.check(e, store);
  };
  return { feed };
}
const edit = (sid: string, t: number, path: string): AgentEvent => ({ sid, t, kind: "tool_start", tool: "Edit", path });

describe("Detectors", () => {
  it("flags two sids editing the same path within the window", () => {
    const { feed } = setup();
    expect(feed(edit("a", 0, "src/a.ts"))).toEqual([]);
    const inc = feed(edit("b", 10000, "src/a.ts"));
    expect(inc).toHaveLength(1);
    expect(inc[0].kind).toBe("collision");
    expect([...inc[0].sids].sort()).toEqual(["a", "b"]);
    expect(inc[0].t).toBe(10000);
    expect(inc[0].detail).toContain("src/a.ts");
  });

  it("matches paths across separators and Windows case", () => {
    const { feed } = setup();
    feed(edit("a", 0, "C:\\p\\src\\a.ts"));
    expect(feed(edit("b", 1000, "c:/P/src/A.ts"))).toHaveLength(1);
  });

  it("does not flag edits further apart than the window", () => {
    const { feed } = setup();
    feed(edit("a", 0, "src/a.ts"));
    expect(feed(edit("b", 40000, "src/a.ts"))).toEqual([]);
  });

  it("does not flag the same sid or different paths", () => {
    const { feed } = setup();
    feed(edit("a", 0, "src/a.ts"));
    expect(feed(edit("a", 1000, "src/a.ts"))).toEqual([]);
    expect(feed(edit("b", 2000, "src/b.ts"))).toEqual([]);
  });

  it("ignores reads", () => {
    const { feed } = setup();
    feed(edit("a", 0, "src/a.ts"));
    expect(feed({ sid: "b", t: 1000, kind: "tool_start", tool: "Read", path: "src/a.ts" })).toEqual([]);
  });

  it("flags budget once per sid", () => {
    const { feed } = setup();
    expect(feed({ sid: "a", t: 1, kind: "status", costUsd: 3.4 })).toEqual([]);
    expect(feed({ sid: "a", t: 1, kind: "status", costUsd: 4.9 }).map((i) => i.kind)).toEqual(["budget_near"]);
    const inc = feed({ sid: "a", t: 2, kind: "status", costUsd: 5 });
    expect(inc).toHaveLength(1);
    expect(inc[0]).toMatchObject({ kind: "budget", sids: ["a"], t: 2 });
    expect(feed({ sid: "a", t: 3, kind: "status", costUsd: 6 })).toEqual([]);
    expect(feed({ sid: "b", t: 4, kind: "status", costUsd: 6 })).toHaveLength(1);
  });

  it("F6: budget_near once per session at 70 %, then the at-budget incident", () => {
    const store = new SessionStore();
    const det = new Detectors({ collisionWindowMs: 30000, budgetUsd: 15 });
    const feed = (sid: string, costUsd: number) => {
      const e: AgentEvent = { sid, t: 1, kind: "status", costUsd };
      store.apply(e);
      return det.check(e, store);
    };
    expect(feed("a", 10.49)).toEqual([]);
    const near = feed("a", 10.5);
    expect(near).toEqual([{ kind: "budget_near", sids: ["a"], detail: "Agent 1: 10.50 USD of 15.00 USD budget (70%)", t: 1 }]);
    expect(feed("a", 12)).toEqual([]);
    expect(feed("a", 15).map((i) => i.kind)).toEqual(["budget"]);
    expect(feed("a", 16)).toEqual([]);
    // a session that jumps straight past the budget gets only the at-budget incident
    expect(feed("b", 15).map((i) => i.kind)).toEqual(["budget"]);
    expect(feed("b", 15.5)).toEqual([]);
  });

  it("keeps the home directory out of the collision detail", () => {
    const { feed } = setup();
    feed(edit("a", 0, "C:\\Users\\linus\\p\\src\\a.ts"));
    const inc = feed(edit("b", 1000, "C:\\Users\\linus\\p\\src\\a.ts"));
    expect(inc[0].detail).toContain("src/a.ts");
    expect(inc[0].detail).not.toContain("linus");
  });

  it("flags a pair once while the window is open (A,B,A,B)", () => {
    const { feed } = setup();
    const n = [feed(edit("a", 0, "x.ts")), feed(edit("b", 1000, "x.ts")), feed(edit("a", 2000, "x.ts")), feed(edit("b", 3000, "x.ts"))].flat();
    expect(n).toHaveLength(1);
  });

  it("flags the pair again after the window expired", () => {
    const { feed } = setup();
    feed(edit("a", 0, "x.ts"));
    expect(feed(edit("b", 1000, "x.ts"))).toHaveLength(1);
    expect(feed(edit("a", 50000, "x.ts"))).toEqual([]);
    expect(feed(edit("b", 51000, "x.ts"))).toHaveLength(1);
  });

  it("window boundary is inclusive", () => {
    const { feed } = setup();
    feed(edit("a", 0, "x.ts"));
    expect(feed(edit("b", 30000, "x.ts"))).toHaveLength(1);
    const o = setup();
    o.feed(edit("a", 0, "x.ts"));
    expect(o.feed(edit("b", 30001, "x.ts"))).toEqual([]);
  });
});

describe("normalizePath", () => {
  it("folds separators and Windows case", () => {
    expect(normalizePath("C:\\P\\Src\\A.ts")).toBe(normalizePath("c:/p/src/a.ts"));
  });
  it("handles UNC paths", () => {
    expect(normalizePath("\\\\Srv\\Share\\a\\b.ts")).toBe(normalizePath("//srv/share/a/b.ts"));
  });
  it("resolves .. and . segments", () => {
    expect(normalizePath("C:/p/src/../lib/./a.ts")).toBe(normalizePath("c:/p/lib/a.ts"));
    expect(normalizePath("/p/src/../lib/a.ts")).toBe("/p/lib/a.ts");
  });
  it("drops trailing dots and spaces of Windows segments", () => {
    expect(normalizePath("C:/p/a.ts.")).toBe(normalizePath("C:/p/a.ts"));
    expect(normalizePath("C:/p/a.ts ")).toBe(normalizePath("C:/p/a.ts"));
  });
  it("keeps POSIX paths case-sensitive", () => {
    expect(normalizePath("/p/A.ts")).not.toBe(normalizePath("/p/a.ts"));
  });
});
