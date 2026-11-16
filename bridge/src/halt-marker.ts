// The halt marker `~/.lotse/halt` (rulings task-p2-fix-1.md B2): while halted, the bridge keeps its process id and
// start time there, so a hook whose bridge is alive but too slow to answer still refuses. The hook looks at it only
// after a timeout (never on the normal path, never on connection refused) and only believes it when the process
// it names is running, so a crashed bridge never blocks an agent.
//
// The marker is a convenience for cooperative agents, not a barrier: an agent that can write files or run commands
// can delete it. Reading it never follows a link (the marker or `~/.lotse` itself), so a planted link cannot make a
// hook reach another path or a network share.

import { lstatSync, mkdirSync, readFileSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const MAX_MARKER = 256;

export function haltMarkerPath(home: string): string {
  return join(home, ".lotse", "halt");
}

interface MarkerInfo {
  pid: number;
  start: number;
}

/** The marker's contents when it is a small regular file in a real directory with a valid pid; else null. */
function readMarker(file: string): MarkerInfo | null {
  try {
    if (!lstatSync(dirname(file)).isDirectory()) return null; // a link or junction is not a directory to lstat
    const st = lstatSync(file);
    if (!st.isFile() || st.size > MAX_MARKER) return null;
    const v: unknown = JSON.parse(readFileSync(file, "utf8"));
    if (typeof v !== "object" || v === null || Array.isArray(v)) return null;
    const { pid, start } = v as Record<string, unknown>;
    if (typeof pid !== "number" || !Number.isSafeInteger(pid) || pid <= 0) return null;
    return { pid, start: typeof start === "number" ? start : 0 };
  } catch {
    return null;
  }
}

/** Whether a process with this id runs (EPERM: it runs, as another user). */
export function pidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return (e as NodeJS.ErrnoException).code === "EPERM";
  }
}

/** True when the marker exists, is well-formed and names a running process. Never throws. */
export function haltMarkerLive(file: string, alive: (pid: number) => boolean = pidAlive): boolean {
  const m = readMarker(file);
  return m !== null && alive(m.pid);
}

/**
 * Writes (or overwrites) the marker for this process. Never throws; false when it could not be written. Never
 * writes through a link (review 2 M-2): `~/.lotse` must be a real directory, and the temporary file is created
 * new (`wx`, which fails on any existing name, a link included) and then renamed over the marker's name.
 */
export function writeHaltMarker(file: string, start: number): boolean {
  try {
    const dir = dirname(file);
    try {
      if (!lstatSync(dir).isDirectory()) return false;
    } catch {
      mkdirSync(dir);
    }
    const tmp = `${file}.${process.pid}.tmp`;
    try {
      unlinkSync(tmp);
    } catch {
      /* not there */
    }
    writeFileSync(tmp, JSON.stringify({ pid: process.pid, start }), { flag: "wx" });
    renameSync(tmp, file);
    return true;
  } catch {
    return false;
  }
}

/**
 * Removes the marker when it is this process's (`mine`), when the process it names is gone (`stale`), or whatever
 * it says (`any`: a bridge that is not halted removes every marker, a planted one too; review 2 M-1, ruling C).
 */
export function removeHaltMarker(file: string, which: "mine" | "stale" | "any", alive: (pid: number) => boolean = pidAlive): void {
  try {
    const m = lstatSync(file).isFile() ? readMarker(file) : null;
    const remove = which === "any" || (which === "mine" ? m !== null && m.pid === process.pid : m === null || !alive(m.pid));
    if (remove) unlinkSync(file);
  } catch {
    /* missing, or not ours to remove */
  }
}
