using System.Security.Cryptography;
using System.Text.Json;

namespace QuestVoice.Core;

public sealed record SpeechOnsetPolicy
{
    public bool Enabled { get; init; }
    public double MinLeadingGapMs { get; init; } = 1000;
    public double MinSoftPreludeMs { get; init; } = 100;
    public double MinSpeechOnsetMs { get; init; } = 300;
    public double FirstSoundMaxMs { get; init; } = 150;
    public double PreRollMs { get; init; } = 120;
    public double MaxTrimMs { get; init; } = 3000;
    public bool RequirePeriodicity { get; init; } = true;
    public string AlgorithmVersion { get; init; } = SpeechOnsetAnalyzer.Version;
    public bool IsValid => AlgorithmVersion == SpeechOnsetAnalyzer.Version &&
        new[] { MinLeadingGapMs, MinSoftPreludeMs, MinSpeechOnsetMs, FirstSoundMaxMs, PreRollMs, MaxTrimMs }.All(v => double.IsFinite(v) && v >= 0 && v <= 7000) && PreRollMs >= 120;
}
public sealed record SpeechOnsetAnalysis(string SourceWavHash, long SourceWavBytes, string AlgorithmVersion, string PlaybackFingerprint,
    string AnalysisState, double? FirstSoundMs, double? EstimatedSpeechMs, double GapMs, double SoftPreludeMs,
    double PeriodicityEvidence, double SuggestedAudioStartMs, double AppliedAudioStartMs, double PreRollMs,
    string ConfidenceReason, string? SkipReason, DateTimeOffset CreatedUtc, bool AnalysisCacheHit = false, double AnalyzerElapsedMs = 0);

/// <summary>Independent PCM16 signal implementation. No filename, voice-model or CSV inference.</summary>
public static class SpeechOnsetAnalyzer
{
    public const string Version = "prevoice-speech-onset-v1";
    private readonly record struct Window(double AtMs, double Db, double Periodicity);
    public static SpeechOnsetAnalysis NoAdjustment(SpeechOnsetPolicy policy, string fingerprint, string reason, string hash = "", long bytes = 0) =>
        new(hash, bytes, Version, fingerprint, "NoAdjustment", null, null, 0, 0, 0, 0, 0, policy.PreRollMs, "insufficient_or_disabled", reason, DateTimeOffset.UtcNow);

