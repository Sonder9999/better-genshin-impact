using System.Threading.Channels;

namespace QuestVoice.Core;

/// <summary>Bounded separate signal-analysis worker, never holds the provider's serial ownership.</summary>
public sealed class SpeechOnsetService : IAsyncDisposable
{
    private sealed record Job(string Key, string Path, VoiceProfile Profile, TaskCompletionSource<SpeechOnsetAnalysis> Completion);
    private readonly object gate = new();
    private readonly Dictionary<string, Task<SpeechOnsetAnalysis>> pending = new(StringComparer.Ordinal);
    private readonly Channel<Job> foreground = Channel.CreateBounded<Job>(4);
    private readonly Channel<Job> background = Channel.CreateBounded<Job>(16);
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private readonly Func<string, VoiceProfile, SpeechOnsetAnalysis> analyze;
    public event Action<string, SpeechOnsetAnalysis>? Analyzed;
    public SpeechOnsetService(Func<string, VoiceProfile, SpeechOnsetAnalysis>? analyze = null)
    { this.analyze = analyze ?? SpeechOnsetSidecar.Get; worker = Task.Run(WorkAsync); }
    public Task<SpeechOnsetAnalysis> Queue(string wavPath, VoiceProfile profile, TTSPriority priority)
    {
        var fingerprint = profile.PlaybackFingerprint;
        if (!profile.SpeechOnset.Enabled || !profile.SpeechOnset.IsValid)
            return Task.FromResult(SpeechOnsetAnalyzer.NoAdjustment(profile.SpeechOnset, fingerprint, profile.SpeechOnset.Enabled ? "InvalidPolicy" : "Disabled"));
        var key = wavPath + "|" + fingerprint;
        lock (gate)
        {
            if (pending.TryGetValue(key, out var existing)) return existing;
            var completion = new TaskCompletionSource<SpeechOnsetAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new Job(key, wavPath, profile, completion);
            if (lifetime.IsCancellationRequested || !(priority == TTSPriority.P0 ? foreground : background).Writer.TryWrite(job))
                return Task.FromResult(SpeechOnsetAnalyzer.NoAdjustment(profile.SpeechOnset, fingerprint, "AnalysisQueueBusy"));
            pending.Add(key, completion.Task); signal.Release(); return completion.Task;
        }
    }
    public async Task<SpeechOnsetAnalysis> ForPlaybackAsync(string path, VoiceProfile profile, CancellationToken token, int budgetMs = 35)
    {
        try { return await Queue(path, profile, TTSPriority.P0).WaitAsync(TimeSpan.FromMilliseconds(budgetMs), token).ConfigureAwait(false); }
        catch (TimeoutException) { return SpeechOnsetAnalyzer.NoAdjustment(profile.SpeechOnset, profile.PlaybackFingerprint, "P0AnalysisBudgetExceeded"); }
    }
    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                if (!foreground.Reader.TryRead(out var job) && !background.Reader.TryRead(out job)) continue;
                SpeechOnsetAnalysis analysis;
                try { analysis = analyze(job.Path, job.Profile); }
                catch (Exception e) { analysis = SpeechOnsetAnalyzer.NoAdjustment(job.Profile.SpeechOnset, job.Profile.PlaybackFingerprint, "AnalysisFailed:" + e.GetType().Name); }
                lock (gate) pending.Remove(job.Key);
                job.Completion.TrySetResult(analysis);
                try { Analyzed?.Invoke(job.Path, analysis); } catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (foreground.Reader.TryRead(out var job)) job.Completion.TrySetCanceled();
            while (background.Reader.TryRead(out var job)) job.Completion.TrySetCanceled();
            lock (gate) pending.Clear();
        }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await worker.ConfigureAwait(false); lifetime.Dispose(); signal.Dispose(); }
}
