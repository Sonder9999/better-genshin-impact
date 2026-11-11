using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record TTSInstance(string InstanceId, string Endpoint, bool Enabled = false, bool Authorized = false,
    bool ExclusiveUseConfirmed = false, int TimeoutSeconds = 120, string ApiVersion = "api-v2",
    string ProviderId = "gpt-sovits", string? ApiKey = null, string? Model = null, double Rate = 1.0, double Pitch = 0.0)
{
    public string Fingerprint => SpeechHash.Of(JsonSerializer.Serialize(new { InstanceId, Endpoint, ApiVersion, ProviderId }));
    public Uri Validate()
    {
        if (!Enabled || !Authorized || !ExclusiveUseConfirmed) throw new InvalidOperationException("TTS instance requires Enabled, Authorized and exclusive use confirmation.");
        if (string.IsNullOrWhiteSpace(InstanceId) || TimeoutSeconds is < 1 or > 600 || ApiVersion != "api-v2") throw new ArgumentException("Invalid TTS instance settings.");
        var uri = new Uri(Endpoint, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("TTS endpoint must not contain credentials, query or fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}

public sealed record SynthesisRequest(string Text, string TextLanguage, VoiceProfile Profile);
public sealed record SynthesisResult(byte[] CompleteWave, double RequestMs, double? FirstByteMs);

public interface ISpeechSynthesisProvider
{
    string InstanceId { get; }
    string InstanceFingerprint { get; }
    bool BackendUnknown { get; }
    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken token);
}

public sealed class BackendUnknownException(string message, Exception? inner = null) : IOException(message, inner);
