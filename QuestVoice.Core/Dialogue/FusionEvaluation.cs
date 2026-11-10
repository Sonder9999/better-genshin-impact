namespace QuestVoice.Core;

public sealed record FusionTruth(string ClipId, double TimestampMs, string? LineId = null, string? NodeKey = null);
public sealed record FusionPlaybackMatch(string ClipId, double TruthMs, double StartedMs, string? NodeKey,
    string? LineId, double AlignmentMs);
public sealed record FusionMetrics(EvaluationMetrics Cut, int PlaybackStarts, int CoveredPlaybackStarts,
    int MatchedPlaybackStarts, int WrongLineStarts, int UnfinishedLineSkips, int SkippedBeforeStart,
    int FalseCutInterruptions, double? FalseDetectionRate, double? MissedPlaybackRate, double? WrongLineRate,
    double? FalseCutInterruptionRate, double? PlaybackAlignmentP95Ms, double[] PlaybackAlignmentMs,
    FusionPlaybackMatch[] PlaybackMatches);

public static class FusionEvaluation
{
    public static FusionMetrics Evaluate(IEnumerable<FusionEvent> events, IEnumerable<SimulatedPlaybackEvent> playback,
        IEnumerable<FusionTruth> truth, IEnumerable<EvaluationWindow> coverage, double toleranceMs = 100)
    {
        ArgumentNullException.ThrowIfNull(events); ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(truth); ArgumentNullException.ThrowIfNull(coverage);
        var cuts = events.Where(e => e.Kind == FusionEventKind.CutTriggered).ToArray();
        var trace = playback.ToArray(); var labels = truth.ToArray(); var windows = coverage.ToArray();
        if (trace.Any(e => string.IsNullOrWhiteSpace(e.ClipId) || !double.IsFinite(e.TimestampMs) || !double.IsFinite(e.SourceMs) ||
            e.SourceMs < 0 || e.SourceMs > e.TimestampMs || e.PlayedMs is { } played && (!double.IsFinite(played) || played < 0)) ||
            labels.Any(t => string.IsNullOrWhiteSpace(t.ClipId) || !double.IsFinite(t.TimestampMs) || t.TimestampMs < 0))
            throw new ArgumentException("Invalid playback or truth timestamps.");
        var cutMetrics = BaselineEvaluation.Evaluate(cuts.Select(e => new EvaluationPrediction(e.ClipId, e.TimestampMs, e.SourceMs)),
            labels.Select(t => new EvaluationTruth(t.ClipId, t.TimestampMs)), windows, toleranceMs);
        bool Covered(string clip, double time) => windows.Any(w => w.ClipId == clip && time >= w.StartMs && time < w.EndMs);

        // Associate an actual audio start with its already matched cut decision, rather than
        // awarding recall to arbitrary audio near a truth time or to a prepare/play command.
        var usedCutEvents = new HashSet<(string, long, string)>();
        var owners = new Dictionary<(string Clip, long Session, string Decision), FusionTruth>();
        foreach (var match in cutMetrics.Matches)
        {
            var cut = cuts.First(e => e.ClipId == match.ClipId && e.TimestampMs == match.DecisionMs && e.SourceMs == match.OnsetMs &&
                !usedCutEvents.Contains((e.ClipId, e.SessionId, e.EventId)));
            usedCutEvents.Add((cut.ClipId, cut.SessionId, cut.EventId));
            owners.TryAdd(Decision(cut), labels.First(t => t.ClipId == match.ClipId && t.TimestampMs == match.TruthMs));
        }
        var starts = trace.Where(e => e.Kind == SimulatedPlaybackKind.Started).ToArray();
        var scoredStarts = starts.Where(e => Covered(e.ClipId, e.TimestampMs)).OrderBy(e => e.TimestampMs).ToArray();
        var usedTruth = new HashSet<(string, double)>(); var matches = new List<FusionPlaybackMatch>();
        var wrong = 0; var identityDenominator = 0;
        foreach (var start in scoredStarts)
        {
            if (!owners.TryGetValue(Decision(start), out var target)) continue;
            if (target.LineId is not null || target.NodeKey is not null) identityDenominator++;
            if (target.LineId is not null && start.LineId != target.LineId || target.NodeKey is not null && start.NodeKey != target.NodeKey)
            { wrong++; continue; }
            if (!usedTruth.Add((target.ClipId, target.TimestampMs))) continue;
            matches.Add(new(start.ClipId, target.TimestampMs, start.TimestampMs, start.NodeKey, start.LineId, start.TimestampMs - target.TimestampMs));
        }
        // A cut can stop unfinished audio even when its replacement WAV is unavailable.
        // Match decision identity so UI exits and semantic corrections remain excluded.
        var cutDecisions = cuts.Select(Decision).ToHashSet();
        var interruptions = trace.Where(e => e.Kind == SimulatedPlaybackKind.Stopped && cutDecisions.Contains(Decision(e)) &&
            e.DurationMs is { } duration && e.PlayedMs is { } played && played < duration).ToArray();
        var falseInterruptions = interruptions.Count(stop => Covered(stop.ClipId, stop.TimestampMs) &&
            cuts.Any(c => Decision(c) == Decision(stop) && Covered(c.ClipId, c.TimestampMs)) && !owners.ContainsKey(Decision(stop)));
        var alignment = matches.Select(m => m.AlignmentMs).ToArray();
        return new(cutMetrics, starts.Length, scoredStarts.Length, matches.Count, wrong, interruptions.Length,
            trace.Count(e => e.Kind == SimulatedPlaybackKind.Cancelled && e.Reason == "superseded"), falseInterruptions,
            cutMetrics.FalseDiscoveryRate, cutMetrics.TruthCount == 0 ? null : 1d - matches.Count / (double)cutMetrics.TruthCount,
            identityDenominator == 0 ? null : wrong / (double)identityDenominator,
            scoredStarts.Length == 0 ? null : falseInterruptions / (double)scoredStarts.Length,
            BaselineEvaluation.Percentile(alignment, .95), alignment, matches.ToArray());
    }
    private static (string Clip, long Session, string Decision) Decision(FusionEvent e) => (e.ClipId, e.SessionId, e.DecisionId);
    private static (string Clip, long Session, string Decision) Decision(SimulatedPlaybackEvent e) => (e.ClipId, e.SessionId, e.DecisionId);
}
