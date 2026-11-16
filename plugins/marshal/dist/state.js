import { capPlan } from "./plan.js";
import { normalizePath } from "./risk/paths.js";
import { projectName } from "./title.js";
import { stripInvisible } from "./text.js";
const MAX_FILES = 20;
const MAX_ACTION = 80;
const READ_TOOLS = new Set(["read", "glob", "grep"]);
const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "notebookedit"]);
const TEST_RE = /\b(?:npm (?:run )?test|pnpm (?:run )?test|yarn (?:run )?test|pytest|vitest|jest|go test|cargo test)\b/;
const WAIT_NOTIFY = new Set(["idle_prompt", "agent_needs_input"]);
const SHELL_TOOLS = new Set(["bash", "powershell"]);
/** Display name prefix per source; Claude keeps "Agent N". */
const NAME_PREFIX = { claude: "Agent", muse: "Muse", codex: "Codex", gemini: "Gemini" };
// Tool names, paths and summaries come from the agent: invisible and control characters are dropped first.
const clip = (raw) => {
    const s = stripInvisible(raw);
    return s.length > MAX_ACTION ? s.slice(0, MAX_ACTION - 1) + "…" : s;
};
/** Known limitation: relative paths are not resolved against cwd (see normalizePath). */
/** Last two path segments, `/`-separated. */
export function shortPath(p) {
    return p.split(/[\\/]+/).filter(Boolean).slice(-2).join("/");
}
/** Only the program name: arguments may carry tokens, URLs or home paths. */
function shellLabel(shell, command) {
    const first = command?.trim().split(/\s+/)[0] ?? "";
    const name = first.split(/[\\/]/).pop() ?? "";
    return /^[\w.+-]{1,40}$/.test(name) ? `${shell}: ${name}` : "Runs a command";
}
function nextFor(e, prev) {
    switch (e.kind) {
        case "session_start":
            return { status: "working", lastAction: "Session started" };
        case "prompt":
            return { status: "working", lastAction: "Working" };
        case "tool_start": {
            const tool = e.tool ?? "";
            const lower = tool.toLowerCase();
            const target = e.path ? shortPath(e.path) : "";
            if (READ_TOOLS.has(lower))
                return { status: "reading", lastAction: clip(target ? `Reads ${target}` : `Reads (${tool})`) };
            if (EDIT_TOOLS.has(lower))
                return { status: "editing", lastAction: clip(target ? `Edits ${target}` : `Edits (${tool})`) };
            if (SHELL_TOOLS.has(lower) && e.command && TEST_RE.test(e.command))
                return { status: "testing", lastAction: "Runs tests" };
            if (SHELL_TOOLS.has(lower))
                return { status: "working", lastAction: clip(shellLabel(tool, e.command)) };
            return { status: "working", lastAction: clip(tool ? `Uses ${tool}` : "Working") };
        }
        case "permission":
            return { status: "blocked", lastAction: clip(`Waiting for approval: ${e.tool ?? "tool"}`) };
        case "notify":
            return e.notifyType !== undefined && WAIT_NOTIFY.has(e.notifyType)
                ? { status: "waiting", lastAction: "Waiting for input" }
                : null;
        case "stop":
            return { status: "done", lastAction: clip(e.summary || "Done") };
        case "stop_fail":
            return { status: "failed", lastAction: "Failed" };
        case "reply":
            return { status: "working", lastAction: "Reply sent" };
        case "session_end":
            return prev.status === "failed" ? null : { status: "done", lastAction: prev.status === "done" ? prev.lastAction : "Session ended" };
        default:
            return null; // tool_end, tool_fail, subagent_*, compact, status keep the state
    }
}
export class SessionStore {
    agents = new Map();
    fileKeys = new Map(); // sid -> normalised edited paths
    /**
     * Applies one event. `status` events only refresh ctxPct/costUsd (the agent has
     * not left its state). Any other event that moves a blocked/waiting agent out of
     * that state adds the elapsed time to waitingMsTotal; events that carry no state
     * of their own (tool_end, subagent_*, ...) release a blocked/waiting agent to working.
     */
    apply(e) {
        let a = this.agents.get(e.sid);
        if (e.kind === "plan") {
            // A plan alone never creates an agent and never changes its status.
            if (!a)
                return null;
            const plan = capPlan(e.plan);
            if (plan)
                a.plan = { ...plan, updatedAt: e.t };
            else
                delete a.plan;
            return a;
        }
        if (!a) {
            const source = e.source ?? "claude";
            const n = this.all().filter((x) => x.source === source).length + 1;
            a = {
                sid: e.sid,
                name: `${NAME_PREFIX[source]} ${n}`,
                source,
                status: "working",
                lastAction: "Session started",
                since: e.t,
                files: [],
                waitingMsTotal: 0,
            };
            this.agents.set(e.sid, a);
        }
        if (a.project === undefined && e.cwd) {
            const p = projectName(e.cwd);
            if (p !== undefined)
                a.project = p;
        }
        if (e.kind === "prompt" && e.title && a.title === undefined)
            a.title = e.title;
        // The plan is per-session data: it ends with the session.
        if (e.kind === "session_end")
            delete a.plan;
        if (e.kind === "status") {
            if (e.ctxPct !== undefined)
                a.ctxPct = e.ctxPct;
            if (e.costUsd !== undefined)
                a.costUsd = e.costUsd;
            return a;
        }
        let next = nextFor(e, a);
        const waiting = a.status === "blocked" || a.status === "waiting";
        if (!next && waiting && e.kind !== "notify")
            next = { status: "working", lastAction: "Working" };
        if (!next)
            return a;
        if (waiting && next.status !== a.status)
            a.waitingMsTotal += Math.max(0, e.t - a.since);
        if (next.status !== a.status)
            a.since = e.t;
        a.status = next.status;
        a.lastAction = next.lastAction;
        if (next.status === "editing" && e.path && a.files.length < MAX_FILES) {
            const keys = this.fileKeys.get(a.sid) ?? new Set();
            const key = normalizePath(e.path);
            if (!keys.has(key)) {
                keys.add(key);
                a.files.push(shortPath(e.path));
            }
            this.fileKeys.set(a.sid, keys);
        }
        return a;
    }
    /** Replaces the title (the AI's); the agent, or undefined for an unknown session. */
    setTitle(sid, title) {
        const a = this.agents.get(sid);
        if (a)
            a.title = title;
        return a;
    }
    get(sid) {
        return this.agents.get(sid);
    }
    all() {
        return [...this.agents.values()];
    }
}
