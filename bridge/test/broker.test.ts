import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PermissionBroker } from "../src/broker.js";

const CWD = process.cwd();
const allow = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };

function mk(hasClient = true) {
  return new PermissionBroker({ holdMs: 90_000, hasClient: () => hasClient });
}
const safeOpen = (b: PermissionBroker) => b.open("s1", "Read", { file_path: "a.ts" }, CWD);
const destructiveOpen = (b: PermissionBroker) => b.open("s1", "Bash", { command: "rm -rf /" }, CWD);

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); });

describe("PermissionBroker", () => {
  it("answers {} immediately when no client", async () => {
    const b = mk(false);
    const { answer } = safeOpen(b);
    await expect(answer).resolves.toEqual({});
    expect(b.pending()).toHaveLength(0);
  });

  it("allow safe with pinch-hold", async () => {
    const b = mk();
    const { request, answer } = safeOpen(b);
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: true });
    await expect(answer).resolves.toEqual(allow);
    expect(b.pending()).toHaveLength(0);
  });

  it("rejects allow without confirm and stays pending", async () => {
    const b = mk();
    const { request, answer } = safeOpen(b);
    expect(b.decide({ id: request.id, behavior: "allow" })).toEqual({ ok: false, error: "needs-pinch-hold" });
    expect(b.pending()).toHaveLength(1);
    b.decide({ id: request.id, behavior: "allow", confirm: "two-hand" });
    await expect(answer).resolves.toEqual(allow);
  });

  it("destructive needs two-hand", async () => {
    const b = mk();
    const { request, answer } = destructiveOpen(b);
    expect(request.risk.risk).toBe("destructive");
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "needs-two-hand" });
    expect(b.decide({ id: request.id, behavior: "allow" })).toEqual({ ok: false, error: "needs-two-hand" });
    expect(b.pending()).toHaveLength(1);
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "two-hand" })).toEqual({ ok: true });
    await expect(answer).resolves.toEqual(allow);
  });

  it("deny passes message and needs no confirm", async () => {
    const b = mk();
    const { request, answer } = destructiveOpen(b);
    b.decide({ id: request.id, behavior: "deny", message: "Nutze utils/helper.ts" });
    await expect(answer).resolves.toEqual({
      hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: "Nutze utils/helper.ts" } },
    });
  });

  it("allow never carries a message", async () => {
    const b = mk();
    const { request, answer } = safeOpen(b);
    b.decide({ id: request.id, behavior: "allow", confirm: "two-hand", message: "x" });
    await expect(answer).resolves.toEqual(allow);
  });

  it("times out to {} after 90000 ms and fires onClose", async () => {
    const b = mk();
    const closed: Array<[string, string]> = [];
    b.onClose((id, r) => closed.push([id, r]));
    const { request, answer } = safeOpen(b);
    vi.advanceTimersByTime(89_999);
    expect(b.pending()).toHaveLength(1);
    vi.advanceTimersByTime(1);
    await expect(answer).resolves.toEqual({});
    expect(closed).toEqual([[request.id, "timeout"]]);
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "two-hand" })).toEqual({ ok: false, error: "unknown" });
  });

  it("closeAll resolves every pending answer to {} with reason", async () => {
    const b = mk();
    const closed: string[] = [];
    b.onClose((_id, r) => closed.push(r));
    const a1 = safeOpen(b);
    const a2 = destructiveOpen(b);
    b.closeAll("disconnect");
    await expect(a1.answer).resolves.toEqual({});
    await expect(a2.answer).resolves.toEqual({});
    expect(closed).toEqual(["disconnect", "disconnect"]);
    expect(b.pending()).toHaveLength(0);
  });

  it("unknown id returns unknown; a request resolves only once", async () => {
    const b = mk();
    expect(b.decide({ id: "nope", behavior: "deny" })).toEqual({ ok: false, error: "unknown" });
    const { request } = safeOpen(b);
    b.decide({ id: request.id, behavior: "deny" });
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "two-hand" })).toEqual({ ok: false, error: "unknown" });
  });

  it("rejects invalid behavior values", () => {
    const b = mk();
    const { request } = safeOpen(b);
    expect(b.decide({ id: request.id, behavior: "yes" as never, confirm: "two-hand" })).toEqual({ ok: false, error: "unknown" });
    expect(b.pending()).toHaveLength(1);
  });

  it("ids are unique across 1000 opens", () => {
    const b = mk(false);
    const ids = new Set<string>();
    for (let i = 0; i < 1000; i++) ids.add(safeOpen(b).request.id);
    expect(ids.size).toBe(1000);
  });

  it("stores display-safe command and path only", () => {
    const b = mk();
    const long = "echo " + "x".repeat(500);
    const r1 = b.open("s", "Bash", { command: long, secret: "s3" }, CWD).request;
    expect(r1.command).toHaveLength(300);
    expect(r1.path).toBeUndefined();
    const r2 = b.open("s", "Write", { file_path: "/a/b.ts", content: "SECRET" }, CWD).request;
    expect(r2.command).toBeUndefined();
    expect(r2.path).toBe("/a/b.ts");
    expect(JSON.stringify(r2)).not.toContain("SECRET");
    expect(b.open("s", "NotebookEdit", { notebook_path: "n.ipynb" }, CWD).request.path).toBe("n.ipynb");
    expect(b.open("s", "Grep", { path: "src" }, CWD).request.path).toBe("src");
    expect(b.open("s", "Bash", { command: 5 }, CWD).request.command).toBeUndefined();
  });

  it("shows command text for every shell-like tool", () => {
    const b = mk();
    expect(b.open("s", "PowerShell", { command: "Remove-Item x" }, CWD).request.command).toBe("Remove-Item x");
    expect(b.open("s", "mcp__srv__run", { command: ["rm", "-rf", "/"] }, CWD).request.command).toBe("rm -rf /");
    expect(b.open("s", "mcp__srv__run", { command: "y".repeat(400) }, CWD).request.command).toHaveLength(300);
    expect(b.open("s", "mcp__srv__run", { command: [1, 2] }, CWD).request.command).toBeUndefined();
    expect(b.open("s", "Read", { file_path: "a" }, CWD).request.command).toBeUndefined();
  });

  it("truncated command is flagged and needs two-hand even when low risk", async () => {
    const b = mk();
    const { request, answer } = b.open("s", "mcp__x__run", { command: "z".repeat(301) }, CWD);
    expect(request.truncated).toBe(true);
    expect(request.commandLength).toBe(301);
    expect(request.command).toHaveLength(300);
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "needs-two-hand" });
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "two-hand" })).toEqual({ ok: true });
    await expect(answer).resolves.toEqual(allow);
  });

  it("untruncated command is not flagged; length reported", () => {
    const r = mk().open("s", "Bash", { command: "ls" }, CWD).request;
    expect([r.truncated, r.commandLength]).toEqual([false, 2]);
  });

  it("tool name case does not matter for display fields", () => {
    const b = mk();
    expect(b.open("s", "BASH", { command: "ls" }, CWD).request.command).toBe("ls");
    expect(b.open("s", "powershell", { command: "dir" }, CWD).request.command).toBe("dir");
  });

  it("exposes classifier path fields (max 5, in order) and url", () => {
    const r = mk().open("s", "Write", { file_path: "a", destination: "b", to: "c", outputPath: "d", target: "e", dest: "f", url: "http://x/" + "u".repeat(400) }, CWD).request;
    expect(r.path).toBe("a");
    expect(r.paths).toEqual(["a", "b", "f", "e", "c"]);
    expect(r.url).toHaveLength(300);
    expect(r.truncated).toBe(true);
    expect(mk().open("s", "Bash", { command: "ls" }, CWD).request.paths).toEqual([]);
  });

  it("network class accepts pinch-hold", async () => {
    const b = mk();
    const { request, answer } = b.open("s", "WebFetch", { url: "https://example.com" }, CWD);
    expect(request.risk.risk).toBe("network");
    expect(request.url).toBe("https://example.com");
    expect(b.decide({ id: request.id, behavior: "allow" })).toEqual({ ok: false, error: "needs-pinch-hold" });
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: true });
    await expect(answer).resolves.toEqual(allow);
  });

  it("mutating the request or its risk cannot lower the gesture", () => {
    const b = mk();
    const { request } = destructiveOpen(b);
    expect(Object.isFrozen(request)).toBe(true);
    expect(Object.isFrozen(request.risk)).toBe(true);
    expect(() => { (request.risk as { risk: string }).risk = "safe"; }).toThrow();
    expect(b.pending()[0]!.risk.risk).toBe("destructive");
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "needs-two-hand" });
  });

  it("type-confused decisions never allow", () => {
    const b = mk();
    const { request } = safeOpen(b);
    const bad: unknown[] = [
      { id: request.id, behavior: "allow", confirm: ["two-hand"] },
      { id: request.id, behavior: "allow", confirm: "TWO-HAND" },
      { id: request.id, behavior: "allow", confirm: 2 },
      { id: request.id, behavior: ["allow"], confirm: "two-hand" },
      { id: {}, behavior: "allow", confirm: "two-hand" },
    ];
    for (const d of bad) expect(b.decide(d as never).ok).toBe(false);
    expect(b.pending()).toHaveLength(1);
  });

  it("deny message is sanitised and capped", async () => {
    const mkDeny = async (message: unknown) => {
      const b = mk();
      const { request, answer } = safeOpen(b);
      b.decide({ id: request.id, behavior: "deny", message: message as string });
      return (await answer) as { hookSpecificOutput: { decision: { message?: string } } };
    };
    const long = await mkDeny("a".repeat(900));
    expect(long.hookSpecificOutput.decision.message).toHaveLength(500);
    const ctl = await mkDeny("  hi\u0000\u001b there\nok\t\u0085 ");
    expect(ctl.hookSpecificOutput.decision.message).toBe("hi there\nok");
    expect((await mkDeny("   \u0000 ")).hookSpecificOutput.decision).toEqual({ behavior: "deny" });
    expect((await mkDeny(42)).hookSpecificOutput.decision).toEqual({ behavior: "deny" });
  });

  it("no timers leak after any resolution path", async () => {
    safeOpen(mk(false));
    expect(vi.getTimerCount()).toBe(0);
    const b = mk();
    const a = safeOpen(b).request;
    expect(vi.getTimerCount()).toBe(1);
    b.decide({ id: a.id, behavior: "allow", confirm: "two-hand" });
    expect(vi.getTimerCount()).toBe(0);
    b.decide({ id: safeOpen(b).request.id, behavior: "deny" });
    expect(vi.getTimerCount()).toBe(0);
    safeOpen(b);
    safeOpen(b);
    b.closeAll("x");
    expect(vi.getTimerCount()).toBe(0);
    const { answer } = safeOpen(b);
    vi.advanceTimersByTime(90_000);
    await answer;
    expect(vi.getTimerCount()).toBe(0);
  });

  it("onOpen/onClose return working unsubscribe functions", () => {
    const b = mk();
    const seen: string[] = [];
    const off = b.onOpen((r) => seen.push(r.id));
    const offC = b.onClose((id) => seen.push("c" + id));
    const r1 = safeOpen(b).request;
    off();
    safeOpen(b);
    offC();
    b.decide({ id: r1.id, behavior: "deny" });
    expect(seen).toEqual([r1.id]);
  });

  it("sets createdAt/expiresAt from injected clock", () => {
    const b = new PermissionBroker({ holdMs: 1000, now: () => 5000, hasClient: () => true });
    const { request } = safeOpen(b);
    expect([request.createdAt, request.expiresAt]).toEqual([5000, 6000]);
  });

  it("supports multiple listeners; a throwing one breaks nothing", async () => {
    const b = mk();
    const seen: string[] = [];
    b.onOpen(() => { throw new Error("boom"); });
    b.onOpen((r) => seen.push(r.id));
    b.onClose(() => { throw new Error("boom"); });
    b.onClose((id) => seen.push("c" + id));
    const { request, answer } = safeOpen(b);
    b.decide({ id: request.id, behavior: "deny" });
    await answer;
    expect(seen).toEqual([request.id, "c" + request.id]);
  });
  it("cancel resolves {} once, fires onClose with the reason and blocks a later decide", async () => {
    const b = mk();
    const closed: Array<[string, string]> = [];
    b.onClose((id, reason) => closed.push([id, reason]));
    const { request, answer } = safeOpen(b);
    expect(b.cancel(request.id, "hook-gone")).toBe(true);
    await expect(answer).resolves.toEqual({});
    expect(closed).toEqual([[request.id, "hook-gone"]]);
    expect(b.pending()).toHaveLength(0);
    expect(b.cancel(request.id, "again")).toBe(false);
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "unknown" });
    expect(closed).toHaveLength(1);
  });

  it("cancel of an unknown id returns false and leaves other requests pending", () => {
    const b = mk();
    const { request } = safeOpen(b);
    expect(b.cancel("nope", "x")).toBe(false);
    expect(b.pending().map((r) => r.id)).toEqual([request.id]);
  });
});
