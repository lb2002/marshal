// What a destructive request would hit ("impact", contract `.superpowers/sdd/2026-11-12-contract-impact-halt.md`).
//
// Principle (ruling after the security review): Lotse only states losses it is sure of. It never states that
// something is safe or recoverable; in doubt it says "Effects unknown". So `recoverable` is "no" or "unknown",
// and every reason is one of the fixed sentences in R below; nothing from the request or the disk goes into it.
//
// Threat model: the coding agent controls the working directory (also `.git`), every string of the request and
// the payload's cwd. The check never runs anything, never reads a file's contents, never follows a link and never
// touches a network location:
// - Targets come from the risk lexer's parse; anything not literal is unknown, with no file-system access.
// - The cwd and every target are walked one component at a time from their root with `lstat` (walkFrom): a
//   component that is a link (symbolic link, junction), missing, or not a directory where one is needed stops the
//   walk at once and makes the result unknown. `..` is only taken after the walk reached a real directory.
// - Before every `lstat` the path must be a local absolute path outside the automount and network locations of
//   the platform (look); on Windows a POSIX-style absolute path is unknown, except a Git Bash drive path.
// - Git: only whether a `.git` entry exists in the target's directory or a parent (lstat, at most 40 levels).
// - Counts: a bounded `lstat` walk (20,000 entries, 400 ms for the whole check), links counted, never followed.
// - At most one computation in the whole bridge at a time, 8 queued, 2 s of work per 10 s (ImpactScheduler).
//
// This module and everything it imports must not import anything that can start a process or open a socket
// (test/impact-imports.test.ts walks the import graph).

import { lstat, opendir } from "node:fs/promises";
import { posix, win32 } from "node:path";
import { redactSecrets, shortenAbsolutePaths } from "./anonymize.js";
import type { AgentSource } from "./events.js";
import type { RiskResult } from "./risk.js";
import { lex, LexError } from "./risk/lexer.js";
import type { Mode, Segment, Word } from "./risk/lexer.js";
import { CONTROL, isLocalAbsolute, MAX_PATH_LEN, networkPath, winComponentOk } from "./local-path.js";
import { PRODUCT_NAME } from "./product.js";
import { cleanText } from "./text.js";

export { isLocalAbsolute, networkPath };

export type ImpactKind = "delete" | "overwrite" | "force_push" | "reset" | "clean" | "other";
/** Only a sure loss ("no") or nothing known ("unknown"): never a reassurance. */
export type Recoverable = "no" | "unknown";

export interface Impact {
  kind: ImpactKind;
  target?: string;
  files?: number;
  filesCapped?: boolean;
  bytes?: number;
  outsideProject?: boolean;
  recoverable: Recoverable;
  reason: string;
}

/** The only reasons an impact can carry (rulings task-p2-fix-1.md A3, A5, A6; task-p2-fix-2.md A1, A7). */
export const R = {
  notInGit: "Not in a Git project: gone for good.",
  inGit: `In a Git project. ${PRODUCT_NAME} does not check whether everything is saved.`,
  ambiguous: "Target unclear. Effects unknown.",
  reset: "Discards all unsaved changes in the project. They are gone afterwards.",
  clean: "Deletes every file that is not in Git. They are gone afterwards.",
  push: "Overwrites what is on the server. Anything new there is gone afterwards.",
  wholeRepo: "Deletes a whole Git project with its history: gone for good.",
  notOne: "Several steps or unclear. Effects unknown.",
} as const;

export const MAX_WALK_ENTRIES = 20_000;
/** Wall time for the whole check of one request (path walks, Git search, counting). */
export const MAX_WALK_MS = 400;
const MAX_TARGET = 120;
const MAX_GIT_UP = 40;
/** More targets than this in one request is unknown (review M4). */
const MAX_TARGETS = 32;
/** The check yields to the event loop after this many file-system calls. */
const YIELD_EVERY = 32;

export type EntryKind = "file" | "dir" | "link" | "other";

export interface FsEntry {
  kind: EntryKind;
  size: number;
  /** Identity ("dev:ino"), to notice a directory swapped while it was listed (optional). */
  id?: string;
}

/** The only file-system access of the impact check. */
export interface ImpactFs {
  /** lstat (never follows a link); null when the path does not exist. Throws for any other error. */
  lstat(path: string): Promise<FsEntry | null>;
  /** At most `max` of a directory's entries (the kind as the listing reports it); `more` when it has further ones. */
  readdir(path: string, max: number): Promise<{ entries: { name: string; kind: EntryKind }[]; more: boolean }>;
}

