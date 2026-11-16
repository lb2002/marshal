import { describe, expect, it } from "vitest";
import { SessionStore } from "../src/state.js";
import type { AgentEvent } from "../src/events.js";

const ev = (e: Partial<AgentEvent> & Pick<AgentEvent, "kind">): AgentEvent => ({ sid: "a", t: 1000, ...e });

describe("SessionStore", () => {
  it("names agents in order of first appearance", () => {
    const s = new SessionStore();
    expect(s.apply(ev({ kind: "session_start" }))?.name).toBe("Agent 1");
    expect(s.apply(ev({ sid: "b", kind: "session_start" }))?.name).toBe("Agent 2");
    expect(s.apply(ev({ kind: "prompt" }))?.name).toBe("Agent 1");
    expect(s.all().map((a) => a.sid)).toEqual(["a", "b"]);
  });

  it("creates the agent for an unknown sid on any event", () => {
    const s = new SessionStore();
    const a = s.apply(ev({ sid: "x", kind: "tool_start", tool: "Read", path: "src/a.ts" }));
    expect(a?.name).toBe("Agent 1");
    expect(a?.status).toBe("reading");
    expect(s.get("x")).toBeDefined();
  });

  it.each(["Read", "Glob", "Grep"])("%s gives reading", (tool) => {
    const s = new SessionStore();
    expect(s.apply(ev({ kind: "tool_start", tool, path: "src/a.ts" }))?.status).toBe("reading");
  });

  it("Read lastAction uses the last two path segments", () => {
    const s = new SessionStore();
    const a = s.apply(ev({ kind: "tool_start", tool: "Read", path: "C:\\p\\src\\a.ts" }));
    expect(a?.lastAction).toBe("Reads src/a.ts");
  });

  it("Edit/Write give editing and track files without duplicates, max 20", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "tool_start", tool: "Edit", path: "src/a.ts" }));
    const a = s.apply(ev({ kind: "tool_start", tool: "Write", path: "src/a.ts" }));
    expect(a?.status).toBe("editing");
    expect(a?.files).toEqual(["src/a.ts"]);
    expect(a?.lastAction).toBe("Edits src/a.ts");
    for (let i = 0; i < 30; i++) s.apply(ev({ kind: "tool_start", tool: "Edit", path: `f${i}.ts` }));
    expect(s.get("a")?.files).toHaveLength(20);
  });

  it.each(["npm test", "pytest -q", "npx vitest run", "jest", "go test ./...", "cargo test", "pnpm test", "yarn test", "npm run test"])(
    "Bash %s gives testing",
    (command) => {
      const s = new SessionStore();
      const a = s.apply(ev({ kind: "tool_start", tool: "Bash", command }));
      expect(a?.status).toBe("testing");
      expect(a?.lastAction).toBe("Runs tests");
    },
  );

  it("other Bash gives working", () => {
    const s = new SessionStore();
    expect(s.apply(ev({ kind: "tool_start", tool: "Bash", command: "ls -la" }))?.status).toBe("working");
  });

  it("permission gives blocked", () => {
    const s = new SessionStore();
    const a = s.apply(ev({ kind: "permission", tool: "Bash" }));
    expect(a?.status).toBe("blocked");
    expect(a?.lastAction).toBe("Waiting for approval: Bash");
  });

  it.each(["idle_prompt", "agent_needs_input"])("notify %s gives waiting", (notifyType) => {
    const s = new SessionStore();
    const a = s.apply(ev({ kind: "notify", notifyType }));
    expect(a?.status).toBe("waiting");
    expect(a?.lastAction).toBe("Waiting for input");
  });

  it("stop gives done with the summary, stop_fail gives failed", () => {
    const s = new SessionStore();
    const a = s.apply(ev({ kind: "stop", summary: "All green" }));
    expect(a?.status).toBe("done");
    expect(a?.lastAction).toBe("All green");
    const b = s.apply(ev({ sid: "b", kind: "stop_fail" }));
    expect(b?.status).toBe("failed");
    expect(b?.lastAction).toBe("Failed");
  });

  it("status updates ctxPct/costUsd without changing status", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "tool_start", tool: "Read", path: "a.ts", t: 1000 }));
    const since = s.get("a")!.since;
    const a = s.apply(ev({ kind: "status", ctxPct: 42, costUsd: 1.5, t: 2000 }));
    expect(a?.status).toBe("reading");
    expect(a?.ctxPct).toBe(42);
    expect(a?.costUsd).toBe(1.5);
    expect(a?.since).toBe(since);
  });

  it("sets since when the status changes", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "tool_start", tool: "Read", path: "a.ts", t: 1000 }));
    expect(s.apply(ev({ kind: "tool_start", tool: "Edit", path: "a.ts", t: 5000 }))?.since).toBe(5000);
  });

  it("accumulates waiting time after permission", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "permission", tool: "Bash", t: 1000 }));
    const a = s.apply(ev({ kind: "tool_start", tool: "Bash", command: "ls", t: 31000 }));
    expect(a?.waitingMsTotal).toBe(30000);
  });

  it("accumulates waiting time after waiting", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "notify", notifyType: "idle_prompt", t: 1000 }));
    const a = s.apply(ev({ kind: "prompt", t: 11000 }));
    expect(a?.waitingMsTotal).toBe(10000);
  });

  it("keeps secrets, URLs and paths of Bash commands out of lastAction", () => {
    const s = new SessionStore();
    const a = s.apply(ev({ kind: "tool_start", tool: "Bash", command: 'curl -H "Authorization: Bearer sk-ant-xyz" https://x.example/y' }));
    expect(a?.status).toBe("working");
    expect(a?.lastAction).toBe("Bash: curl");
    expect(a?.lastAction).not.toContain("sk-ant");
    expect(a?.lastAction).not.toContain("https");
    const b = s.apply(ev({ kind: "tool_start", tool: "Bash", command: "C:\\Users\\linus\\tool.exe --x" }));
    expect(b?.lastAction).not.toContain("linus");
  });

  it("dedupes files by normalised path and stores the short form", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "tool_start", tool: "Edit", path: "C:\\Users\\linus\\p\\src\\a.ts" }));
    s.apply(ev({ kind: "tool_start", tool: "Edit", path: "c:/users/linus/p/src/a.ts" }));
    expect(s.get("a")?.files).toEqual(["src/a.ts"]);
  });

  it("session_end gives done but keeps failed", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "tool_start", tool: "Read", path: "a.ts" }));
    expect(s.apply(ev({ kind: "session_end" }))?.status).toBe("done");
    s.apply(ev({ sid: "b", kind: "stop_fail" }));
    expect(s.apply(ev({ sid: "b", kind: "session_end" }))?.status).toBe("failed");
  });

  it("tool_end releases a blocked agent to working and counts the wait", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "permission", tool: "Bash", t: 1000 }));
    const a = s.apply(ev({ kind: "tool_end", t: 4000 }));
    expect(a?.status).toBe("working");
    expect(a?.waitingMsTotal).toBe(3000);
  });

  it("status events leave a blocked agent blocked and waitingMsTotal untouched", () => {
    const s = new SessionStore();
    s.apply(ev({ kind: "permission", tool: "Bash", t: 1000 }));
    const a = s.apply(ev({ kind: "status", ctxPct: 5, t: 9000 }));
    expect(a?.status).toBe("blocked");
    expect(a?.waitingMsTotal).toBe(0);
    expect(s.apply(ev({ kind: "prompt", t: 11000 }))?.waitingMsTotal).toBe(10000);
  });
});
