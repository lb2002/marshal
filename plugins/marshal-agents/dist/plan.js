// Live plan of an agent (contract `.superpowers/sdd/2026-11-12-contract-agent-plan.md`): its to-do list with
// step statuses, from the agents' own plan tools. Pure: the server feeds tool calls in (from hook payloads, or
// for Codex from its session log, plan-log.ts) and broadcasts what comes out, through PlanGate's rate limit.
//
// - Claude Code `TodoWrite`: input `todos: [{content, status, activeForm}]`, the whole list every time.
// - Claude Code `TaskCreate` / `TaskUpdate` / `TaskList` (incremental; shapes from the published
//   `sdk-tools.d.ts` of @anthropic-ai/claude-code 2.1.191): TaskCreate input `{subject, description}`, result
//   `{task: {id, subject}}`; TaskUpdate input `{taskId, status?: pending|in_progress|completed|deleted,
//   subject?}`, result `{success, taskId, ...}`; TaskList result `{tasks: [{id, subject, status}]}`. They count
//   only after the tool ran (the id comes back in the result). Anything else gives no change.
// - Codex `update_plan`: input `{explanation?, plan: [{step, status}]}`, the whole list.
// - Gemini `write_todos`: input `{todos: [{description, status}]}`, the whole list.
// Step text is untrusted agent text: redacted, absolute paths shortened, one line, at most 80 characters. A
// malformed call never throws and never yields a plan of garbage: what cannot be parsed is skipped.
import { redactSecrets, shortenAbsolutePaths } from "./anonymize.js";
import { cleanText } from "./explain.js";
export const MAX_PLAN_STEPS = 12;
export const MAX_STEP_TEXT = 80;
/** Default rate limit: one plan update per agent in this time. */
export const PLAN_INTERVAL_MS = 500;
/** Raw text is cut to this before redaction, so a huge step costs little. */
const MAX_RAW = 2000;
/**
 * Items read from one list and kept per session. `more` counts the usable entries beyond 12 up to this bound;
 * a longer list is cut here, so `more` is then a lower bound.
 */
const MAX_ITEMS = 200;
const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
/** Plan tools by lower-case name, whatever the source (a Muse tool with one of these names counts too). */
const PLAN_TOOLS = {
    todowrite: "todos",
    write_todos: "todos",
    update_plan: "codex_plan",
    taskcreate: "task_create",
    taskupdate: "task_update",
    tasklist: "task_list",
};
const toolKind = (name) => (Object.hasOwn(PLAN_TOOLS, name.toLowerCase()) ? PLAN_TOOLS[name.toLowerCase()] : undefined);
/** `completed`/`done` → done, `in_progress` → in_progress, anything else → pending. */
export function mapStatus(s) {
    if (s === "completed" || s === "done")
        return "done";
    if (s === "in_progress")
        return "in_progress";
    return "pending";
}
/** Cuts to `max` code points with "…" (no word-boundary search: the text is already clean). */
function clip(s, max) {
    const chars = [...s];
    return chars.length > max ? chars.slice(0, max - 1).join("") + "…" : s;
}
/**
 * Display text of one step: redacted, absolute paths shortened, one line without control or invisible
 * characters, at most 80 characters (cut with "…"), redacted once more after the cut. Null when nothing is left.
 */
export function stepText(raw, homeDir) {
    if (typeof raw !== "string")
        return null;
    // Control and invisible characters go first: one inside a path must not stop the path shortening.
    const visible = cleanText(raw.slice(0, MAX_RAW), MAX_RAW);
    const s = redactSecrets(cleanText(shortenAbsolutePaths(redactSecrets(visible), homeDir), MAX_STEP_TEXT));
    // A redaction marker can be longer than what it replaced.
    const out = clip(s, MAX_STEP_TEXT).trim();
    return out === "" ? null : out;
}
/** The plan-tool call of a pre/post tool hook payload (raw, Claude vocabulary), or null. */
export function planCallFromHook(kind, payload) {
    if (kind !== "tool_start" && kind !== "tool_end")
        return null;
    if (!isObj(payload) || typeof payload.tool_name !== "string" || toolKind(payload.tool_name) === undefined)
        return null;
    return { tool: payload.tool_name, input: payload.tool_input, result: payload.tool_response, phase: kind === "tool_start" ? "pre" : "post" };
}
/** True when `raw` would give a step (stepText not null), without the cost of redaction. */
function usableText(raw) {
    return typeof raw === "string" && cleanText(raw.slice(0, MAX_RAW), MAX_RAW).trim() !== "";
}
/** The subagent id of a hook payload: a non-empty string `agent_id` (Claude Code sets it inside a subagent). */
export function agentIdOf(payload) {
    if (!isObj(payload))
        return undefined;
    const id = payload.agent_id;
    return typeof id === "string" && id !== "" ? id : undefined;
}
/**
 * Decides whether a hook payload comes from a subagent, whose plan-tool calls must not replace the main agent's
 * plan (they share its session id). A payload with `agent_id` is a subagent's. A session that never carried
 * `agent_id` (an agent version without it) falls back to SubagentStart/SubagentStop: while one is open, plan
 * calls are ignored; a turn boundary (prompt, stop, session start or end) closes them all.
 */
