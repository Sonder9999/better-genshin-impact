using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record SimulatedAudioClip(string NodeKey, string LineId, string AudioPath, double DurationMs, bool IsValid = true);
public enum SimulatedPlaybackKind { Prepared, StartScheduled, Started, Stopped, Cancelled, Completed, MissingAudio }
public sealed record SimulatedPlaybackEvent(string EventId, string DecisionId, long SessionId, string ClipId,
    SimulatedPlaybackKind Kind, double TimestampMs, double SourceMs, string? NodeKey, string? LineId,
    string? AudioPath, double? DurationMs, double? PlayedMs, string Reason);

public sealed class SimulatedPlayer
{
    private sealed record Request(FusionEvent Command, SimulatedAudioClip Clip, double DueMs);
    private sealed record Playing(Request Request, double StartedMs);
    private readonly Dictionary<string, SimulatedAudioClip> catalog;
    private readonly Dictionary<string, FusionEvent> prepared = new(StringComparer.Ordinal);
    private readonly HashSet<string> delivered = new(StringComparer.Ordinal);
    private readonly HashSet<string> playDecisions = new(StringComparer.Ordinal);
    private readonly List<SimulatedPlaybackEvent> trace = [];
    private readonly double startupDelay;
    private Request? pending;
    private Playing? active;
    private double clock = double.NegativeInfinity;
    private long sessionId, closedSession, sequence;

