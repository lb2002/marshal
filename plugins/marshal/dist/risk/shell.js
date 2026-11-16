// Shell command classification on top of the quote-aware lexer: wrappers,
// shells, interpreters, PowerShell, cmd.exe, redirections, substitutions and
// download-then-execute chains. Every ambiguity resolves towards the higher class.
import { lex, LexError } from "./lexer.js";
import { COMPLEX, DOWNLOADED_CODE, INLINE_CODE, NESTED, OBFUSCATED, OUTSIDE, RISKY_ENV_SET, SAFE, UNCLEAR_SHELL, destructive, worst, } from "./common.js";
import { ASSIGN_RE, DOWNLOADERS, NULL_SINKS, classifyNamed, commandName, downloadInfo, riskyEnvName, sqlRisk, texts, writeAny, } from "./commands.js";
import { classifyGit } from "./git.js";
import { marshalSelfRisk } from "./self.js";
import { pathEnv, resolveFor, writeTargetRisk } from "./paths.js";
const MAX_DEPTH = 6;
const NO_STDIN = { piped: false, redirected: false };
const PIPED = { piped: true, redirected: false };
const stdinConnected = (env) => env.stdin.piped || env.stdin.redirected;
const stdinReason = (env) => (env.stdin.piped ? DOWNLOADED_CODE : COMPLEX);
// ---------------------------------------------------------------- tables
/** Words that run the next word as a command; value = short options taking an argument. */
const WRAPPERS = {
    sudo: "ugChprtUDRT", doas: "uC", env: "uCSP", nice: "n", ionice: "cnp", nohup: "", time: "fo", command: "",
    builtin: "", exec: "a", coproc: "", setsid: "", stdbuf: "ioe", timeout: "ks", xargs: "INPLdEsa", watch: "nd", caffeinate: "tw",
    pkexec: "u", chroot: "", flock: "wE", busybox: "", toybox: "", wsl: "du", parallel: "jSnNI", unshare: "",
    nsenter: "tSG", taskset: "", chrt: "", strace: "eospEu", ltrace: "eosp", fakeroot: "", runuser: "ugs", setpriv: "",
    then: "", do: "", else: "", elif: "", if: "", while: "", until: "", "!": "", "{": "", "}": "", "(": "", ")": "", "&": "",
};
/** Leading positional arguments a wrapper consumes before the command (numeric-only ones marked). */
const POSITIONAL = {
    timeout: { count: 1, numericOnly: false },
    chroot: { count: 1, numericOnly: false },
    flock: { count: 1, numericOnly: false },
    taskset: { count: 1, numericOnly: false },
    chrt: { count: 1, numericOnly: false },
    nice: { count: 1, numericOnly: true },
    runuser: { count: 1, numericOnly: false }, // user name, unless -u is given
    watch: { count: 1, numericOnly: true },
};
const LONG_WITH_VALUE = {
    sudo: new Set(["--user", "--group", "--host", "--prompt", "--role", "--type", "--chdir", "--close-from", "--other-user", "--command-timeout", "--chroot"]),
    env: new Set(["--unset", "--chdir", "--split-string"]),
    wsl: new Set(["--distribution", "--user", "--cd", "--shell-type"]),
    timeout: new Set(["--signal", "--kill-after"]),
    xargs: new Set(["--arg-file", "--delimiter", "--max-args", "--max-procs", "--max-lines", "--max-chars"]),
    nice: new Set(["--adjustment"]),
    time: new Set(["--format", "--output"]),
    flock: new Set(["--timeout", "--conflict-exit-code"]),
    chroot: new Set(["--userspec", "--groups"]),
    runuser: new Set(["--user", "--group", "--shell"]),
};
const CHDIR_SHORT = { sudo: "D", env: "C" };
const CHDIR_LONG = { sudo: "--chdir", env: "--chdir", wsl: "--cd" };
/** Wrappers whose `-c` / `--command` value is a shell command string. */
const PAYLOAD_SHORT = { flock: "c", runuser: "c" };
const SHELLS = new Set(["sh", "bash", "zsh", "dash", "ksh", "mksh", "fish", "csh", "tcsh", "ash", "yash", "su", "script"]);
const STDIN_PATHS = new Set(["-", "/dev/stdin", "/dev/fd/0", "/proc/self/fd/0"]);
const CD = new Set(["cd", "pushd", "chdir", "set-location", "sl"]);
const BUN_SUBS = new Set(["install", "i", "add", "a", "remove", "rm", "update", "upgrade", "x", "pm", "publish", "create", "init", "link", "unlink", "outdated", "run", "test", "build", "patch", "audit", "info", "why", "exec"]);
const isOctal = (c) => c !== undefined && c >= "0" && c <= "7";
const isHex = (c) => c !== undefined && ((c >= "0" && c <= "9") || (c >= "a" && c <= "f") || (c >= "A" && c <= "F"));
/** In cluster `a`, skip the digits after perl's -l / -0 at index j; returns the last consumed index. */
function skipPerlDigits(a, j) {
    if (a[j] === "0" && a[j + 1] === "x") {
        j++;
        while (isHex(a[j + 1]))
            j++;
    }
    else
        while (isOctal(a[j + 1]))
            j++;
    return j;
}
const NODE_SPEC = {
    inline: "ep", value: "rC", glued: "", interactive: "i", inlineLong: ["--eval", "--print"],
    valueLong: ["--require", "--import", "--loader", "--experimental-loader", "--conditions", "--input-type", "--env-file", "--title"],
};
const INTERPRETERS = {
    python: { inline: "c", value: "WX", glued: "", interactive: "i", script: "m" },
    node: NODE_SPEC,
    bun: NODE_SPEC,
    perl: { inline: "eE", value: "I", glued: "iMmxCDV", interactive: "", digits: "l0", debug: "d" },
    ruby: { inline: "e", value: "rIC", glued: "EFKTWx0l", interactive: "" },
    php: { inline: "rRBE", value: "dcz", glued: "", interactive: "a", script: "fF" },
    lua: { inline: "e", value: "l", glued: "", interactive: "i" },
    osascript: { inline: "e", value: "ls", glued: "", interactive: "" },
    rscript: { inline: "e", value: "", glued: "", interactive: "", inlineLong: ["--expr"] },
};
// ---------------------------------------------------------------- entry points
/** Classify a shell command string (Bash tool: mode "bash"; PowerShell tool: mode "pwsh"). */
export function classifyShellCommand(command, cwd, mode, environment = process.env, paths = pathEnv()) {
    // The bridge runs as the same user on the same machine as Claude Code, so an inherited CDPATH applies there too.
    // `paths`: platform, lstat and lookup budget for the write targets (risk/paths.ts), shared by the whole request.
    const ctx = { root: cwd, cur: cwd, cdpath: !!environment.CDPATH, paths };
    return classifyText(command, ctx, 0, mode, NO_STDIN);
}
/**
 * Classify an argument array: element 0 is the command word, every other element one literal word (no quotes,
 * expansions or separators are read in it). A shell, interpreter or wrapper in element 0 is read by the same rules
 * as on a command line, so `["sh","-c","rm -rf ~"]` classifies `rm -rf ~` with the bash rules.
 */
