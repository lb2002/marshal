// The CLI commands (cli.ts dispatches here for everything except `hook`).
import { randomBytes, randomInt } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { homedir, tmpdir } from "node:os";
import { stableCliFile } from "./runtime-home.js";
import { dirname, join, resolve } from "node:path";
import { createInterface } from "node:readline";
import { CODE_TTL_MS, startServer, type ServerHandle, type ServerOptions, type UiView } from "./server.js";
import { openBrowser, shouldOpenBrowser } from "./open-browser.js";
import { TOOL_NAME } from "./install-global.js";
import { Recorder, readRecording } from "./recorder.js";
import { replay } from "./replay.js";
import { buildPack, readOverridesFile } from "./pack.js";
import { UsageError, parseArgs, parseBudget, parsePackArgs } from "./cli-args.js";
import { Explainer, claudeCliRunner, explainMode, findClaude } from "./explain.js";
import { normalizeStatus, type AgentEvent } from "./events.js";
import { bridgeCommand, type ClaudeTransport } from "./install.js";
import { HOOK_TOOLS, TRUST_STEP } from "./install-tools.js";
import { installHooks, uninstallHooks, uninstallPlan, type HookPlace, type InstallResult, type InstallTool } from "./install-run.js";
import { type BannerQr, connectBanner, hostFlag, isAllMode, museStopCleanup, parseConnectAllArgs, parseConnectArgs, parseDisconnectArgs, temporaryCleanup, type GlobalToolArgs } from "./connect.js";
import { connectAll, disconnectAll, type GlobalOpts } from "./install-global.js";
import { DEADLINE_MS, httpPort, processStart, readStdin, sendBounded } from "./hook-cli.js";
import { hookToken, readTokenFile, tokenFile } from "./token-file.js";
import { haltMarkerPath } from "./halt-marker.js";
import { RELEASE_WORDS } from "./halt.js";
import { haltBlocksRemoval } from "./halt-guard.js";
import { canonJson, entryChanges, formatChanges } from "./settings-diff.js";
import { PRODUCT_NAME } from "./product.js";
import { chooseHost, defaultRouteAddress, hostFirst, lanAddresses, pairingQrLines, type HostChoice } from "./pair-qr.js";
import { encodeQr, renderedWidth } from "./qr.js";
import { QrScreen } from "./qr-screen.js";

const HTTP_PORT = 47820;
const WS_PORT = 47821;

const USAGE = `Usage:
  lotse connect [--all] [--tool claude|muse|codex|gemini|all] [--yes] [--keep-muse] [--budget 15] [--explain cli|off] [--no-qr] [--no-browser]
  lotse connect --project <dir> [--tool claude|muse|codex|gemini] [--transport command|http] [--temporary] [--budget 15] [--explain cli|off] [--no-qr] [--no-browser]
  lotse disconnect [--all] [--tool claude|muse|codex|gemini|all] [--yes]
  lotse start [--record <file> [--force]] [--budget 15] [--explain cli|off] [--no-qr] [--no-browser]
  lotse demo <recording.ndjson> [--speed 1] [--root <dir>] [--budget 15] [--explain cli|off] [--no-qr]
  lotse pack <recording.ndjson> -o <pack.json> [--overrides <file>] [--budget 15] [--explain cli|off]
  lotse statusline                      (reads statusline JSON on stdin)
  lotse install-hooks --project <dir> [--tool claude|muse|codex|gemini] [--transport command|http] [--force]
  lotse uninstall-hooks --project <dir> [--tool claude|muse|codex|gemini] [--yes]
  lotse hook [--source claude|muse|codex|gemini]  (command hook; reads the hook JSON on stdin)
`;

/** LOTSE_TOKEN (trimmed, empty counts as unset), else ~/.lotse/token (created on first use). */
function loadOrCreateToken(): { token: string; fromEnv: boolean } {
  const env = process.env.LOTSE_TOKEN?.trim();
  if (env) return { token: env, fromEnv: true };
  const existing = readTokenFile();
  if (existing) return { token: existing, fromEnv: false };
  const file = tokenFile();
  mkdirSync(dirname(file), { recursive: true, mode: 0o700 });
  const t = randomBytes(32).toString("hex");
  try {
    writeFileSync(file, t + "\n", { flag: "wx", mode: 0o600 });
    return { token: t, fromEnv: false };
  } catch (err) {
    if ((err as NodeJS.ErrnoException).code !== "EEXIST") throw err;
    const other = readTokenFile();
    if (!other) throw err;
    return { token: other, fromEnv: false };
  }
}

