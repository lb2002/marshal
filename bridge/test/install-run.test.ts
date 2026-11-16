import { describe, expect, it } from "vitest";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { installHooks, planHooks, uninstallHooks, type HookPlace } from "../src/install-run.js";
import { buildHookSettings } from "../src/install.js";
import { buildToolHooks } from "../src/install-tools.js";
import { tempDir } from "./tmp.js";

const CMD = 'node "/x/dist/cli.js"';

describe("planHooks (idempotent install)", () => {
  it("installing twice gives the same settings", () => {
    const once = planHooks(null, "claude", CMD, "command", false);
    expect(once).toEqual({ settings: buildHookSettings(CMD, "command") });
    expect(planHooks((once as { settings: object }).settings, "claude", CMD, "command", false)).toEqual(once);
  });
  it("replaces our own http hooks with command hooks (upgrade) and back, keeping other keys", () => {
    const old = { permissions: { allow: ["a"] }, ...(buildHookSettings(CMD, "http") as object) };
    expect(planHooks(old, "claude", CMD, "command", false)).toEqual({ settings: { permissions: { allow: ["a"] }, ...(buildHookSettings(CMD, "command") as object) } });
    const now = { ...(buildHookSettings(CMD, "command") as object) };
    expect(planHooks(now, "claude", CMD, "http", false)).toEqual({ settings: buildHookSettings(CMD, "http") });
  });
  it("still refuses foreign hooks or a foreign statusLine without --force", () => {
    const foreign = { hooks: { Stop: [{ hooks: [{ type: "command", command: "echo hi" }] }] } };
    expect(planHooks(foreign, "claude", CMD, "command", false)).toEqual({ error: "hooks-exist" });
    expect(planHooks({ ...(buildHookSettings(CMD) as object), ...{ statusLine: { type: "command", command: "mine" } } }, "claude", CMD, "command", false)).toEqual({ error: "hooks-exist" });
    const mixed = buildHookSettings(CMD) as { hooks: Record<string, object[]> };
    mixed.hooks.Stop = [...mixed.hooks.Stop, ...foreign.hooks.Stop];
    expect(planHooks(mixed, "claude", CMD, "command", false)).toEqual({ error: "hooks-exist" });
    expect(planHooks(foreign, "claude", CMD, "command", true)).toEqual({ settings: buildHookSettings(CMD) });
  });
  it("is idempotent for the other tools too, and refuses their foreign hooks", () => {
    for (const tool of ["muse", "codex", "gemini"] as const) {
      const built = buildToolHooks(tool, CMD);
      expect(planHooks({ a: 1, ...(built as object) }, tool, CMD, "command", false)).toEqual({ settings: { a: 1, ...(built as object) } });
      expect(planHooks({ hooks: { Stop: [] } }, tool, CMD, "command", false)).toEqual({ error: "hooks-exist" });
    }
  });
});

describe("installHooks / uninstallHooks on disk", () => {
  function place(tool: HookPlace["tool"] = "claude"): HookPlace {
    const root = tempDir("lotse-run-");
    const home = join(root, "home");
    const dir = join(root, "proj");
    mkdirSync(home);
    mkdirSync(dir);
    return { dir, tool, bridgeCmd: CMD, homeDir: home };
  }

  it("writes, then reports no change on a second run, then removes only ours", () => {
    const p = place();
    const first = installHooks(p, "command", false);
    expect(first).toEqual({ ok: true, file: join(p.dir, ".claude", "settings.json"), changed: true });
    const text = readFileSync(join(p.dir, ".claude", "settings.json"), "utf8");
    expect(JSON.parse(text)).toEqual(buildHookSettings(CMD, "command"));
    expect(installHooks(p, "command", false)).toMatchObject({ ok: true, changed: false });
    expect(readFileSync(join(p.dir, ".claude", "settings.json"), "utf8")).toBe(text);
    expect(uninstallHooks(p)).toMatchObject({ ok: true, changed: true, existed: true });
    expect(JSON.parse(readFileSync(join(p.dir, ".claude", "settings.json"), "utf8"))).toEqual({});
    expect(uninstallHooks(p)).toMatchObject({ ok: true, changed: false, existed: true });
  });
  it("refuses foreign hooks and leaves the file untouched", () => {
    const p = place();
    mkdirSync(join(p.dir, ".claude"));
    const file = join(p.dir, ".claude", "settings.json");
    writeFileSync(file, JSON.stringify({ hooks: { Stop: [] } }));
    const r = installHooks(p, "command", false);
    expect(r.ok).toBe(false);
    expect(!r.ok && r.error).toMatch(/already has hooks or a statusLine; use --force/);
    expect(readFileSync(file, "utf8")).toBe(JSON.stringify({ hooks: { Stop: [] } }));
  });
  it("refuses the home directory and a missing settings file is nothing to remove", () => {
    const p = place();
    expect(installHooks({ ...p, dir: p.homeDir }, "command", false).ok).toBe(false);
    expect(existsSync(join(p.homeDir, ".claude"))).toBe(false);
    expect(uninstallHooks(p)).toMatchObject({ ok: true, changed: false, existed: false });
  });
  it("installs the other tools into their own project file", () => {
    const p = place("codex");
    expect(installHooks(p, "command", false)).toMatchObject({ ok: true, changed: true, file: join(p.dir, ".codex", "hooks.json") });
    expect(uninstallHooks(p)).toMatchObject({ ok: true, changed: true });
  });
});
