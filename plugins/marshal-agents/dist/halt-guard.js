// While all agents are halted, Marshal's hooks must not be removed (task-p4 review, finding 1c): `disconnect` and
// `uninstall-hooks` refuse, with or without --yes. Halted means either of:
// - the halt marker `~/.lotse/halt` exists (any entry at that path; a running, halted bridge writes it), or
// - a bridge answers on the hook port that it is halted: an /ev request marked as a pre-tool hook, whose body is
//   not JSON, gets the halt refusal from a halted bridge (server.ts) and 400 from one that is not.
// The probe needs the hook token; without it, or without a bridge, only the marker counts.
import { request } from "node:http";
import { lstatSync } from "node:fs";
import { haltMarkerPath } from "./halt-marker.js";
import { HALT_MESSAGE, PRE_TOOL_HEADER, PRE_TOOL_HEADER_VALUE } from "./halt.js";
import { PRODUCT_NAME } from "./product.js";
export const HALT_REFUSAL = `${PRODUCT_NAME} is halted, so its hooks stay. Release the halt first, from the headset or the bridge console. Nothing was changed.`;
/** Whether the marker path holds anything (a file, a link, a folder: never followed). */
export function haltMarkerPresent(home) {
    try {
        return lstatSync(haltMarkerPath(home), { throwIfNoEntry: false }) !== undefined;
    }
    catch {
        return true; // cannot tell: count as halted
    }
}
/** Asks a bridge on 127.0.0.1:`port` whether it is halted; false when none answers (within `ms`) or it says no. */
export function bridgeSaysHalted(port, token, ms = 1000) {
    if (token === null)
        return Promise.resolve(false);
    return new Promise((resolve) => {
        let done = false;
        const finish = (v) => {
            if (!done) {
                done = true;
                resolve(v);
            }
        };
        const req = request({
            host: "127.0.0.1",
            port,
            path: "/ev",
            method: "POST",
            headers: { "content-type": "application/json", "x-lotse-token": token, [PRE_TOOL_HEADER]: PRE_TOOL_HEADER_VALUE, host: `127.0.0.1:${port}` },
            timeout: ms,
        }, (res) => {
            let body = "";
            res.setEncoding("utf8");
            res.on("data", (c) => {
                if (body.length < 4096)
                    body += c;
            });
            res.on("end", () => finish(res.statusCode === 200 && body.includes(HALT_MESSAGE)));
            res.on("error", () => finish(false));
        });
        req.on("timeout", () => {
            req.destroy();
            finish(false);
        });
        req.on("error", () => finish(false));
        req.end("halt?"); // not JSON on purpose: a bridge that is not halted answers 400 and does nothing
    });
}
/** The refusal text when hooks must not be removed now, else null. */
export async function haltBlocksRemoval(home, port, token) {
    if (haltMarkerPresent(home))
        return HALT_REFUSAL;
    return (await bridgeSaysHalted(port, token)) ? HALT_REFUSAL : null;
}
