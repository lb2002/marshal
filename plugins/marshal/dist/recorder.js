import { createWriteStream, mkdirSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { dirname } from "node:path";
import { anonymizeEvent } from "./anonymize.js";
import { AGENT_EVENT_KINDS, AGENT_SOURCES } from "./events.js";
import { relInside } from "./risk/paths.js";
/**
 * Appends anonymised events as NDJSON, with `t` rewritten to ms since the first recorded event.
 * Each session is anonymised against its own root: the first `cwd` seen for its `sid`. Until a session
 * has sent a cwd, and when that cwd is home or above it, `opts.repoRoot` (the CLI's working directory) stands in.
 */
export class Recorder {
    opts;
    stream;
    done;
    roots = new Map();
    base = null;
    closed = false;
    constructor(filePath, opts) {
        this.opts = opts;
        mkdirSync(dirname(filePath), { recursive: true });
        this.stream = createWriteStream(filePath, { encoding: "utf8" });
        this.done = new Promise((resolve, reject) => {
            this.stream.once("finish", resolve);
            this.stream.once("error", reject);
        });
        this.done.catch(() => { }); // surfaced by close()
    }
    write(e) {
        if (this.closed)
            throw new Error("Recorder is closed");
        this.base ??= e.t;
        if (e.cwd && !this.roots.has(e.sid)) {
            // home itself or a directory above it is no project root: home paths must stay `~/...`
            this.roots.set(e.sid, relInside(this.opts.homeDir, e.cwd) === null ? e.cwd : this.opts.repoRoot);
        }
        const rec = anonymizeEvent(e, { ...this.opts, repoRoot: this.roots.get(e.sid) ?? this.opts.repoRoot });
        rec.t = e.t - this.base;
        this.stream.write(JSON.stringify(rec) + "\n");
    }
    close() {
        if (!this.closed) {
            this.closed = true;
            this.stream.end();
        }
        return this.done;
    }
}
const STRING_FIELDS = ["tool", "path", "command", "notifyType", "summary", "cwd", "title"];
const NUMBER_FIELDS = ["ctxPct", "costUsd"];
const PLAN_STATUSES = new Set(["pending", "in_progress", "done"]);
/** Why a recorded `plan` is malformed, or null. */
function planProblem(p) {
    if (typeof p !== "object" || p === null || Array.isArray(p))
        return "plan must be an object";
    const o = p;
    if (!Array.isArray(o.steps))
        return "plan.steps must be an array";
    for (const s of o.steps) {
        const step = s;
        if (typeof step !== "object" || step === null || typeof step.text !== "string" || typeof step.status !== "string" || !PLAN_STATUSES.has(step.status)) {
            return "plan.steps must be {text, status: pending|in_progress|done}";
        }
    }
    if (!(typeof o.more === "number" && Number.isSafeInteger(o.more) && o.more >= 0))
        return "plan.more must be a whole number >= 0";
    return null;
}
/** Why `o` is not an AgentEvent, or null when it is one. */
function eventProblem(o) {
    if (typeof o.sid !== "string" || typeof o.t !== "number" || typeof o.kind !== "string")
        return "not an agent event (needs sid, t, kind)";
    for (const [k, v] of Object.entries(o))
        if (typeof v === "number" && !Number.isFinite(v))
            return `${k} must be a finite number`;
    if (!AGENT_EVENT_KINDS.has(o.kind))
        return `kind ${JSON.stringify(o.kind).slice(0, 40)} is unknown`;
    for (const k of STRING_FIELDS)
        if (o[k] !== undefined && typeof o[k] !== "string")
            return `${k} must be a string`;
    if (o.source !== undefined && !(typeof o.source === "string" && AGENT_SOURCES.has(o.source)))
        return "source must be claude, muse, codex or gemini";
    for (const k of NUMBER_FIELDS) {
        if (o[k] !== undefined && !(typeof o[k] === "number" && Number.isFinite(o[k])))
            return `${k} must be a number`;
    }
    if (o.plan !== undefined)
        return planProblem(o.plan);
    return null;
}
/** Reads an NDJSON recording; rejects the first line that is not a valid AgentEvent, with its 1-based number. */
export async function readRecording(filePath) {
    const lines = (await readFile(filePath, "utf8")).split("\n");
    const events = [];
    lines.forEach((line, i) => {
        if (line.trim() === "")
            return;
        let v;
        try {
            v = JSON.parse(line);
        }
        catch (err) {
            throw new Error(`${filePath}: line ${i + 1}: invalid JSON (${err.message})`);
        }
        const o = v;
        const problem = typeof o !== "object" || o === null || Array.isArray(o) ? "not an agent event (needs sid, t, kind)" : eventProblem(o);
        if (problem)
            throw new Error(`${filePath}: line ${i + 1}: ${problem}`);
        events.push(o);
    });
    return events;
}
