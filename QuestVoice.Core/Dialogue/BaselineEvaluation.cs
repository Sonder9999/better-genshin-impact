namespace QuestVoice.Core;

public sealed record EvaluationPrediction(string ClipId, double DecisionMs, double OnsetMs);
public sealed record EvaluationTruth(string ClipId, double TimestampMs);
public sealed record EvaluationWindow(string ClipId, double StartMs, double EndMs);
public sealed record EvaluationMatch(string ClipId, double TruthMs, double DecisionMs, double OnsetMs, double LatencyMs);
public sealed record EvaluationMetrics(int PredictionCount, int PredictionsOutsideCoverage, int TruthCount,
    int TruePositives, int FalsePositives, int FalseNegatives, double ScoredDurationMs,
    double? Precision, double? Recall, double? F1, double? FalseDiscoveryRate, double? FalsePositivesPerMinute,
    int EarlyTriggers, double[] LatenciesMs, double? LatencyP50Ms, double? LatencyP90Ms,
    double? LatencyP95Ms, double? LatencyMaxMs, EvaluationMatch[] Matches);

public static class BaselineEvaluation
{
    /// <summary>Nearest truth owns each prediction; a truth can be matched only once. Windows are half-open.</summary>
    public static EvaluationMetrics Evaluate(IEnumerable<EvaluationPrediction> predictions,
        IEnumerable<EvaluationTruth> truth, IEnumerable<EvaluationWindow> coverage, double toleranceMs = 100)
    {
        if (!double.IsFinite(toleranceMs) || toleranceMs < 0) throw new ArgumentOutOfRangeException(nameof(toleranceMs));
        var windows = coverage.OrderBy(w => w.ClipId, StringComparer.Ordinal).ThenBy(w => w.StartMs).ToArray();
        for (var i = 0; i < windows.Length; i++)
        {
            var w = windows[i];
            if (!double.IsFinite(w.StartMs) || !double.IsFinite(w.EndMs) || w.StartMs < 0 || w.EndMs <= w.StartMs ||
                (i > 0 && windows[i - 1].ClipId == w.ClipId && windows[i - 1].EndMs > w.StartMs))
                throw new ArgumentException("Coverage must contain finite, non-overlapping half-open intervals.");
        }
        bool Covered(string clip, double time) => windows.Any(w => w.ClipId == clip && time >= w.StartMs && time < w.EndMs);
        var allPredictions = predictions.ToArray();
        var allTruth = truth.ToArray();
        if (allPredictions.Any(p => !double.IsFinite(p.DecisionMs) || !double.IsFinite(p.OnsetMs) || p.OnsetMs > p.DecisionMs) ||
            allTruth.Any(t => !double.IsFinite(t.TimestampMs))) throw new ArgumentException("Invalid event timestamps.");
        var scoredPredictions = allPredictions.Where(p => Covered(p.ClipId, p.DecisionMs)).OrderBy(p => p.DecisionMs).ToArray();
        var targets = allTruth.Where(t => Covered(t.ClipId, t.TimestampMs)).OrderBy(t => t.TimestampMs).ToArray();
        var used = new HashSet<int>();
        var matches = new List<EvaluationMatch>();
        foreach (var prediction in scoredPredictions)
        {
            var closest = -1;
            var distance = double.PositiveInfinity;
            for (var i = 0; i < targets.Length; i++)
            {
                if (targets[i].ClipId != prediction.ClipId) continue;
                var delta = Math.Abs(prediction.DecisionMs - targets[i].TimestampMs);
                if (delta < distance) { closest = i; distance = delta; }
            }
            if (closest < 0 || distance > toleranceMs || !used.Add(closest)) continue;
            var target = targets[closest];
            matches.Add(new(prediction.ClipId, target.TimestampMs, prediction.DecisionMs, prediction.OnsetMs,
                prediction.DecisionMs - target.TimestampMs));
        }
        var tp = matches.Count;
        var fp = scoredPredictions.Length - tp;
        var fn = targets.Length - tp;
        var duration = windows.Sum(w => w.EndMs - w.StartMs);
        double? precision = scoredPredictions.Length == 0 ? null : (double)tp / scoredPredictions.Length;
        double? recall = targets.Length == 0 ? null : (double)tp / targets.Length;
        double? f1 = targets.Length == 0 ? null : 2d * tp / (2 * tp + fp + fn);
        var latencies = matches.Select(m => m.LatencyMs).ToArray();
        return new(scoredPredictions.Length, allPredictions.Length - scoredPredictions.Length, targets.Length,
            tp, fp, fn, duration, precision, recall, f1, precision is null ? null : 1 - precision,
            duration == 0 ? null : fp * 60000d / duration, latencies.Count(d => d < 0), latencies,
            Percentile(latencies, .5), Percentile(latencies, .9), Percentile(latencies, .95),
            latencies.Length == 0 ? null : latencies.Max(), matches.ToArray());
    }

    public static double? Percentile(IEnumerable<double> values, double quantile)
    {
        if (!double.IsFinite(quantile) || quantile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(quantile));
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(quantile * sorted.Length) - 1)];
    }
}
