using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record GateSample(string ClipId, double TimestampMs, bool Allow);
public sealed record GateLabel(string ClipId, double TimestampMs, bool Allow);
public sealed record GateMetrics(int Frames, int BlockedFrames, double? BlockedFrameRate, int LabeledFrames, double? ClassificationAccuracy);
public sealed record GateEnhancement(int InputCandidates, int SuppressedCandidates, double? SuppressionRate,
    EvaluationMetrics Before, EvaluationMetrics After, double? PrecisionDelta, double? RecallDelta, double? F1Delta);

public static class VisualRecognitionEvaluation
{
    public static string? AcceptedIdentityId(TextMatch match, double confidence) =>
        double.IsFinite(confidence) && confidence is >= .55 and <= 1 && match.Key is not null ? match.Id : null;
    /// <summary>
    /// Validate replay timestamps for any media source. Optional bounds are supplied
    /// by the caller (for example a benchmark fixture), never hard-coded by clip ID.
    /// </summary>
    public static double[] SelectReplayPTS(double[] pts, double? endExclusiveMs = null)
    {
        ArgumentNullException.ThrowIfNull(pts);
        if (pts.Any(t => !double.IsFinite(t) || t < 0) ||
            pts.Zip(pts.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new ArgumentException("Expected strictly increasing nonnegative original PTS.");
        if (endExclusiveMs is not null && (!double.IsFinite(endExclusiveMs.Value) || endExclusiveMs <= 0))
            throw new ArgumentOutOfRangeException(nameof(endExclusiveMs));
        return endExclusiveMs is null ? pts.ToArray() : pts.Where(t => t < endExclusiveMs).ToArray();
    }
    public static GateMetrics GateSummary(IReadOnlyList<GateSample> samples, IReadOnlyList<GateLabel> labels)
    {
        var scored = samples.Join(labels, s => (s.ClipId, s.TimestampMs), l => (l.ClipId, l.TimestampMs), (s, l) => s.Allow == l.Allow).ToArray();
        var blocked = samples.Count(s => !s.Allow);
        return new(samples.Count, blocked, samples.Count == 0 ? null : (double)blocked / samples.Count,
            scored.Length, scored.Length == 0 ? null : (double)scored.Count(v => v) / scored.Length);
    }
    public static EvaluationPrediction[] ApplyGate(IReadOnlyList<EvaluationPrediction> events, IReadOnlyList<GateSample> samples)
    {
        var clips = samples.GroupBy(s => s.ClipId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.TimestampMs).ToArray());
        return events.Where(e => clips.TryGetValue(e.ClipId, out var frames) &&
            frames.LastOrDefault(s => s.TimestampMs <= e.DecisionMs) is { Allow: true }).ToArray();
    }
    public static GateEnhancement Enhancement(IReadOnlyList<EvaluationPrediction> events, IReadOnlyList<GateSample> samples,
        IReadOnlyList<EvaluationTruth> truth, IReadOnlyList<EvaluationWindow> windows)
    {
        var kept = ApplyGate(events, samples);
        var before = BaselineEvaluation.Evaluate(events, truth, windows);
        var after = BaselineEvaluation.Evaluate(kept, truth, windows);
        return new(events.Count, events.Count - kept.Length, events.Count == 0 ? null : (double)(events.Count - kept.Length) / events.Count,
            before, after, after.Precision - before.Precision, after.Recall - before.Recall, after.F1 - before.F1);
    }
    public static VisualFrame Crop(VisualFrame f, double left, double top, double right, double bottom)
    {
        if (new[] { left, top, right, bottom }.Any(v => !double.IsFinite(v) || v < 0 || v > 1) || right <= left || bottom <= top)
            throw new ArgumentException("Invalid normalized crop.");
        var x = (int)Math.Floor(left * f.Width); var y = (int)Math.Floor(top * f.Height);
        var width = (int)Math.Ceiling(right * f.Width) - x; var height = (int)Math.Ceiling(bottom * f.Height) - y;
        var rgb = new byte[width * height * 3];
        for (var row = 0; row < height; row++) Array.Copy(f.Rgb, ((y + row) * f.Width + x) * 3, rgb, row * width * 3, width * 3);
        return new(width, height, rgb, f.TimestampSeconds);
    }
}
