import { describe, expect, it } from "vitest";
import { homedir } from "node:os";
import { mkdirSync, symlinkSync, writeFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { buildHookSettings, mergeIntoProjectSettings, checkProjectDir, removeLotseHooks, parseSettingsText, writeProjectSettings } from "../src/install.js";
import { tempDir } from "./tmp.js";

type Hook = { type: string; url?: string; timeout?: number; headers?: Record<string, string>; allowedEnvVars?: string[] };
type Settings = { hooks: Record<string, { hooks: Hook[] }[]>; statusLine: { type: string; command: string } };

const EV = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Notification", "Stop", "StopFailure", "SubagentStart", "SubagentStop", "PreCompact", "SessionEnd"];

type CmdHook = { type: string; command?: string; timeout?: number };

describe("buildHookSettings (command transport, the default)", () => {
  const s = buildHookSettings('node "/x/dist/cli.js"') as { hooks: Record<string, { hooks: CmdHook[] }[]>; statusLine: unknown };
  const hooksOf = (name: string): CmdHook[] => s.hooks[name].flatMap((g) => g.hooks);
  const SHIM = 'node "/x/dist/cli.js" hook --source claude';

  it("runs the shim as a command hook for every event, PermissionRequest included (timeout 120)", () => {
    expect(Object.keys(s.hooks).sort()).toEqual([...EV, "PermissionRequest"].sort());
    // The pre-tool hook names its event, so a slow or failing run still checks the halt marker (round 2 C).
    for (const name of EV.filter((n) => n !== "Stop")) expect(hooksOf(name), name).toEqual([{ type: "command", command: name === "PreToolUse" ? `${SHIM} --event PreToolUse` : SHIM }]);
    expect(hooksOf("PermissionRequest")).toEqual([{ type: "command", command: `${SHIM} --event PermissionRequest`, timeout: 120 }]);
  });
  it("Stop runs in the background with asyncRewake and listens up to 900 s for a reply from the headset", () => {
    // Both background flags: the docs allow either, and with both the session can never block.
    expect(hooksOf("Stop")).toEqual([{ type: "command", command: `${SHIM} --event Stop --listen`, async: true, asyncRewake: true, timeout: 900 }]);
  });
  it("the http transport has no listener", () => {
    const http = buildHookSettings('node "/x/dist/cli.js"', "http") as { hooks: Record<string, { hooks: { listen?: unknown; asyncRewake?: unknown }[] }[]> };
    expect(JSON.stringify(http)).not.toContain("asyncRewake");
    expect(JSON.stringify(http)).not.toContain("--listen");
  });
  it("needs no env var: no http hook, no header, no token anywhere", () => {
    const text = JSON.stringify(s);
    for (const bad of ["http", "LOTSE_TOKEN", "X-Lotse-Token", "allowedEnvVars"]) expect(text.includes(bad), bad).toBe(false);
  });
  it("adds a statusLine command", () => {
    expect(s.statusLine).toEqual({ type: "command", command: 'node "/x/dist/cli.js" statusline' });
  });
  it("is what removeLotseHooks removes completely", () => {
    expect(removeLotseHooks({ permissions: {}, ...(s as object) }, 'node "/x/dist/cli.js"')).toEqual({ permissions: {} });
  });
});

describe("buildHookSettings (http transport, --transport http)", () => {
  const s = buildHookSettings('node "/x/dist/cli.js"', "http") as Settings;
  const hooksOf = (name: string): Hook[] => s.hooks[name].flatMap((g) => g.hooks);

  it("posts every event except SessionStart to /ev", () => {
    for (const name of EV.filter((n) => n !== "SessionStart")) {
      expect(hooksOf(name), name).toEqual([expect.objectContaining({ type: "http", url: "http://127.0.0.1:47820/ev" })]);
    }
  });
  it("posts PermissionRequest to /perm with timeout 120", () => {
    expect(hooksOf("PermissionRequest")).toEqual([expect.objectContaining({ type: "http", url: "http://127.0.0.1:47820/perm", timeout: 120 })]);
  });
  it("only registers the known events", () => {
    expect(Object.keys(s.hooks).sort()).toEqual([...EV, "PermissionRequest"].sort());
  });
  it("runs SessionStart as a command hook (http is unsupported there)", () => {
    expect(hooksOf("SessionStart")).toEqual([{ type: "command", command: 'node "/x/dist/cli.js" hook' }]);
  });
  it("carries the token by env reference on every http hook, never as a literal", () => {
    for (const name of Object.keys(s.hooks).filter((n) => n !== "SessionStart")) {
      for (const h of hooksOf(name)) {
        // The pre-tool hook also says what it is, so a halted bridge can refuse it from the headers alone.
        expect(h.headers).toEqual(name === "PreToolUse" ? { "X-Lotse-Token": "$LOTSE_TOKEN", "X-Lotse-Hook": "PreToolUse" } : { "X-Lotse-Token": "$LOTSE_TOKEN" });
        expect(h.allowedEnvVars).toEqual(["LOTSE_TOKEN"]);
      }
    }
  });
  it("adds a statusLine command", () => {
    expect(s.statusLine).toEqual({ type: "command", command: 'node "/x/dist/cli.js" statusline' });
  });
});

describe("mergeIntoProjectSettings", () => {
  const hooks = buildHookSettings("lotse");
  it("merge into null gives the full settings", () => {
    expect(mergeIntoProjectSettings(null, hooks, false)).toEqual({ settings: hooks });
  });
  it("refuses existing hooks unless forced", () => {
    expect(mergeIntoProjectSettings({ hooks: { Stop: [] } }, hooks, false)).toEqual({ error: "hooks-exist" });
  });
  it("refuses an existing statusLine unless forced", () => {
    expect(mergeIntoProjectSettings({ statusLine: { type: "command", command: "x" } }, hooks, false)).toEqual({ error: "hooks-exist" });
  });
  it("force replaces only hooks and statusLine", () => {
    const r = mergeIntoProjectSettings({ permissions: { allow: ["a"] }, hooks: { Stop: [] }, statusLine: { type: "command", command: "x" } }, hooks, true);
    expect(r).toEqual({ settings: { permissions: { allow: ["a"] }, ...(hooks as object) } });
  });
  it("keeps unrelated keys", () => {
    const r = mergeIntoProjectSettings({ permissions: { allow: ["a"] } }, hooks, false);
    expect(r).toEqual({ settings: { permissions: { allow: ["a"] }, ...(hooks as object) } });
  });
});

describe("checkProjectDir", () => {
  const ok = () => true;
  const plain = { canon: (p: string) => p, isSymlink: () => false };
  it("refuses the home directory", () => {
    expect(checkProjectDir("/h/me", "/h/me", ok, plain, false)).toMatch(/home/i);
    expect(checkProjectDir("/h/me/", "/h/me", ok, plain, false)).toMatch(/home/i);
  });
  it("refuses a missing directory", () => {
    expect(checkProjectDir("/h/me/p", "/h/me", () => false)).toMatch(/exist/i);
  });
  it("accepts an existing project directory", () => {
    expect(checkProjectDir("/h/me/p", "/h/me", ok, plain, false)).toBeNull();
  });
  it("refuses case variants of the home directory on Windows", () => {
    expect(checkProjectDir("c:\\users\\linus", "C:\\Users\\linus", ok, undefined, true)).toMatch(/home/i);
    expect(checkProjectDir("C:\\USERS\\LINUS", "C:\\Users\\linus", ok, undefined, true)).toMatch(/home/i);
  });
  it("is case-sensitive when the filesystem is", () => {
    expect(checkProjectDir("/h/ME", "/h/me", ok, plain, false)).toBeNull();
  });
  it("refuses the extended-length prefixed form of the home directory", () => {
    expect(checkProjectDir("\\\\?\\C:\\Users\\linus", "C:\\Users\\linus", ok, undefined, true)).toMatch(/home/i);
  });
  it("compares canonical (link-resolved) paths", () => {
    const canon = (p: string) => (p === "/h/me/link" ? "/h/me" : p);
    expect(checkProjectDir("/h/me/link", "/h/me", ok, { canon, isSymlink: () => false }, false)).toMatch(/home/i);
  });
  it("refuses a project whose .claude resolves to the home .claude", () => {
    const canon = (p: string) => (p.split("\\").join("/") === "/p/.claude" ? "/h/me/.claude" : p);
    expect(checkProjectDir("/p", "/h/me", ok, { canon, isSymlink: () => false }, false)).toMatch(/.claude/);
  });
  it("refuses a symlinked settings.json", () => {
    const isSymlink = (p: string) => p.split("\\").join("/").endsWith("/p/.claude/settings.json");
    expect(checkProjectDir("/p", "/h/me", ok, { canon: (p) => p, isSymlink }, false)).toMatch(/symlink/i);
  });
  it("refuses UNC and device paths on Windows, whatever they point at", () => {
    for (const p of ["\\\\localhost\\C$\\Users\\linus", "\\\\127.0.0.1\\C$\\Users\\linus", "\\\\?\\UNC\\localhost\\C$\\Users\\linus"]) {
      expect(checkProjectDir(p, "C:\\Users\\linus", ok, undefined, true), p).toMatch(/UNC|device/i);
    }
  });
  it("refuses when the project has the same file identity as home", () => {
    const fs = { canon: (p: string) => p, isSymlink: () => false, id: (p: string) => (p === "/alias" || p === "/h/me" ? "1:7" : "1:" + p.length) };
    expect(checkProjectDir("/alias", "/h/me", ok, fs, false)).toMatch(/home/i);
  });
  it("refuses when .claude has the same identity as ~/.claude", () => {
    const fs = {
      canon: (p: string) => p,
      isSymlink: () => false,
      id: (p: string) => (p.split("\\").join("/").endsWith("/.claude") ? "9:9" : p),
    };
    expect(checkProjectDir("/p", "/h/me", ok, fs, false)).toMatch(/\.claude/);
  });
  it("refuses when settings.json has the same identity as ~/.claude/settings.json", () => {
    const fs = {
      canon: (p: string) => p,
      isSymlink: () => false,
      id: (p: string) => (p.endsWith("settings.json") ? "5:5" : p),
    };
    expect(checkProjectDir("/p", "/h/me", ok, fs, false)).toMatch(/settings/i);
  });
  it("fails closed when the home or project path cannot be resolved", () => {
    const failHome = { canon: (p: string) => (p === "/h/me" ? null : p), isSymlink: () => false };
    expect(checkProjectDir("/p", "/h/me", ok, failHome, false)).toMatch(/resolve/i);
    const failDir = { canon: (p: string) => (p === "/p" ? null : p), isSymlink: () => false };
    expect(checkProjectDir("/p", "/h/me", ok, failDir, false)).toMatch(/resolve/i);
  });
  it("fails closed when an existing .claude cannot be resolved", () => {
    const f = { canon: (p: string) => (p.split("\\").join("/").endsWith("/p/.claude") ? null : p), isSymlink: () => false };
    expect(checkProjectDir("/p", "/h/me", ok, f, false)).toMatch(/resolve/i);
  });
  it("refuses a .claude that is itself a link", () => {
    const f = { canon: (p: string) => p, isSymlink: (p: string) => p.split("\\").join("/").endsWith("/p/.claude") };
    expect(checkProjectDir("/p", "/h/me", ok, f, false)).toMatch(/link/i);
  });
  const tmp = tempDir("lotse-link-");
  let canLink = true;
  try {
    mkdirSync(join(tmp, "home"));
    symlinkSync(join(tmp, "home"), join(tmp, "link"), "junction");
  } catch {
    canLink = false;
  }
  it.skipIf(!canLink)("refuses a real link to the home directory (real filesystem)", () => {
    expect(checkProjectDir(join(tmp, "link"), join(tmp, "home"), (p) => p.length > 0)).toMatch(/home/i);
  });
  it("refuses a real symlinked settings.json (real filesystem)", (ctx) => {
    const proj = join(tmp, "proj");
    mkdirSync(join(proj, ".claude"), { recursive: true });
    writeFileSync(join(tmp, "target.json"), "{}");
    try {
      symlinkSync(join(tmp, "target.json"), join(proj, ".claude", "settings.json"), "file");
    } catch {
      ctx.skip();
    }
    expect(checkProjectDir(proj, join(tmp, "home"), (p) => p.length > 0)).toMatch(/symlink/i);
  });
  it("writeProjectSettings refuses a symlinked settings.json and leaves the target alone", (ctx) => {
    const proj = join(tmp, "proj2");
    mkdirSync(join(proj, ".claude"), { recursive: true });
    writeFileSync(join(tmp, "target2.json"), "{}");
    try {
      symlinkSync(join(tmp, "target2.json"), join(proj, ".claude", "settings.json"), "file");
    } catch {
      ctx.skip();
    }
    expect(writeProjectSettings(proj, join(tmp, "home"), "X")).toMatch(/symlink/i);
    expect(readFileSync(join(tmp, "target2.json"), "utf8")).toBe("{}");
  });
  it("writeProjectSettings writes into a plain project", () => {
    const proj = join(tmp, "proj3");
    mkdirSync(proj);
    expect(writeProjectSettings(proj, join(tmp, "home"), '{"a":1}\n')).toBeNull();
    expect(readFileSync(join(proj, ".claude", "settings.json"), "utf8")).toBe('{"a":1}\n');
    expect(writeProjectSettings(proj, join(tmp, "home"), "{}\n")).toBeNull();
    expect(readFileSync(join(proj, ".claude", "settings.json"), "utf8")).toBe("{}\n");
  });
  it("writeProjectSettings refuses the home directory", () => {
    expect(writeProjectSettings(join(tmp, "home"), join(tmp, "home"), "X")).toMatch(/home/i);
  });

  describe("F11: CLAUDE_CONFIG_DIR", () => {
    const plainFs = { canon: (p: string) => p, isSymlink: () => false };
    it("refuses a project whose .claude resolves to CLAUDE_CONFIG_DIR", () => {
      const canon = (p: string) => (p.split("\\").join("/") === "/p/.claude" ? "/cfg" : p);
      expect(checkProjectDir("/p", "/h/me", ok, { canon, isSymlink: () => false }, false, "/cfg")).toMatch(/CLAUDE_CONFIG_DIR/);
      expect(checkProjectDir("/p", "/h/me", ok, { canon, isSymlink: () => false }, false, undefined)).toBeNull();
      expect(checkProjectDir("/p", "/h/me", ok, plainFs, false, "/p/.claude")).toMatch(/CLAUDE_CONFIG_DIR/);
    });
    it("refuses by file identity of .claude and of settings.json", () => {
      const dirId = { ...plainFs, id: (p: string) => (/(\/p\/\.claude|\/cfg)$/.test(p.split("\\").join("/")) ? "7:7" : p) };
      expect(checkProjectDir("/p", "/h/me", ok, dirId, false, "/cfg")).toMatch(/CLAUDE_CONFIG_DIR/);
      const n = (p: string) => p.split("\\").join("/");
      const fileId = { ...plainFs, id: (p: string) => (n(p).endsWith("settings.json") && !n(p).startsWith("/h/me") ? "8:8" : p) };
      expect(checkProjectDir("/p", "/h/me", ok, fileId, false, "/cfg")).toMatch(/CLAUDE_CONFIG_DIR/);
    });
    it("fails closed when CLAUDE_CONFIG_DIR exists but cannot be resolved; ignores an empty value", () => {
      const f = { canon: (p: string) => (p.split("\\").join("/").endsWith("/cfg") ? null : p), isSymlink: () => false };
      expect(checkProjectDir("/p", "/h/me", ok, f, false, "/cfg")).toMatch(/resolve/i);
      expect(checkProjectDir("/p", "/h/me", ok, plainFs, false, "")).toBeNull();
    });
    it("writeProjectSettings refuses a project .claude that is CLAUDE_CONFIG_DIR (real filesystem, env var)", () => {
      const proj = join(tmp, "proj-cfg");
      mkdirSync(join(proj, ".claude"), { recursive: true });
      writeFileSync(join(proj, ".claude", "settings.json"), '{"keep":1}');
      const saved = process.env.CLAUDE_CONFIG_DIR;
      process.env.CLAUDE_CONFIG_DIR = join(proj, ".claude");
      try {
        expect(writeProjectSettings(proj, join(tmp, "home"), "X")).toMatch(/CLAUDE_CONFIG_DIR/);
      } finally {
        if (saved === undefined) delete process.env.CLAUDE_CONFIG_DIR;
        else process.env.CLAUDE_CONFIG_DIR = saved;
      }
      expect(readFileSync(join(proj, ".claude", "settings.json"), "utf8")).toBe('{"keep":1}');
    });
  });
});

describe("removeLotseHooks", () => {
  const CMD = 'node "/x/dist/cli.js"';
  const built = buildHookSettings(CMD, "http") as Record<string, unknown>;
  it("removes everything Lotse installed and keeps other keys", () => {
    const r = removeLotseHooks({ permissions: { allow: ["a"] }, ...built }, CMD);
    expect(r).toEqual({ permissions: { allow: ["a"] } });
  });
  it("removes the Stop listener", () => {
    const listener = { hooks: { Stop: [{ hooks: [{ type: "command", command: `${CMD} hook --source claude --event Stop --listen`, asyncRewake: true, timeout: 900 }] }] } };
    expect(removeLotseHooks(listener, CMD)).toEqual({});
  });
  it("also removes an older command install without the --event flag", () => {
    const old = { hooks: { PermissionRequest: [{ hooks: [{ type: "command", command: `${CMD} hook --source claude`, timeout: 120 }] }] } };
    expect(removeLotseHooks(old, CMD)).toEqual({});
  });
  it("recognises both transports, also mixed in one file", () => {
    const http = buildHookSettings(CMD, "http") as { hooks: Record<string, object[]> };
    const cmd = buildHookSettings(CMD, "command") as { hooks: Record<string, object[]> };
    const mixed = { hooks: { Stop: [...http.hooks.Stop, ...cmd.hooks.Stop], PermissionRequest: [...http.hooks.PermissionRequest, ...cmd.hooks.PermissionRequest] } };
    expect(removeLotseHooks(mixed, CMD)).toEqual({});
    expect(removeLotseHooks({ ...http }, CMD)).toEqual({});
  });
  it("keeps foreign hooks and a foreign statusLine", () => {
    const foreign = { hooks: [{ type: "command", command: "echo hi" }] };
    const input = {
      hooks: { ...(built.hooks as object), Stop: [...(built.hooks as Record<string, object[]>).Stop, foreign], PostToolUse: [foreign] },
      statusLine: { type: "command", command: "my-statusline" },
    };
    const r = removeLotseHooks(input, CMD) as { hooks: Record<string, unknown>; statusLine: unknown };
    expect(r.hooks).toEqual({ Stop: [foreign], PostToolUse: [foreign] });
    expect(r.statusLine).toEqual({ type: "command", command: "my-statusline" });
  });
  it("keeps look-alike entries that are not exactly ours", () => {
    const foreignHooks = [
      { type: "command", command: 'node /other/tool/cli.js statusline' },
      { type: "command", command: 'node /other/tool/cli.js hook' },
      { type: "command", command: "my-lotse hook" },
      { type: "command", command: CMD + " hook --extra" },
      { type: "command", command: CMD + " hook --source muse" },
      { type: "command", command: CMD + " hook --source claude --x" },
      { type: "http", url: "http://127.0.0.1:47820/other", headers: { "X-Lotse-Token": "$LOTSE_TOKEN" } },
      { type: "http", url: "http://127.0.0.1:47820/ev" },
    ];
    const input = { hooks: { Stop: [{ hooks: foreignHooks }] }, statusLine: { type: "command", command: "node /other/tool/cli.js statusline" } };
    expect(removeLotseHooks(input, CMD)).toEqual(input);
  });
  it("does nothing to settings without Lotse entries", () => {
    expect(removeLotseHooks({ permissions: {} }, CMD)).toEqual({ permissions: {} });
  });
});

describe("parseSettingsText", () => {
  it("strips a UTF-8 BOM", () => {
    expect(parseSettingsText('﻿{"a":1}')).toEqual({ value: { a: 1 } });
  });
  it("rejects non-objects and bad JSON", () => {
    for (const s of ["[]", '"x"', "null", "1", "{oops"]) expect(parseSettingsText(s), s).toHaveProperty("error");
  });
});
