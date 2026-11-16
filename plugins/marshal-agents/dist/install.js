import { closeSync, constants as fsConstants, existsSync, fstatSync, ftruncateSync, lstatSync, mkdirSync, openSync, realpathSync, statSync, writeSync } from "node:fs";
import { join, resolve } from "node:path";
/**
 * The command prefix of every installed hook: the absolute node (a hook environment may have a trimmed PATH, and
 * Muse clears it to an allowlist) and the absolute cli.js, both quoted, with forward slashes.
 */
export function bridgeCommand(cliFile, nodePath = process.execPath) {
    const fwd = (p) => p.replace(/\\/g, "/");
    return `"${fwd(nodePath)}" "${fwd(cliFile)}"`;
}
/** Earlier spellings of the same command (`node "<cli.js>"`), which still count as Lotse's own when removing. */
export function legacyBridgeCmds(bridgeCmd) {
    const m = /^"[^"]+" "([^"]+)"$/.exec(bridgeCmd);
    return m ? [`node "${m[1]}"`] : [];
}
const EV_URL = "http://127.0.0.1:47820/ev";
const PERM_URL = "http://127.0.0.1:47820/perm";
const EVENTS = [
    "SessionStart",
    "UserPromptSubmit",
    "PreToolUse",
    "PostToolUse",
    "PostToolUseFailure",
    "Notification",
    "Stop",
    "StopFailure",
    "SubagentStart",
    "SubagentStop",
    "PreCompact",
    "SessionEnd",
];
function httpHook(url, extra = {}) {
    // The token is referenced by env var name only; it never lands in a settings file.
    return { type: "http", url, headers: { "X-Lotse-Token": "$LOTSE_TOKEN" }, allowedEnvVars: ["LOTSE_TOKEN"], ...extra };
}
/** The Claude shim command; PermissionRequest waits up to 120 s, and no decision prints nothing (normal dialog). */
const claudeShim = (bridgeCmd) => `${bridgeCmd} hook --source claude`;
/** The approval hook says so itself, so the shim knows before reading stdin (long stdin deadline). */
const claudeApprovalShim = (bridgeCmd) => `${claudeShim(bridgeCmd)} --event PermissionRequest`;
/** The pre-tool hook names its event, so a slow or failing run still checks the halt marker (hook-cli.ts). */
const claudePreToolShim = (bridgeCmd) => `${claudeShim(bridgeCmd)} --event PreToolUse`;
/** The Stop hook also waits for a reply from the headset (see replies.ts). */
const claudeStopListener = (bridgeCmd) => `${claudeShim(bridgeCmd)} --event Stop --listen`;
/**
 * Claude Code docs (hooks): `asyncRewake` "runs in the background and wakes Claude on exit code 2"; the
 * hook's stderr is shown to Claude as a system reminder. Claude Code still enforces `timeout` (seconds) on
 * an asyncRewake hook, so the listener ends before it (hook-cli.ts).
 */
export const STOP_LISTEN_TIMEOUT_S = 900;
export function buildHookSettings(bridgeCmd, transport = "command") {
    const hooks = {};
    if (transport === "command") {
        for (const name of EVENTS)
            hooks[name] = [{ hooks: [{ type: "command", command: claudeShim(bridgeCmd) }] }];
        hooks.PermissionRequest = [{ hooks: [{ type: "command", command: claudeApprovalShim(bridgeCmd), timeout: 120 }] }];
        hooks.PreToolUse = [{ hooks: [{ type: "command", command: claudePreToolShim(bridgeCmd) }] }];
        // Both background flags: the docs allow either; with both the session can never block, whichever a
        // Claude Code version requires. The listener ends on its own before the timeout (hook-cli.ts).
        hooks.Stop = [{ hooks: [{ type: "command", command: claudeStopListener(bridgeCmd), async: true, asyncRewake: true, timeout: STOP_LISTEN_TIMEOUT_S }] }];
    }
    else {
        for (const name of EVENTS)
            hooks[name] = [{ hooks: [httpHook(EV_URL)] }];
        // A halted bridge refuses a pre-tool hook from this header alone, before reading the body (halt.ts).
        hooks.PreToolUse = [{ hooks: [httpHook(EV_URL, { headers: { "X-Lotse-Token": "$LOTSE_TOKEN", "X-Lotse-Hook": "PreToolUse" } })] }];
        // SessionStart does not support http hooks: it goes through the silent `hook` subcommand.
        hooks.SessionStart = [{ hooks: [{ type: "command", command: `${bridgeCmd} hook` }] }];
        hooks.PermissionRequest = [{ hooks: [httpHook(PERM_URL, { timeout: 120 })] }];
    }
    return { hooks, statusLine: { type: "command", command: `${bridgeCmd} statusline` } };
}
export function mergeIntoProjectSettings(existing, hooks, force) {
    const base = (existing ?? {});
    if (!force && ("hooks" in base || "statusLine" in base))
        return { error: "hooks-exist" };
    return { settings: { ...base, ...hooks } };
}
const realFs = {
    canon(p) {
        try {
            return realpathSync.native(p);
        }
        catch {
            return null;
        }
    },
    isSymlink(p) {
        try {
            return lstatSync(p).isSymbolicLink();
        }
        catch {
            return false;
        }
    },
    id(p) {
        try {
            const s = statSync(p, { bigint: true });
            return `${s.dev}:${s.ino}`;
        }
        catch {
            return null;
        }
    },
};
/** Comparable form of a canonical path: \\?\ prefix stripped, optionally case-folded. */
function fold(c, caseInsensitive) {
    const r = resolve(c.replace(/^\\\\\?\\/, ""));
    return caseInsensitive ? r.toLowerCase() : r;
}
/** Claude Code: <project>/.claude/settings.json; never ~/.claude or CLAUDE_CONFIG_DIR. */
export function claudeTarget(configDir = process.env.CLAUDE_CONFIG_DIR) {
    return {
        sub: ".claude",
        file: "settings.json",
        userDirs: (homeDir) => {
            const dirs = [{ dir: join(homeDir, ".claude"), label: "~/.claude" }];
            if (configDir)
                dirs.push({ dir: resolve(configDir), label: "CLAUDE_CONFIG_DIR" });
            return dirs;
        },
    };
}
/**
 * Returns an error message when `dir` must not receive hooks, else null. `dir` is already absolute.
 * Refuses: UNC/device paths (Windows), the home directory (by real path and by file identity),
 * a .claude that is a link or is ~/.claude (real path or identity), a settings.json that is a
 * symlink or is ~/.claude/settings.json (identity). The same .claude and settings.json checks apply to
 * CLAUDE_CONFIG_DIR when it is set. Fails closed when a path cannot be resolved.
 */
export function checkProjectDir(dir, homeDir, exists, fs = realFs, windows = process.platform === "win32", configDir = process.env.CLAUDE_CONFIG_DIR) {
    return checkTargetDir(dir, homeDir, exists, claudeTarget(configDir), fs, windows);
}
/** checkProjectDir for any tool: the same checks with `target.sub`, `target.file` and `target.userDirs`. */
export function checkTargetDir(dir, homeDir, exists, target, fs = realFs, windows = process.platform === "win32") {
    const HOME = `refusing to install into the home directory (that would write ~/${target.sub}/${target.file})`;
    if (windows && resolve(dir).startsWith("\\")) {
        return "refusing UNC or device path (cannot verify it is not the home directory)";
    }
    if (!exists(dir))
        return `project directory does not exist: ${dir}`;
    const cd = fs.canon(dir);
    const ch = fs.canon(homeDir);
    if (cd === null || ch === null)
        return "refusing: cannot resolve the project or home directory to check for the home directory";
    if (fold(cd, windows) === fold(ch, windows))
        return HOME;
    const id = (p) => fs.id?.(p) ?? null;
    const sameId = (a, b) => {
        const x = id(a);
        return x !== null && x === id(b);
    };
    if (sameId(dir, homeDir))
        return HOME;
    const toolDir = join(dir, target.sub);
    const userDirs = target.userDirs(homeDir);
    if (exists(toolDir)) {
        if (fs.isSymlink(toolDir))
            return `refusing: <project>/${target.sub} is a link`;
        const cc = fs.canon(toolDir);
        if (cc === null)
            return `refusing: cannot resolve <project>/${target.sub}`;
        for (const u of userDirs) {
            if (!exists(u.dir))
                continue;
            const cu = fs.canon(u.dir);
            if (cu === null)
                return `refusing: cannot resolve ${u.label}`;
            if (fold(cc, windows) === fold(cu, windows) || sameId(toolDir, u.dir))
                return `refusing: <project>/${target.sub} is ${u.label}`;
        }
    }
    const file = join(toolDir, target.file);
    if (fs.isSymlink(file))
        return `refusing: <project>/${target.sub}/${target.file} is a symlink`;
    for (const u of userDirs) {
        const userFile = join(u.dir, target.file);
        if (exists(file) && exists(userFile) && sameId(file, userFile)) {
            return `refusing: <project>/${target.sub}/${target.file} is ${u.label}/${target.file}`;
        }
    }
    return null;
}
const realExists = (p) => existsSync(p);
/** Writes `text` to <dir>/.claude/settings.json (see writeTargetFile). Returns an error or null. */
export function writeProjectSettings(dir, homeDir, text) {
    return writeTargetFile(dir, homeDir, text, claudeTarget());
}
/**
 * Writes `text` to <dir>/<target.sub>/<target.file>. Repeats the safety check right before writing,
 * requires the tool folder to be a real directory, and opens the file without truncating so its identity
 * can be compared with every user-level file of that tool before any byte is destroyed. Returns an error or null.
 */
export function writeTargetFile(dir, homeDir, text, target) {
    const toolDir = join(dir, target.sub);
    const file = join(toolDir, target.file);
    if (!realExists(dir) || !statSync(dir).isDirectory())
        return `project directory does not exist: ${dir}`;
    const early = checkTargetDir(dir, homeDir, realExists, target);
    if (early)
        return early;
    mkdirSync(toolDir, { recursive: true });
    const again = checkTargetDir(dir, homeDir, realExists, target);
    if (again)
        return again;
    const st = lstatSync(toolDir);
    if (st.isSymbolicLink() || !st.isDirectory())
        return `refusing: <project>/${target.sub} is not a real directory`;
    const flags = fsConstants.O_WRONLY | fsConstants.O_CREAT | (fsConstants.O_NOFOLLOW ?? 0);
    const fd = openSync(file, flags, 0o644);
    try {
        const mine = fstatSync(fd, { bigint: true });
        for (const u of target.userDirs(homeDir)) {
            try {
                const h = statSync(join(u.dir, target.file), { bigint: true });
                if (h.dev === mine.dev && h.ino === mine.ino)
                    return `refusing: <project>/${target.sub}/${target.file} is ${u.label}/${target.file}`;
            }
            catch {
                /* that file does not exist: nothing to collide with */
            }
        }
        ftruncateSync(fd, 0);
        writeSync(fd, text, 0, "utf8");
    }
    finally {
        closeSync(fd);
    }
    return null;
}
const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
/** Parses an existing settings file: strips a UTF-8 BOM, requires a plain JSON object. */
export function parseSettingsText(text) {
    let v;
    try {
        v = JSON.parse(text.replace(/^\uFEFF/, ""));
    }
    catch (err) {
        return { error: `not valid JSON (${err.message})` };
    }
    if (!isObj(v))
        return { error: "not a JSON object" };
    return { value: v };
}
/**
 * Ownership is exact: only what buildHookSettings emits for this bridgeCmd (either transport) counts as ours,
 * also in its earlier `node "<cli.js>"` spelling.
 */
function isLotseHook(h, bridgeCmd) {
    return [bridgeCmd, ...legacyBridgeCmds(bridgeCmd)].some((cmd) => isLotseHookOf(h, cmd));
}
function isLotseHookOf(h, bridgeCmd) {
    if (!isObj(h))
        return false;
    if (h.type === "http") {
        return (h.url === EV_URL || h.url === PERM_URL) && isObj(h.headers) && "X-Lotse-Token" in h.headers;
    }
    if (h.type === "command") {
        return (h.command === `${bridgeCmd} hook` ||
            h.command === claudeShim(bridgeCmd) ||
            h.command === claudeApprovalShim(bridgeCmd) ||
            h.command === claudePreToolShim(bridgeCmd) ||
            h.command === claudeStopListener(bridgeCmd) ||
            h.command === `${bridgeCmd} statusline`);
    }
    return false;
}
/** Removes only what buildHookSettings installs; every other key, hook and statusLine stays. */
export function removeLotseHooks(existing, bridgeCmd) {
    const out = removeHooksWhere(existing, (h) => isLotseHook(h, bridgeCmd));
    if (isObj(out.statusLine) && isLotseHook(out.statusLine, bridgeCmd))
        delete out.statusLine;
    return out;
}
/** Copy of `existing` without the hook handlers `ours` matches; emptied groups and events go too. */
export function removeHooksWhere(existing, ours) {
    const out = { ...existing };
    if (isObj(out.hooks)) {
        const hooks = {};
        for (const [event, groups] of Object.entries(out.hooks)) {
            if (!Array.isArray(groups)) {
                hooks[event] = groups;
                continue;
            }
            const kept = groups
                .map((g) => (isObj(g) && Array.isArray(g.hooks) ? { ...g, hooks: g.hooks.filter((h) => !ours(h)) } : g))
                .filter((g) => !(isObj(g) && Array.isArray(g.hooks) && g.hooks.length === 0));
            if (kept.length > 0)
                hooks[event] = kept;
        }
        if (Object.keys(hooks).length > 0)
            out.hooks = hooks;
        else
            delete out.hooks;
    }
    return out;
}
