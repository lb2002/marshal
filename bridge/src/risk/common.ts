import type { PathEnv } from "./paths.js";

export type Risk = "safe" | "network" | "destructive";

export interface RiskResult {
  risk: Risk;
  reason: string;
}

export const SAFE: RiskResult = { risk: "safe", reason: "Safe" };
export const NETWORK: RiskResult = { risk: "network", reason: "Network access" };
export const destructive = (reason: string): RiskResult => ({ risk: "destructive", reason });

export const OUTSIDE = destructive("Writes outside the project");
export const PROTECTED = destructive("Changes agent or Git settings");
export const OBFUSCATED = destructive("Disguised command");
export const INLINE_CODE = destructive("Runs embedded code");
export const DOWNLOADED_CODE = destructive("Runs downloaded code");
export const COMPLEX = destructive("Complex shell syntax");
export const RISKY_ENV_SET = destructive("Sets a risky environment variable");
export const NESTED = destructive("Command nested too deeply");
export const UNCLEAR_SHELL = destructive("Unclear shell call");
export const SQL = destructive("Deletes database contents");

const RANK: Record<Risk, number> = { safe: 0, network: 1, destructive: 2 };

export function worst(a: RiskResult, b: RiskResult): RiskResult {
  return RANK[b.risk] > RANK[a.risk] ? b : a;
}

/** Evaluation context: `root` is the project (cwd of the hook); `cur` the directory relative paths resolve against (null = unknown). */
export interface Ctx {
  root: string;
  cur: string | null;
  /** CDPATH was assigned or exported earlier in this command, so plain `cd name` is unknowable. */
  cdpath?: boolean;
  /** Platform, lstat and lookup budget of the whole classification (shared by ctx copies); default: this machine. */
  paths?: PathEnv;
}
