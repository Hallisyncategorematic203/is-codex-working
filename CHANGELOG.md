# Changelog

## 0.5.1-preview

- Added immutable per-root group snapshots with root/subagent grouping, independent-chat separation, five-minute per-group DONE retention, and deterministic aggregate priority
- Added per-group lifecycle notifications with startup baselining and new-turn completion deduplication
- Added one-shot semantic deadlines for DONE expiry, STUCK confirmation, and attention-state expiry
- Hardened detached PID identity with process start time, executable, trusted run-root, output evidence, stale-receipt, and PID-reuse checks
- Preserved incremental JSONL parsing through partial writes, truncation, replacement, rotation, watcher recovery, and bounded initial tails; added parser metrics and stress coverage
- Completed the selected popup with simultaneous compact group rows, cute public labels/subtitles, brighter canonical WORKING color, and a ring-free heartbeat tray glyph
- Made build outputs isolated and pointer-selected so a failed build or self-test cannot launch a stale executable
- Split production compilation from test/stress compilation; plain `BUILD.bat` and `RUN_ME.bat` build or launch only the app, while `--with-tests` writes tests to an isolated `bin\test-build-*` output and remains developer-only
- Hardened the build receipt checks so the launch/test scripts accept only generated `bin\build-<digits>-<digits>` paths
- Excluded cold dangling rollouts without recent real progress from public state, active lights, representatives, and notifications; retained them internally as stale history
- Kept trusted detached background work visible only while its identity is current, and advanced `Last work` only on CPU/I/O/output progress evidence
- Made the compact popup reserve a dedicated `+N more` row and grow its height before the divider and Details link

## 0.5.0-preview

- Kept the selected Icon Row + Text popup layout while increasing the visual weight of status icons 2-7
- Added simultaneous status lights for independent Codex task groups; subagents in one chat remain grouped
- Changed completed-task visibility from ten minutes to five minutes
- Reworked the notification-area icon to use a ring-free glyph; `WORKING` is now a brighter heartbeat pulse
- Kept the tray icon driven by the primary state so a recent `DONE` task cannot hide another active `WORKING` task
- Preserved attention priority for `WAITING FOR YOU`, `STUCK`, `ERROR`, and `LIMIT REACHED`; stale error/limit terminals age out after ten minutes

## 0.4.1-preview

- Fixed `Wait-Process -Id <PID>` parsing; a misplaced regex word boundary prevented numeric PIDs from being recovered
- Added regression coverage for normal, case/spacing-varied, and compound `Wait-Process` commands
- Kept failed-build cleanup so a self-test failure cannot leave a runnable stale EXE

## 0.4.0-preview

- Added trusted detached-background PID recovery from `Start-Process` tool output and `Wait-Process -Id` commands
- Added low-frequency CPU/I/O tracking for detached jobs and their surviving descendants
- Added trusted stdout/stderr growth as another detached-job progress signal
- Prevented long-running detached Python jobs from becoming false `STUCK` states
- Added quiet-background detection so an alive but non-progressing detached job can still become `STUCK`
- Kept usage-limit and error states visible even if a detached job remains alive
- Fixed built-in self-tests that depended on stale fixed timestamps or an outdated child-terminal expectation
- Fixed `RUN_ME.bat` so a failed self-test build cannot be launched on the next run
- Reduced trusted PID-receipt recovery latency while keeping it on the low-frequency watchdog path
- Added background-job diagnostics while keeping the default popup simple
- Renamed the user-facing idle label to `NO TASK` for clearer wording

## 0.3.0-preview

- Rebuilt as a native C# Windows tray app
- Removed Python, Tkinter, virtual environments, and one-second polling
- Added event-driven incremental rollout monitoring with coalesced writes
- Added a 30-second missed-event watchdog and watcher recovery
- Added bounded append processing for large rollout files
- Added parent/subagent lineage handling and inherited-history protection
- Added current and legacy Codex lifecycle/error compatibility
- Added native low-frequency Codex process-tree checks for quiet turns
- Added single-worker gates to prevent parser/watchdog overlap
- Added safe terminal-state parsing for done, usage limits, errors, and user-attention states
- Added the selected light Icon Row + Text popup design
- Unified tray and popup status glyph rendering
- Added redacted copyable diagnostics
