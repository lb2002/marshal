// `lotse connect`: the one command for a real session. Without `--project` (or with `--all`) it installs the hooks
// in the user-level files of every agent it finds, after one question (install-global.ts); with `--project <dir>`
// it installs one tool's hooks in that project. Then it starts the bridge like `start` and prints one block with
// everything the headset needs, with the pairing QR code on a terminal (pair-qr.ts).
// The CLI (cli.ts) does the I/O; this file holds the parts that can be tested on their own.
import { UsageError, parseArgs, parseBudget } from "./cli-args.js";
import { HOOK_TOOLS } from "./install-tools.js";
import { uninstallHooks } from "./install-run.js";
import { GLOBAL_TOOLS, TOOL_NAME } from "./install-global.js";
import { PRODUCT_NAME } from "./product.js";
import { parseHostFlag } from "./pair-qr.js";
/** `--host`: a literal private IPv4 address, or a usage error; undefined when not given. */
export function hostFlag(v) {
    if (v === undefined)
        return undefined;
    if (v === true)
        throw new UsageError("--host needs an address");
    try {
        return parseHostFlag(v);
    }
    catch (err) {
        throw new UsageError(err.message);
    }
}
export function parseConnectArgs(args) {
    const { pos, flags } = parseArgs(args, ["project", "tool", "transport", "budget", "explain", "host"], ["temporary", "no-qr", "no-browser"]);
    if (pos.length > 0)
        throw new UsageError(`unexpected argument ${pos[0]}`);
    if (typeof flags.project !== "string")
        throw new UsageError("connect needs --project <dir>");
    const tool = flags.tool ?? "claude";
    if (tool !== "claude" && !(typeof tool === "string" && HOOK_TOOLS.includes(tool))) {
        throw new UsageError("--tool must be claude, muse, codex or gemini");
    }
    const transport = flags.transport ?? "command";
    if (transport !== "command" && transport !== "http")
        throw new UsageError("--transport must be command or http");
    if (transport === "http" && tool !== "claude")
        throw new UsageError("--transport http is only for --tool claude");
    const out = { project: flags.project, tool: tool, transport, temporary: flags.temporary === true, budget: parseBudget(flags.budget), noQr: flags["no-qr"] === true, noBrowser: flags["no-browser"] === true };
    if (flags.explain !== undefined)
        out.explain = flags.explain;
    const host = hostFlag(flags.host);
    if (host !== undefined)
        out.host = host;
    return out;
}
/** The global mode: `--all`, or no `--project` at all (`lotse connect` / `lotse disconnect` with no flags). */
export const isAllMode = (args) => args.includes("--all") || !args.includes("--project");
/** `--tool claude|muse|codex|gemini|all`, default all. */
function globalTools(tool) {
    const t = tool ?? "all";
    if (t === "all")
        return { tools: [...GLOBAL_TOOLS], explicit: false };
    if (typeof t !== "string" || !GLOBAL_TOOLS.includes(t))
        throw new UsageError("--tool must be claude, muse, codex, gemini or all");
    return { tools: [t], explicit: true };
}
export function parseConnectAllArgs(args) {
    const { pos, flags } = parseArgs(args, ["tool", "budget", "explain", "host"], ["all", "yes", "keep-muse", "no-qr", "no-browser"]);
    if (pos.length > 0)
        throw new UsageError(`unexpected argument ${pos[0]}`);
    const out = { ...globalTools(flags.tool), yes: flags.yes === true, keepMuse: flags["keep-muse"] === true, budget: parseBudget(flags.budget), noQr: flags["no-qr"] === true, noBrowser: flags["no-browser"] === true };
    if (flags.explain !== undefined)
        out.explain = flags.explain;
    const host = hostFlag(flags.host);
    if (host !== undefined)
        out.host = host;
    return out;
}
/**
 * Whether `connect --all` removes the Muse user hooks when it stops: Muse denies every approval while the
 * bridge is off, so the hooks go with the bridge unless `--keep-muse` (a crash leaves them: deny is safe).
 */
export function museStopCleanup(keepMuse, installed) {
    return !keepMuse && installed.includes("muse");
}
/** `disconnect` (`--all` optional): the user-level hooks; one project's hooks go with `uninstall-hooks`. */
export function parseDisconnectArgs(args) {
    if (!isAllMode(args))
        throw new UsageError("disconnect removes the user-level hooks; for one project: uninstall-hooks --project <dir>");
    const { pos, flags } = parseArgs(args, ["tool"], ["all", "yes"]);
    if (pos.length > 0)
        throw new UsageError(`unexpected argument ${pos[0]}`);
    return { ...globalTools(flags.tool), yes: flags.yes === true };
}
const RULE = "=".repeat(64);
/** The one line under the ready headline when the terminal shows the QR code. */
export const HEADSET_LOOK = `   In the headset: open ${PRODUCT_NAME} and look at this code.`;
/** The one block `connect` prints: code, addresses, and what to do in the headset. */
export function connectBanner(o) {
    const lines = [
        RULE,
        "",
        `   ${PRODUCT_NAME.toUpperCase()} IS READY`,
        "",
        o.qrShown === true ? HEADSET_LOOK : "   In the headset: Connect to my PC",
        "",
        `   Code:      ${o.code}`,
    ];
    if (o.ips.length === 0)
        lines.push(`   Address:   No network address found (Port ${o.wsPort})`);
    o.ips.forEach((ip, i) => lines.push(`   ${i === 0 ? "Address:" : "        "}   ${ip}   (Port ${o.wsPort})${o.qrHost === ip ? "   <- in the QR code" : ""}`));
    if (o.noQrWhy)
        lines.push(`   No QR code: ${o.noQrWhy}.`);
    if (o.qrShown === true)
        lines.push("   The QR code is below this frame.");
    lines.push("", "   The code works once, for 5 minutes. Enter prints a new one.", ...(o.global
        ? [
            `   Global hooks for: ${o.global.length > 0 ? o.global.map((t) => TOOL_NAME[t]).join(", ") : "none"}`,
            ...(o.museSkipped ? ["   Muse: not global, connect it per project (lotse connect --project … --tool muse)"] : []),
            "   The hooks stay; switch them off with: lotse disconnect --all",
        ]
        : [
            `   Hooks for ${TOOL_NAME[o.tool]} in: ${o.project}`,
            o.temporary ? "   The hooks are removed on Ctrl+C." : "   The hooks stay; remove them with uninstall-hooks.",
        ]), "", RULE);
    return lines.join("\n");
}
/**
 * `--temporary`: what to run on exit. It removes the hooks only when this run installed or changed them;
 * hooks that were already there exactly so stay. Returns the uninstall result, or null when nothing was done.
 */
export function temporaryCleanup(place, installed) {
    return () => (installed.ok && installed.changed ? uninstallHooks(place) : null);
}