function newCode(): string {
  return String(randomInt(0, 1_000_000)).padStart(6, "0");
}

/** The pairing view: addresses to show (the QR code's host first), the host choice, and the QR code per code. */
interface PairingView {
  ips: string[];
  choice: HostChoice;
  qrFor: (code: string) => string[];
  /** Why no QR code is shown although one was wanted on a terminal; null otherwise. */
  noQrWhy: string | null;
}

/**
 * The addresses to show and the QR code for a pairing code. The QR code goes only to a terminal wide enough for
 * it (never into a pipe or a log file), and not with `--no-qr`. `host`: the `--host` override, already checked.
 */
async function pairingView(noQr: boolean, host?: string): Promise<PairingView> {
  const all = lanAddresses();
  const choice = chooseHost(all, host === undefined && all.length > 1 ? await defaultRouteAddress() : null, host);
  const term = { isTTY: process.stdout.isTTY === true, columns: process.stdout.columns };
  const qrFor = (code: string) => pairingQrLines({ host: choice.host, port: WS_PORT, code, term, disabled: noQr });
  let noQrWhy: string | null = null;
  if (!noQr && term.isTTY) {
    if (choice.host === null) noQrWhy = choice.why ?? "no address for it";
    else {
      const width = renderedWidth(encodeQr(`marshal://pair?v=1&h=${choice.host}&p=${WS_PORT}&c=000000`));
      if (term.columns === undefined || term.columns < width) noQrWhy = `the terminal is narrower than the code (${width} columns); widen it and press Enter`;
    }
  }
  return { ips: hostFirst([...(host === undefined ? [] : [host]), ...all.map((a) => a.address)], choice.host), choice, qrFor, noQrWhy };
}

/** The address lines: the QR code's host marked when the QR code is shown and there is more than one address. */
function addressNote(view: PairingView, ip: string, qrShown: boolean): string {
  if (!qrShown || ip !== view.choice.host || view.ips.length < 2) return "";
  return "   <- in the QR code";
}

/** One screen per process: it watches stdout and stderr, so a QR block is only erased while it is the last output. */
let screenOnce: QrScreen | null = null;
function screen(): QrScreen {
  if (screenOnce === null) {
    screenOnce = new QrScreen(process.stdout);
    screenOnce.watch(process.stdout);
    screenOnce.watch(process.stderr);
  }
  return screenOnce;
}

/**
 * The life of the shown code on the terminal: shown (its QR block printed last), then used, expired or replaced
 * (the block erased if it is still the last output, and one line saying the code above is no longer valid).
 */
interface PairingDisplay {
  show(code: string): void;
  used(): void;
  replace(code: string, echoedLines: number): void;
}

function pairingDisplay(view: PairingView): PairingDisplay {
  const sc = screen();
  let timer: NodeJS.Timeout | null = null;
  const stopTimer = () => {
    if (timer !== null) clearTimeout(timer);
    timer = null;
  };
  const d: PairingDisplay = {
    show(code) {
      sc.shown(view.qrFor(code));
      stopTimer();
      timer = setTimeout(() => sc.invalidate("expired"), CODE_TTL_MS);
      timer.unref();
    },
    used() {
      stopTimer();
      sc.invalidate("used");
    },
    replace(code, echoed) {
      stopTimer();
      sc.invalidate("replaced", echoed);
      console.log(`New pairing code: ${code}  (single-use, expires after 5 minutes)`);
      d.show(code);
    },
  };
  return d;
}

/** A late-bound `onCodeUsed` for startServer (the display exists only after the server is up). */
function codeUsedHook(): { fire: () => void; set: (f: () => void) => void } {
  let f: () => void = () => {};
  return { fire: () => f(), set: (g) => (f = g) };
}

/** This build's version (package.json next to dist/, also in the plugin); "unknown" when it cannot be read. */
function bridgeVersion(): string {
  try {
    const v = (JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version?: unknown }).version;
    return typeof v === "string" ? v : "unknown";
  } catch {
    return "unknown";
  }
}

/**
 * The local status page of a live bridge (server.ts `ui`, ui-page.ts): a fresh 32-hex-character key per process, the
 * view the server adds to its own state, and a late-bound hook for a code issued on the page (it is shown on the
 * terminal as on Enter).
 */
