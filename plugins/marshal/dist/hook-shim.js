// `lotse hook --source claude|muse|codex|gemini`: the command hook those tools run. It forwards the hook JSON
// to the bridge and, for an approval, prints the tool's own answer. Every failure prints nothing, so the tool
// falls back to its own prompt (Muse: the no-decision deny). It never prints an allow the bridge did not give
// (see adapters.ts).
import { request } from "node:http";
import { adaptHook, isMusePermission, museNoDecision, preToolDeny, shimOutput } from "./adapters.js";
import { APPROVAL_TEXT, PRE_TOOL_TEXT, TIMEOUT, UNREACHABLE } from "./hook-fallback.js";
import { PRE_TOOL_HEADER, PRE_TOOL_HEADER_VALUE, haltRefusal, preToolRefusal } from "./halt.js";
/** The bridge holds a request up to 90 s; the shim waits a little longer, the hook timeout (120 s) longer still. */
export const PERM_WAIT_MS = 100_000;
/** The kind of a hook from its stdin text, for hooks installed without `--event`. */
export function kindFromText(raw) {
    return PRE_TOOL_TEXT.test(raw) ? "pre" : APPROVAL_TEXT.test(raw) ? "perm" : "other";
}
/**
 * Runs one hook: returns what to print on stdout. Never throws. Without a headset decision it is ""
 * (the tool's own prompt), except for a Muse permission event, which gets the no-decision deny.
 */
export async function runShim(source, raw, post, opts) {
    const museApproval = source === "muse" && (opts.approval === true || isMusePermission(raw));
    const fallback = () => (museApproval ? museNoDecision() : "");
    const kind = opts.kind ?? kindFromText(raw);
    // A live bridge's answer (any parsed 200 body) wins over a marker; a refused connection means the marker is stale.
    let answered = false;
    let refused = false;
    const note = (reply) => {
        if (reply === UNREACHABLE)
            refused = true;
        else if (typeof reply === "object" && reply !== null)
            answered = true;
        return reply;
    };
    /** A pre-tool or permission event ending without a decision and without an answer: a live marker means halted. */
    const markerSaysHalted = () => kind !== "other" && !answered && !refused && opts.haltMarker?.() === true;
    const silent = () => (markerSaysHalted() ? haltRefusal(source, kind) : fallback());
    let parsed;
    try {
        parsed = JSON.parse(raw);
    }
    catch {
        return silent(); // stdin slow, empty or cut
    }
    try {
        const req = adaptHook(source, parsed, opts.platform);
        if (!req)
            return silent();
        const body = JSON.stringify(req.body);
        if (req.route === "/ev") {
            // The same single POST as always; only a halted bridge answers a pre-tool hook with a refusal (halt.ts). The
            // header lets it refuse before reading the body. A budget already spent counts as a timeout.
            const pre = req.body.hook_event_name === "PreToolUse";
            const reply = note(opts.evMs > 0 ? await post("/ev", body, opts.evMs, pre ? { [PRE_TOOL_HEADER]: PRE_TOOL_HEADER_VALUE } : undefined) : TIMEOUT);
            if (pre) {
                const refusal = preToolRefusal(reply);
                if (refusal !== null)
                    return preToolDeny(source, refusal);
            }
            // An approval hook that got something else is not understood: Muse fails closed.
            return silent();
        }
        const reply = note(await post("/perm", body, opts.permMs));
        return markerSaysHalted() ? haltRefusal(source, kind) : shimOutput(source, reply);
    }
    catch {
        return silent();
    }
}
/**
 * `GET /reply?sid=`: waits for the headset's reply to this session. The text, or null for anything else
 * (no reply in time, bridge down, wrong token, a bad answer). Never rejects.
 */
export function getReply(port, sid, token, ms, who = {}) {
    return new Promise((resolve) => {
        let settled = false;
        const done = (v) => {
            if (settled)
                return;
            settled = true;
            clearTimeout(timer);
            resolve(v);
        };
        const timer = setTimeout(() => {
            req.destroy();
            done(null);
        }, ms);
        const headers = {};
        if (token)
            headers["X-Lotse-Token"] = token;
        const q = new URLSearchParams({ sid });
        if (who.cwd)
            q.set("cwd", who.cwd);
        if (who.transcript)
            q.set("transcript", who.transcript);
        const req = request({ host: "127.0.0.1", port, path: `/reply?${q.toString()}`, method: "GET", headers }, (res) => {
            let text = "";
            res.setEncoding("utf8");
            res.on("data", (c) => (text += c));
            res.on("error", () => done(null));
            res.on("close", () => done(null));
            res.on("end", () => {
                if (res.statusCode !== 200)
                    return done(null);
                try {
                    const v = JSON.parse(text);
                    const t = typeof v === "object" && v !== null ? v.text : undefined;
                    done(typeof t === "string" && t.trim() !== "" ? t : null);
                }
                catch {
                    done(null);
                }
            });
        });
        req.on("error", () => done(null));
        req.end();
    });
}
/**
 * The real transport: 127.0.0.1 only, token header when known, one overall deadline. ECONNREFUSED gives UNREACHABLE,
 * the deadline TIMEOUT.
 */
export function postForReply(port, path, body, token, ms, extra = {}) {
    return new Promise((resolve) => {
        let settled = false;
        const done = (v) => {
            if (settled)
                return;
            settled = true;
            clearTimeout(timer);
            resolve(v);
        };
        const timer = setTimeout(() => {
            done(TIMEOUT);
            req.destroy();
        }, ms);
        const headers = { ...extra, "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) };
        if (token)
            headers["X-Lotse-Token"] = token;
        const req = request({ host: "127.0.0.1", port, path, method: "POST", headers }, (res) => {
            let text = "";
            res.setEncoding("utf8");
            res.on("data", (c) => (text += c));
            res.on("error", () => done(undefined));
            res.on("close", () => done(undefined)); // after "end" this is a no-op; before it, the answer was cut
            res.on("end", () => {
                if (res.statusCode !== 200)
                    return done(undefined);
                try {
                    done(JSON.parse(text));
                }
                catch {
                    done(undefined);
                }
            });
        });
        req.on("error", (err) => done(err.code === "ECONNREFUSED" ? UNREACHABLE : undefined));
        req.end(body);
    });
}
