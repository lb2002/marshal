import { afterEach, describe, expect, it, vi } from "vitest";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Explainer, GEMINI_PREFIX, forSource } from "../src/explain.js";
import { buildPack } from "../src/pack.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset, postTo, type Headset } from "./live-helpers.js";

const geminiPerm = (command: string) => ({ hook_event_name: "PermissionRequest", session_id: "g1", cwd: "/w/shop", tool_name: "Bash", tool_input: { command }, lotse_source: "gemini" });

describe("I4: a Gemini request can only be denied; the headset says so, not the explanation", () => {
  it("forSource puts no vendor prefix in front of any text, and takes an old one off", () => {
    const e = { text: "x".repeat(140), replies: ["a"], source: "rules" as const };
    expect(GEMINI_PREFIX).toBe("Gemini (Deny only): ");
    for (const s of [undefined, "claude", "muse", "codex", "gemini"] as const) expect(forSource(e, s)).toEqual(e);
    const old = { text: GEMINI_PREFIX + "Gemini runs a command: npm test", replies: ["a"], source: "rules" as const };
    expect(forSource(old, "gemini").text).toBe("Gemini runs a command: npm test");
  });

  it("forSource names the asking agent in a rule text, and leaves AI and manual texts alone", () => {
    const rule = { text: "Claude runs a command: npm test", replies: ["a"], source: "rules" as const };
    expect(forSource(rule, "claude")).toEqual(rule);
    expect(forSource(rule, undefined)).toEqual(rule);
    expect(forSource(rule, "codex").text).toBe("Codex runs a command: npm test");
    expect(forSource(rule, "muse").text).toBe("Muse runs a command: npm test");
    expect(forSource(rule, "gemini").text).toBe("Gemini runs a command: npm test");
    for (const source of ["ai", "manual"] as const) {
      const e = { ...rule, source };
      expect(forSource(e, "codex")).toEqual(e);
      expect(forSource(e, "gemini")).toEqual(e);
    }
    // An AI result for a risky request keeps the rule text in `text` and the AI sentence in `aiText`.
    const ai = { text: "Claude runs a command: npm test", aiText: "Claude tests.", replies: ["a"], source: "ai" as const };
    expect(forSource(ai, "codex")).toEqual({ ...ai, text: "Codex runs a command: npm test" });
  });

  describe("live", () => {
    let handle: ServerHandle | undefined;
    let h: Headset | undefined;
    afterEach(async () => {
      h?.close();
      await handle?.close();
      handle = undefined;
    });
    it("perm_open and perm_explain carry no vendor prefix; the agent's source says it is Gemini", async () => {
      const run = vi.fn(async () => JSON.stringify({ erklaerung: "Löscht den Ordner build im Projekt.", antworten: ["Nicht löschen."] }));
      handle = await bootLive({ explainer: new Explainer(run, { log: () => {} }) });
      h = await pairHeadset(handle);
      void postTo(handle, "/perm", geminiPerm("rm -rf build"));
      const open = await h.next((m) => m.type === "perm_open");
      expect(open.explain.text.includes("Deny only")).toBe(false);
      expect(open.explain.text.startsWith("Gemini ")).toBe(true);
      const ai = await h.next((m) => m.type === "perm_explain");
      expect(ai.explain.text.includes("Deny only")).toBe(false);
      void postTo(handle, "/perm", { ...geminiPerm("rm -rf dist"), session_id: "c1", lotse_source: undefined });
      const claude = await h.next((m) => m.type === "perm_open" && m.request.sid === "c1");
      expect(claude.explain.text.startsWith("Gemini")).toBe(false);
    });
  });

  it("the pack has no vendor prefix in a recorded Gemini request either", async () => {
    const root = mkdtempSync(join(tmpdir(), "lotse-gem-"));
    try {
      const pack = await buildPack([{ sid: "g", t: 0, kind: "permission", tool: "Bash", command: "rm -rf build", cwd: "<repo>", source: "gemini" }], { budgetUsd: 5, demoRoot: root });
      const open = pack.messages.find((m) => m.msg.type === "perm_open")!.msg as { explain: { text: string } };
      expect(open.explain.text.startsWith(GEMINI_PREFIX)).toBe(false);
      expect(open.explain.text.startsWith("Gemini ")).toBe(true);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
