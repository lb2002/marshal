// Live feed parsers: lines of an agent's local session log become `FeedEntry`s for the headset (contract
// `.superpowers/sdd/2026-11-12-contract-live-feed.md`). Everything is untrusted display text: it never touches
// risk, gestures or decisions, and it is never recorded or logged.
//
// - Claude Code: the session transcript (`transcript_path`, JSONL with user/assistant/system records). The
//   official Agent SDK's session readers were not used: they read whole sessions, while the feed must read only
//   the appended bytes (feed-hub.ts).
// - Codex: rollout JSONL `{timestamp, type, payload}` (response items only; event messages repeat them).
// - Gemini: chat JSONL with `$set.messages` records or single message lines (deduplicated by message id).
// - Muse: event records `{id, recorded_at (µs), payload}`, best effort (model messages and turns).
// Unknown records give nothing, a broken line is skipped, and nothing here ever throws.
import { createHash } from "node:crypto";
import { redactSecrets, shortenAbsolutePaths } from "./anonymize.js";
import { cleanText } from "./explain.js";
import { renderArgvHead } from "./argv.js";
export const MAX_FEED_LABEL = 40;
export const MAX_FEED_TEXT = 400;
/** Raw text is cut to this before redaction, so a huge record costs little. */
const MAX_RAW = 4000;
const MAX_DEPTH = 6;
const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
const str = (v) => (typeof v === "string" ? v : "");
const LABEL = {
    prompt: "Your task",
    thinking: "thinks",
    text: "says",
    result: "result",
    error: "error",
    system: "System",
};
const READ_TOOLS = new Set(["read", "notebookread", "read_file", "read_many_files", "view"]);
const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "notebookedit", "apply_patch", "replace", "write_file", "edit_file"]);
const SEARCH_TOOLS = new Set(["glob", "grep", "ls", "search_file_content", "list_directory", "glob_tool", "search"]);
const SHELL_TOOLS = new Set(["bash", "powershell", "shell", "exec_command", "run_shell_command", "local_shell", "container.exec"]);
const TEST_RE = /\b(?:npm (?:run )?test|pnpm (?:run )?test|yarn (?:run )?test|pytest|vitest|jest|go test|cargo test)\b/;
const SAFE_TOOL = /^[A-Za-z0-9_.:-]{1,40}$/;
/** Every string below `v`, joined, at most MAX_RAW characters. */
function textOf(v, depth = 0) {
    if (depth > MAX_DEPTH)
        return "";
    if (typeof v === "string")
        return v.slice(0, MAX_RAW);
    if (Array.isArray(v))
        return v.map((x) => textOf(x, depth + 1)).filter((s) => s !== "").join(" ").slice(0, MAX_RAW);
    if (isObj(v)) {
        for (const k of ["text", "content", "output", "message", "result", "resultDisplay"]) {
            const s = textOf(v[k], depth + 1);
            if (s !== "")
                return s;
        }
    }
    return "";
}
/** The label and shown text of a tool call: what it reads, edits, searches or runs. */
function toolRaw(name, input) {
    const i = isObj(input) ? input : {};
    const lower = name.toLowerCase();
    const path = str(i.file_path) || str(i.path) || str(i.absolute_path) || str(i.notebook_path);
    const rawCmd = i.command ?? i.cmd;
    // An argument array is shown with each element as one word (argv.ts), never joined by spaces.
    const command = Array.isArray(rawCmd) ? renderArgvHead(rawCmd.filter((x) => typeof x === "string"), MAX_RAW) : str(rawCmd);
    let label;
    let text;
    if (READ_TOOLS.has(lower))
        [label, text] = ["reads", path];
    else if (EDIT_TOOLS.has(lower))
        [label, text] = ["edits", path || command];
    else if (SEARCH_TOOLS.has(lower))
        [label, text] = ["searches", str(i.pattern) || str(i.query) || path];
    else if (SHELL_TOOLS.has(lower) || (command !== "" && !path))
        [label, text] = [TEST_RE.test(command) ? "tests" : "command", command];
    else
        [label, text] = [SAFE_TOOL.test(name) ? `uses ${name}` : "uses a tool", path || compactJson(input)];
    return { kind: "tool", label, text, ...(SAFE_TOOL.test(name) ? { tool: name } : {}) };
}
function compactJson(v) {
    try {
        return v === undefined ? "" : JSON.stringify(v).slice(0, MAX_RAW);
    }
    catch {
        return "";
    }
}
const time = (v) => {
    const t = typeof v === "string" ? Date.parse(v) : NaN;
    return Number.isFinite(t) ? t : undefined;
};
// ---- Claude Code ----
function claudeRecord(o) {
    const t = time(o.timestamp);
    const key = str(o.uuid) || undefined;
    const msg = isObj(o.message) ? o.message : undefined;
    const out = [];
    const push = (r) => out.push({ ...r, t });
    if (o.type === "system") {
        const text = textOf(o.content);
        if (text)
            push({ kind: "system", label: LABEL.system, text });
    }
    else if (o.type === "user" && msg) {
        if (typeof msg.content === "string") {
            const system = o.isMeta === true || /^\s*<(?:command|local-command|system)/.test(msg.content);
            push(system ? { kind: "system", label: LABEL.system, text: msg.content } : { kind: "prompt", label: LABEL.prompt, text: msg.content });
        }
        else if (Array.isArray(msg.content)) {
            for (const b of msg.content.filter(isObj)) {
                if (b.type === "text")
                    push({ kind: "prompt", label: LABEL.prompt, text: str(b.text) });
                else if (b.type === "tool_result")
                    push(b.is_error === true ? { kind: "error", label: LABEL.error, text: textOf(b.content) } : { kind: "result", label: LABEL.result, text: textOf(b.content) });
            }
        }
    }
    else if (o.type === "assistant" && msg && Array.isArray(msg.content)) {
        for (const b of msg.content.filter(isObj)) {
            if (b.type === "thinking")
                push({ kind: "thinking", label: LABEL.thinking, text: str(b.thinking) });
            else if (b.type === "text")
                push({ kind: "text", label: LABEL.text, text: str(b.text) });
            else if (b.type === "tool_use")
                push(toolRaw(str(b.name), b.input));
        }
    }
    return out.map((r, i) => (key ? { ...r, key: `${key}:${i}` } : r));
}
// ---- Codex ----
function parseMaybeJson(s) {
    try {
        return JSON.parse(s);
    }
    catch {
        return s;
    }
}
function codexRecord(o) {
    if (o.type !== "response_item" || !isObj(o.payload))
        return [];
    const p = o.payload;
    const t = time(o.timestamp);
    const one = (r) => [{ ...r, t }];
    switch (p.type) {
        case "message": {
            const text = textOf(p.content);
            if (!text)
                return [];
            if (p.role === "assistant")
                return one({ kind: "text", label: LABEL.text, text });
            if (p.role === "user" && !/^\s*</.test(text))
                return one({ kind: "prompt", label: LABEL.prompt, text });
            return one({ kind: "system", label: LABEL.system, text });
        }
        case "reasoning": {
            const text = textOf(p.summary);
            return text ? one({ kind: "thinking", label: LABEL.thinking, text }) : [];
        }
        case "function_call":
        case "custom_tool_call":
            return one(toolRaw(str(p.name), typeof p.arguments === "string" ? parseMaybeJson(p.arguments) : (p.arguments ?? p.input)));
        case "local_shell_call":
            return one(toolRaw("local_shell", isObj(p.action) ? p.action : {}));
        case "function_call_output":
        case "custom_tool_call_output": {
            const out = typeof p.output === "string" ? parseMaybeJson(p.output) : p.output;
            const meta = isObj(out) && isObj(out.metadata) ? out.metadata : undefined;
            const failed = typeof meta?.exit_code === "number" && meta.exit_code !== 0;
            const text = textOf(out);
            return text ? one(failed ? { kind: "error", label: LABEL.error, text } : { kind: "result", label: LABEL.result, text }) : [];
        }
        default:
            return [];
    }
}
// ---- Gemini ----
const GEMINI_TYPES = new Set(["user", "gemini", "info", "error", "warning"]);
function geminiMessage(m) {
    const t = time(m.timestamp);
    const out = [];
    const content = textOf(m.content);
    switch (m.type) {
        case "user":
            if (content)
                out.push({ kind: "prompt", label: LABEL.prompt, text: content });
            break;
        case "gemini":
            for (const th of Array.isArray(m.thoughts) ? m.thoughts.filter(isObj) : []) {
                const text = [str(th.subject), str(th.description)].filter((s) => s !== "").join(": ");
                if (text)
                    out.push({ kind: "thinking", label: LABEL.thinking, text });
            }
            if (content)
                out.push({ kind: "text", label: LABEL.text, text: content });
            for (const c of Array.isArray(m.toolCalls) ? m.toolCalls.filter(isObj) : []) {
                out.push(toolRaw(str(c.name), c.args));
                const result = textOf(c.resultDisplay) || textOf(c.result);
                if (result)
                    out.push(c.status === "error" ? { kind: "error", label: LABEL.error, text: result } : { kind: "result", label: LABEL.result, text: result });
            }
            break;
        case "error":
            if (content)
                out.push({ kind: "error", label: LABEL.error, text: content });
            break;
        default:
            if (content)
                out.push({ kind: "system", label: LABEL.system, text: content });
    }
    // Keyed by part type and its index within that type: a message that later gains thoughts or tool results
    // keeps the ids of the parts it already had.
    const id = str(m.id);
    const count = new Map();
    return out.map((r) => {
        const n = count.get(r.kind) ?? 0;
        count.set(r.kind, n + 1);
        return { ...r, t, ...(id ? { key: `${id}:${r.kind}:${n}` } : {}) };
    });
}
function geminiRecord(o, state) {
    const set = isObj(o.$set) ? o.$set : undefined;
    if (set && Array.isArray(set.messages)) {
        let list = set.messages.filter(isObj);
        // The last message already seen may have grown; everything before it was sent already.
        const from = state?.lastGeminiId === undefined ? -1 : list.findIndex((m) => m.id === state.lastGeminiId);
        if (from > 0)
            list = list.slice(from);
        const last = list.at(-1);
        if (state && last && typeof last.id === "string")
            state.lastGeminiId = last.id;
        return list.flatMap(geminiMessage);
    }
    if (typeof o.type === "string" && GEMINI_TYPES.has(o.type)) {
        if (state && typeof o.id === "string")
            state.lastGeminiId = o.id;
        return geminiMessage(o);
    }
    return [];
}
// ---- Muse ----
function museMessage(m) {
    const out = [];
    const parts = Array.isArray(m.content) ? m.content.filter(isObj) : [];
    for (const c of parts) {
        const kind = str(c.kind) || str(c.type);
        if (/reason|think/.test(kind))
            out.push({ kind: "thinking", label: LABEL.thinking, text: textOf(c) });
        else if (/tool_(?:call|use)|function_call/.test(kind))
            out.push(toolRaw(str(c.name), c.input ?? c.arguments));
        else if (/tool_result|output/.test(kind) && kind !== "output_text")
            out.push({ kind: "result", label: LABEL.result, text: textOf(c) });
        else if (m.role === "user")
            out.push({ kind: "prompt", label: LABEL.prompt, text: textOf(c) });
        else if (m.role === "assistant")
            out.push({ kind: "text", label: LABEL.text, text: textOf(c) });
    }
    if (typeof m.content === "string")
        out.push(m.role === "user" ? { kind: "prompt", label: LABEL.prompt, text: m.content } : { kind: "text", label: LABEL.text, text: m.content });
    return out;
}
function museRecord(o) {
    if (!isObj(o.payload) || "retained_frame" in o || "retained_marker" in o)
        return [];
    const p = o.payload;
    const out = [];
    if (Array.isArray(p.model_messages))
        for (const m of p.model_messages.filter(isObj))
            out.push(...museMessage(m));
    if (isObj(p.turn)) {
        if (str(p.turn.prompt))
            out.push({ kind: "prompt", label: LABEL.prompt, text: str(p.turn.prompt) });
        if (str(p.turn.answer))
            out.push({ kind: "text", label: LABEL.text, text: str(p.turn.answer) });
    }
    for (const m of [p.message, isObj(p.record) ? p.record.message : undefined])
        if (isObj(m) && typeof m.role === "string")
            out.push(...museMessage(m));
    const us = typeof o.recorded_at === "number" && Number.isFinite(o.recorded_at) ? o.recorded_at : undefined;
    const t = us === undefined ? undefined : Math.floor(us > 1e14 ? us / 1000 : us);
    const id = str(o.id);
    return out.map((r, i) => ({ ...r, t, ...(id ? { key: `${id}:${i}` } : {}) }));
}
const PARSERS = { claude: claudeRecord, codex: codexRecord, gemini: geminiRecord, muse: museRecord };
/** Redact the whole text, shorten paths, cut to one line of at most 400 characters, redact the cut again. */
function shown(text, homeDir) {
    return redactSecrets(cleanText(shortenAbsolutePaths(redactSecrets(text.slice(0, MAX_RAW)), homeDir), MAX_FEED_TEXT));
}
function entryId(sid, key) {
    return createHash("sha256").update(`${sid}\0${key}`).digest("hex").slice(0, 16);
}
/**
 * The entries of complete JSONL lines in `buf`, which starts at byte `start` of the log (ids of records
 * without their own id come from the absolute line offset, so they stay stable across reads). Entries with
 * empty text are dropped; within one call an id appears once.
 */
