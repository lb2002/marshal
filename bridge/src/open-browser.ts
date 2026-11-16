// Opens the local status page (ui-page.ts) in the system browser, once, when `connect` or `start` begins.
// Only a URL of the plain form http://127.0.0.1:<port>/ui?k=<hex> is ever passed on, so nothing in it can be read
// by a shell as an operator. Every failure is swallowed: the URL is printed on the terminal anyway.

import { spawn } from "node:child_process";

/** Characters a status page URL may contain; anything else is never handed to a shell or opener. */
const SAFE_URL = /^http:\/\/[A-Za-z0-9:/?=.]+$/;

export interface OpenCommand {
  command: string;
  args: string[];
  /** Windows: pass the arguments to cmd.exe as written (the quotes around the URL and the empty title stay). */
  windowsVerbatimArguments?: boolean;
}

/** The command that opens `url` on `platform`, or null when the URL is not of the safe form. */
export function browserCommand(url: string, platform: NodeJS.Platform): OpenCommand | null {
  if (!SAFE_URL.test(url)) return null;
  if (platform === "win32") return { command: "cmd.exe", args: ["/c", "start", '""', `"${url}"`], windowsVerbatimArguments: true };
  if (platform === "darwin") return { command: "open", args: [url] };
  return { command: "xdg-open", args: [url] };
}

/** Whether to open the page by itself: not with `--no-browser`, not when stdout is no terminal, not under CI. */
export function shouldOpenBrowser(o: { noBrowser: boolean; isTTY: boolean; env: NodeJS.ProcessEnv }): boolean {
  if (o.noBrowser || !o.isTTY) return false;
  const ci = o.env.CI;
  return ci === undefined || ci === "";
}

/** Starts the opener detached, output ignored; any error (no browser, no xdg-open) is swallowed. */
export function openBrowser(url: string, platform: NodeJS.Platform = process.platform): void {
  const cmd = browserCommand(url, platform);
  if (cmd === null) return;
  try {
    const child = spawn(cmd.command, cmd.args, {
      detached: true,
      stdio: "ignore",
      windowsHide: true,
      ...(cmd.windowsVerbatimArguments ? { windowsVerbatimArguments: true } : {}),
    });
    child.on("error", () => {
      /* no opener: the URL is on the terminal */
    });
    child.unref();
  } catch {
    /* same */
  }
}
