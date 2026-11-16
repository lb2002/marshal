// Permission broker: holds a Claude Code PermissionRequest until the paired
// headset answers, then yields the exact hook JSON. Fail-safe: anything other
// than an explicit, correctly-gestured allow/deny yields {} (no decision), so
// Claude Code falls back to its own terminal dialog.
import { randomUUID } from "node:crypto";
import { argvMinLength, isStringArray, MAX_RENDER, renderArgv, renderArgvHead, shownText } from "./argv.js";
import { classifyRisk } from "./risk.js";
const MAX_COMMAND = 300;
const MAX_URL = 300;
const MAX_PATHS = 5;
const MAX_PATH = 300;
const MAX_TOOL = 40;
const MAX_MESSAGE = 500;
// Same fields the classifier inspects for paths.
const PATH_FIELDS = ["file_path", "notebook_path", "path", "destination", "dest", "target", "output", "to", "outputPath"];
function answer(behavior, message) {
    const decision = { behavior };
    if (behavior === "deny") {
        const clean = sanitizeMessage(message);
        if (clean)
            decision.message = clean;
    }
    return { hookSpecificOutput: { hookEventName: "PermissionRequest", decision } };
}
/** Strip control chars (keep newline and tab), trim, cap length; undefined if empty. */
function sanitizeMessage(m) {
    if (typeof m !== "string")
        return undefined;
    const clean = m.replace(/[\u0000-\u0008\u000B-\u001F\u007F-\u009F]/g, "").trim().slice(0, MAX_MESSAGE).trim();
    return clean === "" ? undefined : clean;
}
function str(v) {
    return typeof v === "string" ? v : undefined;
}
/**
 * Display of a `command` field: a string with invisible and control characters as visible escapes (argv.ts
 * visibleText); an argument array rendered so that every element stays one word (never joined by spaces:
 * `["rm","a b"]` deletes one path; its rendering already escapes them). Shown text cut to MAX_COMMAND after
 * escaping, length of the whole escaped text.
 */
function commandDisplay(v) {
    if (typeof v === "string")
        return { ...shownText(v, MAX_COMMAND), argv: false };
    if (!isStringArray(v))
        return undefined;
    const min = argvMinLength(v);
    if (min > MAX_RENDER)
        return { text: renderArgvHead(v, MAX_COMMAND), length: min, argv: true };
    const full = renderArgv(v);
    return { text: full.slice(0, MAX_COMMAND), length: full.length, argv: true };
}
function subscribe(list, cb) {
    list.push(cb);
    return () => {
        const i = list.indexOf(cb);
        if (i >= 0)
            list.splice(i, 1);
    };
}
/**
 * The display fields of a request exactly as the broker builds them (truncation, paths, frozen copies).
 * Shared by the live broker and the offline demo pack; `risk` comes from classifyRisk.
 */
export function buildPermRequest(p) {
    const input = p.input && typeof p.input === "object" ? p.input : {};
    const shown = commandDisplay(input.command);
    // Every display field escaped first, then cut: a field that grows past its cap by escaping counts as cut.
    const rawUrl = str(input.url);
    const url = rawUrl === undefined ? undefined : shownText(rawUrl, MAX_URL);
    const shownPaths = PATH_FIELDS.map((f) => str(input[f])).filter((x) => x !== undefined).slice(0, MAX_PATHS).map((x) => shownText(x, MAX_PATH));
    // A path cut for display hides part of the target, so it needs two-hand like a cut command.
    const pathCut = shownPaths.some((x) => x.length > MAX_PATH);
    const paths = shownPaths.map((x) => x.text);
    const tool = shownText(p.tool, MAX_TOOL);
    const toolCut = tool.length > MAX_TOOL;
    return Object.freeze({
        id: p.id,
        sid: p.sid,
        tool: tool.text,
        command: shown?.text,
        commandLength: shown?.length,
        ...(shown?.argv ? { argv: true } : {}),
        truncated: (shown?.length ?? 0) > MAX_COMMAND || (url?.length ?? 0) > MAX_URL || pathCut || toolCut,
        path: paths[0],
        paths: Object.freeze(paths),
        url: url?.text,
        // Copy + freeze: classifier results are shared module constants.
        risk: Object.freeze({ ...p.risk }),
        createdAt: p.createdAt,
        expiresAt: p.createdAt + p.holdMs,
    });
}
/**
 * Muse kills a hook after a timeout nobody has measured yet (plugin hooks cannot declare one). Until a live probe
 * says more, the bridge holds a Muse request at most 18 s and the Muse shim waits 20 s (hook-cli.ts), so the
 * headset's answer or the bridge's no-decision always arrives before the shim gives up.
 */
