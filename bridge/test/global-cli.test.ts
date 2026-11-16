// Spawn-level checks of `connect --all` and `disconnect --all` with the real CLI (compiled by globalSetup),
// always with HOME and USERPROFILE pointing at a fake home and the tools' env overrides removed. Only paths
// that never start the bridge run here (a declined question, disconnect).
import { beforeAll, describe, expect, inject, it } from "vitest";
import { spawn } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { createServer, type Server } from "node:net";
import { homedir } from "node:os";
import { join } from "node:path";
import { globalFile } from "../src/install-global.js";
import { tempDir } from "./tmp.js";

/**
 * The shared build has no node_modules on purpose (the hook path must load no `ws`); these commands load
 * the server, so they run from a private copy with a junction to the bridge's node_modules.
 */
let cliDir = "";
beforeAll(() => {
  cliDir = join(tempDir("lotse-gcli-build-"), "dist");
  cpSync(inject("cliBuild"), cliDir, { recursive: true });
  symlinkSync(join(import.meta.dirname, "..", "node_modules"), join(cliDir, "..", "node_modules"), "junction");
});

function fakeEnv(home: string): NodeJS.ProcessEnv {
  // No real Muse binary either: Muse is then skipped (its plugin path is covered with a stub in install-global).
  const env: NodeJS.ProcessEnv = { ...process.env, HOME: home, USERPROFILE: home, LOTSE_MUSE_BIN: join(home, "no-muse-bin.exe") };
  for (const k of ["CLAUDE_CONFIG_DIR", "CODEX_HOME", "GEMINI_CLI_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "LOTSE_TOKEN"]) delete env[k];
  return env;
}

function run(args: string[], home: string, stdin: string, extraEnv: NodeJS.ProcessEnv = {}): Promise<{ code: number | null; stdout: string; stderr: string }> {
  const cli = join(cliDir, "cli.js");
  return new Promise((resolve) => {
    const p = spawn(process.execPath, [cli, ...args], { env: { ...fakeEnv(home), ...extraEnv }, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    p.stdout.on("data", (c) => (stdout += c));
    p.stderr.on("data", (c) => (stderr += c));
    p.on("close", (code) => resolve({ code, stdout, stderr }));
    p.stdin.end(stdin);
  });
}

function home(): string {
  const h = tempDir("lotse-gcli-");
  expect(h).not.toBe(homedir());
  for (const d of [".claude", join(".config", "muse"), ".codex", ".gemini"]) mkdirSync(join(h, d), { recursive: true });
  return h;
}

describe("lotse connect --all / disconnect --all (process, fake HOME)", () => {
  // A spawned CLI has no terminal on stdin: without --yes it lists, asks nothing and changes nothing (the question
  // itself is tested in-process in install-global.test.ts).
  for (const args of [["connect", "--all"], ["connect"]]) {
    it(`${args.join(" ")} without a terminal lists the files and entries, asks nothing, writes nothing and does not start`, async () => {
      const h = home();
      const r = await run([...args, "--explain", "off"], h, "y\n");
      expect(r.code).toBe(1);
      expect(r.stdout).not.toContain("[y/N]");
      expect(r.stdout).toContain("No terminal to ask on, so nothing was changed. To install without the question: lotse connect --yes");
      expect(r.stdout).toContain("Muse: no valid global hook location found. Please connect Muse per project (lotse connect --project … --tool muse)");
      for (const tool of ["claude", "codex", "gemini"] as const) {
        const f = globalFile(tool, h, {});
        expect(r.stdout).toContain(f);
        expect(existsSync(f)).toBe(false);
      }
      expect(r.stdout).toContain("Codex CLI: 0 entries removed, 10 added");
      expect(r.stdout).toContain("      + hooks.SessionStart: ");
      expect(r.stdout).not.toContain("MARSHAL IS READY");
      expect(r.stdout).not.toContain("bridge listening");
    }, 20_000);
  }

  it("connect --all with closed stdin counts as no", async () => {
    const h = home();
    const r = await run(["connect", "--all", "--tool", "codex", "--explain", "off"], h, "");
    expect(r.code).toBe(1);
    expect(existsSync(globalFile("codex", h, {}))).toBe(false);
  }, 20_000);

  it("disconnect --all removes exactly the Lotse handlers of this CLI and prints the file", async () => {
    const h = home();
    const cli = join(cliDir, "cli.js").replace(/\\/g, "/");
    const ours = { type: "command", command: `node "${cli}" hook --source codex` };
    const foreign = { type: "command", command: "guard.sh" };
    const file = globalFile("codex", h, {});
    writeFileSync(file, JSON.stringify({ hooks: { Stop: [{ hooks: [foreign] }, { hooks: [ours] }] } }));
    const r = await run(["disconnect", "--all", "--tool", "codex", "--yes"], h, "");
    expect(r.code).toBe(0);
    expect(r.stdout).toContain(file);
    expect(JSON.parse(readFileSync(file, "utf8"))).toEqual({ hooks: { Stop: [{ hooks: [foreign] }] } });
  }, 20_000);

  it("connect --all removes the Muse plugin again when the bridge cannot start (port taken)", async () => {
    const h = home();
    const state = join(h, "fake-muse-state.json");
    const env = { LOTSE_MUSE_BIN: join(import.meta.dirname, "fixtures", "fake-muse.mjs"), FAKE_MUSE_STATE: state };
    // Take the bridge's HTTP port; if something (a running bridge) already holds it, it is taken anyway.
    const blocker: Server = createServer();
    await new Promise<void>((res) => {
      blocker.once("error", () => res());
      blocker.listen(47820, "127.0.0.1", () => res());
    });
    try {
      const r = await run(["connect", "--all", "--tool", "muse", "--yes", "--explain", "off"], h, "", env);
      expect(r.code).toBe(1);
      const s = JSON.parse(readFileSync(state, "utf8")) as { plugins: unknown[]; log: string[] };
      expect(s.log).toContain("install");
      expect(s.log).toContain("remove");
      expect(s.plugins).toEqual([]);
    } finally {
      await new Promise<void>((res) => blocker.close(() => res()));
    }
  }, 30_000);

  it("disconnect without --yes and without a terminal lists, removes nothing and exits 1", async () => {
    const h = home();
    const cli = join(cliDir, "cli.js").replace(/\\/g, "/");
    const file = globalFile("codex", h, {});
    const text = JSON.stringify({ hooks: { Stop: [{ hooks: [{ type: "command", command: `node "${cli}" hook --source codex` }] }] } });
    writeFileSync(file, text);
    const r = await run(["disconnect"], h, "y\n");
    expect(r.code).toBe(1);
    expect(r.stdout).toContain("- hooks.Stop: ");
    expect(r.stdout).toContain("No terminal to ask on, so nothing was changed. To remove them without the question: lotse disconnect --yes");
    expect(readFileSync(file, "utf8")).toBe(text);
  }, 20_000);

  it("while halted (the marker exists), disconnect and uninstall-hooks refuse, with and without --yes", async () => {
    const h = home();
    const cli = join(cliDir, "cli.js").replace(/\\/g, "/");
    const file = globalFile("codex", h, {});
    const text = JSON.stringify({ hooks: { Stop: [{ hooks: [{ type: "command", command: `node "${cli}" hook --source codex` }] }] } });
    writeFileSync(file, text);
    const project = join(h, "proj");
    mkdirSync(join(project, ".codex"), { recursive: true });
    const projectFile = join(project, ".codex", "hooks.json");
    writeFileSync(projectFile, text);
    mkdirSync(join(h, ".lotse"), { recursive: true });
    writeFileSync(join(h, ".lotse", "halt"), JSON.stringify({ pid: process.pid, start: 0 }));
    for (const args of [["disconnect"], ["disconnect", "--yes"], ["disconnect", "--all", "--yes"], ["uninstall-hooks", "--project", project, "--tool", "codex"], ["uninstall-hooks", "--project", project, "--tool", "codex", "--yes"]]) {
      const r = await run(args, h, "y\n");
      expect(r.code, args.join(" ")).toBe(1);
      expect(r.stderr, args.join(" ")).toContain("Marshal is halted, so its hooks stay. Release the halt first, from the headset or the bridge console. Nothing was changed.");
      expect(readFileSync(file, "utf8")).toBe(text);
      expect(readFileSync(projectFile, "utf8")).toBe(text);
    }
  }, 30_000);

  it("uninstall-hooks lists and asks like disconnect: no terminal and no --yes changes nothing; --yes removes", async () => {
    const h = home();
    const cli = join(cliDir, "cli.js").replace(/\\/g, "/");
    const project = join(h, "proj");
    mkdirSync(join(project, ".gemini"), { recursive: true });
    const projectFile = join(project, ".gemini", "settings.json");
    const text = JSON.stringify({ theme: "x", hooks: { AfterTool: [{ hooks: [{ type: "command", command: `node "${cli}" hook --source gemini`, name: "lotse" }] }] } });
    writeFileSync(projectFile, text);
    const r = await run(["uninstall-hooks", "--project", project, "--tool", "gemini"], h, "y\n");
    expect(r.code).toBe(1);
    expect(r.stdout).toContain("- hooks.AfterTool: ");
    expect(readFileSync(projectFile, "utf8")).toBe(text);
    const y = await run(["uninstall-hooks", "--project", project, "--tool", "gemini", "--yes"], h, "");
    expect(y.code).toBe(0);
    expect(JSON.parse(readFileSync(projectFile, "utf8"))).toEqual({ theme: "x" });
  }, 20_000);

  it("disconnect with no flags is disconnect --all; with --project it points to uninstall-hooks", async () => {
    const h = home();
    const cli = join(cliDir, "cli.js").replace(/\\/g, "/");
    const file = globalFile("gemini", h, {});
    const before = JSON.stringify({ hooks: { BeforeTool: [{ hooks: [{ type: "command", command: "guard.sh" }] }] } });
    writeFileSync(file, JSON.stringify({ hooks: { BeforeTool: [{ hooks: [{ type: "command", command: "guard.sh" }] }, { hooks: [{ type: "command", command: `node "${cli}" hook --source gemini`, name: "lotse" }] }] } }));
    const r = await run(["disconnect", "--yes"], h, "");
    expect(r.code).toBe(0);
    expect(r.stdout).toContain(file);
    expect(JSON.parse(readFileSync(file, "utf8"))).toEqual(JSON.parse(before));
    const p = await run(["disconnect", "--project", h], home(), "");
    expect(p.code).toBe(2);
    expect(p.stderr).toContain("uninstall-hooks --project");
  }, 20_000);
});
