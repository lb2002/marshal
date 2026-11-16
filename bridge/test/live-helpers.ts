// A real bridge plus a paired fake headset, for tests of the live message flow.
import { WebSocket } from "ws";
import { startServer, type ServerHandle, type ServerOptions } from "../src/server.js";

export type Msg = Record<string, any>;
export const CODE = "123456";

export interface Headset {
  msgs: Msg[];
  send(m: unknown): void;
  next(pred: (m: Msg) => boolean, ms?: number): Promise<Msg>;
  close(): void;
}

export async function bootLive(over: Partial<ServerOptions> = {}): Promise<ServerHandle> {
  return startServer({ httpPort: 0, wsPort: 0, pairingCode: CODE, holdMs: 30_000, budgetUsd: 5, ...over });
}

export function postTo(handle: ServerHandle, path: string, body: unknown): Promise<Response> {
  return fetch(`http://127.0.0.1:${handle.httpPort}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
}

export async function pairHeadset(handle: ServerHandle): Promise<Headset> {
  const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
  const msgs: Msg[] = [];
  const waiters: (() => void)[] = [];
  ws.on("message", (d) => {
    msgs.push(JSON.parse(d.toString()));
    waiters.splice(0).forEach((w) => w());
  });
  await new Promise<void>((res, rej) => {
    ws.once("open", () => res());
    ws.once("error", rej);
  });
  const h: Headset = {
    msgs,
    send: (m) => ws.send(typeof m === "string" ? m : JSON.stringify(m)),
    close: () => ws.terminate(),
    async next(pred, ms = 3000) {
      const deadline = Date.now() + ms;
      let from = 0;
      for (;;) {
        for (; from < msgs.length; from++) if (pred(msgs[from])) return msgs[from];
        const left = deadline - Date.now();
        if (left <= 0) throw new Error(`timeout waiting for message; got ${JSON.stringify(msgs).slice(0, 2000)}`);
        await new Promise<void>((res) => {
          waiters.push(res);
          setTimeout(res, left);
        });
      }
    },
  };
  h.send({ type: "hello", code: CODE });
  await h.next((m) => m.type === "paired");
  return h;
}

/** Deterministic "nothing more is queued": a bad frame's error reply comes after anything sent before it. */
export async function settle(h: Headset): Promise<void> {
  const before = h.msgs.filter((m) => m.type === "error" && m.error === "bad-message").length;
  h.send("not json");
  await h.next(() => h.msgs.filter((m) => m.type === "error" && m.error === "bad-message").length > before);
}
