// Local transcript tails for the agent summary. Only files inside the tools' own transcript folders are
// read (real paths compared, so a link cannot point elsewhere), only `.jsonl`, and only the last 64 KB.
// The text stays on the PC: agent-detail.ts turns at most 30 lines into a redacted, fenced AI prompt.

import { existsSync, realpathSync, statSync } from "node:fs";
import { open } from "node:fs/promises";
import { isAbsolute, join, resolve } from "node:path";
import type { AgentSource } from "./events.js";
import { relInside } from "./risk/paths.js";

export const MAX_TAIL_BYTES = 64 * 1024;
export const MAX_LINES = 30;
const MAX_LINE_CHARS = 300;
const MAX_DEPTH = 8;
const SID_RE = /^[A-Za-z0-9_-]{1,128}$/;
/** Muse names a session folder by date; a session may have started a day or two ago. */
const MUSE_DAYS = 3;

/** The folders a tool keeps its transcripts in (env overrides as the tools read them). */
export function transcriptRoots(source: AgentSource, homeDir: string, env: NodeJS.ProcessEnv): string[] {
  switch (source) {
    case "claude":
      return [join(homeDir, ".claude", "projects"), ...(env.CLAUDE_CONFIG_DIR ? [join(resolve(env.CLAUDE_CONFIG_DIR), "projects")] : [])];
    case "codex":
      return [join(homeDir, ".codex", "sessions"), ...(env.CODEX_HOME ? [join(resolve(env.CODEX_HOME), "sessions")] : [])];
    case "gemini":
      return [join(homeDir, ".gemini", "tmp"), ...(env.GEMINI_CLI_HOME ? [join(resolve(env.GEMINI_CLI_HOME), ".gemini", "tmp")] : [])];
    case "muse":
      return [join(homeDir, ".local", "share", "muse", "sessions")];
    case "opencode":
      return []; // OpenCode keeps its sessions in a database, not in a transcript file

  }
}

function real(p: string): string | null {
  try {
    return realpathSync.native(p);
  } catch {
    return null;
  }
}

/**
 * The real path of `path` when it is an existing `.jsonl` file below one of `roots` (real paths too). The
 * real path is what gets read, so a link swapped in after this check is not followed.
 */
function allowed(path: string, roots: string[], realpath: (p: string) => string | null): string | null {
  if (!isAbsolute(path) || !/\.jsonl$/i.test(path) || path.includes("\0")) return null;
  const file = realpath(path);
  // The name a link points at must be a transcript too, not just the link's own name.
  if (file === null || !/\.jsonl$/i.test(file)) return null;
  try {
    if (!statSync(file).isFile()) return null;
  } catch {
    return null;
  }
  for (const root of roots) {
    const r = realpath(root);
    if (r !== null && relInside(file, r) !== null) return file;
  }
  return null;
}

const pad = (n: number) => String(n).padStart(2, "0");

/**
 * The transcript to read for a session, or null. Claude, Codex and Gemini send `transcript_path`; Muse sends
 * none, so its file is looked up by session id in `sessions/YYYY/MM/DD/<sid>/session.jsonl` of the last days.
 */
export function resolveTranscript(o: {
  sid: string;
  source: AgentSource;
  path: unknown;
  homeDir: string;
  env: NodeJS.ProcessEnv;
  now?: Date;
  /** Real-path lookup (tests); default realpathSync.native. */
  realpath?: (p: string) => string | null;
}): string | null {
  const roots = transcriptRoots(o.source, o.homeDir, o.env);
  const realpath = o.realpath ?? real;
  if (typeof o.path === "string" && o.path !== "") return allowed(o.path, roots, realpath);
  if (o.source !== "muse" || !SID_RE.test(o.sid)) return null;
  const now = o.now ?? new Date();
  for (let back = 0; back < MUSE_DAYS; back++) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - back);
    const file = join(roots[0], String(d.getFullYear()), pad(d.getMonth() + 1), pad(d.getDate()), o.sid, "session.jsonl");
    if (existsSync(file)) return allowed(file, roots, realpath);
  }
  return null;
}

