// Deterministic risk classifier for Claude Code tool calls.
// Inputs are untrusted (a prompt-injected agent may craft them), so every
// ambiguity resolves towards the higher risk class. The classifier decides the
// approval COST (pinch vs two-hand), not whether approval is needed: it is a UX
// layer on top of Claude Code's own permission system, not a sandbox.
// No regex in the classifier may backtrack super-linearly.
import { NETWORK, SAFE, destructive, worst } from "./risk/common.js";
import { isInside, isInsideReal, pathEnv, toolPathRisk } from "./risk/paths.js";
import { classifyShellArgv, classifyShellCommand } from "./risk/shell.js";
import { argvMinLength, isStringArray, renderArgv } from "./argv.js";
import { classifyApplyPatch } from "./risk/patch.js";
export { isInside, isInsideReal };
/** Claude Code built-ins with a fixed tier (lowercase names). */
const FIXED_TIER = {
    read: SAFE, glob: SAFE, grep: SAFE, ls: SAFE, notebookread: SAFE,
    skill: SAFE, task: SAFE, agent: SAFE, todowrite: SAFE, exitplanmode: SAFE, askuserquestion: SAFE, slashcommand: SAFE,
    bashoutput: SAFE, taskoutput: SAFE, killshell: SAFE, killbash: SAFE, // stop the agent's own background shell
    webfetch: NETWORK, websearch: NETWORK,
};
const SHELL_TOOLS = { bash: "bash", powershell: "pwsh" };
const WRITE_PATH_FIELD = {
    write: "file_path",
    edit: "file_path",
    multiedit: "file_path",
    notebookedit: "notebook_path",
};
const PATH_FIELDS = ["file_path", "notebook_path", "path", "destination", "dest", "target", "output", "to", "outputPath"];
const DANGER_WORDS = new Set([
    "delete", "remove", "drop", "destroy", "purge", "kill", "reset", "force", "uninstall", "unpublish", "wipe", "truncate",
    "rm", "rmdir", "unlink", "erase",
]);
/** Name segments split on `_ - . :` and camelCase boundaries, lowercase. */
function nameSegments(tool) {
    const out = [];
    let cur = "";
    const isUpper = (c) => c >= "A" && c <= "Z";
    const isLower = (c) => c >= "a" && c <= "z";
    for (let i = 0; i < tool.length; i++) {
        const c = tool[i];
        if (c === "_" || c === "-" || c === "." || c === ":" || c === " " || c === "/") {
            if (cur)
                out.push(cur.toLowerCase());
            cur = "";
            continue;
        }
        const prev = tool[i - 1] ?? "";
        const next = tool[i + 1] ?? "";
        if (cur && isUpper(c) && (isLower(prev) || (isUpper(prev) && isLower(next)))) {
            out.push(cur.toLowerCase());
            cur = "";
        }
        cur += c;
    }
    if (cur)
        out.push(cur.toLowerCase());
    return out;
}
/** The `command` field: a line, or an argument array kept as its elements. */
function commandOf(input) {
    const c = input.command;
    if (typeof c === "string" || isStringArray(c))
        return c;
    return null;
}
const UNKNOWN_TOOL = destructive("Unclear tool");
/**
 * Longest command (an argument array counted as rendered, argv.ts) and longest other string field that is
 * classified at all. Anything longer is not lexed: destructive with a fixed reason, which also means the two-hand
 * hold and a cut display. Chosen so that the slowest measured shape just under it classifies in under 100 ms.
 */
