using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record LogRecord
{
    public DateTimeOffset WallTime { get; init; } = DateTimeOffset.Now;
    public double SessionMs { get; init; }
    public string Level { get; init; } = "INFO";
    public string Stage { get; init; } = "";
    public string Event { get; init; } = "";
    public string Method { get; init; } = "—";
    public string Message { get; init; } = "";
    public double? SourcePtsMs { get; init; }
    public double? MpvPosMs { get; init; }
    public long? SeekEpoch { get; init; }
    public int? FrameIndex { get; init; }
    public string? NodeKey { get; init; }
    public string? WavPath { get; init; }
    public long? AudioGeneration { get; init; }
    public double? ProcessingMs { get; init; }
    public string? RawOcr { get; init; }
    public string? NormalizedOcr { get; init; }
    public string? Speaker { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
}
