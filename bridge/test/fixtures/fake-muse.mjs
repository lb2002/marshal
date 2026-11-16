// A stand-in for `muse-bin-*.exe plugins …` (tests set LOTSE_MUSE_BIN to this file). It keeps its plugin list in
// the JSON file FAKE_MUSE_STATE and prints JSON like Muse. FAKE_MUSE_EXIT1=<command> makes that command exit 1
// while printing JSON without an `error` field.
import { existsSync, readFileSync, writeFileSync } from "node:fs";

const [group, cmd, ...rest] = process.argv.slice(2);
const stateFile = process.env.FAKE_MUSE_STATE;
const state = stateFile && existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, "utf8")) : { plugins: [], log: [] };
const save = () => stateFile && writeFileSync(stateFile, JSON.stringify(state));
const out = (v) => process.stdout.write(JSON.stringify(v) + "\n");
const exitCode = process.env.FAKE_MUSE_EXIT1 === cmd ? 1 : 0;

if (group !== "plugins") {
  out({ error: { message: "unsupported" } });
  process.exit(2);
}
state.log.push(cmd);
if (cmd === "validate") out({ valid: true, capabilities: { hooks: [] }, diagnostics: [] });
else if (cmd === "install") {
  state.plugins = state.plugins.filter((p) => p.id !== "lotse");
  state.plugins.push({ id: "lotse", source: rest[0] });
  out({ installed: { id: "lotse" } });
} else if (cmd === "update") out({ updated: { id: rest[0] } });
else if (cmd === "list") out({ plugins: state.plugins.map((p) => ({ record: { id: p.id, source: { path: p.source } } })) });
else if (cmd === "remove") {
  state.plugins = state.plugins.filter((p) => p.id !== rest[0]);
  out({ removed: rest[0] });
} else out({ error: { message: `unknown command ${cmd}` } });
save();
process.exit(exitCode);
