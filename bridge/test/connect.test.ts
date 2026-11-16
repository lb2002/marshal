import { afterAll, describe, expect, it } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { installHooks, type HookPlace } from "../src/install-run.js";
import { UsageError } from "../src/cli-args.js";
import { connectBanner, isAllMode, museStopCleanup, parseConnectAllArgs, parseConnectArgs, parseDisconnectArgs, temporaryCleanup } from "../src/connect.js";

describe("parseConnectArgs", () => {
  it("needs --project; defaults: claude, command hooks, not temporary, budget 15, explain as start", () => {
    expect(parseConnectArgs(["--project", "p"])).toEqual({ project: "p", tool: "claude", transport: "command", temporary: false, budget: 15, noQr: false });
    expect(() => parseConnectArgs([])).toThrow(UsageError);
  });
  it("takes --tool, --transport (Claude only), --temporary, --budget and --explain", () => {
    expect(parseConnectArgs(["--project", "p", "--tool", "codex", "--temporary"])).toMatchObject({ tool: "codex", temporary: true });
    expect(parseConnectArgs(["--project", "p", "--transport", "http"])).toMatchObject({ transport: "http" });
    expect(parseConnectArgs(["--project", "p", "--budget", "3", "--explain", "off"])).toMatchObject({ budget: 3, explain: "off" });
  });
  it("takes --no-qr in both modes", () => {
    expect(parseConnectArgs(["--project", "p", "--no-qr"])).toMatchObject({ noQr: true });
    expect(parseConnectAllArgs(["--all", "--no-qr"])).toMatchObject({ noQr: true });
    expect(() => parseConnectArgs(["--project", "p", "--no-qr", "x"])).toThrow(UsageError);
  });
  it.each([
    [["--project", "p", "--tool", "cursor"]],
    [["--project", "p", "--tool", "muse", "--transport", "http"]],
    [["--project", "p", "--transport", "smtp"]],
    [["--project", "p", "--force"]],
    [["--project", "p", "extra"]],
    [["--project", "p", "--explain"]],
  ])("rejects %j", (args) => {
    expect(() => parseConnectArgs(args)).toThrow(UsageError);
  });
});

describe("connect --all / disconnect --all arguments", () => {
  it("--all selects the global mode; --project and --all exclude each other", () => {
    expect(isAllMode(["--all"])).toBe(true);
    expect(isAllMode(["--project", "p"])).toBe(false);
    expect(() => parseConnectAllArgs(["--all", "--project", "p"])).toThrow(UsageError);
  });
  it("connect and disconnect with no flags (or without --project) are the global mode", () => {
    expect(isAllMode([])).toBe(true);
    expect(isAllMode(["--yes"])).toBe(true);
    expect(isAllMode(["--tool", "codex"])).toBe(true);
    expect(parseConnectAllArgs([])).toEqual(parseConnectAllArgs(["--all"]));
    expect(parseConnectAllArgs(["--yes"])).toMatchObject({ yes: true, tools: ["claude", "muse", "codex", "gemini", "opencode"] });
  });
  it("defaults to all five tools, asks, budget 15", () => {
    expect(parseConnectAllArgs(["--all"])).toEqual({ tools: ["claude", "muse", "codex", "gemini", "opencode"], explicit: false, yes: false, keepMuse: false, budget: 15, noQr: false });
    expect(parseConnectAllArgs(["--all", "--tool", "all"])).toMatchObject({ explicit: false });
  });
  it("takes --tool <one>, --yes, --budget and --explain", () => {
    expect(parseConnectAllArgs(["--all", "--tool", "muse", "--yes", "--budget", "4", "--explain", "off"])).toEqual({ tools: ["muse"], explicit: true, yes: true, keepMuse: false, budget: 4, explain: "off", noQr: false });
    expect(parseConnectAllArgs(["--all", "--keep-muse"])).toMatchObject({ keepMuse: true });
  });
  it.each([
    [["--all", "--tool", "cursor"]],
    [["--all", "--temporary"]],
    [["--all", "--transport", "http"]],
    [["--all", "extra"]],
  ])("connect rejects %j", (args) => {
    expect(() => parseConnectAllArgs(args)).toThrow(UsageError);
  });
  it("disconnect needs --all and takes --tool", () => {
    expect(parseDisconnectArgs(["--all"])).toEqual({ tools: ["claude", "muse", "codex", "gemini", "opencode"], explicit: false, yes: false });
    expect(parseDisconnectArgs(["--all", "--tool", "gemini", "--yes"])).toEqual({ tools: ["gemini"], explicit: true, yes: true });
    expect(parseDisconnectArgs([])).toEqual({ tools: ["claude", "muse", "codex", "gemini", "opencode"], explicit: false, yes: false });
    expect(parseDisconnectArgs(["--tool", "codex"])).toEqual({ tools: ["codex"], explicit: true, yes: false });
    expect(() => parseDisconnectArgs(["--project", "p"])).toThrow(/uninstall-hooks/);
    expect(() => parseDisconnectArgs(["--all", "--project", "p"])).toThrow(UsageError);
  });
  it("removes the Muse user hooks when connect --all stops, unless --keep-muse or Muse was not installed", () => {
    expect(museStopCleanup(false, ["claude", "muse"])).toBe(true);
    expect(museStopCleanup(true, ["claude", "muse"])).toBe(false);
    expect(museStopCleanup(false, ["claude", "codex"])).toBe(false);
  });
  it("the banner names the tools and the off switch in global mode", () => {
    const b = connectBanner({ code: "123456", ips: ["10.0.0.5"], wsPort: 47821, project: "", tool: "claude", temporary: false, global: ["claude", "muse"] });
    expect(b).toContain("Global hooks for: Claude Code, Muse");
    expect(b).toContain("lotse disconnect --all");
    expect(b).not.toContain("uninstall-hooks");
    expect(b).not.toContain("per project");
    const skipped = connectBanner({ code: "123456", ips: [], wsPort: 47821, project: "", tool: "claude", temporary: false, global: ["claude"], museSkipped: true });
    expect(skipped).toContain("Muse: not global, connect it per project (lotse connect --project … --tool muse)");
  });
});