export interface ImpactDeps {
  fs: ImpactFs;
  now(): number;
  platform: NodeJS.Platform;
  homeDir: string;
}

export interface ImpactRequest {
  tool: string;
  input: Record<string, unknown>;
  cwd: string;
  source?: AgentSource;
  risk: RiskResult;
}

// ---------------------------------------------------------------- entry point

/** The impact of a request, or null when nothing useful is known. Never throws. */
export async function computeImpact(req: ImpactRequest, deps: ImpactDeps): Promise<Impact | null> {
  try {
    if (req.risk?.risk !== "destructive") return null;
    const ctx: Ctx = { deps, P: deps.platform === "win32" ? win32 : posix, deadline: deps.now() + MAX_WALK_MS, ops: 0 };
    const out = await analyse(req, ctx);
    return out === null ? null : finish(out, deps.homeDir);
  } catch {
    return null;
  }
}

/** The impact of a line that is not exactly one simple literal command: no target, no counts, no disk access. */
export const NOT_ONE: Impact = { kind: "other", recoverable: "unknown", reason: R.notOne };

/** The impact sent when the global budget is used up: unknown, without touching the disk. */
export const BUDGET_IMPACT: Impact = { kind: "other", recoverable: "unknown", reason: R.ambiguous };

/** Caps and cleans the display fields. */
function finish(i: Impact, homeDir: string): Impact {
  const out: Impact = { kind: i.kind, recoverable: i.recoverable, reason: i.reason };
  if (i.target !== undefined) {
    const t = cleanText(shortenAbsolutePaths(redactSecrets(cleanText(i.target, MAX_PATH_LEN)), homeDir), MAX_TARGET);
    if (t !== "") out.target = t;
  }
  for (const k of ["files", "bytes"] as const) {
    const v = i[k];
    if (typeof v === "number" && Number.isSafeInteger(v) && v >= 0) out[k] = v;
  }
  if (i.filesCapped) out.filesCapped = true;
  if (i.outsideProject !== undefined) out.outsideProject = i.outsideProject;
  return out;
}

interface Ctx {
  deps: ImpactDeps;
  P: typeof posix;
  deadline: number;
  ops: number;
}

const WRITE_TOOLS: Record<string, string> = { write: "file_path", edit: "file_path", multiedit: "file_path", notebookedit: "notebook_path" };

const NOT_ELIGIBLE: unique symbol = Symbol("not-eligible");
const MAX_ARGV = 64;

/**
 * The command: a line to lex, or an argument array taken as it is (never joined and re-lexed: `["rm","a b"]` names
 * one path); NOT_ELIGIBLE past MAX_COMMAND, for a bad array, or for an array of a shape the bridge does not read.
 */
function commandOf(input: Record<string, unknown>): string | { argv: string[] } | null | typeof NOT_ELIGIBLE {
  const c = input.command;
  if (typeof c === "string") return c.length > MAX_COMMAND ? NOT_ELIGIBLE : c;
  if (!Array.isArray(c)) return null;
  if (c.length === 0 || c.length > MAX_ARGV) return NOT_ELIGIBLE;
  let n = 0;
  for (const x of c) {
    if (typeof x !== "string") return NOT_ELIGIBLE;
    n += x.length + 1;
    if (n > MAX_COMMAND + 1) return NOT_ELIGIBLE;
  }
  return { argv: c as string[] };
}

const SHELLS: Record<string, Mode | "cmd"> = { sh: "bash", bash: "bash", zsh: "bash", dash: "bash", cmd: "cmd", powershell: "pwsh", pwsh: "pwsh" };

/**
 * An argument array: a shell or interpreter in element 0 wraps one command line, read only in the shape
 * `shell -c|-lc|/c|-Command [string]` or `shell -NoProfile -Command [string]`, by that shell's rules; anything else
 * is one command whose elements are its literal words.
 */
function argvPlan(argv: string[]): Plan | null {
  const base = argv[0].split(/[\\/]/).pop()?.toLowerCase().replace(/\.exe$/, "") ?? "";
  const shell = SHELLS[base];
  if (shell === undefined) return planWords(argv.map((text) => ({ text, dyn: false, glob: false, quoted: false, inert: false })), "bash");
  if (shell === "cmd") return null; // cmd.exe lines are not read
  const flags = argv.slice(1, -1).map((f) => f.toLowerCase());
  const ok = shell === "pwsh" ? ["-command", "-c"] : ["-c", "-lc"];
  const shape = (flags.length === 1 && ok.includes(flags[0])) || (shell === "pwsh" && flags.length === 2 && flags[0] === "-noprofile" && flags[1] === "-command");
  return argv.length >= 3 && shape ? planFor(argv[argv.length - 1], shell) : null;
}

