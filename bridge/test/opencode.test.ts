// OpenCode: the adapter (plugin JSON -> Claude hook vocabulary), the shim's outputs, the generated plugin (run for
// real against a fake hook), and the project and global installers. Nothing touches the real home or a real bridge.
import { describe, expect, it } from "vitest";
import { existsSync, mkdirSync, readFileSync, readdirSync, symlinkSync, unlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { adaptHook, preToolDeny, shimOutput } from "../src/adapters.js";
import { runShim, type ShimPost } from "../src/hook-shim.js";
import { hookKind } from "../src/hook-cli.js";
import { UNREACHABLE } from "../src/hook-fallback.js";
import { ENFORCEMENT, HALT_MESSAGE, haltRefusal, preToolHaltReply } from "../src/halt.js";
import { classifyRisk } from "../src/risk.js";
import { normalizeHook } from "../src/events.js";
import { bridgeCommand } from "../src/install.js";
import { installHooks, uninstallHooks, uninstallPlan, type HookPlace } from "../src/install-run.js";
import { connectAll, disconnectAll, globalFile, type GlobalOpts } from "../src/install-global.js";
import { OPENCODE_PLUGIN_MARKER, opencodePluginSource } from "../src/opencode-plugin.js";
import { opencodePluginText } from "../src/opencode-install.js";
import { ALIAS_CONFLICT } from "../src/risk.js";
import { tempDir } from "./tmp.js";

type Obj = Record<string, unknown>;
const ev = (o: Obj) => ({ sessionID: "ses_1", cwd: "/work/proj", ...o });
const body = (o: Obj, platform: NodeJS.Platform = "linux") => adaptHook("opencode", ev(o), platform)?.body as Obj;
const ALLOW = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };
const DENY = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: "not now" } } };

