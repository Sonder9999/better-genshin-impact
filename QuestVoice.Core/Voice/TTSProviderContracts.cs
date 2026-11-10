namespace QuestVoice.Core;

public enum TTSPhase { SwitchingWeights, Synthesizing, Draining }
public enum ReferenceFallback { None, CharacterHasNoEmotionTags, EmotionNotAvailable }
public enum TTSAudioEncoding { Pcm16Wav, Mp3, Pcm16 }

public sealed record TTSProviderCapabilities(
    bool ServerConnection = false,
    bool VoiceAssets = false,
    bool Streaming = false,
    bool PresetVoices = false,
    bool RequiresDrain = false,
    bool Rate = false,
    bool Pitch = false,
    bool Language = false,
    bool Model = false,
    bool ApiKey = false,
    bool AudioFormat = false);

public sealed record TTSProviderInfo(
    string Id,
    string DisplayName,
    TTSProviderCapabilities Capabilities,
    string Description,
    string LicenseNotice = "Built-in / MIT",
    bool IsAvailableInCurrentBuild = true)
{
    public override string ToString() => DisplayName;
}

public sealed record TTSVoiceInfo(string Id, string DisplayName, string Language)
{
    public override string ToString() => DisplayName;
}

public sealed record TTSProviderSettings(
    string ProviderId = "gpt-sovits",
    string InstanceId = "local",
    string VoiceId = "",
    string Endpoint = "http://127.0.0.1:9880/",
    string? ApiKey = null,
    string? Model = null,
    string Language = "auto",
    string Format = "wav",
    double Rate = 1.0,
    double Pitch = 0.0,
    int ConnectTimeoutSeconds = 10,
    int RequestTimeoutSeconds = 60,
    int SynthesisTimeoutSeconds = 120);

public sealed record ReferenceAudio(string Path, string PromptText, string? Emotion = null, string PromptLanguage = "jpn");

public sealed record VoiceBinding(
    string Name,
    string GptWeightsPath,
    string SovitsWeightsPath,
    IReadOnlyList<ReferenceAudio> References,
    TTSProviderSettings? Provider = null);

public sealed record ReferenceSelection(ReferenceAudio Audio, ReferenceFallback Fallback);

public sealed record TTSRequest(string Text, string TextLanguage, VoiceBinding Voice, string? Emotion = null);

public sealed record TTSResult(
    byte[] Wav,
    double SynthesisSeconds,
    double TimeToFirstByteSeconds,
    double AudioSeconds,
    int SampleRate,
    int Channels,
    ReferenceFallback ReferenceFallback,
    TTSAudioEncoding SourceEncoding = TTSAudioEncoding.Pcm16Wav);

public sealed record WAVAudio(byte[] Bytes, double DurationSeconds, int SampleRate, int Channels);

public interface IWAVProcessor
{
    WAVAudio ValidateCompletedResponse(byte[] bytes);
}

public interface ITTSProvider : IDisposable
{
    TTSProviderInfo Info { get; }
    void Validate(TTSProviderSettings settings);
    Task<IReadOnlyList<TTSVoiceInfo>> GetVoicesAsync(TTSProviderSettings settings, CancellationToken token = default);
    Task<string> ProbeAsync(TTSProviderSettings settings, CancellationToken token = default);
    Task<TTSResult> SynthesizeAsync(TTSProviderSettings settings, TTSRequest request,
        IProgress<TTSPhase>? progress = null, CancellationToken cancellationToken = default);
    void Shutdown();
}
