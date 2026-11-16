// Rules for individual commands that need no recursion: deleters, disk and
// process tools, cloud and container CLIs, downloads, package managers,
// writers, environment assignments and SQL clients.

import type { Heredoc, Word } from "./lexer.js";
import { Ctx, NETWORK, OUTSIDE, RISKY_ENV_SET, RiskResult, SAFE, SQL, destructive } from "./common.js";
import { writeTargetRisk } from "./paths.js";

export const texts = (ws: Word[]) => ws.map((w) => w.text);
const nonFlagWords = (ws: Word[]) => ws.filter((w) => !w.text.startsWith("-"));

// ---------------------------------------------------------------- names

const normName = (n: string) => {
  if (n === "py" || n === "pythonw" || /^python[\d.]*$/.test(n)) return "python";
  if (/^pip[\d.]*$/.test(n)) return "pip";
  if (n.startsWith("mkfs.")) return "mkfs";
  if (n === "nodejs") return "node";
  return n;
};

/** Effective command name of a word; obfuscated when it cannot be determined statically. */
export function commandName(w: Word): { name: string; obfuscated: boolean } {
  const t = w.text;
  let k = t.length;
  while (k > 0 && t[k - 1] !== "/" && t[k - 1] !== "\\") k--;
  let base = t.slice(k).replace(/\^/g, "").toLowerCase();
  for (const ext of [".exe", ".cmd", ".bat", ".com", ".ps1"]) if (base.endsWith(ext)) base = base.slice(0, -ext.length);
  const obfuscated = w.dyn || w.glob || base === "" || t.startsWith("-") || t.includes("%");
  return { name: normName(base), obfuscated };
}

// ---------------------------------------------------------------- environment

export const RISKY_ENV = new Set([
  "GIT_SSH_COMMAND", "GIT_SSH", "GIT_EXTERNAL_DIFF", "GIT_PAGER", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_ASKPASS", "GIT_PROXY_COMMAND",
  "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG", "GIT_EXEC_PATH", "GIT_DIR",
  "GIT_TEMPLATE_DIR", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "BASH_ENV", "ENV",
  "PROMPT_COMMAND", "PS4", "SHELLOPTS", "BASHOPTS", "PYTHONSTARTUP", "PYTHONPATH", "PYTHONHOME", "NODE_OPTIONS", "NODE_PATH",
  "PERL5OPT", "PERL5LIB", "PERLDB_OPTS", "RUBYOPT", "RUBYLIB", "PAGER", "EDITOR", "VISUAL", "CDPATH",
]);

export function riskyEnvName(name: string): boolean {
  const n = name.toUpperCase();
  return RISKY_ENV.has(n) || n.startsWith("GIT_CONFIG_KEY_") || n.startsWith("GIT_CONFIG_VALUE_") || n.startsWith("BASH_FUNC_");
}

export const ASSIGN_RE = /^([A-Za-z_][A-Za-z0-9_]*)(?:\[[^\]]*\])?\+?=/;
const IDENT_RE = /^[A-Za-z_][A-Za-z0-9_]*$/;
const ASSIGN_CMDS = new Set(["export", "declare", "typeset", "readonly", "local", "set", "setx", "set-item", "si", "new-item", "ni", "setenv"]);

/** export/declare/... or setx/Set-Item env: of a variable in RISKY_ENV. */
function assignsRiskyEnv(name: string, args: Word[]): boolean {
  const a = texts(args);
  if (name === "set-item" || name === "si" || name === "new-item" || name === "ni") {
    return a.some((x) => {
      const i = x.toLowerCase().indexOf("env:");
      return i >= 0 && riskyEnvName(x.slice(i + 4).split(/[\\/]/)[0]);
    });
  }
  if (name === "setx") {
    for (let i = 0; i < a.length; i++) {
      if (a[i].startsWith("/")) {
        if (/^\/[sup]$/i.test(a[i])) i++;
        continue;
      }
      return riskyEnvName(a[i]);
    }
    return false;
  }
  return a.some((x) => {
    if (x.startsWith("-") || x.startsWith("+")) return false;
    const m = ASSIGN_RE.exec(x);
    return m ? riskyEnvName(m[1]) : IDENT_RE.test(x) && riskyEnvName(x);
  });
}

// ---------------------------------------------------------------- options and writes

export interface Opt {
  name: string;
  value?: Word;
}

