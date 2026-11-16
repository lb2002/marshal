import { mkdtempSync, readFileSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { Detectors } from "../src/detectors.js";
import type { Incident } from "../src/detectors.js";
import type { AgentEvent } from "../src/events.js";
import { readRecording } from "../src/recorder.js";
import { classifyRisk } from "../src/risk.js";
import { SessionStore } from "../src/state.js";

const FILE = resolve(import.meta.dirname, "../../demo/jury.ndjson");
const COLLISION_WINDOW_MS = 30_000; // same as COLLISION_WINDOW_MS in src/pipeline.ts
const BUDGET_USD = 15;
const DESTRUCTIVE = new Set(["rm -rf ../legacy-site", "git push --force origin main"]);

let root: string;
let events: AgentEvent[];

beforeAll(async () => {
  root = realpathSync(mkdtempSync(join(tmpdir(), "lotse-jury-")));
  events = await readRecording(FILE);
});
afterAll(() => rmSync(root, { recursive: true, force: true }));

const mapRepo = (v: string | undefined) => (v === "<repo>" ? root : v !== undefined && v.startsWith("<repo>/") ? join(root, v.slice("<repo>/".length)) : v);

describe("jury demo scenario", () => {
  it("is a valid, ordered recording of about 6-8 minutes with four agents", () => {
    expect(events.length).toBeGreaterThan(50);
    expect(events.map((e) => e.t)).toEqual([...events.map((e) => e.t)].sort((a, b) => a - b));
    const last = events[events.length - 1].t;
    expect(last).toBeGreaterThanOrEqual(360_000);
    expect(last).toBeLessThanOrEqual(480_000);
    expect(new Set(events.map((e) => e.sid)).size).toBe(4);
  });

  it("classifies every permission: two destructive incident commands, the rest safe or network", () => {
    const perms = events.filter((e) => e.kind === "permission");
    expect(perms.length).toBeGreaterThan(10);
    let destructive = 0;
    for (const e of perms) {
      const input: Record<string, unknown> = {};
      if (e.command !== undefined) input.command = e.command;
      const p = mapRepo(e.path);
      if (p !== undefined) input.file_path = p;
      const risk = classifyRisk(e.tool ?? "", input, mapRepo(e.cwd) ?? root).risk;
      if (e.command !== undefined && DESTRUCTIVE.has(e.command)) {
        expect(risk, e.command).toBe("destructive");
        destructive++;
      } else {
        expect(["safe", "network"], `${e.tool} ${e.command ?? e.path}`).toContain(risk);
      }
    }
    expect(destructive).toBe(2);
  });

  it("yields exactly one collision and one budget_near, no budget, and the first agent finishes early", () => {
    const store = new SessionStore();
    const detectors = new Detectors({ collisionWindowMs: COLLISION_WINDOW_MS, budgetUsd: BUDGET_USD });
    const incidents: Incident[] = [];
    for (const e of events) {
      store.apply(e);
      incidents.push(...detectors.check(e, store));
    }
    expect(incidents.filter((i) => i.kind === "collision")).toHaveLength(1);
    expect(incidents.filter((i) => i.kind === "budget_near")).toHaveLength(1);
    expect(incidents.filter((i) => i.kind === "budget")).toHaveLength(0);
    expect(store.all().every((a) => a.status === "done")).toBe(true);
    const stop1 = events.find((e) => e.sid === "demo-a1" && e.kind === "stop");
    expect(stop1?.t).toBeLessThanOrEqual(90_000);
  });

  it("contains no personal paths or secrets", () => {
    const lines = readFileSync(FILE, "utf8").split("\n").filter((l) => l.trim() !== "");
    for (const line of lines) {
      for (const bad of ["C:\\", "/Users/", "/home/", "sk-", "ghp_"]) expect(line.includes(bad), `${bad} in ${line}`).toBe(false);
    }
  });
});
