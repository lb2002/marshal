import { afterEach, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { WebSocket } from "ws";
import { postForReply, runShim, type ShimPost } from "../src/hook-shim.js";
import { MUSE_NO_DECISION_DENY, shimOutput } from "../src/adapters.js";
import { MUSE_BRIDGE_OFF_DENY, TIMEOUT, UNREACHABLE } from "../src/hook-fallback.js";
import { startServer } from "../src/server.js";

const raw = (name: string): string => readFileSync(join(import.meta.dirname, "fixtures", "agents", `${name}.json`), "utf8");
const ALLOW = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };
const OPTS = { evMs: 400, permMs: 5000, platform: "linux" as NodeJS.Platform };

/** A fake bridge: records calls and answers with `reply`. */
function fakePost(reply: unknown): { post: ShimPost; calls: { path: string; body: Record<string, unknown>; ms: number }[] } {
  const calls: { path: string; body: Record<string, unknown>; ms: number }[] = [];
  return {
    calls,
    post: async (path, body, ms) => {
      calls.push({ path, body: JSON.parse(body), ms });
      return reply;
    },
  };
}

describe("runShim", () => {
  it("posts a non-approval event to /ev within the event budget and prints nothing", async () => {
    const f = fakePost(ALLOW);
    expect(await runShim("muse", raw("muse-pre-tool-use-shell"), f.post, OPTS)).toBe("");
    expect(f.calls).toEqual([{ path: "/ev", body: expect.objectContaining({ hook_event_name: "PreToolUse", lotse_source: "muse" }), ms: 400 }]);
  });
  it("skips the post when the event budget is used up", async () => {
    const f = fakePost({});
    expect(await runShim("codex", raw("codex-stop"), f.post, { ...OPTS, evMs: 0 })).toBe("");
    expect(f.calls).toEqual([]);
  });
  it("waits for /perm and prints the tool's allow only after a bridge allow", async () => {
    const f = fakePost(ALLOW);
    expect(JSON.parse(await runShim("codex", raw("codex-permission-request-bash"), f.post, OPTS))).toEqual(ALLOW);
    expect(f.calls[0]).toMatchObject({ path: "/perm", ms: 5000 });
  });
  it("Codex prints nothing when the bridge has no decision (its normal prompt takes over)", async () => {
    for (const reply of [{}, undefined, null, "x"]) {
      expect(await runShim("codex", raw("codex-permission-request-bash"), fakePost(reply).post, OPTS)).toBe("");
    }
  });
  it("Muse denies on every no-decision reply ({} from the bridge, bridge down/401/timeout = undefined, junk)", async () => {
    for (const reply of [{}, undefined, null, "allow", { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "maybe" } } }]) {
      expect(await runShim("muse", raw("muse-permission-request-shell"), fakePost(reply).post, OPTS)).toBe(MUSE_NO_DECISION_DENY);
    }
  });
  it("Muse denies when the post throws", async () => {
    const post: ShimPost = async () => {
      throw new Error("boom");
    };
    expect(await runShim("muse", raw("muse-permission-request-shell"), post, OPTS)).toBe(MUSE_NO_DECISION_DENY);
  });
  it("Muse denies a permission event it cannot read (cut stdin, missing session id), without posting it", async () => {
    const f = fakePost(ALLOW);
    const full = raw("muse-permission-request-shell");
    expect(await runShim("muse", full.slice(0, full.length - 20), f.post, OPTS)).toBe(MUSE_NO_DECISION_DENY);
    expect(await runShim("muse", JSON.stringify({ hook_event_name: "PermissionRequest" }), f.post, OPTS)).toBe(MUSE_NO_DECISION_DENY);
    expect(await runShim("muse", '{ "session_id": "m", "hook_event_name" : "PermissionRequest", "tool_inp', f.post, OPTS)).toBe(MUSE_NO_DECISION_DENY);
    expect(f.calls).toEqual([]);
  });
  it("Muse non-approval events stay silent, whatever the bridge does", async () => {
    for (const fx of ["muse-pre-tool-use-shell", "muse-notification", "muse-stop", "muse-post-tool-batch"]) {
      expect(await runShim("muse", raw(fx), fakePost(undefined).post, OPTS)).toBe("");
    }
    expect(await runShim("muse", "not json", fakePost(undefined).post, OPTS)).toBe("");
  });
  it("Muse approval hook (--event PermissionRequest): every path without a headset decision prints the deny", async () => {
    const APPROVAL = { ...OPTS, approval: true };
    for (const input of ["", "not json", "{", JSON.stringify({ hook_event_name: "Stop", session_id: "m" }), JSON.stringify({ session_id: "m" })]) {
      expect(await runShim("muse", input, fakePost(ALLOW).post, APPROVAL), input).toBe(MUSE_NO_DECISION_DENY);
    }
    for (const reply of [{}, undefined, "x"]) {
      expect(await runShim("muse", raw("muse-permission-request-shell"), fakePost(reply).post, APPROVAL)).toBe(MUSE_NO_DECISION_DENY);
    }
    expect(JSON.parse(await runShim("muse", raw("muse-permission-request-shell"), fakePost(ALLOW).post, APPROVAL))).toEqual(ALLOW);
  });
  it("other tools' approval hooks stay silent on the same paths", async () => {
    for (const s of ["claude", "codex"] as const) {
      for (const input of ["", "not json", "{"]) expect(await runShim(s, input, fakePost(ALLOW).post, { ...OPTS, approval: true })).toBe("");
    }
  });
  it("Gemini: a bridge deny becomes decision deny, an allow prints nothing", async () => {
    const deny = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: "Nein" } } };
    expect(JSON.parse(await runShim("gemini", raw("gemini-before-tool-shell"), fakePost(deny).post, OPTS))).toEqual({ decision: "deny", reason: "Nein" });
    expect(await runShim("gemini", raw("gemini-before-tool-shell"), fakePost(ALLOW).post, OPTS)).toBe("");
  });
  it("Gemini: a safe tool is only observed, even if a bridge would answer allow", async () => {
    const f = fakePost(ALLOW);
    expect(await runShim("gemini", raw("gemini-before-tool-read"), f.post, OPTS)).toBe("");
    expect(f.calls.map((c) => c.path)).toEqual(["/ev"]);
  });
  it("Codex prints nothing and posts nothing for input that is not a hook payload", async () => {
    const f = fakePost(ALLOW);
    for (const s of ["", "not json", "[]", '{"hook_event_name":"PermissionRequest"}']) {
      expect(await runShim("codex", s, f.post, OPTS)).toBe("");
    }
    expect(f.calls).toEqual([]);
  });
  it("prints nothing when the post throws", async () => {
    const post: ShimPost = async () => {
      throw new Error("boom");
    };
    expect(await runShim("codex", raw("codex-permission-request-bash"), post, OPTS)).toBe("");
  });
});

