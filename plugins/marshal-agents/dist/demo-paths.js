// Maps the placeholders of an anonymised recording back onto a real directory, so that a
// replayed permission is classified like the recorded one. Shared by the live demo (inject)
// and the offline pack, so both give the same risk.
import path from "node:path";
/** `<repo>` → demoRoot; `<abs>` → a directory outside demoRoot, so those paths stay unsafe. */
export function mapRepo(v, demoRoot) {
    if (v === "<repo>")
        return demoRoot;
    if (v !== undefined && v.startsWith("<repo>/"))
        return path.join(demoRoot, v.slice("<repo>/".length));
    // `<abs>` paths were outside the recorded project: keep them outside demoRoot so they stay unsafe.
    if (v === "<abs>")
        return path.join(path.parse(demoRoot).root, "__lotse_abs__");
    if (v !== undefined && v.startsWith("<abs>/"))
        return path.join(path.parse(demoRoot).root, "__lotse_abs__", v.slice("<abs>/".length));
    return v;
}
/** The tool input and cwd a recorded permission event stands for, with placeholders mapped onto demoRoot. */
export function demoPermission(e, demoRoot) {
    const input = {};
    if (e.command !== undefined)
        input.command = e.command;
    const filePath = mapRepo(e.path, demoRoot);
    if (filePath !== undefined)
        input.file_path = filePath;
    return { tool: e.tool ?? "", input, cwd: mapRepo(e.cwd, demoRoot) ?? demoRoot };
}
