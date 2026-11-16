import { afterEach, describe, expect, it } from "vitest";
import WebSocket from "ws";
import { startServer } from "../src/server.js";
import { parseClientMessage } from "../src/protocol.js";

const CODE = "246810";
let handle: Awaited<ReturnType<typeof startServer>> | null = null;
afterEach(async () => {
  await handle?.close();
  handle = null;
});

function next(ws: WebSocket, type: string): Promise<Record<string, unknown>> {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`no ${type}`)), 2000);
    ws.on("message", (raw) => {
      const m = JSON.parse(raw.toString()) as Record<string, unknown>;
      if (m.type === type) {
        clearTimeout(t);
        resolve(m);
      }
    });
  });
}

describe("ping / pong", () => {
  it("parses a ping frame", () => {
    expect(parseClientMessage('{"type":"ping"}')).toEqual({ type: "ping" });
  });

  it("answers pong only on the paired socket", async () => {
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: CODE, holdMs: 30_000, budgetUsd: 5 });
    const url = `ws://127.0.0.1:${handle.wsPort}`;
    const a = new WebSocket(url);
    await new Promise((r) => a.once("open", r));
    const paired = next(a, "paired");
    a.send(JSON.stringify({ type: "hello", code: CODE }));
    await paired;
    const pong = next(a, "pong");
    a.send('{"type":"ping"}');
    expect(await pong).toEqual({ type: "pong" });

    const b = new WebSocket(url);
    await new Promise((r) => b.once("open", r));
    const err = next(b, "error");
    b.send('{"type":"ping"}');
    expect((await err).error).toBe("not-paired");
    a.close();
    b.close();
  });
});
