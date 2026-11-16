// Spawn-level tests of `lotse hook`: the real CLI process, compiled once into a temp folder, against a
// test bridge on its own port (LOTSE_HTTP_PORT) with a wrong-for-anything-else token.
import { afterAll, afterEach, beforeAll, describe, expect, inject, it, vi } from "vitest";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { MUSE_NO_DECISION_DENY } from "../src/adapters.js";
import { MUSE_BRIDGE_OFF_DENY } from "../src/hook-fallback.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset, type Headset } from "./live-helpers.js";

const ALLOW = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };
const TOKEN = "hook-cli-test-token";
const raw = (name: string) => readFileSync(join(import.meta.dirname, "fixtures", "agents", `${name}.json`), "utf8");

let out: string;
let home: string;
beforeAll(() => {
  out = inject("cliBuild"); // built by test/global-setup.ts
  home = mkdtempSync(join(tmpdir(), "lotse-cli-home-"));
});
afterAll(() => rmSync(home, { recursive: true, force: true }));

/** A port nothing listens on: the bridge is down. */
async function closedPort(): Promise<number> {
  return new Promise((r) => {
    const s = createServer();
    s.listen(0, "127.0.0.1", () => {
      const p = (s.address() as AddressInfo).port;
      s.close(() => r(p));
    });
  });
}

interface Run {
  stdout: string;
  code: number | null;
}
interface FullRun extends Run {
  stderr: string;
}

/**
 * Runs `hook <args>`; stdin is written after `delayMs` (undefined: closed at once, empty). With `splitAt`, the
 * first part is written at once and the rest after `delayMs`.
 */
async function hook(args: string[], o: HookOpts): Promise<Run> {
  const r = await hookFull(args, o);
  return { stdout: r.stdout, code: r.code };
}

type HookOpts = { port: number; stdin?: string; delayMs?: number; splitAt?: number; env?: Record<string, string> };

function hookFull(args: string[], o: HookOpts): Promise<FullRun> {
  const env: NodeJS.ProcessEnv = { ...process.env, LOTSE_TOKEN: TOKEN, LOTSE_HTTP_PORT: String(o.port), HOME: home, USERPROFILE: home, ...o.env };
  if (!o.env?.MUSE_SESSION_ID) delete env.MUSE_SESSION_ID;
  const child = spawn(process.execPath, [join(out, "cli.js"), "hook", ...args], { env, stdio: ["pipe", "pipe", "pipe"] });
  let stdout = "";
  let stderr = "";
  child.stdout.setEncoding("utf8").on("data", (d: string) => (stdout += d));
  child.stderr.setEncoding("utf8").on("data", (d: string) => (stderr += d));
  const first = o.splitAt !== undefined && o.stdin !== undefined ? o.stdin.slice(0, o.splitAt) : "";
  if (first !== "") child.stdin.write(first);
  const write = () => {
    if (o.stdin !== undefined) child.stdin.write(o.stdin.slice(first.length));
    child.stdin.end();
  };
  if (o.delayMs) setTimeout(write, o.delayMs);
  else write();
  return new Promise((r) => child.on("close", (code) => r({ stdout: stdout.trim(), stderr, code })));
}

