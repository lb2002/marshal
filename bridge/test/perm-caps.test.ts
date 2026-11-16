import { describe, expect, it } from "vitest";
import { buildPermRequest } from "../src/broker.js";

const base = { id: "x", sid: "s", risk: { risk: "safe" as const, reason: "Safe" }, createdAt: 0, holdMs: 90_000 };

describe("display caps on permission requests", () => {
  it("cuts an overlong path and marks the request truncated (two-hand)", () => {
    const r = buildPermRequest({ ...base, tool: "Write", input: { file_path: "/repo/" + "a".repeat(5000) } });
    expect(r.path!.length).toBe(300);
    expect(r.paths.every((p) => p.length <= 300)).toBe(true);
    expect(r.truncated).toBe(true);
  });
  it("keeps a normal path whole and not truncated", () => {
    const r = buildPermRequest({ ...base, tool: "Write", input: { file_path: "/repo/src/a.ts" } });
    expect(r.path).toBe("/repo/src/a.ts");
    expect(r.truncated).toBe(false);
  });
  it("caps the tool name at 40 characters", () => {
    const r = buildPermRequest({ ...base, tool: "mcp__" + "x".repeat(200), input: {} });
    expect(r.tool.length).toBe(40);
    expect(r.truncated).toBe(true);
  });
  it("a 40-character tool name is not truncated", () => {
    const r = buildPermRequest({ ...base, tool: "t".repeat(40), input: {} });
    expect(r.truncated).toBe(false);
  });
});