interface StatusPage {
  url: string;
  options: Pick<ServerOptions, "ui" | "onCodeReplaced">;
  /** The addresses once pairingView has chosen them. */
  setPairing(view: PairingView): void;
  /** What the page does with a new code once the terminal display exists. */
  onReplaced(f: (code: string) => void): void;
  /** Prints the URL line and opens the browser unless `--no-browser`, no terminal or CI. */
  announce(noBrowser: boolean): void;
  /** Opens the browser (after the terminal output, so the page finds a complete state). */
  open(): void;
}

function statusPage(explainOn: boolean, tools?: { name: string; status: string }[]): StatusPage {
  const key = randomBytes(16).toString("hex");
  const url = `http://127.0.0.1:${HTTP_PORT}/ui?k=${key}`;
  const version = bridgeVersion();
  let addresses: string[] = [];
  let qrHost: string | null = null;
  let replaced: (code: string) => void = () => {};
  let wantOpen = false;
  const view = (): UiView => ({ version, addresses, qrHost, explain: explainOn ? "on" : "off", ...(tools ? { tools } : {}) });
  return {
    url,
    options: { ui: { key, view }, onCodeReplaced: (code) => replaced(code) },
    setPairing(v) {
      addresses = v.ips;
      qrHost = v.choice.host;
    },
    onReplaced(f) {
      replaced = f;
    },
    announce(noBrowser) {
      console.log(`Open in your browser: ${url}`);
      wantOpen = shouldOpenBrowser({ noBrowser, isTTY: process.stdout.isTTY === true, env: process.env });
    },
    open() {
      if (wantOpen) openBrowser(url);
    },
  };
}

async function printConnectInfo(code: string, fromEnv: boolean, noQr: boolean, host: string | undefined, before: string[] = [], page?: StatusPage): Promise<PairingDisplay> {
  const view = await pairingView(noQr, host);
  page?.setPairing(view);
  const qrShown = view.qrFor(code).length > 0;
  console.log(`Pairing code: ${code}  (single-use, expires after 5 minutes)`);
  if (view.ips.length === 0) console.log(`No LAN IPv4 address found. WebSocket port: ${WS_PORT}`);
  for (const ip of view.ips) console.log(`Quest: ws://${ip}:${WS_PORT}${addressNote(view, ip, qrShown)}`);
  console.log("Press Enter for a fresh pairing code.");
  if (fromEnv) {
    console.log("Hook token: taken from LOTSE_TOKEN in this shell. Hooks of sessions without LOTSE_TOKEN (Claude desktop app) read ~/.lotse/token: it must hold the same value.");
  } else {
    console.log(`Hook token: ${tokenFile()}. Command hooks (the default) read it themselves.`);
    console.log(`Only http hooks (--transport http) need it in the shell that runs claude (PowerShell: $env:LOTSE_TOKEN = (Get-Content "$HOME/.lotse/token").Trim() ; bash: export LOTSE_TOKEN="$(cat ~/.lotse/token)")`);
  }
  for (const l of before) console.log(l);
  if (view.noQrWhy !== null) console.log(`No QR code: ${view.noQrWhy}.`);
  if (qrShown) console.log(`In the headset: open ${PRODUCT_NAME} and look at this code.`);
  const display = pairingDisplay(view);
  display.show(code); // the QR block is the last output
  return display;
}

/**
 * Enter on stdin prints a fresh single-use code (the previous one stops working): the old QR block is erased if it
 * is still the last output, a line says the old code is invalid, and the new code gets its own QR block.
 */
function newCodeOnEnter(handle: ServerHandle, display: PairingDisplay): void {
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (chunk: string) => {
    // The terminal echoed what was typed: that is output after the QR block too.
    const echoed = process.stdin.isTTY === true ? (chunk.match(/\n/g) ?? []).length : 0;
    // `continue` (or `weiter`) releases a halt (the server reads it itself while halted); it is not a request for a new code.
    if (!chunk.includes("\n") || RELEASE_WORDS.has(chunk.trim())) {
      if (echoed > 0) screen().noteOutput();
      return;
    }
    display.replace(handle.newPairingCode(), echoed);
  });
  process.stdin.on("error", () => {
    /* no usable stdin: Enter just does nothing */
  });
  // After the `connect --all` question, readline left stdin explicitly paused; a new listener alone would not resume it.
  process.stdin.resume();
}

/** `--explain cli|off` (default cli when claude is found on PATH, else off). `print` shows the active mode. */
function resolveExplain(v: string | true | undefined): { explainer?: Explainer; print: () => void } {
  if (v === true) throw new UsageError("--explain must be cli or off");
  const bin = findClaude();
  let mode: "cli" | "off";
  try {
    mode = explainMode(v, bin);
  } catch (err) {
    throw new UsageError((err as Error).message);
  }
  if (mode === "off" || bin === null) return { print: () => console.log("Explanation AI: off") };
  return { explainer: new Explainer(claudeCliRunner(bin)), print: () => console.log("Explanation AI: on (claude -p, haiku)") };
}