export function classifyShellArgv(argv, cwd, mode, environment = process.env, paths = pathEnv()) {
    if (argv.length === 0)
        return SAFE;
    const ctx = { root: cwd, cur: cwd, cdpath: !!environment.CDPATH, paths };
    // inert as the lexer marks a quoted `$(` or backtick: a stored payload (`env X='$(rm -rf ~)' sh`).
    const words = argv.map((text) => ({ text, dyn: false, glob: false, quoted: false, inert: text.includes("$(") || text.includes("`") }));
    return classifyArgv(words, { ctx, depth: 0, mode, stdin: NO_STDIN, procSubst: false, heredocs: [] });
}
const TAINT_ALWAYS = new Set(["source", ".", "eval", "shopt", "read", "mapfile", "readarray"]);
const DECLARERS = new Set(["declare", "typeset", "local", "export", "readonly"]);
/** The command may set CDPATH (or cd-relevant shell options) in a way that cannot be read statically. */
function taintsCdpath(words) {
    const st = stripWrappers(words, { root: "", cur: null });
    const argv = words.slice(st.index);
    const name = argv.length ? commandName(argv[0]).name : "";
    const args = argv.slice(1);
    // `unset CDPATH` clears it, so it does not taint even though the word contains CDPATH.
    if (name === "unset")
        return false;
    if (st.payload !== undefined)
        return true; // wrapper -c payload (eval-like)
    if (TAINT_ALWAYS.has(name))
        return true;
    if (name === "set")
        return args.some((w) => /^[-+][A-Za-z]*a/.test(w.text) || w.text === "allexport");
    if (name === "printf")
        return args.some((w) => w.text.startsWith("-v"));
    // A declarer with a nameref option (`declare -n r=CDPATH`) can alias CDPATH, or use a dynamic name.
    if (DECLARERS.has(name) && args.some((w) => /^-[A-Za-z]*n/.test(w.text) || w.dyn))
        return true;
    // Any word mentioning CDPATH (assignment, nameref target, export) taints.
    return words.some((w) => w.text.includes("CDPATH"));
}
/**
 * PowerShell reads U+2018..U+201E as quotes (also closed by an ASCII quote) and U+2013, U+2014, U+2015 as
 * parameter dashes (tested with PowerShell 5.1); the lexer does not model them, so text read as PowerShell with any
 * of them is not classified further.
 */
