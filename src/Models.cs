using System;
using System.Collections.Generic;

namespace IsCodexWorking
{
    internal enum PublicState
    {
        Working,
        WaitingForYou,
        Stuck,
        Done,
        LimitReached,
        Error,
        Idle
    }

    internal enum TerminalKind
    {
        None,
        Done,
        LimitReached,
        Error,
        Aborted
    }

    internal sealed class StatusSnapshot
    {
        // State is kept as the compatibility alias used by the original preview.
        // PrimaryState is the explicit aggregate representative used by the
        // multi-chat model and tray icon.
        public PublicState State;
        public string PrimaryGroupId;
        public string Project;
        public DateTime LastWorkUtc;
        public DateTime StateSinceUtc;
        public string Reason;
        public string SessionPath;
        public int GroupCount;
        public int OpenTurnCount;
        public bool ProcessAlive;
        public bool ProcessBusy;
        public bool BackgroundProcessAlive;
        public bool BackgroundProcessBusy;
        public int BackgroundProcessCount;
        public DateTime BackgroundLastProgressUtc;
        public string Confidence;
        public int ActiveStatesMask;
        public GroupStatusSnapshot[] Groups = new GroupStatusSnapshot[0];

        public PublicState PrimaryState
        {
            get { return State; }
            set { State = value; }
        }

        public static int StateBit(PublicState state)
        {
            return 1 << (int)state;
        }

        public bool IsStateLit(PublicState state)
        {
            int mask = ActiveStatesMask;
            if (mask == 0) mask = StateBit(State);
            return (mask & StateBit(state)) != 0;
        }

        public PublicState[] ActiveStates
        {
            get
            {
                List<PublicState> result = new List<PublicState>();
                foreach (PublicState state in Enum.GetValues(typeof(PublicState)))
                    if (IsStateLit(state)) result.Add(state);
                return result.ToArray();
            }
        }

        public StatusSnapshot Clone()
        {
            StatusSnapshot copy = (StatusSnapshot)MemberwiseClone();
            if (Groups == null)
            {
                copy.Groups = new GroupStatusSnapshot[0];
            }
            else
            {
                copy.Groups = new GroupStatusSnapshot[Groups.Length];
                for (int i = 0; i < Groups.Length; i++)
                    copy.Groups[i] = Groups[i] == null ? null : Groups[i].Clone();
            }
            return copy;
        }

        public string StateTitle
        {
            get
            {
                switch (State)
                {
                    case PublicState.Working: return "WORKING";
                    case PublicState.WaitingForYou: return "WAITING FOR YOU";
                    case PublicState.Stuck: return "STUCK";
                    case PublicState.Done: return "DONE";
                    case PublicState.LimitReached: return "LIMIT REACHED";
                    case PublicState.Error: return "ERROR";
                    default: return "NO TASK";
                }
            }
        }

        public string Sentence
        {
            get
            {
                switch (State)
                {
                    case PublicState.Working: return "Codex is working";
                    case PublicState.WaitingForYou: return "Codex is waiting for you";
                    case PublicState.Stuck: return "Codex may be stuck";
                    case PublicState.Done: return "Codex is done";
                    case PublicState.LimitReached: return "Usage limit reached";
                    case PublicState.Error: return "Codex had an error";
                    default: return "No Codex task is running";
                }
            }
        }

        public string PublicTitle
        {
            get { return PublicCopy.TitleFor(State); }
        }

        public string PublicSubtitle
        {
            get { return PublicCopy.SubtitleFor(State); }
        }
    }

    internal sealed class GroupStatusSnapshot
    {
        public string GroupId;
        public string RootId;
        public PublicState State;
        public string Project;
        public DateTime LastRealWorkUtc;
        public DateTime StateSinceUtc;
        public DateTime StateEventUtc;
        public string Reason;
        public DateTime EffectiveCompletionUtc;
        public bool BackgroundJobActive;
        public DateTime BackgroundLastProgressUtc;
        public string SessionPath;
        public int OpenTurnCount;
        public bool ProcessAlive;
        public bool ProcessBusy;
        public int BackgroundProcessCount;
        public bool BackgroundProcessBusy;
        public string Confidence;

        public GroupStatusSnapshot Clone()
        {
            return (GroupStatusSnapshot)MemberwiseClone();
        }

        public string PublicTitle
        {
            get { return PublicCopy.TitleFor(State); }
        }

        public string PublicSubtitle
        {
            get { return PublicCopy.SubtitleFor(State); }
        }
    }

    internal static class PublicCopy
    {
        public static string TitleFor(PublicState state)
        {
            switch (state)
            {
                case PublicState.Working: return "I'm working on it!";
                case PublicState.WaitingForYou: return "I need you!";
                case PublicState.Stuck: return "Hmm... I'm stuck";
                case PublicState.Done: return "All done!";
                case PublicState.LimitReached: return "I'm out of juice";
                case PublicState.Error: return "Oops! Something went wrong";
                default: return "Nothing to do!";
            }
        }