/** Parse getopt-style options: short clusters (value chars take the rest or the next word), `--long[=v]`. */
export function parseOpts(args: Word[], shortValue: string, longValue: Set<string>): { opts: Opt[]; positionals: Word[] } {
  const opts: Opt[] = [];
  const positionals: Word[] = [];
  for (let i = 0; i < args.length; i++) {
    const w = args[i];
    const t = w.text;
    if (t === "--") {
      positionals.push(...args.slice(i + 1));
      break;
    }
    if (t.startsWith("--")) {
      const eq = t.indexOf("=");
      if (eq >= 0) opts.push({ name: t.slice(0, eq), value: { ...w, text: t.slice(eq + 1) } });
      else if (longValue.has(t)) opts.push({ name: t, value: args[++i] });
      else opts.push({ name: t });
      continue;
    }
    if (t.startsWith("-") && t.length > 1) {
      for (let j = 1; j < t.length; j++) {
        const ch = t[j];
        if (shortValue.includes(ch)) {
          opts.push({ name: "-" + ch, value: j + 1 < t.length ? { ...w, text: t.slice(j + 1) } : args[++i] });
          break;
        }
        opts.push({ name: "-" + ch });
      }
      continue;
    }
    positionals.push(w);
  }
  return { opts, positionals };
}

const values = (opts: Opt[], ...names: string[]) => opts.filter((o) => names.includes(o.name) && o.value).map((o) => o.value as Word);

export const NULL_SINKS = new Set(["/dev/null", "/dev/stdout", "/dev/stderr", "/dev/tty", "nul", "$null", "-"]);

export function writeAny(paths: Word[], ctx: Ctx): RiskResult | null {
  for (const p of paths) {
    if (NULL_SINKS.has(p.text.toLowerCase())) continue;
    if (p.dyn || p.text === "") return OUTSIDE;
    const r = writeTargetRisk(p.text, ctx);
    if (r) return r;
  }
  return null;
}

function localDestination(args: Word[], ctx: Ctx): RiskResult | null {
  const paths = nonFlagWords(args);
  const dest = paths[paths.length - 1];
  if (dest === undefined || (dest.text.includes(":") && !/^[A-Za-z]:[\\/]/.test(dest.text))) return null; // remote host:path
  return writeAny([dest], ctx);
}

// ---------------------------------------------------------------- downloads

export const DOWNLOADERS = new Set(["curl", "wget", "iwr", "irm", "invoke-webrequest", "invoke-restmethod", "http", "https", "aria2c"]);
const CURL_SHORT_VALUE = "AbcCdDeEFHKmoPQrtTuUwxXyYz";
const CURL_LONG_VALUE = new Set([
  "--output", "--request", "--data", "--data-raw", "--data-binary", "--data-urlencode", "--header", "--user", "--user-agent",
  "--referer", "--output-dir", "--config", "--cookie", "--cookie-jar", "--form", "--max-time", "--proxy", "--upload-file", "--url",
  "--write-out", "--range", "--retry", "--connect-timeout", "--cacert", "--cert", "--key", "--resolve", "--dump-header", "--trace",
  "--trace-ascii", "--stderr", "--json",
]);
const WGET_SHORT_VALUE = "oaeiBtOTwQPlARDXIU";
const WGET_LONG_VALUE = new Set(["--output-document", "--output-file", "--append-output", "--input-file", "--directory-prefix", "--execute", "--tries", "--timeout", "--user-agent", "--header"]);

/** Last path component of a URL (query and fragment removed); "index.html" for a directory URL. */
export function urlBaseName(url: string): string {
  let s = url;
  for (const sep of ["?", "#"]) {
    const i = s.indexOf(sep);
    if (i >= 0) s = s.slice(0, i);
  }
  const scheme = s.indexOf("://");
  if (scheme >= 0) s = s.slice(scheme + 3);
  const slash = s.lastIndexOf("/");
  const base = slash >= 0 ? s.slice(slash + 1) : "";
  return base === "" ? "index.html" : base;
}

export interface Download {
  /** Files written by the download (paths as given). */
  outputs: Word[];
  /** Directories the downloaded files land in. */
  dirs: Word[];
  /** Some written file names cannot be known. */
  unknown: boolean;
  method: string;
}