describe("runShim: Claude Code", () => {
  it("forwards a non-approval event to /ev and prints nothing (stdout would become Claude's context)", async () => {
    const f = fakePost(ALLOW);
    expect(await runShim("claude", raw("claude-pre-tool-use-read"), f.post, OPTS)).toBe("");
    expect(await runShim("claude", raw("claude-session-start"), f.post, OPTS)).toBe("");
    expect(f.calls.map((c) => c.path)).toEqual(["/ev", "/ev"]);
    expect(f.calls[0].body).not.toHaveProperty("lotse_source");
  });
  it("prints the allow only after a bridge allow", async () => {
    const f = fakePost(ALLOW);
    expect(JSON.parse(await runShim("claude", raw("claude-permission-request-bash"), f.post, OPTS))).toEqual(ALLOW);
    expect(f.calls[0]).toMatchObject({ path: "/perm", ms: 5000 });
  });
  it("stays silent on every no-decision path, so Claude shows its normal dialog", async () => {
    for (const reply of [{}, undefined, null, "allow", { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "maybe" } } }]) {
      expect(await runShim("claude", raw("claude-permission-request-bash"), fakePost(reply).post, OPTS)).toBe("");
    }
    const post: ShimPost = async () => {
      throw new Error("boom");
    };
    expect(await runShim("claude", raw("claude-permission-request-bash"), post, OPTS)).toBe("");
    const full = raw("claude-permission-request-bash");
    expect(await runShim("claude", full.slice(0, full.length - 20), fakePost(ALLOW).post, OPTS)).toBe("");
  });
});

