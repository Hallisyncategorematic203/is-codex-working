using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace IsCodexWorking
{
    internal sealed class MonitorEngine : IDisposable
    {
        private const int MaxRecentFiles = 256;
        private const int InitialTailBytes = 1024 * 1024;
        private const int MaxRecordBytes = 1024 * 1024;
        private const int MaxAppendReadBytes = 4 * 1024 * 1024;
        private const int BackgroundHintRecoveryBytes = 16 * 1024 * 1024;
        private const int PrefixFingerprintBytes = 4096;
        private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);
        // A rollout discovered cold with no recent real progress is history, not
        // evidence that a task is currently open. Keep this longer than the STUCK
        // window while excluding old dangling sessions such as 18-hour/18-day logs.
        internal static readonly TimeSpan StaleHistoryAfter = TimeSpan.FromHours(12);
        private static readonly TimeSpan ProcessProbeAfter = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan DoneVisibleFor = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan AttentionTerminalVisibleFor = TimeSpan.FromMinutes(10);
        private static readonly Regex WaitProcessPidRegex = new Regex(@"(?i)\bWait-Process\b[^\r\n]{0,120}?-Id\s+(?<pid>[1-9][0-9]{1,7})\b", RegexOptions.Compiled);
        private static readonly Regex JsonPidRegex = new Regex(@"(?i)[""'](?:pid|process_id|processid)[""']\s*[:=]\s*[""']?(?<pid>[1-9][0-9]{1,7})", RegexOptions.Compiled);
        private static readonly Regex LinePidRegex = new Regex(@"(?m)^\s*(?<pid>[1-9][0-9]{1,7})\s*$", RegexOptions.Compiled);
        private static readonly Regex RunRootRegex = new Regex(@"(?i)\$(?:run|root|runRoot)\s*=\s*[""'](?<path>[A-Z]:\\[^""']+)[""']", RegexOptions.Compiled);

        private sealed class NewestFileComparer : IComparer<FileInfo>
        {
            public static readonly NewestFileComparer Instance = new NewestFileComparer();

            public int Compare(FileInfo left, FileInfo right)
            {
                if (object.ReferenceEquals(left, right)) return 0;
                if (left == null) return 1;
                if (right == null) return -1;
                int time = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                if (time != 0) return time;
                return string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class NewestDirectoryComparer : IComparer<DirectoryInfo>
        {
            public static readonly NewestDirectoryComparer Instance = new NewestDirectoryComparer();

            public int Compare(DirectoryInfo left, DirectoryInfo right)
            {
                if (object.ReferenceEquals(left, right)) return 0;
                if (left == null) return 1;
                if (right == null) return -1;
                int name = string.Compare(right.Name, left.Name, StringComparison.OrdinalIgnoreCase);
                if (name != 0) return name;
                return string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static List<DirectoryInfo> GetNewestDirectories(DirectoryInfo parent, int limit)
        {
            List<DirectoryInfo> result = new List<DirectoryInfo>();
            if (parent == null || limit <= 0) return result;
            foreach (DirectoryInfo directory in parent.EnumerateDirectories())
            {
                int index = result.BinarySearch(directory, NewestDirectoryComparer.Instance);
                if (index < 0) index = ~index;
                result.Insert(index, directory);
                if (result.Count > limit) result.RemoveAt(result.Count - 1);
            }
            return result;
        }

        private readonly string _sessionsRoot;
        private readonly object _sync = new object();
        private readonly object _workGate = new object();
        private readonly Dictionary<string, SessionTracker> _byPath = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SessionTracker> _byId = new Dictionary<string, SessionTracker>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguousIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _pathRetryAttempts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _batchTimer;
        private readonly Timer _watchdogTimer;
        private readonly Timer _deadlineTimer;
        private readonly ProcessProbe _processProbe = new ProcessProbe();
        private readonly Dictionary<string, GroupLifecycle> _groupHistory =
            new Dictionary<string, GroupLifecycle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _groupAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ParserMetricsSnapshot _metrics = new ParserMetricsSnapshot();
        private long _appendReadOperations;
        private long _appendReadBytes;
        private long _parsedRecordCount;
        private long _boundedResyncCount;
        private long _boundedResyncBytes;
        private long _directoryScanCount;
        private FileSystemWatcher _watcher;
        private bool _batchScheduled;
        private bool _batchRunning;
        private bool _resyncNeeded;
        private int _activeParser;
        private bool _notificationBaselineEstablished;
        private DateTime _lastDiscoveryUtc = DateTime.MinValue;
        private StatusSnapshot _current;
        private bool _disposed;
#if TEST_BUILD
        internal BackgroundProbeResult BackgroundProbeOverrideForTests;
        internal ProcessProbeResult ProcessProbeOverrideForTests;
        internal TimeSpan BackgroundProbeIntervalForTests = TimeSpan.FromSeconds(30);
#endif

        public event Action<StatusSnapshot> SnapshotChanged;
        public event Action<GroupStatusSnapshot> GroupNotification;

        public MonitorEngine() : this(null)
        {
        }

        internal MonitorEngine(string sessionsRoot)
        {
            if (string.IsNullOrWhiteSpace(sessionsRoot))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                _sessionsRoot = Path.Combine(home, ".codex", "sessions");
            }
            else
            {
                _sessionsRoot = Path.GetFullPath(sessionsRoot);
            }
            _batchTimer = new Timer(ProcessDirtyBatch, null, Timeout.Infinite, Timeout.Infinite);
            _watchdogTimer = new Timer(WatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
            _deadlineTimer = new Timer(DeadlineTick, null, Timeout.Infinite, Timeout.Infinite);
            _current = MakeIdle("Looking for local Codex sessions");
        }

        public StatusSnapshot Current
        {
            get
            {
                lock (_sync) return _current.Clone();
            }
        }

        internal ParserMetricsSnapshot Metrics
        {
            get
            {
                lock (_sync)
                {
                    ParserMetricsSnapshot copy = _metrics.Clone();
                    copy.AppendReadOperations = Interlocked.Read(ref _appendReadOperations);
                    copy.AppendReadBytes = Interlocked.Read(ref _appendReadBytes);
                    copy.ParsedRecordCount = Interlocked.Read(ref _parsedRecordCount);
                    copy.BoundedResyncCount = Interlocked.Read(ref _boundedResyncCount);
                    copy.BoundedResyncBytes = Interlocked.Read(ref _boundedResyncBytes);
                    copy.DirectoryScanCount = Interlocked.Read(ref _directoryScanCount);
                    return copy;
                }
            }
        }

        public void Start()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_workGate)
                {
                    DiscoverRecent();
                    StartWatcherIfPossible();
                    RecomputeAndPublish(true);
                }
                _watchdogTimer.Change(30000, 30000);
            });
        }

        private void StartWatcherIfPossible()
        {
            if (_disposed) return;
            if (!Directory.Exists(_sessionsRoot)) return;
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl");
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite;
                watcher.InternalBufferSize = 8192;
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnRenamed;
                watcher.Deleted += OnDeleted;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                lock (_sync)
                {
                    if (_watcher != null) _watcher.Dispose();
                    _watcher = watcher;
                }
            }
            catch
            {
                lock (_sync) _resyncNeeded = true;
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.FullPath)) return;
            MarkDirty(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (e == null) return;
            if (!string.IsNullOrEmpty(e.OldFullPath)) RemoveTracker(e.OldFullPath);
            if (!string.IsNullOrEmpty(e.FullPath)) MarkDirty(e.FullPath);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (e == null) return;
            RemoveTracker(e.FullPath);
            ScheduleBatchWithoutPath();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            FileSystemWatcher broken = null;
            lock (_sync)
            {
                _resyncNeeded = true;
                if (object.ReferenceEquals(sender, _watcher))
                {
                    broken = _watcher;
                    _watcher = null;
                }
            }
            if (broken != null)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { broken.EnableRaisingEvents = false; } catch { }
                    try { broken.Dispose(); } catch { }
                });
            }
            ScheduleBatchWithoutPath();
        }

        private void MarkDirty(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_sync)
            {
                if (_disposed) return;
                _dirty.Add(path);
                if (!_batchScheduled && !_batchRunning)
                {
                    _batchScheduled = true;
                    _batchTimer.Change(750, Timeout.Infinite);
                }
            }
        }

        private void ScheduleBatchWithoutPath()
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (!_batchScheduled && !_batchRunning)
                {
                    _batchScheduled = true;
                    _batchTimer.Change(750, Timeout.Infinite);
                }
            }
        }

        private void BeginParserBatch()
        {
            int active = Interlocked.Increment(ref _activeParser);
            lock (_sync)
            {
                _metrics.BatchCount++;
                if (active > _metrics.MaxConcurrentParser) _metrics.MaxConcurrentParser = active;
            }
        }

        private void EndParserBatch()
        {
            Interlocked.Decrement(ref _activeParser);
        }

        private void ProcessDirtyBatch(object state)
        {
            if (_disposed) return;
            List<string> paths;
            bool resync;
            lock (_sync)
            {
                if (_batchRunning)
                {
                    _batchScheduled = false;
                    return;
                }
                _batchRunning = true;
                paths = _dirty.ToList();
                _dirty.Clear();
                resync = _resyncNeeded;
                _resyncNeeded = false;
                _batchScheduled = false;
            }

            try
            {
                BeginParserBatch();
                lock (_workGate)
                {
                    if (resync)
                    {
                        DiscoverRecent();
                        StartWatcherIfPossible();
                    }
                    foreach (string path in paths)
                    {
                        try
                        {
                            ProcessPath(path);
                            lock (_sync) _pathRetryAttempts.Remove(path);
                        }
                        catch
                        {
                            SchedulePathRetry(path);
                        }
                    }
                    RecomputeAndPublish(false);
                }
            }
            finally
            {
                EndParserBatch();
                lock (_sync)
                {
                    _batchRunning = false;
                    if (!_disposed && (_dirty.Count > 0 || _resyncNeeded) && !_batchScheduled)
                    {
                        _batchScheduled = true;
                        _batchTimer.Change(750, Timeout.Infinite);
                    }
                }
            }
        }

        private void WatchdogTick(object state)
        {
            if (_disposed) return;
            if (!System.Threading.Monitor.TryEnter(_workGate)) return;
            try
            {
                if (!Directory.Exists(_sessionsRoot))
                {
                    StartWatcherIfPossible();
                    RecomputeAndPublish(false);
                    return;
                }

                bool needDiscovery = false;
                lock (_sync)
                {
                    if (_watcher == null) needDiscovery = true;
                    if ((DateTime.UtcNow - _lastDiscoveryUtc) > TimeSpan.FromMinutes(5)) needDiscovery = true;
                }
                if (needDiscovery)
                {
                    DiscoverRecent();
                    bool watcherMissing;
                    lock (_sync) watcherMissing = _watcher == null;
                    if (watcherMissing) StartWatcherIfPossible();
                }

                List<SessionTracker> trackers;
                lock (_sync) trackers = _byPath.Values.ToList();
                foreach (SessionTracker tracker in trackers)
                {
                    try
                    {
                        FileInfo info = new FileInfo(tracker.Path);
                        if (!info.Exists) continue;
                        bool changed;
                        lock (tracker.Sync)
                        {
                            changed = info.Length != tracker.Offset || info.LastWriteTimeUtc != tracker.LastWriteUtc;
                        }
                        if (changed) MarkDirty(tracker.Path);
                    }
                    catch { }
                }
                RecomputeAndPublish(false);
            }
            catch { }
            finally { System.Threading.Monitor.Exit(_workGate); }
        }

        private void SchedulePathRetry(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_sync)
            {
                int attempts;
                _pathRetryAttempts.TryGetValue(path, out attempts);
                attempts++;
                if (attempts > 6)
                {
                    _pathRetryAttempts.Remove(path);
                    return;
                }
                _pathRetryAttempts[path] = attempts;
                _dirty.Add(path);
            }
        }

        private void DeadlineTick(object state)
        {
            if (_disposed) return;
            if (!System.Threading.Monitor.TryEnter(_workGate))
            {
                try { _deadlineTimer.Change(250, Timeout.Infinite); } catch { }
                return;
            }
            try { RecomputeAndPublish(false); }
            catch { }
            finally { System.Threading.Monitor.Exit(_workGate); }
        }

        private void ScheduleNextDeadline()
        {
            if (_disposed) return;
            DateTime now = DateTime.UtcNow;
            DateTime next = DateTime.MaxValue;
            List<GroupStatusSnapshot> groups;
            List<SessionTracker> trackers;
            lock (_sync)
            {
                groups = _current == null || _current.Groups == null
                    ? new List<GroupStatusSnapshot>()
                    : _current.Groups.Select(g => g == null ? null : g.Clone()).ToList();
                trackers = _byPath.Values.ToList();
            }

            foreach (GroupStatusSnapshot group in groups)
            {
                if (group == null || group.State != PublicState.Done ||
                    group.EffectiveCompletionUtc == DateTime.MinValue) continue;
                DateTime due = group.EffectiveCompletionUtc + DoneVisibleFor;
                if (due < next) next = due;
            }

            foreach (SessionTracker tracker in trackers)
            {
                lock (tracker.Sync)
                {
                    if (tracker.TurnOpen && tracker.LastMeaningfulUtc != DateTime.MinValue)
                    {
                        DateTime due = tracker.LastMeaningfulUtc + StuckAfter;
                        if (due > now && due < next) next = due;
                        else if (due <= now &&
                            (tracker.LastDeadlineWakeUtc == DateTime.MinValue ||
                             now - tracker.LastDeadlineWakeUtc >= TimeSpan.FromSeconds(5)))
                        {
                            // Scheduler jitter can put us just past the semantic
                            // boundary. Give it one immediate one-shot retry, but
                            // do not spin while a tool needs confirmation samples.
                            tracker.LastDeadlineWakeUtc = now;
                            DateTime retry = now.AddMilliseconds(50);
                            if (retry < next) next = retry;
                        }
                        DateTime probeDue = tracker.LastProcessProbeUtc == DateTime.MinValue
                            ? tracker.LastMeaningfulUtc + ProcessProbeAfter
                            : tracker.LastProcessProbeUtc + TimeSpan.FromSeconds(30);
                        if (probeDue > now && probeDue < next) next = probeDue;
                    }
                    if (tracker.TurnOpen && tracker.StreamErrorsSinceProgress > 0 &&
                        tracker.LastStreamErrorUtc != DateTime.MinValue)
                    {
                        DateTime due = tracker.LastStreamErrorUtc + TimeSpan.FromMinutes(1);
                        if (due > now && due < next) next = due;
                    }
                    if ((tracker.Terminal == TerminalKind.Error || tracker.Terminal == TerminalKind.LimitReached) &&
                        tracker.LastTerminalUtc != DateTime.MinValue)
                    {
                        DateTime due = tracker.LastTerminalUtc + AttentionTerminalVisibleFor;
                        if (due > now && due < next) next = due;
                    }
                }
            }

            try
            {
                if (next == DateTime.MaxValue)
                {
                    _deadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                else
                {
                    TimeSpan wait = next - now;
                    long milliseconds = (long)Math.Max(50, Math.Min(int.MaxValue, wait.TotalMilliseconds));
                    _deadlineTimer.Change((int)milliseconds, Timeout.Infinite);
                }
            }
            catch { }
        }

        private void DiscoverRecent()
        {
            if (!Directory.Exists(_sessionsRoot))
            {
                lock (_sync) _lastDiscoveryUtc = DateTime.UtcNow;
                return;
            }

            List<string> files = DiscoverRecentPaths(MaxRecentFiles);
            int hotLoaded = 0;
            DateTime hotCutoff = DateTime.UtcNow - TimeSpan.FromHours(6);
            foreach (string path in files)
            {
                try
                {
                    SessionTracker tracker = GetOrCreateTracker(path);
                    EnsureMeta(tracker);
                    FileInfo info = new FileInfo(path);
                    bool hot = hotLoaded < 12 || (hotLoaded < 20 && info.Exists && info.LastWriteTimeUtc >= hotCutoff);
                    if (hot)
                    {
                        bool hasOffset;
                        SessionMeta meta;
                        lock (tracker.Sync)
                        {
                            hasOffset = tracker.Offset > 0;
                            meta = tracker.Meta;
                        }
                        if (hasOffset) ReadAppended(tracker);
                        else if (meta != null && meta.HasInheritedHistoryRisk && meta.SubagentHistoryStartOrdinal < 0)
                            BaselineAtEnd(tracker);
                        else ResyncTail(tracker);
                        hotLoaded++;
                    }
                }
                catch { }
            }
            PruneOldTrackers(new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
            lock (_sync) _lastDiscoveryUtc = DateTime.UtcNow;
        }

        private void PruneOldTrackers(HashSet<string> recentPaths)
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            List<string> remove = new List<string>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, SessionTracker> pair in _byPath)
                {
                    if (recentPaths.Contains(pair.Key)) continue;
                    SessionTracker tracker = pair.Value;
                    bool keep;
                    lock (tracker.Sync)
                    {
                        DateTime activity = tracker.LastAnyUtc != DateTime.MinValue ? tracker.LastAnyUtc : tracker.LastWriteUtc;
                        keep = tracker.TurnOpen || (activity != DateTime.MinValue && activity >= cutoff);
                    }
                    if (!keep) remove.Add(pair.Key);
                }
            }
            foreach (string path in remove) RemoveTracker(path);
        }

        private List<string> DiscoverRecentPaths(int limit)
        {
            Interlocked.Increment(ref _directoryScanCount);
            List<string> result = new List<string>();
            int candidateLimit = Math.Max(0, limit * 3);
            List<FileInfo> candidates = new List<FileInfo>();
            try
            {
                DirectoryInfo root = new DirectoryInfo(_sessionsRoot);
                DirectoryInfo[] years = GetNewestDirectories(root, 2).ToArray();
                foreach (DirectoryInfo year in years)
                {
                    DirectoryInfo[] months = GetNewestDirectories(year, 12).ToArray();
                    foreach (DirectoryInfo month in months)
                    {
                        DirectoryInfo[] days = GetNewestDirectories(month, 31).ToArray();
                        foreach (DirectoryInfo day in days)
                        {
                            foreach (FileInfo file in day.EnumerateFiles("rollout-*.jsonl", SearchOption.TopDirectoryOnly))
                            {
                                if (candidateLimit == 0) break;
                                int index = candidates.BinarySearch(file, NewestFileComparer.Instance);
                                if (index < 0) index = ~index;
                                candidates.Insert(index, file);
                                if (candidates.Count > candidateLimit) candidates.RemoveAt(candidates.Count - 1);
                            }
                        }
                    }
                }
            }
            catch { }
            foreach (FileInfo file in candidates) result.Add(file.FullName);
            return result.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(limit)
                .Select(f => f.FullName)
                .ToList();
        }

        private SessionTracker GetOrCreateTracker(string path)
        {
            lock (_sync)
            {
                SessionTracker tracker;
                if (_byPath.TryGetValue(path, out tracker)) return tracker;
                tracker = new SessionTracker();
                tracker.Path = path;
                _byPath[path] = tracker;
                return tracker;
            }
        }

        private void RemoveTracker(string path)
        {
            lock (_sync)
            {
                SessionTracker tracker;
                if (!_byPath.TryGetValue(path, out tracker)) return;
                _byPath.Remove(path);
                if (tracker.Id != null)
                {
                    SessionTracker current;
                    if (_byId.TryGetValue(tracker.Id, out current) && object.ReferenceEquals(current, tracker))
                        _byId.Remove(tracker.Id);
                }
            }
        }

        private void EnsureMeta(SessionTracker tracker)
        {
            bool needed;
            lock (tracker.Sync) needed = tracker.Meta == null || !tracker.Meta.MetadataComplete;
            if (!needed) return;
            SessionMeta meta = ReadSessionMeta(tracker.Path);
            lock (tracker.Sync) tracker.Meta = meta;
            if (meta != null && !string.IsNullOrEmpty(meta.Id))
            {
                lock (_sync)
                {
                    if (_ambiguousIds.Contains(meta.Id))
                    {
                        tracker.AmbiguousId = true;
                        _byId.Remove(meta.Id);
                        return;
                    }
                    SessionTracker existing;
                    if (_byId.TryGetValue(meta.Id, out existing) && !object.ReferenceEquals(existing, tracker))
                    {
                        _byId.Remove(meta.Id);
                        _ambiguousIds.Add(meta.Id);
                        existing.AmbiguousId = true;
                        tracker.AmbiguousId = true;
                    }
                    else if (!tracker.AmbiguousId)
                    {
                        _byId[meta.Id] = tracker;
                    }
                }
            }
        }

        private void InitializeTracker(SessionTracker tracker)
        {
            EnsureMeta(tracker);
            SessionMeta meta;
            lock (tracker.Sync) meta = tracker.Meta;
            if (meta == null || !meta.MetadataComplete)
                BaselineAtEnd(tracker);
            else if (meta.HasInheritedHistoryRisk && meta.SubagentHistoryStartOrdinal < 0)
                BaselineAtEnd(tracker);
            else
                ResyncTail(tracker);
        }

        // Legacy child/fork rollouts can begin with a full copy of parent history,
        // sometimes with timestamps rewritten to the child creation time. Replaying
        // that prefix can manufacture false live activity. For those files, start at
        // EOF and observe only new appends. Parent/root activity still represents the
        // grouped task, and the child becomes visible on its next real append.
        private static void BaselineAtEnd(SessionTracker tracker)
        {
            try
            {
                FileInfo info = new FileInfo(tracker.Path);
                if (!info.Exists) return;
                long prefixLength = Math.Min((long)PrefixFingerprintBytes, info.Length);
                long prefix = ReadPrefixFingerprint(tracker.Path, prefixLength);
                ResetDynamicState(tracker);
                lock (tracker.Sync)
                {
                    // The copied prefix is intentionally skipped. Bytes appended after
                    // this baseline are new child activity and may open the observed
                    // child stream even though its original start record was skipped.
                    tracker.AllowImplicitOpenAfterBaseline = true;
                    tracker.Offset = info.Length;
                    tracker.PrefixFingerprint = prefix;
                    tracker.PrefixFingerprintLength = prefixLength;
                    tracker.TailFingerprintLength = Math.Min((long)PrefixFingerprintBytes, info.Length);
                    tracker.TailFingerprintStart = info.Length - tracker.TailFingerprintLength;
                    tracker.TailFingerprint = ReadFingerprint(tracker.Path,
                        tracker.TailFingerprintStart, tracker.TailFingerprintLength);
                    tracker.LastWriteUtc = info.LastWriteTimeUtc;
                    tracker.CreationUtc = info.CreationTimeUtc;
                }
            }
            catch { }
        }

        private SessionMeta ReadSessionMeta(string path)
        {
            const int PrefixLimit = 128 * 1024;
            try
            {
                byte[] buffer = new byte[PrefixLimit];
                int count = 0;
                bool newlineFound = false;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    while (count < buffer.Length)
                    {
                        int n = fs.Read(buffer, count, Math.Min(4096, buffer.Length - count));
                        if (n <= 0) break;
                        int j;
                        for (j = count; j < count + n; j++)
                        {
                            if (buffer[j] == (byte)'\n')
                            {
                                count = j;
                                newlineFound = true;
                                break;
                            }
                        }
                        if (newlineFound) break;
                        count += n;
                    }
                }
                if (count <= 0) return null;
                string prefix = Encoding.UTF8.GetString(buffer, 0, count).TrimStart('\uFEFF');

                if (newlineFound)
                {
                    Dictionary<string, object> obj = JsonUtil.ParseObject(prefix);
                    if (obj != null && string.Equals(JsonUtil.String(obj, "type"), "session_meta", StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> payload = JsonUtil.Dict(obj, "payload");
                        if (payload != null)
                        {
                    SessionMeta parsed = new SessionMeta();
                    parsed.MetadataComplete = true;
                            parsed.Id = JsonUtil.String(payload, "id");
                            if (string.IsNullOrEmpty(parsed.Id)) parsed.Id = JsonUtil.String(payload, "session_id");
                            parsed.ParentThreadId = JsonUtil.String(payload, "parent_thread_id");
                            parsed.ForkedFromId = JsonUtil.String(payload, "forked_from_id");
                            parsed.Cwd = JsonUtil.String(payload, "cwd");
                            parsed.ThreadSource = JsonUtil.String(payload, "thread_source");
                            parsed.HistoryMode = JsonUtil.String(payload, "history_mode");
                            long historyStartOrdinal;
                            if (JsonUtil.TryGetLong(payload, "subagent_history_start_ordinal", out historyStartOrdinal))
                                parsed.SubagentHistoryStartOrdinal = historyStartOrdinal;
                            parsed.CreatedUtc = JsonUtil.TimestampUtc(obj, DateTime.MinValue);
                            return parsed;
                        }
                    }
                }

                if (prefix.IndexOf("\"type\":\"session_meta\"", StringComparison.Ordinal) < 0 &&
                    prefix.IndexOf("\"type\": \"session_meta\"", StringComparison.Ordinal) < 0)
                    return null;
                SessionMeta meta = new SessionMeta();
                meta.MetadataComplete = false;
                meta.Id = JsonUtil.ExtractJsonStringField(prefix, "id");
                if (string.IsNullOrEmpty(meta.Id)) meta.Id = JsonUtil.ExtractJsonStringField(prefix, "session_id");
                meta.ParentThreadId = JsonUtil.ExtractJsonStringField(prefix, "parent_thread_id");
                meta.ForkedFromId = JsonUtil.ExtractJsonStringField(prefix, "forked_from_id");
                meta.Cwd = JsonUtil.ExtractJsonStringField(prefix, "cwd");
                meta.ThreadSource = JsonUtil.ExtractJsonStringField(prefix, "thread_source");
                meta.HistoryMode = JsonUtil.ExtractJsonStringField(prefix, "history_mode");
                meta.CreatedUtc = JsonUtil.ParseUtc(JsonUtil.ExtractJsonStringField(prefix, "timestamp"), DateTime.MinValue);
                return string.IsNullOrEmpty(meta.Id) ? null : meta;
            }
            catch { return null; }
        }

        private void ProcessPath(string path)
        {
            if (!File.Exists(path)) return;
            SessionTracker tracker = GetOrCreateTracker(path);
            bool needsInitialization;
            bool wasIncomplete;
            lock (tracker.Sync)
            {
                needsInitialization = tracker.Meta == null;
                wasIncomplete = tracker.Meta != null && !tracker.Meta.MetadataComplete;
            }
            if (needsInitialization) InitializeTracker(tracker);
            else if (wasIncomplete)
            {
                EnsureMeta(tracker);
                SessionMeta refreshed;
                lock (tracker.Sync) refreshed = tracker.Meta;
                if (refreshed != null && refreshed.MetadataComplete &&
                    refreshed.HasInheritedHistoryRisk && refreshed.SubagentHistoryStartOrdinal < 0)
                    BaselineAtEnd(tracker);
            }
            ReadAppended(tracker);
        }

        private void ReadAppended(SessionTracker tracker)
        {
            FileInfo info = new FileInfo(tracker.Path);
            if (!info.Exists) return;

            long offset;
            DateTime lastWrite;
            DateTime creation;
            long prefixFingerprint;
            long prefixFingerprintLength;
            long tailFingerprint;
            long tailFingerprintStart;
            long tailFingerprintLength;
            lock (tracker.Sync)
            {
                offset = tracker.Offset;
                lastWrite = tracker.LastWriteUtc;
                creation = tracker.CreationUtc;
                prefixFingerprint = tracker.PrefixFingerprint;
                prefixFingerprintLength = tracker.PrefixFingerprintLength;
                tailFingerprint = tracker.TailFingerprint;
                tailFingerprintStart = tracker.TailFingerprintStart;
                tailFingerprintLength = tracker.TailFingerprintLength;
            }

            long currentPrefix = ReadPrefixFingerprint(tracker.Path,
                prefixFingerprintLength > 0 ? prefixFingerprintLength : Math.Min((long)PrefixFingerprintBytes, info.Length));
            long currentTail = tailFingerprintLength > 0 && info.Length >= tailFingerprintStart + tailFingerprintLength
                ? ReadFingerprint(tracker.Path, tailFingerprintStart, tailFingerprintLength) : 0;
            bool replaced = offset > info.Length ||
                (creation != DateTime.MinValue && info.CreationTimeUtc != creation) ||
                (offset == info.Length && lastWrite != DateTime.MinValue && info.LastWriteTimeUtc != lastWrite) ||
                (prefixFingerprintLength > 0 && prefixFingerprint != 0 && currentPrefix != 0 &&
                 prefixFingerprint != currentPrefix) ||
                (tailFingerprintLength > 0 && tailFingerprint != 0 && currentTail != 0 &&
                 tailFingerprint != currentTail);
            if (replaced)
            {
                ResyncAfterReplacement(tracker);
                return;
            }
            if (info.Length <= offset)
            {
                lock (tracker.Sync)
                {
                    tracker.PrefixFingerprint = currentPrefix;
                    if (tracker.PrefixFingerprintLength == 0)
                        tracker.PrefixFingerprintLength = Math.Min((long)PrefixFingerprintBytes, info.Length);
                    tracker.TailFingerprintLength = Math.Min((long)PrefixFingerprintBytes, info.Length);
                    tracker.TailFingerprintStart = info.Length - tracker.TailFingerprintLength;
                    tracker.TailFingerprint = ReadFingerprint(tracker.Path,
                        tracker.TailFingerprintStart, tracker.TailFingerprintLength);
                    tracker.LastWriteUtc = info.LastWriteTimeUtc;
                    tracker.CreationUtc = info.CreationTimeUtc;
                }
                return;
            }

            byte[] newBytes;
            using (FileStream fs = new FileStream(tracker.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (offset > fs.Length) { ResyncAfterReplacement(tracker); return; }
                fs.Seek(offset, SeekOrigin.Begin);
                long countLong = fs.Length - offset;
                int count = (int)Math.Min((long)MaxAppendReadBytes, countLong);
                newBytes = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = fs.Read(newBytes, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != count) Array.Resize(ref newBytes, read);
                offset = fs.Position;
            }

            ConsumeBytes(tracker, newBytes, DateTime.UtcNow);
            Interlocked.Increment(ref _appendReadOperations);
            Interlocked.Add(ref _appendReadBytes, newBytes.Length);
            info.Refresh();
            bool morePending = false;
            lock (tracker.Sync)
            {
                tracker.Offset = offset;
                tracker.PrefixFingerprint = currentPrefix;
                if (tracker.PrefixFingerprintLength == 0)
                    tracker.PrefixFingerprintLength = Math.Min((long)PrefixFingerprintBytes, offset);
                tracker.TailFingerprintLength = Math.Min((long)PrefixFingerprintBytes, offset);
                tracker.TailFingerprintStart = offset - tracker.TailFingerprintLength;
                tracker.TailFingerprint = ReadFingerprint(tracker.Path,
                    tracker.TailFingerprintStart, tracker.TailFingerprintLength);
                tracker.LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow;
                tracker.CreationUtc = info.Exists ? info.CreationTimeUtc : creation;
                morePending = info.Exists && info.Length > tracker.Offset;
            }
            if (morePending) MarkDirty(tracker.Path);
        }

        private void ResyncAfterReplacement(SessionTracker tracker)
        {
            SessionMeta replacementMeta;
            lock (tracker.Sync) replacementMeta = tracker.Meta;
            string oldId = replacementMeta == null ? null : replacementMeta.Id;
            if (!string.IsNullOrEmpty(oldId))
            {
                lock (_sync)
                {
                    SessionTracker mapped;
                    if (_byId.TryGetValue(oldId, out mapped) && object.ReferenceEquals(mapped, tracker))
                        _byId.Remove(oldId);
                }
            }
            lock (tracker.Sync)
            {
                tracker.Meta = null;
                tracker.BackgroundProcesses.Clear();
                tracker.BackgroundRoots.Clear();
                tracker.PendingBackgroundLaunchTimes.Clear();
                tracker.PendingBackgroundLaunchUtc = DateTime.MinValue;
                tracker.LastProcessProbeUtc = DateTime.MinValue;
                tracker.BackgroundHintScanUtc = DateTime.MinValue;
                tracker.BackgroundReceiptScanUtc = DateTime.MinValue;
            }
            EnsureMeta(tracker);
            lock (tracker.Sync) replacementMeta = tracker.Meta;
            if (replacementMeta != null && replacementMeta.HasInheritedHistoryRisk &&
                replacementMeta.SubagentHistoryStartOrdinal < 0)
                BaselineAtEnd(tracker);
            else
                ResyncTail(tracker);
        }

        private void ResyncTail(SessionTracker tracker)
        {
            try
            {
                Interlocked.Increment(ref _boundedResyncCount);
                using (FileStream fs = new FileStream(tracker.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = fs.Length;
                    long start = Math.Max(0, length - InitialTailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] bytes = new byte[(int)(length - start)];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = fs.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != bytes.Length) Array.Resize(ref bytes, read);
                    Interlocked.Add(ref _boundedResyncBytes, bytes.Length);

                    ResetDynamicState(tracker);
                    if (start > 0)
                    {
                        int firstNl = Array.IndexOf(bytes, (byte)'\n');
                        if (firstNl >= 0 && firstNl + 1 < bytes.Length)
                        {
                            byte[] trimmed = new byte[bytes.Length - firstNl - 1];
                            Buffer.BlockCopy(bytes, firstNl + 1, trimmed, 0, trimmed.Length);
                            bytes = trimmed;
                        }
                        else bytes = new byte[0];
                    }
                    ConsumeBytes(tracker, bytes, DateTime.UtcNow);
                    FileInfo info = new FileInfo(tracker.Path);
                    lock (tracker.Sync)
                    {
                        tracker.Offset = length;
                        tracker.PrefixFingerprintLength = Math.Min((long)PrefixFingerprintBytes, length);
                        tracker.PrefixFingerprint = ReadPrefixFingerprint(tracker.Path, tracker.PrefixFingerprintLength);
                        tracker.TailFingerprintLength = Math.Min((long)PrefixFingerprintBytes, length);
                        tracker.TailFingerprintStart = length - tracker.TailFingerprintLength;
                        tracker.TailFingerprint = ReadFingerprint(tracker.Path,
                            tracker.TailFingerprintStart, tracker.TailFingerprintLength);
                        tracker.LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow;
                        tracker.CreationUtc = info.Exists ? info.CreationTimeUtc : DateTime.MinValue;
                    }
                }
            }
            catch { }
        }

        private static void ResetDynamicState(SessionTracker tracker)
        {
            lock (tracker.Sync)
            {
                tracker.Carry = new byte[0];
                tracker.DiscardUntilNewline = false;
                tracker.TurnOpen = false;
                tracker.CurrentTurnId = null;
                tracker.TaskStartedUtc = DateTime.MinValue;
                tracker.LastMeaningfulUtc = DateTime.MinValue;
                tracker.LastAnyUtc = DateTime.MinValue;
                tracker.LastTerminalUtc = DateTime.MinValue;
                tracker.LastWaitingUtc = DateTime.MinValue;
                tracker.LastErrorUtc = DateTime.MinValue;
                tracker.LastLimitUtc = DateTime.MinValue;
                tracker.LastAssistantReplyUtc = DateTime.MinValue;
                tracker.WaitingForUser = false;
                tracker.Terminal = TerminalKind.None;
                tracker.TerminalReason = null;
                tracker.AgentReplySeenSinceTaskStart = false;
                tracker.ActiveToolCount = 0;
                tracker.ActiveToolIds.Clear();
                tracker.PendingBackgroundLaunchTimes.Clear();
                tracker.PendingBackgroundLaunchUtc = DateTime.MinValue;
                tracker.LastTokenTotal = -1;
                tracker.LastStreamErrorUtc = DateTime.MinValue;
                tracker.StreamErrorsSinceProgress = 0;
                tracker.ExplicitTurnStartSeen = false;
                tracker.AllowImplicitOpenAfterBaseline = false;
                tracker.LastDeadlineWakeUtc = DateTime.MinValue;
            }
        }

        private void ConsumeBytes(SessionTracker tracker, byte[] added, DateTime fallbackUtc)
        {
            if (added == null || added.Length == 0) return;
            byte[] data;
            bool discard;
            lock (tracker.Sync)
            {
                discard = tracker.DiscardUntilNewline;
                byte[] carry = tracker.Carry ?? new byte[0];
                data = new byte[carry.Length + added.Length];
                if (carry.Length > 0) Buffer.BlockCopy(carry, 0, data, 0, carry.Length);
                Buffer.BlockCopy(added, 0, data, carry.Length, added.Length);
                tracker.Carry = new byte[0];
            }

            int cursor = 0;
            if (discard)
            {
                int nl = IndexOf(data, (byte)'\n', 0);
                if (nl < 0) return;
                cursor = nl + 1;
                lock (tracker.Sync) tracker.DiscardUntilNewline = false;
            }

            int lineStart = cursor;
            while (true)
            {
                int nl = IndexOf(data, (byte)'\n', lineStart);
                if (nl < 0) break;
                int len = nl - lineStart;
                if (len > 0 && len <= MaxRecordBytes)
                {
                    try
                    {
                        string line = Encoding.UTF8.GetString(data, lineStart, len).TrimEnd('\r');
                        ProcessLine(tracker, line, fallbackUtc);
                        Interlocked.Increment(ref _parsedRecordCount);
                    }
                    catch { }
                }
                lineStart = nl + 1;
            }

            int remain = data.Length - lineStart;
            if (remain > MaxRecordBytes)
            {
                lock (tracker.Sync)
                {
                    tracker.Carry = new byte[0];
                    tracker.DiscardUntilNewline = true;
                }
            }
            else if (remain > 0)
            {
                byte[] carry = new byte[remain];
                Buffer.BlockCopy(data, lineStart, carry, 0, remain);
                lock (tracker.Sync) tracker.Carry = carry;
            }
        }

        private static int IndexOf(byte[] data, byte value, int start)
        {
            int i;
            for (i = start; i < data.Length; i++) if (data[i] == value) return i;
            return -1;
        }

        private static long ReadPrefixFingerprint(string path, long requestedLength)
        {
            if (string.IsNullOrEmpty(path) || requestedLength <= 0) return 0;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int count = (int)Math.Min(requestedLength, stream.Length);
                    byte[] bytes = new byte[count];
                    int read = 0;
                    while (read < count)
                    {
                        int n = stream.Read(bytes, read, count - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    unchecked
                    {
                        long hash = 1469598103934665603L;
                        for (int i = 0; i < read; i++)
                        {
                            hash ^= bytes[i];
                            hash *= 1099511628211L;
                        }
                        return hash == 0 ? 1 : hash;
                    }
                }
            }
            catch { return 0; }
        }

        private static long ReadFingerprint(string path, long start, long requestedLength)
        {
            if (string.IsNullOrEmpty(path) || start < 0 || requestedLength <= 0) return 0;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (start >= stream.Length) return 0;
                    long available = Math.Min(requestedLength, stream.Length - start);
                    int count = (int)Math.Min((long)PrefixFingerprintBytes, available);
                    byte[] bytes = new byte[count];
                    stream.Seek(start, SeekOrigin.Begin);
                    int read = 0;
                    while (read < count)
                    {
                        int n = stream.Read(bytes, read, count - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    unchecked
                    {
                        long hash = 1469598103934665603L;
                        for (int i = 0; i < read; i++)
                        {
                            hash ^= bytes[i];
                            hash *= 1099511628211L;
                        }
                        return hash == 0 ? 1 : hash;
                    }
                }
            }
            catch { return 0; }
        }

        internal static void ProcessLine(SessionTracker tracker, string line, DateTime fallbackUtc)
        {
            Dictionary<string, object> obj = JsonUtil.ParseObject(line);
            if (obj == null) return;
            string top = JsonUtil.String(obj, "type");
            Dictionary<string, object> payload = JsonUtil.Dict(obj, "payload");
            string kind = JsonUtil.String(payload, "type");
            DateTime ts = JsonUtil.TimestampUtc(obj, fallbackUtc);

            // Multi-agent child rollouts can contain inherited copies of the parent's
            // older history. Never let records from before this session was created
            // become fresh activity for the child.
            SessionMeta trackerMeta = tracker.Meta;
            if (trackerMeta != null && trackerMeta.CreatedUtc != DateTime.MinValue && ts < trackerMeta.CreatedUtc)
                return;

            // Paginated subagent rollouts expose the inherited-history cutoff as an
            // ordinal. Records before that boundary belong to copied parent history.
            if (trackerMeta != null && trackerMeta.SubagentHistoryStartOrdinal >= 0)
            {
                long ordinal;
                if (JsonUtil.TryGetLong(obj, "ordinal", out ordinal) &&
                    ordinal < trackerMeta.SubagentHistoryStartOrdinal) return;
            }

            bool startsTurn = string.Equals(top, "event_msg", StringComparison.OrdinalIgnoreCase) &&
                IsTurnStartedKind(kind);
            string eventTurnId = JsonUtil.String(payload, "turn_id");
            lock (tracker.Sync)
            {
                if (!IsChronologicallyAdmissible(tracker, startsTurn, eventTurnId, ts)) return;
            }

            ProcessBackgroundHintObject(tracker, top, kind, payload, ts);

            lock (tracker.Sync)
            {
                if (ts > tracker.LastAnyUtc) tracker.LastAnyUtc = ts;

                if (string.Equals(top, "event_msg", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsTurnStartedKind(kind))
                    {
                        tracker.TurnOpen = true;
                        string startedTurnId = JsonUtil.String(payload, "turn_id");
                        if (!string.IsNullOrEmpty(startedTurnId)) tracker.CurrentTurnId = startedTurnId;
                        tracker.TaskStartedUtc = ts;
                        tracker.ExplicitTurnStartSeen = true;
                    tracker.AllowImplicitOpenAfterBaseline = false;
                        tracker.WaitingForUser = false;
                        tracker.Terminal = TerminalKind.None;
                        tracker.TerminalReason = null;
                        tracker.LastTerminalUtc = DateTime.MinValue;
                        tracker.LastErrorUtc = DateTime.MinValue;
                        tracker.LastLimitUtc = DateTime.MinValue;
                        tracker.LastWaitingUtc = DateTime.MinValue;
                        tracker.AgentReplySeenSinceTaskStart = false;
                        tracker.ActiveToolCount = 0;
                tracker.ActiveToolIds.Clear();
                        tracker.LastTokenTotal = -1;
                        tracker.LastStreamErrorUtc = DateTime.MinValue;
                        tracker.StreamErrorsSinceProgress = 0;
                        tracker.LastDeadlineWakeUtc = DateTime.MinValue;
                        MarkMeaningful(tracker, ts);
                        return;
                    }

                    if (IsWaitingKind(kind))
                    {
                        if (EnsureObservedTurnOpen(tracker, ts))
                        {
                            tracker.WaitingForUser = true;
                            tracker.LastWaitingUtc = ts;
                        }
                        return;
                    }

                    if (IsUsageLimit(kind, payload))
                    {
                        tracker.LastLimitUtc = ts;
                        tracker.Terminal = TerminalKind.LimitReached;
                        tracker.TerminalReason = "Codex reported a usage limit";
                        tracker.TurnOpen = false;
                        tracker.WaitingForUser = false;
                        tracker.ActiveToolCount = 0;
                tracker.ActiveToolIds.Clear();
                        tracker.LastTerminalUtc = ts;
                        return;
                    }

                    if (string.Equals(kind, "turn_aborted", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kind, "task_aborted", StringComparison.OrdinalIgnoreCase))
                    {
                        tracker.TurnOpen = false;
                        tracker.WaitingForUser = false;
                        tracker.ActiveToolCount = 0;
                tracker.ActiveToolIds.Clear();
                        tracker.Terminal = TerminalKind.Aborted;
                        tracker.TerminalReason = "The turn was stopped";
                        tracker.LastTerminalUtc = ts;
                        return;
                    }

                    if (string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        // Current Codex can surface a rollback-maintenance error that does
                        // not affect turn status. Do not turn that housekeeping event into
                        // a false user-facing ERROR.
                        if (JsonUtil.HasNonFatalErrorCode(payload)) return;

                        tracker.LastErrorUtc = ts;
                        if (JsonUtil.HasUsageLimitCode(payload))
                        {
                            tracker.LastLimitUtc = ts;
                            tracker.Terminal = TerminalKind.LimitReached;
                            tracker.TerminalReason = "Codex reported a usage limit";
                        }
                        else
                        {
                            tracker.Terminal = TerminalKind.Error;
                            tracker.TerminalReason = "Codex reported a turn error";
                        }
                        tracker.TurnOpen = false;
                        tracker.WaitingForUser = false;
                        tracker.ActiveToolCount = 0;
                tracker.ActiveToolIds.Clear();
                        tracker.LastTerminalUtc = ts;
                        return;
                    }

                    if (IsTurnCompleteKind(kind))
                    {
                        bool nullReply = JsonUtil.HasNull(payload, "last_agent_message");
                        Dictionary<string, object> completeError = JsonUtil.Dict(payload, "error");
                        tracker.TurnOpen = false;
                        tracker.WaitingForUser = false;
                        tracker.ActiveToolCount = 0;
                        tracker.ActiveToolIds.Clear();
                        tracker.LastTerminalUtc = ts;
                        if (completeError != null && JsonUtil.HasUsageLimitCode(completeError))
                        {
                            tracker.LastLimitUtc = ts;
                            tracker.Terminal = TerminalKind.LimitReached;
                            tracker.TerminalReason = "Codex reported a usage limit";
                        }
                        else if (completeError != null)
                        {
                            tracker.LastErrorUtc = ts;
                            tracker.Terminal = TerminalKind.Error;
                            tracker.TerminalReason = "Codex ended after an error";
                        }
                        else if (tracker.LastLimitUtc >= tracker.TaskStartedUtc && tracker.LastLimitUtc != DateTime.MinValue)
                        {
                            tracker.Terminal = TerminalKind.LimitReached;
                            tracker.TerminalReason = "Codex reported a usage limit";
                        }
                        else if (tracker.LastErrorUtc >= tracker.TaskStartedUtc && tracker.LastErrorUtc != DateTime.MinValue)
                        {
                            tracker.Terminal = TerminalKind.Error;
                            tracker.TerminalReason = "Codex ended after an error";
                        }
                        else if (nullReply && !tracker.AgentReplySeenSinceTaskStart)
                        {
                            tracker.Terminal = TerminalKind.Error;
                            tracker.TerminalReason = "Codex ended without a final reply";
                        }
                        else
                        {
                            tracker.Terminal = TerminalKind.Done;
                            tracker.TerminalReason = "The Codex turn completed";
                        }
                        return;
                    }

                    if (string.Equals(kind, "token_count", StringComparison.OrdinalIgnoreCase))
                    {
                        long totalTokens;
                        if (JsonUtil.TryFindLongRecursive(payload, "total_tokens", 5, out totalTokens))
                        {
                            if (tracker.LastTokenTotal < 0)
                            {
                                tracker.LastTokenTotal = totalTokens;
                            }
                            else if (totalTokens > tracker.LastTokenTotal && EnsureObservedTurnOpen(tracker, ts))
                            {
                                tracker.LastTokenTotal = totalTokens;
                                tracker.WaitingForUser = false;
                                MarkMeaningful(tracker, ts);
                            }
                        }
                        return;
                    }

                    if (string.Equals(kind, "stream_error", StringComparison.OrdinalIgnoreCase))
                    {
                        if (tracker.TurnOpen)
                        {
                            tracker.LastStreamErrorUtc = ts;
                            tracker.StreamErrorsSinceProgress++;
                        }
                        return;
                    }

                    if (string.Equals(kind, "background_event", StringComparison.OrdinalIgnoreCase))
                    {
                        string message = JsonUtil.String(payload, "message") ?? string.Empty;
                        string lowerMessage = message.ToLowerInvariant();
                        bool streamRetry = lowerMessage.IndexOf("stream error") >= 0 &&
                            (lowerMessage.IndexOf("retry") >= 0 || lowerMessage.IndexOf("disconnect") >= 0 ||
                             lowerMessage.IndexOf("connection") >= 0);
                        if (streamRetry && EnsureObservedTurnOpen(tracker, ts))
                        {
                            tracker.LastStreamErrorUtc = ts;
                            tracker.StreamErrorsSinceProgress++;
                        }
                        return;
                    }

                    UpdateToolActivity(tracker, kind, payload);
                    if (IsMeaningfulEventKind(kind))
                    {
                        if (EnsureObservedTurnOpen(tracker, ts))
                        {
                            tracker.WaitingForUser = false;
                            MarkMeaningful(tracker, ts);
                            if (string.Equals(kind, "agent_message", StringComparison.OrdinalIgnoreCase))
                            {
                                tracker.AgentReplySeenSinceTaskStart = true;
                                tracker.LastAssistantReplyUtc = ts;
                            }
                        }
                        return;
                    }
                }

                if (string.Equals(top, "response_item", StringComparison.OrdinalIgnoreCase))
                {
                    string itemType = JsonUtil.String(payload, "type");
                    string role = JsonUtil.String(payload, "role");
                    bool isContextMessage = string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase));
                    bool meaningfulResponseItem = IsMeaningfulResponseItem(itemType, role);
                    if (!isContextMessage && meaningfulResponseItem && EnsureObservedTurnOpen(tracker, ts))
                    {
                        tracker.WaitingForUser = false;
                        MarkMeaningful(tracker, ts);
                        if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            tracker.AgentReplySeenSinceTaskStart = true;
                            tracker.LastAssistantReplyUtc = ts;
                        }
                    }
                }
            }
        }

        private static bool IsMeaningfulResponseItem(string itemType, string role)
        {
            string item = (itemType ?? string.Empty).ToLowerInvariant();
            if (item == "message") return string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
            if (item == "reasoning" || item == "reasoning_content" || item == "reasoning_summary") return true;
            return item.IndexOf("function_call", StringComparison.Ordinal) >= 0 ||
                item.IndexOf("custom_tool_call", StringComparison.Ordinal) >= 0 ||
                item.IndexOf("tool_call", StringComparison.Ordinal) >= 0 ||
                item.IndexOf("tool_result", StringComparison.Ordinal) >= 0 ||
                item.IndexOf("function_output", StringComparison.Ordinal) >= 0 ||
                item.IndexOf("command_execution", StringComparison.Ordinal) >= 0 ||
                item.IndexOf("web_search", StringComparison.Ordinal) >= 0;
        }

        private static void ProcessBackgroundHintObject(SessionTracker tracker, string top, string kind,
            Dictionary<string, object> payload, DateTime ts)
        {
            if (tracker == null || payload == null) return;
            string lowerTop = (top ?? string.Empty).ToLowerInvariant();
            string lowerKind = (kind ?? JsonUtil.String(payload, "type") ?? string.Empty).ToLowerInvariant();

            // Only tool/function traffic can create a trusted detached-process hint.
            // Assistant/user prose containing "Start-Process" must never be enough.
            bool toolTraffic = lowerTop == "event_msg" &&
                (lowerKind.IndexOf("exec_") == 0 || lowerKind.IndexOf("command_") == 0 ||
                 lowerKind.IndexOf("tool_") == 0 || lowerKind.IndexOf("mcp_") == 0);
            string itemType = lowerTop == "response_item" ? (JsonUtil.String(payload, "type") ?? string.Empty).ToLowerInvariant() : string.Empty;
            if (lowerTop == "response_item")
            {
                toolTraffic = itemType.IndexOf("function_call") >= 0 || itemType.IndexOf("custom_tool_call") >= 0 ||
                    itemType.IndexOf("tool_call") >= 0;
            }
            if (!toolTraffic) return;

            List<string> strings = new List<string>();
            CollectTrustedCommandStrings(payload, strings);
            string callId = JsonUtil.String(payload, "call_id");
            if (string.IsNullOrEmpty(callId)) callId = JsonUtil.String(payload, "id");
            if (string.IsNullOrEmpty(callId)) callId = JsonUtil.String(payload, "item_id");

            bool outputLike = lowerKind.EndsWith("_end") || lowerKind.EndsWith("_completed") ||
                lowerKind.EndsWith("_response") || lowerKind.IndexOf("output") >= 0 ||
                itemType.IndexOf("output") >= 0;

            bool launch = false;
            int i;
            if (!outputLike)
            {
                for (i = 0; i < strings.Count; i++)
                {
                    string text = strings[i] ?? string.Empty;
                    string lower = text.ToLowerInvariant();
                    if (lower.IndexOf("start-process") >= 0 &&
                        (lower.IndexOf("-passthru") >= 0 || lower.IndexOf(".id") >= 0)) launch = true;

                    Match wait = WaitProcessPidRegex.Match(text);
                    while (wait.Success)
                    {
                        int pid;
                        if (int.TryParse(wait.Groups["pid"].Value, out pid))
                            AddBackgroundHint(tracker, pid, DateTime.MinValue, ts, "Wait-Process");
                        wait = wait.NextMatch();
                    }

                    Match rootMatch = RunRootRegex.Match(text);
                    while (rootMatch.Success)
                    {
                        string runRoot = rootMatch.Groups["path"].Value;
                        if (!string.IsNullOrEmpty(runRoot))
                        {
                            lock (tracker.Sync) tracker.BackgroundRoots.Add(runRoot);
                        }
                        rootMatch = rootMatch.NextMatch();
                    }
                }
            }

            lock (tracker.Sync)
            {
                if (launch)
                {
                    tracker.PendingBackgroundLaunchUtc = ts;
                    if (!string.IsNullOrEmpty(callId))
                    {
                        tracker.PendingBackgroundLaunchTimes[callId] = ts;
                    }
                }

                DateTime pendingLaunchUtc = DateTime.MinValue;
                bool pending = !string.IsNullOrEmpty(callId) &&
                    tracker.PendingBackgroundLaunchTimes.TryGetValue(callId, out pendingLaunchUtc) &&
                    ts >= pendingLaunchUtc && ts - pendingLaunchUtc <= TimeSpan.FromMinutes(3);
                if (pending && outputLike)
                {
                    int pid = ExtractPidFromToolOutput(payload);
                    if (pid > 4)
                    {
                        DateTime launchUtc = pendingLaunchUtc;
                        AddBackgroundHintLocked(tracker, pid, launchUtc, ts, "Start-Process");
                    }
                    tracker.PendingBackgroundLaunchTimes.Remove(callId);
                }

                List<string> expiredCalls = new List<string>();
                foreach (KeyValuePair<string, DateTime> pendingPair in tracker.PendingBackgroundLaunchTimes)
                {
                    if (ts - pendingPair.Value > TimeSpan.FromMinutes(3)) expiredCalls.Add(pendingPair.Key);
                }
                foreach (string expiredCall in expiredCalls)
                {
                    tracker.PendingBackgroundLaunchTimes.Remove(expiredCall);
                }
                if (tracker.PendingBackgroundLaunchTimes.Count == 0) tracker.PendingBackgroundLaunchUtc = DateTime.MinValue;
            }
        }

        private static int ExtractPidFromToolOutput(Dictionary<string, object> payload)
        {
            if (payload == null) return -1;
            string[] outputFields = new string[] { "output", "result", "stdout" };
            for (int i = 0; i < outputFields.Length; i++)
            {
                object raw;
                if (!payload.TryGetValue(outputFields[i], out raw) || raw == null) continue;
                int pid = ExtractPidFromOutputValue(raw);
                if (pid > 4) return pid;
            }
            return -1;
        }

        private static void CollectTrustedCommandStrings(Dictionary<string, object> payload, List<string> output)
        {
            if (payload == null || output == null) return;
            string[] commandFields = new string[] { "arguments", "command", "command_line", "script", "input" };
            for (int i = 0; i < commandFields.Length; i++)
            {
                object raw;
                if (!payload.TryGetValue(commandFields[i], out raw) || raw == null) continue;
                string text = raw as string;
                if (!string.IsNullOrWhiteSpace(text)) output.Add(text);
            }
        }

        private static int ExtractPidFromOutputValue(object raw)
        {
            Dictionary<string, object> outputObject = raw as Dictionary<string, object>;
            if (outputObject != null)
            {
                long directPid;
                if (JsonUtil.TryGetLong(outputObject, "pid", out directPid) && directPid > 4 && directPid <= int.MaxValue)
                    return (int)directPid;
                if (JsonUtil.TryGetLong(outputObject, "process_id", out directPid) && directPid > 4 && directPid <= int.MaxValue)
                    return (int)directPid;
                if (JsonUtil.TryGetLong(outputObject, "processId", out directPid) && directPid > 4 && directPid <= int.MaxValue)
                    return (int)directPid;
                return -1;
            }

            string text = raw as string;
            if (string.IsNullOrWhiteSpace(text)) return -1;
            Dictionary<string, object> parsed = JsonUtil.ParseObject(text);
            if (parsed != null)
            {
                // A parsed output object is authoritative only at its top level.
                // Do not accept a PID buried in unrelated metadata or wrapper data.
                long directPid;
                if (JsonUtil.TryGetLong(parsed, "pid", out directPid) && directPid > 4 && directPid <= int.MaxValue)
                    return (int)directPid;
                if (JsonUtil.TryGetLong(parsed, "process_id", out directPid) && directPid > 4 && directPid <= int.MaxValue)
                    return (int)directPid;
                if (JsonUtil.TryGetLong(parsed, "processId", out directPid) && directPid > 4 && directPid <= int.MaxValue)
                    return (int)directPid;
                return -1;
            }

            Match json = JsonPidRegex.Match(text);
            if (json.Success)
            {
                int pid;
                if (int.TryParse(json.Groups["pid"].Value, out pid)) return pid;
            }
            Match line = LinePidRegex.Match(text);
            if (line.Success)
            {
                int pid;
                if (int.TryParse(line.Groups["pid"].Value, out pid)) return pid;
            }
            return -1;
        }

        private static void AddBackgroundHint(SessionTracker tracker, int pid, DateTime launchUtc, DateTime observedUtc, string source)
        {
            AddBackgroundHint(tracker, pid, launchUtc, observedUtc, source, null, null);
        }

        private static void AddBackgroundHint(SessionTracker tracker, int pid, DateTime launchUtc, DateTime observedUtc, string source,
            string stdoutPath, string stderrPath)
        {
            if (tracker == null || pid <= 4) return;
            lock (tracker.Sync) AddBackgroundHintLocked(tracker, pid, launchUtc, observedUtc, source, stdoutPath, stderrPath);
        }

        private static void AddBackgroundHintLocked(SessionTracker tracker, int pid, DateTime launchUtc, DateTime observedUtc, string source)
        {
            AddBackgroundHintLocked(tracker, pid, launchUtc, observedUtc, source, null, null);
        }

        private static void AddBackgroundHintLocked(SessionTracker tracker, int pid, DateTime launchUtc, DateTime observedUtc, string source,
            string stdoutPath, string stderrPath)
        {
            BackgroundProcessHint existing;
            if (!tracker.BackgroundProcesses.TryGetValue(pid, out existing))
            {
                existing = new BackgroundProcessHint();
                existing.Pid = pid;
                tracker.BackgroundProcesses[pid] = existing;
            }
            bool newer = existing.ObservedUtc == DateTime.MinValue || observedUtc >= existing.ObservedUtc;
            if (newer)
            {
                if (launchUtc != DateTime.MinValue) existing.LaunchUtc = launchUtc;
                if (observedUtc > existing.ObservedUtc) existing.ObservedUtc = observedUtc;
                if (!string.IsNullOrWhiteSpace(stdoutPath)) existing.StdoutPath = stdoutPath;
                if (!string.IsNullOrWhiteSpace(stderrPath)) existing.StderrPath = stderrPath;
                existing.Source = source;
            }
            else
            {
                if (existing.LaunchUtc == DateTime.MinValue && launchUtc != DateTime.MinValue) existing.LaunchUtc = launchUtc;
                if (string.IsNullOrWhiteSpace(existing.StdoutPath)) existing.StdoutPath = stdoutPath;
                if (string.IsNullOrWhiteSpace(existing.StderrPath)) existing.StderrPath = stderrPath;
            }
        }

        private void RecoverBackgroundHintsFromTail(SessionTracker tracker)
        {
            if (tracker == null || string.IsNullOrEmpty(tracker.Path)) return;
            lock (tracker.Sync)
            {
                if (tracker.BackgroundHintScanUtc != DateTime.MinValue) return;
                tracker.BackgroundHintScanUtc = DateTime.UtcNow;
            }
            try
            {
                FileInfo info = new FileInfo(tracker.Path);
                if (!info.Exists || info.Length <= 0) return;
                long start = Math.Max(0, info.Length - BackgroundHintRecoveryBytes);
                int count = (int)Math.Min((long)BackgroundHintRecoveryBytes, info.Length - start);
                byte[] bytes = new byte[count];
                using (FileStream stream = new FileStream(tracker.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(start, SeekOrigin.Begin);
                    int read = 0;
                    while (read < count)
                    {
                        int n = stream.Read(bytes, read, count - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < count) Array.Resize(ref bytes, read);
                }
                int firstNewline = start == 0 ? 0 : IndexOf(bytes, (byte)'\n', 0);
                int lineStart = start == 0 ? 0 : (firstNewline < 0 ? bytes.Length : firstNewline + 1);
                int pos = lineStart;
                while (pos < bytes.Length)
                {
                    int nl = IndexOf(bytes, (byte)'\n', pos);
                    if (nl < 0) break;
                    int length = nl - pos;
                    if (length > 0 && length <= MaxRecordBytes)
                    {
                        string line = Encoding.UTF8.GetString(bytes, pos, length).TrimEnd('\r');
                        Dictionary<string, object> obj = JsonUtil.ParseObject(line);
                        if (obj != null)
                        {
                            string top = JsonUtil.String(obj, "type");
                            Dictionary<string, object> payload = JsonUtil.Dict(obj, "payload");
                            string kind = JsonUtil.String(payload, "type");
                            DateTime ts = JsonUtil.TimestampUtc(obj, info.LastWriteTimeUtc);
                            ProcessBackgroundHintObject(tracker, top, kind, payload, ts);
                        }
                    }
                    pos = nl + 1;
                }
            }
            catch { }
        }

        private void RecoverBackgroundHintsFromReceipts(List<SessionTracker> members, DateTime nowUtc)
        {
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionTracker member in members)
            {
                lock (member.Sync)
                {
                    if (member.BackgroundReceiptScanUtc != DateTime.MinValue &&
                        nowUtc - member.BackgroundReceiptScanUtc < TimeSpan.FromSeconds(30)) continue;
                    member.BackgroundReceiptScanUtc = nowUtc;
                    foreach (string root in member.BackgroundRoots) roots.Add(root);
                }
            }
            int rootCount = 0;
            foreach (string root in roots)
            {
                if (rootCount++ >= 8) break;
                TryRecoverPidReceipts(root, members, nowUtc);
            }
        }

        private static void TryRecoverPidReceipts(string runRoot, List<SessionTracker> members, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(runRoot)) return;
            string stateDir;
            try { stateDir = Path.Combine(runRoot, "00_STATE"); }
            catch { return; }
            if (!Directory.Exists(stateDir)) return;
            try
            {
                List<FileInfo> files = new DirectoryInfo(stateDir).GetFiles("*PID*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => string.Equals(f.Extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(f.Extension, ".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(24).ToList();
                foreach (FileInfo file in files)
                {
                    string upperName = file.Name.ToUpperInvariant();
                    if (upperName.IndexOf("HEARTBEAT") >= 0 || upperName.IndexOf("WATCHER") >= 0 ||
                        upperName.IndexOf("MONITOR") >= 0 || upperName.IndexOf("DONE") >= 0 ||
                        upperName.IndexOf("COMPLETE") >= 0 || upperName.IndexOf("FAILED") >= 0 ||
                        upperName.IndexOf("ERROR") >= 0 || upperName.IndexOf("EXIT") >= 0) continue;
                    if (nowUtc - file.LastWriteTimeUtc > TimeSpan.FromDays(7) || file.Length <= 0 || file.Length > 256 * 1024) continue;
                    string text;
                    try { text = File.ReadAllText(file.FullName); }
                    catch { continue; }
                    int pid = -1;
                    DateTime launchUtc = DateTime.MinValue;
                    DateTime processStartUtc = DateTime.MinValue;
                    string executablePath = null;
                    string stdoutPath = null;
                    string stderrPath = null;
                    if (string.Equals(file.Extension, ".json", StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> obj = JsonUtil.ParseObject(text);
                        long value;
                        if (obj != null)
                        {
                            string lifecycle = JsonUtil.String(obj, "state");
                            if (string.IsNullOrEmpty(lifecycle)) lifecycle = JsonUtil.String(obj, "status");
                            if (!string.IsNullOrEmpty(lifecycle) &&
                                lifecycle.ToLowerInvariant() != "running" &&
                                lifecycle.ToLowerInvariant() != "active" &&
                                lifecycle.ToLowerInvariant() != "started" &&
                                lifecycle.ToLowerInvariant() != "in_progress") continue;
                            long exitCode;
                            if (JsonUtil.TryGetLong(obj, "exit_code", out exitCode) ||
                                JsonUtil.TryGetLong(obj, "return_code", out exitCode)) continue;
                            if (JsonUtil.TryGetLong(obj, "pid", out value) ||
                                JsonUtil.TryGetLong(obj, "process_id", out value) ||
                                JsonUtil.TryGetLong(obj, "processId", out value) ||
                                JsonUtil.TryGetLong(obj, "monitored_pid", out value))
                            {
                                if (value > 4 && value <= int.MaxValue) pid = (int)value;
                            }
                            string started = JsonUtil.String(obj, "started_at");
                            if (string.IsNullOrEmpty(started)) started = JsonUtil.String(obj, "started");
                            if (!string.IsNullOrEmpty(started)) launchUtc = JsonUtil.ParseUtc(started, DateTime.MinValue);
                            string processStarted = JsonUtil.String(obj, "process_start_time");
                            if (string.IsNullOrEmpty(processStarted)) processStarted = JsonUtil.String(obj, "creation_time");
                            if (!string.IsNullOrEmpty(processStarted)) processStartUtc = JsonUtil.ParseUtc(processStarted, DateTime.MinValue);
                            executablePath = JsonUtil.String(obj, "executable");
                            if (string.IsNullOrEmpty(executablePath)) executablePath = JsonUtil.String(obj, "executable_path");
                            if (string.IsNullOrEmpty(executablePath)) executablePath = JsonUtil.String(obj, "exe");
                            stdoutPath = JsonUtil.String(obj, "stdout");
                            if (string.IsNullOrEmpty(stdoutPath)) stdoutPath = JsonUtil.String(obj, "stdout_path");
                            stderrPath = JsonUtil.String(obj, "stderr");
                            if (string.IsNullOrEmpty(stderrPath)) stderrPath = JsonUtil.String(obj, "stderr_path");
                        }
                    }
                    else
                    {
                        int parsed;
                        if (int.TryParse(text.Trim(), out parsed) && parsed > 4)
                        {
                            pid = parsed;
                            // A bare *_PID.txt receipt has no embedded identity. Its
                            // write time is the only trustworthy launch bound; using it
                            // prevents a stale numeric PID from being accepted after reuse.
                            launchUtc = file.LastWriteTimeUtc;
                        }
                    }
                    if (pid <= 4) continue;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(stdoutPath))
                        {
                            stdoutPath = Path.IsPathRooted(stdoutPath)
                                ? Path.GetFullPath(stdoutPath)
                                : Path.GetFullPath(Path.Combine(runRoot, stdoutPath));
                            if (!IsPathWithinRoot(runRoot, stdoutPath)) stdoutPath = null;
                        }
                        if (!string.IsNullOrWhiteSpace(stderrPath))
                        {
                            stderrPath = Path.IsPathRooted(stderrPath)
                                ? Path.GetFullPath(stderrPath)
                                : Path.GetFullPath(Path.Combine(runRoot, stderrPath));
                            if (!IsPathWithinRoot(runRoot, stderrPath)) stderrPath = null;
                        }
                    }
                    catch { }
                    foreach (SessionTracker member in members)
                    {
                        bool ownsRoot;
                        lock (member.Sync) ownsRoot = member.BackgroundRoots.Contains(runRoot);
                        if (ownsRoot)
                        {
                            AddBackgroundHint(member, pid, launchUtc, file.LastWriteTimeUtc, "PID receipt", stdoutPath, stderrPath);
                            lock (member.Sync)
                            {
                                BackgroundProcessHint hint;
                                if (member.BackgroundProcesses.TryGetValue(pid, out hint))
                                {
                                    if (processStartUtc != DateTime.MinValue) hint.ProcessStartUtc = processStartUtc;
                                    if (!string.IsNullOrWhiteSpace(executablePath)) hint.ExecutablePath = executablePath;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static bool IsPathWithinRoot(string root, string path)
        {
            try
            {
                string cleanRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string cleanPath = Path.GetFullPath(path);
                return cleanPath.StartsWith(cleanRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void RemoveBackgroundHints(List<SessionTracker> members, List<BackgroundProcessHint> hints)
        {
            if (members == null || hints == null || hints.Count == 0) return;
            HashSet<int> pids = new HashSet<int>();
            int i;
            for (i = 0; i < hints.Count; i++) if (hints[i] != null) pids.Add(hints[i].Pid);
            foreach (SessionTracker member in members)
            {
                lock (member.Sync)
                {
                    foreach (int pid in pids) member.BackgroundProcesses.Remove(pid);
                }
            }
        }

        private static List<BackgroundProcessHint> CollectBackgroundHints(List<SessionTracker> members, DateTime nowUtc)
        {
            Dictionary<int, BackgroundProcessHint> merged = new Dictionary<int, BackgroundProcessHint>();
            foreach (SessionTracker member in members)
            {
                lock (member.Sync)
                {
                    if (member.PendingBackgroundLaunchTimes.Count > 0)
                    {
                        List<string> expiredLaunches = null;
                        foreach (KeyValuePair<string, DateTime> pending in member.PendingBackgroundLaunchTimes)
                        {
                            if (pending.Value != DateTime.MinValue &&
                                nowUtc - pending.Value > TimeSpan.FromMinutes(3))
                            {
                                if (expiredLaunches == null) expiredLaunches = new List<string>();
                                expiredLaunches.Add(pending.Key);
                            }
                        }
                        if (expiredLaunches != null)
                        {
                            for (int i = 0; i < expiredLaunches.Count; i++)
                                member.PendingBackgroundLaunchTimes.Remove(expiredLaunches[i]);
                        }
                        if (member.PendingBackgroundLaunchTimes.Count == 0)
                            member.PendingBackgroundLaunchUtc = DateTime.MinValue;
                    }

                    List<int> expired = null;
                    foreach (KeyValuePair<int, BackgroundProcessHint> pair in member.BackgroundProcesses)
                    {
                        BackgroundProcessHint hint = pair.Value;
                        if (hint == null || (hint.ObservedUtc != DateTime.MinValue && nowUtc - hint.ObservedUtc > TimeSpan.FromDays(7)))
                        {
                            if (expired == null) expired = new List<int>();
                            expired.Add(pair.Key);
                            continue;
                        }
                        BackgroundProcessHint old;
                        if (!merged.TryGetValue(pair.Key, out old))
                        {
                            merged[pair.Key] = hint.Clone();
                        }
                        else
                        {
                            BackgroundProcessHint newer = hint.ObservedUtc > old.ObservedUtc ? hint.Clone() : old;
                            BackgroundProcessHint older = object.ReferenceEquals(newer, old) ? hint : old;
                            if (newer.LaunchUtc == DateTime.MinValue) newer.LaunchUtc = older.LaunchUtc;
                            if (newer.ProcessStartUtc == DateTime.MinValue) newer.ProcessStartUtc = older.ProcessStartUtc;
                            if (string.IsNullOrWhiteSpace(newer.ExecutablePath)) newer.ExecutablePath = older.ExecutablePath;
                            if (string.IsNullOrWhiteSpace(newer.StdoutPath)) newer.StdoutPath = older.StdoutPath;
                            if (string.IsNullOrWhiteSpace(newer.StderrPath)) newer.StderrPath = older.StderrPath;
                            merged[pair.Key] = newer;
                        }
                    }
                    if (expired != null)
                    {
                        int i;
                        for (i = 0; i < expired.Count; i++) member.BackgroundProcesses.Remove(expired[i]);
                    }
                }
            }
            return merged.Values.ToList();
        }

        private static bool IsChronologicallyAdmissible(SessionTracker tracker, bool startsTurn,
            string eventTurnId, DateTime ts)
        {
            if (!startsTurn)
            {
                if (!string.IsNullOrEmpty(tracker.CurrentTurnId) &&
                    !string.IsNullOrEmpty(eventTurnId) &&
                    !string.Equals(tracker.CurrentTurnId, eventTurnId, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Once a terminal boundary is known, records from before that
                // boundary are replayed history, never a new progress signal.
                if (tracker.LastTerminalUtc != DateTime.MinValue && ts < tracker.LastTerminalUtc)
                    return false;
                if (tracker.TaskStartedUtc != DateTime.MinValue && ts < tracker.TaskStartedUtc)
                    return false;
                return true;
            }

            DateTime latest = tracker.LastAnyUtc;
            if (tracker.TaskStartedUtc > latest) latest = tracker.TaskStartedUtc;
            if (tracker.LastMeaningfulUtc > latest) latest = tracker.LastMeaningfulUtc;
            if (tracker.LastTerminalUtc > latest) latest = tracker.LastTerminalUtc;

            if (!string.IsNullOrEmpty(tracker.CurrentTurnId))
            {
                // Duplicate starts for the same turn must not reset its state.
                if (!string.IsNullOrEmpty(eventTurnId) &&
                    string.Equals(tracker.CurrentTurnId, eventTurnId, StringComparison.OrdinalIgnoreCase))
                    return false;
                // A different turn must be strictly newer than the chronology we
                // have already accepted. This rejects out-of-order old starts.
                return ts > latest;
            }

            if (tracker.Terminal != TerminalKind.None && tracker.LastTerminalUtc != DateTime.MinValue)
                return ts > tracker.LastTerminalUtc;
            return true;
        }

        private static bool EnsureObservedTurnOpen(SessionTracker tracker, DateTime ts)
        {
            if (tracker.TurnOpen) return true;

            // Child/fork rollouts may contain copied parent history, sometimes with
            // timestamps rewritten to the child creation time. Do not let replayed
            // output implicitly create a live child turn. A child/fork becomes live
            // only after its own explicit task_started/turn_started event is observed.
            SessionMeta meta = tracker.Meta;
            if (meta != null && meta.HasInheritedHistoryRisk && !tracker.ExplicitTurnStartSeen &&
                !tracker.AllowImplicitOpenAfterBaseline)
                return false;

            // A terminal event is authoritative. Late token/stat/output noise must not
            // silently reopen the same turn. A new turn reopens only via an explicit
            // task_started/turn_started lifecycle event.
            if (tracker.Terminal != TerminalKind.None && tracker.LastTerminalUtc != DateTime.MinValue &&
                ts >= tracker.LastTerminalUtc)
                return false;

            tracker.TurnOpen = true;
            tracker.TaskStartedUtc = ts;
            tracker.Terminal = TerminalKind.None;
            tracker.TerminalReason = null;
            tracker.AgentReplySeenSinceTaskStart = false;
            return true;
        }

        private static void MarkMeaningful(SessionTracker tracker, DateTime ts)
        {
            if (ts > tracker.LastMeaningfulUtc) tracker.LastMeaningfulUtc = ts;
            tracker.LastDeadlineWakeUtc = DateTime.MinValue;
            tracker.LastStreamErrorUtc = DateTime.MinValue;
            tracker.StreamErrorsSinceProgress = 0;
        }

        private static bool IsTurnStartedKind(string kind)
        {
            return string.Equals(kind, "task_started", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "turn_started", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTurnCompleteKind(string kind)
        {
            return string.Equals(kind, "task_complete", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "turn_complete", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWaitingKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            string k = kind.ToLowerInvariant();
            return k == "request_user_input" ||
                   k == "exec_approval_request" ||
                   k == "apply_patch_approval_request" ||
                   k == "request_permissions" ||
                   k == "elicitation_request" ||
                   k == "mcp_server_elicitation_request" ||
                   k == "approval_requested";
        }

        private static bool IsUsageLimit(string kind, Dictionary<string, object> payload)
        {
            if (string.Equals(kind, "usage_limit_exceeded", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase) && JsonUtil.HasUsageLimitCode(payload)) return true;
            return false;
        }

        private static void UpdateToolActivity(SessionTracker tracker, string kind, Dictionary<string, object> payload)
        {
            if (string.IsNullOrEmpty(kind)) return;
            string k = kind.ToLowerInvariant();

            bool toolKind = k.IndexOf("exec_") == 0 ||
                            k.IndexOf("mcp_") == 0 ||
                            k.IndexOf("tool_") == 0 ||
                            k.IndexOf("patch_") == 0 ||
                            k.IndexOf("dynamic_tool_call_") == 0 ||
                            k.IndexOf("web_search") >= 0 ||
                            k.IndexOf("command_") >= 0;
            if (!toolKind) return;

            string callId = JsonUtil.String(payload, "call_id");
            if (string.IsNullOrEmpty(callId)) callId = JsonUtil.String(payload, "item_id");
            if (string.IsNullOrEmpty(callId)) callId = JsonUtil.String(payload, "id");

            bool begin = k.EndsWith("_begin") || k.EndsWith("_started") || k.EndsWith("_request");
            bool end = k.EndsWith("_end") || k.EndsWith("_completed") || k.EndsWith("_failed") ||
                       k.EndsWith("_response");

            if (begin)
            {
                if (!string.IsNullOrEmpty(callId))
                {
                    if (tracker.ActiveToolIds.Add(callId)) tracker.ActiveToolCount++;
                }
                else
                {
                    tracker.ActiveToolCount++;
                }
            }
            else if (end)
            {
                if (!string.IsNullOrEmpty(callId))
                {
                    if (tracker.ActiveToolIds.Remove(callId) && tracker.ActiveToolCount > 0)
                        tracker.ActiveToolCount--;
                }
                else if (tracker.ActiveToolCount > 0)
                {
                    tracker.ActiveToolCount--;
                }
            }
        }

        private static bool IsMeaningfulEventKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            string k = kind.ToLowerInvariant();
            if (k == "user_message" || k == "task_complete" || k == "turn_complete" ||
                k == "turn_aborted" || k == "error" || k == "stream_error" || k == "warning") return false;

            if (k == "agent_message" || k == "agent_message_content_delta" ||
                k == "reasoning" || k == "reasoning_content_delta" ||
                k == "reasoning_raw_content_delta" || k == "agent_reasoning_section_break" ||
                k == "plan_delta" || k == "task_started" || k == "turn_started" ||
                k == "item_started" || k == "item_completed" ||
                k == "sub_agent_activity" || k == "dynamic_tool_call_request" ||
                k == "dynamic_tool_call_response" || k == "context_compacted") return true;

            if (k.IndexOf("exec_") == 0 || k.IndexOf("mcp_") == 0 || k.IndexOf("tool_") == 0 ||
                k.IndexOf("patch_") == 0 || k.IndexOf("web_search") >= 0 || k.IndexOf("command_") >= 0 ||
                k.IndexOf("collab_agent_") == 0 || k.IndexOf("collab_close_") == 0 ||
                k.IndexOf("collab_resume_") == 0 || k.IndexOf("collab_waiting_") == 0)
                return true;
            return false;
        }

        private void RecomputeAndPublish(bool force)
        {
            StatusSnapshot next = ComputeSnapshot();
            Action<StatusSnapshot> handler = null;
            List<GroupStatusSnapshot> notifications = new List<GroupStatusSnapshot>();
            bool notify;
            lock (_sync)
            {
                StatusSnapshot previous = _current;
                if (previous != null && previous.PrimaryState == next.PrimaryState &&
                    string.Equals(previous.Project, next.Project, StringComparison.Ordinal) &&
                    string.Equals(previous.PrimaryGroupId, next.PrimaryGroupId, StringComparison.Ordinal) &&
                    previous.StateSinceUtc != DateTime.MinValue)
                {
                    GroupStatusSnapshot previousGroup = previous.Groups == null ? null :
                        previous.Groups.FirstOrDefault(g => g != null &&
                            string.Equals(g.GroupId, previous.PrimaryGroupId, StringComparison.OrdinalIgnoreCase));
                    GroupStatusSnapshot nextGroup = next.Groups == null ? null :
                        next.Groups.FirstOrDefault(g => g != null &&
                            string.Equals(g.GroupId, next.PrimaryGroupId, StringComparison.OrdinalIgnoreCase));
                    bool lifecycleChanged = previousGroup != null && nextGroup != null &&
                        (previousGroup.StateEventUtc != nextGroup.StateEventUtc ||
                         previousGroup.EffectiveCompletionUtc != nextGroup.EffectiveCompletionUtc);
                    if (!lifecycleChanged) next.StateSinceUtc = previous.StateSinceUtc;
                }
                notify = force || !EquivalentPublic(previous, next);
                _current = next;
                if (notify) handler = SnapshotChanged;

                if (_notificationBaselineEstablished && previous != null && !force)
                {
                    Dictionary<string, GroupStatusSnapshot> oldGroups = new Dictionary<string, GroupStatusSnapshot>(StringComparer.OrdinalIgnoreCase);
                    if (previous.Groups != null)
                    {
                        foreach (GroupStatusSnapshot group in previous.Groups)
                            if (group != null && !string.IsNullOrEmpty(group.GroupId))
                                oldGroups[CanonicalGroupKeyLocked(group.GroupId)] = group;
                    }
                    if (next.Groups != null)
                    {
                        foreach (GroupStatusSnapshot group in next.Groups)
                        {
                            if (group == null || string.IsNullOrEmpty(group.GroupId)) continue;
                            GroupStatusSnapshot old;
                            if (!oldGroups.TryGetValue(CanonicalGroupKeyLocked(group.GroupId), out old) ||
                                old.State != group.State ||
                                old.StateEventUtc != group.StateEventUtc ||
                                old.EffectiveCompletionUtc != group.EffectiveCompletionUtc)
                            {
                                if (IsNotifiableState(group.State)) notifications.Add(group.Clone());
                            }
                        }
                    }
                }
                if (force || !_notificationBaselineEstablished) _notificationBaselineEstablished = true;
            }
            if (notify && handler != null)
            {
                try { handler(next.Clone()); }
                catch { }
            }
            Action<GroupStatusSnapshot> notificationHandler = GroupNotification;
            if (notificationHandler != null)
            {
                foreach (GroupStatusSnapshot group in notifications)
                {
                    try { notificationHandler(group.Clone()); }
                    catch { }
                }
            }
            ScheduleNextDeadline();
        }

        private static bool IsNotifiableState(PublicState state)
        {
            return state == PublicState.WaitingForYou || state == PublicState.Stuck ||
                state == PublicState.Done || state == PublicState.LimitReached ||
                state == PublicState.Error;
        }

        private StatusSnapshot ComputeSnapshot()
        {
            List<SessionTracker> trackers;
            Dictionary<string, SessionTracker> idMap;
            lock (_sync)
            {
                trackers = _byPath.Values.Where(t => t.Meta != null).ToList();
                idMap = new Dictionary<string, SessionTracker>(_byId, StringComparer.OrdinalIgnoreCase);
            }
            Dictionary<string, List<SessionTracker>> groups = new Dictionary<string, List<SessionTracker>>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionTracker tracker in trackers)
            {
                string root = ResolveRootId(tracker, idMap);
                if (string.IsNullOrEmpty(root)) root = tracker.Id ?? tracker.Path;
                List<SessionTracker> list;
                if (!groups.TryGetValue(root, out list))
                {
                    list = new List<SessionTracker>();
                    groups[root] = list;
                }
                list.Add(tracker);
            }

            MigrateLineageHistory(groups);

            int attributableOpenGroups = 0;
            foreach (KeyValuePair<string, List<SessionTracker>> pair in groups)
            {
                if (GroupHasRecentOpenTurn(pair.Value, DateTime.UtcNow, TimeSpan.FromHours(12))) attributableOpenGroups++;
            }

            List<GroupStatusSnapshot> visibleGroups = new List<GroupStatusSnapshot>();
            GroupCandidate selected = null;
            Dictionary<string, DateTime> activityByGroup =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            int activeStatesMask = 0;
            foreach (KeyValuePair<string, List<SessionTracker>> pair in groups.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                GroupCandidate candidate = BuildGroupCandidate(pair.Key, pair.Value, idMap, attributableOpenGroups);
                if (candidate.StaleHistory) continue;
                if (candidate.Snapshot.State != PublicState.Idle)
                {
                    visibleGroups.Add(candidate.Snapshot);
                    activityByGroup[candidate.Snapshot.GroupId] = candidate.ActivityUtc;
                    activeStatesMask |= StatusSnapshot.StateBit(candidate.Snapshot.State);
                    if (selected == null || IsBetterCandidate(candidate, selected)) selected = candidate;
                }
            }
            List<GroupStatusSnapshot> retained = GetRetainedDoneGroups(groups.Keys, DateTime.UtcNow);
            foreach (GroupStatusSnapshot group in retained)
            {
                visibleGroups.Add(group);
                activeStatesMask |= StatusSnapshot.StateBit(group.State);
                GroupCandidate retainedCandidate = new GroupCandidate();
                retainedCandidate.Snapshot = group;
                retainedCandidate.ActivityUtc = GroupActivity(group);
                activityByGroup[group.GroupId] = retainedCandidate.ActivityUtc;
                if (selected == null || IsBetterCandidate(retainedCandidate, selected)) selected = retainedCandidate;
            }
            HashSet<string> activeMemberKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionTracker tracker in trackers)
            {
                lock (tracker.Sync)
                {
                    if (!string.IsNullOrEmpty(tracker.Id)) activeMemberKeys.Add(GroupKey(tracker.Id));
                }
            }
            PruneGroupHistory(groups.Keys, activeMemberKeys);
            if (selected == null) return MakeIdle("No readable Codex activity found");
            if (activeStatesMask == 0) activeStatesMask = StatusSnapshot.StateBit(PublicState.Idle);

            visibleGroups.Sort(delegate(GroupStatusSnapshot a, GroupStatusSnapshot b)
            {
                if (a == null) return 1;
                if (b == null) return -1;
                if (a.GroupId == selected.Snapshot.GroupId) return b.GroupId == selected.Snapshot.GroupId ? 0 : -1;
                if (b.GroupId == selected.Snapshot.GroupId) return 1;
                if (a.State != b.State) return StateTier(b.State).CompareTo(StateTier(a.State));
                DateTime aActivity;
                DateTime bActivity;
                if (!activityByGroup.TryGetValue(a.GroupId, out aActivity)) aActivity = GroupActivity(a);
                if (!activityByGroup.TryGetValue(b.GroupId, out bActivity)) bActivity = GroupActivity(b);
                int activity = bActivity.CompareTo(aActivity);
                if (activity != 0) return activity;
                return string.Compare(a.GroupId, b.GroupId, StringComparison.OrdinalIgnoreCase);
            });

            StatusSnapshot result = new StatusSnapshot();
            result.State = selected.Snapshot.State;
            result.PrimaryGroupId = selected.Snapshot.GroupId;
            result.Project = selected.Snapshot.Project;
            result.LastWorkUtc = selected.Snapshot.LastRealWorkUtc;
            result.StateSinceUtc = selected.Snapshot.StateSinceUtc;
            result.Reason = selected.Snapshot.Reason;
            result.SessionPath = selected.Snapshot.SessionPath;
            result.GroupCount = visibleGroups.Count;
            result.OpenTurnCount = selected.Snapshot.OpenTurnCount;
            result.ProcessAlive = selected.Snapshot.ProcessAlive;
            result.ProcessBusy = selected.Snapshot.ProcessBusy;
            result.BackgroundProcessAlive = selected.Snapshot.BackgroundJobActive;
            result.BackgroundProcessBusy = selected.Snapshot.BackgroundProcessBusy;
            result.BackgroundProcessCount = selected.Snapshot.BackgroundProcessCount;
            result.BackgroundLastProgressUtc = selected.Snapshot.BackgroundLastProgressUtc;
            result.Confidence = selected.Snapshot.Confidence;
            result.ActiveStatesMask = activeStatesMask;
            result.Groups = visibleGroups.Select(g => g.Clone()).ToArray();
            return result;
        }

        private static bool GroupHasRecentOpenTurn(List<SessionTracker> members, DateTime nowUtc, TimeSpan maxAge)
        {
            foreach (SessionTracker member in members)
            {
                lock (member.Sync)
                {
                    if (member.TurnOpen)
                    {
                        // LastAny includes user/context/telemetry records. They can
                        // be fresh while the last real Codex progress is ancient;
                        // that must not make a stale group affect another group's
                        // process-probe/stuck decision.
                        DateTime activity = member.LastMeaningfulUtc;
                        if (activity != DateTime.MinValue && nowUtc - activity <= maxAge) return true;
                    }
                }
            }
            return false;
        }

        private GroupCandidate BuildGroupCandidate(string rootId, List<SessionTracker> members,
            Dictionary<string, SessionTracker> idMap, int activeGroupCount)
        {
            SessionTracker root = null;
            idMap.TryGetValue(rootId, out root);
            SessionTracker displayTracker = root ?? members.OrderByDescending(t => SafeAny(t)).FirstOrDefault();

            DateTime latestMeaningful = DateTime.MinValue;
            DateTime latestAny = DateTime.MinValue;
            int openCount = 0;
            int activeTools = 0;
            bool waiting = false;
            DateTime latestWaiting = DateTime.MinValue;
            int streamErrors = 0;
            DateTime latestStreamError = DateTime.MinValue;
            foreach (SessionTracker member in members)
            {
                lock (member.Sync)
                {
                    if (member.LastMeaningfulUtc > latestMeaningful) latestMeaningful = member.LastMeaningfulUtc;
                    if (member.LastAnyUtc > latestAny) latestAny = member.LastAnyUtc;
                    if (member.TurnOpen) openCount++;
                    if (member.ActiveToolCount > 0) activeTools += member.ActiveToolCount;
                    if (member.TurnOpen && member.WaitingForUser)
                    {
                        waiting = true;
                        if (member.LastWaitingUtc > latestWaiting) latestWaiting = member.LastWaitingUtc;
                    }
                    if (member.TurnOpen && member.StreamErrorsSinceProgress > 0)
                    {
                        streamErrors += member.StreamErrorsSinceProgress;
                        if (member.LastStreamErrorUtc > latestStreamError) latestStreamError = member.LastStreamErrorUtc;
                    }
                }
            }

            string project = ProjectName(displayTracker == null ? null : displayTracker.Cwd);
            string path = displayTracker == null ? members[0].Path : displayTracker.Path;
            DateTime now = DateTime.UtcNow;
            PublicState state;
            string reason;
            string confidence = "high";
            bool processAlive = false;
            bool processBusy = false;

            TerminalKind rootTerminal = TerminalKind.None;
            string rootReason = null;
            DateTime rootTerminalUtc = DateTime.MinValue;
            if (root != null)
            {
                lock (root.Sync)
                {
                    rootTerminal = root.Terminal;
                    rootReason = root.TerminalReason;
                    rootTerminalUtc = root.LastTerminalUtc;
                }
            }

            TimeSpan silence = latestMeaningful == DateTime.MinValue ? TimeSpan.MaxValue : now - latestMeaningful;
            List<BackgroundProcessHint> backgroundHints = CollectBackgroundHints(members, now);
            bool backgroundRecoveryEligible = rootTerminal != TerminalKind.None || openCount > 0 ||
                (latestAny != DateTime.MinValue && now - latestAny <= TimeSpan.FromHours(6));
            if (backgroundRecoveryEligible && (silence >= ProcessProbeAfter || rootTerminal != TerminalKind.None))
            {
                if (backgroundHints.Count == 0)
                {
                    List<SessionTracker> hintSources = members.OrderByDescending(t => SafeAny(t)).Take(3).ToList();
                    if (root != null && !hintSources.Contains(root)) hintSources.Insert(0, root);
                    foreach (SessionTracker hintSource in hintSources) RecoverBackgroundHintsFromTail(hintSource);
                }
                RecoverBackgroundHintsFromReceipts(members, now);
                backgroundHints = CollectBackgroundHints(members, now);
            }

            BackgroundProbeResult backgroundProbe = null;
            bool backgroundAlive = false;
            bool backgroundBusy = false;
            bool backgroundProtectsWork = false;
            bool backgroundLooksStuck = false;
            int backgroundCount = 0;
            DateTime backgroundProgressUtc = DateTime.MinValue;
            GroupLifecycle previousLifecycle = null;
            lock (_sync)
            {
                _groupHistory.TryGetValue(GroupKey(rootId), out previousLifecycle);
            }
            if (backgroundHints.Count > 0)
            {
#if TEST_BUILD
                backgroundProbe = BackgroundProbeOverrideForTests ??
                    _processProbe.SampleBackgroundThrottled(backgroundHints, BackgroundProbeIntervalForTests);
#else
                backgroundProbe = _processProbe.SampleBackgroundThrottled(backgroundHints, TimeSpan.FromSeconds(30));
#endif
                if (backgroundProbe != null && backgroundProbe.Available)
                {
                    backgroundAlive = backgroundProbe.AnyAlive;
                    backgroundBusy = backgroundProbe.Busy;
                    backgroundCount = backgroundProbe.AliveProcessCount;
                    backgroundProgressUtc = backgroundProbe.LastProgressUtc;
                    if (!backgroundAlive && !backgroundProbe.Unknown && backgroundProbe.HasComparison)
                        RemoveBackgroundHints(members, backgroundHints);
                    if (backgroundAlive)
                    {
                        // An explicit detached PID is stronger evidence than generic Codex
                        // CPU. Give a newly discovered/background process time to produce a
                        // comparison sample, then require real CPU/I/O movement.
                        backgroundLooksStuck = backgroundProbe.HasComparison && !backgroundBusy &&
                            backgroundProbe.ConsecutiveQuietSamples >= 3 && silence >= StuckAfter;
                        backgroundProtectsWork = !backgroundLooksStuck;
                    }
                }
            }

            bool postRootChildActivity = false;
            DateTime latestChildDoneAfterRoot = DateTime.MinValue;
            if (root != null && rootTerminalUtc != DateTime.MinValue)
            {
                foreach (SessionTracker member in members)
                {
                    if (object.ReferenceEquals(member, root)) continue;
                    lock (member.Sync)
                    {
                        DateTime childActivity = member.TaskStartedUtc;
                        if (member.LastMeaningfulUtc > childActivity) childActivity = member.LastMeaningfulUtc;
                        if (member.LastWaitingUtc > childActivity) childActivity = member.LastWaitingUtc;
                        // A timestamp copied from inherited child history is not
                        // enough. The child must have an accepted live turn start
                        // after the root terminal boundary.
                        if (member.TurnOpen && member.TaskStartedUtc > rootTerminalUtc &&
                            childActivity > rootTerminalUtc)
                        {
                            postRootChildActivity = true;
                            break;
                        }
                        if (!member.TurnOpen && member.Terminal == TerminalKind.Done &&
                            member.LastTerminalUtc > rootTerminalUtc &&
                            member.LastTerminalUtc > latestChildDoneAfterRoot)
                            latestChildDoneAfterRoot = member.LastTerminalUtc;
                    }
                }
            }

            if (backgroundProbe != null &&
                ((!backgroundProbe.Available) || backgroundProbe.Unknown) &&
                backgroundHints.Count > 0)
            {
                // A probe failure or unverifiable PID identity is not evidence that
                // a trusted detached job died.
                // Retain the prior active state until a later sample proves exit.
                backgroundAlive = true;
                backgroundProtectsWork = true;
                backgroundCount = Math.Max(1, backgroundCount);
            }
            if (backgroundProbe != null && backgroundProbe.Available &&
                !backgroundProbe.Unknown && !backgroundProbe.AnyAlive &&
                !backgroundProbe.HasComparison && backgroundHints.Count > 0)
            {
                // The first cold sample establishes the dead/alive baseline; it
                // must not turn a stale receipt into a fresh DONE boundary.
                backgroundAlive = true;
                backgroundProtectsWork = true;
                backgroundCount = Math.Max(1, backgroundCount);
            }

            // A cold rollout with an old open flag is not a live task. Only recent
            // real progress/lifecycle evidence or a currently verified trusted PID
            // may enter the public state machine. This gate runs before lifecycle
            // history is updated, so stale history cannot create lights, retention,
            // representative changes, or notifications.
            bool trustedBackgroundAlive = backgroundProbe != null &&
                backgroundProbe.Available && !backgroundProbe.Unknown && backgroundProbe.AnyAlive;
            bool backgroundProbeNeedsRetry = backgroundProbe != null && backgroundHints.Count > 0 &&
                (!backgroundProbe.Available || backgroundProbe.Unknown);
            DateTime latestStateSignal = latestMeaningful;
            if (latestWaiting > latestStateSignal) latestStateSignal = latestWaiting;
            if (rootTerminalUtc > latestStateSignal) latestStateSignal = rootTerminalUtc;
            if (latestChildDoneAfterRoot > latestStateSignal) latestStateSignal = latestChildDoneAfterRoot;
            bool recentStateSignal = latestStateSignal != DateTime.MinValue &&
                now - latestStateSignal <= StaleHistoryAfter;
            if (!recentStateSignal && !trustedBackgroundAlive && !backgroundProbeNeedsRetry)
            {
                SetStaleHistory(members, true);
                GroupCandidate stale = new GroupCandidate();
                stale.StaleHistory = true;
                stale.Snapshot = new GroupStatusSnapshot
                {
                    GroupId = GroupKey(rootId),
                    RootId = rootId,
                    State = PublicState.Idle,
                    Project = project,
                    Reason = "Stale rollout history"
                };
                return stale;
            }
            SetStaleHistory(members, false);

            bool backgroundStoppedConfirmed = backgroundProbe != null && backgroundProbe.Available &&
                !backgroundProbe.Unknown &&
                !backgroundAlive && backgroundProbe.HasComparison;
            bool backgroundEndedAfterRootDone = previousLifecycle != null &&
                previousLifecycle.BackgroundJobActive && backgroundStoppedConfirmed &&
                rootTerminal == TerminalKind.Done;
            DateTime effectiveCompletion = rootTerminal == TerminalKind.Done ? rootTerminalUtc : DateTime.MinValue;
            if (rootTerminal == TerminalKind.Done && latestChildDoneAfterRoot > effectiveCompletion)
                effectiveCompletion = latestChildDoneAfterRoot;
            if (rootTerminal == TerminalKind.Done && previousLifecycle != null &&
                previousLifecycle.EffectiveCompletionUtc > effectiveCompletion)
            {
                // Once a detached job has ended, the confirmed background exit is
                // the completion boundary. Keep that boundary on later recomputes;
                // otherwise the root terminal timestamp would silently restart the
                // five-minute DONE retention window.
                effectiveCompletion = previousLifecycle.EffectiveCompletionUtc;
            }
            if (backgroundEndedAfterRootDone) effectiveCompletion = now;

            // User attention states remain explicit. A known detached background job can
            // keep a completed/idle root task WORKING, but it must not hide a usage-limit
            // or error signal that needs attention.
            bool attentionTerminalVisible = rootTerminalUtc != DateTime.MinValue &&
                now - rootTerminalUtc <= AttentionTerminalVisibleFor;
            if (rootTerminal == TerminalKind.LimitReached && attentionTerminalVisible)
            {
                state = PublicState.LimitReached;
                reason = string.IsNullOrEmpty(rootReason) ? "Codex reached a usage limit" : rootReason;
            }
            else if (rootTerminal == TerminalKind.Error && attentionTerminalVisible)
            {
                state = PublicState.Error;
                reason = string.IsNullOrEmpty(rootReason) ? "The Codex turn ended with an error" : rootReason;
            }
            else if (waiting)
            {
                state = PublicState.WaitingForYou;
                reason = "Codex is waiting for approval or input";
            }
            else if (backgroundAlive && backgroundProtectsWork)
            {
                state = PublicState.Working;
                reason = backgroundBusy ? "A background job is still making progress" : "A background job is still running";
                confidence = backgroundProbe != null && backgroundProbe.HasComparison && backgroundBusy ? "high" : "medium";
            }
            else if (backgroundLooksStuck)
            {
                state = PublicState.Stuck;
                reason = "A background job appears to have stopped making progress";
                confidence = "medium";
            }
            else if (rootTerminal != TerminalKind.None && rootTerminalUtc != DateTime.MinValue &&
                !(rootTerminal == TerminalKind.Done && postRootChildActivity))
            {
                bool terminalVisible = rootTerminal == TerminalKind.Done && effectiveCompletion != DateTime.MinValue &&
                    now - effectiveCompletion < DoneVisibleFor;
                if (!terminalVisible)
                {
                    state = PublicState.Idle;
                    reason = "No Codex task is running";
                }
                else if (rootTerminal == TerminalKind.Done)
                {
                    state = PublicState.Done;
                    reason = string.IsNullOrEmpty(rootReason) ? "The Codex turn completed" : rootReason;
                }
                else
                {
                    state = PublicState.Idle;
                    reason = string.IsNullOrEmpty(rootReason) ? "The Codex turn was stopped" : rootReason;
                }
            }
            else if (openCount > 0)
            {
                if (streamErrors >= 2 && latestStreamError != DateTime.MinValue && silence >= TimeSpan.FromMinutes(1))
                {
                    state = PublicState.Stuck;
                    reason = "Codex is retrying a connection with no real progress";
                    confidence = "medium";
                }
                else if (silence >= ProcessProbeAfter && activeGroupCount == 1)
                {
#if TEST_BUILD
                    ProcessProbeResult probe = ProcessProbeOverrideForTests ??
                        _processProbe.SampleThrottled(TimeSpan.FromSeconds(30));
#else
                    ProcessProbeResult probe = _processProbe.SampleThrottled(TimeSpan.FromSeconds(30));
#endif
                    foreach (SessionTracker member in members)
                        lock (member.Sync) member.LastProcessProbeUtc = now;
                    processAlive = probe.Available && probe.AnyCodexProcess;
                    // Generic Codex CPU/I/O can only protect a known open tool. It never
                    // proves forward progress by itself.
                    processBusy = probe.Available && probe.RootCount == 1 && probe.Busy;
                    if (probe.Available && !processAlive && silence >= TimeSpan.FromSeconds(30))
                    {
                        state = PublicState.Stuck;
                        reason = "A Codex task is still open, but Codex is not running";
                    }
                    else if (silence >= StuckAfter && activeTools == 0)
                    {
                        state = PublicState.Stuck;
                        reason = "No real Codex work has moved for " + FriendlyDuration(silence);
                        confidence = activeGroupCount == 1 ? "high" : "medium";
                    }
                    else if (silence >= StuckAfter && activeTools > 0 &&
                        (!probe.Available || probe.RootCount != 1))
                    {
                        state = PublicState.Stuck;
                        reason = "A Codex tool has no confirmed progress";
                        confidence = "medium";
                    }
                    else if (silence >= StuckAfter && activeTools > 0)
                    {
                        state = PublicState.Stuck;
                        reason = "A Codex tool appears to have stopped making progress";
                    }
                    else
                    {
                        state = PublicState.Working;
                        reason = activeTools > 0 ? "A Codex tool is still running" : "Recent Codex work was detected";
                        if (silence >= TimeSpan.FromMinutes(1)) confidence = "medium";
                    }
                }
                else if (silence >= StuckAfter && activeTools == 0)
                {
                    state = PublicState.Stuck;
                    reason = "No real Codex work has moved for " + FriendlyDuration(silence);
                    confidence = activeGroupCount == 1 ? "high" : "medium";
                }
                else
                {
                    state = PublicState.Working;
                    reason = activeTools > 0 ? "A Codex tool is still running" : "Recent Codex work was detected";
                    confidence = silence >= TimeSpan.FromMinutes(1) ? "medium" : "high";
                }
            }
            else
            {
                state = PublicState.Idle;
                reason = "No Codex task is running";
            }

            GroupStatusSnapshot snapshot = new GroupStatusSnapshot();
            snapshot.GroupId = GroupKey(rootId);
            snapshot.RootId = rootId;
            snapshot.State = state;
            snapshot.Project = project;
            snapshot.LastRealWorkUtc = backgroundProgressUtc > latestMeaningful ? backgroundProgressUtc : latestMeaningful;
            snapshot.Reason = reason;
            snapshot.EffectiveCompletionUtc = state == PublicState.Done ? effectiveCompletion : DateTime.MinValue;
            snapshot.BackgroundJobActive = backgroundAlive;
            snapshot.BackgroundLastProgressUtc = backgroundProgressUtc;
            snapshot.SessionPath = path;
            snapshot.OpenTurnCount = openCount;
            snapshot.ProcessAlive = processAlive;
            snapshot.ProcessBusy = processBusy;
            snapshot.BackgroundProcessBusy = backgroundBusy;
            snapshot.BackgroundProcessCount = backgroundCount;
            snapshot.Confidence = confidence;

            DateTime relevantActivity = backgroundProgressUtc > latestMeaningful ? backgroundProgressUtc : latestMeaningful;
            if (latestWaiting > relevantActivity) relevantActivity = latestWaiting;
            if (latestStreamError > relevantActivity) relevantActivity = latestStreamError;
            if (rootTerminalUtc > relevantActivity) relevantActivity = rootTerminalUtc;
            // LastAny contains harmless telemetry and user/context records. Use it only
            // when the group has no stronger lifecycle or work timestamp at all.
            if (relevantActivity == DateTime.MinValue) relevantActivity = latestAny;

            if (state == PublicState.Done)
                snapshot.StateEventUtc = effectiveCompletion;
            else if (state == PublicState.Error || state == PublicState.LimitReached)
                snapshot.StateEventUtc = rootTerminalUtc;
            else if (state == PublicState.WaitingForYou)
                snapshot.StateEventUtc = latestWaiting;
            else if (state == PublicState.Working)
                snapshot.StateEventUtc = relevantActivity;
            else if (state == PublicState.Stuck)
                snapshot.StateEventUtc = latestMeaningful > latestStreamError ? latestMeaningful : latestStreamError;
            else
                snapshot.StateEventUtc = rootTerminalUtc;

            ApplyGroupLifecycle(snapshot, previousLifecycle, now);
            GroupCandidate candidateResult = new GroupCandidate();
            candidateResult.Snapshot = snapshot;
            candidateResult.ActivityUtc = relevantActivity;
            return candidateResult;
        }

        private void MigrateLineageHistory(Dictionary<string, List<SessionTracker>> groups)
        {
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<SessionTracker>> pair in groups)
            {
                string canonical = GroupKey(pair.Key);
                foreach (SessionTracker member in pair.Value)
                {
                    string id = member == null ? null : member.Id;
                    if (!string.IsNullOrEmpty(id)) aliases[GroupKey(id)] = canonical;
                }
            }

            lock (_sync)
            {
                foreach (KeyValuePair<string, string> pair in aliases)
                {
                    string target = CanonicalGroupKeyLocked(pair.Value);
                    if (string.Equals(pair.Key, target, StringComparison.OrdinalIgnoreCase)) continue;
                    _groupAliases[pair.Key] = target;
                }

                List<KeyValuePair<string, GroupLifecycle>> migrations = new List<KeyValuePair<string, GroupLifecycle>>();
                foreach (KeyValuePair<string, GroupLifecycle> pair in _groupHistory)
                {
                    string target;
                    if (!_groupAliases.TryGetValue(pair.Key, out target)) continue;
                    target = CanonicalGroupKeyLocked(target);
                    if (string.Equals(pair.Key, target, StringComparison.OrdinalIgnoreCase)) continue;
                    migrations.Add(new KeyValuePair<string, GroupLifecycle>(pair.Key, pair.Value));
                }

                foreach (KeyValuePair<string, GroupLifecycle> migration in migrations)
                {
                    string target = CanonicalGroupKeyLocked(_groupAliases[migration.Key]);
                    GroupLifecycle existing;
                    if (!_groupHistory.TryGetValue(target, out existing) || existing == null ||
                        (migration.Value != null && migration.Value.LastSeenUtc > existing.LastSeenUtc))
                    {
                        GroupLifecycle moved = migration.Value;
                        if (moved != null && moved.LastSnapshot != null)
                        {
                            moved.LastSnapshot = moved.LastSnapshot.Clone();
                            moved.LastSnapshot.GroupId = target;
                            moved.LastSnapshot.RootId = target.StartsWith("root:", StringComparison.OrdinalIgnoreCase)
                                ? target.Substring(5) : target;
                        }
                        _groupHistory[target] = moved;
                    }
                    _groupHistory.Remove(migration.Key);
                }
            }
        }

        private string CanonicalGroupKeyLocked(string key)
        {
            string current = key;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(current) && seen.Add(current))
            {
                string next;
                if (!_groupAliases.TryGetValue(current, out next) || string.IsNullOrEmpty(next)) break;
                current = next;
            }
            return current;
        }

        private static string GroupKey(string rootId)
        {
            return "root:" + (string.IsNullOrEmpty(rootId) ? "unknown" : rootId);
        }

        private void ApplyGroupLifecycle(GroupStatusSnapshot current, GroupLifecycle previous, DateTime now)
        {
            bool sameLifecycle = previous != null && previous.State == current.State &&
                previous.StateEventUtc == current.StateEventUtc &&
                previous.EffectiveCompletionUtc == current.EffectiveCompletionUtc;
            if (sameLifecycle && previous.StateSinceUtc != DateTime.MinValue)
                current.StateSinceUtc = previous.StateSinceUtc;
            else
                current.StateSinceUtc = now;

            GroupLifecycle next = new GroupLifecycle();
            next.State = current.State;
            next.StateSinceUtc = current.StateSinceUtc;
            next.StateEventUtc = current.StateEventUtc;
            next.EffectiveCompletionUtc = current.EffectiveCompletionUtc;
            next.BackgroundJobActive = current.BackgroundJobActive;
            next.LastSeenUtc = now;
            next.LastSnapshot = current.Clone();
            lock (_sync) _groupHistory[current.GroupId] = next;
        }

        private List<GroupStatusSnapshot> GetRetainedDoneGroups(IEnumerable<string> rootIds, DateTime now)
        {
            HashSet<string> live = new HashSet<string>(rootIds.Select(GroupKey), StringComparer.OrdinalIgnoreCase);
            List<GroupStatusSnapshot> result = new List<GroupStatusSnapshot>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, GroupLifecycle> pair in _groupHistory)
                {
                    GroupLifecycle lifecycle = pair.Value;
                    if (lifecycle == null || live.Contains(pair.Key) || lifecycle.LastSnapshot == null ||
                        lifecycle.LastSnapshot.State != PublicState.Done ||
                        lifecycle.LastSnapshot.EffectiveCompletionUtc == DateTime.MinValue) continue;
                    if (now - lifecycle.LastSnapshot.EffectiveCompletionUtc < DoneVisibleFor)
                        result.Add(lifecycle.LastSnapshot.Clone());
                }
            }
            return result;
        }

        private static DateTime GroupActivity(GroupStatusSnapshot group)
        {
            if (group == null) return DateTime.MinValue;
            if (group.LastRealWorkUtc != DateTime.MinValue) return group.LastRealWorkUtc;
            return group.EffectiveCompletionUtc == DateTime.MinValue ? group.StateSinceUtc : group.EffectiveCompletionUtc;
        }

        private static void SetStaleHistory(List<SessionTracker> members, bool stale)
        {
            if (members == null) return;
            foreach (SessionTracker member in members)
            {
                if (member == null) continue;
                lock (member.Sync) member.StaleHistory = stale;
            }
        }

        private void PruneGroupHistory(IEnumerable<string> rootIds, HashSet<string> activeMemberKeys)
        {
            HashSet<string> live = new HashSet<string>(rootIds.Select(GroupKey), StringComparer.OrdinalIgnoreCase);
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            lock (_sync)
            {
                List<string> remove = new List<string>();
                foreach (KeyValuePair<string, GroupLifecycle> pair in _groupHistory)
                {
                    if (live.Contains(pair.Key)) continue;
                    if (pair.Value == null || pair.Value.LastSeenUtc == DateTime.MinValue || pair.Value.LastSeenUtc < cutoff)
                        remove.Add(pair.Key);
                }
                foreach (string key in remove) _groupHistory.Remove(key);

                List<string> removeAliases = new List<string>();
                foreach (KeyValuePair<string, string> alias in _groupAliases)
                {
                    if ((activeMemberKeys == null || !activeMemberKeys.Contains(alias.Key)) &&
                        !_groupHistory.ContainsKey(alias.Key)) removeAliases.Add(alias.Key);
                }
                foreach (string key in removeAliases) _groupAliases.Remove(key);
            }
        }

        private static string ResolveRootId(SessionTracker tracker, Dictionary<string, SessionTracker> idMap)
        {
            if (tracker.AmbiguousId)
                return tracker.Path ?? (tracker.Id ?? "ambiguous");
            string current = tracker.Id;
            string parent = tracker.ParentId;
            if (string.IsNullOrEmpty(parent) && tracker.Meta != null) parent = tracker.Meta.ForkedFromId;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(current)) seen.Add(current);
            while (!string.IsNullOrEmpty(parent) && !seen.Contains(parent))
            {
                seen.Add(parent);
                current = parent;
                SessionTracker parentTracker;
                if (!idMap.TryGetValue(parent, out parentTracker))
                {
                    // The declared parent/thread lineage is the authoritative
                    // grouping key even when the parent rollout is temporarily
                    // outside the discovery window. The group remains non-DONE
                    // until an actual root terminal is observed.
                    return parent;
                }
                parent = parentTracker.ParentId;
                if (string.IsNullOrEmpty(parent) && parentTracker.Meta != null)
                    parent = parentTracker.Meta.ForkedFromId;
            }
            if (!string.IsNullOrEmpty(parent) && seen.Contains(parent))
                return tracker.Path ?? (tracker.Id ?? "cyclic-lineage");
            return current;
        }

        private static DateTime SafeAny(SessionTracker tracker)
        {
            lock (tracker.Sync) return tracker.LastAnyUtc;
        }

        private static bool IsBetterCandidate(GroupCandidate candidate, GroupCandidate current)
        {
            if (current == null) return true;
            if (candidate.Snapshot.State != current.Snapshot.State || candidate.ActivityUtc != current.ActivityUtc)
                return IsBetterState(candidate.Snapshot.State, candidate.ActivityUtc,
                    current.Snapshot.State, current.ActivityUtc);
            return string.Compare(candidate.Snapshot.GroupId, current.Snapshot.GroupId, StringComparison.OrdinalIgnoreCase) < 0;
        }

#if TEST_BUILD
        internal static bool IsBetterStateForTests(PublicState candidateState, DateTime candidateActivityUtc,
            PublicState currentState, DateTime currentActivityUtc)
        {
            return IsBetterState(candidateState, candidateActivityUtc, currentState, currentActivityUtc);
        }
#endif

        private static bool IsBetterState(PublicState candidateState, DateTime candidateActivityUtc,
            PublicState currentState, DateTime currentActivityUtc)
        {
            int candidateTier = StateTier(candidateState);
            int currentTier = StateTier(currentState);
            if (candidateTier != currentTier) return candidateTier > currentTier;

            if (candidateActivityUtc == DateTime.MinValue && currentActivityUtc != DateTime.MinValue) return false;
            if (currentActivityUtc == DateTime.MinValue && candidateActivityUtc != DateTime.MinValue) return true;
            if (candidateActivityUtc != currentActivityUtc) return candidateActivityUtc > currentActivityUtc;
            return false;
        }

        private static int StateTier(PublicState state)
        {
            switch (state)
            {
                case PublicState.Error: return 7;
                case PublicState.LimitReached: return 6;
                case PublicState.WaitingForYou: return 5;
                case PublicState.Stuck: return 4;
                case PublicState.Working: return 3;
                case PublicState.Done: return 2;
                default: return 1;
            }
        }

        private static string ProjectName(string cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return "Unknown project";
            try
            {
                string clean = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(clean);
                return string.IsNullOrEmpty(name) ? cwd : name;
            }
            catch { return cwd; }
        }

        private static string FriendlyDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1) return string.Format("{0}h {1}m", (int)span.TotalHours, span.Minutes);
            if (span.TotalMinutes >= 1) return string.Format("{0} min", (int)span.TotalMinutes);
            return string.Format("{0} sec", Math.Max(0, (int)span.TotalSeconds));
        }

        private static StatusSnapshot MakeIdle(string reason)
        {
            StatusSnapshot snapshot = new StatusSnapshot();
            snapshot.State = PublicState.Idle;
            snapshot.Project = "No active project";
            snapshot.LastWorkUtc = DateTime.MinValue;
            snapshot.StateSinceUtc = DateTime.UtcNow;
            snapshot.Reason = reason;
            snapshot.SessionPath = null;
            snapshot.GroupCount = 0;
            snapshot.OpenTurnCount = 0;
            snapshot.Confidence = "high";
            snapshot.ActiveStatesMask = StatusSnapshot.StateBit(PublicState.Idle);
            return snapshot;
        }

        private static bool EquivalentPublic(StatusSnapshot a, StatusSnapshot b)
        {
            if (a == null || b == null) return false;

            // Notify the UI only when the primary state/project or the visible status-light
            // set changes. Last-work time, reason and diagnostics stay current in _current
            // and are fetched on demand.
            return a.State == b.State &&
                   a.ActiveStatesMask == b.ActiveStatesMask &&
                   string.Equals(a.Project, b.Project, StringComparison.Ordinal) &&
                   string.Equals(a.PrimaryGroupId, b.PrimaryGroupId, StringComparison.Ordinal) &&
                   GroupsEquivalent(a.Groups, b.Groups);
        }

        private static bool GroupsEquivalent(GroupStatusSnapshot[] a, GroupStatusSnapshot[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == null || b[i] == null)
                {
                    if (!object.ReferenceEquals(a[i], b[i])) return false;
                    continue;
                }
                if (!string.Equals(a[i].GroupId, b[i].GroupId, StringComparison.OrdinalIgnoreCase) ||
                    a[i].State != b[i].State ||
                    !string.Equals(a[i].Project, b[i].Project, StringComparison.Ordinal) ||
                    a[i].StateEventUtc != b[i].StateEventUtc ||
                    a[i].EffectiveCompletionUtc != b[i].EffectiveCompletionUtc) return false;
            }
            return true;
        }

#if TEST_BUILD
        internal StatusSnapshot BuildGroupForTests(string rootId, List<SessionTracker> members,
            Dictionary<string, SessionTracker> idMap, int activeGroupCount, int totalGroupCount)
        {
            GroupStatusSnapshot group = BuildGroupCandidate(rootId, members, idMap, activeGroupCount).Snapshot;
            StatusSnapshot snapshot = new StatusSnapshot();
            snapshot.State = group.State;
            snapshot.PrimaryGroupId = group.GroupId;
            snapshot.Project = group.Project;
            snapshot.LastWorkUtc = group.LastRealWorkUtc;
            snapshot.StateSinceUtc = group.StateSinceUtc;
            snapshot.Reason = group.Reason;
            snapshot.SessionPath = group.SessionPath;
            snapshot.GroupCount = group.State == PublicState.Idle ? 0 : 1;
            snapshot.OpenTurnCount = group.OpenTurnCount;
            snapshot.ProcessAlive = group.ProcessAlive;
            snapshot.ProcessBusy = group.ProcessBusy;
            snapshot.BackgroundProcessAlive = group.BackgroundJobActive;
            snapshot.BackgroundProcessBusy = group.BackgroundProcessBusy;
            snapshot.BackgroundProcessCount = group.BackgroundProcessCount;
            snapshot.BackgroundLastProgressUtc = group.BackgroundLastProgressUtc;
            snapshot.Confidence = group.Confidence;
            snapshot.ActiveStatesMask = StatusSnapshot.StateBit(group.State);
            snapshot.Groups = group.State == PublicState.Idle ? new GroupStatusSnapshot[0] : new GroupStatusSnapshot[] { group.Clone() };
            return snapshot;
        }

        // Narrow real-parser seams used by integration self-tests. They keep the
        // production chronology (load under the work gate, then publish a cloned
        // snapshot) without exposing the mutable tracker collection to the UI.
        internal void LoadPathForTests(string path)
        {
            lock (_workGate) ProcessPath(path);
        }

        internal void RecomputeForTests(bool force)
        {
            lock (_workGate) RecomputeAndPublish(force);
        }

        internal void RemovePathForTests(string path)
        {
            lock (_workGate) RemoveTracker(path);
        }

        internal void SimulateWatcherErrorForTests()
        {
            FileSystemWatcher watcher;
            lock (_sync) watcher = _watcher;
            if (watcher != null)
                OnWatcherError(watcher, new ErrorEventArgs(new IOException("simulated watcher overflow")));
            else
            {
                lock (_sync) _resyncNeeded = true;
            }
        }

        internal void RunParserBatchForTests()
        {
            ProcessDirtyBatch(null);
        }

        internal long GetOffsetForTests(string path)
        {
            lock (_sync)
            {
                SessionTracker tracker;
                if (!_byPath.TryGetValue(path, out tracker)) return -1;
                lock (tracker.Sync) return tracker.Offset;
            }
        }
#endif

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            _batchTimer.Dispose();
            _watchdogTimer.Dispose();
            _deadlineTimer.Dispose();
            _processProbe.Dispose();
        }

        private sealed class GroupCandidate
        {
            public GroupStatusSnapshot Snapshot;
            public DateTime ActivityUtc;
            public bool StaleHistory;
        }

        private sealed class GroupLifecycle
        {
            public PublicState State;
            public DateTime StateSinceUtc;
            public DateTime StateEventUtc;
            public DateTime EffectiveCompletionUtc;
            public bool BackgroundJobActive;
            public DateTime LastSeenUtc;
            public GroupStatusSnapshot LastSnapshot;
        }
    }
}