describe("adaptHook opencode", () => {
  it("maps the plugin's events onto Claude's names with session, folder and source", () => {
    for (const event of ["SessionStart", "Stop", "SessionEnd"]) {
      expect(adaptHook("opencode", ev({ event }))).toEqual({ route: "/ev", body: { session_id: "ses_1", cwd: "/work/proj", hook_event_name: event, lotse_source: "opencode" } });
    }
    expect(body({ event: "StopFailure", error: { name: "APIError" } }).error).toBe('{"name":"APIError"}');
    expect(body({ event: "PostToolUseFailure", tool: "read", input: { path: "a" }, error: "ENOENT" })).toMatchObject({ tool_name: "Read", error: "ENOENT" });
    expect(normalizeHook(body({ event: "SessionStart" }), 1)?.source).toBe("opencode");
  });
  it("drops anything that is not one of its events or has no session", () => {
    for (const bad of [null, [], "x", { event: "PreToolUse" }, { event: "PreToolUse", sessionID: "" }, ev({ event: "UserPromptSubmit" }), ev({ event: "preToolUse" }), ev({})]) {
      expect(adaptHook("opencode", bad)).toBeNull();
    }
    // Claude-shaped JSON is not the plugin's
    expect(adaptHook("opencode", { hook_event_name: "PreToolUse", session_id: "s" })).toBeNull();
  });
  it("names the shell tool for the platform, as for Muse", () => {
    expect(body({ event: "PreToolUse", tool: "shell", input: { command: "ls" } }, "win32")).toMatchObject({ tool_name: "PowerShell", tool_input: { command: "ls" } });
    expect(body({ event: "PreToolUse", tool: "shell", input: { command: "ls" } }, "linux")).toMatchObject({ tool_name: "Bash", tool_input: { command: "ls" } });
  });
  it("maps edit, write and read onto Edit, Write and Read with file_path", () => {
    expect(body({ event: "PreToolUse", tool: "edit", input: { path: "src/a.ts", oldString: "a", newString: "b" } })).toMatchObject({ tool_name: "Edit", tool_input: { file_path: "src/a.ts", oldString: "a", newString: "b" } });
    expect(body({ event: "PostToolUse", tool: "write", input: { path: "a.txt", content: "x" } })).toMatchObject({ tool_name: "Write", tool_input: { file_path: "a.txt", content: "x" } });
    expect(body({ event: "PreToolUse", tool: "read", input: { path: "a.txt" } }).tool_input).toEqual({ file_path: "a.txt" });
    // two different targets, or a target that is not text: unclear
    expect(body({ event: "PreToolUse", tool: "write", input: { path: "a", file_path: "b" } }).tool_input).toMatchObject({ [ALIAS_CONFLICT]: true });
    expect(body({ event: "PreToolUse", tool: "write", input: { path: ["a"] } }).tool_input).toMatchObject({ [ALIAS_CONFLICT]: true });
  });
  it("hands patch to the Codex patch reading as apply_patch, the text in command", () => {
    expect(body({ event: "PreToolUse", tool: "patch", input: { patchText: "*** Begin Patch" } })).toMatchObject({ tool_name: "apply_patch", tool_input: { command: "*** Begin Patch" } });
    expect(body({ event: "PreToolUse", tool: "patch", input: { patchText: "a", command: "b" } }).tool_input).toMatchObject({ [ALIAS_CONFLICT]: true });
  });
  it("makes a shell call in another folder unclear, the session folder itself is fine", () => {
    expect(body({ event: "PreToolUse", tool: "shell", input: { command: "ls", workdir: "/work/proj" } }).tool_input).not.toHaveProperty(ALIAS_CONFLICT);
    expect(body({ event: "PreToolUse", tool: "shell", input: { command: "ls", workdir: "." } }).tool_input).not.toHaveProperty(ALIAS_CONFLICT);
    expect(body({ event: "PreToolUse", tool: "shell", input: { command: "ls", workdir: "/etc" } }).tool_input).toMatchObject({ [ALIAS_CONFLICT]: true });
    expect(body({ event: "PreToolUse", tool: "shell", input: { command: "ls", workdir: "sub" } }).tool_input).toMatchObject({ [ALIAS_CONFLICT]: true });
    expect(adaptHook("opencode", { ...ev({ event: "PreToolUse", tool: "shell", input: { command: "ls", workdir: "C:\\WORK\\Proj" } }), cwd: "c:\\work\\proj" }, "win32")?.body).not.toHaveProperty(["tool_input", ALIAS_CONFLICT]);
  });
  it("sends a permission request with the joined call to /perm as that call", () => {
    const r = adaptHook("opencode", ev({ event: "PermissionRequest", action: "shell", resources: ["rm -rf x"], tool: "shell", input: { command: "rm -rf x" } }), "linux");
    expect(r).toEqual({ route: "/perm", body: { session_id: "ses_1", cwd: "/work/proj", hook_event_name: "PermissionRequest", lotse_source: "opencode", tool_name: "Bash", tool_input: { command: "rm -rf x" } } });
    expect(body({ event: "PermissionRequest", action: "edit", resources: ["a.ts"], tool: "write", input: { path: "a.ts", content: "" } })).toMatchObject({ tool_name: "Write", tool_input: { file_path: "a.ts" } });
  });
  it("without the joined call: only a one-file read is taken as it is; everything else is unclear", () => {
    expect(body({ event: "PermissionRequest", action: "read", resources: [".env"] })).toMatchObject({ tool_name: "Read", tool_input: { file_path: ".env" } });
    const shell = body({ event: "PermissionRequest", action: "shell", resources: ["echo a", "echo b"] }, "win32");
    expect(shell).toMatchObject({ tool_name: "PowerShell", tool_input: { command: "echo a\necho b", [ALIAS_CONFLICT]: true } });
    expect(body({ event: "PermissionRequest", action: "edit", resources: ["a.ts", "b.ts"] }).tool_input).toEqual({ file_path: "a.ts", [ALIAS_CONFLICT]: true });
    expect(body({ event: "PermissionRequest", action: "read", resources: ["a", "b"] }).tool_input).toMatchObject({ [ALIAS_CONFLICT]: true });
    expect(body({ event: "PermissionRequest", action: "external_directory", resources: ["/etc/*"] })).toMatchObject({ tool_name: "external_directory", tool_input: { [ALIAS_CONFLICT]: true } });
    expect(body({ event: "PermissionRequest" })).toMatchObject({ tool_name: "permission", tool_input: { [ALIAS_CONFLICT]: true } });
    // a joined call of another kind than the action does not count
    expect(body({ event: "PermissionRequest", action: "shell", resources: ["ls"], tool: "read", input: { path: "a" } })).toMatchObject({ tool_name: "Bash", tool_input: { [ALIAS_CONFLICT]: true } });
  });
});

