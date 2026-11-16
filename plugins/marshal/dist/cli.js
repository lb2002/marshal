#!/usr/bin/env node
// Entry point. `hook` runs on every agent event and must start fast and fail closed, so it never loads the
// bridge server or the other commands (hook-cli.ts); everything else lives in commands.ts.
import { fileURLToPath } from "node:url";
import { runHook } from "./hook-cli.js";
const argv = process.argv.slice(2);
if (argv[0] === "hook") {
    void runHook(argv.slice(1));
}
else {
    void import("./commands.js").then((m) => m.main(argv, fileURLToPath(import.meta.url)));
}
