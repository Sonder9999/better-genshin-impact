using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record QuestAudioRequest(SpeechLine Line, string ProfileFingerprint, string InstanceFingerprint)
{
    public const int Schema = 1;
    public string CacheKey => SpeechHash.Of(JsonSerializer.Serialize(new
    {
        Schema, Line.SourceKey, Line.ChapterId, Line.StoryId, Line.TaskId, Line.DialogueId, OptionNumber = (int?)null,
        Line.TargetNodeKey, Line.TextLanguage, TextHash = SpeechHash.Of(Line.Text), Line.CharacterId, Line.SpeakerKey,
        Line.Profile.ProfileId, ProfileFingerprint, Line.Profile.ProviderId, Line.Profile.InstanceId,
        Line.Profile.ProviderVersion, InstanceFingerprint, Line.MappingFingerprint
    }));
    public static QuestAudioRequest Capture(SpeechLine line, string instanceFingerprint) => new(
        line with { Profile = line.Profile with { Parameters = new(line.Profile.Parameters, StringComparer.Ordinal) } },
        line.Profile.Fingerprint, instanceFingerprint);
    public bool AssetsUnchanged => Line.Profile.Fingerprint == ProfileFingerprint;
}
public sealed record QuestAudioMetadata(int Schema, string Key, string State, string Language, string ChapterId,
    string StoryId, string TaskId, string DialogueId, string CharacterId, string SpeakerKey, string TextHash,
    string ProfileFingerprint, string InstanceFingerprint, string MappingFingerprint, string ProviderId, string InstanceId,
    DateTimeOffset CreatedUtc, string Sha256, long Bytes, ushort? Channels, uint? SampleRate, ushort? BitsPerSample,
    double? DurationSeconds, double RequestMs, double? FirstByteMs);
public sealed record QuestAudioHit(string Path, QuestAudioMetadata Metadata);

/// <summary>Metadata is the final commit marker. Orphan WAVs and temporary files are never Ready.</summary>
public sealed class QuestAudioStore
{
    public string Root { get; }
    private readonly SemaphoreSlim publication = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public QuestAudioStore(string experimentRoot)
    {
        Root = new PathPolicy(experimentRoot).RequireOutput("cache/QuestAudio"); Directory.CreateDirectory(Root);
    }
    private string PathFor(QuestAudioRequest request)
    {
        var task = SpeechHash.Of(System.Text.Json.JsonSerializer.Serialize(new[] { request.Line.StoryId, request.Line.TaskId }));
        return Path.Combine(Root, QuestAudioRequest.Schema.ToString(), QuestLanguages.Normalize(request.Line.TextLanguage),
            SpeechHash.Of(request.Line.ChapterId), task, request.CacheKey + ".wav");
    }
    public QuestAudioHit? Find(QuestAudioRequest request)
    {
        var path = PathFor(request);
        try
        {
            if (!File.Exists(path) || !File.Exists(path + ".json")) return null;
            var metadata = JsonSerializer.Deserialize<QuestAudioMetadata>(File.ReadAllText(path + ".json"));
            if (metadata is null || metadata.State != "Ready" || metadata.Schema != QuestAudioRequest.Schema || metadata.Key != request.CacheKey ||
                metadata.Bytes != new FileInfo(path).Length || metadata.Sha256 != SpeechHash.FileHash(path)) return null;
            var wave = WaveReader.Read(path);
            return Valid(wave) ? new(path, metadata) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static bool Valid(WaveInfo info) => info.IsValid && info.DataBytes > 0 && info.DurationSeconds is > 0;
    public async Task<QuestAudioHit> PublishAsync(QuestAudioRequest request, ReadOnlyMemory<byte> bytes, double requestMs,
        double? firstByteMs, Func<bool> stillValid, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var path = PathFor(request); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
        { await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true); }
        var wave = WaveReader.Read(temporary);
        if (!Valid(wave)) throw new InvalidDataException("Invalid complete WAV: " + string.Join("; ", wave.Errors));
        var metadata = new QuestAudioMetadata(QuestAudioRequest.Schema, request.CacheKey, "Ready", request.Line.TextLanguage,
            request.Line.ChapterId, request.Line.StoryId, request.Line.TaskId, request.Line.DialogueId, request.Line.CharacterId,
            request.Line.SpeakerKey, SpeechHash.Of(request.Line.Text), request.ProfileFingerprint, request.InstanceFingerprint,
            request.Line.MappingFingerprint, request.Line.Profile.ProviderId, request.Line.Profile.InstanceId, DateTimeOffset.UtcNow,
            SpeechHash.FileHash(temporary), bytes.Length, wave.Channels, wave.SampleRate, wave.BitsPerSample, wave.DurationSeconds, requestMs, firstByteMs);
        var metadataTemporary = temporary + ".json";
        await File.WriteAllTextAsync(metadataTemporary, JsonSerializer.Serialize(metadata, Json), token).ConfigureAwait(false);
        await publication.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!request.AssetsUnchanged || !stillValid()) throw new OperationCanceledException("Fingerprint changed before publication.");
            var hit = Find(request); if (hit is not null) return hit;
            File.Move(temporary, path, true);
            token.ThrowIfCancellationRequested();
            // Publish the metadata last. No reader treats a lone WAV as Ready.
            File.Move(metadataTemporary, path + ".json", true);
            return new(path, metadata);
        }
        finally { publication.Release(); }
    }
    public long DiskBytes() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
}