describe("classifier for OpenCode calls", () => {
  const cwd = tempDir("lotse-oc-risk-");
  const rate = (o: Obj, platform: NodeJS.Platform = process.platform, dir: string = cwd) => {
    const b = adaptHook("opencode", { ...ev(o), cwd: dir }, platform)!.body;
    return classifyRisk(b.tool_name as string, b.tool_input as Obj, dir, "opencode", platform).risk;
  };
  const PATCH = (lines: string[]) => ["*** Begin Patch", ...lines, "*** End Patch"].join("\n");
  it("reads a patch by its file headers and fails closed", () => {
    expect(rate({ event: "PreToolUse", tool: "patch", input: { patchText: PATCH(["*** Add File: a.txt", "+hi"]) } })).toBe("safe");
    expect(rate({ event: "PreToolUse", tool: "patch", input: { patchText: PATCH(["*** Delete File: a.txt"]) } })).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "patch", input: { patchText: PATCH(["*** Frobnicate: a.txt"]) } })).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "patch", input: { patchText: PATCH(["*** Add File: ../x.txt", "+hi"]) } })).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "patch", input: {} })).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "patch", input: { patchText: PATCH(["*** Add File: a.txt"]), extra: 1 } })).toBe("destructive");
  });
  it("rates a permission without the joined call two-hand, except a read", () => {
    expect(rate({ event: "PermissionRequest", action: "shell", resources: ["ls"] })).toBe("destructive");
    expect(rate({ event: "PermissionRequest", action: "edit", resources: ["a.txt"] })).toBe("destructive");
    expect(rate({ event: "PermissionRequest", action: "read", resources: ["a.txt"] })).toBe("safe");
    expect(rate({ event: "PermissionRequest", action: "shell", resources: ["ls"], tool: "shell", input: { command: "ls" } })).toBe("safe");
  });
  it("on Windows reads every OpenCode command by the bash and the PowerShell rules", () => {
    const win = "C:\\work\\proj";
    expect(rate({ event: "PreToolUse", tool: "shell", input: { command: "Set-Content ..\\x.txt hi" } }, "win32", win)).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "shell", input: { command: "echo hi > ..\\x.txt" } }, "win32", win)).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "shell", input: { command: "git status" } }, "win32", win)).toBe("safe");
  });
  it("an unknown tool without command or path is unclear", () => {
    expect(rate({ event: "PreToolUse", tool: "frobnicate", input: { x: 1 } })).toBe("destructive");
    expect(rate({ event: "PreToolUse", tool: "glob", input: { pattern: "*" } })).toBe("safe");
  });
});

describe("shim outputs for opencode", () => {
  it("prints allow only for an exact allow and deny with a message, nothing otherwise", () => {
    expect(shimOutput("opencode", ALLOW)).toBe('{"decision":"allow"}');
    expect(shimOutput("opencode", DENY)).toBe('{"decision":"deny","message":"not now"}');
    expect(JSON.parse(shimOutput("opencode", { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: "  " } } })).message).not.toBe("");
    for (const r of [{}, undefined, null, UNREACHABLE, { decision: "allow" }, { hookSpecificOutput: { hookEventName: "PreToolUse", decision: { behavior: "allow" } } }, { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "Allow" } } }]) {
      expect(shimOutput("opencode", r)).toBe("");
    }
  });
  it("refuses a pre-tool call and a halted request in the plugin's deny form", () => {
    expect(preToolDeny("opencode", "stop")).toBe('{"decision":"deny","message":"stop"}');
    expect(JSON.parse(haltRefusal("opencode", "pre"))).toEqual({ decision: "deny", message: HALT_MESSAGE });
    expect(JSON.parse(haltRefusal("opencode", "perm"))).toEqual({ decision: "deny", message: HALT_MESSAGE });
    expect(ENFORCEMENT.opencode).toBe("every-step");
    expect(hookKind("opencode", "PreToolUse")).toBe("pre");
    expect(hookKind("opencode", "PermissionRequest")).toBe("perm");
    expect(hookKind("opencode", undefined)).toBeUndefined();
  });
  const OPTS = { evMs: 400, permMs: 5000, platform: "linux" as NodeJS.Platform };
  const post = (reply: unknown) => {
    const calls: { path: string; body: Obj }[] = [];
    const f: ShimPost = async (path, b) => {
      calls.push({ path, body: JSON.parse(b) });
      return reply;
    };
    return { f, calls };
  };
  it("runShim: a permission waits on /perm; a halted bridge's pre-tool refusal becomes the deny; failures print nothing", async () => {
    const p = post(ALLOW);
    expect(await runShim("opencode", JSON.stringify(ev({ event: "PermissionRequest", action: "read", resources: ["a"] })), p.f, OPTS)).toBe('{"decision":"allow"}');
    expect(p.calls[0].path).toBe("/perm");
    const halted = post(preToolHaltReply());
    expect(await runShim("opencode", JSON.stringify(ev({ event: "PreToolUse", tool: "read", input: { path: "a" } })), halted.f, { ...OPTS, kind: "pre" })).toBe(JSON.stringify({ decision: "deny", message: HALT_MESSAGE }));
    expect(await runShim("opencode", JSON.stringify(ev({ event: "PreToolUse", tool: "read", input: {} })), post({}).f, OPTS)).toBe("");
    expect(await runShim("opencode", "{not json", post(ALLOW).f, OPTS)).toBe("");
    expect(await runShim("opencode", JSON.stringify(ev({ event: "PermissionRequest" })), post(UNREACHABLE).f, OPTS)).toBe("");
    // no answer from the bridge while the halt marker says halted: the plugin's deny
    expect(await runShim("opencode", "", post(undefined).f, { ...OPTS, kind: "perm", haltMarker: () => true })).toBe(JSON.stringify({ decision: "deny", message: HALT_MESSAGE }));
  });
});

