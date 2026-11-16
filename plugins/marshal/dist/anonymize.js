import { relInside } from "./risk/paths.js";
const REDACTED = "[REDACTED]";
const MAX_RECORDED_COMMAND = 200;
const MAX_RECORDED_SUMMARY = 280;
const MAX_RECORDED_TITLE = 40;
/** Same cap as a plan step on the wire (plan.ts). */
const MAX_RECORDED_STEP = 80;
/** Cuts to at most `n` code points with "…". */
function clipChars(s, n) {
    const chars = [...s];
    return chars.length > n ? chars.slice(0, n - 1).join("") + "…" : s;
}
/** Cuts to at most `n` UTF-16 units without leaving half a surrogate pair. */
function cap(s, n) {
    if (s.length <= n)
        return s;
    const last = s.charCodeAt(n - 1);
    return s.slice(0, last >= 0xd800 && last <= 0xdbff ? n - 1 : n);
}
// ---------------------------------------------------------------------------
// Secrets. Every pattern is anchored on a literal or a lookbehind, and every
// open-ended run is either consumed once or bounded, so no start position
// rescans a long run (linear time).
// ---------------------------------------------------------------------------
/** Redacts `-----BEGIN <label>-----` ... `-----END <label>-----` blocks; an unterminated BEGIN redacts to the end. */
function redactArmored(s) {
    const BEGIN = /-----BEGIN ([A-Z0-9 ]{1,40})-----/g;
    let out = "";
    let pos = 0;
    for (;;) {
        BEGIN.lastIndex = pos;
        const m = BEGIN.exec(s);
        if (!m)
            break;
        out += s.slice(pos, m.index) + REDACTED;
        const endMarker = `-----END ${m[1]}-----`;
        const end = s.indexOf(endMarker, m.index + m[0].length);
        if (end < 0)
            return out;
        pos = end + endMarker.length;
    }
    return out + s.slice(pos);
}
const TOKEN_PREFIXES = [
    "sk-ant-", "sk-", "ghp_", "gho_", "ghs_", "ghu_", "ghr_", "github_pat_",
    "AKIA", "ASIA", "xoxa-", "xoxb-", "xoxp-", "xoxr-", "eyJ",
];
const isTokenChar = (c) => (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c === 46 || c === 95 || c === 126 || c === 43 || c === 47 || c === 61 || c === 45;
const isAlnum = (c) => (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122);
/** A known-prefix token running to the end of the string (upstream truncation) is redacted whatever its length. */
function redactTrailingPartial(s) {
    let i = s.length;
    while (i > 0 && isTokenChar(s.charCodeAt(i - 1)))
        i--;
    let best = -1;
    for (const p of TOKEN_PREFIXES) {
        let from = i;
        for (;;) {
            const k = s.indexOf(p, from);
            if (k < 0)
                break;
            if (k === i || !isAlnum(s.charCodeAt(k - 1))) {
                if (best < 0 || k < best)
                    best = k;
                break;
            }
            from = k + 1;
        }
    }
    return best < 0 ? s : s.slice(0, best) + REDACTED;
}
const VALUE = `"(?:[^"\\\\]|\\\\.)*"?|\\\\"[^"]*"?|'[^']*'?|[^\\s&"']+`;
const KEYWORD = "(?:(?:password|passwd|pwd|token|secret|api[_-]?key|apikey|access[_-]?key|private[_-]?key|passphrase|credentials?)[A-Za-z0-9_]{0,256}|auth)";
const FLAGS = "password|passwd|pass|passphrase|token|secret|api-key|apikey|auth|cookie";
const SECRET_RULES = [
    [/(?<![A-Za-z0-9])authorization\s*[:=]\s*[^\r\n"']+/gi, `Authorization: ${REDACTED}`],
    [/(?<![A-Za-z0-9-])((?:Set-)?Cookie\s*:\s*)[^\r\n"']+/gi, `$1${REDACTED}`],
    [/(?<![A-Za-z0-9])Bearer\s+[A-Za-z0-9._~+/=-]+/gi, REDACTED],
    [/(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*/g, REDACTED],
    [/sk-ant-[A-Za-z0-9_-]+/g, REDACTED],
    [/sk-[A-Za-z0-9_-]{20,}/g, REDACTED],
    [/github_pat_[A-Za-z0-9_]{20,}/g, REDACTED],
    [/gh[pousr]_[A-Za-z0-9]{20,}/g, REDACTED],
    [/(?:AKIA|ASIA)[A-Z0-9]{16}/g, REDACTED],
    [/xox[abprs]-[A-Za-z0-9-]+/g, REDACTED],
    // scheme://user:password@host
    [/([A-Za-z][A-Za-z0-9+.-]{1,20}:\/\/[^\s:@/"']{0,100}:)[^\s@/"']+@/g, `$1${REDACTED}@`],
    [/(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+/g, REDACTED],
    // curl --user user:pass
    [/(?<![A-Za-z0-9])(--user(?:\s+|=)["']?[^\s:"']*:)[^\s"']+/g, `$1${REDACTED}`],
    // mysql -pSECRET (glued form only)
    [/(?<![A-Za-z0-9_-])((?:mysql|mariadb|mysqldump|mysqladmin)\b[^\n|;&]{0,300}?\s-p)(?=\S)\S+/g, `$1${REDACTED}`],
];
const AFTER_KEYWORD_RULES = [[/([=:]\s*)[A-Za-z0-9+_-]{32,}={0,2}/g, `$1${REDACTED}`]];
/** `keyword=value` / `"keyword": value` (optionally prefixed: GITHUB_TOKEN, MYTOKEN) or `--flag value` / `--flag=value`. */
const SECRET_PREFIX = new RegExp(`(?<![A-Za-z0-9_])[A-Za-z0-9_]{0,64}?${KEYWORD}\\\\?["']?\\s*[=:]\\s*|(?<![A-Za-z0-9])--(?:${FLAGS})(?:\\s+|=)`, "gi");
const VALUE_AT = new RegExp(VALUE, "y");
const QUOTED_KEY = /["']\s*[=:]\s*$/;
const isQuote = (c) => c === '"' || c === "'";
/**
 * Value scanning over one text. Value starts arrive in strictly increasing order, but a value may start
 * inside an earlier one (`--token --password x`), so runs and bracket groups are memoised: every character
 * is scanned a bounded number of times (linear time).
 */
class ValueScan {
    s;
    /** Per mode (0 shell, 1 JSON): 0 = unknown, else 1 + index of the next stop character. */
    stopMemo = [undefined, undefined];
    /** Bracket position -> index just past its group (or the text length when it never closes). */
    groupEnd = new Map();
    constructor(s) {
        this.s = s;
    }
    /** First index at or after `p` holding a stop character (`\s &`, JSON also `, } ]`), or the length. */
    toStop(p, json) {
        const s = this.s;
        const k = json ? 1 : 0;
        const memo = (this.stopMemo[k] ??= new Int32Array(s.length + 1));
        let i = p;
        while (i < s.length && memo[i] === 0 && !isStop(s[i], json))
            i++;
        const at = i < s.length && memo[i] !== 0 ? memo[i] - 1 : i;
        for (let j = p; j < i; j++)
            memo[j] = at + 1;
        return at;
    }
    /**
     * Index just past the bracket group opening at `start`; '...' and "..." strings are skipped.
     * Fails closed: an escaped quote (a backslash run right before a quote), an unclosed quote or an
     * unbalanced group runs the value to the end of the text (a pretty-printed object spans lines).
     * A bracket seen here outside quotes always scans the same way from itself, so its end is memoised.
     */
    bracketEnd(start) {
        const s = this.s;
        const known = this.groupEnd.get(start);
        if (known !== undefined)
            return known;
        const open = [];
        const failClosed = () => {
            for (const o of open)
                this.groupEnd.set(o, s.length);
            return s.length;
        };
        for (let i = start; i < s.length; i++) {
            const c = s[i];
            if (c === "\\") {
                let j = i;
                while (j < s.length && s[j] === "\\")
                    j++;
                if (isQuote(s[j]))
                    return failClosed();
                i = (j - i) % 2 === 1 ? j : j - 1; // an odd run escapes the next char
            }
            else if (isQuote(c)) {
                let j = i + 1;
                for (; j < s.length && s[j] !== c; j++) {
                    if (s[j] !== "\\")
                        continue;
                    while (j + 1 < s.length && s[j + 1] === "\\")
                        j++;
                    if (isQuote(s[j + 1]))
                        return failClosed();
                    j++;
                }
                if (j >= s.length)
                    return failClosed();
                i = j;
            }
            else if (c === "{" || c === "[") {
                const end = i > start ? this.groupEnd.get(i) : undefined;
                if (end === undefined)
                    open.push(i);
                else if (end >= s.length)
                    return failClosed();
                else
                    i = end - 1; // group already scanned: skip it
            }
            else if ((c === "}" || c === "]") && open.length > 0) {
                this.groupEnd.set(open.pop(), i + 1);
                if (open.length === 0)
                    return i + 1;
            }
        }
        return failClosed();
    }
    /**
     * End of the value at `start`, or -1 if there is none. Text glued to the value (quotes included, as in
     * shell `abc'def'`) is still value up to whitespace or `&`; JSON context (quoted key) also stops at `,` `}` `]`.
     */
    endOfValue(start, json) {
        const s = this.s;
        const c = s[start];
        if (c === undefined)
            return -1;
        let end;
        if (c === "{" || c === "[")
            end = this.bracketEnd(start);
        else if (isQuote(c) || (c === "\\" && s[start + 1] === '"')) {
            // quoted forms: a later value can only start at or past this one's closing quote, so no rescans
            VALUE_AT.lastIndex = start;
            end = start + VALUE_AT.exec(s)[0].length;
        }
        else if (isStop(c, json))
            return -1;
        else
            end = start; // bare value: the run up to the next stop character
        return this.toStop(end, json);
    }
}
const SHELL_STOP = /[\s&]/;
/** A bare value after a quoted (JSON) key also ends at JSON delimiters. */
const JSON_STOP = /[\s&,}\]]/;
const isStop = (c, json) => (json ? JSON_STOP : SHELL_STOP).test(c);
/**
 * Redacts the value after every keyword or flag prefix. Ranges are collected over the original text,
 * including prefixes found inside an earlier value (`--token --password x`, `--password Ab1token=Q`),
 * merged where they overlap or touch, and replaced once.
 */
function redactKeywordValues(s) {
    const scan = new ValueScan(s);
    const ranges = [];
    SECRET_PREFIX.lastIndex = 0;
    for (let m = SECRET_PREFIX.exec(s); m; m = SECRET_PREFIX.exec(s)) {
        const start = m.index + m[0].length;
        const end = scan.endOfValue(start, QUOTED_KEY.test(m[0]));
        if (end > start) {
            const last = ranges[ranges.length - 1];
            if (last && start <= last[1])
                last[1] = Math.max(last[1], end);
            else
                ranges.push([start, end]);
        }
        SECRET_PREFIX.lastIndex = start; // keep looking inside the value
    }
    let out = "";
    let pos = 0;
    for (const [a, b] of ranges) {
        out += s.slice(pos, a) + REDACTED;
        pos = b;
    }
    return out + s.slice(pos);
}
const CURL_USER_SHORT = /(?<![A-Za-z0-9])(-u(?:\s+|=)["']?[^\s:"']*:)[^\s"']+/g;
export function redactSecrets(text) {
    let s = redactArmored(text);
    for (const [re, rep] of SECRET_RULES)
        s = s.replace(re, rep);
    s = redactKeywordValues(s);
    for (const [re, rep] of AFTER_KEYWORD_RULES)
        s = s.replace(re, rep);
    if (/\bcurl\b/i.test(s))
        s = s.replace(CURL_USER_SHORT, `$1${REDACTED}`);
    return redactTrailingPartial(s);
}
// ---------------------------------------------------------------------------
// Paths and identity.
// ---------------------------------------------------------------------------
const DRIVE = /^([A-Za-z]):(?:[\\/]|$)/;
const isDriveRoot = (p) => DRIVE.test(p);
const isWindowsRoot = (p) => isDriveRoot(p) || /^[\\/]{2}[^\\/]/.test(p);
const isAbsolute = (p) => /^(?:[A-Za-z]:)?[\\/]/.test(p);
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const SEP = "[\\\\/]";
const TAIL = "([\\\\/][^\\s\"'`;&|<>()]*)?";
const normTail = (t) => (t ? t.replace(/[\\/]+/g, "/") : "");
/** Drops `\\?\` / `\\.\` (and turns `\\?\UNC\` into `\\`). */
function stripExtended(p) {
    return p.replace(/^[\\/]{2}[?.][\\/](UNC[\\/])?/i, (_m, unc) => (unc ? "\\\\" : ""));
}
/** A root as configured: extended prefix dropped, `/c/Users/..` and `/mnt/c/Users/..` turned into `c:/Users/..`. */
function normRoot(r) {
    return stripExtended(r).replace(/^\/(?:mnt\/)?([A-Za-z])(?=\/users\/)/i, "$1:");
}
/** Matches a root in free text: either separator, any case for Windows roots, `/c/` and `/mnt/c/` drive forms. */
function rootPattern(root) {
    const drive = DRIVE.exec(root);
    const segs = root.split(/[\\/]+/).filter((x) => x !== "" && !(drive && x.length === 2 && x[1] === ":"));
    if (segs.length === 0)
        return null;
    let lead;
    if (drive) {
        const L = escapeRe(drive[1]);
        lead = `(?:${SEP}{2}[?.]${SEP})?(?:${L}:|/${L}|/mnt/${L})`;
    }
    else if (/^[\\/]{2}/.test(root))
        lead = `${SEP}{2}`;
    else
        lead = "";
    const unc = !drive && lead !== "";
    // a leading separator is a single literal so unanchored scans never rescan a run of slashes
    const body = segs.map((x, i) => (i === 0 ? (unc ? "" : drive ? `${SEP}+` : SEP) : `${SEP}+`) + escapeRe(x)).join("");
    // not followed by more name characters (proj2, proj.bak) but a sentence period is fine
    const end = "(?![A-Za-z0-9_-]|\\.[A-Za-z0-9_])";
    return new RegExp(`(?<![A-Za-z0-9_.~>-])${lead}${body}${end}${TAIL}`, isWindowsRoot(root) ? "gi" : "g");
}
function mapPath(p, repoRoot, homeDir) {
    const q = stripExtended(p);
    if (!isAbsolute(q))
        return null;
    const variants = [q];
    const msys = /^\/(?:mnt\/)?([A-Za-z])(?=\/|$)/.exec(q);
    if (msys && (isDriveRoot(repoRoot) || isDriveRoot(homeDir)))
        variants.push(`${msys[1]}:${q.slice(msys[0].length)}`);
    for (const v of variants) {
        const repoRel = relInside(v, repoRoot);
        if (repoRel)
            return ["<repo>", ...repoRel].join("/");
        const homeRel = relInside(v, homeDir);
        if (homeRel)
            return ["~", ...homeRel].join("/");
    }
    return null;
}
// `X:\Users\name`, `/mnt/x/Users/name`, `/c/Users/name`, `/Users/name`, `/home/name`, `\\wsl$\distro\home\name`
const USER_DIR = new RegExp(`(?<![A-Za-z0-9_.~>-])(?:[A-Za-z]:${SEP}+Users|/mnt/[A-Za-z]/Users|/[A-Za-z]/Users|${SEP}Users|${SEP}home|${SEP}{2}wsl\\$${SEP}+[^\\\\/\\s"']+${SEP}+home)${SEP}+([^\\\\/\\s"'\`;&|<>()]+)${TAIL}`, "gi");
function identityOf(homeDir) {
    const segs = normRoot(homeDir).split(/[\\/]+/).filter((x) => x !== "");
    const last = segs.length > 0 ? segs[segs.length - 1] : "";
    if (last.length < 3 || last.toLowerCase() === "root" || /^[A-Za-z]:$/.test(last))
        return { user: null, word: null, short: null };
    const shortBase = last.replace(/[\s.]/g, "").slice(0, 6);
    return {
        user: last.toLowerCase(),
        word: new RegExp(`(?<![A-Za-z0-9])${escapeRe(last)}(?![A-Za-z0-9])`, "gi"),
        short: new RegExp(`(?<![A-Za-z0-9_])${escapeRe(shortBase)}~\\d(?![A-Za-z0-9_])`, "gi"),
    };
}
function scrubIdentity(s, id) {
    let out = s.replace(USER_DIR, (_m, name, tail) => {
        return (name.toLowerCase() === id.user ? "~" : "~other") + normTail(tail);
    });
    if (id.short)
        out = out.replace(id.short, "<user>");
    if (id.word)
        out = out.replace(id.word, "<user>");
    return out;
}
function replaceRoots(text, repoRoot, homeDir) {
    let s = stripExtended(text);
    const rules = [
        [rootPattern(normRoot(repoRoot)), "<repo>"],
        [rootPattern(normRoot(homeDir)), "~"],
    ];
    for (const [re, label] of rules) {
        if (!re)
            continue;
        s = s.replace(re, (_m, tail) => label + normTail(tail));
    }
    return s;
}
/** `<abs>/<basename>` for an absolute path; a bare root (`/`, `C:\`) becomes `<abs>`. */
function absBasename(p) {
    const segs = p.split(/[\\/]+/).filter((x) => x !== "" && !/^[A-Za-z]:$/.test(x));
    return segs.length === 0 ? "<abs>" : `<abs>/${segs[segs.length - 1]}`;
}
/** Returns an anonymised copy: secrets redacted, repo root -> `<repo>`, home -> `~`, usernames scrubbed, caps applied. */
export function anonymizeEvent(e, opts) {
    const repo = normRoot(opts.repoRoot);
    const home = normRoot(opts.homeDir);
    const id = identityOf(opts.homeDir);
    const text = (s) => scrubIdentity(replaceRoots(redactSecrets(s), repo, home), id);
    const pathLike = (p) => {
        const safe = redactSecrets(p);
        const mapped = mapPath(safe, repo, home);
        if (mapped !== null)
            return scrubIdentity(mapped, id);
        const out = scrubIdentity(replaceRoots(safe, repo, home), id);
        // Any other absolute path (outside the session root and every home) keeps only its basename.
        return isAbsolute(stripExtended(out)) ? absBasename(out) : out;
    };
    const out = { ...e };
    if (out.tool !== undefined)
        out.tool = redactSecrets(out.tool);
    if (out.notifyType !== undefined)
        out.notifyType = redactSecrets(out.notifyType);
    if (out.path !== undefined)
        out.path = pathLike(out.path);
    if (out.cwd !== undefined)
        out.cwd = pathLike(out.cwd);
    if (out.command !== undefined)
        out.command = cap(text(out.command), MAX_RECORDED_COMMAND);
    if (out.summary !== undefined)
        out.summary = cap(text(out.summary), MAX_RECORDED_SUMMARY);
    if (out.title !== undefined)
        out.title = cap(text(out.title), MAX_RECORDED_TITLE);
    if (out.plan !== undefined) {
        out.plan = { ...out.plan, steps: out.plan.steps.map((s) => ({ ...s, text: clipChars(text(s.text), MAX_RECORDED_STEP) })) };
    }
    return out;
}
// An absolute path token in free text: at the start, or after a blank, quote, `=` or `(`.
const ABS_IN_TEXT = /(?<=^|[\s"'=(])(?:[A-Za-z]:[\\/]|[\\/])[^\s"'`;&|<>()]*/g;
/**
 * For text that leaves the machine (the AI prompt): the home directory becomes `~`, any other
 * user directory `~other`, the user name `<user>`, and every other absolute path keeps only its
 * basename (`\u2026/<basename>`); a bare root (`/`, `C:\`) stays. Relative paths are unchanged.
 */
export function shortenAbsolutePaths(text, homeDir) {
    const s = scrubIdentity(replaceRoots(text, "", normRoot(homeDir)), identityOf(homeDir));
    return s.replace(ABS_IN_TEXT, (p) => {
        const segs = p.split(/[\\/]+/).filter((x) => x !== "" && !/^[A-Za-z]:$/.test(x));
        return segs.length === 0 ? p : `\u2026/${segs[segs.length - 1]}`;
    });
}