export class SubagentGuard {
    sessions = new Map();
    /** Notes one event; true when a plan call in this payload must be ignored. */
    note(sid, kind, payload) {
        const id = agentIdOf(payload);
        let s = this.sessions.get(sid);
        if (!s) {
            if (id === undefined && kind !== "subagent_start")
                return false;
            s = { open: 0, ids: false };
            this.sessions.set(sid, s);
        }
        if (id !== undefined)
            s.ids = true;
        if (kind === "subagent_start")
            s.open = Math.min(s.open + 1, 64);
        else if (kind === "subagent_stop")
            s.open = Math.max(0, s.open - 1);
        else if (kind === "prompt" || kind === "stop" || kind === "session_start" || kind === "session_end")
            s.open = 0;
        return id !== undefined || (!s.ids && s.open > 0);
    }
    forget(sid) {
        this.sessions.delete(sid);
    }
}
/** A string that holds JSON is parsed; anything else is returned as it is. */
function maybeJson(v) {
    if (typeof v !== "string")
        return v;
    try {
        return JSON.parse(v);
    }
    catch {
        return v;
    }
}
function idOf(v) {
    if (typeof v === "string" && v !== "" && v.length <= 100)
        return v;
    if (typeof v === "number" && Number.isSafeInteger(v))
        return String(v);
    return undefined;
}
/** Caps a full item list to the wire shape; undefined for no items. */
function toPlan(items, extra) {
    if (items.length === 0)
        return undefined;
    return { steps: items.slice(0, MAX_PLAN_STEPS).map((i) => ({ text: i.text, status: i.status })), more: Math.max(0, items.length - MAX_PLAN_STEPS) + extra };
}
export class PlanTracker {
    homeDir;
    sessions = new Map();
    constructor(homeDir) {
        this.homeDir = homeDir;
    }
    /** Applies one tool call; the new plan when it changed, else null. Never throws. */
    observe(sid, call) {
        try {
            return this.apply(sid, call);
        }
        catch {
            return null;
        }
    }
    /** Drops a session's plan (session end). */
    forget(sid) {
        this.sessions.delete(sid);
    }
    session(sid) {
        let s = this.sessions.get(sid);
        if (!s) {
            s = { mode: "list", items: [], extra: 0, last: "null" };
            this.sessions.set(sid, s);
        }
        return s;
    }
    apply(sid, call) {
        const kind = typeof call.tool === "string" ? toolKind(call.tool) : undefined;
        if (kind === undefined)
            return null;
        const input = maybeJson(call.input);
        if (kind === "todos" || kind === "codex_plan") {
            const got = kind === "todos" ? this.list(input, "todos", ["content", "description"], "status") : this.list(input, "plan", ["step"], "status");
            if (got === null)
                return null;
            const s = this.session(sid);
            s.mode = "list";
            s.items = got.items;
            s.extra = got.extra;
            return this.commit(s);
        }
        // The task tools count once they ran: the id is in the result.
        if (call.phase !== "post")
            return null;
        const result = maybeJson(call.result);
        if (kind === "task_list") {
            const got = isObj(result) ? this.list(result, "tasks", ["subject"], "status", true) : null;
            if (got === null)
                return null;
            const s = this.session(sid);
            s.mode = "tasks";
            s.items = got.items;
            s.extra = 0;
            return this.commit(s);
        }
        if (!isObj(input))
            return null;
        if (kind === "task_create") {
            const text = stepText(input.subject, this.homeDir);
            if (text === null)
                return null;
            const id = isObj(result) && isObj(result.task) ? idOf(result.task.id) : typeof result === "string" ? /#(\d{1,12})\b/.exec(result)?.[1] : undefined;
            const s = this.session(sid);
            if (s.mode !== "tasks")
                s.items = [];
            s.mode = "tasks";
            s.extra = 0;
            const at = id === undefined ? -1 : s.items.findIndex((i) => i.id === id);
            const item = { ...(id === undefined ? {} : { id }), text, status: "pending" };
            if (at >= 0)
                s.items[at] = item;
            else if (s.items.length < MAX_ITEMS)
                s.items.push(item);
            return this.commit(s);
        }
        // task_update
        if (isObj(result) && (result.success === false || (typeof result.error === "string" && result.error !== "" && result.success !== true)))
            return null;
        const id = idOf(input.taskId);
        const s = this.sessions.get(sid);
        if (id === undefined || !s || s.mode !== "tasks")
            return null;
        const at = s.items.findIndex((i) => i.id === id);
        if (at < 0)
            return null;
        if (input.status === "deleted") {
            s.items.splice(at, 1);
            return this.commit(s);
        }
        const next = { ...s.items[at] };
        if (typeof input.status === "string")
            next.status = mapStatus(input.status);
        const text = stepText(input.subject, this.homeDir);
        if (text !== null)
            next.text = text;
        s.items[at] = next;
        return this.commit(s);
    }
    /**
     * Items of `holder[key]`: null when it is not a list, or when it has entries and none is usable (no change);
     * no items for an empty list (the plan is cleared). Without ids (a whole list) only the first 12 usable entries
     * are cleaned; the others up to MAX_ITEMS are only counted (`extra`).
     */
    list(holder, key, textKeys, statusKey, withId = false) {
        if (!isObj(holder) || !Array.isArray(holder[key]))
            return null;
        const raw = holder[key];
        const items = [];
        let extra = 0;
        for (const entry of raw.slice(0, MAX_ITEMS)) {
            if (!isObj(entry))
                continue;
            const rawText = textKeys.map((k) => entry[k]).find((v) => typeof v === "string");
            if (!withId && items.length >= MAX_PLAN_STEPS) {
                if (usableText(rawText))
                    extra++;
                continue;
            }
            const text = stepText(rawText, this.homeDir);
            if (text === null)
                continue;
            const id = withId ? idOf(entry.id) : undefined;
            items.push({ ...(id === undefined ? {} : { id }), text, status: mapStatus(entry[statusKey]) });
        }
        return raw.length > 0 && items.length === 0 ? null : { items, extra };
    }
    commit(s) {
        const plan = toPlan(s.items, s.extra);
        const key = JSON.stringify(plan ?? null);
        if (key === s.last)
            return null;
        s.last = key;
        return { plan };
    }
}
/**
 * Re-applies the wire caps to a plan from outside (a recording): steps with non-string or empty text are
 * dropped, statuses mapped, text cut to 80, at most 12 steps (the rest added to `more`). Undefined for no steps.
 */