    public static SpeechOnsetAnalysis Analyze(string path, SpeechOnsetPolicy policy, string fingerprint)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        return AnalyzeFile(path, policy, fingerprint) with { AnalyzerElapsedMs = watch.Elapsed.TotalMilliseconds };
    }
    private static SpeechOnsetAnalysis AnalyzeFile(string path, SpeechOnsetPolicy policy, string fingerprint)
    {
        if (!policy.Enabled) return NoAdjustment(policy, fingerprint, "Disabled");
        if (!policy.IsValid) return NoAdjustment(policy, fingerprint, "InvalidPolicy");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var info = WaveReader.Read(path);
            if (!info.IsValid || info.FormatTag != 1 || info.BitsPerSample != 16 || info.Channels is not (1 or 2) || info.SampleRate is < 8000 or > 96000)
                return NoAdjustment(policy, fingerprint, "InvalidOrUnsupportedWav", hash, stream.Length);
            stream.Position = 12;
            using var reader = new BinaryReader(stream);
            long dataAt = -1;
            while (stream.Position + 8 <= stream.Length)
            {
                var tag = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)); var size = reader.ReadUInt32();
                if (tag == "data") { dataAt = stream.Position; break; }
                stream.Position += size + (size & 1);
            }
            if (dataAt < 0) return NoAdjustment(policy, fingerprint, "MissingData", hash, stream.Length);
            var rate = (int)info.SampleRate!.Value; var channels = info.Channels!.Value;
            var bytes = reader.ReadBytes((int)Math.Min(info.DataBytes, rate * channels * 2L * 7));
            // Select the channel with greater whole-prefix energy, avoiding antiphase stereo cancellation.
            var count = bytes.Length / (channels * 2); var channelEnergy = new double[channels];
            for (var i = 0; i < count; i++) for (var c = 0; c < channels; c++)
            { var value = BitConverter.ToInt16(bytes, (i * channels + c) * 2) / 32768d; channelEnergy[c] += value * value; }
            var selectedChannel = channels == 2 && channelEnergy[1] > channelEnergy[0] ? 1 : 0;
            var pcm = new short[count];
            for (var i = 0; i < count; i++) pcm[i] = BitConverter.ToInt16(bytes, (i * channels + selectedChannel) * 2);
            return AnalyzePcm(pcm, rate, info.DurationSeconds!.Value * 1000, policy, fingerprint, hash, stream.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { return NoAdjustment(policy, fingerprint, "AnalysisFailed:" + e.GetType().Name); }
    }

    private static SpeechOnsetAnalysis AnalyzePcm(short[] pcm, int rate, double duration, SpeechOnsetPolicy policy, string fingerprint, string hash, long bytes)
    {
        var hop = Math.Max(1, (int)Math.Round(rate * .02)); var windows = new List<Window>();
        for (var start = 0; start + hop <= pcm.Length; start += hop)
        {
            var energy = 0d;
            for (var i = start; i < start + hop; i++) { var value = pcm[i] / 32768d; energy += value * value; }
            var db = 20 * Math.Log10(Math.Max(1e-8, Math.Sqrt(energy / hop)));
            windows.Add(new(start * 1000d / rate, db, db >= -53 ? Periodicity(pcm.AsSpan(start, hop), rate) : 0));
        }
        SpeechOnsetAnalysis Skip(string reason) => NoAdjustment(policy, fingerprint, reason, hash, bytes);
        if (windows.Count < 5) return Skip("TooShort");
        var sorted = windows.Select(w => w.Db).Order().ToArray(); var q90 = sorted[(int)((sorted.Length - 1) * .9)];
        var sound = FirstRun(windows, w => w.Db >= -46, 2);
        var strong = FirstRun(windows, w => w.Db >= Math.Max(-38, q90 - 15), 3);
        var voiced = FirstRun(windows, w => w.Db >= Math.Max(-42, q90 - 19) && w.Periodicity >= .57, 2);
        if (sound < 0 || voiced < 0 && (policy.RequirePeriodicity || strong < 0)) return Skip("NoSoundOrReliableSpeech");
        var speechIndex = voiced >= 0 ? voiced : strong;
        var periodicity = voiced >= 0 ? windows.Skip(voiced).Take(2).Min(w => w.Periodicity) : 0;
        // Protect a contiguous weak consonant immediately preceding the voiced candidate.
        var lookBackFloor = Math.Max(0, speechIndex - 6);
        for (var i = speechIndex - 1; i >= lookBackFloor; i--)
        { if (windows[i].Db < -47) break; speechIndex = i; }
        var first = windows[sound].AtMs; var speech = windows[speechIndex].AtMs;
        var gap = speech - first;
        var soft = windows.Count(w => w.AtMs >= first && w.AtMs < speech && w.Db >= -46 && w.Db < Math.Max(-38, q90 - 15) && w.Periodicity < .57) * 20d;
        var suggested = Math.Max(0, speech - policy.PreRollMs); string? skip = null;
        if (first > policy.FirstSoundMaxMs || speech < policy.MinSpeechOnsetMs || gap < policy.MinLeadingGapMs || soft < policy.MinSoftPreludeMs)
            skip = "InsufficientPreludeEvidence";
        // Early strong energy or voiced material can be intentional speech/sigh; never remove it automatically.
        else if (strong >= 0 && windows[strong].AtMs < speech - 120) skip = "ConflictingEarlyStrongSound";
        else if (suggested > policy.MaxTrimMs) skip = "ExceedsMaxTrim";
        else if (suggested < 80 || duration - suggested < 200) skip = "InvalidOrShortRemainingWindow";
        return new(hash, bytes, Version, fingerprint, skip is null ? "Adjusted" : "NoAdjustment", first, speech, gap, soft,
            periodicity, suggested, skip is null ? suggested : 0, policy.PreRollMs, voiced >= 0 ? "periodic_speech_with_soft_nonperiodic_prelude" : "strong_energy_only",
            skip, DateTimeOffset.UtcNow);
    }
    private static int FirstRun(List<Window> windows, Func<Window, bool> predicate, int required)
    {
        var run = 0;
        for (var i = 0; i < windows.Count; i++) { run = predicate(windows[i]) ? run + 1 : 0; if (run >= required) return i - required + 1; }
        return -1;
    }
    private static double Periodicity(ReadOnlySpan<short> pcm, int rate)
    {
        // Physical 85–350Hz lag range remains correct at all supported rates.
        var step = Math.Max(1, rate / 8000); var n = pcm.Length / step;
        Span<double> signal = stackalloc double[1920]; var mean = 0d;
        for (var i = 0; i < n; i++) { signal[i] = pcm[i * step] / 32768d; mean += signal[i]; }
        mean /= n; var energy = 0d;
        for (var i = 0; i < n; i++) { signal[i] -= mean; energy += signal[i] * signal[i]; }
        if (energy < 1e-7) return 0;
        var effectiveRate = rate / (double)step; var best = 0d;
        for (var lag = (int)(effectiveRate / 350); lag <= Math.Min(n / 2, (int)(effectiveRate / 85)); lag++)
        {
            double dot = 0, a = 0, b = 0;
            for (var i = 0; i < n - lag; i++) { var x = signal[i]; var y = signal[i + lag]; dot += x * y; a += x * x; b += y * y; }
            best = Math.Max(best, dot / Math.Sqrt(Math.Max(1e-15, a * b)));
        }
        return Math.Clamp(best, 0, 1);
    }
}

