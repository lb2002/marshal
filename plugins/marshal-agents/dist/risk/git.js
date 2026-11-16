import { destructive, NETWORK, worst } from "./common.js";
/** Config keys whose value git executes or that load further config. Compared lowercase. */
const RISKY_KEY_PREFIXES = [
    "alias.", "pager.", "filter.", "protocol.", "includeif.", "include.path", "credential.", "gpg.",
    "core.sshcommand", "core.pager", "core.fsmonitor", "core.hookspath", "core.editor", "core.askpass", "core.gitproxy",
    "diff.external", "sequence.editor", "uploadpack.packobjectshook", "interactive.difffilter", "core.alternaterefscommand",
    "sendemail.smtpserver",
];
const CONFIG_REASON = "Git setting runs commands";
const LOSE = "Discards local changes";
// git send-email options that neither run a command nor point at a server.
const SEND_EMAIL_SAFE_OPTS = new Set([
    "--to", "--cc", "--bcc", "--from", "--subject", "--in-reply-to", "--reply-to", "--annotate", "--no-annotate",
    "--compose", "--dry-run", "--numbered", "--no-numbered", "-n", "--confirm", "--quiet", "--thread", "--no-thread",
]);
export function riskyGitKey(key) {
    const k = key.trim().toLowerCase();
    if (RISKY_KEY_PREFIXES.some((p) => k.startsWith(p)))
        return true;
    if (k.startsWith("diff.") && (k.endsWith(".textconv") || k.endsWith(".command")))
        return true;
    if (k.startsWith("merge.") && k.endsWith(".driver"))
        return true;
    if (k.startsWith("remote.") && (k.endsWith(".uploadpack") || k.endsWith(".receivepack")))
        return true;
    if ((k.startsWith("difftool.") || k.startsWith("mergetool.")) && k.endsWith(".cmd"))
        return true;
    if ((k.startsWith("man.") || k.startsWith("browser.")) && (k.endsWith(".cmd") || k.endsWith(".path")))
        return true;
    if (k.startsWith("sendemail.") && (k.endsWith(".smtpserver") || k.endsWith("cmd")))
        return true;
    return false;
}
/** `--name` matches when the given option name is a prefix (git accepts unambiguous abbreviations). */
function longOpt(arg, ...names) {
    if (!arg.startsWith("--") || arg.length < 3)
        return false;
    const given = arg.slice(2).split("=")[0].toLowerCase();
    return names.some((n) => n.startsWith(given));
}
function riskyConfig(kv) {
    const eq = kv.indexOf("=");
    const key = eq < 0 ? kv : kv.slice(0, eq);
    return riskyGitKey(key) || (eq >= 0 && kv.slice(eq + 1).trimStart().startsWith("!"));
}
/** `-c k=v`, `--config k=v`, `--config-env k=ENV`, `--exec-path=dir` anywhere in the arguments. */
function hasRiskyGlobal(args) {
    for (let i = 0; i < args.length; i++) {
        const a = args[i];
        if (a === "-c" || a === "--config" || a === "--config-env") {
            if (args[i + 1] !== undefined && riskyConfig(args[i + 1]))
                return true;
        }
        else if ((a.startsWith("--config=") || a.startsWith("--config-env=")) && riskyConfig(a.slice(a.indexOf("=") + 1)))
            return true;
        else if (a.startsWith("-c") && !a.startsWith("--") && a.length > 2 && riskyConfig(a.slice(2)))
            return true;
        else if (a.startsWith("--exec-path="))
            return true;
    }
    return false;
}
/** Values of `-x cmd`, `-xcmd`, `-ix cmd`, `--exec cmd`, `--exec=cmd` (and abbreviations; long name configurable). */
function execValues(rest, long = "exec") {
    const out = [];
    for (let i = 0; i < rest.length; i++) {
        const a = rest[i];
        if (a.startsWith("--")) {
            if (a.length > 2 && longOpt(a, long)) {
                const eq = a.indexOf("=");
                out.push(eq >= 0 ? a.slice(eq + 1) : rest[++i] ?? "");
            }
            continue;
        }
        if (a.startsWith("-") && a.length > 1) {
            const x = a.indexOf("x", 1);
            if (x > 0)
                out.push(x + 1 < a.length ? a.slice(x + 1) : rest[++i] ?? "");
        }
    }
    return out;
}
export function classifyGit(args, rec) {
    if (hasRiskyGlobal(args))
        return destructive(CONFIG_REASON);
    let i = 0;
    while (i < args.length && args[i].startsWith("-")) {
        i += /^(-C|-c|--git-dir|--work-tree|--namespace|--exec-path|--config-env|--config)$/.test(args[i]) ? 2 : 1;
    }
    const sub = args[i]?.toLowerCase();
    const rest = args.slice(i + 1);
    const has = (re) => rest.some((a) => re.test(a));
    const long = (...names) => rest.some((a) => longOpt(a, ...names));
    const shortFlag = (ch) => rest.some((a) => /^-[A-Za-z]+$/.test(a) && a.includes(ch));
    /** Classify option values that git runs as commands; null when there are none. */
    const commands = (cmds) => {
        let r = null;
        for (const cmd of cmds) {
            const c = rec.text(cmd);
            if (c.risk === "destructive")
                return c;
            r = r ? worst(r, c) : c;
        }
        return r;
    };
    switch (sub) {
        case "push":
            if (long("receive-pack", "exec"))
                return destructive(CONFIG_REASON);
            if (long("force", "force-with-lease", "force-if-includes", "mirror", "delete", "prune") || shortFlag("f") || shortFlag("d") || has(/^\+/) || has(/^:/) || has(/:\+/)) {
                return destructive("Forced push (overwrites history)");
            }
            return NETWORK;
        case "reset":
            return long("hard", "merge", "keep") ? destructive(LOSE) : null;
        case "clean":
            return has(/^(--dry-run|-n)$/) && !long("force") && !shortFlag("f") ? null : destructive("Deletes files not in Git");
        case "checkout": {
            if (has(/^(--|\.|:\/)$/) || long("force", "merge", "ours", "theirs") || shortFlag("f") || shortFlag("m"))
                return destructive(LOSE);
            const creates = shortFlag("b") || shortFlag("B") || long("orphan", "track", "detach");
            const positionals = rest.filter((a) => !a.startsWith("-"));
            return !creates && positionals.length >= 2 ? destructive(LOSE) : null; // <tree-ish> <path>
        }
        case "restore":
            return rest.includes("--staged") && !long("worktree") && !shortFlag("W") ? null : destructive(LOSE);
        case "switch":
            return long("discard-changes", "force") || shortFlag("f") ? destructive(LOSE) : null;
        case "rm":
            return has(/^--cached$/) && !long("force") && !shortFlag("f") ? null : destructive("Deletes files");
        case "branch":
            return shortFlag("D") || (long("delete") && long("force")) || (shortFlag("d") && shortFlag("f"))
                ? destructive("Deletes a branch for good")
                : null;
        case "stash":
            return rest[0] === "drop" || rest[0] === "clear" ? destructive("Deletes stashed changes") : null;
        case "reflog":
            return rest[0] === "expire" || rest[0] === "delete" ? destructive("Deletes history") : null;
        case "gc":
        case "prune":
        case "filter-branch":
        case "filter-repo":
        case "update-ref":
        case "replace":
            return destructive("Changes history for good");
        case "worktree":
            return rest[0] === "remove" || rest[0] === "prune" ? destructive("Deletes a working folder") : null;
        case "rebase":
            return commands(execValues(rest));
        case "difftool":
            return commands(execValues(rest, "extcmd"));
        case "grep": {
            // -O<pager> / --open-files-in-pager=<pager> (optional value, glued only)
            const pagers = [];
            for (const a of rest) {
                if (a.startsWith("--")) {
                    if (longOpt(a, "open-files-in-pager") && a.includes("="))
                        pagers.push(a.slice(a.indexOf("=") + 1));
                }
                else if (a.startsWith("-") && a.indexOf("O") > 0 && a.indexOf("O") + 1 < a.length)
                    pagers.push(a.slice(a.indexOf("O") + 1));
            }
            return commands(pagers);
        }
        case "send-email": {
            // Getopt::Long accepts prefix abbreviations and single-dash long names, so option parsing is
            // not reliable. Any option outside this known-harmless set may run a command or set the server.
            for (const a of rest) {
                if (!a.startsWith("-"))
                    continue; // patch files / addresses
                const key = a.split("=")[0];
                if (!SEND_EMAIL_SAFE_OPTS.has(key))
                    return destructive(CONFIG_REASON);
            }
            return NETWORK;
        }
        case "bisect": {
            const run = rest.indexOf("run");
            return run >= 0 ? rec.argv(i + 1 + run + 1) : null;
        }
        case "submodule": {
            const f = rest.indexOf("foreach");
            return f >= 0 ? rec.text(rest.slice(f + 1).filter((a) => !longOpt(a, "recursive")).join(" ")) : NETWORK;
        }
        case "config": {
            const read = has(/^(--get|--list|-l$|--show|--name-only)/);
            if (read)
                return null;
            if (rest.some((a) => !a.startsWith("-") && riskyGitKey(a)) || rest.some((a) => a.trimStart().startsWith("!")))
                return destructive(CONFIG_REASON);
            const writes = rest.filter((a) => !a.startsWith("-")).length >= 2 || has(/^--(add|unset|replace-all|edit|remove-section|rename-section)/) || shortFlag("e");
            return writes ? NETWORK : null;
        }
        case "clone":
        case "ls-remote":
            if (shortFlag("u"))
                return destructive(CONFIG_REASON); // -u <upload-pack>
        // falls through
        case "pull":
        case "fetch":
            if (long("upload-pack", "exec"))
                return destructive(CONFIG_REASON);
            return NETWORK;
        case "archive":
            if (long("exec"))
                return destructive(CONFIG_REASON);
            return long("remote") ? NETWORK : null;
        case "remote":
        case "lfs":
            return NETWORK;
        default:
            return null;
    }
}
