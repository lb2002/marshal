# Other coding-agent CLIs as Lotse feeders (research, 2026-11-11)

Common finding: all three use command hooks (JSON on stdin, JSON on stdout). None has an HTTP hook, so each needs a thin shim that POSTs stdin to the bridge and waits for the reply. Muse and Codex use a Claude-like schema. Gemini differs.

**Side effect to disclose:** `muse --help` ran the launcher script, which checked for updates and downloaded `muse-bin-1.4.15-R8339.1.exe` next to the 1.4.14 binary (see `C:\Users\<user>\AppData\Local\Programs\muse\.muse-update-notice`). No session was started. Facts below are for 1.4.14 unless stated.

## Muse (Meta)
- **Vendor:** Meta. The launcher talks to `api.meta.ai/muse-code`, `auth.meta.com`, `lookaside.facebook.com` (`...\muse\.muse-launcher.ps1`, `.muse-release-info.json`). `muse --version`: 1.4.14 (1.4.14-R7309.1).
- **Help:** `muse hooks --help` just prints top-level help; no hooks subcommand (https://dev.meta.ai/docs/muse-code/extending). `muse plugins hook test <plugin>:<hook> --fixture <path>` exists.
- **Events:** `muse schema generate-json-schema` (offline) lists 20 `HookEventName` values, including sessionStart, userPromptSubmit, preToolUse, permissionRequest, postToolUse, notification, stop, sessionEnd, subagentStart/Stop, postToolBatch. All 8 Lotse events exist. Third-party tests on 1.4.14 saw PermissionRequest and Notification (`permission_prompt`) fire (https://github.com/Safa675/meta-hackathon-project/issues/11).
- **Payload:** `session_id`, `cwd`, `hook_event_name`, `model`, `permission_mode`, `turn_id`, `tool_use_id`, `tool_name` (shell tool is `shell`), `tool_input` (`{command}`), `transcript_path` (null) (https://github.com/Dicklesworthstone/destructive_command_guard/issues/572). Hook env is cleared to an allowlist (`MUSE_SESSION_ID`, HOME, PATH).
- **Approval:** yes, `permissionRequest`. Format (third-party, https://docs.arcjet.com/coding-agents/muse-code/): `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"..."}}}`; allow is `behavior:"allow"` or empty stdout. PreToolUse uses `permissionDecision` allow/ask/deny plus reason. Strings in the 1.4.15 binary confirm: deny needs a non-empty reason, a bare allow without `updatedInput` is rejected for PreToolUse, unknown keys fail the hook. Exit 2 is a fallback block.
- **Timeout:** undocumented; Arcjet's example sets `timeout: 5`. Must be tested. PermissionRequest only fires when a real prompt is needed, and the default approval judge may auto-clear calls (`--approval-judge off` for deterministic tests).
- **Transport:** command only.
- **Scope:** project `<root>/.muse/hooks.json`, shape `{"hooks":{"Event":[{"matcher":"..","hooks":[{"type":"command","command":".."}]}]}}`, runs only after the folder is trusted (extending docs above). Also user settings, managed file (`managed_hooks_path`), plugins (`.muse-plugin/plugin.json`, `muse plugins install <path> --scope project`, then `approve`). One report says a project hooks.json was silently ignored on a beta (https://agenticcontrolplane.com/blog/muse-code-acp-integration), so verify. Trust lives in `~/.config/muse/trust.json` (one-time user step).
- **Transcripts:** `~/.local/share/muse/sessions/YYYY/MM/DD/<uuid>/session.jsonl` (third-party: https://github.com/pohunekd/plugin-muse-code; not checked locally; `~/.local/share/muse` exists with `local-tracing`, `plugins`).

## OpenAI Codex CLI
- **Vendor/version:** OpenAI. Not on PATH; binary is `C:\Users\<user>\.vscode\extensions\openai.chatgpt-26.1021.22027-win32-x64\bin\windows-x86_64\codex.exe`, `codex-cli 0.164.0-alpha.11.1`. `codex features list`: `hooks` stable, on. `~/.codex/config.toml` has no hooks.
- **Docs:** https://learn.chatgpt.com/docs/hooks (redirect from developers.openai.com/codex/hooks).
- **Events:** SessionStart, UserPromptSubmit, PreToolUse, PermissionRequest, PostToolUse, PreCompact, PostCompact, SubagentStart/Stop, Stop, PreModelRequest, PostModelRequest, SessionEnd, Interrupt. **No Notification**; use PermissionRequest for "needs attention".
- **Payload:** `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `model`, `permission_mode`; PreToolUse/PermissionRequest add `turn_id`, `tool_name`, `tool_input` (`tool_input.command` for Bash and apply_patch), PreToolUse also `tool_use_id`.
- **Approval:** yes, best of the three. PermissionRequest runs when Codex would prompt. Allow `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}`; deny same with `"behavior":"deny","message":"..."`. Any deny wins; if hooks allow and none deny the prompt is skipped; if none decide, the normal prompt shows (safe fallback). PreToolUse deny: `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"..."}}` or exit 2 with stderr. Do not return `updatedInput`, `updatedPermissions`, `interrupt` (fail closed).
- **Timeout:** default 600 s (SessionEnd/Interrupt 1 s). For PreToolUse a failed run lets the tool continue. PermissionRequest timeout behaviour not stated; verify.
- **Transport:** `command` and `mcp_tool`; no HTTP.
- **Scope:** `<repo>/.codex/hooks.json` or `.codex/config.toml`, only when the project layer is trusted, and each hook must be reviewed via `/hooks` (hash-based trust; `--dangerously-bypass-hook-trust` skips it per run). Project install works but needs two user trust steps.
- **Transcripts:** `~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl`, JSON lines `{timestamp, ordinal, type, payload}`, first `session_meta` (session_id, cwd, cli_version). Also `session_index.jsonl`, sqlite files.

## Gemini CLI
- **Vendor/version:** Google, `@google/gemini-cli` 0.41.2 (`C:\Users\<user>\AppData\Roaming\npm\node_modules\@google\gemini-cli`). `gemini hooks` only offers `migrate` (from Claude Code).
- **Docs (local):** `...\gemini-cli\bundle\docs\hooks\{index,reference}.md`, `...\docs\reference\policy-engine.md`.
- **Events:** SessionStart, SessionEnd, BeforeAgent (~UserPromptSubmit), AfterAgent (~Stop), BeforeModel, AfterModel, BeforeToolSelection, BeforeTool (~PreToolUse), AfterTool (~PostToolUse), PreCompress, Notification. Notification (`notification_type:"ToolPermission"`, `message`, `details`) is observability-only; it cannot answer.
- **Payload:** `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `timestamp`; BeforeTool adds `tool_name` (e.g. `run_shell_command`), `tool_input`. Env `GEMINI_PROJECT_DIR`, `GEMINI_SESSION_ID`.
- **Approval:** partial. No PermissionRequest hook. From the bundle code (`bundle\chunk-6DSAZLFF.js`, `_processToolCall`), BeforeTool fires before the policy check and prompt. `{"decision":"deny","reason":"..."}` (or `block`) or exit 2 blocks the tool (reason goes to the model); `{"decision":"ask"}` forces the prompt. `allow` does NOT skip the CLI's own confirmation. So Lotse can deny remotely but cannot approve remotely unless a user/admin policy TOML already allows the tool (or yolo), making the hook the only gate. `continue:false` kills the agent loop.
- **Timeout:** default 60000 ms, configurable per hook. A timeout or non-zero code other than 2 is a warning and the tool proceeds (fail-open; reference.md plus the runner code). Non-JSON stdout is treated as allow.
- **Transport:** `type:"command"` only.
- **Scope:** project `.gemini/settings.json` `hooks` key (highest precedence), fingerprinted, needs trusted folder (`~/.gemini/trustedFolders.json`). Project-level policy files are documented as non-functional.
- **Transcripts:** `~/.gemini/tmp/<project>/chats/session-<ts>-<id>.jsonl`; first line header `{sessionId, projectHash, startTime, lastUpdated, kind}`, then `{"$set":{"messages":[...]}}` records. Path also in `transcript_path`.

## Feasibility for Lotse

| Tool | Events | Approval interception | Config scope | Effort |
|---|---|---|---|---|
| Muse 1.4.14 | All 8 map | Yes, PermissionRequest (Codex-style JSON); timeout default unknown | Project `.muse/hooks.json` after folder trust (verify) | Medium: adapter plus live test of approval and timeout |
| Codex 0.164 | 7 of 8 (no Notification) | Yes, PermissionRequest allow/deny, falls back to normal prompt | Project `.codex/hooks.json` plus hash trust in `/hooks` | Low-medium: closest to Claude schema |
| Gemini 0.41.2 | 7 of 8 via renamed events | Deny only (BeforeTool); approve needs policy allow; timeout fail-open | Project `.gemini/settings.json` plus folder trust | Medium-high: renames, no true approval hook |

Nothing here was verified by running a live agent session; payloads for Muse come from third-party captures.