/** The last 64 KB of a file as text, without the (cut) first line when the file is longer; "" on any error. */
export async function readTail(path: string, maxBytes: number = MAX_TAIL_BYTES): Promise<string> {
  let fh;
  try {
    fh = await open(path, "r");
    const { size } = await fh.stat();
    const start = Math.max(0, size - maxBytes);
    const buf = Buffer.alloc(size - start);
    const { bytesRead } = await fh.read(buf, 0, buf.length, start);
    const text = buf.subarray(0, bytesRead).toString("utf8");
    if (start === 0) return text;
    const nl = text.indexOf("\n");
    return nl < 0 ? "" : text.slice(nl + 1);
  } catch {
    return "";
  } finally {
    await fh?.close().catch(() => {});
  }
}

type Obj = Record<string, unknown>;
const isObj = (v: unknown): v is Obj => typeof v === "object" && v !== null && !Array.isArray(v);

/** Keys whose values are bookkeeping or private reasoning, never shown. */
const SKIP_KEYS = new Set([
  "type", "id", "uuid", "parentUuid", "parentId", "timestamp", "sessionId", "session_id", "cwd", "version", "gitBranch",
  "requestId", "model", "usage", "signature", "thinking", "encrypted_content", "tool_use_id", "call_id", "projectHash",
  "role", "isSidechain", "userType", "leafUuid", "ordinal",
]);
/** Whole records that hold settings, not conversation. */
const SKIP_TYPES = new Set(["session_meta", "turn_context", "file-history-snapshot"]);
const ROLE_RE = /^[A-Za-z_]{1,20}$/;

function roleOf(o: Obj): string {
  const msg = isObj(o.message) ? o.message : undefined;
  const payload = isObj(o.payload) ? o.payload : undefined;
  for (const r of [msg?.role, o.role, payload?.role, o.type, payload?.type]) {
    if (typeof r === "string" && ROLE_RE.test(r)) return r;
  }
  return "eintrag";
}

/** Every shown string below `v`, depth-first, until `budget` characters are collected. */
function collect(v: unknown, out: string[], budget: { left: number }, depth: number): void {
  if (budget.left <= 0 || depth > MAX_DEPTH) return;
  if (typeof v === "string") {
    const s = v.replace(/\s+/g, " ").trim();
    if (s === "") return;
    out.push(s.slice(0, budget.left));
    budget.left -= s.length + 1;
  } else if (Array.isArray(v)) {
    for (const x of v) collect(x, out, budget, depth + 1);
  } else if (isObj(v)) {
    for (const [k, x] of Object.entries(v)) if (!SKIP_KEYS.has(k)) collect(x, out, budget, depth + 1);
  }
}

function entry(o: Obj): string | null {
  if (typeof o.type === "string" && SKIP_TYPES.has(o.type)) return null;
  const parts: string[] = [];
  collect(o, parts, { left: MAX_LINE_CHARS }, 0);
  const text = parts.join(" ").slice(0, MAX_LINE_CHARS);
  return text === "" ? null : `${roleOf(o)}: ${text}`;
}

/** One record of a JSONL transcript as display entries (Gemini packs several messages into one record). */
function entriesOf(o: Obj): string[] {
  const set = isObj(o.$set) ? o.$set : undefined;
  const list = Array.isArray(set?.messages) ? set.messages : Array.isArray(o.messages) ? o.messages : null;
  if (list) return list.filter(isObj).map(entry).filter((x): x is string => x !== null);
  // a Gemini header line ({sessionId, projectHash, ...}) carries no conversation
  if (typeof o.projectHash === "string" && o.message === undefined && o.content === undefined) return [];
  const e = entry(o);
  return e === null ? [] : [e];
}

/** The last 30 conversation entries of a JSONL text as `role: text` (each at most ~300 characters). */
export function transcriptLines(text: string, max: number = MAX_LINES): string[] {
  const out: string[] = [];
  for (const line of text.split(/\r?\n/)) {
    if (line.trim() === "") continue;
    let v: unknown;
    try {
      v = JSON.parse(line);
    } catch {
      continue;
    }
    if (isObj(v)) out.push(...entriesOf(v));
  }
  return out.slice(-max);
}
