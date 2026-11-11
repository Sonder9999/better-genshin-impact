using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public enum TTSPriority { P0, P1 }
public sealed record TTSOutcome(string Path, bool CacheHit);
public sealed record TTSQueueSnapshot(string State, string? CurrentDialogue, TTSPriority? CurrentPriority, int P0Waiting, int P1Waiting, string? ActiveTask,
    string? CurrentText, string? CurrentLanguage, string? CurrentCharacter);
public sealed record TTSQueueEvent(DateTimeOffset Utc, string Event, string Key, string? TaskScope, TTSPriority Priority, string Detail);

/// <summary>One actual worker. P0 promotes duplicates and outranks all unstarted P1 work.</summary>
public sealed class TTSPriorityScheduler : IAsyncDisposable
{
    private sealed class Work(string key, SynthesisRequest synthesis, QuestAudioRequest? cacheRequest,
        TTSPriority priority, string? scope, Func<bool> valid, long order)
    {
        public readonly string Key = key;
        public readonly SynthesisRequest Synthesis = synthesis;
        public readonly QuestAudioRequest? CacheRequest = cacheRequest;
        public TTSPriority Priority = priority;
        public string? Scope = scope;
        public readonly Func<bool> Valid = valid;
        public readonly long Order = order;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<TTSOutcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started;
    }
    private readonly object gate = new();
    private readonly QuestAudioStore store;
    private readonly Func<string, ISpeechSynthesisProvider> provider;
    private readonly string ambientDirectory;
    private readonly Dictionary<string, Work> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private readonly System.Threading.Channels.Channel<TTSQueueEvent> events = System.Threading.Channels.Channel.CreateUnbounded<TTSQueueEvent>();
    private readonly Task eventWorker;
    private long order;
    private string? activeTask;
    private Work? current;
    private string state = "Idle";
    public event Action<TTSQueueEvent>? Recorded;
    public event Action<TTSOutcome, VoiceProfile, TTSPriority>? AudioReady;
    public TTSPriorityScheduler(QuestAudioStore store, Func<string, ISpeechSynthesisProvider> provider, string ambientDirectory)
    {
        this.store = store; this.provider = provider; this.ambientDirectory = ambientDirectory; Directory.CreateDirectory(ambientDirectory);
        eventWorker = Task.Run(async () => { await foreach (var ev in events.Reader.ReadAllAsync())
            { try { Recorded?.Invoke(ev); } catch (Exception) { /* Event consumers cannot interrupt model ownership. */ } } });
        worker = Task.Run(WorkerAsync);
    }
    public TTSQueueSnapshot Snapshot
    {
        get { lock (gate) return new(state, current?.CacheRequest?.Line.DialogueId ?? (current is null ? null : "NPC (session only)"), current?.Priority,
            pending.Values.Count(w => !w.Started && w.Priority == TTSPriority.P0), pending.Values.Count(w => !w.Started && w.Priority == TTSPriority.P1), activeTask,
            current?.Synthesis.Text, current?.Synthesis.TextLanguage, current?.Synthesis.Profile.CharacterId); }
    }
    public void SetActiveTask(string? scope)
    { lock (gate) { activeTask = scope; signal.Release(); } }
    public void Wake() => signal.Release();
    public Task<TTSOutcome> Enqueue(QuestAudioRequest request, TTSPriority priority, string? scope, Func<bool> stillValid, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var hit = store.Find(request);
        if (hit is not null && request.AssetsUnchanged && stillValid())
        { Record("CacheHit", request.CacheKey, scope, priority, "Validated disk WAV"); var outcome = new TTSOutcome(hit.Path, true);
            NotifyAudioReady(outcome, request.Line.Profile, priority); return Task.FromResult(outcome); }
        return Add(request.CacheKey, new(request.Line.Text, request.Line.TextLanguage, request.Line.Profile), request, priority, scope, stillValid, token);
    }
    public Task<TTSOutcome> EnqueueAmbient(string text, string textLanguage, VoiceProfile profile, CancellationToken token = default)
    {
        if (QuestLanguages.Normalize(profile.Language) != QuestLanguages.Normalize(textLanguage)) throw new ArgumentException("NPC target text language must equal VoiceProfile language.");
        var frozen = profile with { Parameters = new(profile.Parameters, StringComparer.Ordinal) }; var fingerprint = frozen.Fingerprint;
        // No fabricated quest IDs and no QuestAudio entry. Each ambient request belongs only to this Live session.
        return Add("ambient-" + Guid.NewGuid().ToString("N"), new(text, textLanguage, frozen), null, TTSPriority.P0, null,
            () => frozen.Fingerprint == fingerprint, token);
    }
    private Task<TTSOutcome> Add(string key, SynthesisRequest synthesis, QuestAudioRequest? request, TTSPriority priority,
        string? scope, Func<bool> valid, CancellationToken token)
    {
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(TTSPriorityScheduler));
            if (pending.TryGetValue(key, out var existing))
            {
                if (existing.Started && existing.Cancellation.IsCancellationRequested)
                    return RequeueAfterDrainAsync(existing.Completion.Task, () => Add(key, synthesis, request, priority, scope, valid, token), token);
                if (!existing.Started && existing.Cancellation.IsCancellationRequested)
                { pending.Remove(key); existing.Completion.TrySetCanceled(); }
                else
                {
                if (priority == TTSPriority.P0) { existing.Priority = TTSPriority.P0; existing.Scope = null; }
                Record("Deduplicated", key, scope, priority, "Shared cache key; P0 promoted when needed"); signal.Release();
                return existing.Completion.Task.WaitAsync(token);
                }
            }
            var work = new Work(key, synthesis, request, priority, scope, valid, ++order);
            pending.Add(key, work);
            if (token.CanBeCanceled)
            {
                var registration = token.Register(() => { work.Cancellation.Cancel(); signal.Release(); });
                _ = work.Completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }
            Record("Queued", key, scope, priority, request?.Line.DialogueId ?? "NPC"); signal.Release();
            return work.Completion.Task;
        }
    }
    private static async Task<TTSOutcome> RequeueAfterDrainAsync(Task<TTSOutcome> old, Func<Task<TTSOutcome>> requeue, CancellationToken token)
    {
        try { await old.WaitAsync(token).ConfigureAwait(false); }
        catch (Exception) { token.ThrowIfCancellationRequested(); }
        return await requeue().ConfigureAwait(false);
    }
    public void CancelBackground(string scope, bool includeInFlight = false)
    {
        lock (gate)
        {
            foreach (var work in pending.Values.Where(w => w.Scope == scope && w.Priority == TTSPriority.P1 && (!w.Started || includeInFlight)))
            { work.Cancellation.Cancel(); Record(work.Started ? "CancelAfterDrain" : "Paused", work.Key, work.Scope, work.Priority, "Completed disk cache retained"); }
            signal.Release();
        }
    }
    private async Task WorkerAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            Work? work = null;
            lock (gate)
            {
                foreach (var cancelled in pending.Values.Where(w => !w.Started && w.Cancellation.IsCancellationRequested).ToArray())
                { pending.Remove(cancelled.Key); cancelled.Completion.TrySetCanceled(); Record("Cancelled", cancelled.Key, cancelled.Scope, cancelled.Priority, "Not dispatched"); }
                var candidate = pending.Values.Where(w => !w.Started && (w.Priority == TTSPriority.P0 || w.Scope == activeTask))
                    .OrderBy(w => w.Priority).ThenBy(w => w.Order).FirstOrDefault();
                if (candidate is not null)
                {
                    try
                    {
                        if (provider(candidate.Synthesis.Profile.InstanceId).BackendUnknown) state = "BackendUnknown";
                        else { work = candidate; work.Started = true; current = work; state = work.Priority + " Synthesizing"; }
                    }
                    catch (Exception e)
                    { pending.Remove(candidate.Key); candidate.Completion.TrySetException(e); Record("Failed", candidate.Key, candidate.Scope, candidate.Priority, e.Message); }
                }
                else if (current is null) state = "Idle / Paused";
            }
            if (work is null)
            {
                try { await signal.WaitAsync(lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            Record("Started", work.Key, work.Scope, work.Priority, work.CacheRequest?.Line.DialogueId ?? "NPC");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(work.Cancellation.Token, lifetime.Token);
            TTSOutcome? completed = null; Exception? failure = null; bool cancelledResult = false;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!work.Valid()) throw new OperationCanceledException("Fingerprint changed before dispatch.");
                var instance = provider(work.Synthesis.Profile.InstanceId);
                if (work.CacheRequest is not null && (instance.InstanceFingerprint != work.CacheRequest.InstanceFingerprint || !work.CacheRequest.AssetsUnchanged))
                    throw new OperationCanceledException("Instance/profile fingerprint changed.");
                var hit = work.CacheRequest is null ? null : store.Find(work.CacheRequest);
                TTSOutcome outcome;
                if (hit is not null) outcome = new(hit.Path, true);
                else
                {
                    var response = await instance.SynthesizeAsync(work.Synthesis, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (!work.Valid()) throw new OperationCanceledException("Profile changed after full response.");
                    if (work.CacheRequest is not null)
                    {
                        var published = await store.PublishAsync(work.CacheRequest, response.CompleteWave, response.RequestMs, response.FirstByteMs,
                            work.Valid, linked.Token).ConfigureAwait(false);
                        outcome = new(published.Path, false);
                    }
                    else
                    {
                        var temporary = Path.Combine(ambientDirectory, work.Key + ".tmp");
                        await File.WriteAllBytesAsync(temporary, response.CompleteWave, linked.Token).ConfigureAwait(false);
                        if (!QuestAudioStore.Valid(WaveReader.Read(temporary))) throw new InvalidDataException("Invalid NPC WAV");
                        linked.Token.ThrowIfCancellationRequested();
                        var path = Path.ChangeExtension(temporary, ".wav"); File.Move(temporary, path); outcome = new(path, false);
                    }
                }
                completed = outcome; Record(outcome.CacheHit ? "CacheHit" : "Completed", work.Key, work.Scope, work.Priority, outcome.Path);
            }
            catch (OperationCanceledException) { cancelledResult = true; Record("Discarded", work.Key, work.Scope, work.Priority, "Cancelled or fingerprint changed; no Ready cache"); }
            catch (Exception e)
            { failure = e; Record(e is BackendUnknownException ? "BackendUnknown" : "Failed", work.Key, work.Scope, work.Priority, e.Message); }
            finally { lock (gate) { pending.Remove(work.Key); current = null; } }
            // Continuations can now safely requeue the same key after a cancelled in-flight request.
            if (cancelledResult) work.Completion.TrySetCanceled();
            else if (failure is not null) work.Completion.TrySetException(failure);
            else { work.Completion.TrySetResult(completed!); NotifyAudioReady(completed!, work.Synthesis.Profile, work.Priority); }
        }
        lock (gate) foreach (var work in pending.Values) work.Completion.TrySetCanceled();
    }
    private void Record(string ev, string key, string? scope, TTSPriority priority, string detail)
        => events.Writer.TryWrite(new(DateTimeOffset.UtcNow, ev, key, scope, priority, detail));
    private void NotifyAudioReady(TTSOutcome outcome, VoiceProfile profile, TTSPriority priority)
    { try { AudioReady?.Invoke(outcome, profile, priority); } catch (Exception) { /* Optional analysis cannot affect Ready or provider ownership. */ } }
    public async ValueTask DisposeAsync()
    { lifetime.Cancel(); signal.Release(); await worker.ConfigureAwait(false); events.Writer.TryComplete(); await eventWorker.ConfigureAwait(false); }
}
