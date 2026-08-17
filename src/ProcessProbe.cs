using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IsCodexWorking
{
    internal sealed class ProcessProbe : IDisposable
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr processHandle, out IO_COUNTERS counters);

        private readonly object _sync = new object();
        private readonly Dictionary<int, long> _lastCpuTicks = new Dictionary<int, long>();
        private readonly Dictionary<int, ulong> _lastIoBytes = new Dictionary<int, ulong>();
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private ProcessProbeResult _lastResult;
        private int _quietSamples;
        private DateTime _treeCacheUtc = DateTime.MinValue;
        private Dictionary<int, int> _treeCacheParents;
        private Dictionary<int, string> _treeCacheNames;

        private sealed class BackgroundSampleState
        {
            public readonly Dictionary<int, long> CpuTicks = new Dictionary<int, long>();
            public readonly Dictionary<int, ulong> IoBytes = new Dictionary<int, ulong>();
            public readonly Dictionary<string, long> FileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, long> FileWriteTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, DateTime> IdentityStarts = new Dictionary<int, DateTime>();
            public DateTime LastSampleUtc = DateTime.MinValue;
            public DateTime LastSeenUtc = DateTime.MinValue;
            public BackgroundProbeResult LastResult;
            public int QuietSamples;
            public DateTime LastProgressUtc = DateTime.MinValue;
        }

        private readonly Dictionary<string, BackgroundSampleState> _backgroundStates =
            new Dictionary<string, BackgroundSampleState>(StringComparer.Ordinal);

        public ProcessProbeResult SampleThrottled(TimeSpan minimumInterval)
        {
            lock (_sync)
            {
                if (_lastResult != null && _lastSampleUtc != DateTime.MinValue &&
                    DateTime.UtcNow - _lastSampleUtc < minimumInterval)
                    return Clone(_lastResult);
            }
            return SampleNow();
        }

        private ProcessProbeResult SampleNow()
        {
            ProcessProbeResult result = new ProcessProbeResult();
            Dictionary<int, int> parents = new Dictionary<int, int>();
            Dictionary<int, string> names = new Dictionary<int, string>();
            if (!ReadProcessTreeCached(out parents, out names))
            {
                result.Available = false;
                result.Note = "Process details unavailable";
                SaveUnavailable(result);
                return result;
            }

            result.Available = true;
            HashSet<int> codexCandidates = new HashSet<int>();
            foreach (KeyValuePair<int, string> pair in names)
            {
                string lower = (pair.Value ?? string.Empty).ToLowerInvariant();
                if (lower == "codex.exe" || lower == "codex" || lower.IndexOf("codex-code-mode-host") >= 0)
                    codexCandidates.Add(pair.Key);
            }

            // A Codex helper/host can itself have a Codex-looking executable name.
            // Count independent Codex process trees, not every matching process name.
            HashSet<int> roots = new HashSet<int>(codexCandidates);
            foreach (int candidate in codexCandidates)
            {
                int parent;
                int guardParents = 0;
                if (!parents.TryGetValue(candidate, out parent)) continue;
                while (parent > 0 && guardParents++ < 64)
                {
                    if (codexCandidates.Contains(parent))
                    {
                        roots.Remove(candidate);
                        break;
                    }
                    int next;
                    if (!parents.TryGetValue(parent, out next) || next == parent) break;
                    parent = next;
                }
            }

            result.RootCount = roots.Count;
            result.AnyCodexProcess = roots.Count > 0;
            if (roots.Count == 0)
            {
                lock (_sync)
                {
                    _lastCpuTicks.Clear();
                    _lastIoBytes.Clear();
                    _quietSamples = 0;
                    _lastSampleUtc = DateTime.UtcNow;
                    result.HasComparison = true;
                    result.ConsecutiveQuietSamples = 0;
                    result.Note = "No Codex process";
                    _lastResult = Clone(result);
                }
                return result;
            }

            HashSet<int> related = new HashSet<int>(roots);
            bool added = true;
            int guard = 0;
            while (added && guard++ < 32)
            {
                added = false;
                foreach (KeyValuePair<int, int> pair in parents)
                {
                    if (!related.Contains(pair.Key) && related.Contains(pair.Value))
                    {
                        related.Add(pair.Key);
                        added = true;
                    }
                }
            }

            result.ProcessCount = related.Count;
            Dictionary<int, long> nowTicks = new Dictionary<int, long>();
            Dictionary<int, ulong> nowIo = new Dictionary<int, ulong>();
            foreach (int pid in related)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        nowTicks[pid] = p.TotalProcessorTime.Ticks;
                        try
                        {
                            IO_COUNTERS io;
                            if (GetProcessIoCounters(p.Handle, out io))
                                nowIo[pid] = io.ReadTransferCount + io.WriteTransferCount + io.OtherTransferCount;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            lock (_sync)
            {
                bool hadPrior = _lastSampleUtc != DateTime.MinValue && (_lastCpuTicks.Count > 0 || _lastIoBytes.Count > 0);
                result.HasComparison = hadPrior;
                double cpuSeconds = 0.0;
                ulong ioBytes = 0;
                if (hadPrior)
                {
                    long cpuTicks = 0;
                    foreach (KeyValuePair<int, long> pair in nowTicks)
                    {
                        long old;
                        if (_lastCpuTicks.TryGetValue(pair.Key, out old) && pair.Value > old)
                            cpuTicks += pair.Value - old;
                    }
                    foreach (KeyValuePair<int, ulong> pair in nowIo)
                    {
                        ulong old;
                        if (_lastIoBytes.TryGetValue(pair.Key, out old) && pair.Value > old)
                            ioBytes += pair.Value - old;
                    }
                    cpuSeconds = TimeSpan.FromTicks(cpuTicks).TotalSeconds;
                    result.Busy = cpuSeconds >= 0.20 || ioBytes >= 64 * 1024;
                    _quietSamples = result.Busy ? 0 : _quietSamples + 1;
                }
                else
                {
                    result.Busy = false;
                    _quietSamples = 0;
                }

                result.Note = hadPrior
                    ? "CPU " + cpuSeconds.ToString("0.00") + "s, I/O " + ioBytes + " B"
                    : "First process sample";
                result.ConsecutiveQuietSamples = _quietSamples;
                _lastCpuTicks.Clear();
                _lastIoBytes.Clear();
                foreach (KeyValuePair<int, long> pair in nowTicks) _lastCpuTicks[pair.Key] = pair.Value;
                foreach (KeyValuePair<int, ulong> pair in nowIo) _lastIoBytes[pair.Key] = pair.Value;
                _lastSampleUtc = DateTime.UtcNow;
                _lastResult = Clone(result);
            }
            return result;
        }

        public BackgroundProbeResult SampleBackgroundThrottled(IEnumerable<BackgroundProcessHint> hints, TimeSpan minimumInterval)
        {
            Dictionary<int, BackgroundProcessHint> byPid = new Dictionary<int, BackgroundProcessHint>();
            if (hints != null)
            {
                foreach (BackgroundProcessHint hint in hints)
                {
                    if (hint == null || hint.Pid <= 4) continue;
                    BackgroundProcessHint current;
                    if (!byPid.TryGetValue(hint.Pid, out current) || hint.ObservedUtc > current.ObservedUtc)
                        byPid[hint.Pid] = hint.Clone();
                }
            }
            List<BackgroundProcessHint> list = new List<BackgroundProcessHint>(byPid.Values);
            list.Sort(delegate(BackgroundProcessHint a, BackgroundProcessHint b) { return a.Pid.CompareTo(b.Pid); });

            string signature = string.Empty;
            int si;
            for (si = 0; si < list.Count; si++)
            {
                // Observation time is intentionally excluded. Receipt rescans refresh
                // it, but the process identity and evidence paths are unchanged; using
                // it here would reset the comparison baseline every 30 seconds.
                signature += list[si].Pid + ":" + list[si].LaunchUtc.Ticks + ":" +
                    list[si].ProcessStartUtc.Ticks + ":" + (list[si].ExecutablePath ?? string.Empty) + ":" +
                    (list[si].StdoutPath ?? string.Empty) + ":" + (list[si].StderrPath ?? string.Empty) + ";";
            }

            BackgroundSampleState sampleState;
            lock (_sync)
            {
                CleanupBackgroundStatesLocked(DateTime.UtcNow);
                if (!_backgroundStates.TryGetValue(signature, out sampleState))
                {
                    sampleState = new BackgroundSampleState();
                    _backgroundStates[signature] = sampleState;
                }
                sampleState.LastSeenUtc = DateTime.UtcNow;
                if (sampleState.LastResult != null && sampleState.LastSampleUtc != DateTime.MinValue &&
                    DateTime.UtcNow - sampleState.LastSampleUtc < minimumInterval)
                    return CloneBackground(sampleState.LastResult);
            }

            BackgroundProbeResult result = new BackgroundProbeResult();
            Dictionary<int, int> parents = new Dictionary<int, int>();
            Dictionary<int, string> names = new Dictionary<int, string>();
            if (!ReadProcessTreeCached(out parents, out names))
            {
                result.Available = false;
                result.Note = "Background process details unavailable";
                SaveBackground(result, signature, new Dictionary<int, long>(), new Dictionary<int, ulong>(),
                    new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
                return result;
            }
            result.Available = true;

            HashSet<int> seeds = new HashSet<int>();
            bool identityUnknown = false;
            foreach (BackgroundProcessHint hint in list)
            {
                bool accept = true;
                try
                {
                    using (Process p = Process.GetProcessById(hint.Pid))
                    {
                        DateTime started = p.StartTime.ToUniversalTime();
                        DateTime knownStart;
                        lock (_sync) sampleState.IdentityStarts.TryGetValue(hint.Pid, out knownStart);
                        if (knownStart != DateTime.MinValue &&
                            Math.Abs((started - knownStart).TotalSeconds) > 5)
                        {
                            accept = false;
                        }
                        else if (hint.ProcessStartUtc != DateTime.MinValue &&
                            Math.Abs((started - hint.ProcessStartUtc).TotalSeconds) > 5)
                        {
                            accept = false;
                        }
                        else if (hint.LaunchUtc != DateTime.MinValue)
                        {
                            if (started < hint.LaunchUtc - TimeSpan.FromMinutes(2) ||
                                started > hint.LaunchUtc + TimeSpan.FromMinutes(10)) accept = false;
                        }
                        else if (knownStart == DateTime.MinValue && hint.ObservedUtc != DateTime.MinValue &&
                                 started > hint.ObservedUtc + TimeSpan.FromMinutes(10))
                        {
                            accept = false;
                        }
                        if (accept && !string.IsNullOrWhiteSpace(hint.ExecutablePath))
                        {
                            try
                            {
                                string actual = p.MainModule.FileName;
                                accept = string.Equals(Path.GetFullPath(actual), Path.GetFullPath(hint.ExecutablePath),
                                    StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                // The PID is present, but its executable identity
                                // cannot be inspected. Keep it as UNKNOWN rather
                                // than allowing a later sample to call it exited.
                                identityUnknown = true;
                                accept = false;
                            }
                        }
                        if (accept)
                        {
                            lock (_sync)
                            {
                                DateTime oldStart;
                                if (sampleState.IdentityStarts.TryGetValue(hint.Pid, out oldStart) &&
                                    Math.Abs((started - oldStart).TotalSeconds) > 5)
                                    accept = false;
                                else if (!sampleState.IdentityStarts.ContainsKey(hint.Pid))
                                    sampleState.IdentityStarts[hint.Pid] = started;
                            }
                        }
                    }
                }
                catch
                {
                    // A missing process is naturally absent from the tree. If identity
                    // inspection itself fails for a live PID, fail closed rather than
                    // treating a numeric PID as proof of work.
                    if (names.ContainsKey(hint.Pid)) identityUnknown = true;
                    accept = false;
                }
                if (accept) seeds.Add(hint.Pid);
            }

            HashSet<int> related = new HashSet<int>();
            foreach (int pid in names.Keys)
            {
                int current = pid;
                int guard = 0;
                while (current > 0 && guard++ < 64)
                {
                    if (seeds.Contains(current))
                    {
                        related.Add(pid);
                        break;
                    }
                    int parent;
                    if (!parents.TryGetValue(current, out parent) || parent <= 0 || parent == current) break;
                    current = parent;
                }
            }

            Dictionary<int, long> nowTicks = new Dictionary<int, long>();
            Dictionary<int, ulong> nowIo = new Dictionary<int, ulong>();
            foreach (int pid in related)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        nowTicks[pid] = p.TotalProcessorTime.Ticks;
                        try
                        {
                            IO_COUNTERS io;
                            if (GetProcessIoCounters(p.Handle, out io))
                                nowIo[pid] = io.ReadTransferCount + io.WriteTransferCount + io.OtherTransferCount;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            Dictionary<string, long> nowFileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> nowFileWriteTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (BackgroundProcessHint hint in list)
            {
                string[] evidencePaths = new string[] { hint.StdoutPath, hint.StderrPath };
                int ep;
                for (ep = 0; ep < evidencePaths.Length; ep++)
                {
                    string evidencePath = evidencePaths[ep];
                    if (string.IsNullOrWhiteSpace(evidencePath) || nowFileSizes.ContainsKey(evidencePath)) continue;
                    try
                    {
                        FileInfo file = new FileInfo(evidencePath);
                        if (!file.Exists) continue;
                        nowFileSizes[evidencePath] = file.Length;
                        nowFileWriteTicks[evidencePath] = file.LastWriteTimeUtc.Ticks;
                    }
                    catch { }
                }
            }

            lock (_sync)
            {
                if (!_backgroundStates.TryGetValue(signature, out sampleState))
                {
                    sampleState = new BackgroundSampleState();
                    _backgroundStates[signature] = sampleState;
                }
                bool hadPrior = sampleState.LastSampleUtc != DateTime.MinValue;
                result.HasComparison = hadPrior;
                result.Unknown = identityUnknown;
                result.AnyAlive = related.Count > 0;
                result.AliveProcessCount = related.Count;
                double cpuSeconds = 0.0;
                ulong ioBytes = 0;
                bool outputProgress = false;

                if (hadPrior)
                {
                    long cpuTicks = 0;
                    foreach (KeyValuePair<int, long> pair in nowTicks)
                    {
                        long old;
                        if (sampleState.CpuTicks.TryGetValue(pair.Key, out old) && pair.Value > old)
                            cpuTicks += pair.Value - old;
                    }
                    foreach (KeyValuePair<int, ulong> pair in nowIo)
                    {
                        ulong old;
                        if (sampleState.IoBytes.TryGetValue(pair.Key, out old) && pair.Value > old)
                            ioBytes += pair.Value - old;
                    }
                    foreach (KeyValuePair<string, long> pair in nowFileSizes)
                    {
                        long oldSize;
                        long oldWrite;
                        if (!sampleState.FileSizes.TryGetValue(pair.Key, out oldSize) ||
                            !sampleState.FileWriteTicks.TryGetValue(pair.Key, out oldWrite))
                        {
                            // A newly observed stdout/stderr path establishes a
                            // baseline only. Discovery is not forward progress.
                            continue;
                        }
                        long writeTicks;
                        if (IsForwardOutputProgress(oldSize, oldWrite, pair.Value,
                            nowFileWriteTicks.TryGetValue(pair.Key, out writeTicks) ? writeTicks : oldWrite))
                        {
                            outputProgress = true;
                            break;
                        }
                    }

                    cpuSeconds = TimeSpan.FromTicks(cpuTicks).TotalSeconds;
                    result.Busy = cpuSeconds >= 0.10 || ioBytes >= 64 * 1024 || outputProgress;
                    sampleState.QuietSamples = result.AnyAlive && !result.Busy ? sampleState.QuietSamples + 1 : 0;
                    if (result.Busy) sampleState.LastProgressUtc = DateTime.UtcNow;
                }
                else
                {
                    result.Busy = false;
                    sampleState.QuietSamples = 0;
                }

                result.ConsecutiveQuietSamples = sampleState.QuietSamples;
                result.LastProgressUtc = sampleState.LastProgressUtc;
                result.Note = result.AnyAlive
                    ? (hadPrior ? "Background CPU " + cpuSeconds.ToString("0.00") + "s, I/O " + ioBytes + " B" +
                        (outputProgress ? ", output changed" : string.Empty) : "Background process found")
                    : "No tracked background process";

                sampleState.CpuTicks.Clear();
                sampleState.IoBytes.Clear();
                sampleState.FileSizes.Clear();
                sampleState.FileWriteTicks.Clear();
                foreach (KeyValuePair<int, long> pair in nowTicks) sampleState.CpuTicks[pair.Key] = pair.Value;
                foreach (KeyValuePair<int, ulong> pair in nowIo) sampleState.IoBytes[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, long> pair in nowFileSizes) sampleState.FileSizes[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, long> pair in nowFileWriteTicks) sampleState.FileWriteTicks[pair.Key] = pair.Value;
                sampleState.LastSampleUtc = DateTime.UtcNow;
                sampleState.LastSeenUtc = DateTime.UtcNow;
                sampleState.LastResult = CloneBackground(result);
            }
            return result;
        }

        private void CleanupBackgroundStatesLocked(DateTime nowUtc)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, BackgroundSampleState> pair in _backgroundStates)
            {
                if (pair.Value.LastSeenUtc != DateTime.MinValue && nowUtc - pair.Value.LastSeenUtc > TimeSpan.FromHours(12))
                {
                    if (expired == null) expired = new List<string>();
                    expired.Add(pair.Key);
                }
            }
            if (expired == null) return;
            int i;
            for (i = 0; i < expired.Count; i++) _backgroundStates.Remove(expired[i]);
        }

#if TEST_BUILD
        internal static bool IsForwardOutputProgressForTests(long oldSize, long oldWriteTicks,
            long newSize, long newWriteTicks)
        {
            return IsForwardOutputProgress(oldSize, oldWriteTicks, newSize, newWriteTicks);
        }
#endif

        private static bool IsForwardOutputProgress(long oldSize, long oldWriteTicks,
            long newSize, long newWriteTicks)
        {
            // mtime churn on an unchanged stdout/stderr file is not evidence of
            // work. Content/length growth is the trusted output signal here.
            return newSize > oldSize;
        }

        private void SaveBackground(BackgroundProbeResult result, string signature,
            Dictionary<int, long> ticks, Dictionary<int, ulong> io,
            Dictionary<string, long> fileSizes, Dictionary<string, long> fileWriteTicks)
        {
            lock (_sync)
            {
                BackgroundSampleState state;
                if (!_backgroundStates.TryGetValue(signature, out state))
                {
                    state = new BackgroundSampleState();
                    _backgroundStates[signature] = state;
                }
                if (!result.Available)
                {
                    // Do not turn a temporary access/read failure into a valid
                    // comparison baseline. The next successful sample must still
                    // be treated as the first sample.
                    state.LastSeenUtc = DateTime.UtcNow;
                    state.LastResult = CloneBackground(result);
                    return;
                }
                state.CpuTicks.Clear();
                state.IoBytes.Clear();
                state.FileSizes.Clear();
                state.FileWriteTicks.Clear();
                foreach (KeyValuePair<int, long> pair in ticks) state.CpuTicks[pair.Key] = pair.Value;
                foreach (KeyValuePair<int, ulong> pair in io) state.IoBytes[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, long> pair in fileSizes) state.FileSizes[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, long> pair in fileWriteTicks) state.FileWriteTicks[pair.Key] = pair.Value;
                state.LastSampleUtc = DateTime.UtcNow;
                state.LastSeenUtc = DateTime.UtcNow;
                state.LastResult = CloneBackground(result);
            }
        }

        private static BackgroundProbeResult CloneBackground(BackgroundProbeResult input)
        {
            if (input == null) return null;
            BackgroundProbeResult output = new BackgroundProbeResult();
            output.Available = input.Available;
            output.Unknown = input.Unknown;
            output.AnyAlive = input.AnyAlive;
            output.Busy = input.Busy;
            output.HasComparison = input.HasComparison;
            output.AliveProcessCount = input.AliveProcessCount;
            output.ConsecutiveQuietSamples = input.ConsecutiveQuietSamples;
            output.LastProgressUtc = input.LastProgressUtc;
            output.Note = input.Note;
            return output;
        }

        private bool ReadProcessTreeCached(out Dictionary<int, int> parents, out Dictionary<int, string> names)
        {
            lock (_sync)
            {
                if (_treeCacheParents != null && _treeCacheNames != null &&
                    _treeCacheUtc != DateTime.MinValue && DateTime.UtcNow - _treeCacheUtc < TimeSpan.FromSeconds(2))
                {
                    parents = _treeCacheParents;
                    names = _treeCacheNames;
                    return true;
                }
            }

            Dictionary<int, int> freshParents = new Dictionary<int, int>();
            Dictionary<int, string> freshNames = new Dictionary<int, string>();
            if (!ReadProcessTree(freshParents, freshNames))
            {
                parents = freshParents;
                names = freshNames;
                return false;
            }
            lock (_sync)
            {
                _treeCacheParents = freshParents;
                _treeCacheNames = freshNames;
                _treeCacheUtc = DateTime.UtcNow;
                parents = _treeCacheParents;
                names = _treeCacheNames;
                return true;
            }
        }

        private static bool ReadProcessTree(Dictionary<int, int> parents, Dictionary<int, string> names)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == InvalidHandleValue || snapshot == IntPtr.Zero) return false;
            try
            {
                PROCESSENTRY32 entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snapshot, ref entry)) return false;
                do
                {
                    int pid = unchecked((int)entry.th32ProcessID);
                    int ppid = unchecked((int)entry.th32ParentProcessID);
                    parents[pid] = ppid;
                    names[pid] = entry.szExeFile ?? string.Empty;
                    entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                }
                while (Process32Next(snapshot, ref entry));
                return true;
            }
            finally { CloseHandle(snapshot); }
        }

        private void SaveUnavailable(ProcessProbeResult result)
        {
            lock (_sync)
            {
                _lastSampleUtc = DateTime.UtcNow;
                _lastResult = Clone(result);
            }
        }

        private static ProcessProbeResult Clone(ProcessProbeResult input)
        {
            if (input == null) return null;
            ProcessProbeResult output = new ProcessProbeResult();
            output.AnyCodexProcess = input.AnyCodexProcess;
            output.Busy = input.Busy;
            output.Available = input.Available;
            output.ProcessCount = input.ProcessCount;
            output.RootCount = input.RootCount;
            output.Note = input.Note;
            output.HasComparison = input.HasComparison;
            output.ConsecutiveQuietSamples = input.ConsecutiveQuietSamples;
            return output;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _lastCpuTicks.Clear();
                _lastIoBytes.Clear();
                _lastResult = null;
                _treeCacheParents = null;
                _treeCacheNames = null;
                _treeCacheUtc = DateTime.MinValue;
                _backgroundStates.Clear();
            }
        }
    }
}