// ---- the generated plugin ----

describe("opencodePluginSource", () => {
  it("embeds both paths as JSON strings, spaces and backslashes included, and is deterministic", () => {
    const node = "C:\\Program Files\\node js\\node.exe";
    const cli = 'C:\\Users\\a b\\"quoted"\\dist\\cli.js';
    const src = opencodePluginSource(node, cli);
    expect(src.startsWith(OPENCODE_PLUGIN_MARKER)).toBe(true);
    expect(src).toContain(`const NODE = ${JSON.stringify(node)};`);
    expect(src).toContain(`const CLI = ${JSON.stringify(cli)};`);
    expect(src).toBe(opencodePluginSource(node, cli));
    expect(src).not.toMatch(/@opencode|from "effect"/);
    expect(opencodePluginText('"C:/n o/node.exe" "C:/x y/cli.js"')).toBe(opencodePluginSource("C:/n o/node.exe", "C:/x y/cli.js"));
    expect(opencodePluginText('node "/x/cli.js"')).toBe(opencodePluginSource("node", "/x/cli.js"));
  });
});

/** A fake OpenCode plugin context: records the hooks and lets a test publish events. */
function fakeContext(directory: string) {
  const hooks: Record<string, (e: Obj) => Promise<void>> = {};
  const listeners: Record<string, ((d: unknown) => void)[]> = {};
  const ctx = {
    location: { directory },
    tool: { hook: (name: string, fn: (e: Obj) => Promise<void>) => void (hooks[`tool.${name}`] = fn) },
    permission: { hook: (name: string, fn: (e: Obj) => Promise<void>) => void (hooks[`permission.${name}`] = fn) },
    event: {
      subscribe({ types, signal }: { types: string[]; signal: AbortSignal }) {
        const queue: unknown[] = [];
        let wake: (() => void) | null = null;
        for (const t of types) (listeners[t] ??= []).push((d) => {
          queue.push({ type: t, data: d });
          wake?.();
        });
        signal.addEventListener("abort", () => wake?.());
        return (async function* () {
          while (!signal.aborted) {
            if (queue.length > 0) yield queue.shift();
            else await new Promise<void>((r) => (wake = r));
          }
        })();
      },
    },
  };
  return { ctx, hooks, publish: (type: string, data: unknown) => (listeners[type] ?? []).forEach((f) => f(data)) };
}

