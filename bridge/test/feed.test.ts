// The feed parsers: session log lines of each tool become capped, redacted FeedEntries with stable ids.
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { feedEntries, MAX_FEED_LABEL, MAX_FEED_TEXT, type FeedEntry } from "../src/feed.js";
import type { AgentSource } from "../src/events.js";

const fixture = (name: string) => readFileSync(join(import.meta.dirname, "fixtures", "feed", `${name}.jsonl`));
const ctx = (source: AgentSource) => ({ sid: "s1", source, homeDir: "/home/linus", now: 1 });
const parse = (source: AgentSource, buf: Buffer = fixture(source), start = 0) => feedEntries(buf, start, ctx(source));
const brief = (es: FeedEntry[]) => es.map((e) => [e.kind, e.label, e.text, e.tool ?? null]);

describe("feedEntries: Claude Code transcript", () => {
  const es = parse("claude");
  it("turns prompt, thinking, text, tools, results and system lines into entries, skipping bookkeeping and the bad line", () => {
    expect(brief(es)).toEqual([
      ["prompt", "Your task", "Fix the cart badge in ~/shop/src/cart.ts please", null],
      ["thinking", "thinks", "The badge count is off by one.", null],
      ["text", "says", "I'll look at the cart first.", null],
      ["tool", "reads", "~/shop/src/cart.ts", "Read"],
      ["result", "result", "export const count = items.length + 1;", null],
      ["tool", "tests", "npm test -- cart", "Bash"],
      ["error", "error", "1 failed: expected 2", null],
      ["tool", "command", expect.stringContaining("[REDACTED]"), "Bash"],
      ["system", "System", "Conversation compacted", null],
    ]);
    expect(JSON.stringify(es)).not.toContain("sk-ant-api03");
  });
  it("takes the record time", () => {
    expect(es[0].t).toBe(Date.parse("2026-11-12T08:00:00.000Z"));
  });
});

describe("feedEntries: Codex rollout", () => {
  it("reads response items, marks environment context as system and a failed command as error", () => {
    expect(brief(parse("codex"))).toEqual([
      ["system", "System", "<environment_context>cwd</environment_context>", null],
      ["prompt", "Your task", "Add a price filter to the search", null],
      ["thinking", "thinks", "Looking for the search module", null],
      ["tool", "command", "bash -lc 'rg price src'", "shell"], // an argument array: each element one word (argv.ts)
      ["result", "result", "src/search.ts: price", null],
      ["text", "says", "The filter is in place.", null],
      ["error", "error", "boom", null],
    ]);
  });
});

describe("feedEntries: Gemini chat log", () => {
  it("reads $set.messages and single message lines, once per message id", () => {
    expect(brief(parse("gemini"))).toEqual([
      ["prompt", "Your task", "Rename the legacy pages", null],
      ["thinking", "thinks", "Plan: Check legacy-site first", null],
      ["text", "says", "Listing the folder first.", null],
      ["tool", "command", "ls legacy-site", "run_shell_command"],
      ["result", "result", "a.html b.html", null],
      ["error", "error", "Quota exceeded", null],
      ["system", "System", "Switched to fallback model", null],
    ]);
  });
});

describe("feedEntries: Muse session log (best effort)", () => {
  it("reads model messages and turns, ignores frames, markers, unknown payloads and the bad line", () => {
    const es = parse("muse");
    expect(brief(es)).toEqual([
      ["prompt", "Your task", "first turn", null],
      ["text", "says", "echo: first turn", null],
      ["prompt", "Your task", "second turn", null],
      ["text", "says", "echo: second turn", null],
    ]);
    // recorded_at is in microseconds
    expect(es[0].t).toBe(Math.floor(1793916301171098 / 1000));
  });
});

