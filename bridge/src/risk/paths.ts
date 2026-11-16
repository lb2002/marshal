// Paths for the risk classifier. Whether a path leaves the project is decided lexically from the cwd and the raw
// path. The file system is asked only one thing: whether a component below the cwd is a link (lstat, never
// followed), and only for a path that passed the same local-path validation as the impact check (local-path.ts).
// A path that cannot be decided (a link on the way, a network location, an unvalidated cwd or path, a failed
// lookup, the lookup budget spent) counts as outside the project, the more severe reading.

import { lstatSync } from "node:fs";
import { isLocalAbsolute, networkPath, winComponentOk } from "../local-path.js";
import { Ctx, OUTSIDE, PROTECTED, RiskResult } from "./common.js";

interface ParsedPath {
  root: string; // "" relative, "/" posix, "c:" drive, "//srv/share" UNC
  segs: string[];
  foldCase: boolean;
}

const isAlpha = (c: string | undefined) => c !== undefined && ((c >= "a" && c <= "z") || (c >= "A" && c <= "Z"));

function parsePath(p: string): ParsedPath | null {
  let s = p.replace(/\\/g, "/");
  let root = "";
  let foldCase = false;
  if (isAlpha(s[0]) && s[1] === ":") {
    if (s.length > 2 && s[2] !== "/") return null; // drive-relative: unknowable
    root = s[0].toLowerCase() + ":";
    s = s.slice(2);
    foldCase = true;
  } else if (s.startsWith("//")) {
    const m = /^\/\/+([^/]+)\/+([^/]+)/.exec(s);
    if (!m) return null;
    root = `//${m[1].toLowerCase()}/${m[2].toLowerCase()}`;
    s = s.slice(m[0].length);
    foldCase = true;
  } else if (s.startsWith("/")) {
    root = "/";
  }
  return { root, segs: s.split("/").filter((x) => x !== "" && x !== "."), foldCase };
}

/**
 * Windows name normalisation of one segment: drop an alternate data stream
 * suffix (`name:stream`) and trailing dots and spaces. Returns null when nothing
 * meaningful is left (the name cannot be known).
 */
export function normalizeWinSegment(seg: string): string | null {
  if (seg === "." || seg === "..") return seg;
  const colon = seg.indexOf(":");
  let s = colon >= 0 ? seg.slice(0, colon) : seg;
  let end = s.length;
  while (end > 0 && (s[end - 1] === "." || s[end - 1] === " ")) end--;
  if (end === s.length) return s === "" ? null : s;
  s = s.slice(0, end);
  if (s === "") return seg.startsWith("..") ? ".." : null; // "..." or ".. " behaves like a parent reference
  return s;
}

function resolveSegs(segs: string[]): string[] {
  const out: string[] = [];
  for (const s of segs) {
    if (s === "..") out.pop();
    else out.push(s);
  }
  return out;
}

function winNormalize(segs: string[]): string[] | null {
  const out: string[] = [];
  for (const s of segs) {
    const n = normalizeWinSegment(s);
    if (n === null) return null;
    out.push(n);
  }
  return out;
}

/** Segments of `child` below `parent`, or null when it is not inside. Purely lexical. */
export function relInside(child: string, parent: string): string[] | null {
  if (typeof child !== "string" || typeof parent !== "string") return null;
  if (/^[~$%]/.test(child) || child.includes("\0")) return null;
  const par = parsePath(parent);
  const chi = parsePath(child);
  if (!par || !chi || par.root === "") return null; // parent must be absolute
  const fold = par.foldCase || chi.foldCase;
  const parRaw = fold ? winNormalize(par.segs) : par.segs;
  const chiRaw = fold ? winNormalize(chi.segs) : chi.segs;
  if (!parRaw || !chiRaw) return null;
  const parSegs = resolveSegs(parRaw);
  let chiSegs: string[];
  if (chi.root === "") {
    chiSegs = resolveSegs([...parSegs, ...chiRaw]);
  } else {
    if (chi.root !== par.root) return null;
    chiSegs = resolveSegs(chiRaw);
  }
  const norm = (x: string) => (par.foldCase ? x.toLowerCase() : x);
  if (chiSegs.length < parSegs.length) return null;
  if (!parSegs.every((seg, i) => norm(seg) === norm(chiSegs[i]))) return null;
  return chiSegs.slice(parSegs.length);
}

