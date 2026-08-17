using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace IsCodexWorking
{
    internal static class SelfTests
    {
        private static int _passed;
        private static int _failed;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Run("task_started opens turn", TestTaskStarted);
            Run("user message is not agent progress", TestUserMessageNotProgress);
            Run("response item is progress", TestResponseItemProgress);
            Run("fake terminal text in user message ignored", TestFakeTerminalText);
            Run("normal completion", TestNormalCompletion);
            Run("null completion without reply is error", TestNullCompletion);
            Run("usage limit overrides completion", TestUsageLimitAfterComplete);
            Run("request user input waits", TestWaiting);
            Run("new work clears waiting", TestWaitingClears);
            Run("error event stops the turn", TestErrorTerminal);
            Run("non-fatal rollback error does not stop the turn", TestNonFatalRollbackError);
            Run("snake-case rollback error is non-fatal", TestSnakeCaseRollbackError);
            Run("active-turn-not-steerable is non-fatal", TestActiveTurnNotSteerable);
            Run("error plus null completion stays error", TestErrorThenNullComplete);
            Run("error cannot be overwritten by later non-null completion", TestErrorThenReplyComplete);
            Run("late token event does not reopen completed turn", TestLateTokenDoesNotReopen);
            Run("system and developer context are not agent progress", TestContextMessagesNotProgress);
            Run("inherited parent history before child creation is ignored", TestInheritedParentHistoryIgnored);
            Run("child replay cannot implicitly open a live turn", TestChildReplayCannotImplicitlyOpen);
            Run("child explicit turn start opens live activity", TestChildExplicitTurnStart);
            Run("legacy child accepts activity appended after safe baseline", TestLegacyChildPostBaselineActivity);
            Run("paginated child inherited ordinals are ignored", TestPaginatedChildInheritedOrdinal);
            Run("turn aborted closes turn", TestTurnAborted);
            Run("fake usage limit in user content ignored", TestFakeUsageLimit);
            Run("nested usage limit code detected on error event", TestNestedUsageLimit);
            Run("turn completion nested error is terminal error", TestTurnCompleteNestedError);
            Run("turn completion nested usage limit is limit", TestTurnCompleteNestedUsageLimit);
            Run("session budget exceeded is a limit", TestSessionBudgetExceeded);
            Run("token count grows means progress", TestTokenCount);
            Run("duplicate token count is not progress", TestDuplicateTokenCount);
            Run("assistant message records reply", TestAssistantMessage);
            Run("tool activity is progress", TestToolProgress);
            Run("turn_started alias opens turn", TestTurnStartedAlias);
            Run("turn_complete alias closes turn", TestTurnCompleteAlias);
            Run("streaming reasoning delta is progress", TestStreamingDelta);
            Run("item lifecycle is progress without fake tool lock", TestItemLifecycle);
            Run("real tool lifecycle opens and closes tool activity", TestToolLifecycleCount);
            Run("duplicate tool begin does not leak active-tool count", TestDuplicateToolBegin);
            Run("stream error is not real progress", TestStreamErrorNotProgress);
            Run("legacy background stream retry is tracked, not progress", TestLegacyBackgroundStreamRetry);
            Run("detached Start-Process output records PID", TestDetachedStartProcessPid);
            Run("detached launch command records run root and PID together", TestDetachedLaunchIntegrated);
            Run("Wait-Process command records PID", TestWaitProcessPid);
            Run("Wait-Process matching is case and spacing tolerant", TestWaitProcessPidSpacing);
            Run("Wait-Process in a compound command records PID", TestWaitProcessPidCompound);
            Run("trusted tool command records run root", TestTrustedToolCommandRecordsRunRoot);
            Run("PID receipt under trusted run root is recovered", TestPidReceiptRecovered);
            Run("PID receipt keeps stdout and stderr evidence paths", TestPidReceiptEvidencePaths);
            Run("named PID receipt variant is recovered", TestNamedPidReceiptVariant);
            Run("assistant prose cannot fake background PID", TestAssistantProseCannotFakeBackgroundPid);
            Run("busy detached process prevents false stuck", TestBusyDetachedProcessPreventsStuck);
            Run("first detached-process sample stays working", TestFirstDetachedSampleStaysWorking);
            Run("quiet detached process stays working before stuck timeout", TestQuietDetachedProcessBeforeTimeout);
            Run("quiet detached process can become stuck", TestQuietDetachedProcessBecomesStuck);
            Run("done root stays working while detached job advances", TestDoneRootWithDetachedJobWorking);
            Run("usage limit remains visible with detached job", TestLimitStillVisibleWithDetachedJob);
            Run("repeated stream errors are tracked until progress", TestRepeatedStreamErrors);
            Run("repeated stream errors become stuck after silence", TestStreamErrorsBecomeStuck);
            Run("request permissions waits", TestRequestPermissions);
            Run("missing parent child completion is not whole-task done", TestMissingParentNotDone);
            Run("root completion remains authoritative", TestRootCompletionDone);
            Run("root completion overrides stale child open", TestRootCompletionOverridesChildOpen);
            Run("expired root terminal cannot revive stale child", TestExpiredRootTerminalDoesNotReviveChild);
            Run("working child keeps root group working", TestChildKeepsWorking);
            Run("done remains visible inside five-minute window", TestDoneVisibleInsideFiveMinutes);
            Run("done expires after five-minute window", TestDoneExpiresAfterFiveMinutes);
            Run("multiple task states can light together", TestMultipleStateLights);
            Run("status and tray glyphs render", TestStatusGlyphRendering);
            Run("working task beats newer done task in tray selection", TestWorkingBeatsNewerDone);
            Run("attention state beats working task in tray selection", TestAttentionBeatsWorking);
            Run("stale error attention expires", TestStaleErrorExpires);
            Run("old terminal state expires to idle", TestOldTerminalExpires);
            Run("stale open turn is stuck even with no attributable group", TestStaleOpenTurn);
            Run("invalid json ignored", TestInvalidJson);
            Run("oversized json rejected", TestOversizedJson);
            Run("state labels stay simple", TestLabels);
            Run("idle sentence explains no running task", TestIdleSentence);
            Run("bounded session-meta field extraction", TestMetaFieldExtraction);
            RequiredRegressionTests.Register(Run);
            Console.WriteLine("PASS " + _passed + " / " + (_passed + _failed));
            if (_failed > 0) Console.WriteLine("FAIL " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + " :: " + ex.Message);
            }
        }

        private static SessionTracker T()
        {
            SessionTracker t = new SessionTracker();
            t.Path = "test.jsonl";
            t.Meta = new SessionMeta { Id = "root", Cwd = "C:\\repo" };
            return t;
        }

        private static void Feed(SessionTracker t, string json)
        {
            MonitorEngine.ProcessLine(t, json, DateTime.UtcNow);
        }

        private static void Start(SessionTracker t)
        {
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn1\"}}");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void TestTaskStarted()
        {
            SessionTracker t = T(); Start(t);
            Assert(t.TurnOpen, "turn should be open");
            Assert(t.LastMeaningfulUtc != DateTime.MinValue, "task start should count as progress");
        }

        private static void TestUserMessageNotProgress()
        {
            SessionTracker t = T();
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"hello\"}}");
            Assert(t.LastMeaningfulUtc == DateTime.MinValue, "user message must not count as Codex work");
        }

        private static void TestResponseItemProgress()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[]}}");
            Assert(t.LastMeaningfulUtc > before, "reasoning should advance work time");
        }

        private static void TestFakeTerminalText()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"text\":\"type task_complete usage_limit_exceeded\"}]}}");
            Assert(t.TurnOpen, "user text must not close turn");
            Assert(t.Terminal == TerminalKind.None, "fake terminal text must be ignored");
        }

        private static void TestNormalCompletion()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn1\",\"last_agent_message\":\"done\"}}");
            Assert(!t.TurnOpen && t.Terminal == TerminalKind.Done, "completion should be done");
        }

        private static void TestNullCompletion()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn1\",\"last_agent_message\":null}}");
            Assert(t.Terminal == TerminalKind.Error, "silent completion should not be called normal done");
        }

        private static void TestUsageLimitAfterComplete()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"partial\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"usage_limit_exceeded\"}}");
            Assert(t.Terminal == TerminalKind.LimitReached, "limit must override prior done");
        }

        private static void TestWaiting()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"request_user_input\",\"questions\":[]}}");
            Assert(t.WaitingForUser && t.TurnOpen, "waiting should keep turn open");
        }

        private static void TestWaitingClears()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"exec_approval_request\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"exec_command_begin\"}}");
            Assert(!t.WaitingForUser, "later work should clear waiting");
        }

        private static void TestErrorTerminal()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"error\":{\"code\":\"connection_failed\"}}}");
            Assert(!t.TurnOpen, "Error event should stop the turn");
            Assert(t.Terminal == TerminalKind.Error, "Error event should become ERROR");
        }

        private static void TestNonFatalRollbackError()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"codex_error_info\":{\"type\":\"ThreadRollbackFailed\"},\"message\":\"rollback maintenance failed\"}}");
            Assert(t.TurnOpen, "non-fatal rollback housekeeping must not close the turn");
            Assert(t.Terminal == TerminalKind.None, "non-fatal rollback housekeeping must not become ERROR");
        }

        private static void TestSnakeCaseRollbackError()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"codex_error_info\":\"thread_rollback_failed\"}}");
            Assert(t.TurnOpen, "snake-case rollback maintenance error must not stop the turn");
            Assert(t.Terminal == TerminalKind.None, "snake-case rollback maintenance error must remain non-terminal");
        }

        private static void TestActiveTurnNotSteerable()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"codex_error_info\":{\"active_turn_not_steerable\":{\"turn_kind\":\"review\"}}}}");
            Assert(t.TurnOpen, "active-turn-not-steerable must not stop the current turn");
            Assert(t.Terminal == TerminalKind.None, "active-turn-not-steerable must remain non-terminal");
        }

        private static void TestErrorThenNullComplete()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"error\":{\"code\":\"fatal\"}}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":null}}");
            Assert(t.Terminal == TerminalKind.Error, "error plus silent end should be error");
        }

        private static void TestErrorThenReplyComplete()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"message\":\"fatal\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"partial\"}}");
            Assert(t.Terminal == TerminalKind.Error, "a terminal error must not be overwritten by a later completion record");
        }

        private static void TestLateTokenDoesNotReopen()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"done\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":999}}}}");
            Assert(!t.TurnOpen, "late token telemetry must not reopen a completed turn");
            Assert(t.Terminal == TerminalKind.Done, "late telemetry must not replace DONE");
        }

        private static void TestContextMessagesNotProgress()
        {
            SessionTracker t = T();
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"system\",\"content\":[]}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"developer\",\"content\":[]}}");
            Assert(!t.TurnOpen, "context messages must not invent a running turn");
            Assert(t.LastMeaningfulUtc == DateTime.MinValue, "context messages must not count as Codex work");
        }

        private static void TestInheritedParentHistoryIgnored()
        {
            SessionTracker t = T();
            t.Meta.ParentThreadId = "parent";
            t.Meta.CreatedUtc = new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc);
            Feed(t, "{\"timestamp\":\"2026-08-13T09:59:50Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[]}}");
            Assert(!t.TurnOpen, "copied parent history must not open the child turn");
            Assert(t.LastMeaningfulUtc == DateTime.MinValue, "copied parent history must not count as child progress");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_started\"}}");
            Assert(t.TurnOpen, "real child activity after creation must still work");
        }

        private static void TestChildReplayCannotImplicitlyOpen()
        {
            SessionTracker t = new SessionTracker();
            t.Path = "child.jsonl";
            t.Meta = new SessionMeta
            {
                Id = "child",
                ParentThreadId = "root",
                Cwd = "C:\\repo",
                CreatedUtc = DateTime.Parse("2026-08-13T10:00:00Z").ToUniversalTime()
            };
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[]}}");
            Assert(!t.TurnOpen, "copied child history must not implicitly create a live turn");
            Assert(t.LastMeaningfulUtc == DateTime.MinValue, "copied child output must not count as live work");
        }

        private static void TestChildExplicitTurnStart()
        {
            SessionTracker t = new SessionTracker();
            t.Path = "child.jsonl";
            t.Meta = new SessionMeta
            {
                Id = "child",
                ParentThreadId = "root",
                Cwd = "C:\\repo",
                CreatedUtc = DateTime.Parse("2026-08-13T10:00:00Z").ToUniversalTime()
            };
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"child-turn\"}}");
            Assert(t.TurnOpen && t.ExplicitTurnStartSeen, "explicit child task start must open the live turn");
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\"}}");
            Assert(t.LastMeaningfulUtc > before, "child activity after explicit start must count");
        }

        private static void TestLegacyChildPostBaselineActivity()
        {
            SessionTracker t = new SessionTracker();
            t.Path = "child.jsonl";
            t.Meta = new SessionMeta
            {
                Id = "child", ParentThreadId = "root", Cwd = "C:\\repo",
                CreatedUtc = DateTime.Parse("2026-08-13T10:00:00Z").ToUniversalTime()
            };
            t.AllowImplicitOpenAfterBaseline = true;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\"}}");
            Assert(t.TurnOpen, "new bytes after a safe legacy-child baseline must be allowed to represent live child work");
            Assert(t.LastMeaningfulUtc != DateTime.MinValue, "new post-baseline child work must advance meaningful time");
        }

        private static void TestPaginatedChildInheritedOrdinal()
        {
            SessionTracker t = new SessionTracker();
            t.Path = "child.jsonl";
            t.Meta = new SessionMeta
            {
                Id = "child",
                ParentThreadId = "root",
                Cwd = "C:\\repo",
                CreatedUtc = DateTime.Parse("2026-08-13T10:00:00Z").ToUniversalTime(),
                SubagentHistoryStartOrdinal = 3
            };
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"parent-turn\"}}");
            Assert(!t.TurnOpen, "inherited ordinal before child boundary must be ignored");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"ordinal\":3,\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"child-turn\"}}");
            Assert(t.TurnOpen, "child ordinal at boundary must be processed");
        }

        private static void TestTurnAborted()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_aborted\"}}");
            Assert(!t.TurnOpen && t.Terminal == TerminalKind.Aborted, "abort should close turn");
        }

        private static void TestFakeUsageLimit()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"text\":\"usage_limit_exceeded\"}]}}");
            Assert(t.Terminal == TerminalKind.None, "user content must not trigger limit");
        }

        private static void TestNestedUsageLimit()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"error\":{\"details\":{\"code\":\"usage_limit_exceeded\"}}}}");
            Assert(t.Terminal == TerminalKind.LimitReached, "real limit code should be detected");
            Assert(!t.TurnOpen, "limit should stop active turn");
        }

        private static void TestTurnCompleteNestedError()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_complete\",\"last_agent_message\":\"partial reply\",\"error\":{\"message\":\"upstream failed\",\"codex_error_info\":\"internal_server_error\"}}}");
            Assert(!t.TurnOpen, "turn completion with nested error must close the turn");
            Assert(t.Terminal == TerminalKind.Error, "nested completion error must win over a non-null reply");
        }

        private static void TestTurnCompleteNestedUsageLimit()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_complete\",\"error\":{\"message\":\"limit\",\"codex_error_info\":\"usage_limit_exceeded\"}}}");
            Assert(t.Terminal == TerminalKind.LimitReached, "nested usage-limit completion must map to LIMIT REACHED");
        }

        private static void TestSessionBudgetExceeded()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"codex_error_info\":\"session_budget_exceeded\"}}");
            Assert(t.Terminal == TerminalKind.LimitReached, "session budget exceeded must map to LIMIT REACHED");
        }

        private static void TestTokenCount()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":100}}}}");
            Assert(t.LastMeaningfulUtc == before, "first token count should establish a baseline, not fake progress");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":120}}}}");
            Assert(t.LastMeaningfulUtc > before, "an actual token increase should advance time");
        }

        private static void TestDuplicateTokenCount()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":120}}}}");
            DateTime first = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:08Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":120}}}}");
            Assert(t.LastMeaningfulUtc == first, "same token total must not fake progress");
        }

        private static void TestAssistantMessage()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:05Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
            Assert(t.AgentReplySeenSinceTaskStart, "assistant reply should be recorded");
        }

        private static void TestToolProgress()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:06Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"mcp_tool_call_begin\",\"call_id\":\"x\"}}");
            Assert(t.LastMeaningfulUtc > before, "tool activity should be progress");
        }

        private static void TestTurnStartedAlias()
        {
            SessionTracker t = T();
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_started\",\"turn_id\":\"turn1\"}}");
            Assert(t.TurnOpen, "turn_started must open the turn");
        }

        private static void TestTurnCompleteAlias()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_complete\",\"last_agent_message\":\"done\"}}");
            Assert(!t.TurnOpen && t.Terminal == TerminalKind.Done, "turn_complete must close the turn");
        }

        private static void TestStreamingDelta()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\",\"delta\":\"x\"}}");
            Assert(t.LastMeaningfulUtc > before, "reasoning delta should count as real activity");
        }

        private static void TestItemLifecycle()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_started\",\"item\":{\"type\":\"command_execution\"}}}");
            Assert(t.LastMeaningfulUtc > before, "item_started should count as real activity");
            Assert(t.ActiveToolCount == 0, "generic item lifecycle must not create a permanent tool lock");
        }

        private static void TestToolLifecycleCount()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"mcp_tool_call_begin\",\"call_id\":\"x\"}}");
            Assert(t.ActiveToolCount == 1, "tool begin should mark one active tool");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"mcp_tool_call_end\",\"call_id\":\"x\"}}");
            Assert(t.ActiveToolCount == 0, "tool end should clear active tool state");
        }

        private static void TestDuplicateToolBegin()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"exec_command_begin\",\"call_id\":\"same\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02.5Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"exec_command_begin\",\"call_id\":\"same\"}}");
            Assert(t.ActiveToolCount == 1, "duplicate begin for the same call_id must count once");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"exec_command_end\",\"call_id\":\"same\"}}");
            Assert(t.ActiveToolCount == 0, "one matching end must clear the duplicate-suppressed tool");
        }

        private static void TestStreamErrorNotProgress()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"stream_error\",\"message\":\"retrying\"}}");
            Assert(t.LastMeaningfulUtc == before, "retry stream errors must not fake forward progress");
        }

        private static void TestLegacyBackgroundStreamRetry()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"background_event\",\"message\":\"stream error: stream disconnected before completion; retrying 1/5\"}}");
            Assert(t.StreamErrorsSinceProgress == 1, "legacy background retry should be tracked");
            Assert(t.LastMeaningfulUtc == before, "legacy background retry must not fake forward progress");
        }

        private static void TestDetachedStartProcessPid()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"bg1\",\"arguments\":\"$p=Start-Process -FilePath $py -PassThru; $p.Id\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"bg1\",\"output\":\"13216\\n\"}}");
            Assert(t.BackgroundProcesses.ContainsKey(13216), "Start-Process output PID should be tracked");
        }

        private static void TestDetachedLaunchIntegrated()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"bg2\",\"arguments\":\"$run='C:\\\\work\\\\RUN_002'; $proc=Start-Process -FilePath $py -PassThru; [pscustomobject]@{pid=$proc.Id}|ConvertTo-Json; $proc.Id\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"bg2\",\"output\":\"{\\\"pid\\\":24680}\\n24680\\n\"}}");
            Assert(t.BackgroundRoots.Contains("C:\\work\\RUN_002"), "detached launch should remember its run root");
            Assert(t.BackgroundProcesses.ContainsKey(24680), "detached launch should remember the returned PID");
        }

        private static void TestWaitProcessPid()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"wait1\",\"arguments\":\"Wait-Process -Id 15048; 'PROCESS_COMPLETE'\"}}");
            Assert(t.BackgroundProcesses.ContainsKey(15048), "Wait-Process PID should be tracked as a background job hint");
        }

        private static void TestWaitProcessPidSpacing()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"wait2\",\"arguments\":\"wait-process    -id    16044\"}}");
            Assert(t.BackgroundProcesses.ContainsKey(16044), "Wait-Process matching should ignore case and extra spaces");
        }

        private static void TestWaitProcessPidCompound()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"wait3\",\"arguments\":\"$root='C:\\\\work\\\\RUN'; Wait-Process -Id 10108; 'PROCESS_COMPLETE'\"}}");
            Assert(t.BackgroundProcesses.ContainsKey(10108), "Wait-Process should be detected inside a compound tool command");
        }

        private static void TestTrustedToolCommandRecordsRunRoot()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"root1\",\"arguments\":\"$run='C:\\\\work\\\\RUN_001'; Wait-Process -Id 15048\"}}");
            Assert(t.BackgroundRoots.Contains("C:\\work\\RUN_001"), "trusted run-root assignment should be remembered");
        }

        private static void TestPidReceiptRecovered()
        {
            string temp = Path.Combine(Path.GetTempPath(), "icw-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temp, "00_STATE"));
            try
            {
                string pidFile = Path.Combine(temp, "00_STATE", "RECOVERY_PID.json");
                string stdout = Path.Combine(temp, "run.stdout.log");
                string stderr = Path.Combine(temp, "run.stderr.log");
                File.WriteAllText(pidFile, "{\"pid\":43210,\"started_at\":\"" + DateTime.UtcNow.ToString("o") +
                    "\",\"stdout\":\"" + stdout.Replace("\\", "\\\\") + "\",\"stderr\":\"" + stderr.Replace("\\", "\\\\") + "\"}");
                File.WriteAllText(Path.Combine(temp, "00_STATE", "HEARTBEAT_PID.txt"), "54321");
                SessionTracker root = DirectTracker("root", null, true, TerminalKind.None, 15 * 60);
                root.BackgroundRoots.Add(temp);
                Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
                using (MonitorEngine engine = new MonitorEngine())
                {
                    engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                    StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                    Assert(root.BackgroundProcesses.ContainsKey(43210), "PID receipt should be recovered from the trusted 00_STATE folder");
                    Assert(!root.BackgroundProcesses.ContainsKey(54321), "heartbeat/watcher PID receipts must not be mistaken for work");
                    Assert(snapshot.State == PublicState.Working, "recovered background PID should protect a live background job from false STUCK");
                }
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private static void TestPidReceiptEvidencePaths()
        {
            string temp = Path.Combine(Path.GetTempPath(), "icw-selftest-paths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temp, "00_STATE"));
            try
            {
                string stdout = Path.Combine(temp, "stdout.log");
                string stderr = Path.Combine(temp, "stderr.log");
                string payload = "{\"pid\":43211,\"started_at\":\"" + DateTime.UtcNow.ToString("o") +
                    "\",\"stdout\":\"" + stdout.Replace("\\", "\\\\") + "\",\"stderr\":\"" + stderr.Replace("\\", "\\\\") + "\"}";
                File.WriteAllText(Path.Combine(temp, "00_STATE", "JOB_PID.json"), payload);
                SessionTracker root = DirectTracker("root", null, true, TerminalKind.None, 15 * 60);
                root.BackgroundRoots.Add(temp);
                Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
                using (MonitorEngine engine = new MonitorEngine())
                {
                    engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                    engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                    BackgroundProcessHint hint;
                    Assert(root.BackgroundProcesses.TryGetValue(43211, out hint), "PID receipt should create a background hint");
                    Assert(string.Equals(hint.StdoutPath, stdout, StringComparison.OrdinalIgnoreCase), "stdout evidence path should be preserved");
                    Assert(string.Equals(hint.StderrPath, stderr, StringComparison.OrdinalIgnoreCase), "stderr evidence path should be preserved");
                }
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        private static void TestNamedPidReceiptVariant()
        {
            string temp = Path.Combine(Path.GetTempPath(), "icw-selftest-named-pid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temp, "00_STATE"));
            try
            {
                string stdout = Path.Combine(temp, "00_STATE", "LONG_JOB_STDOUT.log");
                string stderr = Path.Combine(temp, "00_STATE", "LONG_JOB_STDERR.log");
                string payload = "{\"pid\":47654,\"started_at\":\"" + DateTime.UtcNow.ToString("o") +
                    "\",\"command\":\"long_job.py\",\"stdout\":\"" + stdout.Replace("\\", "\\\\") +
                    "\",\"stderr\":\"" + stderr.Replace("\\", "\\\\") + "\"}";
                File.WriteAllText(Path.Combine(temp, "00_STATE", "LONG_JOB_PID.json"), payload);
                SessionTracker root = DirectTracker("root", null, true, TerminalKind.None, 15 * 60);
                root.BackgroundRoots.Add(temp);
                Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
                using (MonitorEngine engine = new MonitorEngine())
                {
                    engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                    StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                    BackgroundProcessHint hint;
                    Assert(root.BackgroundProcesses.TryGetValue(47654, out hint), "LONG_JOB_PID.json should be accepted as a work PID receipt");
                    Assert(string.Equals(hint.StdoutPath, stdout, StringComparison.OrdinalIgnoreCase), "named PID receipt stdout path should be kept");
                    Assert(string.Equals(hint.StderrPath, stderr, StringComparison.OrdinalIgnoreCase), "named PID receipt stderr path should be kept");
                    Assert(snapshot.State == PublicState.Working, "an advancing named background process must be WORKING, not STUCK");
                }
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        private static void TestAssistantProseCannotFakeBackgroundPid()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"text\":\"Run Start-Process -PassThru and PID 9999\"}]}}");
            Assert(!t.BackgroundProcesses.ContainsKey(9999), "assistant prose must not create a process hint");
        }

        private static SessionTracker TrackerWithBackground(int secondsAgo, TerminalKind terminal)
        {
            SessionTracker root = DirectTracker("root", null, terminal == TerminalKind.None, terminal, secondsAgo);
            root.BackgroundProcesses[43210] = new BackgroundProcessHint
            {
                Pid = 43210,
                ObservedUtc = DateTime.UtcNow,
                LaunchUtc = DateTime.MinValue,
                Source = "test"
            };
            return root;
        }

        private static BackgroundProbeResult FakeBackground(bool busy, bool comparison, int quiet)
        {
            return new BackgroundProbeResult
            {
                Available = true,
                AnyAlive = true,
                Busy = busy,
                HasComparison = comparison,
                AliveProcessCount = 1,
                ConsecutiveQuietSamples = quiet,
                LastProgressUtc = busy ? DateTime.UtcNow : DateTime.MinValue,
                Note = "test"
            };
        }

        private static void TestFirstDetachedSampleStaysWorking()
        {
            SessionTracker root = TrackerWithBackground(15 * 60, TerminalKind.None);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(false, false, 0);
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State == PublicState.Working, "the first live detached-process sample must not be called STUCK before a comparison exists");
            }
        }

        private static void TestBusyDetachedProcessPreventsStuck()
        {
            SessionTracker root = TrackerWithBackground(15 * 60, TerminalKind.None);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State == PublicState.Working, "busy detached work must prevent a false STUCK state");
                Assert(snapshot.BackgroundProcessAlive && snapshot.BackgroundProcessBusy, "background diagnostics should show progress");
            }
        }

        private static void TestQuietDetachedProcessBeforeTimeout()
        {
            SessionTracker root = TrackerWithBackground(2 * 60, TerminalKind.None);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(false, true, 3);
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State == PublicState.Working, "a live tracked background process must not fall to IDLE before the stuck timeout");
            }
        }

        private static void TestQuietDetachedProcessBecomesStuck()
        {
            SessionTracker root = TrackerWithBackground(15 * 60, TerminalKind.None);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(false, true, 3);
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State == PublicState.Stuck, "three quiet detached-process samples after long silence should become STUCK");
            }
        }

        private static void TestDoneRootWithDetachedJobWorking()
        {
            SessionTracker root = TrackerWithBackground(1, TerminalKind.Done);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Working, "a completed Codex turn may still have an advancing detached job");
            }
        }

        private static void TestLimitStillVisibleWithDetachedJob()
        {
            SessionTracker root = TrackerWithBackground(1, TerminalKind.LimitReached);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(); map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.LimitReached, "a usage-limit signal must remain visible even if a detached job continues");
            }
        }

        private static void TestRepeatedStreamErrors()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"stream_error\",\"message\":\"retrying\"}}");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:20Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"stream_error\",\"message\":\"retrying\"}}");
            Assert(t.StreamErrorsSinceProgress == 2, "stream retry count should be tracked");
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:21Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\",\"delta\":\"x\"}}");
            Assert(t.StreamErrorsSinceProgress == 0, "real progress should clear stream retry state");
        }

        private static void TestStreamErrorsBecomeStuck()
        {
            SessionTracker root = DirectTracker("root", null, true, TerminalKind.None, 90);
            root.StreamErrorsSinceProgress = 2;
            root.LastStreamErrorUtc = DateTime.UtcNow - TimeSpan.FromSeconds(5);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            MonitorEngine engine = new MonitorEngine();
            try
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State == PublicState.Stuck, "repeated connection retries with no progress must become STUCK");
            }
            finally { engine.Dispose(); }
        }

        private static void TestRequestPermissions()
        {
            SessionTracker t = T(); Start(t);
            Feed(t, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"request_permissions\"}}");
            Assert(t.WaitingForUser && t.TurnOpen, "permission request should wait for the user");
        }

        private static SessionTracker DirectTracker(string id, string parentId, bool open, TerminalKind terminal, int secondsAgo)
        {
            SessionTracker t = new SessionTracker();
            t.Path = id + ".jsonl";
            t.Meta = new SessionMeta { Id = id, ParentThreadId = parentId, Cwd = "C:\\repo" };
            t.TurnOpen = open;
            t.Terminal = terminal;
            t.LastMeaningfulUtc = DateTime.UtcNow - TimeSpan.FromSeconds(secondsAgo);
            t.LastAnyUtc = t.LastMeaningfulUtc;
            t.LastTerminalUtc = terminal == TerminalKind.None ? DateTime.MinValue : t.LastMeaningfulUtc;
            return t;
        }

        private static void TestMissingParentNotDone()
        {
            SessionTracker child = DirectTracker("child", "missing-root", false, TerminalKind.Done, 1);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["child"] = child;
            MonitorEngine engine = new MonitorEngine();
            try
            {
                StatusSnapshot s = engine.BuildGroupForTests("missing-root", new List<SessionTracker> { child }, map, 0, 1);
                Assert(s.State != PublicState.Done, "a child completion must not become whole-task DONE when the parent is unknown");
            }
            finally { engine.Dispose(); }
        }

        private static void TestRootCompletionDone()
        {
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.Done, 1);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            MonitorEngine engine = new MonitorEngine();
            try
            {
                StatusSnapshot s = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(s.State == PublicState.Done, "known root completion should be DONE");
            }
            finally { engine.Dispose(); }
        }

        private static void TestRootCompletionOverridesChildOpen()
        {
            // The child is intentionally older than the authoritative root
            // terminal. A stale open flag must not revive the completed group.
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.Done, 30);
            SessionTracker child = DirectTracker("child", "root", true, TerminalKind.None, 60);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase);
            map["root"] = root; map["child"] = child;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root, child }, map, 1, 1);
                Assert(snapshot.State == PublicState.Done, "root completion must override a stale child open turn");
            }
        }

        private static void TestExpiredRootTerminalDoesNotReviveChild()
        {
            SessionTracker root = T();
            root.Terminal = TerminalKind.Done;
            root.TerminalReason = "done";
            root.LastTerminalUtc = DateTime.UtcNow - TimeSpan.FromMinutes(20);
            root.TurnOpen = false;
            SessionTracker child = new SessionTracker();
            child.Path = "child.jsonl";
            child.Meta = new SessionMeta { Id = "child", ParentThreadId = "root", Cwd = "C:\\repo" };
            child.TurnOpen = true;
            child.ExplicitTurnStartSeen = true;
            child.LastMeaningfulUtc = DateTime.UtcNow;
            child.LastAnyUtc = child.LastMeaningfulUtc;
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase);
            map["root"] = root; map["child"] = child;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot s = engine.BuildGroupForTests("root", new List<SessionTracker> { root, child }, map, 1, 1);
                Assert(s.State == PublicState.Idle, "an expired root terminal must not let stale child activity revive the task");
            }
        }

        private static void TestChildKeepsWorking()
        {
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.None, 2);
            SessionTracker child = DirectTracker("child", "root", true, TerminalKind.None, 1);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root; map["child"] = child;
            MonitorEngine engine = new MonitorEngine();
            try
            {
                StatusSnapshot s = engine.BuildGroupForTests("root", new List<SessionTracker> { root, child }, map, 1, 1);
                Assert(s.State == PublicState.Working, "an open child must keep the grouped task working");
            }
            finally { engine.Dispose(); }
        }

        private static void TestDoneVisibleInsideFiveMinutes()
        {
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.Done, 4 * 60 + 50);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Done, "DONE should remain visible before five minutes");
            }
        }

        private static void TestDoneExpiresAfterFiveMinutes()
        {
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.Done, 5 * 60 + 10);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Idle, "DONE should age out after five minutes");
            }
        }

        private static void TestMultipleStateLights()
        {
            StatusSnapshot snapshot = new StatusSnapshot();
            snapshot.State = PublicState.Working;
            snapshot.ActiveStatesMask = StatusSnapshot.StateBit(PublicState.Working) | StatusSnapshot.StateBit(PublicState.Done);
            Assert(snapshot.IsStateLit(PublicState.Working), "WORKING light should be on");
            Assert(snapshot.IsStateLit(PublicState.Done), "DONE light should be on at the same time");
            Assert(!snapshot.IsStateLit(PublicState.Stuck), "unrelated state should stay dim");
        }

        private static void TestStatusGlyphRendering()
        {
            foreach (PublicState state in Enum.GetValues(typeof(PublicState)))
            {
                using (Bitmap bitmap = new Bitmap(64, 64))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    StatusPainter.Draw(graphics, state, new RectangleF(8, 8, 32, 32), true);
                    StatusPainter.Draw(graphics, state, new RectangleF(8, 8, 32, 32), false);
                }
                using (Icon icon = StatusPainter.CreateTrayIcon(state))
                    Assert(icon != null, "tray icon rendering returned null for " + state);
            }
        }

        private static void TestWorkingBeatsNewerDone()
        {
            DateTime now = DateTime.UtcNow;
            bool doneBeatsWorking = MonitorEngine.IsBetterStateForTests(PublicState.Done, now,
                PublicState.Working, now - TimeSpan.FromMinutes(8));
            bool workingBeatsDone = MonitorEngine.IsBetterStateForTests(PublicState.Working, now - TimeSpan.FromMinutes(8),
                PublicState.Done, now);
            Assert(!doneBeatsWorking, "a recent DONE task must not hide another WORKING task");
            Assert(workingBeatsDone, "WORKING must stay primary while another task is DONE");
        }

        private static void TestAttentionBeatsWorking()
        {
            DateTime now = DateTime.UtcNow;
            Assert(MonitorEngine.IsBetterStateForTests(PublicState.WaitingForYou, now - TimeSpan.FromMinutes(1),
                PublicState.Working, now), "WAITING FOR YOU should outrank WORKING");
            Assert(MonitorEngine.IsBetterStateForTests(PublicState.Stuck, now - TimeSpan.FromMinutes(1),
                PublicState.Working, now), "STUCK should outrank WORKING");
            Assert(MonitorEngine.IsBetterStateForTests(PublicState.Error, now - TimeSpan.FromMinutes(1),
                PublicState.Working, now), "ERROR should outrank WORKING");
        }

        private static void TestStaleErrorExpires()
        {
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.Error, 11 * 60);
            root.TerminalReason = "error";
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Idle, "stale terminal errors should not dominate the tray forever");

                SessionTracker limited = DirectTracker("limited", null, false, TerminalKind.LimitReached, 11 * 60);
                Dictionary<string, SessionTracker> limitMap = new Dictionary<string, SessionTracker>();
                limitMap["limited"] = limited;
                StatusSnapshot limitSnapshot = engine.BuildGroupForTests("limited", new List<SessionTracker> { limited }, limitMap, 0, 1);
                Assert(limitSnapshot.State == PublicState.Idle, "stale usage-limit terminals should not dominate the tray forever");
            }
        }

        private static void TestOldTerminalExpires()
        {
            SessionTracker root = DirectTracker("root", null, false, TerminalKind.Done, 20 * 60);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            MonitorEngine engine = new MonitorEngine();
            try
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Idle, "old completed work should age out to IDLE");
            }
            finally { engine.Dispose(); }
        }

        private static void TestStaleOpenTurn()
        {
            SessionTracker root = DirectTracker("root", null, true, TerminalKind.None, 15 * 60);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map["root"] = root;
            MonitorEngine engine = new MonitorEngine();
            try
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests("root", new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Stuck, "a stale open turn must not remain WORKING when attribution aged out");
            }
            finally { engine.Dispose(); }
        }

        private static void TestInvalidJson()
        {
            SessionTracker t = T(); Start(t);
            DateTime before = t.LastMeaningfulUtc;
            Feed(t, "not-json");
            Assert(t.LastMeaningfulUtc == before, "bad line should be ignored");
        }

        private static void TestOversizedJson()
        {
            string huge = "{\"x\":\"" + new string('a', 1024 * 1024 + 10) + "\"}";
            Assert(JsonUtil.ParseObject(huge) == null, "oversized line must be rejected");
        }


        private static void TestMetaFieldExtraction()
        {
            string prefix = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"abc\",\"parent_thread_id\":\"root\",\"cwd\":\"C:\\\\repo\",\"base_instructions\":\"";
            Assert(JsonUtil.ExtractJsonStringField(prefix, "id") == "abc", "id extraction failed");
            Assert(JsonUtil.ExtractJsonStringField(prefix, "parent_thread_id") == "root", "parent extraction failed");
            Assert(JsonUtil.ExtractJsonStringField(prefix, "cwd") == "C:\\repo", "cwd extraction failed");
        }

        private static void TestIdleSentence()
        {
            StatusSnapshot s = new StatusSnapshot();
            s.State = PublicState.Idle;
            Assert(s.Sentence == "No Codex task is running", "IDLE should explain that no task is running");
        }

        private static void TestLabels()
        {
            StatusSnapshot s = new StatusSnapshot();
            s.State = PublicState.WaitingForYou;
            Assert(s.Sentence == "Codex is waiting for you", "waiting copy changed");
            s.State = PublicState.LimitReached;
            Assert(s.Sentence == "Usage limit reached", "limit copy changed");
        }
    }
}