describe("the generated plugin, run against a fake hook", () => {
  const dir = tempDir("lotse oc plugin ");
  const log = join(dir, "calls.ndjson");
  const cli = join(dir, "fake cli", "cli.js");
  mkdirSync(join(dir, "fake cli"));
  // The fake hook logs its arguments and stdin, and answers by a word in the payload.
  writeFileSync(
    cli,
    `const fs = require("node:fs");
let s = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (c) => (s += c));
process.stdin.on("end", () => {
  fs.appendFileSync(${JSON.stringify(log)}, JSON.stringify({ args: process.argv.slice(2), payload: JSON.parse(s) }) + "\\n");
  if (s.includes("SAY_DENY")) process.stdout.write(JSON.stringify({ decision: "deny", message: "no from the headset" }));
  else if (s.includes("SAY_ALLOW")) process.stdout.write(JSON.stringify({ decision: "allow" }));
  else if (s.includes("SAY_ODD")) process.stdout.write(JSON.stringify({ decision: "Allow" }));
  else if (s.includes("SAY_GARBAGE")) process.stdout.write("{not json");
});
`,
  );
  writeFileSync(join(dir, "fake cli", "package.json"), '{"type":"commonjs"}');
  const pluginFile = join(dir, "plugin dir", "index.js");
  mkdirSync(join(dir, "plugin dir"));
  writeFileSync(pluginFile, opencodePluginSource(process.execPath, cli));
  writeFileSync(join(dir, "plugin dir", "package.json"), '{"type":"module"}');
  const calls = (): { args: string[]; payload: Obj }[] =>
    existsSync(log) ? readFileSync(log, "utf8").trim().split("\n").filter(Boolean).map((l) => JSON.parse(l)) : [];
  const waitFor = async (n: number, sid?: string) => {
    const count = () => calls().filter((c) => sid === undefined || c.payload.sessionID === sid).length;
    for (let i = 0; i < 100 && count() < n; i++) await new Promise((r) => setTimeout(r, 50));
    return calls();
  };
  const load = async () => {
    const mod = (await import(pathToFileURL(pluginFile).href)) as { default: { id: string; setup(ctx: unknown): Promise<() => void> } };
    const f = fakeContext("/work/oc");
    const cleanup = await mod.default.setup(f.ctx);
    return { ...f, cleanup, id: mod.default.id };
  };

  it("blocks a tool only on the hook's deny, and passes the call to the hook with --event PreToolUse", { timeout: 20_000 }, async () => {
    const p = await load();
    expect(p.id).toBe("marshal");
    const before = p.hooks["tool.execute.before"];
    await expect(before({ tool: "shell", sessionID: "s1", id: "c1", input: { command: "echo SAY_DENY" } })).rejects.toThrow("no from the headset");
    await expect(before({ tool: "shell", sessionID: "s1", id: "c2", input: { command: "echo ok" } })).resolves.toBeUndefined();
    await expect(before({ tool: "shell", sessionID: "s1", id: "c3", input: { command: "echo SAY_GARBAGE" } })).resolves.toBeUndefined();
    await expect(before({ tool: "shell", sessionID: "s1", id: "c4", input: { command: "echo SAY_ALLOW" } })).resolves.toBeUndefined();
    const c = calls();
    expect(c[0]).toEqual({ args: ["hook", "--source", "opencode", "--event", "PreToolUse"], payload: { event: "PreToolUse", sessionID: "s1", cwd: "/work/oc", tool: "shell", input: { command: "echo SAY_DENY" } } });
    p.cleanup();
  });

  it("answers a permission it would ask about from the hook, joined to its call; otherwise leaves the effect alone", { timeout: 20_000 }, async () => {
    const p = await load();
    const before = p.hooks["tool.execute.before"];
    const evaluate = p.hooks["permission.evaluate"];
    await before({ tool: "write", sessionID: "s2", id: "w1", input: { path: "a.txt", content: "SAY_ALLOW" } });
    const perms = () => calls().filter((c) => c.payload.event === "PermissionRequest");
    const n = perms().length;
    const allow: Obj = { sessionID: "s2", action: "edit", resources: ["a.txt"], source: { type: "tool", messageID: "m", id: "w1" }, effect: "ask" };
    await evaluate(allow);
    expect(allow.effect).toBe("allow");
    const sent = perms()[n];
    expect(sent.args).toEqual(["hook", "--source", "opencode", "--event", "PermissionRequest"]);
    expect(sent.payload).toEqual({ event: "PermissionRequest", sessionID: "s2", cwd: "/work/oc", action: "edit", resources: ["a.txt"], tool: "write", input: { path: "a.txt", content: "SAY_ALLOW" } });

    const deny: Obj = { sessionID: "s2", action: "shell", resources: ["SAY_DENY"], effect: "ask" };
    await evaluate(deny);
    expect(deny).toMatchObject({ effect: "deny", message: "no from the headset" });
    for (const word of ["SAY_GARBAGE", "SAY_ODD", "nothing"]) {
      const e: Obj = { sessionID: "s2", action: "shell", resources: [word], effect: "ask" };
      await evaluate(e);
      expect(e.effect).toBe("ask");
      expect(e.message).toBeUndefined();
    }
    // what OpenCode already allows or denies never reaches the headset
    const m = perms().length;
    for (const effect of ["allow", "deny"]) {
      const e: Obj = { sessionID: "s2", action: "shell", resources: ["SAY_ALLOW"], effect };
      await evaluate(e);
      expect(e.effect).toBe(effect);
    }
    expect(perms().length).toBe(m);
    // a broken event never rejects
    await expect(evaluate(null as unknown as Obj)).resolves.toBeUndefined();
    p.cleanup();
  });

  it("reports sessions, finished tool calls and failures, fire and forget", { timeout: 20_000 }, async () => {
    const p = await load();
    p.publish("session.created", { sessionID: "s3", location: { directory: "/work/other" } });
    await waitFor(1, "s3");
    await p.hooks["tool.execute.after"]({ tool: "read", sessionID: "s3", id: "r1", input: { path: "x" }, status: "error", error: { message: "ENOENT" } });
    await waitFor(2, "s3");
    p.publish("session.status", { sessionID: "s3", status: "busy" });
    p.publish("session.status", { sessionID: "s3", status: "idle" });
    const c = (await waitFor(3, "s3")).map((x) => x.payload).filter((x) => x.sessionID === "s3");
    expect(c).toEqual([
      { event: "SessionStart", sessionID: "s3", cwd: "/work/other" },
      { event: "PostToolUseFailure", sessionID: "s3", cwd: "/work/other", tool: "read", input: { path: "x" }, status: "error", error: "ENOENT" },
      { event: "Stop", sessionID: "s3", cwd: "/work/other" },
    ]);
    expect(calls().find((x) => x.payload.sessionID === "s3")?.args).toEqual(["hook", "--source", "opencode"]);
    p.cleanup();
    const ended = (await waitFor(4, "s3")).filter((x) => x.payload.sessionID === "s3").map((x) => x.payload.event);
    expect(ended).toContain("SessionEnd");
  });

  it("gives up silently when the hook cannot start", { timeout: 20_000 }, async () => {
    const broken = join(dir, "plugin dir", "broken.js");
    writeFileSync(broken, opencodePluginSource(join(dir, "no such node.exe"), cli));
    const mod = (await import(pathToFileURL(broken).href)) as { default: { setup(ctx: unknown): Promise<() => void> } };
    const f = fakeContext("/w");
    const cleanup = await mod.default.setup(f.ctx);
    await expect(f.hooks["tool.execute.before"]({ tool: "shell", sessionID: "s", id: "x", input: { command: "SAY_DENY" } })).resolves.toBeUndefined();
    const e: Obj = { sessionID: "s", action: "shell", resources: ["SAY_DENY"], effect: "ask" };
    await f.hooks["permission.evaluate"](e);
    expect(e.effect).toBe("ask");
    cleanup();
  });
});

