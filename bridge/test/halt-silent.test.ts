// Fix round 2, ruling C (review 2 I-4, M-1): every path on which a pre-tool or permission hook would end without a
// decision checks the halt marker first, and a live bridge that is not halted wins over a planted marker. All homes
// here are temporary directories, never the real one.
import { spawn } from "node:child_process";
import { existsSync, linkSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { afterEach, beforeAll, describe, expect, inject, it } from "vitest";
import { HALT_MESSAGE } from "../src/halt.js";
import { writeHaltMarker } from "../src/halt-marker.js";
import { hookKind } from "../src/hook-cli.js";
import { TIMEOUT, UNREACHABLE } from "../src/hook-fallback.js";
import { runShim } from "../src/hook-shim.js";
import { buildHookSettings, removeLotseHooks } from "../src/install.js";
import { buildToolHooks, removeToolHooks } from "../src/install-tools.js";
import { musePluginManifest } from "../src/muse-plugin.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive } from "./live-helpers.js";
import { tempDir } from "./tmp.js";

const fixture = (name: string) => readFileSync(join(import.meta.dirname, "fixtures", "agents", name), "utf8");
const PRE_TOOL_HALT = JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: HALT_MESSAGE } });
const PERM_HALT = JSON.stringify({ hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: HALT_MESSAGE } } });
const GEMINI_HALT = JSON.stringify({ decision: "deny", reason: HALT_MESSAGE });
const TOKEN = "s".repeat(64);
const CMD = '"/usr/bin/node" "/x/dist/cli.js"';

describe("the kind of a hook comes from its arguments", () => {
  it("pre-tool, permission and other per tool", () => {
    expect(hookKind("claude", "PreToolUse")).toBe("pre");
    expect(hookKind("codex", "PreToolUse")).toBe("pre");
    expect(hookKind("muse", "PreToolUse")).toBe("pre");
    expect(hookKind("gemini", "BeforeTool")).toBe("pre"); // also Gemini's approval event; same refusal format
    expect(hookKind("claude", "PermissionRequest")).toBe("perm");
    expect(hookKind("muse", "permissionRequest")).toBe("perm");
    expect(hookKind("claude", "Stop")).toBe("other");
    expect(hookKind("claude", undefined)).toBeUndefined();
  });

  it("every installer names the pre-tool event, and removal still knows the older hooks without it", () => {
    const claude = buildHookSettings(CMD, "command") as { hooks: Record<string, { hooks: { command: string }[] }[]> };
    expect(claude.hooks.PreToolUse[0].hooks[0].command).toBe(`${CMD} hook --source claude --event PreToolUse`);
    const old = { hooks: { PreToolUse: [{ hooks: [{ type: "command", command: `${CMD} hook --source claude` }] }] } };
    expect(removeLotseHooks(old, CMD)).toEqual({});
    expect(removeLotseHooks(claude, CMD)).toEqual({});
    for (const tool of ["codex", "muse"] as const) {
      const built = buildToolHooks(tool, CMD) as { hooks: Record<string, { hooks: { command: string }[] }[]> };
      expect(built.hooks.PreToolUse[0].hooks[0].command, tool).toBe(`${CMD} hook --source ${tool} --event PreToolUse`);
      expect(removeToolHooks(built, CMD, tool), tool).toEqual({});
      const legacy = { hooks: { PreToolUse: [{ hooks: [{ type: "command", command: `${CMD} hook --source ${tool}` }] }] } };
      expect(removeToolHooks(legacy, CMD, tool), tool).toEqual({});
    }
    const muse = musePluginManifest(CMD) as { capabilities: { hooks: { event: string; command: string[] }[] } };
    expect(muse.capabilities.hooks.find((h) => h.event === "PreToolUse")?.command.slice(-2)).toEqual(["--event", "PreToolUse"]);
  });
});

describe("shim: every silent exit of a pre-tool or permission hook checks the marker", () => {
  const live = () => true;
  const opts = (kind: "pre" | "perm" | "other" | undefined, haltMarker = live, evMs = 400) => ({ evMs, permMs: 1000, haltMarker, ...(kind ? { kind } : {}) });
  const answer = async () => ({});

  it("empty, cut or late stdin with a live marker refuses (pre-tool and permission, in the tool's format)", async () => {
    for (const raw of ["", "{", '{"session_id":"s","hook_event_name":"PreTo']) {
      expect(await runShim("claude", raw, answer, opts("pre")), raw).toBe(PRE_TOOL_HALT);
      expect(await runShim("claude", raw, answer, opts("perm")), raw).toBe(PERM_HALT);
      expect(await runShim("gemini", raw, answer, opts("pre")), raw).toBe(GEMINI_HALT);
      expect(await runShim("claude", raw, answer, opts("other")), raw).toBe("");
    }
    // without --event (an older install): the kind is read from the partial text
    expect(await runShim("claude", '{"hook_event_name":"PreToolUse","session_id":', answer, opts(undefined))).toBe(PRE_TOOL_HALT);
  });

  it("no marker: as before (nothing, or Muse's own no-decision deny)", async () => {
    expect(await runShim("claude", "", answer, opts("pre", () => false))).toBe("");
    expect(await runShim("claude", "", answer, opts("perm", () => false))).toBe("");
  });

  it("an error and a non-200 answer count as silent; a timeout too", async () => {
    const pre = fixture("claude-pre-tool-use-read.json");
    expect(await runShim("claude", pre, async () => { throw new Error("x"); }, opts("pre"))).toBe(PRE_TOOL_HALT);
    expect(await runShim("claude", pre, async () => undefined, opts("pre"))).toBe(PRE_TOOL_HALT);
    expect(await runShim("claude", pre, async () => TIMEOUT, opts("pre"))).toBe(PRE_TOOL_HALT);
    expect(await runShim("claude", fixture("claude-permission-request-bash.json"), async () => undefined, opts("perm"))).toBe(PERM_HALT);
  });

  it("the bridge's answer wins over the marker; a refused connection ignores it", async () => {
    let looked = 0;
    const marker = () => (looked++, true);
    for (const reply of [{}, { x: 1 }, UNREACHABLE]) {
      expect(await runShim("claude", fixture("claude-pre-tool-use-read.json"), async () => reply, opts("pre", marker))).toBe("");
      expect(await runShim("claude", fixture("claude-permission-request-bash.json"), async () => reply, opts("perm", marker))).toBe("");
    }
    expect(looked).toBe(0);
  });
});

