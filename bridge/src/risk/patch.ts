// Codex `apply_patch`: the whole patch arrives as `tool_input.command`. It is classified by its file
// headers, never by the shell rules. The parser fails closed: anything it does not fully understand is
// destructive, so a header it misses can never hide a delete or a write outside the project.

import { destructive, SAFE, worst } from "./common.js";
import type { RiskResult } from "./common.js";
import { toolPathRisk } from "./paths.js";
import type { PathEnv } from "./paths.js";

/** Same as the path slots the broker shows (MAX_PATHS): more files than the card can list is not trusted. */
export const MAX_PATCH_FILES = 5;

const UNCLEAR = destructive("Unclear patch");
const TOO_MANY = destructive("Patch changes too many files");
const DELETES = destructive("Patch deletes a file");

// Matched against a trimmed line. Whitespace between the words is flexible; the words themselves are not.
const MARKER = /^\*\*\*\s*(?:Begin\s+Patch|End\s+Patch|End\s+of\s+File)$/;
const FILE = /^\*\*\*\s*(Add|Update|Delete)\s+File\s*:\s*(\S.*)$/;
const MOVE = /^\*\*\*\s*Move\s+to\s*:\s*(\S.*)$/;

/** Codex sends the patch as the only input field; anything else is a shape Lotse does not know. */
export function classifyApplyPatch(input: Record<string, unknown>, cwd: string, env?: PathEnv): RiskResult {
  if (Object.keys(input).some((k) => k !== "command")) return UNCLEAR;
  const text = input.command;
  if (typeof text !== "string") return UNCLEAR;
  const paths: string[] = [];
  let deletes = false;
  for (const raw of text.split(/\r\n|\r|\n/)) {
    const line = raw.trim();
    if (!line.startsWith("***")) continue;
    if (MARKER.test(line)) continue;
    const file = FILE.exec(line);
    const move = file ? null : MOVE.exec(line);
    if (!file && !move) return UNCLEAR;
    if (file?.[1] === "Delete") deletes = true;
    paths.push((file ? file[2] : move![1]).trim());
  }
  if (paths.length === 0) return UNCLEAR;
  if (paths.length > MAX_PATCH_FILES) return TOO_MANY;
  let result: RiskResult = deletes ? DELETES : SAFE;
  for (const p of paths) result = worst(result, toolPathRisk(p, cwd, env) ?? SAFE);
  return result;
}