const UNINSTALL_REMINDER = `Hooks stay active in projects where you ran install-hooks. Run \`lotse uninstall-hooks --project <dir>\` when you stop using ${PRODUCT_NAME}.`;

/** Runs cleanup, then always closes the server; exits 1 when either step fails. Handles every stop signal. */
function onShutdown(handle: ServerHandle, cleanup: () => Promise<void>, reminder: boolean): void {
  let started = false;
  const run = async (): Promise<void> => {
    if (started) return;
    started = true;
    let code = 0;
    try {
      await cleanup();
    } catch (err) {
      console.error(`lotse: shutdown error: ${(err as Error).message}`);
      code = 1;
    } finally {
      try {
        await handle.close();
      } catch (err) {
        console.error(`lotse: server close error: ${(err as Error).message}`);
        code = 1;
      }
    }
    if (reminder) console.log(UNINSTALL_REMINDER);
    process.exit(code);
  };
  for (const sig of ["SIGINT", "SIGTERM", "SIGHUP", "SIGBREAK"] as const) process.on(sig, () => void run());
}

/** The bridge's own console, for releasing a halt with `continue` or `weiter` (only a TTY counts; server.ts). */
function haltConsole(): { input: NodeJS.ReadableStream; isTTY: boolean } {
  return { input: process.stdin, isTTY: process.stdin.isTTY === true };
}

/** The live bridge as `start` runs it (fixed ports, 90 s hold, the token, the optional explainer). */
async function startLive(
  budget: number,
  explainer: Explainer | undefined,
  token: string,
  code: string,
  onEvent?: (e: AgentEvent) => void,
  onCodeUsed?: () => void,
  page?: StatusPage,
): Promise<ServerHandle> {
  return startServer({
    ...page?.options,
    onCodeUsed,
    httpPort: HTTP_PORT,
    wsPort: WS_PORT,
    pairingCode: code,
    holdMs: 90_000,
    budgetUsd: budget,
    ingestToken: token,
    explainer,
    onEvent,
    console: haltConsole(),
    haltMarker: haltMarkerPath(homedir()),
  });
}

async function cmdStart(args: string[]): Promise<void> {
  const { pos, flags } = parseArgs(args, ["record", "budget", "explain", "host"], ["force", "no-qr", "no-browser"]);
  if (pos.length > 0) throw new UsageError(`unexpected argument ${pos[0]}`);
  const budget = parseBudget(flags.budget);
  const host = hostFlag(flags.host);
  const recFile = typeof flags.record === "string" ? resolve(flags.record) : null;
  if (flags.force === true && !recFile) throw new UsageError("--force only applies to --record");
  if (recFile && existsSync(recFile) && flags.force !== true) {
    throw new Error(`${recFile} already exists; pass --force to overwrite it`);
  }
  const explain = resolveExplain(flags.explain);
  const { token, fromEnv } = loadOrCreateToken();
  let recorder: Recorder | null = null;
  const code = newCode();
  const used = codeUsedHook();
  const page = statusPage(explain.explainer !== undefined);
  const handle = await startLive(budget, explain.explainer, token, code, (e) => recorder?.write(e), used.fire, page);
  // The recording file is only opened once the ports are bound.
  try {
    if (recFile) recorder = new Recorder(recFile, { repoRoot: process.cwd(), homeDir: homedir() });
  } catch (err) {
    await handle.close();
    throw err;
  }
  console.log(`${PRODUCT_NAME} bridge listening (HTTP 127.0.0.1:${HTTP_PORT}, WS 0.0.0.0:${WS_PORT})`);
  page.announce(flags["no-browser"] === true);
  explain.print();
  const display = await printConnectInfo(code, fromEnv, flags["no-qr"] === true, host, recFile ? [`Recording to ${recFile}`] : [], page);
  used.set(() => display.used());
  page.onReplaced((c) => display.replace(c, 0));
  newCodeOnEnter(handle, display);
  page.open();
  onShutdown(handle, async () => {
    explain.explainer?.abortAll();
    await recorder?.close();
  }, true);
}

/**
 * `connect`: installs the hooks (idempotent; foreign hooks are still refused), then starts the bridge as
 * `start` does and prints one block for the headset. `--temporary` removes the hooks again on Ctrl+C.
 */
