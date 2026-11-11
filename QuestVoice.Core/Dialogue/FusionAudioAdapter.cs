namespace QuestVoice.Core;

public sealed record FusionAudioBatch(long SessionId, string ClipId, double DecisionMs, FusionResult Result);

public sealed class FusionAudioAdapter
{
    private readonly object gate = new();
    private readonly AudioManager manager;
    private readonly QuestLine[] lines;
    private readonly HashSet<(long Session, string Event)> delivered = [];
    private readonly Queue<(long Session, string Event)> deliveryOrder = new();
    private static long sequenceSource;
    public static long NextEventSequence() => Interlocked.Increment(ref sequenceSource);
    private long session, closedSession;
    private string? clip;
    private double lastDecision = -1, latestSource = -1;
    public FusionAudioAdapter(AudioManager manager, IReadOnlyList<QuestLine> lines)
    {
        ArgumentNullException.ThrowIfNull(manager); ArgumentNullException.ThrowIfNull(lines);
        if (lines.Any(l => l is null)) throw new ArgumentException("Null audio index entry.", nameof(lines));
        this.manager = manager; this.lines = lines.ToArray();
    }
    public Task ConsumeBatchAsync(FusionAudioBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.SessionId <= 0 || string.IsNullOrWhiteSpace(batch.ClipId) || !double.IsFinite(batch.DecisionMs) || batch.DecisionMs < 0)
            throw new ArgumentException("Fusion batch requires session, clip and original causal decision PTS.", nameof(batch));
        return ConsumeCore(batch.Result, batch, cancellationToken);
    }
    // Unscoped, event-free states are only safe when supplied synchronously by the current coordinator.
    public Task ConsumeAsync(FusionResult result, CancellationToken cancellationToken = default)
        => ConsumeCore(result, null, cancellationToken);
    private Task ConsumeCore(FusionResult result, FusionAudioBatch? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var events = result.Events.ToArray();
        if (!Enum.IsDefined(result.State) || events.Any(e => e is null || !Enum.IsDefined(e.Kind) || e.SessionId <= 0
            || string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.DecisionId) || string.IsNullOrWhiteSpace(e.ClipId)
            || !double.IsFinite(e.TimestampMs) || !double.IsFinite(e.SourceMs) || e.SourceMs < 0 || e.SourceMs > e.TimestampMs))
            throw new ArgumentException("Fusion audio events must have causal timestamps and unique session/event identity.", nameof(result));
        if (scope is not null && events.Any(e => e.SessionId != scope.SessionId || e.ClipId != scope.ClipId || e.TimestampMs > scope.DecisionMs))
            throw new ArgumentException("Fusion events must belong to the batch session, clip and decision PTS.", nameof(scope));
        var tasks = new List<Task>();
        lock (gate)
        {
            if (scope is not null)
            {
                if (scope.SessionId < session || scope.SessionId <= closedSession
                    || scope.SessionId == session && (scope.ClipId != clip || scope.DecisionMs < lastDecision)) return Task.CompletedTask;
                if (scope.SessionId > session)
                {
                    if (session > 0) manager.Stop(AudioStopReason.DialogueExit);
                    session = scope.SessionId; clip = scope.ClipId; lastDecision = -1; latestSource = -1;
                }
            }
            if (events.Length > 0 && !events.Any(e => e.SessionId > closedSession
                && (e.SessionId > session || e.SessionId == session && e.ClipId == clip && e.TimestampMs >= lastDecision))) return Task.CompletedTask;
            var blocked = result.State is FusionState.NoDialogue or FusionState.UnknownContext or FusionState.Recovering or FusionState.Finished;
            if (blocked) manager.Stop(result.State == FusionState.Recovering ? AudioStopReason.Correction
                : result.State is FusionState.NoDialogue or FusionState.Finished ? AudioStopReason.DialogueExit : AudioStopReason.UnknownContext);
            foreach (var e in events)
            {
                if (e.SessionId < session || e.SessionId <= closedSession || delivered.Contains((e.SessionId, e.EventId))) continue;
                if (e.SessionId > session)
                {
                    if (session > 0) manager.Stop(AudioStopReason.DialogueExit);
                    session = e.SessionId; clip = e.ClipId; lastDecision = -1; latestSource = -1;
                }
                if (e.ClipId != clip || e.TimestampMs < lastDecision) continue;
                var delivery = (e.SessionId, e.EventId); delivered.Add(delivery); deliveryOrder.Enqueue(delivery);
                while (deliveryOrder.Count > 4096) delivered.Remove(deliveryOrder.Dequeue());
                if (e.Kind is FusionEventKind.AudioPlayCommand or FusionEventKind.AudioPrepareCommand && e.SourceMs < latestSource) continue;
                lastDecision = e.TimestampMs;
                if (e.Kind == FusionEventKind.DialogueEnd)
                { manager.Stop(AudioStopReason.DialogueExit); closedSession = session; continue; }
                if (blocked) continue;
                var binding = Binding(e);
                switch (e.Kind)
                {
                    case FusionEventKind.AudioBoundaryStopCommand:
                        latestSource=Math.Max(latestSource,e.SourceMs);
                        // A confirmed replacement in this same batch owns the existing short fade.
                        if(!events.Any(x=>x.Kind==FusionEventKind.AudioPlayCommand && x.SessionId==session && x.ClipId==clip
                            && x.TimestampMs>=lastDecision && x.SourceMs>=latestSource && !delivered.Contains((x.SessionId,x.EventId))
                            && x.NodeKey!=manager.Snapshot.NodeId && x.NodeKey!=manager.Snapshot.PendingNodeId && Binding(x) is not null))
                            manager.StopAtBoundary();
                        break;
                    case FusionEventKind.AudioPrepareCommand:
                        if (binding is not null) tasks.Add(manager.PrepareAsync(Line(e, binding), cancellationToken));
                        break;
                    case FusionEventKind.AudioPlayCommand:
                        if (e.NodeKey is null) manager.Stop(AudioStopReason.UnknownContext);
                        else { latestSource = e.SourceMs; tasks.Add(manager.ConfirmAsync(Line(e, binding), cancellationToken)); }
                        break;
                    case FusionEventKind.CutTriggered:
                        if (e.NodeKey is null || !events.Any(other => other.DecisionId == e.DecisionId
                            && other.Kind is FusionEventKind.AudioPlayCommand or FusionEventKind.AudioStopCommand))
                            manager.Stop(AudioStopReason.UnknownContext);
                        break;
                    case FusionEventKind.AudioStopCommand:
                        if (e.NodeKey is not null && events.Any(other => other.Kind == FusionEventKind.CutTriggered && other.DecisionId == e.DecisionId && other.NodeKey == e.NodeKey))
                        { latestSource = e.SourceMs; tasks.Add(manager.ConfirmAsync(Line(e, null), cancellationToken)); }
                        else manager.Stop(e.Reason.Contains("correct", StringComparison.Ordinal) ? AudioStopReason.Correction : AudioStopReason.UnknownContext);
                        break;
                    case FusionEventKind.OptionWait:
                        manager.ChoiceWaiting(); break;
                }
            }
            if (scope is not null)
            {
                lastDecision = Math.Max(lastDecision, scope.DecisionMs);
                if (result.State == FusionState.Finished) closedSession = Math.Max(closedSession, scope.SessionId);
            }
        }
        // Every confirmation has invalidated older generations before any load is awaited.
        return Task.WhenAll(tasks);
    }
    private AudioLine Line(FusionEvent e, QuestLine? binding) => new(e.ClipId, e.SourceMs, e.NodeKey!,
        binding?.AudioPath, $"{e.SessionId}/{e.EventId}", NextEventSequence(),
        binding?.OptionNumber is int option ? "player_opt" + option : "default");
    private QuestLine? Binding(FusionEvent e)
    {
        if (e.NodeKey is null || e.LineId is null || e.AudioPath is null) return null;
        try
        {
            var path = Path.GetFullPath(e.AudioPath);
            var matches = lines.Where(l => l.NodeKey == e.NodeKey && l.Id == e.LineId && l.AudioStatus == "covered"
                && !string.IsNullOrWhiteSpace(l.Text) && l.AudioPath is not null
                && Path.GetFullPath(l.AudioPath).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (ArgumentException) { return null; }
    }
}