export function capPlan(raw) {
    if (!isObj(raw) || !Array.isArray(raw.steps))
        return undefined;
    const steps = [];
    for (const s of raw.steps) {
        if (!isObj(s) || typeof s.text !== "string")
            continue;
        const text = clip(cleanText(s.text, MAX_STEP_TEXT), MAX_STEP_TEXT);
        if (text !== "")
            steps.push({ text, status: mapStatus(s.status) });
    }
    if (steps.length === 0)
        return undefined;
    const recorded = typeof raw.more === "number" && Number.isSafeInteger(raw.more) && raw.more > 0 ? raw.more : 0;
    return { steps: steps.slice(0, MAX_PLAN_STEPS), more: recorded + Math.max(0, steps.length - MAX_PLAN_STEPS) };
}
/**
 * The `update_plan` calls in Codex rollout JSONL text (`{type: "response_item", payload: {type:
 * "function_call", name: "update_plan", arguments: "<json>"}}`), oldest first. Broken lines are skipped.
 */
export function codexPlanCalls(text) {
    const out = [];
    for (const line of text.split("\n")) {
        if (!line.includes("update_plan"))
            continue;
        let v;
        try {
            v = JSON.parse(line);
        }
        catch {
            continue;
        }
        if (!isObj(v) || v.type !== "response_item" || !isObj(v.payload))
            continue;
        const p = v.payload;
        if (p.type !== "function_call" || p.name !== "update_plan")
            continue;
        if (typeof p.arguments !== "string" && !isObj(p.arguments))
            continue;
        out.push({ tool: "update_plan", input: p.arguments, phase: "post" });
    }
    return out;
}
/**
 * Rate limit for plan updates: at most one per agent per interval (default 500 ms). A change inside the
 * interval waits; only the latest waiting state goes out, at the end of the interval, so the last state
 * always reaches the headset.
 */
export class PlanGate {
    deps;
    states = new Map();
    interval;
    constructor(deps) {
        this.deps = deps;
        this.interval = deps.intervalMs ?? PLAN_INTERVAL_MS;
    }
    offer(sid, plan) {
        const now = this.deps.now();
        const s = this.states.get(sid);
        if (!s || (s.timer === undefined && now - s.last >= this.interval)) {
            this.states.set(sid, { last: now });
            this.deps.emit(sid, plan);
            return;
        }
        s.pending = { plan };
        if (s.timer !== undefined)
            return;
        s.timer = this.deps.setTimer(() => {
            s.timer = undefined;
            const p = s.pending;
            s.pending = undefined;
            if (!p || this.states.get(sid) !== s)
                return;
            s.last = this.deps.now();
            this.deps.emit(sid, p.plan);
        }, Math.max(0, s.last + this.interval - now));
    }
    /** Forgets an agent: a waiting update is dropped (session end). */
    drop(sid) {
        const s = this.states.get(sid);
        if (s?.timer !== undefined)
            this.deps.clearTimer(s.timer);
        this.states.delete(sid);
    }
    close() {
        for (const sid of [...this.states.keys()])
            this.drop(sid);
    }
}
