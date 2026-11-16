import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { replay } from "../src/replay.js";
import type { AgentEvent } from "../src/events.js";

const evs: AgentEvent[] = [0, 1500, 4000].map((t) => ({ sid: "s", t, kind: "tool_start" }));

describe("replay", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("emits at t/speed", () => {
    const at: number[] = [];
    const start = Date.now();
    replay(evs, () => at.push(Date.now() - start), { speed: 2 });
    vi.advanceTimersByTime(3000);
    expect(at).toEqual([0, 750, 2000]);
  });
  it("cancel stops further emits", () => {
    const got: number[] = [];
    const cancel = replay(evs, (e) => got.push(e.t), { speed: 1 });
    vi.advanceTimersByTime(1500);
    cancel();
    vi.advanceTimersByTime(10_000);
    expect(got).toEqual([0, 1500]);
    expect(vi.getTimerCount()).toBe(0);
  });
  it("cancel also guards injected timers", () => {
    const queued: (() => void)[] = [];
    const got: number[] = [];
    const cancel = replay(evs, (e) => got.push(e.t), { speed: 1, setTimer: (fn) => queued.push(fn) });
    cancel();
    queued.forEach((f) => f());
    expect(got).toEqual([]);
  });
  it("rejects non-positive speed", () => {
    for (const speed of [0, -1, NaN]) expect(() => replay(evs, () => {}, { speed })).toThrow(/speed/);
  });
});
