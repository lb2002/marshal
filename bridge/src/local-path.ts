// Which paths a file-system call may be made for at all. Shared by the impact check (impact.ts) and the risk
// classifier (risk/paths.ts): no UNC, device or drive-relative path, no control characters, no Windows name the
// shells may read differently, and none of the platform's network or automount locations. Pure string checks.

/** Longest path that is looked at. */
export const MAX_PATH_LEN = 4096;

export const CONTROL = /[\u0000-\u001f\u007f-\u009f]/;
const RESERVED = /^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³]|conin\$|conout\$)(\..*)?$/i;

/**
 * A local absolute path: on Windows a drive path (`C:\…`), elsewhere `/…`. Never UNC (`\\host\share`,
 * `//host/share`), device (`\\?\`, `\\.\`), drive-relative (`C:foo`), with control characters, an alternate data
 * stream, a reserved device name, or longer than 4096 characters.
 */
export function isLocalAbsolute(p: string, platform: NodeJS.Platform): boolean {
  if (typeof p !== "string" || p.length === 0 || p.length > MAX_PATH_LEN || CONTROL.test(p)) return false;
  if (/^[\\/]{2}/.test(p)) return false;
  if (platform === "win32") {
    if (!/^[A-Za-z]:[\\/]/.test(p)) return false;
    return p.slice(3).split(/[\\/]+/).filter(Boolean).every(winComponentOk);
  }
  return p.startsWith("/");
}

/** A Windows name the shells cannot read differently: no stream, no reserved name, no trailing dot or space. */
export function winComponentOk(s: string): boolean {
  if (s === "." || s === "..") return true;
  return !s.includes(":") && !/[ .]$/.test(s) && !RESERVED.test(s.replace(/[ .]+$/, ""));
}

/**
 * Locations where an `lstat` can mount something or reach another machine (review I4): macOS `/net` and `/home`
 * (autofs), `/Network`, `/Volumes` (also through `/System/Volumes/Data`); Linux and others `/net`, `/mnt`,
 * `/media`, `/run/user/<id>/gvfs`. macOS compares case-insensitively (APFS usually is).
 */
export function networkPath(p: string, platform: NodeJS.Platform): boolean {
  if (platform === "win32") return false;
  const under = (s: string, prefix: string) => s === prefix || s.startsWith(prefix + "/");
  if (platform === "darwin") {
    const s = p.toLowerCase();
    const roots = ["/net", "/network", "/volumes", "/home"];
    return roots.some((r) => under(s, r) || under(s, "/system/volumes/data" + r));
  }
  return ["/net", "/mnt", "/media"].some((r) => under(p, r)) || /^\/run\/user\/[^/]+\/gvfs(\/|$)/.test(p);
}
