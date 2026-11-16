// `agent_detail_request` → `agent_detail`: what one agent is doing, for the headset's detail view.
//
// - recent: the session's last tool starts as rule labels ("Reads x", "Edits y", "Runs tests",
//   "Runs: <command, 40 characters>"), newest first, at most 5 of at most 80 characters. Only these
//   labels are kept, never the raw tool input.
// - waitingFor: the explanation of the agent's pending request, else a notification or a terminal prompt.
// - summary: by rules from the state and `recent`; with the explainer on, haiku summarises the last ~30
//   lines of the session's local transcript (redacted, fenced) and a second `agent_detail` with source "ai"
//   follows. An AI summary is cached per session for 30 s, in memory only, and never recorded or logged.
// - At most one request per session every 3 s. Everything here is display text: it never touches risk,
//   gestures or decisions.
import { redactSecrets, shortenAbsolutePaths } from "./anonymize.js";
import { sanitizeSummary, summaryPrompt } from "./agent-ai.js";
import { cleanText } from "./explain.js";
import { shortPath } from "./state.js";
import { readTail, resolveTranscript, transcriptLines } from "./transcript.js";
const MAX_RECENT = 5;
const MAX_LABEL = 80;
const MAX_COMMAND = 40;
const MAX_SUMMARY = 280;
const MAX_WAITING = 140;
const RATE_MS = 3000;
const CACHE_MS = 30_000;
/** A session silent for longer than this is forgotten. */
const IDLE_MS = 2 * 3600_000;
/** At most this many sessions are remembered (the least recently seen goes first). */
const MAX_SESSIONS = 200;
const READ_TOOLS = new Set(["read", "notebookread"]);
const SEARCH_TOOLS = new Set(["glob", "grep", "ls"]);
const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "notebookedit"]);
const SHELL_TOOLS = new Set(["bash", "powershell"]);
const TEST_RE = /\b(?:npm (?:run )?test|pnpm (?:run )?test|yarn (?:run )?test|pytest|vitest|jest|go test|cargo test)\b/;
const SAFE_TOOL = /^[A-Za-z0-9_.:-]{1,40}$/;
const NOTIFY_TEXT = {
    idle_prompt: "Waiting for your next instruction.",
    agent_needs_input: "Needs an answer from you.",
    elicitation_dialog: "Needs an answer from you.",
    permission_prompt: "Waiting for approval in the terminal.",
};
const one = (s, max) => cleanText(s, max);
/** The rule label of one tool start, or null for any other event. */
export function recentLabel(e, homeDir) {
    if (e.kind !== "tool_start" || !e.tool)
        return null;
    const tool = e.tool.toLowerCase();
    const target = e.path ? shortPath(redactSecrets(e.path)) : "";
    let label;
    if (READ_TOOLS.has(tool))
        label = target ? `Reads ${target}` : "Reads a file";
    else if (SEARCH_TOOLS.has(tool))
        label = target ? `Searches ${target}` : "Searches the project";
    else if (EDIT_TOOLS.has(tool))
        label = target ? `Edits ${target}` : "Edits a file";
    else if (SHELL_TOOLS.has(tool) && e.command && TEST_RE.test(e.command))
        label = "Runs tests";
    else if (SHELL_TOOLS.has(tool) && e.command) {
        // Redact and shorten the whole command first, then cut, then redact the cut once more.
        const cmd = redactSecrets(one(shortenAbsolutePaths(redactSecrets(e.command), homeDir), MAX_COMMAND));
        label = `Runs: ${cmd}`;
    }
    else
        label = SAFE_TOOL.test(e.tool) ? `Uses ${e.tool}` : "Uses a tool";
    return one(label, MAX_LABEL);
}
/** A few plain sentences from the state and the recent labels, at most 280 characters. */
function rulesSummary(a, recent) {
    const parts = [`${a.lastAction}.`];
    const before = recent.filter((r) => r !== a.lastAction).slice(0, 3);
    if (before.length > 0)
        parts.push(`Before that: ${before.join(", ")}.`);
    if (a.files.length > 0)
        parts.push(`Changed files: ${a.files.slice(-3).join(", ")}.`);
    return one(parts.join(" "), MAX_SUMMARY);
}
export class AgentDetails {
    deps;
    recent = new Map();
    notify = new Map();
    /** The `transcript_path` a session's hooks reported (checked only when it is read). */
    transcripts = new Map();
    lastAsk = new Map();
    /** AI summary per session, for 30 s; null remembers a failure for as long. */
    cache = new Map();
    inflight = new Map();
    /** Sessions whose first prompt was seen (only that one is given to the AI for a title). */
    titled = new Set();
    /** Last time each session was seen, least recent first; the bound for every per-session map above. */
    seen = new Map();
    constructor(deps) {
        this.deps = deps;
    }
    /** Number of sessions with anything remembered (tests). */
    trackedSessions() {
        return this.seen.size;
    }
    /** True exactly once per session: on its first prompt. */
    firstPrompt(sid) {
        this.touch(sid);
        if (this.titled.has(sid))
            return false;
        this.titled.add(sid);
        return true;
    }
    /** Marks a session as seen now, and forgets idle sessions and the oldest beyond the cap. */
    touch(sid) {
        const now = this.deps.now();
        this.seen.delete(sid);
        this.seen.set(sid, now);
        for (const [s, at] of this.seen) {
            if (this.seen.size <= MAX_SESSIONS && now - at <= IDLE_MS)
                break; // the rest is more recent
            this.forget(s);
        }
    }
    forget(sid) {
        for (const m of [this.recent, this.notify, this.transcripts, this.lastAsk, this.cache, this.seen])
            m.delete(sid);
        this.titled.delete(sid);
    }
    /** Every ingested event: keeps the tool labels and the last notification type; session_end forgets. */
    observe(e) {
        if (e.kind === "session_end") {
            this.forget(e.sid);
            return;
        }
        this.touch(e.sid);
        if (e.kind === "notify" && e.notifyType)
            this.notify.set(e.sid, e.notifyType);
        const label = recentLabel(e, this.deps.homeDir);
        if (label === null)
            return;
        const list = this.recent.get(e.sid) ?? [];
        if (list[0] !== label)
            list.unshift(label);
        this.recent.set(e.sid, list.slice(0, MAX_RECENT));
    }
    /** Remembers the transcript path from a hook payload (Claude, Codex, Gemini send one). */
    notePayload(sid, payload) {
        const p = typeof payload === "object" && payload !== null ? payload.transcript_path : undefined;
        if (typeof p !== "string" || p === "" || p.length >= 4096)
            return;
        this.touch(sid);
        this.transcripts.set(sid, p);
    }
    /** The `transcript_path` this session's hooks reported (unchecked; resolveTranscript checks it). */
    transcriptPath(sid) {
        return this.transcripts.get(sid);
    }
    request(sid) {
        const a = this.deps.agent(sid);
        if (!a)
            return { error: "unknown-agent" };
        const t = this.deps.now();
        const last = this.lastAsk.get(sid);
        if (last !== undefined && t - last < RATE_MS)
            return { error: "rate-limited" };
        this.lastAsk.set(sid, t);
        const rules = this.rulesDetail(a);
        if (!this.deps.explainer)
            return { now: rules };
        const cached = this.cache.get(sid);
        if (cached && t - cached.at < CACHE_MS) {
            return cached.summary === null ? { now: rules } : { now: { ...rules, summary: cached.summary, source: "ai" } };
        }
        this.cache.delete(sid);
        // Only a transcript inside the tool's own folder is read; without one there is no AI version.
        const file = resolveTranscript({ sid, source: a.source, path: this.transcripts.get(sid), homeDir: this.deps.homeDir, env: this.deps.env });
        if (file === null)
            return { now: rules };
        const later = this.aiSummary(sid, file, this.deps.explainer).then((summary) => {
            const now = this.deps.agent(sid);
            return summary && now ? { ...this.rulesDetail(now), summary, source: "ai" } : null;
        });
        return { now: rules, later };
    }
    rulesDetail(a) {
        const recent = [...(this.recent.get(a.sid) ?? [])];
        return {
            type: "agent_detail",
            sid: a.sid,
            title: a.title ?? "",
            summary: rulesSummary(a, recent),
            recent,
            waitingFor: this.waitingFor(a),
            source: "rules",
        };
    }
    waitingFor(a) {
        const pending = this.deps.pendingExplain(a.sid);
        if (pending)
            return one(pending, MAX_WAITING);
        if (a.status === "waiting")
            return NOTIFY_TEXT[this.notify.get(a.sid) ?? ""] ?? "Needs you.";
        if (a.status === "blocked") {
            const tool = /^Waiting for approval: (.+)$/.exec(a.lastAction)?.[1];
            return one(tool ? `Waiting for approval in the terminal: ${tool}` : "Waiting for approval in the terminal.", MAX_WAITING);
        }
        return null;
    }
    /** The AI summary of the transcript tail (one run per session at a time), or null. Never throws. */
    aiSummary(sid, file, explainer) {
        const running = this.inflight.get(sid);
        if (running)
            return running;
        const run = (async () => {
            try {
                const lines = transcriptLines(await readTail(file));
                const summary = lines.length === 0 ? null : sanitizeSummary(await explainer.ask(summaryPrompt(lines, this.deps.homeDir)));
                // A failure is remembered too, so a broken AI is not asked again for 30 s.
                if (this.seen.has(sid))
                    this.cache.set(sid, { summary, at: this.deps.now() });
                return summary;
            }
            catch {
                return null;
            }
        })().finally(() => this.inflight.delete(sid));
        this.inflight.set(sid, run);
        return run;
    }
}
