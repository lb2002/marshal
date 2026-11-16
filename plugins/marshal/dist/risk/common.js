export const SAFE = { risk: "safe", reason: "Safe" };
export const NETWORK = { risk: "network", reason: "Network access" };
export const destructive = (reason) => ({ risk: "destructive", reason });
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
const RANK = { safe: 0, network: 1, destructive: 2 };
export function worst(a, b) {
    return RANK[b.risk] > RANK[a.risk] ? b : a;
}