// ---- installers ----

const CMD = bridgeCommand("/x/dist/cli.js", "/n/node");
const canSymlink = (() => {
  const d = tempDir("lotse-oc-symcheck-");
  try {
    symlinkSync(join(d, "nowhere"), join(d, "link"), "junction");
    unlinkSync(join(d, "link"));
    return true;
  } catch {
    return false;
  }
})();

describe("install-hooks --tool opencode (project)", () => {
  const home = tempDir("lotse-oc-home-");
  const place = (dir: string): HookPlace => ({ dir, tool: "opencode", bridgeCmd: CMD, homeDir: home });
  const pluginOf = (dir: string) => join(dir, ".opencode", "plugins", "marshal", "index.js");

  it("writes the plugin, is idempotent, and removes exactly it again (and its empty folder)", () => {
    const proj = tempDir("lotse-oc-proj-");
    expect(installHooks(place(proj), "command", false)).toEqual({ ok: true, file: pluginOf(proj), changed: true });
    expect(readFileSync(pluginOf(proj), "utf8")).toBe(opencodePluginText(CMD));
    expect(installHooks(place(proj), "command", false)).toEqual({ ok: true, file: pluginOf(proj), changed: false });
    const plan = uninstallPlan(place(proj));
    expect(plan.ok && plan.before !== null && plan.after !== null && Object.keys(plan.after).length === 0).toBe(true);
    expect(uninstallHooks(place(proj))).toEqual({ ok: true, file: pluginOf(proj), changed: true, existed: true });
    expect(existsSync(pluginOf(proj))).toBe(false);
    expect(existsSync(join(proj, ".opencode", "plugins", "marshal"))).toBe(false);
    expect(existsSync(join(proj, ".opencode", "plugins"))).toBe(true);
    expect(uninstallHooks(place(proj))).toEqual({ ok: true, file: pluginOf(proj), changed: false, existed: false });
  });
  it("replaces an older Marshal plugin after a backup; refuses a foreign file unless forced (then backs it up)", () => {
    const proj = tempDir("lotse-oc-proj-");
    mkdirSync(join(proj, ".opencode", "plugins", "marshal"), { recursive: true });
    writeFileSync(pluginOf(proj), opencodePluginText(bridgeCommand("/old/cli.js", "/n/node")));
    expect(installHooks(place(proj), "command", false)).toMatchObject({ ok: true, changed: true });
    expect(readFileSync(pluginOf(proj), "utf8")).toBe(opencodePluginText(CMD));
    expect(readdirSync(join(proj, ".opencode", "plugins", "marshal")).filter((f) => f.startsWith("index.js.lotse-backup-"))).toHaveLength(1);

    writeFileSync(pluginOf(proj), "export default { id: 'mine' }\n");
    const r = installHooks(place(proj), "command", false);
    expect(r.ok).toBe(false);
    expect(readFileSync(pluginOf(proj), "utf8")).toBe("export default { id: 'mine' }\n");
    // uninstall leaves a file that is not exactly Marshal's
    expect(uninstallHooks(place(proj))).toMatchObject({ ok: true, changed: false, existed: true });
    expect(installHooks(place(proj), "command", true)).toMatchObject({ ok: true, changed: true });
    const backups = readdirSync(join(proj, ".opencode", "plugins", "marshal")).filter((f) => f.startsWith("index.js.lotse-backup-"));
    expect(backups.map((b) => readFileSync(join(proj, ".opencode", "plugins", "marshal", b), "utf8"))).toContain("export default { id: 'mine' }\n");
  });
  it("refuses the home directory, a missing folder, and OpenCode's user folder as the project's .opencode", () => {
    expect(installHooks(place(home), "command", false)).toMatchObject({ ok: false });
    expect(installHooks(place(join(home, "missing")), "command", false)).toMatchObject({ ok: false });
    expect(existsSync(join(home, ".opencode"))).toBe(false);
  });
  it.skipIf(!canSymlink)("refuses a link anywhere on the way to the plugin file", () => {
    for (const at of [".opencode", join(".opencode", "plugins"), join(".opencode", "plugins", "marshal")]) {
      const proj = tempDir("lotse-oc-proj-");
      const elsewhere = tempDir("lotse-oc-target-");
      const parts = at.split(/[\\/]/);
      if (parts.length > 1) mkdirSync(join(proj, ...parts.slice(0, -1)), { recursive: true });
      symlinkSync(elsewhere, join(proj, at), "junction");
      expect(installHooks(place(proj), "command", false)).toMatchObject({ ok: false });
      expect(uninstallHooks(place(proj))).toMatchObject({ ok: false });
      expect(readdirSync(elsewhere)).toEqual([]);
    }
  });
});