describe("feedEntries: robustness, caps and ids", () => {
  it("unknown formats and junk give no entries, never a throw", () => {
    for (const s of ["", "\n\n", "[1,2,3]\n", "null\n", '"x"\n', '{"a":{"b":{"c":1}}}\n', "{".repeat(5000) + "\n"]) {
      for (const source of ["claude", "muse", "codex", "gemini"] as const) expect(parse(source, Buffer.from(s))).toEqual([]);
    }
  });
  it("caps label at 40 and text at 400 characters, on one line", () => {
    const long = "word ".repeat(300) + "\nnext line";
    const line = JSON.stringify({ type: "assistant", uuid: "x", message: { content: [{ type: "text", text: long }, { type: "tool_use", name: "Z".repeat(80), input: {} }] } });
    const es = parse("claude", Buffer.from(line + "\n"));
    expect([...es[0].text].length).toBeLessThanOrEqual(MAX_FEED_TEXT);
    expect(es[0].text).not.toContain("\n");
    for (const e of es) expect([...e.label].length).toBeLessThanOrEqual(MAX_FEED_LABEL);
    expect(es[1].label).toBe("uses a tool");
    expect(es[1].tool).toBeUndefined();
  });
  it("redacts secrets that the cut would otherwise half-reveal", () => {
    const secret = "ghp_" + "a".repeat(36);
    const text = "x".repeat(390) + " " + secret;
    const es = parse("claude", Buffer.from(JSON.stringify({ type: "user", uuid: "q", message: { content: text } }) + "\n"));
    expect(es[0].text).not.toContain("ghp_aaaa");
  });
  it("ids are stable: the same entry gets the same id when read from another offset", () => {
    for (const source of ["claude", "codex", "gemini", "muse"] as const) {
      const buf = fixture(source);
      const all = parse(source, buf);
      const cut = buf.indexOf("\n", Math.floor(buf.length / 2)) + 1;
      const tail = parse(source, buf.subarray(cut), cut);
      expect(tail.length).toBeGreaterThan(0);
      const ids = new Map(all.map((e) => [e.id, e]));
      for (const e of tail) expect(ids.get(e.id)).toEqual(e);
      expect(new Set(all.map((e) => e.id)).size).toBe(all.length);
    }
  });
  it("Gemini: a message that grows (thoughts, tool results added) keeps the ids of its earlier parts", () => {
    const m = (extra: object) => JSON.stringify({ $set: { messages: [{ id: "g9", type: "gemini", content: "Doing it.", ...extra }] } }) + "\n";
    const first = parse("gemini", Buffer.from(m({})));
    const later = parse("gemini", Buffer.from(m({ thoughts: [{ subject: "Plan", description: "x" }], toolCalls: [{ name: "ls", args: {}, resultDisplay: "ok" }] })));
    const text = (es: FeedEntry[]) => es.find((e) => e.kind === "text")!.id;
    expect(text(later)).toBe(text(first));
  });
  it("Gemini $set: with the last sent message id, only that message and later ones are parsed", () => {
    const set = JSON.stringify({ $set: { messages: [{ id: "a", type: "user", content: "one" }, { id: "b", type: "gemini", content: "two" }, { id: "c", type: "user", content: "three" }] } }) + "\n";
    const state: { lastGeminiId?: string } = { lastGeminiId: "b" };
    const es = feedEntries(Buffer.from(set), 0, { ...ctx("gemini"), state });
    expect(es.map((e) => e.text)).toEqual(["two", "three"]);
    expect(state.lastGeminiId).toBe("c");
  });
  it("redacts labels and tool names", () => {
    const secretTool = "ghp_" + "a".repeat(36);
    const line = JSON.stringify({ type: "assistant", uuid: "z", message: { content: [{ type: "tool_use", name: secretTool, input: {} }] } });
    const es = parse("claude", Buffer.from(line + "\n"));
    expect(JSON.stringify(es)).not.toContain("ghp_aaaa");
  });
  it("ids differ between sessions and reveal nothing", () => {
    const a = feedEntries(fixture("claude"), 0, ctx("claude"));
    const b = feedEntries(fixture("claude"), 0, { ...ctx("claude"), sid: "other" });
    expect(a[0].id).not.toBe(b[0].id);
    expect(a[0].id).toMatch(/^[0-9a-f]{16}$/);
  });
});