        public static string SubtitleFor(PublicState state)
        {
            switch (state)
            {
                case PublicState.Working: return "Making real progress";
                case PublicState.WaitingForYou: return "Waiting for your approval";
                case PublicState.Stuck: return "No real progress for 5 min";
                case PublicState.Done: return "Finished successfully";
                case PublicState.LimitReached: return "Usage limit reached";
                case PublicState.Error: return "Codex stopped with an error";
                default: return "No Codex task is running";
            }
        }
    }

    internal sealed class SessionMeta
    {
        public string Id;
        public string ParentThreadId;
        public string ForkedFromId;
        public string Cwd;
        public string ThreadSource;
        public string HistoryMode;
        public long SubagentHistoryStartOrdinal = -1;
        public DateTime CreatedUtc;
        public bool MetadataComplete = true;

        public bool HasInheritedHistoryRisk
        {
            get
            {
                return !string.IsNullOrEmpty(ParentThreadId) || !string.IsNullOrEmpty(ForkedFromId);
            }
        }
    }

    internal sealed class SessionTracker
    {
        public readonly object Sync = new object();
        public string Path;
        public SessionMeta Meta;
        public long Offset;
        public byte[] Carry = new byte[0];
        public bool DiscardUntilNewline;
        public long PrefixFingerprint;
        public long PrefixFingerprintLength;
        public long TailFingerprint;
        public long TailFingerprintStart;
        public long TailFingerprintLength;
        public DateTime LastWriteUtc;
        public DateTime CreationUtc;
        public bool AmbiguousId;
        // Internal only: stale history is retained for diagnostics but excluded
        // from public snapshots, state lights, and notifications.
        public bool StaleHistory;

        public bool TurnOpen;
        public string CurrentTurnId;
        public DateTime TaskStartedUtc;
        public DateTime LastMeaningfulUtc;
        public DateTime LastAnyUtc;
        public DateTime LastTerminalUtc;
        public DateTime LastWaitingUtc;
        public DateTime LastErrorUtc;
        public DateTime LastLimitUtc;
        public DateTime LastAssistantReplyUtc;
        public bool WaitingForUser;
        public TerminalKind Terminal;
        public string TerminalReason;
        public bool AgentReplySeenSinceTaskStart;
        public int ActiveToolCount;
        public readonly HashSet<string> ActiveToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public long LastTokenTotal = -1;
        public DateTime LastStreamErrorUtc;
        public int StreamErrorsSinceProgress;
        public bool ExplicitTurnStartSeen;
        public bool AllowImplicitOpenAfterBaseline;
        public DateTime BackgroundHintScanUtc;
        public DateTime BackgroundReceiptScanUtc;
        public DateTime LastProcessProbeUtc;
        public DateTime LastDeadlineWakeUtc;
        public DateTime PendingBackgroundLaunchUtc;
        public readonly Dictionary<string, DateTime> PendingBackgroundLaunchTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, BackgroundProcessHint> BackgroundProcesses = new Dictionary<int, BackgroundProcessHint>();
        public readonly HashSet<string> BackgroundRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Id
        {
            get { return Meta == null ? null : Meta.Id; }
        }

        public string ParentId
        {
            get { return Meta == null ? null : Meta.ParentThreadId; }
        }

        public string Cwd
        {
            get { return Meta == null ? null : Meta.Cwd; }
        }
    }

    internal sealed class BackgroundProcessHint
    {
        public int Pid;
        public DateTime ObservedUtc;
        public DateTime LaunchUtc;
        public DateTime ProcessStartUtc;
        public string Source;
        public string ExecutablePath;
        public string StdoutPath;
        public string StderrPath;

        public BackgroundProcessHint Clone()
        {
            return (BackgroundProcessHint)MemberwiseClone();
        }
    }

    internal sealed class BackgroundProbeResult
    {
        public bool Available;
        // Unknown identity/probe state is not a confirmed process exit.
        public bool Unknown;
        public bool AnyAlive;
        public bool Busy;
        public bool HasComparison;
        public int AliveProcessCount;
        public int ConsecutiveQuietSamples;
        public DateTime LastProgressUtc;
        public string Note;
    }

    internal sealed class ProcessProbeResult
    {
        public bool AnyCodexProcess;
        public bool Busy;
        public int ProcessCount;
        public int RootCount;
        public string Note;
        public bool Available;
        public bool HasComparison;
        public int ConsecutiveQuietSamples;
    }

    internal sealed class ParserMetricsSnapshot
    {
        public long BatchCount;
        public long AppendReadOperations;
        public long AppendReadBytes;
        public long ParsedRecordCount;
        public long BoundedResyncCount;
        public long BoundedResyncBytes;
        public long DirectoryScanCount;
        public int MaxConcurrentParser;

        public ParserMetricsSnapshot Clone()
        {
            return (ParserMetricsSnapshot)MemberwiseClone();
        }
    }
}