describe("postForReply", () => {
  let server: Server | null = null;
  afterEach(async () => {
    await new Promise<void>((r) => (server ? server.close(() => r()) : r()));
    server = null;
  });
  const listen = (handler: Parameters<typeof createServer>[1]): Promise<number> =>
    new Promise((r) => {
      server = createServer(handler);
      server.listen(0, "127.0.0.1", () => r((server!.address() as AddressInfo).port));
    });

  it("resolves UNREACHABLE when nothing listens (ECONNREFUSED: the bridge is off)", async () => {
    const port = await listen(() => {});
    await new Promise<void>((r) => server!.close(() => r()));
    server = null;
    expect(await postForReply(port, "/perm", "{}", null, 1000)).toBe(UNREACHABLE);
  });
  it("UNREACHABLE: Muse gets the bridge-off deny, the other tools print nothing", async () => {
    const post: ShimPost = async () => UNREACHABLE;
    expect(await runShim("muse", raw("muse-permission-request-shell"), post, OPTS)).toBe(MUSE_BRIDGE_OFF_DENY);
    expect(JSON.parse(MUSE_BRIDGE_OFF_DENY).hookSpecificOutput.decision).toEqual({
      behavior: "deny",
      message: "Marshal is not running, so this is denied. The user decides how to go on.",
    });
    expect(await runShim("codex", raw("codex-permission-request-bash"), post, OPTS)).toBe("");
    expect(await runShim("claude", raw("claude-permission-request-bash"), post, OPTS)).toBe("");
    expect(await runShim("gemini", raw("gemini-before-tool-shell"), post, OPTS)).toBe("");
  });
  it("a timeout is TIMEOUT, not UNREACHABLE (Muse keeps the no-decision deny)", async () => {
    const port = await listen(() => {});
    expect(await postForReply(port, "/perm", "{}", null, 200)).toBe(TIMEOUT);
    expect(shimOutput("muse", TIMEOUT)).toBe(MUSE_NO_DECISION_DENY);
    expect(shimOutput("claude", TIMEOUT)).toBe("");
  });
  it("sends the token header and returns the parsed 200 body", async () => {
    let token: unknown;
    const port = await listen((req, res) => {
      token = req.headers["x-lotse-token"];
      req.resume();
      req.on("end", () => res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify(ALLOW)));
    });
    expect(await postForReply(port, "/perm", "{}", "tok", 1000)).toEqual(ALLOW);
    expect(token).toBe("tok");
  });
  it("ignores the body of a non-200 answer", async () => {
    const port = await listen((req, res) => {
      req.resume();
      req.on("end", () => res.writeHead(401).end(JSON.stringify(ALLOW)));
    });
    expect(await postForReply(port, "/perm", "{}", null, 1000)).toBeUndefined();
  });
  it("resolves undefined on bad JSON", async () => {
    const port = await listen((req, res) => {
      req.resume();
      req.on("end", () => res.writeHead(200).end("allow"));
    });
    expect(await postForReply(port, "/perm", "{}", null, 1000)).toBeUndefined();
  });
  it("gives up at its deadline when the bridge never answers", async () => {
    const port = await listen((req) => req.resume());
    const t0 = Date.now();
    expect(await postForReply(port, "/perm", "{}", null, 200)).toBe(TIMEOUT);
    expect(Date.now() - t0).toBeLessThan(2000);
  });
});

