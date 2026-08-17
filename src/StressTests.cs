using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace IsCodexWorking
{
    internal static class StressTests
    {
        public static int RunAll()
        {
            List<string> failures = new List<string>();
            try { RunJsonlStress(); }
            catch (Exception ex) { failures.Add("jsonl: " + ex.Message); }
            try { RunDetachedPythonStress(); }
            catch (Exception ex) { failures.Add("background: " + ex.Message); }

            if (failures.Count == 0)
            {
                Console.WriteLine("STRESS PASS jsonl+background");
                return 0;
            }
            foreach (string failure in failures) Console.WriteLine("STRESS FAIL " + failure);
            return 1;
        }

        public static int RunIdleSmoke()
        {
            string temp = NewTemp("idle");
            try
            {
                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.Start();
                    WaitFor(delegate { return engine.Current.PrimaryState == PublicState.Idle; }, 10000,
                        "idle warmup did not settle");
                    Process process = Process.GetCurrentProcess();
                    TimeSpan before = process.TotalProcessorTime;
                    Thread.Sleep(60000);
                    TimeSpan cpu = process.TotalProcessorTime - before;
                    ParserMetricsSnapshot metrics = engine.Metrics;
                    StatusSnapshot snapshot = engine.Current;
                    Assert(snapshot.PrimaryState == PublicState.Idle && snapshot.Groups.Length == 0,
                        "empty 60-second monitor must remain NO TASK");
                    Assert(cpu.TotalSeconds < 3.0,
                        "idle monitor CPU time exceeded the resource-smoke bound: " + cpu.TotalSeconds.ToString("0.00") + "s");
                    Assert(metrics.DirectoryScanCount <= 2,
                        "idle watchdog performed too many directory scans: " + metrics.DirectoryScanCount);
                    Assert(metrics.MaxConcurrentParser <= 1, "idle parser concurrency exceeded one");
                    Console.WriteLine("IDLE PASS cpu=" + cpu.TotalSeconds.ToString("0.00") + "s scans=" + metrics.DirectoryScanCount);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("IDLE FAIL " + ex.Message);
                return 1;
            }
            finally { DeleteTemp(temp); }
        }

        private static void RunJsonlStress()
        {
            string temp = NewTemp("jsonl-stress");
            try
            {
                DateTime folderDate = DateTime.UtcNow;
                string day = Path.Combine(temp, folderDate.ToString("yyyy"), folderDate.ToString("MM"), folderDate.ToString("dd"));
                Directory.CreateDirectory(day);
                string path = Path.Combine(day, "rollout-stress-" + Guid.NewGuid().ToString("N") + ".jsonl");
                File.WriteAllText(path, SessionText("stress-root"), new UTF8Encoding(false));
                int appendedLines = 0;
                DateTime finalMarker = DateTime.MinValue;

                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.Start();
                    WaitFor(delegate { return engine.Metrics.BoundedResyncCount >= 1; }, 10000,
                        "watcher did not perform initial bounded discovery");

                    for (int i = 0; i < 60; i++)
                    {
                        DateTime timestamp = DateTime.UtcNow;
                        if (i == 59)
                        {
                            timestamp = DateTime.UtcNow.AddSeconds(1);
                            finalMarker = timestamp;
                        }
                        string line = ReasoningLine(timestamp, "continuous-" + i);
                        if (i == 10)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                            int split = Math.Max(1, bytes.Length / 2);
                            AppendBytes(path, bytes, 0, split);
                            Thread.Sleep(50);
                            AppendBytes(path, bytes, split, bytes.Length - split);
                        }
                        else AppendLine(path, line);
                        appendedLines++;
                        Thread.Sleep(100);
                    }

                    StringBuilder large = new StringBuilder();
                    for (int i = 0; i < 2500; i++)
                        large.Append(ReasoningLine(DateTime.UtcNow.AddSeconds(1), "large-" + i + "-zzzzzzzzzzzzzzzzzzzzzzzz"))
                            .Append(Environment.NewLine);
                    AppendText(path, large.ToString());
                    appendedLines += 2500;

                    long expectedLines = 3 + appendedLines;
                    WaitFor(delegate
                    {
                        ParserMetricsSnapshot metrics = engine.Metrics;
                        return metrics.ParsedRecordCount == expectedLines &&
                            engine.GetOffsetForTests(path) == new FileInfo(path).Length;
                    }, 15000, "continuous append did not reach EOF without starvation");

                    long scans = engine.Metrics.DirectoryScanCount;
                    engine.SimulateWatcherErrorForTests();
                    engine.RunParserBatchForTests();
                    WaitFor(delegate { return engine.Metrics.DirectoryScanCount > scans; }, 5000,
                        "watcher recovery did not rescan the local session root");

                    ParserMetricsSnapshot finalMetrics = engine.Metrics;
                    StatusSnapshot snapshot = engine.Current;
                    Assert(finalMetrics.MaxConcurrentParser <= 1, "parser worker concurrency exceeded one");
                    Assert(finalMetrics.AppendReadOperations > 0 && finalMetrics.AppendReadOperations < appendedLines,
                        "750ms coalescing did not batch continuous watcher events");
                    Assert(finalMetrics.BoundedResyncBytes <= 1024 * 1024,
                        "initial resync exceeded its one-megabyte bound");
                    Assert(snapshot.PrimaryState == PublicState.Working && snapshot.LastWorkUtc >= finalMarker,
                        "the final semantic progress event was not reflected in the snapshot");

                    using (StatusPopupForm popup = new StatusPopupForm())
                    using (Bitmap bitmap = new Bitmap(popup.Width, popup.Height))
                    {
                        popup.UpdateSnapshot(snapshot);
                        popup.CreateControl();
                        popup.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    }
                }
            }
            finally { DeleteTemp(temp); }
        }

        private static void RunDetachedPythonStress()
        {
            string temp = NewTemp("background-stress");
            Process child = null;
            try
            {
                string python = ResolvePython();
                string runRoot = Path.Combine(temp, "RUN_BACKGROUND");
                string state = Path.Combine(runRoot, "00_STATE");
                Directory.CreateDirectory(state);
                string script = Path.Combine(runRoot, "worker.py");
                string stdout = Path.Combine(runRoot, "worker.stdout.log");
                string stderr = Path.Combine(runRoot, "worker.stderr.log");
                File.WriteAllText(script,
                    "import sys, time\nfrom pathlib import Path\ntarget = Path(sys.argv[1])\nfor i in range(80):\n    with target.open('a', encoding='utf-8') as f:\n        f.write('progress %d\\n' % i)\n    time.sleep(0.15)\n",
                    new UTF8Encoding(false));

                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = python;
                start.Arguments = Quote(script) + " " + Quote(stdout);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                child = Process.Start(start);
                Assert(child != null, "python detached process did not start");
                DateTime launchUtc = DateTime.UtcNow;
                DateTime processStartUtc = child.StartTime.ToUniversalTime();
                string executable = child.MainModule.FileName;
                string receipt = "{\"pid\":" + child.Id +
                    ",\"state\":\"running\",\"started_at\":\"" + Stamp(launchUtc) +
                    "\",\"process_start_time\":\"" + Stamp(processStartUtc) +
                    "\",\"executable\":\"" + Q(executable) +
                    "\",\"stdout\":\"" + Q(stdout) +
                    "\",\"stderr\":\"" + Q(stderr) + "\"}";
                File.WriteAllText(Path.Combine(state, "PYTHON_JOB_PID.json"), receipt, new UTF8Encoding(false));

                DateTime now = DateTime.UtcNow;
                string day = Path.Combine(temp, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
                Directory.CreateDirectory(day);
                string sessionPath = Path.Combine(day, "rollout-background-" + Guid.NewGuid().ToString("N") + ".jsonl");
                StringBuilder session = new StringBuilder();
                session.Append(MetaLine("background-root")).Append(Environment.NewLine);
                session.Append(StartLine("turn1", now.AddSeconds(-3))).Append(Environment.NewLine);
                session.Append(AssistantLine(now.AddSeconds(-2), "background launched")).Append(Environment.NewLine);
                string arguments = "$run='" + runRoot + "'; Start-Process -FilePath '" + executable + "' -PassThru; $p.Id";
                session.Append(FunctionCallLine(now.AddSeconds(-1), "bg-call", arguments)).Append(Environment.NewLine);
                session.Append(FunctionOutputLine(now, "bg-call", "{\"pid\":" + child.Id + "}")).Append(Environment.NewLine);
                File.WriteAllText(sessionPath, session.ToString(), new UTF8Encoding(false));

                using (MonitorEngine engine = new MonitorEngine(temp))
                {
                    engine.BackgroundProbeIntervalForTests = TimeSpan.FromSeconds(1);
                    engine.Start();
                    WaitFor(delegate
                    {
                        GroupStatusSnapshot group = FindGroup(engine.Current, "background-root");
                        return group != null && group.BackgroundJobActive && group.State == PublicState.Working;
                    }, 10000, "trusted detached Python PID was not observed as WORKING");

                    DateTime terminalTime = DateTime.UtcNow.AddSeconds(1);
                    AppendLine(sessionPath, CompleteLine("turn1", terminalTime, "root finished"));
                    engine.LoadPathForTests(sessionPath);
                    engine.RecomputeForTests(false);

                    bool outputProgress = false;
                    for (int i = 0; i < 8; i++)
                    {
                        Thread.Sleep(1200);
                        engine.RecomputeForTests(false);
                        GroupStatusSnapshot group = FindGroup(engine.Current, "background-root");
                        if (group != null && group.BackgroundLastProgressUtc != DateTime.MinValue)
                        {
                            outputProgress = true;
                            break;
                        }
                    }
                    Assert(outputProgress, "trusted Python output growth was not recognized as background progress");
                    Assert(child.WaitForExit(20000), "detached Python process did not finish its finite workload");
                    Thread.Sleep(2500);
                    engine.RecomputeForTests(false);
                    GroupStatusSnapshot completed = FindGroup(engine.Current, "background-root");
                    Assert(completed != null && completed.State == PublicState.Done,
                        "root DONE must begin only after the trusted background process exits");
                    Assert(!completed.BackgroundJobActive && completed.EffectiveCompletionUtc > terminalTime,
                        "background exit must end the job and set a later effective completion time");
                }
            }
            finally
            {
                if (child != null)
                {
                    try { if (!child.HasExited) child.WaitForExit(15000); }
                    catch { }
                    child.Dispose();
                }
                DeleteTemp(temp);
            }
        }

        private static string ResolvePython()
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "python.exe";
            info.Arguments = "--version";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using (Process probe = Process.Start(info))
            {
                Assert(probe != null && probe.WaitForExit(5000) && probe.ExitCode == 0,
                    "python.exe is unavailable for the detached-process stress");
            }
            return "python.exe";
        }

        private static void WaitFor(Func<bool> condition, int timeoutMilliseconds, string message)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (condition()) return;
                Thread.Sleep(50);
            }
            if (!condition()) throw new InvalidOperationException(message);
        }

        private static GroupStatusSnapshot FindGroup(StatusSnapshot snapshot, string rootId)
        {
            if (snapshot == null || snapshot.Groups == null) return null;
            for (int i = 0; i < snapshot.Groups.Length; i++)
                if (snapshot.Groups[i] != null && string.Equals(snapshot.Groups[i].RootId, rootId, StringComparison.OrdinalIgnoreCase))
                    return snapshot.Groups[i];
            return null;
        }

        private static string SessionText(string id)
        {
            DateTime now = DateTime.UtcNow;
            return MetaLine(id) + Environment.NewLine +
                StartLine("turn1", now.AddSeconds(-3)) + Environment.NewLine +
                AssistantLine(now.AddSeconds(-2), "stress started") + Environment.NewLine;
        }

        private static string MetaLine(string id)
        {
            return "{\"timestamp\":\"" + Stamp(DateTime.UtcNow.AddSeconds(-4)) + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + Q(id) + "\",\"cwd\":\"C:\\\\repo\"}}";
        }

        private static string StartLine(string turnId, DateTime timestamp)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"" + Q(turnId) + "\"}}";
        }

        private static string AssistantLine(DateTime timestamp, string text)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"text\":\"" + Q(text) + "\"}]}}";
        }

        private static string ReasoningLine(DateTime timestamp, string text)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_content_delta\",\"delta\":\"" + Q(text) + "\"}}";
        }

        private static string FunctionCallLine(DateTime timestamp, string callId, string arguments)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"call_id\":\"" + Q(callId) + "\",\"arguments\":\"" + Q(arguments) + "\"}}";
        }

        private static string FunctionOutputLine(DateTime timestamp, string callId, string output)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"" + Q(callId) + "\",\"output\":\"" + Q(output) + "\"}}";
        }

        private static string CompleteLine(string turnId, DateTime timestamp, string text)
        {
            return "{\"timestamp\":\"" + Stamp(timestamp) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_complete\",\"turn_id\":\"" + Q(turnId) + "\",\"last_agent_message\":\"" + Q(text) + "\"}}";
        }

        private static string Stamp(DateTime value)
        {
            return value.ToUniversalTime().ToString("o");
        }

        private static string Q(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
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
