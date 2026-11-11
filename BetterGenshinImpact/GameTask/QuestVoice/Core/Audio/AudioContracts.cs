using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public readonly record struct AudioFormat(int SampleRate, int Channels);
public sealed record AudioLine(string ClipId, double SourcePtsMs, string NodeId, string? WavePath,
    string EventId, long EventSequence, string Variant = "default", double AudioStartMs = 0);
public sealed record AudioOptions(AudioFormat? OutputFormat = null, double FadeOutMs = 20,
    long CacheBudgetBytes = 32 * 1024 * 1024, long MaxDecodedBytes = 32 * 1024 * 1024,
    int MaxPrefetch = 4, int MaxLogEntries = 4096);

public sealed class PCMWave
{
    private readonly float[] samples;
    public AudioFormat Format { get; }
    public ReadOnlyMemory<float> Samples => samples;
    public int FrameCount => samples.Length / Format.Channels;
    public long ByteLength => samples.LongLength * sizeof(float);
    public double DurationMs => FrameCount * 1000d / Format.SampleRate;
    public bool IsSilent { get; }
    public PCMWave(AudioFormat format, float[] samples) : this(format, samples, false) { }
    private PCMWave(AudioFormat format, float[] samples, bool ownsSamples)
    {
        ValidateFormat(format);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0 || samples.Length % format.Channels != 0 || samples.Any(s => !float.IsFinite(s)))
            throw new ArgumentException("PCM requires nonempty, aligned, finite samples.", nameof(samples));
        Format = format;
        this.samples = ownsSamples ? samples : samples.ToArray();
        IsSilent=this.samples.All(s=>s==0);
    }
    internal static PCMWave Own(AudioFormat format, float[] samples) => new(format, samples, true);
    public static void ValidateFormat(AudioFormat format)
    {
        if (format.SampleRate is <= 0 or > 384000 || format.Channels is not (1 or 2))
            throw new ArgumentException("Audio requires a positive sample rate and mono/stereo output.", nameof(format));
    }
}

public interface IWaveLoader
{
    Task<PCMWave> LoadAsync(string wavePath, AudioFormat outputFormat, long maxDecodedBytes,
        CancellationToken cancellationToken = default);
}

public enum AudioState { Idle, Loading, Playing, Paused, Failed, Disposed }
public enum AudioStopReason { UserStop, UnknownContext, NonQuestDialogue, DialogueExit, Correction }
public interface IAudioClock { double NowMs { get; } }
public sealed class StopwatchAudioClock : IAudioClock
{
    public double NowMs => System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency;
}
public readonly record struct AudioRenderResult(long Generation, long? ActiveGeneration,
    int ActiveFrames, int TailFrames, bool Completed);
public delegate AudioRenderResult AudioRenderCallback(Span<float> samples);
// The epoch captured by this thread before invoking the renderer; the getter must not take a native lock.
public interface IGenerationAudioOutput { long? RenderingGeneration { get; } }
public sealed record AudioOutputEvent(long Generation, string Command, double OutputClockMs,
    double ObservedWallMs, string? Error = null);
public interface IAudioOutput : IDisposable
{
    double? OutputClockMs { get; }
    void Initialize(AudioFormat format, AudioRenderCallback render, Action<AudioOutputEvent> observe);
    void Flush(long generation);
    void Pause(long controlVersion = 0);
    void Resume(long controlVersion = 0);
}
public sealed record AudioSnapshot(AudioState State, long Generation, string? NodeId, string? PendingNodeId,
    double RenderedDurationMs, double? PlayedMs, double? DurationMs, long CacheBytes, int PrefetchCount,
    long ActivePcmBytes);
public sealed record AudioLogEntry(string ClipId, double SourcePtsMs, string? NodeId, string? WavePath,
    string EventId, long Generation, string Command, double WallClockMs, double? OutputClockMs,
    double? ObservedLatencyMs, double? PlayedMs, double? DurationMs, string? CancelReason,
    double? LoadMs = null, double? OverlapMs = null, double SuggestedAudioStartMs = 0, double AppliedAudioStartMs = 0, string? PlaybackSkipReason = null);
