using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public enum SessionLifecycleState
{
    Idle,
    Starting,
    Running,
    Paused,
    Stopping,
    Failed
}

/// <summary>
/// Global quest selection produced exclusively by the Scripts page and consumed by PreSynthesis and GameVoice.
/// Sub-task filtering inside PreSynthesis never mutates this global identity.
/// </summary>
public sealed record SelectedQuestScope(
    string GameId,
    string SelectionName,
    string[] ChapterIds,
    string RecognitionLanguage,
    string SpeechLanguage,
    int MatchedNodes,
    int UnresolvedNodes,
    int CachedCount,
    int DownloadedCount,
    string? PreferredStoryId = null,
    string? PreferredTaskId = null,
    DateTimeOffset? SelectedUtc = null)
{
    public bool HasSelection => ChapterIds.Length > 0;
    public string SummaryLabel => HasSelection
        ? $"{SelectionName}（{ChapterIds.Length} 章 · 识别 {RecognitionLanguage.ToUpperInvariant()} → 配音 {SpeechLanguage.ToUpperInvariant()}）"
        : "尚未在「剧情资料」选择当前剧情";
}

public sealed record SessionSnapshot(
    string SessionId,
    long Generation,
    SessionLifecycleState State,
    string GameId,
    string SourceMode,
    string RecognitionLanguage,
    string VoiceLanguage,
    string StatusText,
    string? CurrentSpeaker,
    string? CurrentOcrText,
    string? CurrentMatchedText,
    string? CurrentNodeKey,
    string? CurrentVoiceName,
    string? CurrentProviderId,
    string AudioPlaybackState,
    string? LastError,
    int P0Waiting,
    int P1Waiting);

/// <summary>
/// Shared state across the four sub-views of VideoWorkspace (Preview, OfflineScan, Compare, TimelineExport).
/// Switching sub-views never resets loaded media or valid scan runs; changing VideoPath validates MediaFingerprint.
/// </summary>
public sealed record VideoProjectState(
    string VideoPath,
    string MediaFingerprint,
    double DurationMs,
    string VideoCodec,
    IReadOnlyList<string> ScanRunIds,
    string? SelectedTimelineRunId,
    string? SelectedTimelineJsonPath,
    string? SelectedDialogueEventId)
{
    public static VideoProjectState Empty { get; } = new("", "", 0, "", [], null, null, null);

    public static string ComputeMediaFingerprint(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        var info = new FileInfo(Path.GetFullPath(path));
        return SpeechHash.Of($"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")[..16];
    }

    public bool MatchesMedia(string candidateVideoPath) =>
        !string.IsNullOrWhiteSpace(MediaFingerprint) &&
        string.Equals(MediaFingerprint, ComputeMediaFingerprint(candidateVideoPath), StringComparison.OrdinalIgnoreCase);
}

public sealed record ExportSelection(
    string TimelineRunId,
    string TimelineJsonPath,
    string ExpectedMediaFingerprint,
    string ProfileTag,
    bool KeepSourceAudio,
    double SourceGainPercent,
    bool ExportAac,
    double? StartSeconds,
    double? EndSeconds);

/// <summary>
/// Single authoritative source of cross-page workspace state (Docs/03-PAGES-AND-STATE.md).
/// </summary>
public sealed class AppWorkspaceState
{
    private readonly object gate = new();
    private string selectedGameId = "genshin";
    private string sourceMode = "window"; // "window" (WGC) or "video" (MPV)
    private string recognitionLanguage = "chs";
    private string voiceLanguage = "jpn";
    private SelectedQuestScope? selectedQuestScope;
    private int voiceBindingsVersion = 1;
    private VideoProjectState videoProject = VideoProjectState.Empty;

    public event Action? StateChanged;
    public event Action<SelectedQuestScope?>? QuestScopeChanged;
    public event Action<string>? GameChanged;
    public event Action<int>? VoiceBindingsVersionChanged;
    public event Action<VideoProjectState>? VideoProjectChanged;

    public string SelectedGameId
    {
        get { lock (gate) return selectedGameId; }
    }

    public string SourceMode
    {
        get { lock (gate) return sourceMode; }
        set { lock (gate) sourceMode = value; StateChanged?.Invoke(); }
    }

    public string RecognitionLanguage
    {
        get { lock (gate) return recognitionLanguage; }
        set { lock (gate) recognitionLanguage = QuestLanguages.Normalize(value); StateChanged?.Invoke(); }
    }

    public string VoiceLanguage
    {
        get { lock (gate) return voiceLanguage; }
        set { lock (gate) voiceLanguage = QuestLanguages.Normalize(value); StateChanged?.Invoke(); }
    }

    public SelectedQuestScope? SelectedQuestScope
    {
        get { lock (gate) return selectedQuestScope; }
    }

    public int VoiceBindingsVersion
    {
        get { lock (gate) return voiceBindingsVersion; }
    }

    public VideoProjectState VideoProject
    {
        get { lock (gate) return videoProject; }
    }

    public bool SelectGame(string gameId, bool sessionRunning, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(gameId)) return false;
        var normalized = gameId.Trim().ToLowerInvariant();
        bool scopeCleared = false;
        lock (gate)
        {
            if (string.Equals(selectedGameId, normalized, StringComparison.OrdinalIgnoreCase)) return true;
            if (sessionRunning)
                warning = "当前会话仍在运行；请先停止当前配音会话后再切换活动游戏，避免混用游戏配置。";
            selectedGameId = normalized;
            if (selectedQuestScope is not null && !string.Equals(selectedQuestScope.GameId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                selectedQuestScope = null;
                scopeCleared = true;
            }
        }
        GameChanged?.Invoke(normalized);
        if (scopeCleared) QuestScopeChanged?.Invoke(null);
        StateChanged?.Invoke();
        return true;
    }

    public void SetSelectedQuestScope(SelectedQuestScope? scope)
    {
        lock (gate)
        {
            selectedQuestScope = scope;
            if (scope is not null)
            {
                recognitionLanguage = QuestLanguages.Normalize(scope.RecognitionLanguage);
                voiceLanguage = QuestLanguages.Normalize(scope.SpeechLanguage);
            }
        }
        QuestScopeChanged?.Invoke(scope);
        StateChanged?.Invoke();
    }

    public void NotifyVoiceBindingsUpdated(int newVersion)
    {
        lock (gate) voiceBindingsVersion = Math.Max(voiceBindingsVersion + 1, newVersion);
        VoiceBindingsVersionChanged?.Invoke(voiceBindingsVersion);
        StateChanged?.Invoke();
    }

    public void UpdateVideoProject(VideoProjectState next)
    {
        lock (gate) videoProject = next;
        VideoProjectChanged?.Invoke(next);
        StateChanged?.Invoke();
    }
}
