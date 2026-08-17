# Is Codex Working?

A tiny Windows tray app that tells you whether Codex is actually working, waiting for you, stuck, done, at its usage limit, or not running.

## Try it

1. Download and unzip the project
2. Double-click `RUN_ME.bat`
3. Look for the tray icon near the Windows clock
4. Left-click the icon to open the small status card

The first run uses the Windows .NET Framework compiler when it is available. If it is missing, `BUILD.bat` tells you instead of installing anything silently. No Python, Node.js, API key, account login, telemetry, or log upload required.

## What it watches

- Local Codex rollout files under `~/.codex/sessions`
- Real Codex lifecycle events such as turn start, completion, approval/input requests, errors, and usage limits
- Parent/subagent session relationships
- Codex process activity only when a live turn has gone quiet long enough to need a deeper check
- Explicit detached background jobs started by Codex, when a PID can be recovered from trusted tool traffic

## Low-overhead design

- File change events instead of one-second folder polling
- High-frequency writes coalesced into short batches
- Only newly appended JSONL bytes read during normal operation
- A 30-second file-size/timestamp watchdog only as a missed-event safety net
- Deep process inspection only after meaningful Codex activity has been quiet for a while
- Detached PIDs and trusted output logs sampled at low frequency; no per-second process polling
- Hidden tray UI stays quiet unless the primary state, project, or visible status-light set changes

## Public states

- `WORKING`
- `WAITING FOR YOU`
- `STUCK`
- `DONE`
- `LIMIT REACHED`
- `ERROR`
- `NO TASK`

Hover a status icon in the popup to see its meaning. Independent Codex chats can light more than one status at once (for example `WORKING` + `DONE`); subagents inside the same chat remain grouped under their parent task. A completed task keeps its `DONE` light for five minutes.

## Privacy

Local and read-only by design. The app does not upload Codex logs, send telemetry, modify session files, or require an API key. Copied diagnostics redact the Windows user-profile path.

## Development verification

The developer source tree can run `RUN_SELF_TESTS.bat`; it invokes
`BUILD.bat --with-tests` and executes the built-in parser/state regression tests.
The end-user release intentionally contains only the production build inputs and
does not ship the test executable or stress-test source/binary. The commands in
the rest of this section apply to the developer source tree only.

For the Windows integration checks, run the test executable selected by
`bin\CURRENT_TEST_BUILD.txt` with `--stress`. The production pointer remains
in `bin\CURRENT_BUILD.txt`; test output is isolated under `bin\test-build-*`.
This exercises 100ms JSONL appends,
partial lines, coalescing, watcher recovery, bounded resync, UI rendering, and
a real detached Python workload. `--idle-smoke` runs the separate 60-second
empty-folder resource check.

## Current preview scope

This preview targets Windows 10/11 and local Codex sessions. It does not control, resume, stop, or modify Codex.

`NO TASK` means no Codex turn and no tracked background job is currently running. It is a normal waiting state, not a failure.

When Codex explicitly starts a detached process and its PID can be recovered from tool traffic, that process can keep the project in `WORKING` while it is still making CPU/I/O or trusted stdout/stderr progress. A live but repeatedly quiet detached process can become `STUCK`.

Cold rollout history with no recent real progress is kept internally as stale
history and is excluded from the popup, tray representative, status lights, and
notifications. The developer test/stress sources are intentionally omitted from
the end-user package.