async function cmdConnect(args: string[]): Promise<number> {
  if (isAllMode(args)) return cmdConnectAll(args);
  const a = parseConnectArgs(args);
  const explain = resolveExplain(a.explain);
  const place: HookPlace = { dir: resolve(a.project), tool: a.tool, bridgeCmd: bridgeCmd(), homeDir: homedir() };
  const installed = installAndReport(place, a.transport, false);
  if (!installed.ok) return 1;
  const cleanup = temporaryCleanup(place, installed);
  /** Removes the hooks (only if this run installed or changed them) and reports it. */
  const removeIfTemporary = (): boolean => {
    if (!a.temporary) return true;
    const r = cleanup();
    if (r === null) {
      console.log("The hooks were there before and stay.");
      return true;
    }
    if (!r.ok) console.error(`lotse: ${r.error}`);
    else console.log(r.file);
    return r.ok;
  };
  await runConnected({
    budget: a.budget,
    explain,
    noQr: a.noQr,
    noBrowser: a.noBrowser,
    host: a.host,
    tools: [{ name: TOOL_NAME[a.tool], status: installed.changed ? "hooks installed in this project" : "hooks already in this project" }],
    banner: (code, ips, qr) => connectBanner({ code, ips, wsPort: WS_PORT, project: place.dir, tool: a.tool, temporary: a.temporary, ...qr }),
    onStartFailed: () => void removeIfTemporary(),
    onStop: async () => {
      if (!removeIfTemporary()) throw new Error("could not remove the hooks");
    },
    reminder: !a.temporary,
  });
  return 0;
}

/**
 * The bridge as `connect` runs it: start, the token warning, one block for the headset, Enter for a new
 * code, and the shutdown handling. Shared by `connect --project` and `connect --all`.
 */
async function runConnected(o: {
  budget: number;
  explain: { explainer?: Explainer; print: () => void };
  noQr: boolean;
  noBrowser: boolean;
  host?: string;
  /** What this run set up, for the status page. */
  tools: { name: string; status: string }[];
  banner: (code: string, ips: string[], qr: BannerQr) => string;
  onStartFailed?: () => void | Promise<void>;
  onStop?: () => Promise<void>;
  reminder: boolean;
}): Promise<void> {
  const { token, fromEnv } = loadOrCreateToken();
  const code = newCode();
  const used = codeUsedHook();
  const page = statusPage(o.explain.explainer !== undefined, o.tools);
  let handle: ServerHandle;
  try {
    handle = await startLive(o.budget, o.explain.explainer, token, code, undefined, used.fire, page);
  } catch (err) {
    try {
      await o.onStartFailed?.();
    } catch (cleanup) {
      console.error(`lotse: ${(cleanup as Error).message}`);
    }
    throw err;
  }
  console.log(`${PRODUCT_NAME} bridge listening (HTTP 127.0.0.1:${HTTP_PORT}, WS 0.0.0.0:${WS_PORT})`);
  page.announce(o.noBrowser);
  o.explain.print();
  // Hooks of sessions without LOTSE_TOKEN (the Claude desktop app) read the token file.
  if (fromEnv && readTokenFile() !== token) {
    console.log("Warning: LOTSE_TOKEN in this shell is not the content of ~/.lotse/token. Hooks without LOTSE_TOKEN (Claude desktop app) would be refused.");
  }
  const view = await pairingView(o.noQr, o.host);
  page.setPairing(view);
  const qrShown = view.qrFor(code).length > 0;
  console.log("");
  console.log(o.banner(code, view.ips, { qrShown, qrHost: qrShown && view.ips.length > 1 ? view.choice.host : null, noQrWhy: view.noQrWhy }));
  const display = pairingDisplay(view);
  display.show(code); // the QR block goes below the frame, as the last output
  used.set(() => display.used());
  page.onReplaced((c) => display.replace(c, 0));
  newCodeOnEnter(handle, display);
  page.open();
  onShutdown(
    handle,
    async () => {
      o.explain.explainer?.abortAll();
      await o.onStop?.();
    },
    o.reminder,
  );
}

/** One question on the terminal; "" when stdin ends first. */
function askOnce(question: string): Promise<string> {
  return new Promise((resolveAnswer) => {
    const rl = createInterface({ input: process.stdin, output: process.stdout, terminal: false });
    let done = false;
    const finish = (answer: string) => {
      if (done) return;
      done = true;
      rl.close();
      resolveAnswer(answer);
    };
    rl.once("line", finish);
    rl.once("close", () => finish(""));
    process.stdout.write(question);
  });
}

