using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record FusionCachedFrame(FusionObservation Observation, int GlyphCount,
    IReadOnlyList<FusionSemanticEvidence>? TextOnlySemantic = null, double GateMs = 0,
    double GlyphMs = 0, double OcrMs = 0, double TemplateMs = 0);
public sealed record FusionCachedCandidate(string ClipId, FusionVisualSignal Signal);
public sealed record FusionReplayFrameState(string ClipId, int FrameIndex, double TimestampMs,
    FusionState State, string? NodeKey, string[] PrefetchKeys, string Reason, double FusionMs);
public sealed record FusionReplayClip(string ClipId, int FrameCount, double FirstPtsMs,
    double LastPtsMs, double DevEndMs, string? ActiveAudioAtEnd, string? PendingAudioAtEnd);
public sealed record FusionReplayRun(FusionMode Mode, FusionReplayClip[] Clips, FusionReplayFrameState[] Frames,
    FusionEvent[] Decisions, SimulatedPlaybackEvent[] Playback, FusionOptions? Parameters = null)
{
    public int FrameCount => Frames.Length;
    public IReadOnlyList<string> FrameIds => Array.AsReadOnly(Frames.Select(f => $"{f.ClipId}/{f.FrameIndex}@{f.TimestampMs.ToString("R", CultureInfo.InvariantCulture)}").ToArray());
}