const PWSH_TYPOGRAPHIC = /[\u{2013}-\u{2015}\u{2018}-\u{201e}]/u;
function classifyText(text, ctx, depth, mode, stdin) {
    if (depth > MAX_DEPTH)
        return NESTED;
    if (mode === "pwsh" && PWSH_TYPOGRAPHIC.test(text))
        return COMPLEX;
    let segs;
    try {
        segs = lex(text, mode);
    }
    catch (e) {
        if (e instanceof LexError)
            return e.complex ? COMPLEX : NESTED;
        throw e;
    }
    return classifySegments(segs, ctx, depth, mode, stdin);
}
/** Classify already-lexed segments (a whole command string, or a substitution parsed in its context). */
function classifySegments(segs, ctx, depth, mode, stdin) {
    if (depth > MAX_DEPTH)
        return NESTED;
    let result = SAFE;
    for (let i = 0; i < segs.length; i++) {
        result = worst(result, classifySegment(segs[i], ctx, depth, mode, stdin)); // every command may read inherited stdin
        if (result.risk === "destructive")
            return result;
    }
    return downloadThenExec(segs) ? DOWNLOADED_CODE : result;
}
function classifySegment(seg, ctx, depth, mode, outer) {
    if (seg.funcDef)
        return destructive("Defines a shell function");
    let result = SAFE;
    for (const s of seg.substitutions) {
        // Use the parse made in context ($(...) closing rules affect heredocs); backticks are re-lexed like bash does.
        const sub = { ...ctx };
        const stdin = s.kind === ">(" ? PIPED : NO_STDIN;
        const r = s.segments ? classifySegments(s.segments, sub, depth + 1, mode, stdin) : classifyText(s.text, sub, depth + 1, mode, stdin);
        if (r.risk === "destructive")
            return r;
        result = worst(result, r);
    }
    const red = classifyRedirects(seg, ctx);
    if (red)
        return red;
    if (seg.compound)
        return result; // array elements are data; only their substitutions/redirects matter
    if (!ctx.cdpath && taintsCdpath(seg.words))
        ctx.cdpath = true;
    const env = {
        ctx,
        depth,
        mode,
        stdin: {
            piped: seg.pipedIn || outer.piped,
            redirected: outer.redirected || seg.heredocs.length > 0 || seg.redirects.some((r) => r.op === "<" || r.op === "<>" || r.op === "<&"),
        },
        procSubst: seg.substitutions.some((s) => s.kind === "<(" || s.kind === ">("),
        heredocs: seg.heredocs,
    };
    return worst(result, classifyArgv(seg.words, env));
}
const isFd = (t) => t !== "" && [...(t.endsWith("-") ? t.slice(0, -1) : t)].every((c) => c >= "0" && c <= "9");
/** Output redirection targets; an unknown target counts as outside. Identical targets are checked once. */
function classifyRedirects(seg, ctx) {
    let seen = null;
    for (const r of seg.redirects) {
        const t = r.target.text;
        if (r.op === "<" || r.op === "<&")
            continue;
        if (r.op === ">&" && (isFd(t) || t === "-"))
            continue;
        if (t === "")
            return OUTSIDE;
        if (NULL_SINKS.has(t.toLowerCase()))
            continue;
        if (r.target.dyn)
            return OUTSIDE;
        if (seg.redirects.length > 8) {
            if (seen === null)
                seen = new Set();
            if (seen.has(t))
                continue;
            seen.add(t);
        }
        const w = writeTargetRisk(t, ctx);
        if (w)
            return w;
    }
    return null;
}
function stripWrappers(words, ctx) {
    const out = { index: 0, risky: false, ctx, extra: [], parallel: false, readsExternalArgs: false };
    let i = 0;
    while (i < words.length) {
        const t = words[i].text;
        const asg = ASSIGN_RE.exec(t);
        if (asg) {
            if (riskyEnvName(asg[1]))
                out.risky = true;
            i++;
            continue;
        }
        if (words[i].dyn)
            break;
        const w = commandName(words[i]).name;
        const opts = WRAPPERS[w];
        if (opts === undefined)
            break;
        i++;
        if (w === "command" && /^-[A-Za-z]*[vV]/.test(words[i]?.text ?? "")) {
            out.index = words.length; // lookup only
            return out;
        }
        const pos = POSITIONAL[w];
        let positional = pos?.count ?? 0;
        let chdir = null;
        while (i < words.length) {
            const tok = words[i].text;
            if (tok === "--") {
                i++;
                break;
            }
            if (tok.startsWith("--") && tok.length > 2) {
                const eq = tok.indexOf("=");
                const key = eq >= 0 ? tok.slice(0, eq) : tok;
                let value = eq >= 0 ? tok.slice(eq + 1) : undefined;
                if (value === undefined && LONG_WITH_VALUE[w]?.has(key))
                    value = words[++i]?.text ?? "";
                if (key === "--command" && PAYLOAD_SHORT[w])
                    return { ...out, payload: value ?? words[i + 1]?.text ?? "" };
                if (w === "env" && key === "--split-string")
                    return { ...out, payload: [value ?? "", ...texts(words.slice(i + 1))].join(" ") };
                if (CHDIR_LONG[w] === key)
                    chdir = value ?? "";
                if ((w === "xargs" || w === "parallel") && key === "--replace")
                    out.replace = value ?? "{}";
                if (w === "parallel" && (key === "--arg-file" || key === "--arg-file-sep"))
                    out.readsExternalArgs = true;
                if (w === "runuser" && key === "--user")
                    positional = 0;
                if (w === "sudo" && key === "--chroot")
                    out.ctx = { root: "", cur: null };
                i++;
                continue;
            }
            if (tok.startsWith("-") && tok.length > 1) {
                for (let j = 1; j < tok.length; j++) {
                    const ch = tok[j];
                    if (PAYLOAD_SHORT[w] === ch) {
                        const value = j + 1 < tok.length ? tok.slice(j + 1) : words[i + 1]?.text ?? "";
                        return { ...out, payload: value };
                    }
                    if (w === "xargs" && (ch === "i" || ch === "e" || ch === "l")) {
                        if (ch === "i")
                            out.replace = j + 1 < tok.length ? tok.slice(j + 1) : "{}";
                        break;
                    }
                    if (w === "parallel" && ch === "i") {
                        out.replace = j + 1 < tok.length ? tok.slice(j + 1) : "{}";
                        break;
                    }
                    if (w === "parallel" && ch === "a") {
                        out.readsExternalArgs = true;
                        if (j === tok.length - 1)
                            i++; // value is the file
                        break;
                    }
                    if (opts.includes(ch)) {
                        const value = j + 1 < tok.length ? tok.slice(j + 1) : words[++i]?.text ?? "";
                        if (CHDIR_SHORT[w] === ch)
                            chdir = value;
                        if (w === "sudo" && ch === "R")
                            out.ctx = { root: "", cur: null };
                        if (w === "env" && ch === "S")
                            return { ...out, payload: [value, ...texts(words.slice(i + 1))].join(" ") };
                        if ((w === "xargs" || w === "parallel") && ch === "I")
                            out.replace = value;
                        if (w === "runuser" && ch === "u")
                            positional = 0;
                        break;
                    }
                }
                i++;
                continue;
            }
            if (ASSIGN_RE.test(tok)) {
                if (riskyEnvName(tok.slice(0, tok.indexOf("=")).replace(/\+$/, "")))
                    out.risky = true;
                i++;
                continue;
            }
            if (positional > 0 && (!pos.numericOnly || /^[+-]?\d+(\.\d+)?[smhd]?$/.test(tok))) {
                if (w === "chroot" && tok !== "/")
                    out.ctx = { root: "", cur: null };
                positional--;
                i++;
                continue;
            }
            break;
        }
        if (chdir !== null && out.ctx.root !== "") {
            out.ctx = { ...out.ctx, cur: chdir === "" ? null : resolveFor(chdir, out.ctx) };
        }
        if (w === "parallel") {
            out.parallel = true;
            out.replace ??= "{}";
            const sep = words.findIndex((x, k) => k >= i && x.text.startsWith(":::"));
            if (sep >= 0)
                out.extra.push(...texts(words.slice(sep + 1)).filter((x) => !x.startsWith(":::")));
        }
    }
    out.index = i;
    return out;
}
// ---------------------------------------------------------------- commands
function classifyArgv(words, env) {
    if (env.depth > MAX_DEPTH)
        return NESTED;
    for (const w of words) {
        if (w.dyn && w.text.includes("@P}"))
            return COMPLEX; // prompt expansion runs command substitutions
        if (w.inert && ASSIGN_RE.test(w.text))
            return COMPLEX; // stores a payload for later indirect execution
    }
    const st = stripWrappers(words, env.ctx);
    if (st.risky)
        return RISKY_ENV_SET;
    const adds = st.parallel || st.replace !== undefined || words.slice(0, st.index).some((w) => commandName(w).name === "xargs");
    const base = adds ? { ...env, argsAdded: true } : env;
    const e = st.ctx === env.ctx ? base : { ...base, ctx: st.ctx };
    if (st.payload !== undefined) {
        return st.payload ? classifyText(st.payload, e.ctx, e.depth + 1, "bash", e.stdin) : UNCLEAR_SHELL;
    }
    let result = SAFE;
    for (const x of st.extra) {
        result = worst(result, classifyText(x, e.ctx, e.depth + 1, e.mode, NO_STDIN));
        if (result.risk === "destructive")
            return result;
    }
    const argv = words.slice(st.index);
    const called = words.slice(0, st.index).some((w) => w.text === "&"); // PowerShell call operator
    if (env.mode === "pwsh" && called && argv.length === 0)
        return OBFUSCATED; // `& { ... }`, `& (expr)`
    if (st.parallel && (argv.length === 0 || argv[0].text.startsWith(":::")))
        return OBFUSCATED; // commands from stdin or args
    if (argv.length === 0)
        return result;
    // Replace strings put stdin data into the command (also into -c payloads and inline code).
    if (st.replace !== undefined && argv.some((w) => w.text.includes(st.replace)))
        return OBFUSCATED;
    if (st.parallel && argv.some((w) => w.text.includes("{") && w.text.includes("}")))
        return OBFUSCATED;
    // parallel feeding untrusted arguments (stdin pipe or -a/--arg-file) into a command with no replace string.
    if (st.parallel && !argv.some((w) => w.text.startsWith(":::")) && (env.stdin.piped || st.readsExternalArgs)) {
        return COMPLEX;
    }
    result = worst(result, classifyCommand(argv, e, called));
    // `{` inside a simple command opens a group (function f { ...; }, coproc NAME { ...; }).
    const group = argv.findIndex((w, k) => k > 0 && w.text === "{" && !w.quoted);
    if (result.risk !== "destructive" && group > 0)
        result = worst(result, classifyArgv(argv.slice(group + 1), { ...e, depth: e.depth + 1 }));
    // A command word with blanks or shell syntax is also read as a command string (watch, carriers using sh -c).
    if (result.risk !== "destructive" && /[\s;&|<>()`]/.test(argv[0].text)) {
        result = worst(result, classifyText(texts(argv).join(" "), e.ctx, e.depth + 1, e.mode, e.stdin));
    }
    return result;
}
function classifyCommand(argv, env, called = false) {
    const head = argv[0];
    const args = argv.slice(1);
    if (env.mode === "pwsh" && !called && head.text.startsWith("$"))
        return classifyPwshExpression(argv, env);
    const { name, obfuscated } = commandName(head);
    if (obfuscated)
        return OBFUSCATED;
    const sql = sqlRisk(argv, env.heredocs);
    if (sql)
        return sql;
    const self = marshalSelfRisk(name, args, env.ctx, { argsAdded: env.argsAdded === true, stdinConnected: stdinConnected(env) });
    if (self)
        return self;
    const recurseArgv = (ws) => classifyArgv(ws, { ...env, depth: env.depth + 1 });
    const recurseText = (s, mode = env.mode) => classifyText(s, env.ctx, env.depth + 1, mode, env.stdin);
    if (name === "case")
        return COMPLEX; // `)` pattern terminators cannot be tracked (also swallows subst-level code)
    if (name === "function")
        return destructive("Defines a shell function"); // may shadow any command
    if (name === "trap") {
        const action = args.find((w) => !w.text.startsWith("-"));
        return action && action.text ? recurseText(action.text) : SAFE;
    }
    if ((name === "alias" && args.some((w) => w.text.includes("="))) || name === "set-alias" || name === "new-alias" || name === "sal" || name === "nal") {
        return destructive("Defines a shell alias");
    }
    if (CD.has(name)) {
        const target = args.find((w) => !w.text.startsWith("-"));
        env.ctx.cur = cdTarget(target, env.ctx);
        return SAFE;
    }
    if (name === "iex" || name === "invoke-expression" || name === "invoke-command" || name === "icm" || name === "start-process" || name === "saps")
        return INLINE_CODE;
    if (name === "bun" && !BUN_SUBS.has((args.find((w) => !w.text.startsWith("-"))?.text ?? "").toLowerCase()) && !args.some((w) => w.text === "--version")) {
        return classifyInterpreter("bun", args, env);
    }
    if ((INTERPRETERS[name] && name !== "bun") || name === "deno")
        return classifyInterpreter(name, args, env);
    if (name === "powershell" || name === "pwsh")
        return classifyPowerShell(args, env);
    if (name === "cmd")
        return classifyCmd(args, env);
    if (SHELLS.has(name))
        return classifyShell(name, args, env);
    if (name === "source" || name === ".")
        return classifySource(args, env);
    if (name === "eval")
        return recurseText(texts(args).join(" "));
    if (name === "start")
        return classifyStart(args, env);
    if (name === "find") {
        const lower = texts(args).map((x) => x.toLowerCase());
        if (lower.includes("-delete"))
            return destructive("Deletes files and subfolders");
        let r = SAFE;
        for (let x = 0; x < lower.length; x++) {
            if (!/^-(exec|execdir|ok|okdir)$/.test(lower[x]))
                continue;
            let end = x + 1;
            while (end < args.length && args[end].text !== ";" && args[end].text !== "+")
                end++;
            r = worst(r, classifyArgv(args.slice(x + 1, end), { ...env, depth: env.depth + 1, argsAdded: true }));
            if (r.risk === "destructive")
                return r;
            x = end;
        }
        return r;
    }
    if (name === "git") {
        return (classifyGit(texts(args), {
            text: (s) => recurseText(s, "bash"),
            argv: (from) => recurseArgv(args.slice(from)),
        }) ?? SAFE);
    }
    return classifyNamed(name, args, env.ctx) ?? SAFE;
}
/** Directory after `cd target`; null when it cannot be known. Plain names resolve relative unless CDPATH was set in this command. */
function cdTarget(target, ctx) {
    if (target === undefined || target.dyn)
        return null;
    const t = target.text;
    if (t === "-" || t === "")
        return null;
    const explicit = t === "." || t === ".." || /^\.\.?[\\/]/.test(t) || /^(?:[A-Za-z]:)?[\\/]/.test(t);
    return explicit || !ctx.cdpath ? resolveFor(t, ctx) : null;
}
/** PowerShell statement starting with a variable: reads and assignments are understood, method calls are not. */
function classifyPwshExpression(argv, env) {
    const t = argv[0].text;
    if (t.endsWith("("))
        return OBFUSCATED; // $x.Method(...)
    const eq = t.indexOf("=");
    const lhs = eq >= 0 ? t.slice(0, eq) : t;
    let rhs;
    if (eq >= 0)
        rhs = [...(eq + 1 < t.length ? [{ ...argv[0], text: t.slice(eq + 1), dyn: false }] : []), ...argv.slice(1)];
    else if (argv[1]?.text.startsWith("="))
        rhs = [...(argv[1].text.length > 1 ? [{ ...argv[1], text: argv[1].text.slice(1) }] : []), ...argv.slice(2)];
    else
        return /^\$[\w:.?{}[\]]*$/.test(t) ? SAFE : OBFUSCATED; // variable or property read, e.g. `$x.version -ne 0`
    if (!/^\$[A-Za-z_][\w:]*$/.test(lhs))
        return OBFUSCATED;
    if (lhs.toLowerCase().startsWith("$env:") && riskyEnvName(lhs.slice(5)))
        return RISKY_ENV_SET;
    if (rhs.length === 0 || rhs[0].quoted || /^[\d.+-]/.test(rhs[0].text))
        return SAFE; // literal value
    return classifyArgv(rhs, env);
}
function classifySource(args, env) {
    if (env.procSubst)
        return COMPLEX;
    const file = args.find((w) => !w.text.startsWith("-"));
    if (env.mode === "pwsh" && (!file || file.dyn))
        return OBFUSCATED; // `. { ... }`, `. (expr)`, `. $scriptblock`
    if (!file || STDIN_PATHS.has(file.text))
        return stdinConnected(env) ? stdinReason(env) : SAFE;
    return SAFE; // running a script file stays safe (policy)
}
function classifyStart(args, env) {
    let i = args[0]?.quoted ? 1 : 0; // window title
    while (i < args.length && args[i].text.startsWith("/")) {
        const k = args[i].text.toLowerCase();
        i += k === "/d" || k === "/affinity" || k === "/node" ? 2 : 1;
    }
    return classifyArgv(args.slice(i), { ...env, depth: env.depth + 1 });
}
const POSIX_SHELL = {
    flags: "abefhiklmnpqrstuvxBCDEHIPTV", value: "oO", glued: "", payload: "c", init: "", getopt: false,
    long: ["--debugger", "--dump-po-strings", "--dump-strings", "--help", "--login", "--noediting", "--noprofile", "--norc", "--posix", "--pretty-print", "--restricted", "--verbose", "--version"],
    longValue: ["--rcfile", "--init-file"], longPayload: [], longInit: [],
};
const CSH = { ...POSIX_SHELL, flags: "bdefilmnqstvxVXF", value: "", long: ["--help", "--version"], longValue: [] };
const SHELL_SPECS = {
    zsh: { ...POSIX_SHELL, anyOption: true },
    csh: CSH,
    tcsh: CSH,
    fish: {
        flags: "ilnvhNP", value: "pdof", glued: "", payload: "c", init: "C", getopt: true,
        long: ["--interactive", "--login", "--no-execute", "--print-rusage-self", "--version", "--help", "--no-config", "--private", "--print-debug-categories"],
        longValue: ["--profile", "--profile-startup", "--debug", "--debug-output", "--features", "--debug-stack-frames"],
        longPayload: ["--command"], longInit: ["--init-command"],
    },
    su: {
        flags: "lmpPf", value: "sgGw", glued: "", payload: "c", init: "", getopt: true,
        long: ["--login", "--preserve-environment", "--pty", "--help", "--version"],
        longValue: ["--shell", "--group", "--supp-group", "--whitelist-environment"],
        longPayload: ["--command", "--session-command"], longInit: [],
    },
    script: {
        flags: "aefqIOBhV", value: "TEm", glued: "t", payload: "c", init: "", getopt: true,
        long: ["--append", "--return", "--flush", "--quiet", "--force", "--help", "--version"],
        longValue: ["--timing", "--log-in", "--log-out", "--log-io", "--log-timing", "--logging-format", "--echo", "--output-limit"],
        longPayload: ["--command"], longInit: [],
    },
};
const UNKNOWN_SHELL_OPTION = destructive("Unclear shell option");
function classifyShell(name, args, env) {
    const spec = SHELL_SPECS[name] ?? POSIX_SHELL;
    let cmd = false;
    let readsStdin = false;
    const payloads = [];
    const inits = [];
    let optsDone = false;
    const positionals = [];
    for (let i = 0; i < args.length; i++) {
        const t = args[i].text;
        if (!optsDone && (t === "--" || (t === "-" && !spec.getopt))) {
            optsDone = true;
            continue;
        }
        if (!optsDone && t.startsWith("--") && t.length > 2) {
            const eq = t.indexOf("=");
            const key = eq >= 0 ? t.slice(0, eq) : t;
            const value = () => (eq >= 0 ? t.slice(eq + 1) : args[++i]?.text ?? "");
            if (spec.longPayload.includes(key)) {
                cmd = true;
                payloads.push(value());
            }
            else if (spec.longInit.includes(key))
                inits.push(value());
            else if (spec.longValue.includes(key))
                value();
            else if (!spec.anyOption && (eq >= 0 || !spec.long.includes(key)))
                return UNKNOWN_SHELL_OPTION;
            continue;
        }
        if (!optsDone && (t[0] === "-" || t[0] === "+") && t.length > 1) {
            for (let j = 1; j < t.length; j++) {
                const ch = t[j];
                const rest = () => (j + 1 < t.length ? t.slice(j + 1) : args[++i]?.text ?? "");
                if (spec.payload.includes(ch)) {
                    cmd = true;
                    if (spec.getopt) {
                        payloads.push(rest());
                        break;
                    }
                    continue;
                }
                if (spec.init.includes(ch)) {
                    inits.push(rest());
                    break;
                }
                if (spec.value.includes(ch)) {
                    rest();
                    break;
                }
                if (spec.glued.includes(ch))
                    break;
                if (!spec.anyOption && !spec.flags.includes(ch))
                    return UNKNOWN_SHELL_OPTION;
                if (ch === "s" || ch === "i")
                    readsStdin = true;
            }
            continue;
        }
        positionals.push(args[i]);
        if (!spec.getopt)
            optsDone = true;
    }
    let result = SAFE;
    for (const p of inits) {
        result = worst(result, p ? classifyText(p, env.ctx, env.depth + 1, "bash", env.stdin) : UNCLEAR_SHELL);
        if (result.risk === "destructive")
            return result;
    }
    if (cmd) {
        const strings = spec.getopt ? payloads : positionals.length ? [texts(positionals).join(" ")] : [];
        if (strings.length === 0 || strings.some((p) => !p))
            return UNCLEAR_SHELL; // command string supplied from elsewhere (stdin, xargs)
        for (const p of strings) {
            result = worst(result, classifyText(p, env.ctx, env.depth + 1, "bash", env.stdin));
            if (result.risk === "destructive")
                return result;
        }
        return result;
    }
    if (env.procSubst)
        return COMPLEX;
    const script = spec.getopt ? undefined : positionals[0];
    if (readsStdin || !script || STDIN_PATHS.has(script.text)) {
        if (stdinConnected(env))
            return stdinReason(env);
    }
    if (name === "script")
        return writeAny(positionals, env.ctx) ?? result;
    return result; // running a script file stays safe (policy)
}
const NODE_CODE_OPTIONS = new Set(["--import", "--loader", "--experimental-loader", "--require", "-r", "--preload"]);
const PHP_CODE_INI = ["auto_prepend_file", "auto_append_file", "allow_url_include"];
/** perl -d:Module value: a bare module name only. */
function perlBareModule(v) {
    if (v === "")
        return false;
    for (const c of v) {
        const ok = (c >= "a" && c <= "z") || (c >= "A" && c <= "Z") || (c >= "0" && c <= "9") || c === "_" || c === ":";
        if (!ok)
            return false;
    }
    return true;
}
/** perl -M/-m value: `Module`, `-Module` or `Module=import,list`; anything else is code. */
function perlModuleArg(v) {
    let k = v.startsWith("-") ? 1 : 0;
    const start = k;
    while (k < v.length) {
        const c = v[k];
        const ok = (c >= "a" && c <= "z") || (c >= "A" && c <= "Z") || (c >= "0" && c <= "9") || c === "_" || c === ":";
        if (!ok)
            break;
        k++;
    }
    if (k === start)
        return false;
    if (k === v.length)
        return true;
    return v[k] === "=" && !v.includes(String.fromCharCode(0));
}
/** Interpreter options whose value is code: node/bun loaders from data:/http(s): URLs, perl -M with code, php -d prepend/include. */
function codeInOptionValue(name, args) {
    for (let i = 0; i < args.length; i++) {
        const a = args[i];
        if (!a.startsWith("-") || a === "--" || a === "-")
            return false; // script name or end of options
        if (name === "node" || name === "bun") {
            const eq = a.startsWith("--") ? a.indexOf("=") : -1;
            const key = eq >= 0 ? a.slice(0, eq) : a;
            if (NODE_CODE_OPTIONS.has(key)) {
                const v = (eq >= 0 ? a.slice(eq + 1) : args[++i] ?? "").toLowerCase();
                if (v.startsWith("data:") || v.startsWith("http:") || v.startsWith("https:"))
                    return true;
            }
            else if (eq < 0 && (NODE_SPEC.valueLong?.includes(key) || key === "-C"))
                i++;
        }
        else if (name === "perl") {
            if (a.startsWith("--"))
                continue;
            for (let j = 1; j < a.length; j++) {
                const ch = a[j];
                if (ch === "M" || ch === "m") {
                    if (!perlModuleArg(j + 1 < a.length ? a.slice(j + 1) : args[++i] ?? ""))
                        return true;
                    break;
                }
                if (ch === "d") {
                    // -d[t]:Module runs a debugger module: only a bare module name is allowed (no `=...`, `{`, `;`).
                    let r = a.slice(j + 1);
                    if (r.startsWith("t"))
                        r = r.slice(1);
                    if (r.startsWith("="))
                        return true;
                    if (r.startsWith(":")) {
                        if (!perlBareModule(r.slice(1)))
                            return true;
                        break;
                    }
                    if (a[j + 1] === "t")
                        j++;
                    continue; // plain -d / -dt: the cluster goes on
                }
                if (ch === "l" || ch === "0") {
                    j = skipPerlDigits(a, j);
                    continue;
                }
                if (ch === "I") {
                    if (j === a.length - 1)
                        i++;
                    break;
                }
                if ("ixCDVeE".includes(ch))
                    break;
            }
        }
        else if (name === "php") {
            let v = "";
            if (a === "-d" || a === "--define")
                v = args[++i] ?? "";
            else if (a.startsWith("-d"))
                v = a.slice(2);
            else if (a.startsWith("--define="))
                v = a.slice(9);
            else if (a === "-c" || a === "-z")
                i++;
            const key = v.toLowerCase().trimStart();
            if (PHP_CODE_INI.some((k) => key.startsWith(k)))
                return true;
        }
    }
    return false;
}
function interpreterMode(spec, args) {
    let interactive = false;
    for (let i = 0; i < args.length; i++) {
        const a = args[i];
        if (a === "-")
            return "stdin";
        if (a === "--")
            return args[i + 1] !== undefined && !interactive ? "script" : "stdin";
        if (a.startsWith("--")) {
            const key = a.split("=")[0];
            if (spec.inlineLong?.includes(key))
                return "inline";
            if (key === "--interactive")
                interactive = true;
            if (spec.valueLong?.includes(key) && !a.includes("="))
                i++;
            continue;
        }
        if (a.startsWith("-") && a.length > 1) {
            for (let j = 1; j < a.length; j++) {
                const ch = a[j];
                if (spec.inline.includes(ch))
                    return "inline";
                if (spec.script?.includes(ch))
                    return interactive ? "stdin" : "script";
                if (spec.interactive.includes(ch))
                    interactive = true;
                if (spec.digits?.includes(ch)) {
                    j = skipPerlDigits(a, j); // `-lne`, `-0777ne`: the cluster goes on
                    continue;
                }
                if (spec.debug?.includes(ch)) {
                    if (a[j + 1] === "t")
                        j++;
                    if (a[j + 1] === ":" || a[j + 1] === "=")
                        break; // -d:Module (checked separately)
                    continue; // `-de`, `-dte`: the cluster goes on
                }
                if (spec.value.includes(ch)) {
                    if (j === a.length - 1)
                        i++;
                    break;
                }
                if (spec.glued.includes(ch))
                    break;
            }
            continue;
        }
        return interactive ? "stdin" : "script";
    }
    return "stdin";
}
function denoMode(args) {
    let i = 0;
    while (i < args.length && args[i].startsWith("-"))
        i += args[i] === "-L" || args[i] === "--log-level" ? 2 : 1;
    const sub = args[i];
    if (sub === undefined)
        return "stdin"; // repl
    if (sub === "eval")
        return "inline";
    if (sub === "repl")
        return args.some((a) => a.startsWith("--eval")) ? "inline" : "stdin";
    if (sub === "run") {
        const file = args.slice(i + 1).find((a) => !a.startsWith("-"));
        return file === undefined || file === "-" ? "stdin" : "script";
    }
    return "script";
}
function classifyInterpreter(name, args, env) {
    const a = texts(args);
    if (codeInOptionValue(name, a))
        return INLINE_CODE;
    const mode = name === "deno" ? denoMode(a) : interpreterMode(INTERPRETERS[name], a);
    if (mode === "inline")
        return INLINE_CODE;
    if (env.procSubst)
        return COMPLEX;
    if (mode === "stdin" && stdinConnected(env))
        return stdinReason(env);
    return SAFE; // running a script file stays safe (policy)
}
// ---------------------------------------------------------------- PowerShell and cmd
const PS_PREFIXES = new Set(["-", "/", "–", "—", "―"]);
const PS_VALUE = [
    ["executionpolicy", ["ep", "ex"]], ["workingdirectory", ["wd"]], ["configurationname", ["config"]], ["custompipename", []],
    ["settingsfile", ["settings"]], ["outputformat", ["of", "o"]], ["inputformat", ["if", "inp"]], ["windowstyle", ["w", "win"]],
    ["version", ["v"]], ["psconsolefile", []],
];
const PS_SWITCH = [
    ["noprofile", ["nop"]], ["nologo", ["nol"]], ["noninteractive", ["noni"]], ["noexit", ["noe"]], ["sta", []], ["mta", []],
    ["interactive", ["i"]], ["login", ["l"]], ["help", ["h", "?"]], ["noprofileloadtime", []], ["sshservermode", ["sshs"]],
    ["servermode", ["s"]], ["namedpipeservermode", []], ["socketservermode", []],
];
const psMatch = (table, key) => table.some(([full, aliases]) => aliases.includes(key) || full.startsWith(key));
function classifyPowerShell(args, env) {
    const payload = (from, glued) => {
        const parts = [...(glued ? [glued] : []), ...texts(args.slice(from))];
        if (parts.length === 0 || (parts.length === 1 && parts[0] === "-"))
            return stdinConnected(env) ? stdinReason(env) : SAFE;
        return classifyText(parts.join(" "), env.ctx, env.depth + 1, "pwsh", env.stdin);
    };
    for (let i = 0; i < args.length; i++) {
        const t = args[i].text;
        if (t.length > 0 && PS_PREFIXES.has(t[0])) {
            const colon = t.indexOf(":");
            const key = (colon >= 0 ? t.slice(1, colon) : t.slice(1)).toLowerCase();
            const glued = colon >= 0 ? t.slice(colon + 1) : undefined;
            if (key === "")
                return stdinConnected(env) ? stdinReason(env) : SAFE; // "-": commands from stdin
            if (PS_VALUE.some(([, aliases]) => aliases.includes(key))) {
                if (glued === undefined)
                    i++;
                continue;
            }
            if (key === "ec" || key === "ea" || "encodedcommand".startsWith(key) || "encodedarguments".startsWith(key))
                return OBFUSCATED;
            if ("command".startsWith(key))
                return payload(i + 1, glued);
            if ("file".startsWith(key)) {
                const file = glued ?? args[i + 1]?.text;
                return file === undefined || file === "-" ? (stdinConnected(env) ? stdinReason(env) : SAFE) : SAFE; // script file (policy)
            }
            if (psMatch(PS_VALUE, key)) {
                if (glued === undefined)
                    i++;
                continue;
            }
            if (psMatch(PS_SWITCH, key))
                continue;
            return UNCLEAR_SHELL;
        }
        if (/\.ps1$/i.test(t))
            return SAFE; // script file (policy)
        return payload(i); // positional = -Command in Windows PowerShell
    }
    return stdinConnected(env) ? stdinReason(env) : SAFE;
}
function classifyCmd(args, env) {
    for (let i = 0; i < args.length; i++) {
        const t = args[i].text;
        const m = /^\/\/?([ckr])(.*)$/is.exec(t);
        if (m) {
            const parts = [...(m[2] ? [m[2]] : []), ...texts(args.slice(i + 1))];
            return parts.length ? classifyText(parts.join(" "), env.ctx, env.depth + 1, "cmd", env.stdin) : UNCLEAR_SHELL;
        }
        if (!t.startsWith("/"))
            break;
    }
    return stdinConnected(env) ? stdinReason(env) : SAFE; // cmd reads commands from stdin
}
// ---------------------------------------------------------------- download, then execute
const EXEC_LIKE = new Set([
    ...SHELLS, ...Object.keys(INTERPRETERS), "deno", "source", ".", "pwsh", "powershell", "cmd", "start", "chmod", "eval", "iex",
    "invoke-expression", "start-process",
]);
const baseName = (p) => {
    let k = p.length;
    while (k > 0 && p[k - 1] !== "/" && p[k - 1] !== "\\")
        k--;
    return p.slice(k).toLowerCase();
};
const effectiveArgv = (seg) => {
    const st = stripWrappers(seg.words, { root: "", cur: null });
    return st.payload !== undefined ? [] : seg.words.slice(st.index);
};
/** A file written by a download in this command list is executed later in it. Single pass. */
function downloadThenExec(segs) {
    const outputs = new Set();
    let unknown = false;
    let downloaded = false;
    let inDownloadPipe = false; // this pipeline carries downloaded bytes
    for (const seg of segs) {
        if (seg.compound)
            continue; // array elements are not commands
        if (!seg.pipedIn)
            inDownloadPipe = false;
        const argv = effectiveArgv(seg);
        const name = argv.length ? commandName(argv[0]).name : "";
        if (DOWNLOADERS.has(name)) {
            const d = downloadInfo(name, argv.slice(1));
            for (const o of d.outputs)
                outputs.add(baseName(o.text));
            unknown ||= d.unknown;
            downloaded = inDownloadPipe = true;
        }
        else if (downloaded && argv.length) {
            const head = argv[0].text;
            const pathLike = head.includes("/") || head.includes("\\");
            if (outputs.has(baseName(head)) || (unknown && pathLike))
                return true;
            if (EXEC_LIKE.has(name)) {
                const files = argv.slice(1).filter((w) => !w.text.startsWith("-") && !w.text.startsWith("+"));
                if (files.some((w) => outputs.has(baseName(w.text))))
                    return true;
                if (unknown && name !== "chmod" && files.length > 0)
                    return true;
            }
        }
        if (inDownloadPipe) {
            // Redirect targets and tee arguments in the pipeline receive the download.
            for (const r of seg.redirects)
                if (r.op.includes(">") && !isFd(r.target.text))
                    outputs.add(baseName(r.target.text));
            if (name === "tee")
                for (const w of argv.slice(1))
                    if (!w.text.startsWith("-"))
                        outputs.add(baseName(w.text));
        }
    }
    return false;
}