async function analyse(req: ImpactRequest, ctx: Ctx): Promise<Impact | null> {
  const input = req.input && typeof req.input === "object" ? req.input : {};
  const name = String(req.tool ?? "").toLowerCase();
  const cwd = typeof req.cwd === "string" ? req.cwd : "";
  const field = WRITE_TOOLS[name];
  if (field !== undefined) {
    const p = input[field];
    if (typeof p !== "string" || p === "") return null;
    return targetsImpact("overwrite", [p], cwd, ctx);
  }
  if (name === "apply_patch") return null; // a patch is read by its headers (risk/patch.ts), not here
  const command = commandOf(input);
  if (command === null) return null;
  if (command === NOT_ELIGIBLE) return NOT_ONE;
  const plan = typeof command === "string" ? shellPlan(command, dialects(name, req.source, ctx.deps.platform)) : argvPlan(command.argv);
  if (plan === null) return NOT_ONE;
  switch (plan.op) {
    case "delete":
      return targetsImpact("delete", plan.targets, cwd, ctx);
    // These destroy by what they do; no file-system fact is needed or added.
    case "push":
      return { kind: "force_push", ...(plan.target ? { target: plan.target } : {}), recoverable: "no", reason: R.push };
    case "reset":
      return { kind: "reset", recoverable: "no", reason: R.reset };
    case "clean":
      return { kind: "clean", recoverable: "no", reason: R.clean };
  }
}

// ---------------------------------------------------------------- shell parse: one simple literal command only

/**
 * Facts are given only for a line that is exactly one simple command the bridge understands, with literal
 * arguments (rulings task-p2-fix-2.md A). A line the bridge cannot fully account for (a chain, a sourced script, a
 * function, a directory change, an expansion, a redirection, a wildcard PowerShell would expand, an unknown flag)
 * gets NOT_ONE: the impact of one part would be presented as the impact of the whole request.
 */
export const MAX_COMMAND = 4096;

/** Same dialect choice as the classifier (risk.ts shellRisk): other tools on Windows get both. */
function dialects(tool: string, source: AgentSource | undefined, platform: NodeJS.Platform): Mode[] {
  const own: Mode = tool === "powershell" ? "pwsh" : "bash";
  if (source === undefined || source === "claude" || platform !== "win32") return [own];
  return [own, own === "bash" ? "pwsh" : "bash"];
}

type Plan = { op: "delete"; targets: string[] } | { op: "push"; target?: string } | { op: "reset" } | { op: "clean" };

/** The plan of an eligible line, the same in every dialect; null when the line is not eligible. */
function shellPlan(command: string, modes: Mode[]): Plan | null {
  if (command.length > MAX_COMMAND) return null; // before any lexing (review I-3)
  let first: string | undefined;
  let plan: Plan | null = null;
  for (const m of modes) {
    const p = planFor(command, m);
    if (p === null) return null;
    const key = JSON.stringify(p);
    if (first !== undefined && key !== first) return null;
    first = key;
    plan = p;
  }
  return plan;
}

/**
 * Characters that make a bash line not one simple literal command, anywhere (quoted or not): separators, pipes,
 * redirections, subshells and groups, substitutions, expansions, globs, `!`, comments, escapes, control
 * characters (a tab separates words and is allowed).
 */