export function feedEntries(buf, start, ctx) {
    const out = [];
    const seen = new Set();
    const parse = PARSERS[ctx.source];
    let pos = 0;
    while (pos < buf.length) {
        let nl = buf.indexOf(10, pos);
        if (nl < 0)
            nl = buf.length;
        const offset = start + pos;
        const line = buf.subarray(pos, nl).toString("utf8").trim();
        pos = nl + 1;
        if (line === "" || line[0] !== "{")
            continue;
        let raws;
        try {
            const v = JSON.parse(line);
            raws = isObj(v) ? parse(v, ctx.state) : [];
        }
        catch {
            continue;
        }
        raws.forEach((r, i) => {
            const text = shown(r.text, ctx.homeDir);
            if (text === "")
                return;
            const id = entryId(ctx.sid, r.key ?? `@${offset}:${i}`);
            if (seen.has(id))
                return;
            seen.add(id);
            // Labels carry tool names: redacted like the text; a tool name that needs redaction is left out.
            const e = { id, t: r.t ?? ctx.now, kind: r.kind, label: redactSecrets(cleanText(redactSecrets(r.label), MAX_FEED_LABEL)), text };
            if (r.tool && redactSecrets(r.tool) === r.tool)
                e.tool = r.tool;
            out.push(e);
        });
    }
    return out;
}