/**
 * Canonical lexical form of a path, for equality checks. Same rules as isInside
 * (separators, Windows case folding and name rules, `.`/`..`). Unparseable paths
 * fall back to the raw string with backslashes turned into `/`.
 */
export function normalizePath(p: string): string {
  const raw = p.replace(/\\/g, "/");
  const parsed = parsePath(p);
  if (!parsed) return raw;
  const segs = parsed.foldCase ? winNormalize(parsed.segs) : parsed.segs;
  if (!segs) return raw;
  const out = resolveSegs(segs).map((x) => (parsed.foldCase ? x.toLowerCase() : x));
  return parsed.root === "/" ? "/" + out.join("/") : [parsed.root, ...out].filter((x) => x !== "").join("/");
}

/** True when `child` is `parent` or lies below it (lexical). Relative children resolve against `parent`. */
export function isInside(child: string, parent: string): boolean {
  return relInside(child, parent) !== null;
}

export type PathKind = "dir" | "file" | "link" | "other";

/** The one file-system call the classifier makes: lstat (never follows a link). null = does not exist. */
export interface PathFs {
  lstat(path: string): PathKind | null;
}

export const realPathFs: PathFs = {
  lstat(p: string): PathKind | null {
    let st;
    try {
      st = lstatSync(p, { throwIfNoEntry: false });
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code === "ENOTDIR") return null; // a file on the way: nothing below exists
      throw e;
    }
    if (st === undefined) return null;
    return st.isSymbolicLink() ? "link" : st.isDirectory() ? "dir" : st.isFile() ? "file" : "other";
  },
};

/** Most lstat calls for one request; beyond them a path is not decided (outside). */
export const MAX_LOOKUPS = 128;

/** Platform, file system, remaining budget and answers of one classification (shared by all its paths). */
export interface PathEnv {
  platform: NodeJS.Platform;
  fs: PathFs;
  left: number;
  seen: Map<string, PathKind | null>;
}

export function pathEnv(platform: NodeJS.Platform = process.platform, fs: PathFs = realPathFs): PathEnv {
  return { platform, fs, left: MAX_LOOKUPS, seen: new Map() };
}

const UNDECIDED: unique symbol = Symbol("undecided");

/** lstat of `p`, validated first: never for a path that is not local or lies in a network location. */
function lookup(p: string, env: PathEnv): PathKind | null | typeof UNDECIDED {
  if (!isLocalAbsolute(p, env.platform) || networkPath(p, env.platform)) return UNDECIDED;
  if (env.seen.has(p)) return env.seen.get(p) ?? null;
  if (env.left <= 0) return UNDECIDED;
  env.left--;
  let kind: PathKind | null;
  try {
    kind = env.fs.lstat(p);
  } catch {
    return UNDECIDED; // permission denied and the like: cannot be decided
  }
  env.seen.set(p, kind);
  return kind;
}

interface LocalPath {
  base: string; // "" (posix root) or "C:"
  segs: string[]; // components, `.` and empty ones dropped; `..` kept
}

/** A validated local absolute path split into its components, or null (local-path.ts rules). */
function splitLocal(p: string, platform: NodeJS.Platform): LocalPath | null {
  if (!isLocalAbsolute(p, platform)) return null;
  const keep = (x: string) => x !== "" && x !== ".";
  if (platform === "win32") return { base: p.slice(0, 2).toUpperCase(), segs: p.slice(3).split(/[\\/]/).filter(keep) };
  return { base: "", segs: p.slice(1).split("/").filter(keep) };
}

const joinLocal = (base: string, segs: string[], platform: NodeJS.Platform) =>
  platform === "win32" ? base + "\\" + segs.join("\\") : "/" + segs.join("/");

/**
 * Whether writing to `target` (absolute, lexically inside `root`) could leave the project through a link: null
 * when it cannot, OUTSIDE when it cannot be decided. The cwd must be a validated local path without `.`/`..`; it is
 * not looked at itself. Each component below it is checked with lstat in order: a link (also as the last
 * component) is outside, a missing part or a file ends the walk (nothing below it can be a link; the write fails
 * or creates a plain file). POSIX takes `..` physically, after the part before it was found to be a real
 * directory; Windows folds `..` as written, as its path APIs do.
 */