/** What a download command writes to disk. */
export function downloadInfo(name: string, args: Word[]): Download {
  const res: Download = { outputs: [], dirs: [], unknown: false, method: "" };
  const plain = (text: string): Word => ({ text, dyn: false, glob: false, quoted: false, inert: false });
  if (name === "curl") {
    const { opts, positionals } = parseOpts(args, CURL_SHORT_VALUE, CURL_LONG_VALUE);
    const urls = [...positionals, ...values(opts, "--url")];
    res.method = (values(opts, "-X", "--request")[0]?.text ?? "").toLowerCase();
    res.outputs.push(...values(opts, "-o", "--output", "-c", "--cookie-jar", "-D", "--dump-header", "--trace", "--trace-ascii", "--stderr"));
    if (opts.some((o) => o.name === "-O" || o.name === "--remote-name" || o.name === "--remote-name-all")) {
      res.outputs.push(...urls.map((u) => plain(urlBaseName(u.text))));
    }
    if (opts.some((o) => o.name === "-J" || o.name === "--remote-header-name" || o.name === "-K" || o.name === "--config")) res.unknown = true;
    res.dirs.push(...values(opts, "--output-dir"));
  } else if (name === "wget") {
    const { opts, positionals } = parseOpts(args, WGET_SHORT_VALUE, WGET_LONG_VALUE);
    const docs = values(opts, "-O", "--output-document");
    res.outputs.push(...docs, ...values(opts, "-o", "--output-file", "-a", "--append-output"));
    if (docs.length === 0) res.outputs.push(...positionals.map((u) => plain(urlBaseName(u.text))));
    if (opts.some((o) => ["-i", "--input-file", "-r", "--recursive", "-m", "--mirror", "--content-disposition", "-e", "--execute"].includes(o.name))) res.unknown = true;
    res.dirs.push(...values(opts, "-P", "--directory-prefix"));
  } else if (name === "aria2c") {
    const { opts, positionals } = parseOpts(args, "odi", new Set(["--out", "--dir", "--input-file"]));
    const outs = values(opts, "-o", "--out");
    res.outputs.push(...(outs.length ? outs : positionals.map((u) => plain(urlBaseName(u.text)))));
    res.dirs.push(...values(opts, "-d", "--dir"));
    if (opts.some((o) => o.name === "-i" || o.name === "--input-file")) res.unknown = true;
  } else if (name === "http" || name === "https") {
    const { opts } = parseOpts(args, "o", new Set(["--output"]));
    res.outputs.push(...values(opts, "-o", "--output"));
    if (opts.some((o) => o.name === "-d" || o.name === "--download")) res.unknown = true;
    const del = args.find((a) => !a.text.startsWith("-"));
    if (del && del.text.toLowerCase() === "delete") res.method = "delete";
  } else {
    // Invoke-WebRequest / Invoke-RestMethod: -OutFile x, -Method Delete (parameter prefixes allowed)
    for (let i = 0; i < args.length; i++) {
      const t = args[i].text;
      if (!t.startsWith("-")) continue;
      const colon = t.indexOf(":");
      const key = (colon >= 0 ? t.slice(1, colon) : t.slice(1)).toLowerCase();
      const value = colon >= 0 ? plain(t.slice(colon + 1)) : args[i + 1];
      if (key.length > 0 && "outfile".startsWith(key) && value) res.outputs.push(value);
      if (key.length > 1 && "method".startsWith(key) && value) res.method = value.text.toLowerCase();
    }
  }
  return res;
}

function classifyDownload(name: string, args: Word[], ctx: Ctx): RiskResult {
  const d = downloadInfo(name, args);
  if (d.method === "delete") return destructive("Deletes data on the server");
  return writeAny(d.outputs, ctx) ?? writeAny(d.dirs, ctx) ?? NETWORK;
}

// ---------------------------------------------------------------- SQL

