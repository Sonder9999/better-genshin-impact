using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

/// <summary>
/// Unified frame packet across real-time game window capture (WGC) and video preview (MPV/FFmpeg).
/// SourcePtsSeconds is available for video media; MonotonicMs is always causal.
/// </summary>
public sealed record FramePacket(
    string SourceId,
    int FrameIndex,
    long Generation,
    double MonotonicMs,
    double? SourcePtsSeconds,
    SourcePtsTrust PtsTrust,
    int Width,
    int Height,
    byte[] Rgb)
{
    public SourceFrameTime Time => new(
        (SourcePtsSeconds ?? (MonotonicMs / 1000.0)) * 1000.0,
        SourcePtsSeconds.HasValue ? SourcePtsSeconds.Value * 1000.0 : null,
        PtsTrust);

    public VisualFrame ToVisualFrame() => new(Width, Height, Rgb, SourcePtsSeconds ?? (MonotonicMs / 1000.0));
}

public interface IFrameSource : IAsyncDisposable
{
    string SourceId { get; }
    string SourceKind { get; } // "wgc-window" or "ffmpeg-video"
    bool IsRunning { get; }
    int Width { get; }
    int Height { get; }
    long DroppedFrames { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<FramePacket?> ReadNextFrameAsync(int timeoutMs, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Video-specific seek/PTS/speed capability. Real-time window capture does not implement this.</summary>
public interface ISeekableMediaSource
{
    bool Connected { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; }
    bool Eof { get; }
    double PositionMs { get; }
    double DurationMs { get; }
    double Speed { get; }
    bool Muted { get; }
    int Volume { get; }
    string MediaPath { get; }
    Task SeekAbsoluteAsync(double seconds, CancellationToken cancellationToken = default);
    Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default);
    Task TogglePauseAsync(CancellationToken cancellationToken = default);
    Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default);
    Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
}

public sealed record GameWindowDescriptor(
    int ProcessId,
    nint WindowHandle,
    string ProcessName,
    string WindowTitle,
    string ClassName,
    int ClientWidth,
    int ClientHeight,
    bool IsMinimized)
{
    public string DisplayLabel => $"{WindowTitle} ({ProcessName}.exe · PID {ProcessId} · {ClientWidth}x{ClientHeight})" +
                                  (IsMinimized ? " [已最小化]" : "");
    public override string ToString() => DisplayLabel;
}

public interface IGameWindowLocator
{
    Task<IReadOnlyList<GameWindowDescriptor>> FindCandidateWindowsAsync(
        IReadOnlyList<string>? preferredProcessNames = null,
        CancellationToken cancellationToken = default);
}