/** What both global commands share; the real home and env are read here and nowhere else. */
function globalOpts(a: GlobalToolArgs, yes: boolean): GlobalOpts {
  return { ...a, home: homedir(), env: process.env, bridgeCmd: bridgeCmd(), yes, interactive: process.stdin.isTTY === true, ask: askOnce, log: (l) => console.log(l), err: (l) => console.error(l) };
}

/**
 * `connect` (no `--project`) and `connect --all`: lists what it would install for every agent found, installs the
 * hooks in the user-level files of the chosen tools (after one question on a terminal, or `--yes`; without a
 * terminal and without `--yes` nothing changes), then starts the bridge exactly as `connect` does. The hooks stay
 * until `disconnect`.
 */
async function cmdConnectAll(args: string[]): Promise<number> {
  const a = parseConnectAllArgs(args);
  const explain = resolveExplain(a.explain);
  console.log(`${PRODUCT_NAME} setup: hooks for every agent found on this computer, in its user settings, so every project and every session reaches the headset.`);
  const r = await connectAll(globalOpts(a, a.yes));
  if (!r.ok) return 1;
  const removeMuse = museStopCleanup(a.keepMuse, r.tools);
  if (removeMuse) console.log("The Muse hooks are removed again on exit (to keep them: --keep-muse).");
  /** Removes the Muse plugin again (stop, or a bridge that cannot start): Muse would otherwise deny everything. */
  const removeMusePlugin = async (): Promise<void> => {
    const done = await disconnectAll(globalOpts({ tools: ["muse"], explicit: true }, true));
    if (!done.ok) throw new Error("could not remove the Muse hooks");
  };
  await runConnected({
    budget: a.budget,
    explain,
    noQr: a.noQr,
    noBrowser: a.noBrowser,
    host: a.host,
    tools: [
      ...r.tools.map((t) => ({ name: TOOL_NAME[t], status: t === "muse" && removeMuse ? "hooks installed until you stop" : "hooks installed" })),
      ...(r.museSkipped ? [{ name: TOOL_NAME.muse, status: "connect it per project" }] : []),
    ],
    banner: (code, ips, qr) => connectBanner({ code, ips, wsPort: WS_PORT, project: "", tool: r.tools[0] ?? "claude", temporary: false, global: r.tools, museSkipped: r.museSkipped, ...qr }),
    onStartFailed: removeMuse ? removeMusePlugin : undefined,
    onStop: removeMuse ? removeMusePlugin : undefined,
    reminder: false,
  });
  return 0;
}

/** `disconnect` (`--all` optional): removes exactly Lotse's entries from the user-level files and lists them. */
async function cmdDisconnect(args: string[]): Promise<number> {
  const a = parseDisconnectArgs(args);
  if (await refusedWhileHalted()) return 1;
  return (await disconnectAll(globalOpts(a, a.yes))).ok ? 0 : 1;
}

/** Removing hooks while halted is refused (with or without --yes); prints why. true: refused. */
async function refusedWhileHalted(): Promise<boolean> {
  const refusal = await haltBlocksRemoval(homedir(), httpPort(process.env), hookToken(process.env));
  if (refusal !== null) console.error(`lotse: ${refusal}`);
  return refusal !== null;
}

async function cmdDemo(args: string[]): Promise<void> {
  const { pos, flags } = parseArgs(args, ["speed", "root", "budget", "explain", "host"], ["no-qr"]);
  if (pos.length !== 1) throw new UsageError("demo needs exactly one recording file");
  const speed = flags.speed === undefined ? 1 : Number(flags.speed);
  if (!(speed > 0) || !Number.isFinite(speed)) throw new UsageError("--speed must be > 0");
  const budget = parseBudget(flags.budget);
  const events = await readRecording(resolve(pos[0]));
  const explain = resolveExplain(flags.explain);
  const host = hostFlag(flags.host);
  const { token, fromEnv } = loadOrCreateToken();
  const code = newCode();
  const used = codeUsedHook();
  let cancel: (() => void) | null = null;
  // A recording must never crash the bridge: a bad event is skipped with one line.
  const emit = (e: AgentEvent) => {
    try {
      handle.inject(e);
    } catch (err) {
      console.error(`lotse: replay event skipped: ${(err as Error).message}`);
    }
  };
  const handle: ServerHandle = await startServer({
    httpPort: HTTP_PORT,
    wsPort: WS_PORT,
    pairingCode: code,
    holdMs: 90_000,
    budgetUsd: budget,
    ingestToken: token,
    explainer: explain.explainer,
    console: haltConsole(),
    haltMarker: haltMarkerPath(homedir()),
    demoRoot: typeof flags.root === "string" ? resolve(flags.root) : process.cwd(),
    onCodeUsed: used.fire,
    // The replay starts only once the headset has paired, so the jury sees it from the first event.
    onPaired: () => {
      if (cancel) return;
      console.log("Quest paired, replay starting.");
      cancel = replay(events, emit, { speed });
    },
  });
  console.log(`${PRODUCT_NAME} demo: ${events.length} events from ${pos[0]} at speed ${speed}`);
  explain.print();
  const display = await printConnectInfo(code, fromEnv, flags["no-qr"] === true, host, ["Waiting for the Quest to pair ..."]);
  used.set(() => display.used());
  newCodeOnEnter(handle, display);
  onShutdown(
    handle,
    async () => {
      cancel?.();
      explain.explainer?.abortAll();
    },
    false,
  );
}