describe("lotse hook as a process", () => {
  let handle: ServerHandle | undefined;
  let h: Headset | undefined;
  afterEach(async () => {
    h?.close();
    await handle?.close();
    handle = undefined;
  });

  it("Muse approval hook: empty stdin, a cut payload and a bridge that is down all print the deny, exit 0", async () => {
    const port = await closedPort();
    const args = ["--source", "muse", "--event", "PermissionRequest"];
    for (const stdin of [undefined, "", '{"session_id":"m","hook_ev']) {
      const r = await hook(args, { port, stdin });
      expect(r, String(stdin).slice(0, 30)).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    }
    // A full request to a bridge that is off: the deny says so.
    expect(await hook(args, { port, stdin: raw("muse-permission-request-shell") })).toEqual({ stdout: MUSE_BRIDGE_OFF_DENY, code: 0 });
  }, 30_000);

  it("Muse approval hook: bad arguments still print the deny; MUSE_SESSION_ID makes any run a Muse run", async () => {
    const port = await closedPort();
    expect(await hook(["--source", "muse", "--event", "PermissionRequest", "--bogus"], { port })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook(["--source", "muse", "--event", "PermissionRequest", "extra"], { port })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    const env = { MUSE_SESSION_ID: "m-1" };
    expect(await hook(["--source", "codex", "--event", "PermissionRequest"], { port, env })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook(["--source", "nope", "--event", "PermissionRequest"], { port, env })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook(["--source", "claude", "--event", "PermissionRequest"], { port, env, stdin: raw("claude-permission-request-bash") })).toEqual({ stdout: MUSE_BRIDGE_OFF_DENY, code: 0 });
  }, 30_000);

  it("a Muse approval waits at most LOTSE_MUSE_PERM_WAIT_MS for the headset, then denies (no decision)", async () => {
    handle = await bootLive({ ingestToken: TOKEN, holdMs: 30_000 });
    h = await pairHeadset(handle); // paired, but nobody answers: the bridge holds the request
    const t = Date.now();
    const r = await hook(["--source", "muse", "--event", "PermissionRequest"], { port: handle.httpPort, stdin: raw("muse-permission-request-shell"), env: { LOTSE_MUSE_PERM_WAIT_MS: "800" } });
    expect(r).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(Date.now() - t).toBeLessThan(8000);
  }, 30_000);

  it("approval hooks wait 10 s for stdin: a payload arriving after 600 ms still reaches the headset (Muse and Claude)", async () => {
    handle = await bootLive({ ingestToken: TOKEN, holdMs: 5000 });
    h = await pairHeadset(handle);
    h.msgs.length = 0;
    const allowEach = setInterval(() => {
      for (const m of h!.msgs.splice(0)) if (m.type === "perm_open") h!.send({ type: "perm_decision", decision: { id: m.request.id, behavior: "allow", confirm: "two-hand" } });
    }, 20);
    try {
      const muse = await hook(["--source", "muse", "--event", "PermissionRequest"], { port: handle.httpPort, stdin: raw("muse-permission-request-shell"), delayMs: 600 });
      expect(JSON.parse(muse.stdout)).toEqual(ALLOW);
      const claude = await hook(["--source", "claude", "--event", "PermissionRequest"], { port: handle.httpPort, stdin: raw("claude-permission-request-bash"), delayMs: 600 });
      expect(JSON.parse(claude.stdout)).toEqual(ALLOW);
      // An older install without --event: once the part that arrived shows a permission event, it gets the long deadline too.
      const payload = raw("claude-permission-request-bash");
      const old = await hook(["--source", "claude"], { port: handle.httpPort, stdin: payload, delayMs: 700, splitAt: payload.indexOf('"tool_name"') });
      expect(JSON.parse(old.stdout)).toEqual(ALLOW);
    } finally {
      clearInterval(allowEach);
    }
  }, 30_000);

  it("legacy Muse installs (MUSE_SESSION_ID, no --event): empty stdin is an approval without decision, so deny", async () => {
    const port = await closedPort();
    const env = { MUSE_SESSION_ID: "m-1" };
    expect(await hook(["--source", "muse"], { port, env })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook([], { port, env, stdin: "" })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    // an observe event of a legacy install stays silent
    expect(await hook(["--source", "muse"], { port, env, stdin: raw("muse-pre-tool-use-shell") })).toEqual({ stdout: "", code: 0 });
  }, 30_000);

  it("OpenCode: the plugin's permission reaches the headset as two-hand when unjoined; allow and deny come back in the plugin's form", async () => {
    const port = await closedPort();
    const perm = (resources: string[], extra: object = {}) =>
      JSON.stringify({ event: "PermissionRequest", sessionID: "oc-1", cwd: home, action: "shell", resources, ...extra });
    // bridge down: nothing (OpenCode asks itself), exit 0
    expect(await hook(["--source", "opencode", "--event", "PermissionRequest"], { port, stdin: perm(["ls"]) })).toEqual({ stdout: "", code: 0 });
    expect(await hook(["--source", "opencode", "--event", "PreToolUse"], { port, stdin: "{cut" })).toEqual({ stdout: "", code: 0 });
    handle = await bootLive({ ingestToken: TOKEN, holdMs: 5000 });
    h = await pairHeadset(handle);
    h.msgs.length = 0;
    const seen: { risk: string; tool: string; command?: string }[] = [];
    const answer = setInterval(() => {
      for (const m of h!.msgs.splice(0)) {
        if (m.type !== "perm_open") continue;
        seen.push({ risk: m.request.risk.risk, tool: m.request.tool, command: m.request.command });
        h!.send({ type: "perm_decision", decision: { id: m.request.id, behavior: m.request.command?.includes("deny") ? "deny" : "allow", confirm: "two-hand", message: "no" } });
      }
    }, 20);
    try {
      const args = ["--source", "opencode", "--event", "PermissionRequest"];
      expect(JSON.parse((await hook(args, { port: handle.httpPort, stdin: perm(["ls"]) })).stdout)).toEqual({ decision: "allow" });
      expect(JSON.parse((await hook(args, { port: handle.httpPort, stdin: perm(["echo deny"], { tool: "shell", input: { command: "echo deny" } }) })).stdout)).toMatchObject({ decision: "deny" });
      // without the joined call the request is unclear (two hands); with it, the call itself is rated
      expect(seen[0]).toMatchObject({ risk: "destructive", command: "ls" });
      expect(seen[1]).toMatchObject({ risk: "safe", command: "echo deny" });
    } finally {
      clearInterval(answer);
    }
  }, 30_000);

  it("legacy Muse installs wait up to 10 s for a late stdin and still reach the headset", async () => {
    handle = await bootLive({ ingestToken: TOKEN, holdMs: 5000 });
    h = await pairHeadset(handle);
    h.msgs.length = 0;
    const allowEach = setInterval(() => {
      for (const m of h!.msgs.splice(0)) if (m.type === "perm_open") h!.send({ type: "perm_decision", decision: { id: m.request.id, behavior: "allow", confirm: "two-hand" } });
    }, 20);
    try {
      const r = await hook(["--source", "muse"], { port: handle.httpPort, env: { MUSE_SESSION_ID: "m-1" }, stdin: raw("muse-permission-request-shell"), delayMs: 700 });
      expect(JSON.parse(r.stdout)).toEqual(ALLOW);
    } finally {
      clearInterval(allowEach);
    }
  }, 30_000);

  it("accepts --source=muse and --event=PermissionRequest; a flag left without its value is a Muse approval (deny)", async () => {
    const port = await closedPort();
    expect(await hook(["--source=muse", "--event=PermissionRequest"], { port, stdin: raw("muse-permission-request-shell") })).toEqual({ stdout: MUSE_BRIDGE_OFF_DENY, code: 0 });
    expect(await hook(["--source", "muse", "--event"], { port, stdin: raw("muse-pre-tool-use-shell") })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook(["--source=muse", "--event"], { port })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook(["--source=muse", "--event="], { port })).toEqual({ stdout: MUSE_NO_DECISION_DENY, code: 0 });
    expect(await hook(["--source=claude", "--event=PermissionRequest"], { port, stdin: raw("claude-permission-request-bash") })).toEqual({ stdout: "", code: 0 });
  }, 30_000);

  it("--source=muse --event=PermissionRequest reaches the headset like the spaced form", async () => {
    handle = await bootLive({ ingestToken: TOKEN, holdMs: 5000 });
    h = await pairHeadset(handle);
    h.msgs.length = 0;
    const allowEach = setInterval(() => {
      for (const m of h!.msgs.splice(0)) if (m.type === "perm_open") h!.send({ type: "perm_decision", decision: { id: m.request.id, behavior: "allow", confirm: "two-hand" } });
    }, 20);
    try {
      const r = await hook(["--source=muse", "--event=PermissionRequest"], { port: handle.httpPort, stdin: raw("muse-permission-request-shell") });
      expect(JSON.parse(r.stdout)).toEqual(ALLOW);
    } finally {
      clearInterval(allowEach);
    }
  }, 30_000);

  it("Claude: no decision on every path prints nothing and exits 0 (its normal dialog)", async () => {
    const port = await closedPort();
    for (const args of [["--source", "claude", "--event", "PermissionRequest"], ["--source", "claude", "--event", "PermissionRequest", "--bogus"], ["--source", "claude"], []]) {
      for (const stdin of [undefined, "not json", raw("claude-permission-request-bash")]) {
        expect(await hook(args, { port, stdin }), `${args.join(" ")} / ${String(stdin).slice(0, 20)}`).toEqual({ stdout: "", code: 0 });
      }
    }
  }, 60_000);
});

describe("hook --source claude --event Stop --listen (reply from the headset)", () => {
  let handle: ServerHandle | undefined;
  let h: Headset | undefined;
  afterEach(async () => {
    h?.close();
    await handle?.close();
    handle = undefined;
  });
  const LISTEN = ["--source", "claude", "--event", "Stop", "--listen"];
  const stopPayload = (sid = "c-reply") =>
    JSON.stringify({ session_id: sid, hook_event_name: "Stop", cwd: "/w/shop", transcript_path: `/h/.claude/projects/p/${sid}.jsonl`, stop_hook_active: false, last_assistant_message: "Fertig." });
  /** The session is known to the bridge (a SessionStart arrived), as in a real Claude session. */
  const started = (sid = "c-reply") =>
    fetch(`http://127.0.0.1:${handle!.httpPort}/ev`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN },
      body: JSON.stringify({ hook_event_name: "SessionStart", session_id: sid, cwd: "/w/shop", transcript_path: `/h/.claude/projects/p/${sid}.jsonl` }),
    });

  it("a reply wakes Claude: the contract line on stderr, exit 2, nothing on stdout", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid) });
    h = await pairHeadset(handle);
    await started();
    const run = hookFull(LISTEN, { port: handle.httpPort, stdin: stopPayload() });
    await vi.waitFor(() => expect(listening).toEqual(["c-reply"]), { timeout: 5000 });
    expect(h.msgs.some((m) => m.type === "agent" && m.agent.sid === "c-reply" && m.agent.status === "done")).toBe(true);
    h.send({ type: "agent_reply", sid: "c-reply", text: "Bitte nur die Preise anpassen.\nUnd die Tests laufen lassen." });
    expect(await h.next((m) => m.type === "agent_reply_ack")).toMatchObject({ delivered: true });
    const r = await run;
    expect(r).toEqual({
      stdout: "",
      stderr: "Message from the user via the Marshal headset: Bitte nur die Preise anpassen.\nUnd die Tests laufen lassen.\n",
      code: 2,
    });
  }, 30_000);

  it("no reply (the bridge's wait ends), bridge down, a bad payload or another tool: exit 0, silent", async () => {
    handle = await bootLive({ ingestToken: TOKEN, replyWaitMs: 300 });
    await started();
    expect(await hookFull(LISTEN, { port: handle.httpPort, stdin: stopPayload() })).toEqual({ stdout: "", stderr: "", code: 0 });
    expect(await hookFull(LISTEN, { port: handle.httpPort, stdin: "not json" })).toEqual({ stdout: "", stderr: "", code: 0 });
    expect(await hookFull(LISTEN, { port: handle.httpPort, stdin: JSON.stringify({ hook_event_name: "Stop" }) })).toEqual({ stdout: "", stderr: "", code: 0 });
    const down = await closedPort();
    expect(await hookFull(LISTEN, { port: down, stdin: stopPayload() })).toEqual({ stdout: "", stderr: "", code: 0 });
    // --listen only means something for Claude's Stop hook
    const listening: string[] = [];
    await handle.close();
    handle = await bootLive({ ingestToken: TOKEN, replyWaitMs: 300, onReplyListen: (sid) => listening.push(sid) });
    expect(await hookFull(["--source", "codex", "--event", "Stop", "--listen"], { port: handle.httpPort, stdin: stopPayload() })).toEqual({ stdout: "", stderr: "", code: 0 });
    expect(await hookFull(["--source", "claude", "--listen"], { port: handle.httpPort, stdin: stopPayload() })).toEqual({ stdout: "", stderr: "", code: 0 });
    expect(listening).toEqual([]);
  }, 30_000);

  it("the shim's own listen budget (LOTSE_LISTEN_MS) ends it silently, and its listener is withdrawn", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid) });
    h = await pairHeadset(handle);
    await started();
    const t0 = Date.now();
    const r = await hookFull(LISTEN, { port: handle.httpPort, stdin: stopPayload(), env: { LOTSE_LISTEN_MS: "1500" } });
    expect(r).toEqual({ stdout: "", stderr: "", code: 0 });
    expect(Date.now() - t0).toBeLessThan(8000);
    expect(listening).toEqual(["c-reply"]);
    await new Promise((res) => setTimeout(res, 100));
    h.send({ type: "agent_reply", sid: "c-reply", text: "zu spät" });
    expect(await h.next((m) => m.type === "agent_reply_ack")).toMatchObject({ delivered: false });
  }, 30_000);

  it("a wrong token never gets a reply", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: "other-token", onReplyListen: (sid) => listening.push(sid), replyWaitMs: 300 });
    expect(await hookFull(LISTEN, { port: handle.httpPort, stdin: stopPayload() })).toEqual({ stdout: "", stderr: "", code: 0 });
    expect(listening).toEqual([]);
  }, 30_000);
});
