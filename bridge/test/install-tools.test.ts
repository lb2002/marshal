import { describe, expect, it } from "vitest";
import { mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { buildToolHooks, mergeToolHooks, removeToolHooks, TOOL_TARGETS, TRUST_STEP, type HookTool } from "../src/install-tools.js";
import { checkTargetDir, writeTargetFile } from "../src/install.js";
import { tempDir } from "./tmp.js";

const CMD = 'node "/x/dist/cli.js"';
type Hook = { type: string; command: string; timeout?: number; name?: string };
type Groups = Record<string, { matcher?: string; hooks: Hook[] }[]>;
const hooksOf = (tool: HookTool): Groups => (buildToolHooks(tool, CMD) as { hooks: Groups }).hooks;
const all = (g: Groups) => Object.values(g).flatMap((x) => x.flatMap((y) => y.hooks));

describe("buildToolHooks", () => {
  it("Muse: Claude-style event keys, every hook runs the shim, PermissionRequest waits 120 s", () => {
    const h = hooksOf("muse");
    expect(Object.keys(h).sort()).toEqual(
      ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUseFailure", "Notification", "Stop", "StopFailure", "SubagentStart", "SubagentStop", "PreCompact", "SessionEnd"].sort(),
    );
    for (const [event, groups] of Object.entries(h)) {
      const cmd = event === "PermissionRequest" || event === "PreToolUse" ? `${CMD} hook --source muse --event ${event}` : `${CMD} hook --source muse`;
      for (const x of groups.flatMap((g) => g.hooks)) expect(x, event).toMatchObject({ type: "command", command: cmd });
    }
    expect(h.PermissionRequest[0].hooks[0].timeout).toBe(120);
    expect(h.PreToolUse[0].hooks[0].timeout).toBeUndefined();
  });
  it("Codex: no Notification hook, PermissionRequest waits 120 s", () => {
    const h = hooksOf("codex");
    expect(Object.keys(h).sort()).toEqual(["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PreCompact", "SubagentStart", "SubagentStop", "Stop", "SessionEnd"].sort());
    for (const [event, groups] of Object.entries(h)) {
      const cmd = event === "PermissionRequest" || event === "PreToolUse" ? `${CMD} hook --source codex --event ${event}` : `${CMD} hook --source codex`;
      for (const x of groups.flatMap((g) => g.hooks)) expect(x.command, event).toBe(cmd);
    }
    expect(h.PermissionRequest[0].hooks[0].timeout).toBe(120);
  });
  it("Gemini: its own event names, BeforeTool waits 120000 ms, hooks are named lotse", () => {
    const h = hooksOf("gemini");
    expect(Object.keys(h).sort()).toEqual(["SessionStart", "BeforeAgent", "BeforeTool", "AfterTool", "AfterAgent", "PreCompress", "Notification", "SessionEnd"].sort());
    for (const [event, groups] of Object.entries(h)) {
      const cmd = event === "BeforeTool" ? `${CMD} hook --source gemini --event BeforeTool` : `${CMD} hook --source gemini`;
      for (const x of groups.flatMap((g) => g.hooks)) expect(x, event).toMatchObject({ type: "command", command: cmd, name: "lotse" });
    }
    expect(h.BeforeTool[0].hooks[0].timeout).toBe(120_000);
  });
  it("names a trust step for every tool", () => {
    for (const t of ["muse", "codex", "gemini"] as const) expect(TRUST_STEP[t].length).toBeGreaterThan(20);
  });
});

describe("mergeToolHooks", () => {
  const built = buildToolHooks("gemini", CMD);
  it("writes only the hooks key and keeps the rest", () => {
    expect(mergeToolHooks({ model: { name: "x" } }, built, false)).toEqual({ settings: { model: { name: "x" }, ...(built as object) } });
    expect(mergeToolHooks(null, built, false)).toEqual({ settings: built });
  });
  it("refuses existing hooks unless forced", () => {
    expect(mergeToolHooks({ hooks: {} }, built, false)).toEqual({ error: "hooks-exist" });
    expect(mergeToolHooks({ hooks: { X: [] }, keep: 1 }, built, true)).toEqual({ settings: { keep: 1, ...(built as object) } });
  });
});

describe("removeToolHooks", () => {
  it("removes exactly what was installed, for the same tool only", () => {
    for (const t of ["muse", "codex", "gemini"] as const) {
      expect(removeToolHooks({ keep: 1, ...(buildToolHooks(t, CMD) as object) }, CMD, t)).toEqual({ keep: 1 });
    }
    const muse = buildToolHooks("muse", CMD) as object;
    expect(removeToolHooks(muse, CMD, "codex")).toEqual(muse);
  });
  it("also removes an older install without the --event flag", () => {
    const old = { hooks: { PermissionRequest: [{ hooks: [{ type: "command", command: `${CMD} hook --source muse`, timeout: 120 }] }] } };
    expect(removeToolHooks(old, CMD, "muse")).toEqual({});
  });
  it("keeps foreign and look-alike hooks", () => {
    const foreign = [
      { type: "command", command: "echo hi" },
      { type: "command", command: `${CMD} hook --source muse --x` },
      { type: "command", command: `${CMD} hook --source muse --event Stop` },
      { type: "command", command: `${CMD} hook` },
      { type: "command", command: 'node "/other/cli.js" hook --source muse' },
    ];
    const built = buildToolHooks("muse", CMD) as { hooks: Groups };
    const input = { hooks: { ...built.hooks, Stop: [...built.hooks.Stop, { hooks: foreign }] } };
    expect(removeToolHooks(input, CMD, "muse")).toEqual({ hooks: { Stop: [{ hooks: foreign }] } });
  });
});

describe("guards per tool", () => {
  const ok = () => true;
  const plain = { canon: (p: string) => p, isSymlink: () => false };
  const n = (p: string) => p.split("\\").join("/");
  it("refuses the home directory for every tool", () => {
    for (const t of ["muse", "codex", "gemini"] as const) {
      expect(checkTargetDir("/h/me", "/h/me", ok, TOOL_TARGETS[t], plain, false), t).toMatch(/home/i);
    }
  });
  it.each([
    ["muse", "/p/.muse", "/h/me/.muse", /~\/\.muse/],
    ["muse", "/p/.muse", "/h/me/.config/muse", /~\/\.config\/muse/],
    ["codex", "/p/.codex", "/h/me/.codex", /~\/\.codex/],
    ["gemini", "/p/.gemini", "/h/me/.gemini", /~\/\.gemini/],
  ] as const)("%s: refuses a project folder %s that resolves to %s", (tool, from, to, msg) => {
    const canon = (p: string) => (n(p) === from ? to : p);
    expect(checkTargetDir("/p", "/h/me", ok, TOOL_TARGETS[tool], { canon, isSymlink: () => false }, false)).toMatch(msg);
  });
  it("refuses XDG_CONFIG_HOME/muse as the project .muse", () => {
    const saved = process.env.XDG_CONFIG_HOME;
    process.env.XDG_CONFIG_HOME = "/xdg";
    try {
      const canon = (p: string) => (n(p) === "/p/.muse" ? n(join("/xdg", "muse")) : p);
      expect(checkTargetDir("/p", "/h/me", ok, TOOL_TARGETS.muse, { canon, isSymlink: () => false }, false)).toMatch(/XDG_CONFIG_HOME/);
    } finally {
      if (saved === undefined) delete process.env.XDG_CONFIG_HOME;
      else process.env.XDG_CONFIG_HOME = saved;
    }
  });
  it("refuses CODEX_HOME as the project .codex", () => {
    const target = TOOL_TARGETS.codex;
    const saved = process.env.CODEX_HOME;
    process.env.CODEX_HOME = "/cx";
    try {
      const canon = (p: string) => (n(p) === "/p/.codex" ? "/cx" : p);
      expect(checkTargetDir("/p", "/h/me", ok, target, { canon, isSymlink: () => false }, false)).toMatch(/CODEX_HOME/);
    } finally {
      if (saved === undefined) delete process.env.CODEX_HOME;
      else process.env.CODEX_HOME = saved;
    }
  });
  it("refuses GEMINI_CLI_HOME/.gemini as the project .gemini", () => {
    const saved = process.env.GEMINI_CLI_HOME;
    process.env.GEMINI_CLI_HOME = "/gh";
    try {
      const canon = (p: string) => (n(p) === "/p/.gemini" ? n(join("/gh", ".gemini")) : p);
      const r = checkTargetDir("/p", "/h/me", ok, TOOL_TARGETS.gemini, { canon, isSymlink: () => false }, false);
      expect(r).toMatch(/GEMINI_CLI_HOME/);
    } finally {
      if (saved === undefined) delete process.env.GEMINI_CLI_HOME;
      else process.env.GEMINI_CLI_HOME = saved;
    }
  });
  it("refuses a linked tool folder and a symlinked hooks file", () => {
    expect(checkTargetDir("/p", "/h/me", ok, TOOL_TARGETS.muse, { canon: (p) => p, isSymlink: (p) => n(p) === "/p/.muse" }, false)).toMatch(/link/);
    expect(checkTargetDir("/p", "/h/me", ok, TOOL_TARGETS.codex, { canon: (p) => p, isSymlink: (p) => n(p) === "/p/.codex/hooks.json" }, false)).toMatch(/symlink/);
  });
  it("refuses UNC paths on Windows", () => {
    expect(checkTargetDir("\\\\server\\share\\p", "C:\\Users\\me", ok, TOOL_TARGETS.gemini, undefined, true)).toMatch(/UNC/);
  });

  const tmp = tempDir("lotse-tools-");
  mkdirSync(join(tmp, "home"));
  it("writeTargetFile writes <project>/.muse/hooks.json, <project>/.codex/hooks.json and <project>/.gemini/settings.json", () => {
    const proj = join(tmp, "proj");
    mkdirSync(proj);
    expect(writeTargetFile(proj, join(tmp, "home"), "{}\n", TOOL_TARGETS.muse)).toBeNull();
    expect(writeTargetFile(proj, join(tmp, "home"), "{}\n", TOOL_TARGETS.codex)).toBeNull();
    expect(writeTargetFile(proj, join(tmp, "home"), "{}\n", TOOL_TARGETS.gemini)).toBeNull();
    expect(readFileSync(join(proj, ".muse", "hooks.json"), "utf8")).toBe("{}\n");
    expect(readFileSync(join(proj, ".codex", "hooks.json"), "utf8")).toBe("{}\n");
    expect(readFileSync(join(proj, ".gemini", "settings.json"), "utf8")).toBe("{}\n");
  });
  it("writeTargetFile refuses the home directory and writes nothing there", () => {
    const home = join(tmp, "home2");
    mkdirSync(home);
    expect(writeTargetFile(home, home, "X", TOOL_TARGETS.gemini)).toMatch(/home/i);
  });
  it("writeTargetFile refuses a symlinked settings file and leaves its target alone", (ctx) => {
    const proj = join(tmp, "proj-link");
    mkdirSync(join(proj, ".gemini"), { recursive: true });
    writeFileSync(join(tmp, "victim.json"), "{}");
    try {
      symlinkSync(join(tmp, "victim.json"), join(proj, ".gemini", "settings.json"), "file");
    } catch {
      ctx.skip();
    }
    expect(writeTargetFile(proj, join(tmp, "home"), "X", TOOL_TARGETS.gemini)).toMatch(/symlink/);
    expect(readFileSync(join(tmp, "victim.json"), "utf8")).toBe("{}");
  });
});