/// <summary>Sidecar is separate from Ready metadata. File lock binds analysis to the original WAV hash.</summary>
public static class SpeechOnsetSidecar
{
    public static string PathFor(string wavPath, string fingerprint) => wavPath + ".onset-" + fingerprint + ".json";
    public static SpeechOnsetAnalysis Get(string path, VoiceProfile profile)
    {
        var policy = profile.SpeechOnset; var fingerprint = profile.PlaybackFingerprint;
        if (!policy.Enabled || !policy.IsValid) return SpeechOnsetAnalyzer.NoAdjustment(policy, fingerprint, policy.Enabled ? "InvalidPolicy" : "Disabled");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            var sidecar = PathFor(path, fingerprint);
            try
            {
                if (File.Exists(sidecar))
                {
                    var value = JsonSerializer.Deserialize<SpeechOnsetAnalysis>(File.ReadAllText(sidecar));
                    if (value is not null && value.SourceWavHash == hash && value.SourceWavBytes == file.Length &&
                        value.PlaybackFingerprint == fingerprint && value.AlgorithmVersion == SpeechOnsetAnalyzer.Version &&
                        value.AnalysisState is "Adjusted" or "NoAdjustment" && double.IsFinite(value.AppliedAudioStartMs) &&
                        value.AppliedAudioStartMs >= 0 && value.AppliedAudioStartMs <= policy.MaxTrimMs &&
                        (value.AnalysisState == "Adjusted" || value.AppliedAudioStartMs == 0)) return value with { AnalysisCacheHit = true };
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            var analysis = SpeechOnsetAnalyzer.Analyze(path, policy, fingerprint);
            if (analysis.SourceWavHash != hash) return SpeechOnsetAnalyzer.NoAdjustment(policy, fingerprint, "WavChanged");
            try
            {
                var temporary = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, sidecar, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Playback never depends on sidecar writes. */ }
            return analysis;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return SpeechOnsetAnalyzer.NoAdjustment(policy, fingerprint, "SidecarOrSourceUnavailable:" + e.GetType().Name); }
    }
}