// ---- spawn level ----

describe("lotse hook, spawned, with a temporary home", () => {
  let out: string;
  let handle: ServerHandle | undefined;
  beforeAll(() => {
    out = inject("cliBuild");
  });
  afterEach(async () => {
    await handle?.close();
    handle = undefined;
  });

  async function hangingBridge(): Promise<{ port: number; close: () => Promise<void> }> {
    const s: Server = createServer(() => {});
    await new Promise<void>((r) => s.listen(0, "127.0.0.1", () => r()));
    return { port: (s.address() as AddressInfo).port, close: () => new Promise((r) => { s.closeAllConnections(); s.close(() => r()); }) };
  }

  /** Runs `hook` with stdin left open and silent for `silentMs`, then `stdin` written and closed. */
  function hook(args: string[], port: number, home: string, stdin: string, silentMs = 0): Promise<string> {
    const env: NodeJS.ProcessEnv = { ...process.env, LOTSE_TOKEN: TOKEN, LOTSE_HTTP_PORT: String(port), HOME: home, USERPROFILE: home };
    delete env.MUSE_SESSION_ID;
    const child = spawn(process.execPath, [join(out, "cli.js"), "hook", ...args], { env, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    child.stdout.on("data", (c) => (stdout += c));
    child.stdin.on("error", () => {});
    const t = setTimeout(() => child.stdin.end(stdin), silentMs);
    return new Promise((r) => child.on("close", () => (clearTimeout(t), r(stdout.trim()))));
  }

  function home(markerPid?: number): string {
    const h = tempDir("lotse-silent-home-");
    if (markerPid !== undefined) {
      mkdirSync(join(h, ".lotse"));
      writeFileSync(join(h, ".lotse", "halt"), JSON.stringify({ pid: markerPid, start: Date.now() }));
    }
    return h;
  }

  it("stdin silent for 600 ms: a live marker refuses, no marker prints nothing (pre-tool and permission)", async () => {
    const bridge = await hangingBridge();
    try {
      const pre = fixture("claude-pre-tool-use-read.json");
      expect(await hook(["--source", "claude", "--event", "PreToolUse"], bridge.port, home(process.pid), pre, 600)).toBe(PRE_TOOL_HALT);
      expect(await hook(["--source", "claude", "--event", "PreToolUse"], bridge.port, home(), pre, 600)).toBe("");
      expect(await hook(["--source", "gemini", "--event", "BeforeTool"], bridge.port, home(process.pid), "", 0)).toBe(GEMINI_HALT);
      expect(await hook(["--source", "claude", "--event", "Stop"], bridge.port, home(process.pid), "", 600)).toBe("");
    } finally {
      await bridge.close();
    }
  }, 30_000);

  it("a planted marker with a live pid and a bridge that is not halted: no refusal, and the marker is gone after", async () => {
    const h = home();
    const marker = join(h, ".lotse", "halt");
    handle = await bootLive({ ingestToken: TOKEN, log: () => {}, haltMarker: marker });
    mkdirSync(join(h, ".lotse"), { recursive: true });
    writeFileSync(marker, JSON.stringify({ pid: process.pid, start: 1 }));
    expect(await hook(["--source", "claude", "--event", "PreToolUse"], handle.httpPort, h, fixture("claude-pre-tool-use-read.json"))).toBe("");
    expect(existsSync(marker)).toBe(false);
  }, 30_000);

  it("a bridge that starts removes any marker, also one naming a live process (review 2 M-1)", async () => {
    const h = home(process.pid);
    const marker = join(h, ".lotse", "halt");
    handle = await bootLive({ ingestToken: TOKEN, log: () => {}, haltMarker: marker });
    expect(existsSync(marker)).toBe(false);
  });
});

describe("writing the marker never follows a link (review 2 M-2)", () => {
  it("a planted temporary-name (hard) link is replaced, not written through; a linked ~/.lotse is refused", () => {
    const root = tempDir("lotse-mark-");
    mkdirSync(join(root, ".lotse"));
    const victim = join(root, "victim.txt");
    writeFileSync(victim, "keep me");
    const marker = join(root, ".lotse", "halt");
    // A hard link needs no rights (also on Windows); a write through the temporary name would truncate the victim.
    linkSync(victim, `${marker}.${process.pid}.tmp`);
    expect(writeHaltMarker(marker, 1)).toBe(true);
    expect(readFileSync(victim, "utf8")).toBe("keep me");
    expect(JSON.parse(readFileSync(marker, "utf8")).pid).toBe(process.pid);
    const other = tempDir("lotse-mark-");
    const real = tempDir("lotse-mark-real-");
    try {
      symlinkSync(real, join(other, ".lotse"), process.platform === "win32" ? "junction" : "dir");
    } catch {
      return;
    }
    expect(writeHaltMarker(join(other, ".lotse", "halt"), 1)).toBe(false);
    expect(existsSync(join(real, "halt"))).toBe(false);
  });
});