/** Offline demo pack for the Quest app: the server message stream of a recording plus allow/deny consequences. */
async function cmdPack(argv: string[]): Promise<void> {
  const args = parsePackArgs(argv);
  const events = await readRecording(resolve(args.recording));
  const inputs = args.overrides !== undefined ? readOverridesFile(args.overrides) : undefined;
  const explain = resolveExplain(args.explain);
  explain.print();
  // Classification needs a real directory for <repo>; it never appears in the pack.
  const demoRoot = realpathSync(mkdtempSync(join(tmpdir(), "lotse-pack-")));
  try {
    const pack = await buildPack(events, {
      budgetUsd: args.budget,
      demoRoot,
      explainer: explain.explainer,
      overrides: inputs?.overrides,
      agents: inputs?.agents,
      replies: inputs?.replies,
      onProgress: explain.explainer ? (done, total) => console.log(`Explanation ${done}/${total}`) : undefined,
    });
    const out = resolve(args.out);
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, JSON.stringify(pack, null, 2) + "\n");
    console.log(`Pack: ${pack.messages.length} messages, ${Object.keys(pack.perms).length} permission requests, ${Math.round(pack.durationMs / 1000)} s -> ${out}`);
  } finally {
    rmSync(demoRoot, { recursive: true, force: true });
  }
}

/** Never throws, never exits non-zero: it must not break Claude's status line. */
async function cmdStatusline(): Promise<void> {
  let line = PRODUCT_NAME;
  try {
    const deadline = processStart() + DEADLINE_MS;
    const raw = await readStdin(deadline);
    try {
      const ev = normalizeStatus(JSON.parse(raw), Date.now());
      if (ev && ev.ctxPct !== undefined && ev.costUsd !== undefined) {
        line = `${PRODUCT_NAME} · ${Math.round(ev.ctxPct)}% · $${ev.costUsd.toFixed(2)}`;
      }
    } catch {
      /* not JSON: print the bare name */
    }
    await sendBounded("/status", raw, deadline);
  } catch {
    /* swallow */
  }
  console.log(line);
  process.exit(0);
}

/** Absolute path of the entry `cli.js`; hooks run `"<node>" "<it>" hook …`. Set by main(). */
let cliFile = "";

function bridgeCmd(): string {
  return bridgeCommand(cliFile);
}

/** `--project` (required), `--tool` (default claude). */
function hookPlace(flags: Record<string, string | true>): HookPlace {
  if (typeof flags.project !== "string") throw new UsageError("--project <dir> is required");
  const t = flags.tool ?? "claude";
  if (t !== "claude" && !(typeof t === "string" && (HOOK_TOOLS as readonly string[]).includes(t))) {
    throw new UsageError("--tool must be claude, muse, codex or gemini");
  }
  return { dir: resolve(flags.project), tool: t as InstallTool, bridgeCmd: bridgeCmd(), homeDir: homedir() };
}

/** `--transport command|http` (Claude only), default command. */
function transportFlag(flags: Record<string, string | true>, tool: InstallTool): ClaudeTransport {
  const t = flags.transport ?? "command";
  if (t !== "command" && t !== "http") throw new UsageError("--transport must be command or http");
  if (t === "http" && tool !== "claude") throw new UsageError("--transport http is only for --tool claude");
  return t;
}

/** Installs (idempotently) and prints what happened. */
function installAndReport(place: HookPlace, transport: ClaudeTransport, force: boolean): InstallResult {
  const r = installHooks(place, transport, force);
  if (!r.ok) {
    console.error(`lotse: ${r.error}`);
    return r;
  }
  console.log(r.changed ? r.file : `${r.file}: ${PRODUCT_NAME} hooks already installed`);
  if (place.tool !== "claude") console.log(`One-time step: ${TRUST_STEP[place.tool]}`);
  return r;
}