describe("shim against the real bridge", () => {
  type Handle = Awaited<ReturnType<typeof startServer>>;
  let handle: Handle | null = null;
  let ws: WebSocket | null = null;
  afterEach(async () => {
    ws?.terminate();
    ws = null;
    await handle?.close();
    handle = null;
  });

  async function pairedHeadset(onPerm: (m: Record<string, any>, send: (x: unknown) => void) => void): Promise<Record<string, any>[]> {
    const msgs: Record<string, any>[] = [];
    ws = new WebSocket(`ws://127.0.0.1:${handle!.wsPort}`);
    const sock = ws;
    const send = (x: unknown) => sock.send(JSON.stringify(x));
    await new Promise<void>((res, rej) => {
      sock.once("open", () => res());
      sock.once("error", rej);
    });
    const ready = new Promise<void>((res) => {
      sock.on("message", (d) => {
        const m = JSON.parse(d.toString());
        msgs.push(m);
        if (m.type === "paired") res();
        if (m.type === "perm_open") onPerm(m, send);
      });
    });
    send({ type: "hello", code: "123456" });
    await ready;
    return msgs;
  }

  const shimPost = (token: string): ShimPost => (path, body, ms) => postForReply(handle!.httpPort, path, body, token, ms);

  it("a Muse approval allowed on the headset prints allow, and the agent is named Muse 1", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 5000, budgetUsd: 5, ingestToken: "tok" });
    const msgs = await pairedHeadset((m, send) => send({ type: "perm_decision", decision: { id: m.request.id, behavior: "allow", confirm: "two-hand" } }));
    const out = await runShim("muse", raw("muse-permission-request-shell"), shimPost("tok"), OPTS);
    expect(JSON.parse(out)).toEqual(ALLOW);
    expect(msgs.find((m) => m.type === "agent")?.agent).toMatchObject({ name: "Muse 1", source: "muse", status: "blocked" });
  });
  it("prints nothing when no headset is paired (the tool's own prompt takes over)", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 5000, budgetUsd: 5, ingestToken: "tok" });
    expect(await runShim("codex", raw("codex-permission-request-bash"), shimPost("tok"), OPTS)).toBe("");
  });
  it("never allows with a wrong token, even though a headset would allow (Codex silent, Muse deny)", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 5000, budgetUsd: 5, ingestToken: "tok" });
    await pairedHeadset((m, send) => send({ type: "perm_decision", decision: { id: m.request.id, behavior: "allow", confirm: "two-hand" } }));
    expect(await runShim("codex", raw("codex-permission-request-bash"), shimPost("wrong"), OPTS)).toBe("");
    expect(await runShim("muse", raw("muse-permission-request-shell"), shimPost("wrong"), OPTS)).toBe(MUSE_NO_DECISION_DENY);
  });
  it("Muse: no headset paired and a hold timeout both give the no-decision deny", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 150, budgetUsd: 5, ingestToken: "tok" });
    expect(await runShim("muse", raw("muse-permission-request-shell"), shimPost("tok"), OPTS)).toBe(MUSE_NO_DECISION_DENY);
    await pairedHeadset(() => {});
    expect(await runShim("muse", raw("muse-permission-request-shell"), shimPost("tok"), OPTS)).toBe(MUSE_NO_DECISION_DENY);
  });
  it("Muse: bridge down gives the bridge-off deny", async () => {
    const port = await new Promise<number>((r) => {
      const s = createServer();
      s.listen(0, "127.0.0.1", () => {
        const p = (s.address() as AddressInfo).port;
        s.close(() => r(p));
      });
    });
    const post: ShimPost = (path, body, ms) => postForReply(port, path, body, "tok", ms);
    expect(await runShim("muse", raw("muse-permission-request-shell"), post, OPTS)).toBe(MUSE_BRIDGE_OFF_DENY);
  });
  it("prints nothing when the hold times out", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 150, budgetUsd: 5, ingestToken: "tok" });
    await pairedHeadset(() => {});
    expect(await runShim("codex", raw("codex-permission-request-bash"), shimPost("tok"), OPTS)).toBe("");
  });
  it("a Claude approval allowed on the headset prints Claude's allow; a wrong token or a timeout prints nothing", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 5000, budgetUsd: 5, ingestToken: "tok" });
    const msgs = await pairedHeadset((m, send) => send({ type: "perm_decision", decision: { id: m.request.id, behavior: "allow", confirm: "two-hand" } }));
    expect(JSON.parse(await runShim("claude", raw("claude-permission-request-bash"), shimPost("tok"), OPTS))).toEqual(ALLOW);
    expect(msgs.find((m) => m.type === "agent")?.agent).toMatchObject({ name: "Agent 1", source: "claude" });
    expect(await runShim("claude", raw("claude-permission-request-bash"), shimPost("wrong"), OPTS)).toBe("");
  });
  it("a Gemini deny on the headset prints decision deny", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 5000, budgetUsd: 5, ingestToken: "tok" });
    await pairedHeadset((m, send) => send({ type: "perm_decision", decision: { id: m.request.id, behavior: "deny", message: "Bitte nicht" } }));
    expect(JSON.parse(await runShim("gemini", raw("gemini-before-tool-shell"), shimPost("tok"), OPTS))).toEqual({ decision: "deny", reason: "Bitte nicht" });
  });
});