export const MAX_CLASSIFIED = 16 * 1024;
const TOO_LONG = destructive("Command too long, not checked");
/** A top-level string field, or a string array as rendered, longer than MAX_CLASSIFIED. Reads no long text. */
function oversized(tool, input) {
    if (tool.length > MAX_CLASSIFIED)
        return true;
    for (const k of Object.keys(input)) {
        const v = input[k];
        if (typeof v === "string") {
            if (v.length > MAX_CLASSIFIED)
                return true;
        }
        else if (Array.isArray(v) && v.length > MAX_CLASSIFIED) {
            return true; // more elements than characters allowed, whatever they are (decided without reading them)
        }
        else if (isStringArray(v)) {
            // Rendering never shortens, so the cheap lower bound decides first; only a short array is rendered.
            if (argvMinLength(v) > MAX_CLASSIFIED || renderArgv(v).length > MAX_CLASSIFIED)
                return true;
        }
    }
    return false;
}
/**
 * Set by the hook shim when it mapped an alias field (Codex `cmd` -> `command`, Gemini `absolute_path` ->
 * `file_path`) and the payload also carried the other field with a different value. The field the tool really
 * runs is the one shown and classified; the conflict itself makes the call unclear.
 */
export const ALIAS_CONFLICT = "lotse_alias_conflict";
/**
 * The same command, as a string or an argument array. An array counts as its rendering (argv.ts), never as its
 * elements joined by spaces: `["rm","a b"]` and `rm a b` differ.
 */
export function sameCommand(a, b) {
    const text = (v) => (typeof v === "string" ? v : isStringArray(v) ? renderArgv(v) : null);
    const x = text(a);
    return x !== null && x === text(b);
}
/**
 * Characters that make a field read differently from what runs: controls other than `\n` and `\t` (also CR,
 * which PowerShell reads as a line break), bidi controls, zero-width and invisible format characters, the Unicode
 * tag block.
 */
const HIDDEN = /[\u{0}-\u{8}\u{b}-\u{1f}\u{7f}-\u{9f}\u{61c}\u{200e}\u{200f}\u{202a}-\u{202e}\u{2066}-\u{2069}\u{200b}-\u{200d}\u{2060}\u{feff}\u{ad}\u{180e}\u{e0000}-\u{e007f}]/u;
/** In a command also the blanks PowerShell 5.1 splits words at and the lexer does not (tested; see README). */
const HIDDEN_IN_COMMAND = new RegExp(HIDDEN.source.slice(0, -1) + "\\u{a0}\\u{1680}\\u{2000}-\\u{200a}\\u{2028}\\u{2029}\\u{202f}\\u{205f}\\u{3000}]", "u");
const INVISIBLE_CHARS = destructive("Command contains invisible or control characters");
/** The tool name, the command (string or array elements, also Codex `cmd`), a path field or the URL hides characters. */
function hiddenCharacters(tool, input) {
    const has = (v, re) => (typeof v === "string" ? re.test(v) : isStringArray(v) && v.some((x) => re.test(x)));
    if (HIDDEN.test(tool) || has(input.command, HIDDEN_IN_COMMAND) || has(input.cmd, HIDDEN_IN_COMMAND) || has(input.url, HIDDEN))
        return true;
    return PATH_FIELDS.some((f) => has(input[f], HIDDEN));
}
function classify(tool, input, cwd, ctx) {
    if (oversized(tool, input))
        return TOO_LONG; // before anything else reads the text
    if (hiddenCharacters(tool, input))
        return INVISIBLE_CHARS;
    const name = tool.toLowerCase();
    if (Object.hasOwn(input, ALIAS_CONFLICT))
        return UNKNOWN_TOOL;
    // Codex exec_command runs `cmd`. Without it, or with a `command` that says something else, it is unclear.
    if (ctx.source === "codex" && name === "exec_command") {
        if (input.cmd === undefined || (input.command !== undefined && !sameCommand(input.cmd, input.command)))
            return UNKNOWN_TOOL;
        input = { ...input, command: input.cmd };
    }
    if (Object.hasOwn(FIXED_TIER, name))
        return FIXED_TIER[name];
    // Codex apply_patch: its `command` is the patch, read by its file headers, never by the shell rules. The
    // generic name and path checks still run (worst of both); any other tool named apply_patch is generic.
    if (ctx.source === "codex" && name === "apply_patch")
        return worst(classifyApplyPatch(input, cwd, ctx.paths), generic(tool, input, cwd, false, ctx));
    return generic(tool, input, cwd, true, ctx);
}
/**
 * The shell rules for a command. Claude names its dialect (Bash, PowerShell). The other tools on Windows run
 * their shell in PowerShell even when the tool is called Bash (Muse's hook-compat name, Codex), so there a
 * command gets the worst of the Bash and the PowerShell rules.
 */