const SQL_STRONG = /\b(?:drop\s+(?:table|database|schema|index|view|user|role|column|function|procedure|trigger|extension|sequence|collection)|truncate\s+table|delete\s+from|flushall|flushdb|dropdatabase)\b|\.drop\s*\(|\bdrop\s*\(/i;
const SQL_WEAK = /\b(?:truncate|alter\s+table)\b/i;
const SQL_CLIENTS = new Set([
  "psql", "mysql", "mariadb", "sqlite3", "sqlite", "sqlcmd", "osql", "isql", "mongo", "mongosh", "redis-cli", "clickhouse-client",
  "clickhouse", "duckdb", "cockroach", "cqlsh", "bq", "snowsql",
]);

const SQL_TEXT_ONLY = new Set(["git", "gh", "grep", "egrep", "fgrep", "rg", "ag", "ack", "select-string"]);

/** Remove SQL block comments (linear scan) and optionally line comments. */
function stripSqlComments(s: string, lineComments: boolean): string {
  let out = "";
  let i = 0;
  while (i < s.length) {
    if (s.startsWith("/*", i)) {
      const end = s.indexOf("*/", i + 2);
      out += " ";
      if (end < 0) break;
      i = end + 2;
    } else if (lineComments && s.startsWith("--", i)) {
      const end = s.indexOf("\n", i);
      out += " ";
      if (end < 0) break;
      i = end;
    } else {
      out += s[i++];
    }
  }
  return out;
}

function sqlMatch(text: string, weak: boolean): boolean {
  if (!text) return false;
  return [text, stripSqlComments(text, false), stripSqlComments(text, true)].some((v) => SQL_STRONG.test(v) || (weak && SQL_WEAK.test(v)));
}

/**
 * Destructive SQL in a simple command. Strong statements count everywhere; `truncate` and `alter table`
 * only when a SQL client is part of the command (its arguments or here-documents).
 */
export function sqlRisk(argv: Word[], heredocs: Heredoc[]): RiskResult | null {
  const client = argv.some((w) => SQL_CLIENTS.has(commandName(w).name));
  // Commit messages, PR bodies and search patterns are text, never executed SQL.
  if (!client && SQL_TEXT_ONLY.has(commandName(argv[0]).name)) return null;
  if (sqlMatch(texts(argv).join(" "), client)) return SQL;
  if (client && heredocs.some((h) => sqlMatch(h.body, true))) return SQL;
  return null;
}

// ---------------------------------------------------------------- tables

export const DELETERS = new Set([
  "rm", "rmdir", "unlink", "shred", "srm", "del", "erase", "rd", "remove-item", "ri", "rimraf", "del-cli", "trash", "trash-put",
  "vssadmin", "wbadmin", "diskshadow", "cipher",
]);
const DISK = new Set(["dd", "mkswap", "fdisk", "parted", "diskpart", "format", "wipefs", "format-volume", "clear-disk", "remove-partition", "mkfs"]);
const KILLERS = new Set(["kill", "pkill", "killall", "taskkill", "stop-process"]);
const POWER = new Set(["shutdown", "reboot", "halt", "poweroff", "restart-computer", "stop-computer"]);
const PERMS = new Set(["chmod", "chown", "chgrp"]);
const LINKERS = new Set(["ln", "mklink"]);
const NET_CMDS = new Set([
  "ssh", "scp", "sftp", "rsync", "nc", "ncat", "netcat", "telnet", "ftp", "tftp", "socat", "ping", "nslookup", "dig", "host",
  "traceroute", "test-netconnection", "mosh", "gh", "npx", "pnpx", "bunx",
]);
export const PKG_MANAGERS = new Set(["npm", "pnpm", "yarn", "bun", "pip", "pipx", "uv", "poetry", "gem", "composer", "go", "apt", "apt-get", "brew", "choco", "winget", "dotnet"]);
const PKG_NET_SUBS = new Set(["install", "i", "add", "ci", "update", "upgrade", "publish", "login", "download", "get", "fetch", "restore", "push", "dist-tag", "view", "info", "search", "outdated", "audit", "create", "sync"]);
const PKG_EXEC_SUBS = new Set(["exec", "dlx", "x"]);
const PKG_DESTRUCTIVE_SUBS = new Set(["unpublish", "deprecate", "uninstall-global", "logout", "token", "owner", "access"]);
/**
 * Commands writing to every non-flag argument. PowerShell aliases included: sc (Set-Content, Windows
 * PowerShell), ac (Add-Content), clc (Clear-Content), mi (Move-Item), rni (Rename-Item), sp
 * (Set-ItemProperty), si (Set-Item), and tee for Tee-Object. `sc delete` (the service tool) is caught
 * earlier by its own rule, a risky environment variable set with Set-Item too.
 */
const WRITERS_ALL = new Set([
  "mv", "tee", "tee-object", "truncate", "set-content", "sc", "add-content", "ac", "out-file", "clear-content", "clc",
  "new-item", "ni", "move-item", "mi", "rename-item", "rni", "ren", "move",
  "export-csv", "export-clixml", "set-itemproperty", "sp", "set-item", "si",
]);
/** Commands writing to their last non-flag argument (destination); cpi is Copy-Item. */
const WRITERS_LAST = new Set(["cp", "copy", "copy-item", "cpi", "install", "xcopy", "robocopy"]);

const DOCKER_GLOBAL_VALUE = new Set(["--context", "-c", "--config", "-H", "--host", "-l", "--log-level", "--tlscacert", "--tlscert", "--tlskey"]);
const DOCKER_GROUPS = new Set(["container", "image", "network", "volume", "system", "builder", "buildx", "context", "plugin", "stack", "service", "swarm", "node", "secret", "config", "trust", "manifest", "compose"]);
const COMPOSE_VALUE = new Set(["-f", "--file", "-p", "--project-name", "--profile", "--env-file", "--project-directory", "--ansi", "--progress", "--parallel"]);

// ---------------------------------------------------------------- per command

function mountsHost(args: string[]): boolean {
  for (const t of args) {
    const lower = t.toLowerCase();
    if (lower === "--privileged" || /^--(volume|mount)(=|$)/.test(lower)) return true;
    if (t.startsWith("-") && !t.startsWith("--")) {
      for (let j = 1; j < t.length; j++) {
        if (t[j] === "v") return true;
        if ("acehlmpuw".includes(t[j])) break; // value follows
      }
    }
  }
  return false;
}

function classifyDocker(args: Word[], ctx: Ctx): RiskResult {
  const a = texts(args);
  let i = 0;
  const skipFlags = (valued: Set<string>) => {
    while (i < a.length && a[i].startsWith("-")) i += valued.has(a[i]) ? 2 : 1;
  };
  skipFlags(DOCKER_GLOBAL_VALUE);
  if (DOCKER_GROUPS.has((a[i] ?? "").toLowerCase())) {
    const group = a[i].toLowerCase();
    i++;
    skipFlags(group === "compose" ? COMPOSE_VALUE : new Set());
  }
  const sub = (a[i] ?? "").toLowerCase();
  const rest = a.slice(i + 1);
  if ((sub === "run" || sub === "create") && mountsHost(rest)) return destructive("Mounts a host folder or grants full access");
  if (a.some((x) => /^(rm|rmi|prune|kill)$/i.test(x))) return destructive("Deletes containers or images");
  if (sub === "cp") return localDestination(args.slice(i + 1), ctx) ?? SAFE;
  return /^(pull|push|login|search|build)$/.test(sub) ? NETWORK : SAFE;
}

function classifyWriters(name: string, args: Word[], ctx: Ctx): RiskResult | null {
  const a = texts(args);
  if (name === "cp" || name === "mv" || name === "install") {
    const { opts } = parseOpts(args, "tS", new Set(["--target-directory", "--suffix"]));
    const t = values(opts, "-t", "--target-directory");
    if (t.length) return writeAny(t, ctx);
  }
  if (WRITERS_ALL.has(name) || WRITERS_LAST.has(name)) {
    const slashFlags = name === "xcopy" || name === "robocopy";
    const paths = args.filter((w) => !w.text.startsWith("-") && !(slashFlags && /^\/[A-Za-z?]+(:.*)?$/.test(w.text)));
    return writeAny(WRITERS_LAST.has(name) ? paths.slice(-1) : paths, ctx);
  }
  if (name === "sed" || name === "perl" || name === "gsed") {
    const inPlace = a.some((x) => x === "--in-place" || x.startsWith("--in-place=") || /^-[A-Za-z]*i/.test(x));
    return inPlace ? writeAny(nonFlagWords(args), ctx) : null;
  }
  if (name === "tar" || name === "bsdtar") {
    const { opts } = parseOpts(args, "CfbTX", new Set(["--directory", "--file"]));
    return writeAny(values(opts, "-C", "--directory"), ctx);
  }
  if (name === "unzip") return writeAny(parseOpts(args, "d", new Set()).opts.filter((o) => o.name === "-d" && o.value).map((o) => o.value as Word), ctx);
  return null;
}

/** Rules for commands that need no recursion; null when the command is not covered. */
export function classifyNamed(name: string, args: Word[], ctx: Ctx): RiskResult | null {
  const a = texts(args);
  const lower = a.map((x) => x.toLowerCase());

  if (ASSIGN_CMDS.has(name) && assignsRiskyEnv(name, args)) return RISKY_ENV_SET;

  if (DELETERS.has(name)) {
    if (name === "vssadmin" || name === "wbadmin" || name === "diskshadow") return lower.some((x) => x.startsWith("delete")) || name === "diskshadow" ? destructive("Deletes backups") : null;
    if (name === "cipher") return lower.some((x) => x.startsWith("/w")) ? destructive("Overwrites free disk space") : null;
    const recursive = a.some((x) => /^-[A-Za-z]*[rR]/.test(x) || /^-recurse$/i.test(x) || /^\/s$/i.test(x)) || lower.includes("--recursive");
    return destructive(recursive ? "Deletes files and subfolders" : "Deletes files");
  }
  if (name === "reg" && lower.some((x) => x === "delete" || x === "unload")) return destructive("Deletes registry entries");
  if (name === "sc" && lower.includes("delete")) return destructive("Deletes a service");
  if (DISK.has(name)) return destructive("Overwrites a disk");
  if (KILLERS.has(name)) return destructive("Stops processes");
  if (POWER.has(name)) return destructive("Shuts down the computer");
  if (name === "icacls" || name === "takeown") return destructive("Changes permissions");
  if (PERMS.has(name)) {
    if (a.some((x) => /^-[A-Za-z]*R/.test(x)) || lower.includes("--recursive")) return destructive("Changes permissions in subfolders too");
    if (a.some((x) => /^(0?777|0?666|a\+rwx|ugo\+rwx|a\+w|o\+w)$/.test(x))) return destructive("Sets unsafe permissions");
    return null;
  }
  if (LINKERS.has(name) || ((name === "new-item" || name === "ni") && lower.some((x) => /^(symboliclink|junction|hardlink)$/.test(x)))) {
    return destructive("Creates a link");
  }
  if ((name === "foreach-object" || name === "foreach") && lower.some((x) => x === "delete" || x === "remove" || x === "-membername")) return destructive("Deletes files");
  if (name === "robocopy" && lower.some((x) => x === "/mir" || x === "/purge")) return destructive("Deletes files at the target");
  if (name === "docker" || name === "podman") return classifyDocker(args, ctx);
  if (name === "kubectl" || name === "helm" || name === "oc") {
    return lower.some((x) => /^(delete|uninstall|drain|replace)$/.test(x)) ? destructive("Deletes cluster resources") : NETWORK;
  }
  if (name === "terraform" || name === "tofu" || name === "pulumi") {
    return lower.some((x) => /^(destroy|-auto-approve|--yes)$/.test(x)) ? destructive("Destroys infrastructure") : NETWORK;
  }
  if (name === "aws" || name === "gcloud" || name === "az") {
    const ops = lower.filter((x) => !x.startsWith("-")).slice(0, 4);
    return ops.some((x) => /(delete|remove|terminate|destroy|purge)/.test(x) || x === "rm" || x === "rb" || x === "mv") ? destructive("Deletes cloud resources") : NETWORK;
  }
  if (name === "gh") {
    const pos = lower.filter((x) => !x.startsWith("-"));
    if (pos.slice(0, 2).some((x) => x.includes("delete"))) return destructive("Deletes resources on GitHub");
    if (pos[0] === "api") {
      const { opts } = parseOpts(args, "XfFHpq", new Set(["--method", "--field", "--raw-field", "--header", "--input", "--jq"]));
      if (values(opts, "-X", "--method").some((v) => v.text.toLowerCase() === "delete")) return destructive("Deletes resources on GitHub");
    }
    return NETWORK;
  }
  if (name === "rsync") {
    if (lower.some((x) => x.startsWith("--delete") || x.startsWith("--remove-source"))) return destructive("Deletes files at the target");
    return localDestination(args, ctx) ?? NETWORK;
  }
  if (name === "scp") return localDestination(args, ctx) ?? NETWORK;
  if (DOWNLOADERS.has(name)) return classifyDownload(name, args, ctx);
  if (PKG_MANAGERS.has(name) || name === "npx" || name === "pnpx" || name === "bunx") {
    const sub = lower.find((x) => !x.startsWith("-")) ?? "";
    const exec = name === "npx" || name === "pnpx" || name === "bunx" || PKG_EXEC_SUBS.has(sub);
    if (exec && args.some((w) => DELETERS.has(commandName(w).name))) return destructive("Deletes files");
    if (PKG_DESTRUCTIVE_SUBS.has(sub)) return destructive("Removes a package or access online");
    if (exec) return NETWORK;
    return PKG_NET_SUBS.has(sub) ? NETWORK : null;
  }
  if (NET_CMDS.has(name)) return NETWORK;
  return classifyWriters(name, args, ctx);
}