function cmdInstallHooks(args: string[]): number {
  const { pos, flags } = parseArgs(args, ["project", "tool", "transport"], ["force"]);
  if (pos.length > 0) throw new UsageError(`unexpected argument ${pos[0]}`);
  const place = hookPlace(flags);
  return installAndReport(place, transportFlag(flags, place.tool), flags.force === true).ok ? 0 : 1;
}

/** Removes the hooks and prints what happened; returns the exit code. */
function uninstallAndReport(place: HookPlace): number {
  const r = uninstallHooks(place);
  if (!r.ok) {
    console.error(`lotse: ${r.error}`);
    return 1;
  }
  if (!r.existed) console.log(`${r.file} does not exist; nothing to remove`);
  else if (!r.changed) console.log(`${r.file}: no ${PRODUCT_NAME} hooks found; file left unchanged`);
  else console.log(r.file);
  return 0;
}

/**
 * `uninstall-hooks`: refused while halted; lists what it removes and asks once on a terminal (`--yes` skips the
 * question; no terminal and no --yes: nothing changes, exit 1), then removes exactly Marshal's hooks.
 */
async function cmdUninstallHooks(args: string[]): Promise<number> {
  const { pos, flags } = parseArgs(args, ["project", "tool"], ["yes"]);
  if (pos.length > 0) throw new UsageError(`unexpected argument ${pos[0]}`);
  const place = hookPlace(flags);
  if (await refusedWhileHalted()) return 1;
  const plan = uninstallPlan(place);
  if (!plan.ok) {
    console.error(`lotse: ${plan.error}`);
    return 1;
  }
  const changes = plan.before === null ? [] : entryChanges(plan.before, plan.after);
  if (changes.length > 0 && flags.yes !== true) {
    console.log(`${PRODUCT_NAME} removes:`);
    console.log(`  ${plan.file}`);
    for (const l of formatChanges(changes)) console.log(l);
    if (process.stdin.isTTY !== true) {
      console.log("No terminal to ask on, so nothing was changed. To remove them without the question: add --yes to uninstall-hooks");
      return 1;
    }
    if (!/^(j|ja|y|yes)$/i.test((await askOnce(`Remove ${PRODUCT_NAME} hooks from ${plan.file} ? [y/N] `)).trim())) {
      console.log("Cancelled. Nothing changed.");
      return 1;
    }
    const again = uninstallPlan(place);
    if (!again.ok || canonJson(again.before) !== canonJson(plan.before)) {
      console.error(`lotse: ${plan.file}: changed during the question, nothing written`);
      return 1;
    }
  }
  return uninstallAndReport(place);
}

/** Every command except `hook` (see hook-cli.ts). `entry` is the path of cli.js, for the hook commands. */
export async function main(argv: string[], entry: string): Promise<void> {
  // `npx marshal-agents` with no command is the one-command setup.
  const [cmd, ...rest] = argv.length === 0 ? ["connect"] : argv;
  // Hooks call the CLI by path: never point them into npx's cache (runtime-home.ts).
  cliFile = cmd === "connect" || cmd === "disconnect" || cmd === "install-hooks" || cmd === "uninstall-hooks" ? stableCliFile(entry, homedir()) : entry;
  try {
    if (cmd === "start") return await cmdStart(rest);
    if (cmd === "connect") {
      process.exitCode = await cmdConnect(rest);
      return;
    }
    if (cmd === "disconnect") {
      process.exitCode = await cmdDisconnect(rest);
      return;
    }
    if (cmd === "demo") return await cmdDemo(rest);
    if (cmd === "pack") return await cmdPack(rest);
    if (cmd === "statusline") {
      parseArgs(rest, [], []);
      return await cmdStatusline();
    }
    if (cmd === "install-hooks") {
      process.exitCode = cmdInstallHooks(rest);
      return;
    }
    if (cmd === "uninstall-hooks") {
      process.exitCode = await cmdUninstallHooks(rest);
      return;
    }
    throw new UsageError(cmd ? `unknown command ${cmd}` : "no command");
  } catch (err) {
    if (err instanceof UsageError) {
      console.error(`lotse: ${err.message}\n\n${USAGE}`);
      process.exit(2);
    }
    console.error(`lotse: ${(err as Error).message}`);
    process.exit(1);
  }
}

