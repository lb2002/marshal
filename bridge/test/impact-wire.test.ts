// perm_impact on the wire: after perm_open, only while open, in the snapshot, dropped on close.
import { afterEach, describe, expect, it } from "vitest";
import type { Impact, ImpactRequest } from "../src/impact.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset, postTo, settle } from "./live-helpers.js";
import type { Headset, Msg } from "./live-helpers.js";
import { WebSocket } from "ws";

let handle: ServerHandle | undefined;
let headset: Headset | undefined;
afterEach(async () => {
  headset?.close();
  headset = undefined;
  await handle?.close();
  handle = undefined;
});

const IMPACT: Impact = { kind: "delete", target: "../legacy-site", files: 214, outsideProject: true, recoverable: "no", reason: "Not in a Git project: gone for good." };
const perm = (command: string, sid = "c1") => ({ session_id: sid, hook_event_name: "PermissionRequest", cwd: "/tmp/shop", tool_name: "Bash", tool_input: { command } });

/** An impact function the test controls: resolves when release() is called. */
function controlled() {
  const calls: ImpactRequest[] = [];
  let release: (i: Impact | null) => void = () => {};
  const fn = (req: ImpactRequest) => {
    calls.push(req);
    return new Promise<Impact | null>((r) => (release = r));
  };
  return { fn, calls, release: (i: Impact | null) => release(i) };
}

async function resume(h: ServerHandle, session: string): Promise<Msg> {
  const ws = new WebSocket(`ws://127.0.0.1:${h.wsPort}`);
  try {
    return await new Promise<Msg>((res, rej) => {
      ws.on("message", (d) => {
        const m = JSON.parse(d.toString()) as Msg;
        if (m.type === "snapshot") res(m);
      });
      ws.once("open", () => ws.send(JSON.stringify({ type: "hello", session })));
      ws.once("error", rej);
      setTimeout(() => rej(new Error("no snapshot")), 3000);
    });
  } finally {
    ws.terminate();
  }
}

describe("perm_impact", () => {
  it("follows perm_open without delaying it, and the snapshot carries it while the request is open", async () => {
    const c = controlled();
    handle = await bootLive({ impact: c.fn });
    headset = await pairHeadset(handle);
    void postTo(handle, "/perm", perm("rm -rf ../legacy-site"));
    const open = await headset.next((m) => m.type === "perm_open");
    await new Promise((r) => setTimeout(r, 20));
    expect(c.calls).toHaveLength(1);
    expect(c.calls[0]).toMatchObject({ tool: "Bash", input: { command: "rm -rf ../legacy-site" }, cwd: "/tmp/shop", risk: { risk: "destructive" } });
    c.release(IMPACT);
    const m = await headset.next((x) => x.type === "perm_impact");
    expect(m).toEqual({ type: "perm_impact", id: open.request.id, impact: IMPACT });
    const session = (headset.msgs.find((x) => x.type === "paired") as Msg).session as string;
    // A second socket with the session token replaces the first (the request stays open).
    const snap = await resume(handle, session);
    expect(snap.impacts).toEqual({ [open.request.id]: IMPACT });
  });

  it("is not sent for a request that already closed, and the snapshot drops it", async () => {
    const c = controlled();
    handle = await bootLive({ impact: c.fn });
    headset = await pairHeadset(handle);
    void postTo(handle, "/perm", perm("rm -rf ../legacy-site"));
    const open = await headset.next((m) => m.type === "perm_open");
    headset.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await headset.next((m) => m.type === "perm_closed");
    c.release(IMPACT);
    await new Promise((r) => setTimeout(r, 30));
    await settle(headset);
    expect(headset.msgs.some((m) => m.type === "perm_impact")).toBe(false);
  });

  it("nothing is computed or sent for a request that is not destructive", async () => {
    const c = controlled();
    handle = await bootLive({ impact: c.fn });
    headset = await pairHeadset(handle);
    void postTo(handle, "/perm", perm("ls -la"));
    const open = await headset.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).toBe("safe");
    await new Promise((r) => setTimeout(r, 30));
    expect(c.calls).toEqual([]);
    await settle(headset);
    expect(headset.msgs.some((m) => m.type === "perm_impact")).toBe(false);
  });

  it("a null impact sends nothing; a throwing impact function breaks nothing", async () => {
    handle = await bootLive({ impact: async () => { throw new Error("boom"); } });
    headset = await pairHeadset(handle);
    void postTo(handle, "/perm", perm("rm -rf x"));
    const open = await headset.next((m) => m.type === "perm_open");
    await new Promise((r) => setTimeout(r, 30));
    headset.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await headset.next((m) => m.type === "perm_closed");
    expect(headset.msgs.some((m) => m.type === "perm_impact")).toBe(false);
  });

  it("50 forged requests with different session ids: one computation at a time in the whole bridge, at most 9 run", async () => {
    let running = 0;
    let maxRunning = 0;
    let calls = 0;
    const fn = async () => {
      calls++;
      running++;
      maxRunning = Math.max(maxRunning, running);
      await new Promise((r) => setTimeout(r, 20));
      running--;
      return IMPACT;
    };
    handle = await bootLive({ impact: fn });
    headset = await pairHeadset(handle);
    const posts: Promise<Response>[] = [];
    for (let k = 0; k < 50; k++) posts.push(postTo(handle, "/perm", perm(`rm -rf x${k}`, `forged-${k}`)));
    for (let k = 0; k < 50; k++) await headset.next((m) => m.type === "perm_open" && m.request.sid === `forged-${k}`, 5000);
    await new Promise((r) => setTimeout(r, 600));
    expect(maxRunning).toBe(1);
    expect(calls).toBeLessThanOrEqual(9);
    expect(headset.msgs.filter((m) => m.type === "perm_impact").length).toBe(calls);
    await handle.close();
    handle = undefined;
    await Promise.allSettled(posts);
  }, 20_000);

  it("past the global budget the impact is unknown and the impact function is not called", async () => {
    let clock = 0;
    let calls = 0;
    const fn = async () => {
      calls++;
      clock += 700; // each computation "takes" 700 ms of the bridge clock
      return IMPACT;
    };
    handle = await bootLive({ impact: fn, now: () => clock });
    headset = await pairHeadset(handle);
    for (let k = 0; k < 5; k++) {
      void postTo(handle, "/perm", perm(`rm -rf y${k}`, `b-${k}`));
      await headset.next((m) => m.type === "perm_impact" && headset!.msgs.filter((x) => x.type === "perm_impact").length === k + 1, 3000);
    }
    expect(calls).toBe(3);
    const impacts = headset.msgs.filter((m) => m.type === "perm_impact").map((m) => m.impact);
    expect(impacts.slice(3)).toEqual([
      { kind: "other", recoverable: "unknown", reason: "Target unclear. Effects unknown." },
      { kind: "other", recoverable: "unknown", reason: "Target unclear. Effects unknown." },
    ]);
  });

  it("demo injection computes no impact (the recording is not this machine)", async () => {
    const c = controlled();
    handle = await bootLive({ impact: c.fn });
    headset = await pairHeadset(handle);
    handle.inject({ sid: "d1", t: Date.now(), kind: "permission", tool: "Bash", command: "rm -rf ../legacy-site", cwd: "<repo>" });
    await headset.next((m) => m.type === "perm_open");
    await new Promise((r) => setTimeout(r, 30));
    expect(c.calls).toEqual([]);
  });
});