function shellRisk(command, cwd, name, ctx) {
    const dialect = SHELL_TOOLS[name] ?? "bash";
    const own = classifyShellCommand(command, cwd, dialect, process.env, ctx.paths);
    if (ctx.source === undefined || ctx.source === "claude" || ctx.platform !== "win32" || own.risk === "destructive")
        return own;
    return worst(own, classifyShellCommand(command, cwd, dialect === "bash" ? "pwsh" : "bash", process.env, ctx.paths));
}
/**
 * An argument array: element 0 is the command word, every other element one literal argument (a shell wrapper's
 * command string is read by that shell's rules). Never below the elements joined by spaces and read as a line,
 * because a harness may run the array that way: the worse of the two.
 */
function argvRisk(argv, cwd, name, ctx) {
    // Same dialect choice as shellRisk: a non-Claude agent on Windows may run either shell, so the element reading
    // is made in both dialects too (the dialect reaches eval/trap payloads, `$` command words, call operators). A
    // shell wrapper in element 0 names the dialect of its own command string itself. Nothing ranks above
    // destructive, so later readings are skipped then.
    const dialect = SHELL_TOOLS[name] ?? "bash";
    let own = classifyShellArgv(argv, cwd, dialect, process.env, ctx.paths);
    if (own.risk === "destructive")
        return own;
    if (!(ctx.source === undefined || ctx.source === "claude" || ctx.platform !== "win32")) {
        own = worst(own, classifyShellArgv(argv, cwd, dialect === "bash" ? "pwsh" : "bash", process.env, ctx.paths));
        if (own.risk === "destructive")
            return own;
    }
    return worst(own, shellRisk(argv.join(" "), cwd, name, ctx));
}
/**
 * The tool-name, command and path-field checks; `shellCommand` false skips the shell rules for `command`
 * (the Codex patch). For Muse and Codex, an unknown tool with neither a command nor a path field is unclear.
 */
function generic(tool, input, cwd, shellCommand, ctx) {
    const name = tool.toLowerCase();
    const known = name in SHELL_TOOLS || name in WRITE_PATH_FIELD;
    let result = SAFE;
    if (!known && nameSegments(tool).some((s) => DANGER_WORDS.has(s)))
        result = destructive("Tool deletes or resets");
    const command = shellCommand ? commandOf(input) : null;
    if (command !== null)
        result = worst(result, typeof command === "string" ? shellRisk(command, cwd, name, ctx) : argvRisk(command, cwd, name, ctx));
    const anyPath = PATH_FIELDS.some((f) => typeof input[f] === "string");
    if (shellCommand && !known && command === null && !anyPath && (ctx.source === "muse" || ctx.source === "codex")) {
        result = worst(result, UNKNOWN_TOOL);
    }
    const knownField = WRITE_PATH_FIELD[name];
    if (knownField) {
        // A writing tool whose target cannot be determined is not trusted.
        return worst(result, toolPathRisk(input[knownField], cwd, ctx.paths) ?? SAFE);
    }
    for (const field of PATH_FIELDS) {
        const p = input[field];
        if (typeof p === "string")
            result = worst(result, toolPathRisk(p, cwd, ctx.paths) ?? SAFE);
    }
    return result;
}
/**
 * `source` is the coding agent behind the call (only Codex has the apply_patch tool; Muse and Codex get the
 * unknown-tool rule; non-Claude shells on win32 get both dialects). `platform` is where the agent runs; paths are validated by its
 * rules (local-path.ts). `fs`: the lstat the path checks use (tests inject one; default the real one).
 */
export function classifyRisk(tool, input, cwd, source, platform = process.platform, fs) {
    try {
        const paths = pathEnv(platform, fs);
        return classify(String(tool ?? ""), input && typeof input === "object" ? input : {}, typeof cwd === "string" ? cwd : "", { source, platform, paths });
    }
    catch {
        return destructive("Check failed");
    }
}
