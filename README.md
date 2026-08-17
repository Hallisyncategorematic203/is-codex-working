# Is Codex Working?

[![Latest release](https://img.shields.io/github/v/release/kim-sin/is-codex-working?label=release)](https://github.com/kim-sin/is-codex-working/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows11&logoColor=white)](https://github.com/kim-sin/is-codex-working/releases/latest)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Windows CI](https://github.com/kim-sin/is-codex-working/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/kim-sin/is-codex-working/actions/workflows/windows-ci.yml)

> **Know when Codex is actually working — and when it isn't.**

<p align="center">
  <img width="650" alt="Is Codex Working? tray popup" src="https://github.com/user-attachments/assets/534ca993-1a8e-4cdc-9187-19eb1bf26e55" />
</p>

<p align="center">
  <a href="https://github.com/kim-sin/is-codex-working/releases/latest"><strong>Download for Windows →</strong></a>
</p>

**Is Codex Working?** is a tiny Windows tray app that watches local Codex activity and tells you whether a task is actually progressing, waiting for you, stuck, finished, rate-limited, or idle.

No log uploads. No API key. No account login. No telemetry.

## Quick start

1. Open the [latest release](https://github.com/kim-sin/is-codex-working/releases/latest)
2. Download the attached Windows ZIP
3. Extract it
4. Double-click `RUN_ME.bat`
5. Look for the heartbeat icon near the Windows clock

Left-click the tray icon to open the compact status card. The end-user release ships a prebuilt app; it does **not** require Python or Node.js.

## What you'll see

| State | Friendly label | Meaning |
|---|---|---|
| `WORKING` | **I'm working on it!** | Real task progress is being observed |
| `WAITING FOR YOU` | **I need you!** | Codex is waiting for approval or input |
| `STUCK` | **Hmm... I'm stuck** | The task is open but real progress has stopped |
| `DONE` | **All done!** | The task completed successfully |
| `LIMIT REACHED` | **I'm out of juice** | Codex hit its usage limit |
| `ERROR` | **Oops! Something went wrong** | The task stopped with an error |
| `NO TASK` | **Nothing to do!** | No Codex task or tracked background job is running |

Independent Codex chats can light more than one state at once. Subagents inside the same chat stay grouped under their parent task. A completed task keeps its `DONE` light for five minutes.

## Why not just trust “Working”?

A live process, CPU activity, or a changing file timestamp does not prove useful work is moving forward. This app combines local Codex lifecycle events, meaningful rollout progress, parent/subagent relationships, and trusted detached-job activity before deciding what to show.

It deliberately avoids treating these signals as proof of `WORKING` on their own:

- a process merely existing
- CPU usage by itself
- JSONL modification time by itself
- UI text saying “Working”
- duplicate token counters

Cold rollout history with no recent real progress is kept out of the public status so old dangling sessions do not become fake current work.

## Multiple chats and background jobs

- Independent root chats are tracked separately
- Subagents are grouped with their parent chat
- Detached jobs launched by Codex can stay `WORKING` after the parent shell exits when their PID can be recovered from trusted tool traffic
- A detached process must keep making CPU, I/O, or trusted output progress; being alive is not enough
- One finished chat does not hide another chat that is still working

## Low-overhead by design

- `FileSystemWatcher` instead of one-second folder polling
- High-frequency writes coalesced into short batches
- Only newly appended JSONL bytes read during normal operation
- 30-second file-size/timestamp watchdog as a missed-event safety net
- Deep process checks only when a live task has been quiet long enough to need them
- Detached PIDs and trusted output logs sampled at low frequency
- Hidden tray UI does not repaint continuously

## Privacy

Local and read-only by design.

The app does **not**:

- upload Codex logs
- send telemetry
- modify rollout/session files
- require an API key
- require an account login
- control, resume, stop, or modify Codex

Copied diagnostics redact the Windows user-profile path.

## Development

The repository contains the full source, regression tests, stress tests, and build scripts.

```text
BUILD.bat
RUN_SELF_TESTS.bat
```

`RUN_SELF_TESTS.bat` builds the production and test executables and runs the parser/state regression suite. The developer stress suite additionally exercises 100 ms JSONL appends, partial writes, coalescing, watcher recovery, bounded resync, UI rendering, and a detached Python workload. Python is therefore needed only for that developer stress scenario, not for normal use of the released app.

See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

## Scope

Current release target: **Windows 10 & 11** with local Codex sessions.

This is an **unofficial community project** and is not affiliated with or endorsed by OpenAI.

## License

[MIT](LICENSE)