const BASH_UNSAFE = /[;&|<>()`$\\{}*?[\]!#%^\u0000-\u0008\u000a-\u001f\u007f]/;
/** The same for PowerShell; `[ ] * ?` are checked per path value (literal only through -LiteralPath). */
const PWSH_UNSAFE = /[;&|<>(){}@`#%\u0000-\u0008\u000a-\u001f\u007f]|::/;

const RM_LONG = new Set(["--recursive", "--force", "--verbose", "--dir"]);
const RM_SHORT = /^-[rRfvdiI]+$/;
const RMDIR_FLAGS = new Set(["-v", "--verbose"]);
const PWSH_DELETERS = new Set(["remove-item", "rm", "del", "rd", "ri", "erase", "rmdir"]);

function planFor(command: string, mode: Mode): Plan | null {
  if ((mode === "pwsh" ? PWSH_UNSAFE : BASH_UNSAFE).test(command)) return null;
  let segs: Segment[];
  try {
    segs = lex(command, mode);
  } catch (e) {
    if (e instanceof LexError) return null;
    throw e;
  }
  const used = segs.filter((s) => s.words.length > 0 || s.redirects.length > 0 || s.heredocs.length > 0);
  if (used.length !== 1) return null;
  const seg = used[0];
  if (seg.redirects.length > 0 || seg.heredocs.length > 0 || seg.substitutions.length > 0 || seg.pipedIn || seg.funcDef || seg.compound) return null;
  return planWords(seg.words, mode);
}

/** The plan of one command's words (from the lexer, or literal from an argument array). */
function planWords(words: Word[], mode: Mode): Plan | null {
  if (words.length === 0) return null;
  // `~` expands at the start of a word or after `=`/`:` (bash), and names the home at the start of a path (PowerShell).
  if (words.some((w) => w.text === "" || w.inert || w.text.includes(",") || /(^|[=:])~/.test(w.text))) return null;
  // The command word is written plainly: no quotes, no path, a name the bridge knows.
  const head = words[0];
  if (head.quoted || head.dyn || head.glob) return null;
  const name = mode === "pwsh" ? head.text.toLowerCase() : head.text;
  const args = words.slice(1);
  if (name === "git") return args.some((w) => w.dyn || w.glob || w.text.includes("$")) ? null : gitPlan(args.map((w) => w.text));
  if (mode === "pwsh") return PWSH_DELETERS.has(name) ? removeItemPlan(args) : null;
  if (args.some((w) => w.dyn || w.glob)) return null;
  const a = args.map((w) => w.text);
  if (name === "rm") return posixDelete(a, (f) => RM_LONG.has(f) || RM_SHORT.test(f));
  if (name === "rmdir") return posixDelete(a, (f) => RMDIR_FLAGS.has(f));
  if (name === "unlink") return posixDelete(a, () => false);
  return null;
}

/** A path operand rm refuses (`.`, `..`, or ending in `/.` or `/..`), or an empty one: not eligible. */
const refusedOperand = (t: string) => t === "" || /(^|[\\/])\.\.?[\\/]*$/.test(t);

/** rm/rmdir/unlink: only flags `known` accepts before `--`; at least one target. */
function posixDelete(args: string[], known: (flag: string) => boolean): Plan | null {
  const targets: string[] = [];
  let opts = true;
  for (const t of args) {
    if (opts && t === "--") {
      opts = false;
      continue;
    }
    if (opts && t.startsWith("-")) {
      if (!known(t)) return null;
      continue;
    }
    targets.push(t);
  }
  if (targets.length === 0 || targets.some(refusedOperand)) return null;
  return { op: "delete", targets };
}

const isPrefix = (p: string, full: string, min: number) => p.length >= min && full.startsWith(p);

/**
 * Remove-Item (and its known aliases): -Recurse, -Force, -Path, -LiteralPath, -Confirm:$false, -ErrorAction <name>.
 * A `-Path` or positional value with `[ ] * ?` is a wildcard to PowerShell even when quoted (review I-2); only
 * `-LiteralPath` values are literal. `$` appears only in `-Confirm:$false`.
 */
function removeItemPlan(args: Word[]): Plan | null {
  const targets: string[] = [];
  const path = (w: Word | undefined, literal: boolean): boolean => {
    if (w === undefined || w.text.includes("$") || refusedOperand(w.text)) return false;
    if (!literal && /[[\]*?]/.test(w.text)) return false;
    targets.push(w.text);
    return true;
  };
  for (let i = 0; i < args.length; i++) {
    const w = args[i];
    const t = w.text;
    if (w.quoted || !t.startsWith("-") || t === "-") {
      if (!path(w, false)) return null;
      continue;
    }
    const p = t.slice(1).toLowerCase();
    if (p === "confirm:$false") continue;
    if (p.includes(":") || p.includes("$")) return null;
    if (isPrefix(p, "recurse", 1) || isPrefix(p, "force", 2)) continue;
    if (isPrefix(p, "path", 2)) {
      if (!path(args[++i], false)) return null;
      continue;
    }
    if (isPrefix(p, "literalpath", 1) || p === "lp" || p === "pspath") {
      if (!path(args[++i], true)) return null;
      continue;
    }
    if (isPrefix(p, "erroraction", 6) || p === "ea") {
      const v = args[++i];
      if (v === undefined || !/^[A-Za-z]+$|^[0-6]$/.test(v.text)) return null;
      continue;
    }
    return null;
  }
  return targets.length > 0 ? { op: "delete", targets } : null;
}

/** `git push` (forced), `git reset --hard`, `git clean -f…`: only the forms of fix round 1, no global options. */
function gitPlan(a: string[]): Plan | null {
  const [sub, ...rest] = a;
  if (sub === "push") {
    let forced = false;
    const pos: string[] = [];
    for (const x of rest) {
      if (x === "-f" || x === "--force" || x === "--force-with-lease" || x.startsWith("--force-with-lease=")) forced = true;
      else if (x.startsWith("-")) return null;
      else pos.push(x);
    }
    if (pos.length > 2) return null;
    if (!forced && !(pos[1] ?? "").startsWith("+")) return null;
    return pos.length > 0 ? { op: "push", target: pos.join(" ") } : { op: "push" };
  }
  if (sub === "reset") {
    const pos = rest.filter((x) => x !== "--hard");
    if (!rest.includes("--hard") || pos.length > 1 || pos.some((x) => x.startsWith("-"))) return null;
    return { op: "reset" };
  }
  if (sub === "clean") {
    let forced = false;
    let opts = true;
    for (const x of rest) {
      if (opts && x === "--") opts = false;
      else if (opts && (x === "--force" || /^-[fdxXq]+$/.test(x))) forced ||= x === "--force" || x.includes("f");
      else if (opts && x === "--quiet") continue;
      else if (opts && x.startsWith("-")) return null;
    }
    return forced ? { op: "clean" } : null;
  }
  return null;
}

// ---------------------------------------------------------------- where the file system may be touched


const REFUSED: unique symbol = Symbol("refused");
type Look = FsEntry | null | typeof REFUSED;

/**
 * The one door to the file system: refuses (without touching anything) a path that is not local or lies in a
 * network location, turns errors into REFUSED, and yields to the event loop every few calls.
 */
async function look(path: string, ctx: Ctx): Promise<Look> {
  const platform = ctx.deps.platform;
  if (!isLocalAbsolute(path, platform) || networkPath(path, platform)) return REFUSED;
  await pace(ctx);
  try {
    return await ctx.deps.fs.lstat(path);
  } catch {
    return REFUSED;
  }
}

async function pace(ctx: Ctx): Promise<void> {
  if (++ctx.ops % YIELD_EVERY === 0) await new Promise<void>((r) => setImmediate(r));
}

const late = (ctx: Ctx) => ctx.deps.now() >= ctx.deadline;

interface Found {
  path: string;
  entry: FsEntry;
}

/**
 * Walks `parts` from the real directory `start`, one `lstat` per component, as written (no lexical folding of
 * `..`, which is only taken after a real directory). null (unknown) at the first component that is a link,
 * missing, refused, not a directory where one is needed, or a Windows name the shells may read differently;
 * nothing beyond it is touched.
 */
async function walkFrom(start: Found, parts: string[], trailingSep: boolean, ctx: Ctx): Promise<Found | null> {
  const { P } = ctx;
  let cur = start;
  for (const c of parts) {
    if (late(ctx)) return null;
    if (c === "") continue;
    // `x/.`, `x/..` and `x/y` all need `x` to be a real directory (the shells fail on a file).
    if (cur.entry.kind !== "dir") return null;
    if (c === ".") continue;
    if (c === "..") {
      const up = P.dirname(cur.path);
      if (up === cur.path) continue;
      const e = await look(up, ctx);
      if (e === null || e === REFUSED || e.kind !== "dir") return null;
      cur = { path: up, entry: e };
      continue;
    }
    if (ctx.deps.platform === "win32" && !winComponentOk(c)) return null;
    const next = P.join(cur.path, c);
    const e = await look(next, ctx);
    if (e === null || e === REFUSED || e.kind === "link") return null;
    cur = { path: next, entry: e };
  }
  if (trailingSep && cur.entry.kind !== "dir") return null;
  return cur;
}

const splitParts = (rest: string, platform: NodeJS.Platform) => rest.split(platform === "win32" ? /[\\/]/ : "/");
const endsWithSep = (s: string, platform: NodeJS.Platform) => (platform === "win32" ? /[\\/]$/ : /\/$/).test(s);

/** The root of an absolute local path and the components below it. */
function rootAndParts(abs: string, platform: NodeJS.Platform): { root: string; parts: string[] } {
  if (platform === "win32") return { root: abs.slice(0, 2).toUpperCase() + "\\", parts: splitParts(abs.slice(3), platform) };
  return { root: "/", parts: splitParts(abs.slice(1), platform) };
}

async function rootEntry(root: string, ctx: Ctx): Promise<Found | null> {
  const e = await look(root, ctx);
  return e === null || e === REFUSED || e.kind !== "dir" ? null : { path: root, entry: e };
}

/** The session cwd, walked from its root: every component a real directory (review I4: it comes from the payload). */
async function realCwd(cwd: string, ctx: Ctx): Promise<Found | null> {
  const platform = ctx.deps.platform;
  if (!isLocalAbsolute(cwd, platform)) return null;
  const { root, parts } = rootAndParts(cwd, platform);
  if (parts.some((p) => p === "." || p === "..")) return null;
  const start = await rootEntry(root, ctx);
  if (start === null) return null;
  const out = await walkFrom(start, parts, false, ctx);
  return out !== null && out.entry.kind === "dir" ? out : null;
}

/** A target that is unknown from its text alone (no file system needed). */
function refusedText(raw: string, platform: NodeJS.Platform): boolean {
  if (raw === "" || raw.length > MAX_PATH_LEN || CONTROL.test(raw) || raw.startsWith("~") || /^[\\/]{2}/.test(raw)) return true;
  if (platform === "win32") {
    if (/^[A-Za-z]:[\\/]/.test(raw)) return false;
    if (/^[A-Za-z]:/.test(raw)) return true; // drive-relative
    return /^[\\/]/.test(raw) && !/^\/[A-Za-z](\/|$)/.test(raw);
  }
  return raw.startsWith("/") && networkPath(raw, platform);
}

/** Where the shells would find `raw`, or null when that is not certain (ruling A6). */
async function findTarget(raw: string, cwd: Found, ctx: Ctx): Promise<Found | null> {
  const platform = ctx.deps.platform;
  if (refusedText(raw, platform)) return null;
  const trailing = endsWithSep(raw, platform);
  if (platform === "win32") {
    if (/^[A-Za-z]:[\\/]/.test(raw)) {
      const start = await rootEntry(raw.slice(0, 2).toUpperCase() + "\\", ctx);
      return start && walkFrom(start, splitParts(raw.slice(3), platform), trailing, ctx);
    }
    if (/^[A-Za-z]:/.test(raw)) return null; // drive-relative
    if (/^[\\/]/.test(raw)) {
      // Git Bash reads `/c/rest` as `C:\rest`; Windows tools as `<cwd drive>:\c\rest`. Only when the latter does
      // not exist is the reading certain. Every other rooted form (`/tmp`, `/cygdrive/…`, `/mnt/c/…`, `\x`) is not.
      const m = /^\/([A-Za-z])(\/.*)?$/.exec(raw);
      if (m === null) return null;
      const shadow = await look(cwd.path.slice(0, 3) + m[1], ctx);
      if (shadow !== null) return null;
      const start = await rootEntry(m[1].toUpperCase() + ":\\", ctx);
      return start && walkFrom(start, splitParts((m[2] ?? "").slice(1), platform), trailing, ctx);
    }
    return walkFrom(cwd, splitParts(raw, platform), trailing, ctx);
  }
  if (raw.startsWith("/")) {
    const start = await rootEntry("/", ctx);
    return start && walkFrom(start, splitParts(raw.slice(1), platform), trailing, ctx);
  }
  return walkFrom(cwd, splitParts(raw, platform), trailing, ctx);
}

function isUnder(child: string, parent: string, P: typeof posix): boolean {
  const rel = P.relative(parent, child);
  if (rel === "") return true;
  return rel !== ".." && !rel.startsWith(".." + P.sep) && !P.isAbsolute(rel) && !/^[A-Za-z]:/.test(rel);
}

/** Whether a `.git` entry exists in `dir` or a parent (lstat only, at most 40 levels); null when that is not known. */
async function inGit(dir: string, ctx: Ctx): Promise<boolean | null> {
  const { P } = ctx;
  let d = dir;
  for (let n = 0; n < MAX_GIT_UP; n++) {
    if (late(ctx)) return null;
    const e = await look(P.join(d, ".git"), ctx);
    if (e === REFUSED) return null;
    if (e !== null) return true;
    const up = P.dirname(d);
    if (up === d) return false;
    d = up;
  }
  return null;
}

interface Count {
  files: number;
  bytes: number;
  capped: boolean;
  /** Something changed under the walk (a directory swapped, a link the listing did not report): the verdict is unknown. */
  swapped?: boolean;
}

/**
 * Files and bytes at and below `t`, links counted as one file and never followed, within the entry and time caps.
 * The walk works by name (Node has no openat/fstatat). It stops at once, with the verdict unknown, the first time a
 * directory is not the one checked before listing it, or a child is a link the listing did not report as one.
 */
async function count(t: Found, budget: { entries: number }, ctx: Ctx): Promise<Count> {
  const { P } = ctx;
  const out: Count = { files: 0, bytes: 0, capped: false };
  if (t.entry.kind !== "dir") {
    out.files = 1;
    out.bytes = t.entry.size;
    return out;
  }
  const stack: Found[] = [t];
  while (stack.length > 0) {
    if (budget.entries <= 0 || late(ctx)) return { ...out, capped: true };
    const dir = stack.pop() as Found;
    await pace(ctx);
    let listing: { entries: { name: string; kind: EntryKind }[]; more: boolean };
    try {
      listing = await ctx.deps.fs.readdir(dir.path, budget.entries);
    } catch {
      return { ...out, capped: true };
    }
    // Right after the listing and before any child: the directory must still be the one checked before listing it.
    const again = await look(dir.path, ctx);
    if (again === null || again === REFUSED || again.kind !== "dir" || again.id !== dir.entry.id) return { ...out, capped: true, swapped: true };
    for (const { name, kind } of listing.entries) {
      if (--budget.entries < 0 || late(ctx)) return { ...out, capped: true };
      const path = P.join(dir.path, name);
      const st = await look(path, ctx);
      if (st === REFUSED) return { ...out, capped: true };
      if (st === null) continue; // gone meanwhile
      if (st.kind === "link" && kind !== "link") return { ...out, capped: true, swapped: true };
      if (st.kind === "dir") stack.push({ path, entry: st });
      else {
        out.files++;
        out.bytes += st.size;
      }
    }
    if (listing.more) return { ...out, capped: true };
  }
  return out;
}

/** The cwd is a home directory or a root: "outside the project" would mean nothing there (review M-4). */
function noProject(cwd: string, ctx: Ctx): boolean {
  const { P } = ctx;
  const fold = ctx.deps.platform === "win32" || ctx.deps.platform === "darwin" ? (x: string) => x.toLowerCase() : (x: string) => x;
  return P.dirname(cwd) === cwd || fold(P.resolve(cwd)) === fold(P.resolve(ctx.deps.homeDir));
}

/**
 * Delete or overwrite of named targets (fix round 1 A3, A4, A6, A7; round 2 A6, A7). The target sent is the path
 * that was examined, resolved from the request's cwd.
 */
async function targetsImpact(kind: "delete" | "overwrite", raw: string[], cwdText: string, ctx: Ctx): Promise<Impact> {
  const unknown: Impact = { kind, recoverable: "unknown", reason: R.ambiguous };
  // What can be refused from the text alone is refused before the first lstat.
  const platform = ctx.deps.platform;
  if (raw.length > MAX_TARGETS || !isLocalAbsolute(cwdText, platform) || networkPath(cwdText, platform) || raw.some((r) => refusedText(r, platform))) return unknown;
  const cwd = await realCwd(cwdText, ctx);
  if (cwd === null) return unknown;
  const found: Found[] = [];
  for (const r of raw) {
    const t = await findTarget(r, cwd, ctx);
    if (t === null || t.entry.kind === "other" || (kind === "overwrite" && t.entry.kind !== "file")) return unknown;
    found.push(t);
  }
  // A target inside another one is counted once.
  const P = ctx.P;
  const targets = found.filter((t, i) => !found.some((o, j) => j !== i && (o.path === t.path ? j < i : isUnder(t.path, o.path, P))));
  let git = false;
  let wholeRepo = false;
  for (const t of targets) {
    // A directory that holds a `.git` entry is a whole project, history included (round 2 A7).
    if (t.entry.kind === "dir") {
      const own = await look(P.join(t.path, ".git"), ctx);
      if (own === REFUSED) return unknown;
      if (own !== null) {
        wholeRepo = true;
        continue;
      }
    }
    const g = await inGit(P.dirname(t.path), ctx);
    if (g === null) return unknown;
    git ||= g;
  }
  const budget = { entries: MAX_WALK_ENTRIES };
  const sum: Count = { files: 0, bytes: 0, capped: false };
  for (const t of targets) {
    const c = await count(t, budget, ctx);
    sum.files += c.files;
    sum.bytes += c.bytes;
    if (c.capped) {
      sum.capped = true;
      sum.swapped = c.swapped;
      break;
    }
  }
  return {
    kind,
    target: targets.map((t) => t.path).join(", "),
    files: sum.files,
    ...(sum.capped ? { filesCapped: true } : {}),
    bytes: sum.bytes,
    ...(noProject(cwd.path, ctx) ? {} : { outsideProject: targets.some((t) => !isUnder(t.path, cwd.path, P)) }),
    recoverable: git || sum.swapped ? "unknown" : "no",
    reason: sum.swapped ? R.ambiguous : git ? R.inGit : wholeRepo ? R.wholeRepo : R.notInGit,
  };
}

// ---------------------------------------------------------------- scheduling

interface Job {
  run: (allowed: boolean) => Promise<void>;
  isOpen: () => boolean;
}

/**
 * The global bound on impact work (review I5): one computation at a time in the whole bridge, at most 8 waiting
 * (the oldest dropped), and at most `budgetMs` of computation per rolling `windowMs`. A job past the budget runs
 * with `allowed` false and must not touch the disk. A queued job whose request closed meanwhile is skipped.
 */
export class ImpactScheduler {
  private running = false;
  private readonly queue: Job[] = [];
  private readonly spent: { end: number; ms: number }[] = [];

  constructor(
    private readonly now: () => number = Date.now,
    private readonly maxQueued = 8,
    private readonly budgetMs = 2000,
    private readonly windowMs = 10_000,
    /** A job still running after this releases the slot (a hung lstat must not stop every later impact; M-8). */
    private readonly watchdogMs = 2000,
  ) {}

  /** Waiting jobs (not counting the running one). */
  get waiting(): number {
    return this.queue.length;
  }

  enqueue(run: (allowed: boolean) => Promise<void>, isOpen: () => boolean): void {
    this.queue.push({ run, isOpen });
    while (this.queue.length > this.maxQueued) this.queue.shift();
    if (!this.running) void this.drain();
  }

  private used(): number {
    const from = this.now() - this.windowMs;
    while (this.spent.length > 0 && this.spent[0].end <= from) this.spent.shift();
    return this.spent.reduce((s, x) => s + x.ms, 0);
  }

  private async drain(): Promise<void> {
    this.running = true;
    try {
      for (let job = this.queue.shift(); job !== undefined; job = this.queue.shift()) {
        // A full turn of the event loop between jobs: several short jobs never add up to one long block.
        await new Promise<void>((r) => setImmediate(r));
        if (!job.isOpen()) continue;
        const allowed = this.used() < this.budgetMs;
        const t0 = this.now();
        let timer: ReturnType<typeof setTimeout> | undefined;
        try {
          await Promise.race([job.run(allowed), new Promise<void>((r) => (timer = setTimeout(r, this.watchdogMs)))]);
        } catch {
          /* display only */
        } finally {
          clearTimeout(timer);
        }
        const ms = this.now() - t0;
        if (allowed && ms > 0) this.spent.push({ end: this.now(), ms });
      }
    } finally {
      this.running = false;
    }
  }
}

// ---------------------------------------------------------------- real dependencies

function kindOfStats(st: { isDirectory(): boolean; isFile(): boolean; isSymbolicLink(): boolean }): EntryKind {
  if (st.isSymbolicLink()) return "link";
  if (st.isDirectory()) return "dir";
  if (st.isFile()) return "file";
  return "other";
}

export const realFs: ImpactFs = {
  async lstat(path) {
    try {
      const st = await lstat(path, { bigint: true });
      return { kind: kindOfStats(st), size: Number(st.size), id: `${st.dev}:${st.ino}` };
    } catch (e) {
      const code = (e as NodeJS.ErrnoException).code;
      if (code === "ENOENT" || code === "ENOTDIR") return null;
      throw e;
    }
  },
  async readdir(path, max) {
    // opendir reads the directory in small batches, so a huge one is never loaded whole (review M4).
    const dir = await opendir(path, { bufferSize: 32 });
    const entries: { name: string; kind: EntryKind }[] = [];
    let more = false;
    for await (const e of dir) {
      if (entries.length >= max) {
        more = true;
        break; // closes the directory
      }
      entries.push({ name: e.name, kind: kindOfStats(e) });
    }
    return { entries, more };
  },
};

/** The live dependencies: `lstat` and directory listings only. */
export function realImpactDeps(homeDir: string): ImpactDeps {
  return { fs: realFs, now: Date.now, platform: process.platform, homeDir };
}
