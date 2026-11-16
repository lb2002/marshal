// Agent identity by rules: a short title from the first prompt, and the project from the cwd.
// The prompt text is only read here, once, and never kept: what leaves is the capped, redacted title.
import { redactSecrets, shortenAbsolutePaths } from "./anonymize.js";
import { cleanText } from "./explain.js";
export const MAX_TITLE = 40;
export const MAX_PROJECT = 30;
/** Only the start of the first line is looked at. */
const MAX_SCAN = 2000;
/** The first sentence of the first non-empty line: redacted, absolute paths shortened, one clean line, at most 40 characters. */
export function ruleTitle(prompt, homeDir) {
    if (typeof prompt !== "string")
        return undefined;
    const line = prompt
        .slice(0, MAX_SCAN * 4)
        .split(/\r\n|\r|\n|\u2028|\u2029/)
        .map((l) => l.trim())
        .find((l) => l !== "");
    if (line === undefined)
        return undefined;
    const safe = shortenAbsolutePaths(redactSecrets(line.slice(0, MAX_SCAN)), homeDir);
    // A sentence ends at . ! or ? followed by a blank or the end (so `cart.ts` does not end it).
    const sentence = /^.*?[.!?](?=\s|$)/.exec(safe)?.[0] ?? safe;
    const title = redactSecrets(cleanText(sentence.replace(/[.!?]+$/, ""), MAX_TITLE));
    return title === "" ? undefined : title;
}
/** Basename of the cwd, at most 30 characters; undefined for a bare root or a recording placeholder (`<repo>`, `~`). */
export function projectName(cwd) {
    if (typeof cwd !== "string" || cwd.startsWith("<") || cwd.startsWith("~"))
        return undefined;
    const last = cwd.split(/[\\/]+/).filter((s) => s !== "").at(-1);
    if (last === undefined || /^[A-Za-z]:$/.test(last))
        return undefined;
    const name = cleanText(last, MAX_PROJECT);
    return name === "" ? undefined : name;
}
