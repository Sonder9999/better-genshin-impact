namespace QuestVoice.Core;

public sealed record PreSynthesisSnapshot(string Task, string State, string Start, int Total, int Ready, int CacheHits,
    int Queued, int Synthesizing, int Failed, int Skipped, IReadOnlyList<string> Diagnostics);
public sealed class PreSynthesisCoordinator
{
    private sealed class TaskState(QuestTaskScope scope)
    {
        public readonly QuestTaskScope Scope = scope;
        public readonly HashSet<string> Passed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Items = new(StringComparer.Ordinal);
        public readonly List<string> Diagnostics = [];
        public QuestGraph? Graph;
        public SpeechLineResolver? Resolver;
        public string? Current;
        public string State = "Paused", Start = "";
        public int Generation, Hits, Skipped;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, TaskState> tasks = new(StringComparer.Ordinal);
    private readonly TTSPriorityScheduler scheduler;
    private readonly Func<string, ISpeechSynthesisProvider> providers;
    private TaskState? active;
    public event Action? Changed;
    public PreSynthesisCoordinator(TTSPriorityScheduler scheduler, Func<string, ISpeechSynthesisProvider> providers)
    {
        this.scheduler = scheduler; this.providers = providers;
        scheduler.Recorded += ev =>
        {
            lock (gate)
            {
                if (ev.TaskScope is not null && tasks.TryGetValue(ev.TaskScope, out var task) && task.Items.GetValueOrDefault(ev.Key) == "Queued" && ev.Event == "Started")
                    task.Items[ev.Key] = "Synthesizing";
            }
            Changed?.Invoke();
        };
    }
    public PreSynthesisSnapshot? Snapshot
    { get { lock (gate) return active is null ? null : SnapshotOf(active); } }
    public IReadOnlyList<PreSynthesisSnapshot> TaskSnapshots
    { get { lock (gate) return tasks.Values.Select(SnapshotOf).ToArray(); } }
    private static PreSynthesisSnapshot SnapshotOf(TaskState t) => new(t.Scope.ToString(), t.State, t.Start, t.Items.Count,
        t.Items.Values.Count(s => s == "Ready"), t.Hits, t.Items.Values.Count(s => s == "Queued"),
        t.Items.Values.Count(s => s == "Synthesizing"), t.Items.Values.Count(s => s == "Failed"), t.Skipped, t.Diagnostics.ToArray());
    public void Start(QuestTaskScope scope, QuestGraph graph, SpeechLineResolver resolver, string? confirmedNode = null)
    {
        lock (gate)
        {
            if (active is not null && active.Scope.Key != scope.Key)
            { active.State = "Paused"; scheduler.CancelBackground(active.Scope.Key); }
            if (!tasks.TryGetValue(scope.Key, out var state)) tasks.Add(scope.Key, state = new(scope));
            state.Graph = graph; state.Resolver = resolver;
            if (confirmedNode is not null) { state.Current = confirmedNode; state.Passed.Add(confirmedNode); }
            active = state; Plan(state);
        }
        Changed?.Invoke();
    }
    public void Pause()
    {
        lock (gate) if (active is not null) { active.State = "Paused"; scheduler.SetActiveTask(null); scheduler.CancelBackground(active.Scope.Key); }
        Changed?.Invoke();
    }
    public void Resume()
    { lock (gate) { if (active is not null) Plan(active); } Changed?.Invoke(); }
    public void Stop()
    {
        lock (gate) if (active is not null)
        { active.State = "Stopped"; active.Generation++; scheduler.SetActiveTask(null); scheduler.CancelBackground(active.Scope.Key, includeInFlight: true); }
        Changed?.Invoke();
    }
    public void Confirm(QuestTaskScope scope, string nodeKey, IReadOnlyList<string>? recoveryPath = null)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(scope.Key, out var state)) tasks.Add(scope.Key, state = new(scope));
            if (state.Current == nodeKey) return;
            if (state.Current is not null) state.Passed.Add(state.Current);
            foreach (var passed in recoveryPath ?? []) state.Passed.Add(passed);
            state.Current = nodeKey; state.Passed.Add(nodeKey);
            if (ReferenceEquals(state, active) && state.State == "Active") Plan(state);
        }
        Changed?.Invoke();
    }
    private void Plan(TaskState state)
    {
        if (state.Graph is null || state.Resolver is null) return;
        scheduler.SetActiveTask(null); scheduler.CancelBackground(state.Scope.Key);
        var generation = ++state.Generation;
        state.Items.Clear(); state.Diagnostics.Clear(); state.Hits = state.Skipped = 0; state.State = "Planning";
        var enumeration = TaskDialogueEnumerator.Enumerate(state.Graph, state.Scope, state.Current, state.Passed);
        state.Start = enumeration.StartStatus; state.Skipped = enumeration.Skipped.Count;
        state.Diagnostics.AddRange(enumeration.Skipped.Select(s => s.NodeKey + ": " + s.Reason));
        foreach (var node in enumeration.Spoken)
        {
            var resolution = state.Resolver.Resolve(node.Key);
            if (resolution.Line is null) { state.Skipped++; state.Diagnostics.Add(node.Id + ": " + resolution.Status); continue; }
            if (resolution.Status.StartsWith("Fallback:", StringComparison.Ordinal))
                state.Diagnostics.Add(node.Id + ": " + resolution.Status);
            try
            {
                var instance = providers(resolution.Line.Profile.InstanceId);
                var request = QuestAudioRequest.Capture(resolution.Line, instance.InstanceFingerprint);
                state.Items[request.CacheKey] = "Queued";
                var resolverSnapshot = state.Resolver;
                bool Valid() => resolverSnapshot.Resolve(node.Key).Line is { } fresh && fresh.Text == request.Line.Text &&
                    fresh.Profile.Fingerprint == request.ProfileFingerprint;
                var task = scheduler.Enqueue(request, TTSPriority.P1, state.Scope.Key, Valid);
                _ = ObserveAsync(state, request.CacheKey, generation, task);
            }
            catch (Exception error) { state.Skipped++; state.Diagnostics.Add(node.Id + ": " + error.Message); }
        }
        scheduler.SetActiveTask(state.Scope.Key);
        state.State = state.Items.Count == 0 ? state.Skipped == 0 ? "Completed" : "Blocked / Skipped" : "Active";
        Finish(state);
    }
    private async Task ObserveAsync(TaskState state, string key, int generation, Task<TTSOutcome> task)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            lock (gate) if (state.Generation == generation) { state.Items[key] = "Ready"; if (result.CacheHit) state.Hits++; Finish(state); }
        }
        catch (OperationCanceledException) { lock (gate) if (state.Generation == generation) { state.Items[key] = "Paused / Cancelled"; Finish(state); } }
        catch (Exception e) { lock (gate) if (state.Generation == generation) { state.Items[key] = "Failed"; state.Diagnostics.Add(e.Message); Finish(state); } }
        Changed?.Invoke();
    }
    private static void Finish(TaskState state)
    {
        if (state.State == "Active" && state.Items.Values.All(s => s is "Ready" or "Failed" or "Paused / Cancelled"))
            state.State = state.Items.Values.Any(s => s == "Paused / Cancelled") ? "Needs recalibration" : state.Items.Values.Any(s => s == "Failed") ? "Failed" : "Completed";
    }
}
