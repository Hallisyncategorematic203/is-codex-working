# Contributing

Small, focused changes are preferred.

Before opening a pull request:

1. Keep the app local and read-only
2. Avoid polling loops when an event or bounded check can do the job
3. Do not classify a task as working from CPU, process existence, or file mtime alone
4. Keep the main popup simple; put technical evidence in Details
5. Run `RUN_SELF_TESTS.bat` on Windows

Bug reports are most useful with copied diagnostics and a description of what Codex was actually doing. Do not attach private rollout contents unless you intentionally want to share them.
