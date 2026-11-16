import { describe, expect, it } from "vitest";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { normalizeHook, type AgentEvent } from "../src/events.js";
import { SessionStore } from "../src/state.js";
import { Recorder, readRecording } from "../src/recorder.js";
import { createPipeline } from "../src/pipeline.js";
import { tempDir } from "./tmp.js";

const ev = (e: Partial<AgentEvent> & Pick<AgentEvent, "kind">): AgentEvent => ({ sid: "a", t: 1000, ...e });

describe("AgentEvent.source", () => {
  it("is read from lotse_source for the three other tools", () => {
    for (const s of ["muse", "codex", "gemini"] as const) {
      expect(normalizeHook({ hook_event_name: "Stop", session_id: "x", lotse_source: s }, 1)).toEqual({ sid: "x", t: 1, kind: "stop", source: s });
    }
  });
  it("is left out for Claude and for anything unknown (Claude is the default)", () => {
    for (const v of [undefined, "claude", "Muse", "other", 3, null]) {
      expect(normalizeHook({ hook_event_name: "Stop", session_id: "x", lotse_source: v }, 1)).toEqual({ sid: "x", t: 1, kind: "stop" });
    }
  });
});

describe("SessionStore names by source", () => {
  it("counts each tool on its own; Claude keeps Agent N", () => {
    const s = new SessionStore();
    expect(s.apply(ev({ sid: "c1", kind: "session_start" }))).toMatchObject({ name: "Agent 1", source: "claude" });
    expect(s.apply(ev({ sid: "m1", kind: "session_start", source: "muse" }))).toMatchObject({ name: "Muse 1", source: "muse" });
    expect(s.apply(ev({ sid: "m2", kind: "prompt", source: "muse" }))?.name).toBe("Muse 2");
    expect(s.apply(ev({ sid: "x1", kind: "prompt", source: "codex" }))?.name).toBe("Codex 1");
    expect(s.apply(ev({ sid: "g1", kind: "prompt", source: "gemini" }))?.name).toBe("Gemini 1");
    expect(s.apply(ev({ sid: "c2", kind: "prompt" }))?.name).toBe("Agent 2");
  });
  it("keeps the source of the first event of a session", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "session_start", source: "codex" }));
    expect(s.apply(ev({ kind: "prompt" }))).toMatchObject({ name: "Codex 1", source: "codex" });
  });
  it("the pipeline's agent message carries the source", () => {
    const p = createPipeline({ budgetUsd: 5, collisionWindowMs: 1000 });
    expect(p.ingest(ev({ kind: "prompt", source: "gemini" }))[0]).toMatchObject({ type: "agent", agent: { source: "gemini", name: "Gemini 1" } });
  });
  it("a PowerShell call shows the program name and detects tests like Bash", () => {
    const s = new SessionStore();
    expect(s.apply(ev({ kind: "tool_start", tool: "PowerShell", command: "git status" }))?.lastAction).toBe("PowerShell: git");
    expect(s.apply(ev({ kind: "tool_start", tool: "PowerShell", command: "npm test" }))?.status).toBe("testing");
  });
});

describe("recordings carry the source", () => {
  const dir = tempDir("lotse-src-");
  it("round-trips through the recorder", async () => {
    const file = join(dir, "r.ndjson");
    const r = new Recorder(file, { repoRoot: dir, homeDir: join(dir, "home") });
    r.write(ev({ kind: "prompt", source: "muse" }));
    r.write(ev({ sid: "b", kind: "prompt" }));
    await r.close();
    const got = await readRecording(file);
    expect(got[0].source).toBe("muse");
    expect(got[1].source).toBeUndefined();
  });
  it("rejects an unknown source in a recording", async () => {
    const file = join(dir, "bad.ndjson");
    writeFileSync(file, JSON.stringify({ sid: "a", t: 0, kind: "prompt", source: "skynet" }) + "\n");
    await expect(readRecording(file)).rejects.toThrow(/line 1: source/);
  });
});