export const MUSE_HOLD_MS = 18_000;
export class PermissionBroker {
    holdMs;
    now;
    hasClient;
    entries = new Map();
    openCbs = [];
    closeCbs = [];
    /** Set while halted (halt-all): the deny message every request gets. */
    haltMessage = null;
    constructor(opts) {
        this.holdMs = opts.holdMs;
        this.now = opts.now ?? Date.now;
        this.hasClient = opts.hasClient;
    }
    open(sid, tool, input, cwd, source) {
        const safeInput = input && typeof input === "object" ? input : {};
        const createdAt = this.now();
        const risk = classifyRisk(tool, safeInput, cwd, source);
        const holdMs = source === "muse" ? Math.min(this.holdMs, MUSE_HOLD_MS) : this.holdMs;
        const request = buildPermRequest({ id: randomUUID(), sid, tool, input: safeInput, risk, createdAt, holdMs });
        // Halted: denied at once, never opened, whether or not a headset is connected (a stop stays a stop).
        if (this.haltMessage !== null)
            return { request, answer: Promise.resolve(answer("deny", this.haltMessage)) };
        if (!this.hasClient())
            return { request, answer: Promise.resolve({}) };
        const promise = new Promise((resolve) => {
            const timer = setTimeout(() => this.finish(request.id, {}, "timeout"), holdMs);
            this.entries.set(request.id, { request, riskClass: request.risk.risk, resolve, timer });
        });
        const ctx = Object.freeze({ tool, input: safeInput, cwd, source });
        for (const cb of [...this.openCbs]) {
            try {
                cb(request, ctx);
            }
            catch { /* a listener must not break the broker */ }
        }
        return { request, answer: promise };
    }
    decide(d) {
        // While halted nothing may be allowed, whatever the id (the open requests were already denied).
        if (this.haltMessage !== null && d?.behavior !== "deny")
            return { ok: false, error: "halted" };
        const entry = typeof d?.id === "string" ? this.entries.get(d.id) : undefined;
        if (!entry)
            return { ok: false, error: "unknown" };
        if (d.behavior === "deny") {
            this.finish(d.id, answer("deny", d.message), "deny");
            return { ok: true };
        }
        if (d.behavior !== "allow")
            return { ok: false, error: "unknown" };
        // Allow only with the gesture the risk class demands; request stays pending otherwise.
        // Fail closed: only exactly safe/network on a fully shown request may use pinch-hold.
        const lowRisk = (entry.riskClass === "safe" || entry.riskClass === "network") && !entry.request.truncated;
        if (!lowRisk) {
            if (d.confirm !== "two-hand")
                return { ok: false, error: "needs-two-hand" };
        }
        else if (d.confirm !== "pinch-hold" && d.confirm !== "two-hand") {
            return { ok: false, error: "needs-pinch-hold" };
        }
        this.finish(d.id, answer("allow"), "allow");
        return { ok: true };
    }
    /** Halt-all: every open request is denied with `message` (reason "halt"); new ones get the same deny at once. */
    halt(message) {
        this.haltMessage = message;
        for (const id of [...this.entries.keys()])
            this.finish(id, answer("deny", message), "halt");
    }
    /** Ends a halt: requests open and decide as before. */
    resume() {
        this.haltMessage = null;
    }
    isHalted() {
        return this.haltMessage !== null;
    }
    closeAll(reason) {
        for (const id of [...this.entries.keys()])
            this.finish(id, {}, reason);
    }
    /** Withdraws one request (answer {}); false when it is not pending. */
    cancel(id, reason) {
        if (!this.entries.has(id))
            return false;
        this.finish(id, {}, reason);
        return true;
    }
    pending() {
        return [...this.entries.values()].map((e) => e.request);
    }
    onOpen(cb) {
        return subscribe(this.openCbs, cb);
    }
    onClose(cb) {
        return subscribe(this.closeCbs, cb);
    }
    /** Resolves a request exactly once; later calls find no entry. */
    finish(id, a, reason) {
        const entry = this.entries.get(id);
        if (!entry)
            return;
        this.entries.delete(id);
        clearTimeout(entry.timer);
        entry.resolve(a);
        for (const cb of [...this.closeCbs]) {
            try {
                cb(id, reason);
            }
            catch { /* a listener must not break the broker */ }
        }
    }
}
