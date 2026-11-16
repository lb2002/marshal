// Task p3, item 2: an oversized command (or any other oversized string field) is not lexed at all. It is
// destructive with a fixed reason, in constant time, so a forged request cannot stall the event loop that also
// answers permission requests and halt.
import { describe, expect, it } from "vitest";
import { renderArgv } from "../src/argv.js";
import { PermissionBroker } from "../src/broker.js";
import { classifyRisk, MAX_CLASSIFIED } from "../src/risk.js";

const TOO_LONG = { risk: "destructive", reason: "Command too long, not checked" };
const MB = 1024 * 1024;

/** Milliseconds of the fastest of 3 runs (a loaded machine slows one run, not all three). */
function best(run: () => void): number {
  let b = Infinity;
  for (let i = 0; i < 3; i++) {
    const t = performance.now();
    run();
    b = Math.min(b, performance.now() - t);
  }
  return b;
}

// Shapes that are expensive to lex (the measured worst cases at 1 MB: a here-document, many targets, pipes).
const big = (n: number): string[] => [
  "cat <<E\n" + "x\n".repeat(Math.floor(n / 2)),
  "rm -rf" + " a".repeat(Math.floor(n / 2)),
  "ls" + " | x".repeat(Math.floor(n / 4)),
];

describe("oversized input is not lexed", () => {
  it("the cap is at most 64 KB", () => {
    expect(MAX_CLASSIFIED).toBeLessThanOrEqual(64 * 1024);
    expect(MAX_CLASSIFIED).toBeGreaterThanOrEqual(4096);
  });

  it("a 1 MB and a 10 MB command are destructive with the fixed reason, in constant time", () => {
    for (const n of [MB, 10 * MB]) {
      for (const command of big(n)) {
        for (const [tool, source] of [["Bash", "claude"], ["Bash", "codex"], ["PowerShell", undefined]] as const) {
          let r: unknown;
          const ms = best(() => (r = classifyRisk(tool, { command }, "C:\\proj", source, "win32")));
          expect(r).toEqual(TOO_LONG);
          expect(ms, `${tool} ${source} ${n}`).toBeLessThan(10);
        }
      }
    }
  });

  it("an argument array counts by its rendered length", () => {
    const at = MAX_CLASSIFIED;
    // Joined it fits, rendered (every quote takes four characters) it does not.
    const quotes = ["echo", "'".repeat(Math.floor(at / 3))];
    expect(quotes.join(" ").length).toBeLessThanOrEqual(at);
    expect(renderArgv(quotes).length).toBeGreaterThan(at);
    expect(classifyRisk("exec_command", { cmd: quotes }, "/repo", "codex", "linux")).toEqual(TOO_LONG);
    for (const argv of [["bash", "-lc", big(10 * MB)[0]], Array.from({ length: MB }, () => "abcdefghi")]) {
      let r: unknown;
      expect(best(() => (r = classifyRisk("mcp__srv__run", { command: argv }, "/repo", undefined, "linux")))).toBeLessThan(10);
      expect(r).toEqual(TOO_LONG);
    }
  });

  it("any other string field over the cap is not checked either", () => {
    const long = "a".repeat(MAX_CLASSIFIED + 1);
    expect(classifyRisk("Write", { file_path: long }, "/repo", undefined, "linux")).toEqual(TOO_LONG);
    expect(classifyRisk("Bash", { command: "ls", description: long }, "/repo", undefined, "linux")).toEqual(TOO_LONG);
    expect(classifyRisk("exec_command", { cmd: long }, "/repo", "codex", "linux")).toEqual(TOO_LONG);
    expect(classifyRisk("apply_patch", { command: long }, "/repo", "codex", "linux")).toEqual(TOO_LONG);
    expect(classifyRisk(long, {}, "/repo", undefined, "linux")).toEqual(TOO_LONG);
  });

  it("up to the cap the command is classified as usual", () => {
    const exact = "ls " + "a".repeat(MAX_CLASSIFIED - 3);
    expect(exact.length).toBe(MAX_CLASSIFIED);
    expect(classifyRisk("Bash", { command: exact }, "/repo", undefined, "linux").risk).toBe("safe");
    expect(classifyRisk("Bash", { command: exact + "a" }, "/repo", undefined, "linux")).toEqual(TOO_LONG);
  });

  it("the broker answers at once, shows a cut command and demands two hands", () => {
    const b = new PermissionBroker({ holdMs: 90_000, hasClient: () => true });
    for (const command of [big(MB)[2], "ls " + "a".repeat(10 * MB), ["ls", "a".repeat(10 * MB)]]) {
      const t = performance.now();
      const { request } = b.open("s", "Bash", { command }, "C:\\proj", "codex");
      expect(performance.now() - t).toBeLessThan(50);
      expect(request.risk).toEqual(TOO_LONG);
      expect(request.truncated).toBe(true);
      expect(request.command).toHaveLength(300);
      expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "needs-two-hand" });
    }
  });

  it("a burst of 20 such requests keeps the event loop responsive", async () => {
    const b = new PermissionBroker({ holdMs: 90_000, hasClient: () => true });
    const commands: (string | string[])[] = [...big(MB), ...big(10 * MB), ["bash", "-lc", big(MB)[0]]];
    let maxGap = 0;
    let last = performance.now();
    const timer = setInterval(() => {
      const t = performance.now();
      maxGap = Math.max(maxGap, t - last);
      last = t;
    }, 2);
    // Each request in its own macrotask, as HTTP requests arrive.
    for (let k = 0; k < 20; k++) {
      await new Promise<void>((r) => setImmediate(r));
      const { request } = b.open("s", "Bash", { command: commands[k % commands.length] }, "C:\\proj", k % 2 ? "codex" : "claude");
      expect(request.risk).toEqual(TOO_LONG);
    }
    await new Promise((r) => setTimeout(r, 20));
    maxGap = Math.max(maxGap, performance.now() - last);
    clearInterval(timer);
    b.closeAll("test");
    expect(maxGap).toBeLessThan(50);
  });
});