describe("connectBanner", () => {
  const banner = connectBanner({ code: "482913", ips: ["192.168.1.23", "10.0.0.5"], wsPort: 47821, project: "C:/work/shop", tool: "claude", temporary: false });
  it("shows the code, every address and what to press in the headset, framed as one block", () => {
    expect(banner).toContain("482913");
    expect(banner).toContain("192.168.1.23");
    expect(banner).toContain("10.0.0.5");
    expect(banner).toContain("In the headset: Connect to my PC");
    const lines = banner.split("\n");
    expect(lines[0]).toMatch(/^=+$/);
    expect(lines[lines.length - 1]).toMatch(/^=+$/);
  });
  it("names the project and the tool, and says when the hooks go away again", () => {
    expect(banner).toContain("C:/work/shop");
    expect(banner).toMatch(/claude/i);
    expect(banner).not.toMatch(/removed on Ctrl\+C/);
    const temp = connectBanner({ code: "000001", ips: [], wsPort: 47821, project: "p", tool: "muse", temporary: true });
    expect(temp).toMatch(/removed on Ctrl\+C/);
    expect(temp).toMatch(/No network address found/);
    expect(temp).toContain("47821");
  });
});

describe("I5/M3: --temporary removes only what this run installed or changed", () => {
  const dirs: string[] = [];
  afterAll(() => {
    for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
  });
  const CMD = 'node "/x/dist/cli.js"';
  function place(): HookPlace {
    const root = mkdtempSync(join(tmpdir(), "lotse-conn-"));
    dirs.push(root);
    mkdirSync(join(root, "home"));
    mkdirSync(join(root, "proj"));
    return { dir: join(root, "proj"), tool: "claude", bridgeCmd: CMD, homeDir: join(root, "home") };
  }
  it("a fresh install is removed again on exit", () => {
    const p = place();
    const cleanup = temporaryCleanup(p, installHooks(p, "command", false));
    expect(existsSync(join(p.dir, ".claude", "settings.json"))).toBe(true);
    expect(cleanup()).toMatchObject({ ok: true, changed: true });
    expect(JSON.parse(readFileSync(join(p.dir, ".claude", "settings.json"), "utf8"))).toEqual({});
  });
  it("hooks that were already there before this run stay", () => {
    const p = place();
    installHooks(p, "command", false);
    const before = readFileSync(join(p.dir, ".claude", "settings.json"), "utf8");
    const cleanup = temporaryCleanup(p, installHooks(p, "command", false));
    expect(cleanup()).toBeNull();
    expect(readFileSync(join(p.dir, ".claude", "settings.json"), "utf8")).toBe(before);
  });
  it("a failed install removes nothing", () => {
    const p = place();
    expect(temporaryCleanup(p, { ok: false, error: "x" })()).toBeNull();
  });
});
