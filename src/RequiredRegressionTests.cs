using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace IsCodexWorking
{
    // Contract-focused coverage for the v0.5 multi-chat, lifecycle, identity,
    // parser, and UI changes. The original characterization tests remain in
    // SelfTests.cs; these cases are additive and are registered from there.
    internal static class RequiredRegressionTests
    {
        private enum FixtureTerminal
        {
            MetadataOnly,
            Working,
            Done,
            Error
        }

        public static void Register(Action<string, Action> run)
        {
            run("aggregate keeps WORKING and DONE lights with WORKING primary", TestAggregateStates);
            run("DONE group remains retained after tracker removal", TestDoneRetention);
            run("group notifications baseline and new completion lifecycle", TestGroupNotifications);
            run("same-state completion in one batch notifies once", TestAtomicSameStateCompletionNotification);
            run("root and child lineage form one group", TestRootChildLineage);
            run("post-completion child activity reopens the root group", TestPostCompletionChildActivity);
            run("child completion defines the grouped DONE boundary", TestPostCompletionChildDoneBoundary);
            run("orphan child keeps its declared parent group", TestOrphanChildParentGroup);
            run("duplicate session IDs stay isolated", TestDuplicateIdIsolation);
            run("three duplicate session IDs stay isolated", TestThreeDuplicateIdIsolation);
            run("incomplete session metadata is refreshed safely", TestIncompleteSessionMetadata);
            run("stale turn events cannot affect the new turn", TestStaleTurnEvent);
            run("out-of-order lifecycle starts and terminal history are ignored", TestOutOfOrderLifecycle);
            run("18-hour dangling sessions stay internal stale history", TestOldDanglingSessionExcluded);
            run("cold stale session with unverified background remains visible", TestStaleSessionWithUnverifiedBackground);
            run("background completion starts its own effective DONE time", TestBackgroundEffectiveCompletion);
            run("background forward progress updates last-work time", TestBackgroundProgressUpdatesLastWork);
            run("quiet background baseline does not claim real progress", TestQuietBackgroundBaselineCopy);
            run("unknown background identity never reveals DONE", TestUnknownBackgroundIdentity);
            run("stale terminal PID receipt does not create work", TestStalePidReceipt);
            run("nested PID receipt metadata cannot create work", TestNestedPidReceiptMetadata);
            run("nested Wait-Process metadata cannot create work", TestNestedWaitProcessMetadata);
            run("generic Codex CPU alone cannot prove WORKING", TestGenericCpuDoesNotProveWorking);
            run("generic CPU cannot protect an idle active tool", TestGenericCpuCannotProtectTool);
            run("PID start identity mismatch is rejected", TestPidReuseRejected);
            run("nested tool metadata cannot fake a detached PID", TestNestedToolMetadataPid);
            run("background output progress requires forward evidence", TestBackgroundOutputProgressEvidence);
            run("partial append and duplicate watcher reads are safe", TestPartialAppendAndDuplicateRead);
            run("truncate and replacement resync the parser", TestTruncateAndReplacement);
            run("watcher error requests bounded resync", TestWatcherRecovery);
            run("large append is consumed incrementally", TestLargeAppend);
            run("large existing rollout uses bounded initial read", TestLargeExistingRollout);
            run("group and status snapshots are immutable copies", TestSnapshotImmutability);
            run("public labels and tray colors share the contract", TestPublicCopyAndPalette);
            run("multi-group popup renders compact rows", TestMultiGroupPopupRendering);
            run("latest waiting activity selects the representative group", TestLatestWaitingRepresentative);
        }

        private static void TestAggregateStates()
        {
            string temp = NewTemp("aggregate");
            try
            {
                string root = Path.Combine(temp, "root.jsonl");
                string child = Path.Combine(temp, "child.jsonl");
                string done = Path.Combine(temp, "done.jsonl");
                string sameProject = Path.Combine(temp, "same-project.jsonl");
                WriteSession(root, "root", null, "C:\\repo", FixtureTerminal.Working);
                WriteSession(child, "child", "root", "C:\\repo", FixtureTerminal.MetadataOnly);
                WriteSession(done, "done", null, "C:\\repo", FixtureTerminal.Done);
                WriteSession(sameProject, "same-project", null, "C:\\repo", FixtureTerminal.Done);

                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(root);
                    engine.LoadPathForTests(child);
                    engine.LoadPathForTests(done);
                    engine.LoadPathForTests(sameProject);
                    engine.RecomputeForTests(true);
                    StatusSnapshot snapshot = engine.Current;
                    Assert(snapshot.PrimaryState == PublicState.Working, "WORKING must be the representative state");
                    Assert(snapshot.IsStateLit(PublicState.Working) && snapshot.IsStateLit(PublicState.Done),
                        "WORKING and DONE must be lit together");
                    Assert(snapshot.Groups.Length == 3,
                        "root plus child must be one group while two independent roots remain separate");
                    Assert(snapshot.Groups.Count(g => g != null && g.Project == "repo") == 3,
                        "same project names must not merge independent roots");
                    Assert(snapshot.Groups.Count(g => g != null && g.RootId == "root") == 1,
                        "the root group must remain addressable by its root ID");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestDoneRetention()
        {
            string temp = NewTemp("done-retention");
            try
            {
                string path = Path.Combine(temp, "done.jsonl");
                WriteSession(path, "done-retained", null, "C:\\repo", FixtureTerminal.Done);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    Assert(engine.Current.Groups.Length == 1 && engine.Current.PrimaryState == PublicState.Done,
                        "completed group should be visible before retention expiry");
                    engine.RemovePathForTests(path);
                    engine.RecomputeForTests(false);
                    StatusSnapshot retained = engine.Current;
                    Assert(retained.Groups.Length == 1 && retained.Groups[0].State == PublicState.Done,
                        "removing the live tracker must not remove its five-minute DONE row");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestGroupNotifications()
        {
            string temp = NewTemp("notifications");
            try
            {
                string a = Path.Combine(temp, "a.jsonl");
                string b = Path.Combine(temp, "b.jsonl");
                string c = Path.Combine(temp, "c.jsonl");
                string error = Path.Combine(temp, "error.jsonl");
                WriteSession(a, "chat-a", null, "C:\\repo", FixtureTerminal.Working);
                WriteSession(b, "chat-b", null, "C:\\repo", FixtureTerminal.Working);
                WriteSession(c, "chat-c", null, "C:\\repo", FixtureTerminal.Done);
                WriteSession(error, "chat-error", null, "C:\\repo", FixtureTerminal.Error);

                List<GroupStatusSnapshot> notifications = new List<GroupStatusSnapshot>();
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.GroupNotification += delegate(GroupStatusSnapshot group)
                    {
                        notifications.Add(group == null ? null : group.Clone());
                    };
                    engine.LoadPathForTests(a);
                    engine.LoadPathForTests(b);
                    engine.LoadPathForTests(c);
                    engine.LoadPathForTests(error);
                    engine.RecomputeForTests(true);
                    Assert(notifications.Count == 0, "startup discovery must establish a notification baseline only");

                    AppendLine(b, CompleteLine("turn1", DateTime.UtcNow.AddSeconds(1), "done"));
                    engine.LoadPathForTests(b);
                    engine.RecomputeForTests(false);
                    Assert(notifications.Count == 1 && notifications[0].State == PublicState.Done,
                        "a newly completed group must notify exactly once even when DONE was already lit elsewhere");
                    engine.RecomputeForTests(false);
                    Assert(notifications.Count == 1, "unchanged group state must not duplicate notifications");

                    AppendLine(b, StartLine("turn2", DateTime.UtcNow.AddSeconds(2)));
                    AppendLine(b, AssistantLine(DateTime.UtcNow.AddSeconds(3), "turn2 work"));
                    engine.LoadPathForTests(b);
                    engine.RecomputeForTests(false);
                    Assert(FindGroup(engine.Current, "chat-b").State == PublicState.Working,
                        "new work must clear the prior DONE state for the same group");

                    AppendLine(b, CompleteLine("turn2", DateTime.UtcNow.AddSeconds(4), "done again"));
                    engine.LoadPathForTests(b);
                    engine.RecomputeForTests(false);
                    Assert(notifications.Count == 2,
                        "DONE after a new WORKING turn must be a new completion notification");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestAtomicSameStateCompletionNotification()
        {
            string temp = NewTemp("atomic-done");
            try
            {
                string path = Path.Combine(temp, "chat.jsonl");
                WriteSession(path, "atomic-chat", null, "C:\\repo", FixtureTerminal.Done);
                List<GroupStatusSnapshot> notifications = new List<GroupStatusSnapshot>();
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.GroupNotification += delegate(GroupStatusSnapshot group)
                    {
                        if (group != null) notifications.Add(group.Clone());
                    };
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    DateTime oldCompletion = engine.Current.Groups[0].EffectiveCompletionUtc;

                    DateTime now = DateTime.UtcNow.AddSeconds(2);
                    AppendLine(path, StartLine("turn2", now));
                    AppendLine(path, AssistantLine(now.AddMilliseconds(1), "second reply"));
                    AppendLine(path, CompleteLine("turn2", now.AddMilliseconds(2), "done again"));
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(false);
                    Assert(notifications.Count == 1 && notifications[0].State == PublicState.Done,
                        "a new DONE lifecycle must notify even when the final public state is still DONE");
                    Assert(engine.Current.Groups[0].EffectiveCompletionUtc > oldCompletion,
                        "a new completion must advance the effective completion boundary");
                    engine.RecomputeForTests(false);
                    Assert(notifications.Count == 1,
                        "re-reading the same completion must not duplicate the notification");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestRootChildLineage()
        {
            string temp = NewTemp("lineage");
            try
            {
                string root = Path.Combine(temp, "root.jsonl");
                string child = Path.Combine(temp, "child.jsonl");
                WriteSession(root, "root-lineage", null, "C:\\project", FixtureTerminal.Working);
                WriteSession(child, "child-lineage", "root-lineage", "C:\\project", FixtureTerminal.Done);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(root);
                    engine.LoadPathForTests(child);
                    engine.RecomputeForTests(true);
                    StatusSnapshot snapshot = engine.Current;
                    Assert(snapshot.Groups.Length == 1, "root and child must be represented by one visible group");
                    Assert(snapshot.PrimaryState == PublicState.Working,
                        "child DONE must not promote a still-working root to DONE");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestPostCompletionChildActivity()
        {
            SessionTracker root = NewDirectTracker("post-root", false, TerminalKind.Done, 30);
            SessionTracker child = NewDirectTracker("post-child", true, TerminalKind.None, 1);
            child.Meta.ParentThreadId = root.Id;
            child.ExplicitTurnStartSeen = true;
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase);
            map[root.Id] = root;
            map[child.Id] = child;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id,
                    new List<SessionTracker> { root, child }, map, 1, 1);
                Assert(snapshot.State == PublicState.Working,
                    "a child turn with activity after root DONE must keep the root group WORKING");
            }
        }

        private static void TestPostCompletionChildDoneBoundary()
        {
            SessionTracker root = NewDirectTracker("post-done-root", false, TerminalKind.Done, 30);
            SessionTracker child = NewDirectTracker("post-done-child", false, TerminalKind.Done, 1);
            child.Meta.ParentThreadId = root.Id;
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase);
            map[root.Id] = root;
            map[child.Id] = child;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id,
                    new List<SessionTracker> { root, child }, map, 0, 1);
                Assert(snapshot.State == PublicState.Done &&
                    snapshot.Groups[0].EffectiveCompletionUtc == child.LastTerminalUtc,
                    "grouped DONE retention must begin at the latest child completion");
            }
        }

        private static void TestOrphanChildParentGroup()
        {
            string temp = NewTemp("orphan-parent");
            try
            {
                string child = Path.Combine(temp, "child.jsonl");
                WriteSession(child, "orphan-child", "declared-root", "C:\\repo", FixtureTerminal.MetadataOnly);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(child);
                    AppendLine(child, StartLine("child-turn", DateTime.UtcNow));
                    AppendLine(child, ReasoningLine(DateTime.UtcNow.AddMilliseconds(1), "child progress"));
                    engine.LoadPathForTests(child);
                    engine.RecomputeForTests(true);
                    Assert(engine.Current.Groups.Length == 1 &&
                        engine.Current.Groups[0].RootId == "declared-root" &&
                        engine.Current.PrimaryState == PublicState.Working,
                        "a child must remain under its declared root while the parent file is undiscovered");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestDuplicateIdIsolation()
        {
            string temp = NewTemp("duplicate-id");
            try
            {
                string one = Path.Combine(temp, "one.jsonl");
                string two = Path.Combine(temp, "two.jsonl");
                WriteSession(one, "duplicate", null, "C:\\same-project", FixtureTerminal.Working);
                WriteSession(two, "duplicate", null, "C:\\same-project", FixtureTerminal.Working);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(one);
                    engine.LoadPathForTests(two);
                    engine.RecomputeForTests(true);
                    StatusSnapshot snapshot = engine.Current;
                    Assert(snapshot.Groups.Length == 2,
                        "ambiguous duplicate IDs must be isolated instead of merged by identifier");
                    Assert(snapshot.Groups.All(g => g.RootId != "duplicate"),
                        "ambiguous roots must use a unique path-derived identity");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestThreeDuplicateIdIsolation()
        {
            string temp = NewTemp("triple-duplicate-id");
            try
            {
                string one = Path.Combine(temp, "one.jsonl");
                string two = Path.Combine(temp, "two.jsonl");
                string three = Path.Combine(temp, "three.jsonl");
                WriteSession(one, "duplicate-three", null, "C:\\same-project", FixtureTerminal.Working);
                WriteSession(two, "duplicate-three", null, "C:\\same-project", FixtureTerminal.Working);
                WriteSession(three, "duplicate-three", null, "C:\\same-project", FixtureTerminal.Working);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(one);
                    engine.LoadPathForTests(two);
                    engine.LoadPathForTests(three);
                    engine.RecomputeForTests(true);
                    Assert(engine.Current.Groups.Length == 3 &&
                        engine.Current.Groups.All(g => g != null && g.RootId != "duplicate-three"),
                        "an ID that was ambiguous once must remain path-isolated for later files");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestIncompleteSessionMetadata()
        {
            string temp = NewTemp("incomplete-meta");
            try
            {
                string path = Path.Combine(temp, "child.jsonl");
                string prefix = "{\"timestamp\":\"" + Stamp(DateTime.UtcNow) +
                    "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"partial-child\"";
                File.WriteAllText(path, prefix, new UTF8Encoding(false));
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    AppendText(path, ",\"parent_thread_id\":\"partial-root\",\"cwd\":\"C:\\\\repo\"}}" + Environment.NewLine);
                    engine.LoadPathForTests(path);
                    AppendLine(path, StartLine("partial-turn", DateTime.UtcNow));
                    AppendLine(path, ReasoningLine(DateTime.UtcNow.AddMilliseconds(1), "after metadata"));
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    Assert(engine.Current.Groups.Length == 1 && engine.Current.Groups[0].RootId == "partial-root" &&
                        engine.Current.PrimaryState == PublicState.Working,
                        "partial metadata must be refreshed before child lineage is classified");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestStaleTurnEvent()
        {
            SessionTracker tracker = NewTracker("turn-root");
            Feed(tracker, StartLine("turn1", DateTime.UtcNow));
            Feed(tracker, StartLine("turn2", DateTime.UtcNow.AddSeconds(1)));
            DateTime before = tracker.LastMeaningfulUtc;
            Feed(tracker, "{\"timestamp\":\"" + Stamp(DateTime.UtcNow.AddSeconds(2)) +
                "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\",\"turn_id\":\"turn1\",\"delta\":\"old\"}}\n");
            Feed(tracker, "{\"timestamp\":\"" + Stamp(DateTime.UtcNow.AddSeconds(3)) +
                "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"turn_id\":\"turn1\",\"call_id\":\"old-call\"}}\n");
            Assert(tracker.LastMeaningfulUtc == before && tracker.CurrentTurnId == "turn2" &&
                tracker.BackgroundProcesses.Count == 0,
                "late events from an older turn must not reopen or advance the new turn");
        }

        private static void TestOutOfOrderLifecycle()
        {
            SessionTracker tracker = NewTracker("chronology-root");
            DateTime first = DateTime.UtcNow.AddMinutes(-2);
            DateTime second = first.AddSeconds(10);
            Feed(tracker, StartLine("turn1", first));
            Feed(tracker, CompleteLine("turn1", first.AddSeconds(1), "done"));
            Feed(tracker, StartLine("turn2", second));
            DateTime accepted = tracker.LastMeaningfulUtc;
            Feed(tracker, StartLine("turn1", first.AddSeconds(2)));
            Assert(tracker.CurrentTurnId == "turn2" && tracker.LastMeaningfulUtc == accepted && tracker.TurnOpen,
                "an older task_started must not roll lifecycle back to an earlier turn");

            Feed(tracker, "{\"timestamp\":\"" + Stamp(first.AddSeconds(3)) +
                "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\",\"turn_id\":\"turn2\",\"delta\":\"old\"}}\n");
            Assert(tracker.LastMeaningfulUtc == accepted,
                "pre-terminal or pre-start records must not advance the active chronology");
        }

        private static void TestBackgroundEffectiveCompletion()
        {
            SessionTracker root = NewDirectTracker("background-root", false, TerminalKind.Done, 1);
            root.BackgroundProcesses[43210] = new BackgroundProcessHint
            {
                Pid = 43210,
                ObservedUtc = DateTime.UtcNow,
                Source = "test"
            };
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map[root.Id] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                StatusSnapshot working = engine.BuildGroupForTests(root.Id, new List<SessionTracker> { root }, map, 0, 1);
                Assert(working.State == PublicState.Working, "advancing detached work must keep a completed root WORKING");
                DateTime rootTerminal = root.LastTerminalUtc;
                engine.BackgroundProbeOverrideForTests = StoppedBackground();
                StatusSnapshot done = engine.BuildGroupForTests(root.Id, new List<SessionTracker> { root }, map, 0, 1);
                Assert(done.State == PublicState.Done, "confirmed background exit must reveal DONE");
                Assert(done.Groups[0].EffectiveCompletionUtc > rootTerminal,
                    "effective completion must begin at confirmed background exit, not overwrite root terminal time");
                DateTime effectiveCompletion = done.Groups[0].EffectiveCompletionUtc;
                StatusSnapshot doneAgain = engine.BuildGroupForTests(root.Id, new List<SessionTracker> { root }, map, 0, 1);
                Assert(doneAgain.Groups[0].EffectiveCompletionUtc == effectiveCompletion,
                    "background-derived DONE time must remain stable on later recomputes");
            }
        }

        private static void TestOldDanglingSessionExcluded()
        {
            string temp = NewTemp("stale-history");
            try
            {
                DateTime old = DateTime.UtcNow - TimeSpan.FromHours(18);
                string stale = Path.Combine(temp, "stale-18h.jsonl");
                string veryOld = Path.Combine(temp, "stale-18d.jsonl");
                string fresh = Path.Combine(temp, "fresh.jsonl");
                WriteSessionAt(stale, "stale", null, "C:\\stale-project", FixtureTerminal.Working, old);
                WriteSessionAt(veryOld, "very-old", null, "C:\\very-old-project", FixtureTerminal.Working,
                    DateTime.UtcNow - TimeSpan.FromDays(18));
                AppendLine(stale, "{\"timestamp\":\"" + Stamp(DateTime.UtcNow) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"still here\"}}");
                WriteSession(fresh, "fresh", null, "C:\\fresh-project", FixtureTerminal.Working);

                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    int notifications = 0;
                    engine.GroupNotification += delegate { notifications++; };
                    engine.LoadPathForTests(stale);
                    engine.LoadPathForTests(veryOld);
                    engine.LoadPathForTests(fresh);
                    engine.RecomputeForTests(true);
                    StatusSnapshot snapshot = engine.Current;

                    Assert(snapshot.Groups.Length == 1 && snapshot.Groups[0].RootId == "fresh",
                        "18-hour and 18-day dangling sessions must be excluded from visible groups");
                    Assert(snapshot.PrimaryGroupId == "root:fresh" && snapshot.PrimaryState == PublicState.Working,
                        "stale history must not change the primary state");
                    Assert(!snapshot.IsStateLit(PublicState.Stuck),
                        "stale history must not light STUCK");
                    Assert(notifications == 0,
                        "startup discovery of stale history must not notify");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestBackgroundProgressUpdatesLastWork()
        {
            string temp = NewTemp("background-progress-time");
            MonitorEngine engine = new MonitorEngine(temp);
            try
            {
                SessionTracker root = NewDirectTracker("background-progress-time-root", false, TerminalKind.Done, 60);
                root.BackgroundProcesses[43214] = new BackgroundProcessHint
                {
                    Pid = 43214,
                    ObservedUtc = DateTime.UtcNow,
                    LaunchUtc = DateTime.UtcNow.AddSeconds(-10),
                    Source = "test"
                };
                Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase)
                {
                    { root.Id, root }
                };
                engine.BackgroundProbeOverrideForTests = FakeBackground(true, true, 0);
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id,
                    new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Working,
                    "forward detached progress must keep a completed root WORKING");
                Assert(snapshot.LastWorkUtc >= DateTime.UtcNow.AddSeconds(-5),
                    "detached forward progress must update the group last-work timestamp");
            }
            finally
            {
                engine.Dispose();
                DeleteTemp(temp);
            }
        }

        private static void TestStaleSessionWithUnverifiedBackground()
        {
            SessionTracker root = NewDirectTracker("cold-background", false, TerminalKind.Done, 18 * 60 * 60);
            root.BackgroundProcesses[43215] = new BackgroundProcessHint
            {
                Pid = 43215,
                ObservedUtc = DateTime.UtcNow.AddSeconds(-2),
                LaunchUtc = DateTime.UtcNow.AddHours(-19),
                Source = "trusted-test-receipt"
            };
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map[root.Id] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = new BackgroundProbeResult
                {
                    Available = false,
                    Unknown = true,
                    AnyAlive = false,
                    HasComparison = false,
                    AliveProcessCount = 0,
                    LastProgressUtc = DateTime.MinValue,
                    Note = "first identity probe unavailable"
                };
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id,
                    new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Working && snapshot.BackgroundProcessAlive,
                    "an unverified first probe must not hide a trusted background job as stale history");
            }
        }

        private static void TestQuietBackgroundBaselineCopy()
        {
            StatusSnapshot snapshot = new StatusSnapshot
            {
                State = PublicState.Working,
                BackgroundProcessAlive = true,
                BackgroundProcessBusy = false,
                BackgroundLastProgressUtc = DateTime.MinValue,
                LastWorkUtc = DateTime.UtcNow - TimeSpan.FromHours(18)
            };
            Assert(StatusPopupForm.DisplaySubtitleFor(snapshot) == "Checking background progress",
                "a quiet background baseline must not claim Making real progress");
        }

        private static void TestUnknownBackgroundIdentity()
        {
            SessionTracker root = NewDirectTracker("unknown-background-root", false, TerminalKind.Done, 1);
            root.BackgroundProcesses[43211] = new BackgroundProcessHint
            {
                Pid = 43211,
                ObservedUtc = DateTime.UtcNow,
                Source = "trusted-test-receipt"
            };
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map[root.Id] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.BackgroundProbeOverrideForTests = new BackgroundProbeResult
                {
                    Available = true,
                    Unknown = true,
                    AnyAlive = false,
                    HasComparison = false,
                    Note = "identity unavailable"
                };
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id,
                    new List<SessionTracker> { root }, map, 0, 1);
                Assert(snapshot.State == PublicState.Working && snapshot.BackgroundProcessAlive,
                    "unknown trusted background identity must be retained as active, not DONE");
            }
        }

        private static void TestStalePidReceipt()
        {
            string temp = NewTemp("stale-pid");
            try
            {
                string state = Path.Combine(temp, "00_STATE");
                Directory.CreateDirectory(state);
                string receipt = Path.Combine(state, "JOB_DONE_PID.json");
                File.WriteAllText(receipt, "{\"pid\":43212,\"state\":\"completed\",\"exit_code\":0}", new UTF8Encoding(false));
                SessionTracker root = NewDirectTracker("stale-root", true, TerminalKind.None, 15 * 60);
                root.BackgroundRoots.Add(temp);
                Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
                map[root.Id] = root;
                using (MonitorEngine engine = new MonitorEngine())
                {
                    StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id, new List<SessionTracker> { root }, map, 1, 1);
                    Assert(!root.BackgroundProcesses.ContainsKey(43212),
                        "terminal PID receipts must not be recovered as a fresh active background job");
                    Assert(!snapshot.BackgroundProcessAlive,
                        "a stale/dead receipt must not create a background WORKING signal");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestNestedPidReceiptMetadata()
        {
            string temp = NewTemp("nested-pid-receipt");
            try
            {
                string state = Path.Combine(temp, "00_STATE");
                Directory.CreateDirectory(state);
                string receipt = Path.Combine(state, "RECOVERY_PID.json");
                File.WriteAllText(receipt,
                    "{\"state\":\"running\",\"started_at\":\"" + DateTime.UtcNow.ToString("o") + "\",\"metadata\":{\"pid\":43213}}",
                    new UTF8Encoding(false));
                SessionTracker root = NewDirectTracker("nested-receipt-root", true, TerminalKind.None, 1);
                root.BackgroundRoots.Add(temp);
                Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
                map[root.Id] = root;
                using (MonitorEngine engine = new MonitorEngine())
                {
                    engine.BuildGroupForTests(root.Id, new List<SessionTracker> { root }, map, 1, 1);
                    Assert(!root.BackgroundProcesses.ContainsKey(43213),
                        "a PID only in receipt metadata must not become a trusted background hint");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestNestedWaitProcessMetadata()
        {
            SessionTracker tracker = NewDirectTracker("nested-wait", true, TerminalKind.None, 1);
            Feed(tracker, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"nested-wait\",\"arguments\":\"Write-Output ok\",\"metadata\":{\"note\":\"Wait-Process -Id 24681\"}}}");
            Assert(!tracker.BackgroundProcesses.ContainsKey(24681),
                "Wait-Process text in unrelated tool metadata must not create a background hint");
        }

        private static void TestGenericCpuDoesNotProveWorking()
        {
            SessionTracker root = NewDirectTracker("cpu-root", true, TerminalKind.None, 15 * 60);
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map[root.Id] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id, new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State != PublicState.Working,
                    "generic Codex process CPU without a trusted forward-progress source must not prove WORKING");
            }
        }

        private static void TestGenericCpuCannotProtectTool()
        {
            SessionTracker root = NewDirectTracker("cpu-tool-root", true, TerminalKind.None, 15 * 60);
            root.ActiveToolCount = 1;
            Dictionary<string, SessionTracker> map = new Dictionary<string, SessionTracker>();
            map[root.Id] = root;
            using (MonitorEngine engine = new MonitorEngine())
            {
                engine.ProcessProbeOverrideForTests = new ProcessProbeResult
                {
                    Available = true,
                    AnyCodexProcess = true,
                    Busy = true,
                    HasComparison = true,
                    RootCount = 1,
                    ConsecutiveQuietSamples = 0
                };
                StatusSnapshot snapshot = engine.BuildGroupForTests(root.Id,
                    new List<SessionTracker> { root }, map, 1, 1);
                Assert(snapshot.State == PublicState.Stuck,
                    "generic Codex CPU must not protect an idle tool from STUCK");
            }
        }

        private static void TestPidReuseRejected()
        {
            Process current = Process.GetCurrentProcess();
            BackgroundProcessHint hint = new BackgroundProcessHint
            {
                Pid = current.Id,
                ObservedUtc = DateTime.UtcNow,
                ProcessStartUtc = DateTime.UtcNow.AddHours(-4),
                Source = "identity-mismatch-test"
            };
            using (ProcessProbe probe = new ProcessProbe())
            {
                BackgroundProbeResult result = probe.SampleBackgroundThrottled(
                    new BackgroundProcessHint[] { hint }, TimeSpan.Zero);
                Assert(!result.AnyAlive,
                    "a PID whose process creation time does not match the trusted receipt must be rejected");
            }
        }

        private static void TestNestedToolMetadataPid()
        {
            SessionTracker tracker = NewDirectTracker("nested-pid", true, TerminalKind.None, 1);
            Feed(tracker, "{\"timestamp\":\"2026-08-13T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"nested\",\"arguments\":\"$p=Start-Process -FilePath $py -PassThru; $p.Id\"}}");
            Feed(tracker, "{\"timestamp\":\"2026-08-13T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"nested\",\"metadata\":{\"pid\":24680},\"output\":\"PROCESS_COMPLETE\"}}");
            Assert(!tracker.BackgroundProcesses.ContainsKey(24680),
                "a PID in unrelated function-output metadata must not become a trusted background hint");
        }

        private static void TestBackgroundOutputProgressEvidence()
        {
            Assert(!ProcessProbe.IsForwardOutputProgressForTests(10, 100, 10, 100),
                "unchanged output must not count as progress");
            Assert(!ProcessProbe.IsForwardOutputProgressForTests(10, 100, 10, 101),
                "same-size timestamp churn must not count as progress");
            Assert(ProcessProbe.IsForwardOutputProgressForTests(10, 100, 11, 100),
                "forward output length must count as progress");
        }

        private static void TestPartialAppendAndDuplicateRead()
        {
            string temp = NewTemp("partial");
            try
            {
                string path = Path.Combine(temp, "partial.jsonl");
                WriteSession(path, "partial-root", null, "C:\\repo", FixtureTerminal.Working);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    long initialReads = engine.Metrics.AppendReadOperations;
                    engine.LoadPathForTests(path);
                    Assert(engine.Metrics.AppendReadOperations == initialReads,
                        "duplicate watcher notifications must not reread unchanged JSONL bytes");

                    string line = ReasoningLine(DateTime.UtcNow.AddSeconds(1), "partial-😀");
                    byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                    int split = Math.Max(1, bytes.Length / 2);
                    AppendBytes(path, bytes, 0, split);
                    engine.LoadPathForTests(path);
                    long beforeCompleteLine = engine.Metrics.ParsedRecordCount;
                    AppendBytes(path, bytes, split, bytes.Length - split);
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(false);
                    Assert(engine.Metrics.ParsedRecordCount > beforeCompleteLine,
                        "a JSON line split across appends must be parsed once after completion");
                    Assert(engine.GetOffsetForTests(path) == new FileInfo(path).Length,
                        "parser offset must reach the actual file length after the partial line completes");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestTruncateAndReplacement()
        {
            string temp = NewTemp("replacement");
            try
            {
                string path = Path.Combine(temp, "rollout.jsonl");
                WriteSession(path, "before-replace", null, "C:\\repo", FixtureTerminal.Working);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    File.WriteAllText(path, SessionText("after-truncate", null, "C:\\repo", FixtureTerminal.Working), new UTF8Encoding(false));
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(false);
                    Assert(FindGroup(engine.Current, "after-truncate") != null,
                        "a truncated file with new metadata must be resynchronized");

                    string rotated = path + ".old";
                    File.Move(path, rotated);
                    File.WriteAllText(path, SessionText("after-rotation", null, "C:\\repo", FixtureTerminal.Done), new UTF8Encoding(false));
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(false);
                    Assert(FindGroup(engine.Current, "after-rotation") != null,
                        "replace/rotation at the same path must not retain old session state");
                    Assert(engine.Metrics.BoundedResyncCount >= 3,
                        "initial load, truncate, and replacement must each use bounded resync accounting");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestWatcherRecovery()
        {
            string temp = NewTemp("watcher-recovery");
            try
            {
                string path = Path.Combine(temp, "watcher.jsonl");
                WriteSession(path, "watcher-root", null, "C:\\repo", FixtureTerminal.Working);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    long scans = engine.Metrics.DirectoryScanCount;
                    engine.SimulateWatcherErrorForTests();
                    engine.RunParserBatchForTests();
                    Assert(engine.Metrics.DirectoryScanCount > scans,
                        "watcher failure must trigger a directory resync scan");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestLargeAppend()
        {
            string temp = NewTemp("large-append");
            try
            {
                string path = Path.Combine(temp, "large-append.jsonl");
                WriteSession(path, "large-root", null, "C:\\repo", FixtureTerminal.Working);
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    StringBuilder bulk = new StringBuilder();
                    for (int i = 0; i < 1800; i++)
                        bulk.Append(ReasoningLine(DateTime.UtcNow.AddSeconds(1), "bulk-" + i + "-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"))
                            .Append(Environment.NewLine);
                    AppendText(path, bulk.ToString());
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(false);
                    ParserMetricsSnapshot metrics = engine.Metrics;
                    Assert(metrics.AppendReadOperations >= 1 && metrics.AppendReadBytes > 0,
                        "a large append must be read as appended bytes");
                    Assert(engine.GetOffsetForTests(path) == new FileInfo(path).Length,
                        "large append must not leave the parser starved behind its offset");
                    Assert(metrics.MaxConcurrentParser <= 1,
                        "parser worker concurrency must never exceed one");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestLargeExistingRollout()
        {
            string temp = NewTemp("large-existing");
            try
            {
                string path = Path.Combine(temp, "large-existing.jsonl");
                WriteSession(path, "large-existing-root", null, "C:\\repo", FixtureTerminal.Working);
                StringBuilder bulk = new StringBuilder();
                for (int i = 0; i < 24000; i++)
                    bulk.Append(ReasoningLine(DateTime.UtcNow.AddSeconds(1), "existing-" + i + "-yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy"))
                        .Append(Environment.NewLine);
                AppendText(path, bulk.ToString());
                long length = new FileInfo(path).Length;
                Assert(length > 1024 * 1024, "fixture must exceed the bounded initial tail size");
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(path);
                    engine.RecomputeForTests(true);
                    ParserMetricsSnapshot metrics = engine.Metrics;
                    Assert(metrics.BoundedResyncCount >= 1 && metrics.BoundedResyncBytes <= 1024 * 1024,
                        "large existing rollout must use a bounded initial read");
                    Assert(metrics.AppendReadBytes == 0,
                        "initial discovery must not masquerade as a steady-state full JSONL reread");
                    Assert(engine.GetOffsetForTests(path) == length,
                        "bounded initial tail parse must still advance the stored offset to EOF");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void TestSnapshotImmutability()
        {
            StatusSnapshot original = new StatusSnapshot();
            original.State = PublicState.Working;
            original.ActiveStatesMask = StatusSnapshot.StateBit(PublicState.Working);
            original.Groups = new GroupStatusSnapshot[]
            {
                new GroupStatusSnapshot { GroupId = "g1", RootId = "r1", State = PublicState.Working, Project = "repo" }
            };
            StatusSnapshot copy = original.Clone();
            copy.Groups[0].Project = "mutated";
            copy.Groups = new GroupStatusSnapshot[0];
            Assert(original.Groups.Length == 1 && original.Groups[0].Project == "repo",
                "snapshot clones must deep-copy group arrays and group objects");

            using (MonitorEngine engine = new MonitorEngine())
            {
                StatusSnapshot current = engine.Current;
                current.Groups = new GroupStatusSnapshot[]
                {
                    new GroupStatusSnapshot { GroupId = "external", State = PublicState.Done }
                };
                Assert(engine.Current.Groups.Length == 0,
                    "the monitor must not expose a mutable collection through Current");
            }
        }

        private static void TestPublicCopyAndPalette()
        {
            Assert(PublicCopy.TitleFor(PublicState.Working) == "I'm working on it!", "working public title mismatch");
            Assert(PublicCopy.TitleFor(PublicState.WaitingForYou) == "I need you!", "waiting public title mismatch");
            Assert(PublicCopy.TitleFor(PublicState.Stuck) == "Hmm... I'm stuck", "stuck public title mismatch");
            Assert(PublicCopy.TitleFor(PublicState.Done) == "All done!", "done public title mismatch");
            Assert(PublicCopy.TitleFor(PublicState.LimitReached) == "I'm out of juice", "limit public title mismatch");
            Assert(PublicCopy.TitleFor(PublicState.Error) == "Oops! Something went wrong", "error public title mismatch");
            Assert(PublicCopy.TitleFor(PublicState.Idle) == "Nothing to do!", "idle public title mismatch");
            Assert(StatusPalette.ColorFor(PublicState.Working) == StatusPalette.TrayColorFor(PublicState.Working),
                "popup and tray must use one canonical WORKING color");
            Assert(StatusPalette.LabelFor(PublicState.Done) == PublicCopy.TitleFor(PublicState.Done),
                "tooltip labels must use the public copy table");
        }

        private static void TestMultiGroupPopupRendering()
        {
            StatusSnapshot snapshot = new StatusSnapshot();
            snapshot.State = PublicState.Working;
            snapshot.PrimaryGroupId = "g1";
            snapshot.Project = "repo";
            snapshot.ActiveStatesMask = StatusSnapshot.StateBit(PublicState.Working) |
                StatusSnapshot.StateBit(PublicState.Done) | StatusSnapshot.StateBit(PublicState.Error);
            snapshot.Groups = new GroupStatusSnapshot[]
            {
                new GroupStatusSnapshot { GroupId = "g1", RootId = "r1", State = PublicState.Working, Project = "one", LastRealWorkUtc = DateTime.UtcNow },
                new GroupStatusSnapshot { GroupId = "g2", RootId = "r2", State = PublicState.Done, Project = "two", LastRealWorkUtc = DateTime.UtcNow },
                new GroupStatusSnapshot { GroupId = "g3", RootId = "r3", State = PublicState.Error, Project = "three", LastRealWorkUtc = DateTime.UtcNow },
                new GroupStatusSnapshot { GroupId = "g4", RootId = "r4", State = PublicState.WaitingForYou, Project = "four", LastRealWorkUtc = DateTime.UtcNow }
            };
            using (StatusPopupForm form = new StatusPopupForm())
            {
                form.UpdateSnapshot(snapshot);
                Assert(form.Height == StatusPopupForm.PopupHeightForTests(4),
                    "popup height must follow the multi-group layout");
                Assert(form.Height > StatusPopupForm.PopupHeightForTests(3),
                    "a +N more row must increase popup height");
                Assert(StatusPopupForm.PopupDividerYForTests(4) -
                    (StatusPopupForm.PopupMoreTopForTests(4) + StatusPopupForm.PopupMoreHeightForTests()) >= 6,
                    "+N more needs dedicated space before the divider");
                Assert(StatusPopupForm.PopupDetailsRectForTests(4).Top -
                    StatusPopupForm.PopupDividerYForTests(4) >= 3,
                    "Details needs a gap below the divider");
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.CreateControl();
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                }
            }
        }

        private static void TestLatestWaitingRepresentative()
        {
            string temp = NewTemp("waiting-representative");
            try
            {
                DateTime now = DateTime.UtcNow;
                string a = Path.Combine(temp, "a.jsonl");
                string b = Path.Combine(temp, "b.jsonl");
                string latest = a;
                string older = b;
                WriteSession(latest, "a", null, "C:\\same-project", FixtureTerminal.Working);
                WriteSession(older, "b", null, "C:\\same-project", FixtureTerminal.Working);
                AppendLine(latest, WaitingLine("turn1", now));
                AppendLine(older, WaitingLine("turn1", now.AddMinutes(-1)));

                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(latest);
                    engine.LoadPathForTests(older);
                    engine.RecomputeForTests(true);
                    Assert(engine.Current.PrimaryGroupId == "root:a",
                        "the group with the latest waiting transition must be the representative");
                    Assert(engine.Current.Groups.Length == 2 && engine.Current.Groups[0].GroupId == "root:a",
                        "multi-group rows must use the same activity ordering as representative selection");
                }

                // Reuse the same IDs and paths with the newer transition on the
                // other group. This makes the regression independent of dictionary
                // bucket order: one of the two orientations exposes the old bug.
                DateTime secondNow = DateTime.UtcNow;
                WriteSession(a, "a", null, "C:\\same-project", FixtureTerminal.Working);
                WriteSession(b, "b", null, "C:\\same-project", FixtureTerminal.Working);
                AppendLine(a, WaitingLine("turn1", secondNow.AddMinutes(-1)));
                AppendLine(b, WaitingLine("turn1", secondNow));
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.LoadPathForTests(a);
                    engine.LoadPathForTests(b);
                    engine.RecomputeForTests(true);
                    Assert(engine.Current.PrimaryGroupId == "root:b",
                        "the latest waiting transition must win regardless of group enumeration order");
                    Assert(engine.Current.Groups.Length == 2 && engine.Current.Groups[0].GroupId == "root:b",
                        "row ordering must follow the same latest-activity rule");
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static SessionTracker NewTracker(string id)
        {
            SessionTracker tracker = new SessionTracker();
            tracker.Path = id + ".jsonl";
            tracker.Meta = new SessionMeta { Id = id, Cwd = "C:\\repo" };
            return tracker;
        }

        private static SessionTracker NewDirectTracker(string id, bool open, TerminalKind terminal, int secondsAgo)
        {
            SessionTracker tracker = NewTracker(id);
            DateTime activity = DateTime.UtcNow - TimeSpan.FromSeconds(secondsAgo);
            tracker.TurnOpen = open;
            tracker.Terminal = terminal;
            tracker.LastMeaningfulUtc = activity;
            tracker.LastAnyUtc = activity;
            tracker.LastTerminalUtc = terminal == TerminalKind.None ? DateTime.MinValue : activity;
            tracker.TaskStartedUtc = activity;
            return tracker;
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
                Note = "regression-test"
            };
        }

        private static BackgroundProbeResult StoppedBackground()
        {
            return new BackgroundProbeResult
            {
                Available = true,
                AnyAlive = false,
                Busy = false,
                HasComparison = true,
                AliveProcessCount = 0,
                ConsecutiveQuietSamples = 0,
                LastProgressUtc = DateTime.MinValue,
                Note = "regression-test-stopped"
            };
        }

        private static GroupStatusSnapshot FindGroup(StatusSnapshot snapshot, string rootId)
        {
            if (snapshot == null || snapshot.Groups == null) return null;
            return snapshot.Groups.FirstOrDefault(g => g != null && string.Equals(g.RootId, rootId, StringComparison.OrdinalIgnoreCase));
        }

        private static void Feed(SessionTracker tracker, string line)
        {
            MonitorEngine.ProcessLine(tracker, line, DateTime.UtcNow);
        }

        private static string StartLine(string turnId, DateTime timestamp)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"" + Q(turnId) + "\"}}";
        }

        private static string AssistantLine(DateTime timestamp, string text)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"text\":\"" + Q(text) + "\"}]}}";
        }

        private static string CompleteLine(string turnId, DateTime timestamp, string text)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_complete\",\"turn_id\":\"" + Q(turnId) + "\",\"last_agent_message\":\"" + Q(text) + "\"}}";
        }

        private static string ReasoningLine(DateTime timestamp, string text)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\",\"delta\":\"" + Q(text) + "\"}}";
        }

        private static string WaitingLine(string turnId, DateTime timestamp)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"request_user_input\",\"turn_id\":\"" + Q(turnId) + "\"}}";
        }

        private static string ErrorLine(DateTime timestamp)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"error\",\"message\":\"failure\"}}";
        }

        private static void WriteSession(string path, string id, string parent, string cwd, FixtureTerminal terminal)
        {
            WriteSessionAt(path, id, parent, cwd, terminal, DateTime.UtcNow);
        }

        private static void WriteSessionAt(string path, string id, string parent, string cwd,
            FixtureTerminal terminal, DateTime now)
        {
            File.WriteAllText(path, SessionTextAt(id, parent, cwd, terminal, now), new UTF8Encoding(false));
        }

        private static string SessionText(string id, string parent, string cwd, FixtureTerminal terminal)
        {
            return SessionTextAt(id, parent, cwd, terminal, DateTime.UtcNow);
        }

        private static string SessionTextAt(string id, string parent, string cwd, FixtureTerminal terminal,
            DateTime now)
        {
            StringBuilder text = new StringBuilder();
            text.Append(MetaLineAt(id, parent, cwd, now)).Append(Environment.NewLine);
            if (terminal != FixtureTerminal.MetadataOnly)
            {
                text.Append(StartLine("turn1", now.AddSeconds(-5))).Append(Environment.NewLine);
                text.Append(AssistantLine(now.AddSeconds(-4), "initial progress")).Append(Environment.NewLine);
                if (terminal == FixtureTerminal.Done)
                    text.Append(CompleteLine("turn1", now.AddSeconds(-3), "done")).Append(Environment.NewLine);
                else if (terminal == FixtureTerminal.Error)
                    text.Append(ErrorLine(now.AddSeconds(-3))).Append(Environment.NewLine);
            }
            return text.ToString();
        }

        private static string MetaLine(string id, string parent, string cwd)
        {
            return MetaLineAt(id, parent, cwd, DateTime.UtcNow);
        }

        private static string MetaLineAt(string id, string parent, string cwd, DateTime timestamp)
        {
            string parentField = string.IsNullOrEmpty(parent) ? string.Empty : ",\"parent_thread_id\":\"" + Q(parent) + "\"";
            return "{\"timestamp\":\"" + Stamp(timestamp.AddSeconds(-10)) + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + Q(id) + "\",\"cwd\":\"" + Q(cwd) + "\"" + parentField + "}}";
        }

        private static string Stamp(DateTime value)
        {
            return value.ToUniversalTime().ToString("o");
        }

        private static string Q(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void AppendLine(string path, string line)
        {
            AppendText(path, line + Environment.NewLine);
        }

        private static void AppendText(string path, string text)
        {
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }

        private static void AppendBytes(string path, byte[] bytes, int offset, int count)
        {
            using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.Write(bytes, offset, count);
        }

        private static string NewTemp(string label)
        {
            string path = Path.Combine(Path.GetTempPath(), "is-codex-working-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTemp(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
