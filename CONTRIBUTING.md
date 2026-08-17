# Contributing

Thanks for helping improve Is Codex Working?

Small, focused changes are preferred.

## Before opening a pull request

1. Keep the app local and read-only
2. Preserve zero-login, zero-telemetry behavior
3. Avoid polling loops when an event or bounded check can do the job
4. Never classify a task as `WORKING` from CPU, process existence, UI text, or file mtime alone
5. Keep subagents grouped under their root chat
6. Keep the main popup simple; technical evidence belongs in Details
7. Run the Windows regression tests before submitting

```bat
RUN_SELF_TESTS.bat
```

For changes to monitoring, watcher recovery, detached jobs, or performance, also run the stress suite from the test build.

## Bug reports

Useful reports include:

- Windows version
- Codex app/CLI context
- what the tray app displayed
- what Codex was actually doing
- copied diagnostics, when safe to share

Please do **not** attach private rollout contents, credentials, or personal project data unless you intentionally want to make them public.

## Pull requests

Explain:

- what changed
- why it changed
- which false-positive/false-negative or UX case it addresses
- how you tested it

Avoid unrelated cleanup in the same PR.
