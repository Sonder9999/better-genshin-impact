using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed class AudioManager : IDisposable
{
    private sealed class Session(AudioLine line, long generation, double receivedMs, double durationMs, int totalFrames)
    {
        public readonly AudioLine Line = line;
        public readonly long Generation = generation;
        public readonly double ReceivedMs = receivedMs, DurationMs = durationMs;
        public readonly int TotalFrames = totalFrames;
        public int RenderedFrames;
        public bool RenderStarted, OutputFinished;
        public double? OutputStartedMs;
    }
    private sealed record Playing(PCMWave Wave, Session Session);
    private sealed class FadeTail(PCMWave wave, Session session, int firstFrame, int frames)
    {
        public readonly PCMWave Wave = wave;
        public readonly Session Session = session;
        public readonly int FirstFrame = firstFrame, Frames = frames;
        public int Elapsed;
    }
    private sealed class Request(AudioLine line, long generation, double receivedMs, CancellationTokenSource cancellation)
    {
        public readonly AudioLine Line = line;
        public readonly long Generation = generation;
        public readonly double ReceivedMs = receivedMs;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public Task Task = Task.CompletedTask;
    }
    private readonly object gate = new();
    private readonly IAudioOutput output;
    private readonly IWaveLoader loader;
    private readonly IAudioClock clock;
    private readonly AudioOptions options;
    private readonly AudioFormat format;
    private readonly AudioWaveCache cache;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<AudioLogEntry> logs = new();
    private readonly Dictionary<string, Request> prepared = new(StringComparer.Ordinal);
    private readonly HashSet<(string Clip, string Event)> delivered = [];
    private readonly Queue<(string Clip, string Event)> deliveredOrder = new();
    private long generation, lastSequence, controlVersion;
    private bool disposed, paused, deviceFailed;
    private AudioState state = AudioState.Idle;
    private Playing? active;
    private FadeTail? tail;
    private Session? lastSession;
    private AudioLine? lastLine;
    private Request? pending;

    public AudioSnapshot Snapshot
    {
        get
        {
            lock (gate) return new(state, generation, active?.Session.Line.NodeId ?? (HasPendingOutput() ? lastSession!.Line.NodeId : null), pending?.Line.NodeId,
                lastSession?.RenderedFrames * 1000d / format.SampleRate ?? 0, Played(lastSession), lastSession?.DurationMs,
                cache.Bytes, prepared.Count, (active?.Wave.ByteLength ?? 0) + (tail?.Wave.ByteLength ?? 0));
        }
    }
    public IReadOnlyList<AudioLogEntry> Logs { get { lock (gate) return logs.ToArray(); } }
    public AudioManager(IAudioOutput? output = null, IWaveLoader? loader = null,
        IAudioClock? clock = null, AudioOptions? options = null)
    {
        this.options = options ?? new();
        format = this.options.OutputFormat ?? new(48000, 2);
        PCMWave.ValidateFormat(format);
        if (!double.IsFinite(this.options.FadeOutMs) || this.options.FadeOutMs is < 0 or > 30
            || this.options.CacheBudgetBytes < 0 || this.options.MaxDecodedBytes <= 0
            || this.options.MaxPrefetch <= 0 || this.options.MaxLogEntries <= 0)
            throw new ArgumentException("Invalid audio budgets or fade duration.", nameof(options));
        this.output = output ?? new NullAudioOutput(); this.loader = loader ?? new WaveFileLoader();
        this.clock = clock ?? new StopwatchAudioClock(); cache = new(this.options.CacheBudgetBytes);
        try { this.output.Initialize(format, RenderOutput, ObserveOutput); }
        catch (Exception e) { deviceFailed = true; state = AudioState.Failed; Log(null, 0, "error", "device-initialize: " + e.Message); }
    }

    // State invalidation is synchronous; file I/O starts after the returned task yields.
    public Task ConfirmAsync(AudioLine line, CancellationToken cancellationToken = default)
    {
        Validate(line);
        List<CancellationTokenSource> cancelled;
        Request? request;
        long epoch, control; bool pauseOutput;
        lock (gate)
        {
            RequireAlive();
            if (line.EventSequence <= lastSequence || delivered.Contains((line.ClipId, line.EventId)))
            { lastSequence = Math.Max(lastSequence, line.EventSequence); Log(line, generation, "discard", "stale-event"); return Task.CompletedTask; }
            lastSequence = line.EventSequence;
            Remember(line);
            if (lastLine is not null && lastLine.ClipId == line.ClipId && lastLine.NodeId == line.NodeId && lastLine.Variant == line.Variant)
            { Log(line, generation, "duplicate", "same-node"); return Task.CompletedTask; }
            cancelled = CancelWork("superseded");
            if (tail is not null) Log(tail.Session.Line, tail.Session.Generation, "stop", "tail-superseded", tail.Session);
            tail = null;
            if (active is not null)
            {
                var remaining = active.Wave.FrameCount - active.Session.RenderedFrames;
                var fadeFrames = Math.Min(remaining, (int)Math.Ceiling(format.SampleRate * options.FadeOutMs / 1000));
                if (!paused && !deviceFailed && line.WavePath is not null && !cancellationToken.IsCancellationRequested && fadeFrames > 0)
                {
                    tail = new(active.Wave, active.Session, active.Session.RenderedFrames, fadeFrames);
                    Log(active.Session.Line, active.Session.Generation, "fade", "replacement", active.Session);
                }
                else Log(active.Session.Line, active.Session.Generation, "stop", "replacement", active.Session);
            }
            else if (HasPendingOutput()) Log(lastSession!.Line, lastSession.Generation, "stop", "queued-replacement", lastSession);
            active = null; lastSession = null; lastLine = line; epoch = ++generation;
            control = ++controlVersion; pauseOutput = paused;
            if (line.WavePath is null || deviceFailed)
            {
                state = AudioState.Failed; request = null;
                Log(line, epoch, "error", deviceFailed ? "device-unavailable" : "missing-source");
            }
            else
            {
                var receivedMs = clock.NowMs;
                Log(line, epoch, "prepare", "confirmed-load");
                var ready = cancellationToken.IsCancellationRequested ? null : cache.TryGetReady(line, format, options.MaxDecodedBytes);
                if (ready is not null)
                {
                    ready = ApplyPlaybackWindow(line, epoch, ready);
                    var session = new Session(line, epoch, receivedMs, ready.DurationMs, ready.FrameCount);
                    active = new(ready, session); lastSession = session; state = paused ? AudioState.Paused : AudioState.Playing;
                    request = null; Log(line, epoch, "ready", "cached-before-flush", session, loadMs: clock.NowMs - receivedMs);
                    if(ready.IsSilent)Log(line,epoch,"silent","all-zero-pcm",session);
                }
                else
                {
                    request = new(line, epoch, receivedMs, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken));
                    pending = request; state = AudioState.Loading;
                }
            }
        }
        Cancel(cancelled);
        Device(() => output.Flush(epoch), epoch);
        Device(() => { if (pauseOutput) output.Pause(control); else output.Resume(control); }, epoch);
        return request is null ? Task.CompletedTask : LoadConfirmedAsync(request);
    }

    public Task PrepareAsync(AudioLine candidate, CancellationToken cancellationToken = default)
    {
        Validate(candidate);
        Request request;
        CancellationTokenSource? evicted = null;
        lock (gate)
        {
            RequireAlive();
            if (candidate.WavePath is null || deviceFailed)
            { Log(candidate, generation, "cancel", "unavailable-candidate"); return Task.CompletedTask; }
            var key = CandidateKey(candidate);
            if (prepared.TryGetValue(key, out var existing)) return existing.Task;
            if (prepared.Count >= options.MaxPrefetch)
            {
                var old = prepared.First(); prepared.Remove(old.Key); evicted = old.Value.Cancellation;
                Log(old.Value.Line, old.Value.Generation, "cancel", "prefetch-budget");
            }
            request = new(candidate, generation, clock.NowMs, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken));
            prepared.Add(key, request); Log(candidate, generation, "prepare", "candidate-only");
            request.Task = LoadCandidateAsync(key, request);
        }
        if (evicted is not null) Cancel([evicted]);
        return request.Task;
    }

    public void ChoiceWaiting()
    {
        lock (gate) { RequireAlive(); Log(lastLine, generation, "choice", "hold-current"); }
    }
    public void StopAtBoundary()
    {
        List<CancellationTokenSource> cancelled;long epoch,control;bool pauseOutput;
        lock(gate)
        {
            RequireAlive();cancelled=CancelWork("confirmed-line-boundary");tail=null;
            if(active is not null)
            {
                var frames=Math.Min(active.Wave.FrameCount-active.Session.RenderedFrames,(int)Math.Ceiling(format.SampleRate*options.FadeOutMs/1000));
                if(!paused && !deviceFailed && frames>0)
                {
                    tail=new(active.Wave,active.Session,active.Session.RenderedFrames,frames);
                    Log(active.Session.Line,active.Session.Generation,"fade","confirmed-line-boundary",active.Session);
                }
                else Log(active.Session.Line,active.Session.Generation,"stop","confirmed-line-boundary",active.Session);
            }
            active=null;lastSession=null;lastLine=null;pauseOutput=paused;
            epoch=++generation;control=++controlVersion;state=deviceFailed?AudioState.Failed:paused?AudioState.Paused:AudioState.Idle;
        }
        Cancel(cancelled);Device(()=>output.Flush(epoch),epoch);
        Device(()=>{if(pauseOutput)output.Pause(control);else output.Resume(control);},epoch);
    }
    public void Stop(AudioStopReason reason = AudioStopReason.UserStop)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentException("Invalid stop reason.", nameof(reason));
        List<CancellationTokenSource> cancelled; long epoch, control;
        lock (gate)
        {
            RequireAlive(); cancelled = CancelWork(reason.ToString());
            if (active is not null) Log(active.Session.Line, active.Session.Generation, "stop", reason.ToString(), active.Session);
            else if (HasPendingOutput()) Log(lastSession!.Line, lastSession.Generation, "stop", reason.ToString(), lastSession);
            if (tail is not null) Log(tail.Session.Line, tail.Session.Generation, "stop", reason.ToString(), tail.Session);
            active = null; tail = null; lastSession = null; lastLine = null; paused = false; control = ++controlVersion;
            cache.Clear(); epoch = ++generation; state = deviceFailed ? AudioState.Failed : AudioState.Idle;
        }
        Cancel(cancelled); Device(() => output.Flush(epoch), epoch);
        Device(() => output.Resume(control), epoch);
    }
    public void Pause()
    {
        long epoch, control;
        lock (gate)
        {
            RequireAlive(); if (paused || active is null && pending is null && !HasPendingOutput()) return;
            paused = true; if (active is not null || HasPendingOutput()) state = AudioState.Paused;
            epoch = generation; control = ++controlVersion; Log(lastLine, epoch, "pause", "current-generation", lastSession);
        }
        Device(() => output.Pause(control), epoch);
    }
    public void Resume()
    {
        long epoch, control;
        lock (gate)
        {
            RequireAlive(); if (!paused) return; paused = false;
            state = deviceFailed || state == AudioState.Failed ? AudioState.Failed : active is not null || HasPendingOutput() ? AudioState.Playing : pending is not null ? AudioState.Loading : AudioState.Idle;
            epoch = generation; control = ++controlVersion; Log(lastLine, epoch, "resume", "current-generation", lastSession);
        }
        Device(() => output.Resume(control), epoch);
    }

    public AudioRenderResult Render(Span<float> samples) => RenderCore(samples, null);
    private AudioRenderResult RenderOutput(Span<float> samples) => RenderCore(samples, (output as IGenerationAudioOutput)?.RenderingGeneration);
    private AudioRenderResult RenderCore(Span<float> samples, long? expectedGeneration)
    {
        if (samples.Length % format.Channels != 0) throw new ArgumentException("Render buffer must contain complete sample frames.", nameof(samples));
        samples.Clear();
        lock (gate)
        {
            if (disposed || deviceFailed || paused || samples.Length == 0 || expectedGeneration.HasValue && expectedGeneration.Value != generation)
                return new(generation, null, 0, 0, false);
            var playing = active;
            var count = playing is null ? 0 : Math.Min(samples.Length / format.Channels, playing.Wave.FrameCount - playing.Session.RenderedFrames);
            var completed = false;
            if (playing is not null && count > 0)
            {
                playing.Wave.Samples.Span.Slice(playing.Session.RenderedFrames * format.Channels, count * format.Channels).CopyTo(samples);
                if (!playing.Session.RenderStarted)
                { playing.Session.RenderStarted = true; Log(playing.Session.Line, generation, "render-start", "PCM-generated", playing.Session); }
                playing.Session.RenderedFrames += count;
                completed = playing.Session.RenderedFrames == playing.Wave.FrameCount;
                if (completed)
                { Log(playing.Session.Line, generation, "render-finish", "PCM-generated", playing.Session); active = null; state = OutputClock().HasValue ? AudioState.Playing : AudioState.Idle; }
            }
            var fading = tail;
            var tailCount = fading is null ? 0 : Math.Min(samples.Length / format.Channels, fading.Frames - fading.Elapsed);
            if (fading is not null)
            {
                for (var i = 0; i < tailCount; i++)
                {
                    var gain = 1f - (fading.Elapsed + i + 1f) / fading.Frames;
                    for (var channel = 0; channel < format.Channels; channel++)
                        samples[i * format.Channels + channel] += fading.Wave.Samples.Span[(fading.FirstFrame + fading.Elapsed + i) * format.Channels + channel] * gain;
                }
                fading.Elapsed += tailCount;
                if (fading.Elapsed == fading.Frames) { Log(fading.Session.Line, fading.Session.Generation, "fade-end", "PCM-generated", fading.Session); tail = null; }
                if (count > 0 && tailCount > 0)
                    Log(playing!.Session.Line, generation, "render-overlap", "bounded-tail", playing.Session,
                        overlapMs: Math.Min(count, tailCount) * 1000d / format.SampleRate);
            }
            for (var i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i], -1, 1);
            return new(generation, count > 0 ? playing!.Session.Generation : null, count, tailCount, completed);
        }
    }

    private async Task LoadConfirmedAsync(Request request)
    {
        try
        {
            await Task.Yield();
            request.Cancellation.Token.ThrowIfCancellationRequested();
            var wave = await cache.LoadAsync(request.Line, format, loader, options.MaxDecodedBytes, request.Cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed || request.Generation != generation || request.Cancellation.IsCancellationRequested)
                { Log(request.Line, request.Generation, "discard", "stale-load"); return; }
                wave = ApplyPlaybackWindow(request.Line, request.Generation, wave);
                var session = new Session(request.Line, request.Generation, request.ReceivedMs, wave.DurationMs, wave.FrameCount);
                active = new(wave, session); lastSession = session; pending = null;
                state = paused ? AudioState.Paused : AudioState.Playing;
                Log(request.Line, generation, "ready", "decoded", session, loadMs: clock.NowMs - request.ReceivedMs);
                if(wave.IsSilent)Log(request.Line,generation,"silent","all-zero-pcm",session);
            }
        }
        catch (Exception e)
        {
            long epoch = request.Generation; bool flush = false;
            lock (gate)
            {
                if (disposed || request.Generation != generation) Log(request.Line, request.Generation, "discard", "stale-load: " + e.GetType().Name);
                else
                {
                    pending = null; active = null; tail = null; lastSession = null;
                    state = e is OperationCanceledException ? AudioState.Idle : AudioState.Failed;
                    Log(request.Line, epoch, e is OperationCanceledException ? "cancel" : "error", e is OperationCanceledException ? "load-cancelled" : "load-failed: " + e.Message);
                    flush = true;
                }
            }
            if (flush) Device(() => output.Flush(epoch), epoch);
        }
        finally { request.Cancellation.Dispose(); }
    }
    private async Task LoadCandidateAsync(string key, Request request)
    {
        try
        {
            await Task.Yield(); request.Cancellation.Token.ThrowIfCancellationRequested();
            var wave = await cache.LoadAsync(request.Line, format, loader, options.MaxDecodedBytes, request.Cancellation.Token).ConfigureAwait(false);
            lock (gate)
                if (!disposed && prepared.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                    Log(request.Line, request.Generation, "prepared", "candidate-only", durationMs: wave.DurationMs, loadMs: clock.NowMs - request.ReceivedMs);
        }
        catch (Exception e)
        {
            lock (gate) Log(request.Line, request.Generation, "discard", "candidate: " + e.GetType().Name);
        }
        finally
        {
            lock (gate)
                if (prepared.TryGetValue(key, out var current) && ReferenceEquals(current, request)) prepared.Remove(key);
            request.Cancellation.Dispose();
        }
    }

    private void ObserveOutput(AudioOutputEvent e)
    {
        List<CancellationTokenSource>? cancelled = null; long epoch = 0;
        lock (gate)
        {
            if (disposed || e.Generation != generation || !double.IsFinite(e.OutputClockMs) || !double.IsFinite(e.ObservedWallMs)) return;
            if (e.Command == "error")
            {
                var line = lastLine; var session = lastSession;
                Log(line, generation, "error", "device: " + e.Error, session);
                cancelled = CancelWork("device-error"); active = null; tail = null; lastSession = null; cache.Clear();
                deviceFailed = true; state = AudioState.Failed; epoch = ++generation;
            }
            else if (lastSession is Session session && session.Generation == generation)
            {
                if (e.Command == "start" && session.OutputStartedMs is null)
                {
                    session.OutputStartedMs = e.OutputClockMs;
                    Log(session.Line, generation, "start", "device-sample-observed", session, e.OutputClockMs,
                        Math.Max(0, e.ObservedWallMs - session.ReceivedMs), e.ObservedWallMs);
                }
                if (e.Command == "finish" && !session.OutputFinished && session.OutputStartedMs.HasValue && session.RenderedFrames == session.TotalFrames)
                {
                    session.OutputFinished = true;
                    state = paused ? AudioState.Paused : AudioState.Idle;
                    Log(session.Line, generation, "finish", "device-sample-observed", session, e.OutputClockMs, wallMs: e.ObservedWallMs);
                }
            }
        }
        if (cancelled is not null) { Cancel(cancelled); try { output.Flush(epoch); } catch (Exception) { } }
    }

    private List<CancellationTokenSource> CancelWork(string reason)
    {
        var result = new List<CancellationTokenSource>();
        if (pending is not null)
        { result.Add(pending.Cancellation); Log(pending.Line, pending.Generation, "cancel", reason); pending = null; }
        foreach (var request in prepared.Values)
        { result.Add(request.Cancellation); Log(request.Line, request.Generation, "cancel", reason); }
        prepared.Clear(); return result;
    }
    private static void Cancel(IEnumerable<CancellationTokenSource> sources)
    {
        foreach (var source in sources)
            try { source.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void Device(Action action, long expected)
    {
        lock (gate) if (disposed || deviceFailed || expected != generation) return;
        try { action(); }
        catch (Exception e) { ObserveOutput(new(expected, "error", OutputClock() ?? 0, clock.NowMs, e.Message)); }
    }
    private double? OutputClock()
    {
        try { return output.OutputClockMs; } catch (Exception) { return null; }
    }
    private double? Played(Session? session)
    {
        var now = OutputClock();
        return session?.OutputStartedMs is double start && now.HasValue ? Math.Clamp(now.Value - start, 0, session.DurationMs) : null;
    }
    private bool HasPendingOutput() => lastSession is not null && !lastSession.OutputFinished
        && lastSession.RenderedFrames == lastSession.TotalFrames && OutputClock().HasValue;
    private void Log(AudioLine? line, long epoch, string command, string? reason, Session? session = null,
        double? outputMs = null, double? latencyMs = null, double? wallMs = null, double? durationMs = null, double? loadMs = null,
        double? overlapMs = null)
    {
        logs.Enqueue(new(line?.ClipId ?? "", line?.SourcePtsMs ?? 0, line?.NodeId, line?.WavePath,
            line?.EventId ?? "", epoch, command, wallMs ?? clock.NowMs, outputMs ?? OutputClock(), latencyMs,
            Played(session), durationMs ?? session?.DurationMs, reason, loadMs, overlapMs));
        while (logs.Count > options.MaxLogEntries) logs.Dequeue();
    }
    private static string CandidateKey(AudioLine line) => string.Join('\u001f', line.ClipId, line.NodeId, line.Variant, line.WavePath);
    private PCMWave ApplyPlaybackWindow(AudioLine line, long epoch, PCMWave complete)
    {
        var window = AudioPlaybackWindow.Apply(complete, line.AudioStartMs);
        logs.Enqueue(new(line.ClipId, line.SourcePtsMs, line.NodeId, line.WavePath, line.EventId, epoch, "playback-window",
            clock.NowMs, OutputClock(), null, null, window.Wave.DurationMs, window.SkipReason,
            SuggestedAudioStartMs: window.SuggestedAudioStartMs, AppliedAudioStartMs: window.AppliedAudioStartMs, PlaybackSkipReason: window.SkipReason));
        while (logs.Count > options.MaxLogEntries) logs.Dequeue();
        return window.Wave;
    }
    private void Remember(AudioLine line)
    {
        var key = (line.ClipId, line.EventId); delivered.Add(key); deliveredOrder.Enqueue(key);
        while (deliveredOrder.Count > options.MaxLogEntries) delivered.Remove(deliveredOrder.Dequeue());
    }
    private static void Validate(AudioLine line)
    {
        if (line is null || string.IsNullOrWhiteSpace(line.ClipId) || string.IsNullOrWhiteSpace(line.NodeId)
            || string.IsNullOrWhiteSpace(line.EventId) || string.IsNullOrWhiteSpace(line.Variant)
            || !double.IsFinite(line.SourcePtsMs) || line.SourcePtsMs < 0 || line.EventSequence <= 0)
            throw new ArgumentException("Audio line requires reliable node, unique event, finite source PTS and positive sequence.", nameof(line));
    }
    private void RequireAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        List<CancellationTokenSource> cancelled; long epoch;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; cancelled = CancelWork("dispose"); epoch = ++generation;
            active = null; tail = null; lastSession = null; lastLine = null; paused = false; cache.Clear(); state = AudioState.Disposed;
        }
        Cancel(cancelled); lifetime.Cancel();
        try { output.Flush(epoch); } catch (Exception) { }
        output.Dispose(); lifetime.Dispose();
    }
}
