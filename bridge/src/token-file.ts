// The hook token file, `~/.lotse/token`. Light on purpose: the hook path reads it on every call.

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export function tokenFile(): string {
  return join(homedir(), ".lotse", "token");
}

/** The token in the file, trimmed; null when missing or empty. */
export function readTokenFile(): string | null {
  try {
    const t = readFileSync(tokenFile(), "utf8").trim();
    return t === "" ? null : t;
  } catch {
    return null;
  }
}

/** What a hook sends: LOTSE_TOKEN (trimmed, empty counts as unset), else the file. */
export function hookToken(env: NodeJS.ProcessEnv = process.env): string | null {
  return env.LOTSE_TOKEN?.trim() || readTokenFile();
}