public sealed class FusionReplayAdapter
{
    private readonly string clip;
    private readonly double[] pts;
    private int next, previousGlyphs;
    public FusionReplayAdapter(string clipId, double[] originalPts)
    {
        ArgumentNullException.ThrowIfNull(originalPts);
        var selected = FusionReplay.SelectReplayPTS(originalPts);
        if (selected.Length == 0 || selected.Length != originalPts.Length) throw new ArgumentException("Adapter requires valid original replay PTS.");
        clip = clipId; pts = originalPts.ToArray();
    }
    public FusionObservation Append(FusionCachedFrame frame, IReadOnlyList<FusionCachedCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(candidates);
        var o = frame.Observation;
        if (o is null || o.ClipId != clip || next >= pts.Length || o.FrameIndex != next || Math.Abs(o.TimestampMs - pts[next]) > .000001 ||
            frame.GlyphCount < 0 || o.Visual.Count != 0 || Math.Abs(o.Gate.TimestampSeconds * 1000 - o.TimestampMs) > .000001 ||
            new[] { frame.GateMs, frame.GlyphMs, frame.OcrMs, frame.TemplateMs }.Any(v => !double.IsFinite(v) || v < 0) ||
            o.Semantic.Concat(frame.TextOnlySemantic ?? []).Any(e => !double.IsFinite(e.SourceMs) || !double.IsFinite(e.AvailableMs) ||
                e.SourceMs < 0 || e.AvailableMs < e.SourceMs || e.AvailableMs > o.TimestampMs))
            throw new InvalidDataException("Cached frame does not match the next original causal frame.");
        foreach (var candidate in candidates)
        {
            var s = candidate.Signal;
            var onset = OriginalIndex(pts, s.SourceMs); var returned = OriginalIndex(pts, s.AvailableMs);
            if (candidate.ClipId != clip || s.Kind != FusionVisualKind.PossibleClear || s.Method is not ("A" or "B" or "C") ||
                onset < 0 || returned != next || onset > next || s.SourceMs > s.AvailableMs)
                throw new InvalidDataException("Boundary candidate must be available now and refer to an original past frame.");
        }
        var signals = candidates.Select(c => c.Signal with
            { SourceMs = pts[OriginalIndex(pts, c.Signal.SourceMs)], AvailableMs = pts[next] }).ToList();
        // The cached fall/SSIM candidates are not labeled first-character detections. Only
        // a current glyph-presence rise supplies that additional observed evidence.
        if (frame.GlyphCount > 0 && previousGlyphs == 0)
            signals.Add(new("B", FusionVisualKind.NewLineStarted, $"glyph/{clip}/{next}", o.TimestampMs, o.TimestampMs, 1));
        previousGlyphs = frame.GlyphCount; next++;
        return o with { Visual = signals.AsReadOnly(), Semantic = Array.AsReadOnly(o.Semantic.ToArray()) };
    }
    public void Complete()
    { if (next != pts.Length) throw new InvalidDataException($"Incomplete replay: {next}/{pts.Length} original frames."); }
    internal static int OriginalIndex(double[] timestamps, double value)
    {
        if (!double.IsFinite(value)) return -1;
        var index = Array.BinarySearch(timestamps, value);
        if (index >= 0) return index;
        var insertion = ~index;
        if (insertion < timestamps.Length && Math.Abs(timestamps[insertion] - value) <= .000001) return insertion;
        return insertion > 0 && Math.Abs(timestamps[insertion - 1] - value) <= .000001 ? insertion - 1 : -1;
    }
}
public static class FusionReplay
{
    public static double[] SelectReplayPTS(double[] originalPTS, double? endExclusiveMs = null) =>
        VisualRecognitionEvaluation.SelectReplayPTS(originalPTS, endExclusiveMs);
    public static FusionReplayRun Run(QuestGraph graph, IEnumerable<FusionCachedFrame> frames,
        IEnumerable<FusionCachedCandidate> candidates, IReadOnlyDictionary<string, double[]> originalPts,
        FusionOptions? options = null, IReadOnlyList<SimulatedAudioClip>? audioClips = null)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(candidates); ArgumentNullException.ThrowIfNull(originalPts);
        var input = frames.ToArray(); var boundaries = candidates.ToArray(); var config = options ?? new(); var audio = audioClips ?? [];
        var groups = input.GroupBy(f => f.Observation.ClipId).ToArray();
        if (groups.Length == 0 || groups.Any(g => !originalPts.ContainsKey(g.Key)) || originalPts.Keys.Any(k => !groups.Any(g => g.Key == k)) ||
            boundaries.Any(c => !originalPts.ContainsKey(c.ClipId))) throw new InvalidDataException("Replay inputs and original PTS clips differ.");
        foreach (var c in boundaries)
        {
            var timestamps = originalPts[c.ClipId];
            var source = FusionReplayAdapter.OriginalIndex(timestamps, c.Signal.SourceMs);
            var returned = FusionReplayAdapter.OriginalIndex(timestamps, c.Signal.AvailableMs);
            if (source < 0 || returned < source) throw new InvalidDataException("Candidate PTS is outside original Dev frames.");
        }
        var output = new List<FusionEvent>(); var playback = new List<SimulatedPlaybackEvent>();
        var states = new List<FusionReplayFrameState>(); var clips = new List<FusionReplayClip>();
        foreach (var group in groups)
        {
            var timestamps = originalPts[group.Key]; var adapter = new FusionReplayAdapter(group.Key, timestamps);
            var engine = new FusionEngine(graph, config, audio); var player = new SimulatedPlayer(audio);
            var deliveries = boundaries.Where(c => c.ClipId == group.Key)
                .GroupBy(c => FusionReplayAdapter.OriginalIndex(timestamps, c.Signal.AvailableMs)).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var cached in group)
            {
                var frame = config.Mode == FusionMode.NoVision
                    ? cached with { Observation = cached.Observation with { Semantic = cached.TextOnlySemantic ?? [] } } : cached;
                var observation = adapter.Append(frame, deliveries.GetValueOrDefault(frame.Observation.FrameIndex) ?? []);
                var start = Stopwatch.GetTimestamp(); var result = engine.Observe(observation);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                output.AddRange(result.Events); player.Consume(result.Events, observation.TimestampMs);
                states.Add(new(group.Key, observation.FrameIndex, observation.TimestampMs, result.State, result.CurrentNodeKey,
                    result.Prefetch.Select(l => l.Key).ToArray(), result.Reason, elapsed));
            }
            adapter.Complete();
            var end = group.Key == "S1" ? 7724.667 : 45000d;
            player.AdvanceTo(end); playback.AddRange(player.Trace);
            clips.Add(new(group.Key, timestamps.Length, timestamps[0], timestamps[^1], end, player.ActiveNodeKey, player.PendingNodeKey));
        }
        return new(config.Mode, clips.ToArray(), states.ToArray(), output.ToArray(), playback.ToArray(), config);
    }
}