    public SimulatedPlayer(IReadOnlyList<SimulatedAudioClip> clips, double startupDelayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (!double.IsFinite(startupDelayMs) || startupDelayMs < 0 || clips.Any(c => c is null ||
            string.IsNullOrWhiteSpace(c.NodeKey) || string.IsNullOrWhiteSpace(c.LineId) || string.IsNullOrWhiteSpace(c.AudioPath) ||
            !double.IsFinite(c.DurationMs) || c.DurationMs <= 0) || clips.GroupBy(c => c.NodeKey, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new ArgumentException("Invalid simulation metadata or startup delay.");
        catalog = clips.ToDictionary(c => c.NodeKey, StringComparer.Ordinal);
        startupDelay = startupDelayMs;
    }
    public IReadOnlyList<SimulatedPlaybackEvent> Trace => trace.AsReadOnly();
    public IReadOnlyList<string> PreparedNodeKeys => Array.AsReadOnly(prepared.Keys.ToArray());
    public string? ActiveNodeKey => active?.Request.Clip.NodeKey;
    public string? PendingNodeKey => pending?.Clip.NodeKey;

    public void Consume(IEnumerable<FusionEvent> events, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(events);
        ValidateNow(nowMs);
        var batch = events.ToArray();
        for (var i = 0; i < batch.Length; i++)
        {
            var e = batch[i];
            if (e is null || string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.DecisionId) ||
                string.IsNullOrWhiteSpace(e.ClipId) || e.SessionId <= 0 || !Enum.IsDefined(e.Kind) ||
                !double.IsFinite(e.TimestampMs) || !double.IsFinite(e.SourceMs) || e.SourceMs < 0 || e.SourceMs > e.TimestampMs ||
                e.TimestampMs > nowMs || e.TimestampMs < clock && !delivered.Contains(e.EventId) && e.SessionId >= sessionId ||
                i > 0 && e.TimestampMs < batch[i - 1].TimestampMs)
                throw new ArgumentException("Simulation commands must be causal and ordered.");
        }
        foreach (var e in batch)
        {
            if (!delivered.Add(e.EventId)) continue;
            if (e.SessionId < sessionId || e.SessionId <= closedSession)
            {
                Log(e, SimulatedPlaybackKind.Cancelled, Math.Max(clock, e.TimestampMs), null, 0, "stale_session");
                continue;
            }
            AdvanceTo(e.TimestampMs);
            if (e.SessionId > sessionId)
            {
                CancelWork(e, "session_superseded");
                sessionId = e.SessionId;
            }
            switch (e.Kind)
            {
                case FusionEventKind.AudioPrepareCommand:
                    var candidate = Binding(e);
                    if (candidate is null) Log(e, SimulatedPlaybackKind.MissingAudio, clock, null, null, "unavailable_preparation");
                    else if (prepared.TryAdd(candidate.NodeKey, e)) Log(e, SimulatedPlaybackKind.Prepared, clock, candidate, null, "prepare_only");
                    break;
                case FusionEventKind.AudioPlayCommand:
                    if (!playDecisions.Add($"{e.SessionId}/{e.DecisionId}")) break;
                    var clip = Binding(e);
                    Stop(e, clip is null ? "missing_audio" : "replacement");
                    CancelPending(e, clip is null ? "missing_audio" : "superseded");
                    if (clip is null) Log(e, SimulatedPlaybackKind.MissingAudio, clock, null, null, "missing_invalid_or_mismatched_audio");
                    else
                    {
                        prepared.Remove(clip.NodeKey);
                        pending = new(e, clip, clock + startupDelay);
                        Log(e, SimulatedPlaybackKind.StartScheduled, clock, clip, 0, "scheduled");
                    }
                    break;
                case FusionEventKind.AudioStopCommand:
                    CancelWork(e, e.Reason);
                    break;
                case FusionEventKind.OptionWait:
                    CancelPending(e, "option_wait"); // Existing audio can naturally finish.
                    break;
                case FusionEventKind.DialogueEnd:
                    CancelWork(e, "dialogue_end");
                    closedSession = sessionId;
                    break;
                case FusionEventKind.CutTriggered:
                    break; // A boundary observation alone is never permission to start a target WAV.
            }
        }
        AdvanceTo(nowMs);
    }

    public void AdvanceTo(double nowMs)
    {
        ValidateNow(nowMs);
        if (pending is { } due && due.DueMs <= nowMs)
        {
            pending = null; active = new(due, due.DueMs);
            Log(due.Command, SimulatedPlaybackKind.Started, due.DueMs, due.Clip, 0, "simulated_start");
        }
        if (active is { } playing && playing.StartedMs + playing.Request.Clip.DurationMs <= nowMs)
        {
            var end = playing.StartedMs + playing.Request.Clip.DurationMs;
            Log(playing.Request.Command, SimulatedPlaybackKind.Completed, end, playing.Request.Clip,
                playing.Request.Clip.DurationMs, "natural_completion");
            active = null;
        }
        clock = nowMs;
    }
    private SimulatedAudioClip? Binding(FusionEvent e) => e.NodeKey is not null && catalog.TryGetValue(e.NodeKey, out var clip) &&
        clip.IsValid && clip.LineId == e.LineId && string.Equals(clip.AudioPath, e.AudioPath, StringComparison.OrdinalIgnoreCase) ? clip : null;
    private void Stop(FusionEvent cause, string reason)
    {
        if (active is not { } playing) return;
        Log(cause, SimulatedPlaybackKind.Stopped, clock, playing.Request.Clip,
            Math.Clamp(clock - playing.StartedMs, 0, playing.Request.Clip.DurationMs), reason);
        active = null;
    }
    private void CancelPending(FusionEvent cause, string reason)
    {
        if (pending is not { } cancelled) return;
        Log(cause, SimulatedPlaybackKind.Cancelled, clock, cancelled.Clip, 0, reason);
        pending = null;
    }
    private void CancelPrepared(FusionEvent cause, string reason)
    {
        foreach (var key in prepared.Keys) Log(cause, SimulatedPlaybackKind.Cancelled, clock, catalog[key], 0, reason);
        prepared.Clear();
    }
    private void CancelWork(FusionEvent cause, string reason)
    { Stop(cause, reason); CancelPending(cause, reason); CancelPrepared(cause, reason); }
    private void Log(FusionEvent cause, SimulatedPlaybackKind kind, double time, SimulatedAudioClip? clip, double? played, string reason) =>
        trace.Add(new($"sim:{++sequence}", cause.DecisionId, cause.SessionId, cause.ClipId, kind, time, cause.SourceMs,
            clip?.NodeKey ?? cause.NodeKey, clip?.LineId ?? cause.LineId, clip?.AudioPath ?? cause.AudioPath, clip?.DurationMs, played, reason));
    private void ValidateNow(double now)
    {
        if (!double.IsFinite(now) || now < 0 || now < clock) throw new ArgumentException("Simulation time must be finite and nondecreasing.");
    }
}