describe("connect / disconnect --all for OpenCode", () => {
  function opts(home: string, over: Partial<GlobalOpts> = {}): GlobalOpts & { lines: string[]; errs: string[] } {
    const lines: string[] = [];
    const errs: string[] = [];
    return { tools: ["opencode"], explicit: false, home, env: {}, bridgeCmd: CMD, yes: true, ask: async () => "y", log: (l) => lines.push(l), err: (l) => errs.push(l), muse: null, lines, errs, ...over };
  }
  it("skips OpenCode when its config folder is missing, creates it with --tool opencode", async () => {
    const home = tempDir("lotse-oc-g-");
    const o = opts(home);
    expect(await connectAll(o)).toMatchObject({ ok: true, changed: [] });
    expect(o.lines.join("\n")).toContain("OpenCode: not found");
    expect(existsSync(join(home, ".config", "opencode"))).toBe(false);
    const r = await connectAll(opts(home, { explicit: true }));
    expect(r).toMatchObject({ ok: true, changed: [globalFile("opencode", home, {})] });
    expect(readFileSync(join(home, ".config", "opencode", "plugins", "marshal", "index.js"), "utf8")).toBe(opencodePluginText(CMD));
  });
  it("installs into the user folder (XDG_CONFIG_HOME wins), lists it, is idempotent and is removed exactly", async () => {
    const home = tempDir("lotse-oc-g-");
    const xdg = tempDir("lotse-oc-xdg-");
    mkdirSync(join(xdg, "opencode"));
    const env = { XDG_CONFIG_HOME: xdg };
    const file = join(xdg, "opencode", "plugins", "marshal", "index.js");
    expect(globalFile("opencode", home, env)).toBe(file);
    const o = opts(home, { env, yes: false });
    const asked: string[] = [];
    o.ask = async (q) => {
      asked.push(q);
      return "y";
    };
    expect(await connectAll(o)).toMatchObject({ ok: true, changed: [file], tools: ["opencode"] });
    expect(asked[0]).toContain("OpenCode");
    expect(o.lines.join("\n")).toContain("+ plugin:");
    expect(o.lines.join("\n")).toContain("restart OpenCode");
    const again = opts(home, { env });
    expect(await connectAll(again)).toMatchObject({ ok: true, changed: [] });
    expect(again.lines.join("\n")).toContain("already installed");
    const off = opts(home, { env });
    expect(await disconnectAll(off)).toMatchObject({ ok: true, changed: [file] });
    expect(existsSync(file)).toBe(false);
    expect(existsSync(join(xdg, "opencode", "plugins", "marshal"))).toBe(false);
    expect(existsSync(join(xdg, "opencode"))).toBe(true);
    expect(await disconnectAll(opts(home, { env }))).toMatchObject({ ok: true, changed: [] });
  });
  it("refuses a foreign plugin file (writes nothing, not even for the other tools) and never removes it", async () => {
    const home = tempDir("lotse-oc-g-");
    mkdirSync(join(home, ".codex"));
    const file = globalFile("opencode", home, {});
    mkdirSync(join(file, ".."), { recursive: true });
    writeFileSync(file, "// someone else's plugin\n");
    const o = opts(home, { tools: ["codex", "opencode"] });
    expect(await connectAll(o)).toMatchObject({ ok: false });
    expect(existsSync(join(home, ".codex", "hooks.json"))).toBe(false);
    expect(await disconnectAll(opts(home))).toMatchObject({ ok: true, changed: [] });
    expect(readFileSync(file, "utf8")).toBe("// someone else's plugin\n");
  });
  it("replaces an older Marshal plugin with a backup; leaves a look-alike on disconnect and says so", async () => {
    const home = tempDir("lotse-oc-g-");
    const file = globalFile("opencode", home, {});
    mkdirSync(join(file, ".."), { recursive: true });
    const old = opencodePluginText(bridgeCommand("/old/cli.js", "/n/node"));
    writeFileSync(file, old);
    const off = opts(home);
    expect(await disconnectAll(off)).toMatchObject({ ok: true, changed: [] });
    expect(off.lines.join("\n")).toContain("look like");
    expect(await connectAll(opts(home))).toMatchObject({ ok: true, changed: [file] });
    const backups = readdirSync(join(file, "..")).filter((f) => f.startsWith("index.js.lotse-backup-"));
    expect(backups.map((b) => readFileSync(join(file, "..", b), "utf8"))).toEqual([old]);
  });
  it.skipIf(!canSymlink)("refuses a linked plugins folder", async () => {
    const home = tempDir("lotse-oc-g-");
    const elsewhere = tempDir("lotse-oc-target-");
    mkdirSync(join(home, ".config", "opencode"), { recursive: true });
    symlinkSync(elsewhere, join(home, ".config", "opencode", "plugins"), "junction");
    expect(await connectAll(opts(home))).toMatchObject({ ok: false });
    expect(readdirSync(elsewhere)).toEqual([]);
  });
});