function linkRisk(target: string, root: string, env: PathEnv): RiskResult | null {
  const { platform } = env;
  const r = splitLocal(root, platform);
  if (r === null || r.segs.includes("..") || networkPath(joinLocal(r.base, r.segs, platform), platform)) return OUTSIDE;
  const t = splitLocal(target, platform);
  if (t === null || t.base.toLowerCase() !== r.base.toLowerCase()) return OUTSIDE;
  const win = platform === "win32";
  const segs = win ? resolveSegs(t.segs) : t.segs;
  if (win && !segs.every(winComponentOk)) return OUTSIDE;
  if (networkPath(joinLocal(t.base, resolveSegs(t.segs), platform), platform)) return OUTSIDE;
  const same = (a: string, b: string) => (win ? a.toLowerCase() === b.toLowerCase() : a === b);
  for (let i = 0; i < r.segs.length; i++) if (segs[i] === undefined || segs[i] === ".." || !same(segs[i], r.segs[i])) return OUTSIDE;
  const stack = [...r.segs];
  for (const seg of segs.slice(r.segs.length)) {
    if (seg === "..") {
      if (stack.length <= r.segs.length) return OUTSIDE;
      stack.pop();
      continue;
    }
    stack.push(seg);
    const kind = lookup(joinLocal(r.base, stack, platform), env);
    if (kind === UNDECIDED || kind === "link") return OUTSIDE;
    if (kind !== "dir") return null;
  }
  return null;
}

const ABSOLUTE_RE = /^(?:[A-Za-z]:)?[\\/]|^\\\\/;

/** Inside `parent` lexically, with no link on the way below it (never followed; see linkRisk). */
export function isInsideReal(child: string, parent: string, env: PathEnv = pathEnv()): boolean {
  if (!isInside(child, parent)) return false;
  return linkRisk(ABSOLUTE_RE.test(child) ? child : `${parent}/${child}`, parent, env) === null;
}

/** Resolve a path argument for containment checks; null when it cannot be known. */
export function resolveFor(p: string, ctx: Ctx): string | null {
  if (ABSOLUTE_RE.test(p)) return p;
  if (/^[~$%]/.test(p) || ctx.cur === null) return null;
  return `${ctx.cur}/${p}`;
}

/** Folders of the agents' and Marshal's own settings and state: writing below any of them is a settings change. */
const AGENT_DIRS = new Set([".claude", ".codex", ".gemini", ".muse", ".opencode", ".lotse"]);

/**
 * A Windows 8.3 short name (`CLAUDE~1`, `SETTIN~1.JSO`): it can stand for any long name, a protected one included,
 * and it is not resolved here (no file-system call), so it counts as protected.
 */
const SHORT_NAME = /~\d+(?:\.[^.]*)?$/i;

/**
 * `.claude/**` (and the other agents' folders, `.config/muse/**`, Marshal's `.lotse/**`), `.git/hooks/**`,
 * `.git/config` below the project root, and any short-name segment. Windows name rules applied to every segment:
 * case folded, an alternate data stream suffix (`:name`) and trailing dots and spaces dropped.
 */
export function isProtectedSegs(segs: string[]): boolean {
  const s = segs.map((x) => (normalizeWinSegment(x) ?? x).toLowerCase());
  for (let i = 0; i < s.length; i++) {
    if (SHORT_NAME.test(s[i])) return true;
    if (AGENT_DIRS.has(s[i])) return true;
    if (s[i] === ".config" && s[i + 1] === "muse") return true;
    if (s[i] === ".git" && (s[i + 1] === "hooks" || (s[i + 1] === "config" && i + 2 === s.length))) return true;
  }
  return false;
}

/**
 * Risk of writing to `p` as seen from `ctx`, or null when the write is harmless. Decided lexically; a link below
 * the cwd, a network location or an unvalidated path counts as outside (linkRisk). Never follows a link.
 */
export function writeTargetRisk(p: string, ctx: Ctx): RiskResult | null {
  const r = resolveFor(p, ctx);
  if (r === null) return OUTSIDE;
  const rel = relInside(r, ctx.root);
  if (rel === null) return OUTSIDE;
  if (isProtectedSegs(rel)) return PROTECTED;
  return linkRisk(r, ctx.root, ctx.paths ?? pathEnv());
}

/** Same for the Write/Edit tools, whose path must be a non-empty string. */
export function toolPathRisk(p: unknown, root: string, paths?: PathEnv): RiskResult | null {
  if (typeof p !== "string" || p === "") return OUTSIDE;
  return writeTargetRisk(p, { root, cur: root, paths });
}
