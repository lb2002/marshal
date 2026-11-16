// Muse for all sessions: a user-scope Muse plugin named `lotse` that holds only hook capabilities.
//
// Why not the `hooks` block of ~/.config/muse/settings.json: Muse has no offline check for it, and per the Muse
// docs "a malformed user settings file fails settings validation", which would break every Muse session. A
// plugin bundle, in contrast, is checked by Muse itself, offline (`plugins validate`), and only installed after
// it passes. Its hooks then wait for the user's one-time `muse plugins approve lotse` (status review_needed).
//
// The bundle lives in Lotse's own folder (~/.lotse/muse-plugin); Muse copies it into its plugin cache on install.
// Muse is driven through the versioned `muse-bin-*.exe` (never `muse.cmd`, which updates itself), with
// `plugins validate|install|update|list|remove --json` only: no agent session is ever started.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { MUSE_EVENTS } from "./install-tools.js";
import { PRODUCT_NAME } from "./product.js";
export const MUSE_PLUGIN_ID = "lotse";
export const MUSE_NO_GLOBAL = "Muse: no valid global hook location found. Please connect Muse per project (lotse connect --project … --tool muse)";
const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
/** Where Lotse keeps the bundle it installs (its own folder, not Muse's). */
export const musePluginDir = (home) => join(home, ".lotse", "muse-plugin");
/** The newest versioned Muse binary: `LOTSE_MUSE_BIN`, else `%LOCALAPPDATA%\Programs\muse\muse-bin-*.exe`; null if none. */
export function findMuseBin(env) {
    if (env.LOTSE_MUSE_BIN)
        return existsSync(env.LOTSE_MUSE_BIN) ? env.LOTSE_MUSE_BIN : null;
    if (!env.LOCALAPPDATA)
        return null;
    const dir = join(env.LOCALAPPDATA, "Programs", "muse");
    try {
        const bins = readdirSync(dir)
            .filter((f) => /^muse-bin-[\w.-]+\.exe$/i.test(f))
            .sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
        return bins.length > 0 ? join(dir, bins[bins.length - 1]) : null;
    }
    catch {
        return null;
    }
}
/** `"<node>" "<cli.js>"` (or the older `node "<cli.js>"`) → the node and the cli.js the hooks run. */
function commandParts(bridgeCmd) {
    const m = /^(?:"([^"]+)"|(node)) "([^"]+)"$/.exec(bridgeCmd);
    if (!m)
        throw new Error(`unexpected bridge command: ${bridgeCmd}`);
    return { node: m[1] ?? m[2], cli: m[3] };
}
/** The plugin manifest: one hook capability per Muse event, each running the shim (the approval one with `--event`). */
export function musePluginManifest(bridgeCmd) {
    const { node, cli } = commandParts(bridgeCmd);
    return {
        schemaVersion: 1,
        name: MUSE_PLUGIN_ID,
        version: "0.1.0",
        description: `${PRODUCT_NAME}: shows this Muse session on the Quest headset and lets the headset answer permission requests.`,
        compat: { manifestDir: ".muse-plugin" },
        capabilities: {
            hooks: MUSE_EVENTS.map((event) => ({
                id: event.replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase(),
                event,
                command: [node, cli, "hook", "--source", "muse", ...(event === "PermissionRequest" || event === "PreToolUse" ? ["--event", event] : [])],
            })),
        },
    };
}
export const manifestText = (bridgeCmd) => JSON.stringify(musePluginManifest(bridgeCmd), null, 2) + "\n";
/** Writes a bundle (just the manifest) into `dir`. */
export function writeBundle(dir, text) {
    mkdirSync(join(dir, ".muse-plugin"), { recursive: true });
    writeFileSync(join(dir, ".muse-plugin", "plugin.json"), text);
}
/** The manifest text of the bundle Lotse installed last, or null. */
export function installedBundleText(home) {
    try {
        return readFileSync(join(musePluginDir(home), ".muse-plugin", "plugin.json"), "utf8");
    }
    catch {
        return null;
    }
}
/** Comparable form of a path Muse prints (`\\?\` prefix, slashes, case on Windows). */
export function samePath(a, b) {
    if (a === null)
        return false;
    const norm = (p) => {
        const r = resolve(p.replace(/^\\\\\?\\/, ""));
        return process.platform === "win32" ? r.toLowerCase() : r;
    };
    return norm(a) === norm(b);
}
/** One Muse call: its JSON and whether it failed (a non-zero exit, a timeout, or an `error` field). */
function json(bin, args, env) {
    let out;
    let failure = null;
    // A `.mjs`/`.js` LOTSE_MUSE_BIN is a stand-in run with node (tests).
    const [file, argv] = /\.m?js$/i.test(bin) ? [process.execPath, [bin, ...args, "--json"]] : [bin, [...args, "--json"]];
    try {
        out = execFileSync(file, argv, { env, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 60_000, windowsHide: true });
    }
    catch (err) {
        // A refused command still prints its JSON on stdout; the exit code alone already means failure.
        const e = err;
        out = String(e.stdout ?? "");
        failure = e.status !== null && e.status !== undefined ? `exit code ${e.status}` : `${e.signal ?? "no exit code"} (${e.message.split(/\r?\n/)[0]})`;
        if (out.trim() === "")
            return { v: {}, failure: `muse ${args[0]} ${args[1] ?? ""} failed: ${failure}` };
    }
    const start = out.indexOf("{");
    const end = out.lastIndexOf("}");
    let v;
    try {
        v = JSON.parse(start >= 0 && end > start ? out.slice(start, end + 1) : out);
    }
    catch {
        return { v: {}, failure: `muse ${args[1]}: unreadable output${failure ? ` (${failure})` : ""}` };
    }
    if (!isObj(v))
        return { v: {}, failure: `muse ${args[1]}: unexpected output` };
    const message = errorMessage(v);
    return { v, failure: message ?? (failure ? `muse ${args[1]} failed: ${failure}` : null) };
}
const errorMessage = (v) => (isObj(v.error) ? String(v.error.message ?? "error") : null);
/** The real runner, or null when no versioned Muse binary is installed. */
export function realMuseRunner(env) {
    const bin = findMuseBin(env);
    if (bin === null)
        return null;
    const run = (args) => {
        const { v, failure } = json(bin, ["plugins", ...args], env);
        if (failure)
            throw new Error(failure);
        return v;
    };
    return {
        validate(dir) {
            const { v, failure } = json(bin, ["plugins", "validate", dir], env);
            return v.valid === true && failure === null ? { valid: true } : { valid: false, message: failure ?? "invalid plugin bundle" };
        },
        install: (dir) => void run(["install", dir, "--scope", "user"]),
        update: (id) => void run(["update", id]),
        remove: (id) => void run(["remove", id]),
        list() {
            const v = run(["list"]);
            const plugins = Array.isArray(v.plugins) ? v.plugins : [];
            return plugins.flatMap((p) => {
                const rec = isObj(p) && isObj(p.record) ? p.record : isObj(p) ? p : undefined;
                if (!rec || typeof rec.id !== "string")
                    return [];
                const src = isObj(rec.source) && typeof rec.source.path === "string" ? rec.source.path : null;
                return [{ id: rec.id, source: src }];
            });
        },
    };
}
/** Validates a manifest text with Muse in a fresh temp bundle (offline), then removes the temp folder. */
export function validateInTemp(runner, text) {
    const dir = mkdtempSync(join(tmpdir(), "lotse-muse-plugin-"));
    try {
        writeBundle(dir, text);
        return runner.validate(dir);
    }
    catch (err) {
        return { valid: false, message: err.message };
    }
    finally {
        rmSync(dir, { recursive: true, force: true });
    }
}
