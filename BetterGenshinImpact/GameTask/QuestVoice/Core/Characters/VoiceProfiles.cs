using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record RegisteredVoice(string Id, string Name, string OriginalLanguage, bool AssetsAvailable,
    string ProviderId = "gpt-sovits", string? ModelAssetId = null)
{
    public override string ToString() => Name;
}

public interface IVoiceRoutingProvider
{
    (string CharacterId, VoiceProfile? Profile, string Reason) Resolve(
        QuestNode chineseNode, string targetLanguage, TravelerGender? gender);
    VoiceProfile? ResolveAmbient(string characterId, string language);
    string? VoiceOverride(QuestNode chineseNode, string language);
    void SetVoiceOverride(QuestNode chineseNode, string language, string? voiceId);
    string SpeakerKey(QuestNode chineseNode);
    string VoiceDisplayName(string voiceId);
    IReadOnlyList<RegisteredVoice> AvailableVoices(string targetLanguage);
}

/// <summary>Separated entity: reusable GPT/SoVITS weight asset pair that can be shared by multiple VoiceDefinitions.</summary>
public sealed record ModelAssetDefinition(
    string AssetId, string DisplayName, string GptWeightsPath, string SovitsWeightsPath, string DefaultLanguage = "jpn")
{
    public bool Exists => Path.IsPathFullyQualified(GptWeightsPath) && File.Exists(GptWeightsPath) &&
                          Path.IsPathFullyQualified(SovitsWeightsPath) && File.Exists(SovitsWeightsPath);
}

/// <summary>Separated entity: emotion-tagged reference audio & prompt text.</summary>
public sealed record ReferenceAudioEntry(
    string ReferenceId, string AudioPath, string PromptText, string PromptLanguage = "jpn", string Emotion = "default")
{
    public bool Exists => Path.IsPathFullyQualified(AudioPath) && File.Exists(AudioPath) &&
                          !string.IsNullOrWhiteSpace(PromptText);
}

/// <summary>Separated entity: a voice definition bound either to a ModelAsset + ReferenceAudios or to a preset TTS Provider voice.</summary>
public sealed record VoiceDefinition(
    string VoiceId, string DisplayName, string Language, string ProviderId = "gpt-sovits",
    string InstanceId = "local", string? ModelAssetId = null, string? PresetVoiceId = null,
    IReadOnlyList<ReferenceAudioEntry>? References = null, double Rate = 1.0, double Pitch = 0.0)
{
    public IReadOnlyList<ReferenceAudioEntry> SafeReferences => References ?? [];
}

/// <summary>Separated entity: explicit game + character + language to VoiceDefinition binding with version tracking.</summary>
public sealed record CharacterVoiceBinding(
    string GameId, string CharacterId, string RoleDisplayName, string Language,
    string VoiceId, int Version = 1, DateTimeOffset? UpdatedUtc = null);

public sealed record VoiceProfile
{
    public string ProfileId { get; init; } = "";
    public string CharacterId { get; init; } = "";
    public string Language { get; init; } = "jpn";
    public string ProviderId { get; init; } = "gpt-sovits";
    public string InstanceId { get; init; } = "local";
    public string ProviderVersion { get; init; } = "api-v2";
    public string VoiceId { get; init; } = "";
    public string GptWeights { get; init; } = "";
    public string SovitsWeights { get; init; } = "";
    public string ReferenceAudio { get; init; } = "";
    public string PromptText { get; init; } = "";
    public string PromptLanguage { get; init; } = "jpn";
    public IReadOnlyList<ReferenceAudioEntry> ExtraReferences { get; init; } = [];
    public SortedDictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.Ordinal);
    public bool Enabled { get; init; }
    public int ConfigurationVersion { get; init; } = 1;
    private readonly SpeechOnsetPolicy speechOnset = new();
    public SpeechOnsetPolicy SpeechOnset { get => speechOnset; init => speechOnset = value ?? new(); }
    [System.Text.Json.Serialization.JsonIgnore]
    public string PlaybackFingerprint => SpeechHash.Of(JsonSerializer.Serialize(SpeechOnset));
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => SpeechHash.Of(JsonSerializer.Serialize(new
    {
        ProfileId, CharacterId, Language = QuestLanguages.Normalize(Language), ProviderId, InstanceId, ProviderVersion, VoiceId,
        GptWeights, GptHash = Asset(GptWeights), SovitsWeights, SovitsHash = Asset(SovitsWeights),
        ReferenceAudio, ReferenceHash = Asset(ReferenceAudio), PromptHash = SpeechHash.Of(PromptText),
        PromptLanguage, Parameters = Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), Enabled, ConfigurationVersion
    }));
    private static string Asset(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? SpeechHash.AssetHash(path) : "missing:" + path;
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled) errors.Add("Profile disabled");
        if (string.IsNullOrWhiteSpace(ProfileId) || string.IsNullOrWhiteSpace(CharacterId) || string.IsNullOrWhiteSpace(InstanceId)) errors.Add("Missing profile/character/instance identity");
        try { QuestLanguages.Normalize(Language); if (ProviderId == "gpt-sovits") QuestLanguages.Normalize(PromptLanguage); } catch (ArgumentException e) { errors.Add(e.Message); }
        var supportedProviders = new[] { "gpt-sovits", "voicevox", "vits-simple-api", "openai-speech", "windows-sapi", "huoshan", "google-translate" };
        if (!supportedProviders.Contains(ProviderId, StringComparer.Ordinal))
        {
            errors.Add("Unsupported provider");
            return errors;
        }
        if (ProviderId == "gpt-sovits")
        {
            foreach (var path in new[] { GptWeights, SovitsWeights, ReferenceAudio })
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) errors.Add("Missing local asset: " + path);
            if (string.IsNullOrWhiteSpace(PromptText)) errors.Add("Missing reference prompt");
            var allowed = new[] { "top_k", "top_p", "temperature", "text_split_method", "batch_size", "batch_threshold", "split_bucket", "speed_factor", "seed", "parallel_infer", "repetition_penalty", "sample_steps", "super_sampling" };
            if (Parameters.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) errors.Add("Unknown generation parameter");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(VoiceId)) errors.Add("Missing preset VoiceId for provider " + ProviderId);
        }
        return errors;
    }
}
public sealed record CharacterBinding(string Language, string SpeakerKey, string CharacterId, TravelerGender? Gender = null);
public sealed record CharacterSuggestion(string ChsName, string JpName, int Evidence, int Conflicts,
    IReadOnlyList<DialogueMapping> Lines)
{
    public override string ToString() => $"{ChsName} ↔ {JpName} · 证据 {Evidence} / 冲突 {Conflicts}";
}
public sealed record VoiceConfiguration(
    IReadOnlyList<CharacterBinding> Bindings,
    IReadOnlyList<VoiceProfile> Profiles,
    IReadOnlyList<ModelAssetDefinition>? ModelAssets = null,
    IReadOnlyList<VoiceDefinition>? VoiceDefinitions = null,
    IReadOnlyList<CharacterVoiceBinding>? GameCharacterBindings = null,
    int Version = 1);

/// <summary>Only explicit evidence confirmations persist identity. Unknown occurrences cannot be confirmed.</summary>
public sealed class VoiceProfileStore
{
    private readonly string path;
    private readonly object gate = new();
    public VoiceConfiguration Configuration { get; private set; } = new([], []);
    public int Revision { get; private set; } = 1;
    public event Action<int>? VersionChanged;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public VoiceProfileStore(string experimentRoot)
    {
        path = Path.Combine(experimentRoot, "config", "voice-profiles.json");
        if (File.Exists(path))
        {
            Configuration = JsonSerializer.Deserialize<VoiceConfiguration>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Invalid voice configuration");
            Revision = Math.Max(1, Configuration.Version);
        }
    }
    public string ConfigurationPath => path;
    public static bool CanConfirm(QuestNode n) => n.Speaker?.IdentityStatus is SpeakerIdentityStatus.DisplayNameOnly or SpeakerIdentityStatus.Resolved;
    public static IReadOnlyList<CharacterSuggestion> Suggestions(DialogueLanguageMap map)
    {
        var paired = map.Entries.Where(e => e.Status == MappingStatus.Matched && e.Chs is not null && e.Jp is not null
            && CanConfirm(e.Chs) && CanConfirm(e.Jp)).ToArray();
        return paired.GroupBy(e => (e.Chs!.Role, e.Jp!.Role)).Select(g => new CharacterSuggestion(g.Key.Item1, g.Key.Item2,
            g.Count(), paired.Count(e => e.Chs!.Role == g.Key.Item1 && e.Jp!.Role != g.Key.Item2 ||
                e.Jp!.Role == g.Key.Item2 && e.Chs!.Role != g.Key.Item1), g.ToArray())).OrderByDescending(s => s.Evidence).ToArray();
    }
    public void Confirm(CharacterSuggestion suggestion, string characterId, TravelerGender? gender = null)
    {
        if (string.IsNullOrWhiteSpace(characterId) || suggestion.Conflicts > 0 || suggestion.Lines.Count == 0 ||
            suggestion.Lines.Any(e => e.Status != MappingStatus.Matched || e.Chs is null || e.Jp is null || !CanConfirm(e.Chs) || !CanConfirm(e.Jp)))
            throw new InvalidOperationException("Missing evidence, conflicted suggestion or Unresolved character.");
        lock (gate)
        {
            var additions = suggestion.Lines.SelectMany(e => new[] { new CharacterBinding("chs", e.Chs!.Speaker!.SpeakerKey, characterId.Trim(), gender),
                new CharacterBinding("jpn", e.Jp!.Speaker!.SpeakerKey, characterId.Trim(), gender) }).Distinct().ToArray();
            var bindings = Configuration.Bindings.Where(b => !additions.Any(a => a.Language == b.Language && a.SpeakerKey == b.SpeakerKey && a.Gender == b.Gender)).Concat(additions).ToArray();
            Save(Configuration with { Bindings = bindings, Version = ++Revision });
        }
    }
    public string? Character(QuestNode node, string language, TravelerGender? gender)
    {
        if (!CanConfirm(node)) return null;
        lock (gate)
        {
            var candidates = Configuration.Bindings.Where(b => b.Language == QuestLanguages.Normalize(language) && b.SpeakerKey == node.Speaker!.SpeakerKey).ToArray();
            var specific = candidates.Where(b => b.Gender == gender && b.Gender is not null).ToArray();
            var matches = specific.Length > 0 ? specific : candidates.Where(b => b.Gender is null).ToArray();
            return matches.Select(b => b.CharacterId).Distinct().Count() == 1 ? matches[0].CharacterId : null;
        }
    }
    public VoiceProfile? Profile(string characterId, string language)
    { lock (gate) return Configuration.Profiles.SingleOrDefault(p => p.CharacterId == characterId && QuestLanguages.Normalize(p.Language) == QuestLanguages.Normalize(language)); }
    public void PutProfile(VoiceProfile profile)
    {
        // Save incomplete profiles for editing, but resolver will never synthesize them.
        var normalized = profile with { Language = QuestLanguages.Normalize(profile.Language), ConfigurationVersion = Revision + 1 };
        lock (gate) Save(Configuration with
        {
            Profiles = Configuration.Profiles.Where(p => p.CharacterId != normalized.CharacterId ||
                QuestLanguages.Normalize(p.Language) != normalized.Language).Append(normalized).ToArray(),
            Version = ++Revision
        });
    }
    public void SaveVoiceDefinition(VoiceDefinition definition, ModelAssetDefinition? asset = null)
    {
        lock (gate)
        {
            var assets = (Configuration.ModelAssets ?? []).Where(a => a.AssetId != asset?.AssetId).ToList();
            if (asset is not null) assets.Add(asset);
            var defs = (Configuration.VoiceDefinitions ?? []).Where(d => d.VoiceId != definition.VoiceId).Append(definition).ToArray();
            Save(Configuration with { ModelAssets = assets, VoiceDefinitions = defs, Version = ++Revision });
        }
    }
    public void SaveCharacterVoiceBinding(CharacterVoiceBinding binding)
    {
        lock (gate)
        {
            var next = binding with { Version = Revision + 1, UpdatedUtc = DateTimeOffset.UtcNow };
            var bindings = (Configuration.GameCharacterBindings ?? [])
                .Where(b => !(string.Equals(b.GameId, next.GameId, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(b.CharacterId, next.CharacterId, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(b.Language, next.Language, StringComparison.OrdinalIgnoreCase)))
                .Append(next).ToArray();
            Save(Configuration with { GameCharacterBindings = bindings, Version = ++Revision });
        }
    }
    private void Save(VoiceConfiguration config)
    {
        var text = JsonSerializer.Serialize(config, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, text); File.Move(tmp, path, true); Configuration = config;
        VersionChanged?.Invoke(Revision);
    }
}

public sealed record SpeechLine(string SourceKey, string ChapterId, string StoryId, string TaskId, string DialogueId,
    string RecognitionNodeKey, string TargetNodeKey, string TextLanguage, string Text, string CharacterId, string SpeakerKey,
    string MappingFingerprint, VoiceProfile Profile);
public sealed record SpeechResolution(SpeechLine? Line, string Status);
public sealed class SpeechLineResolver(DialogueLanguageMap map, VoiceProfileStore profiles, LiveLanguageOptions options, QuestTextContext context, IVoiceRoutingProvider? automaticRouting = null)
{
    public SpeechResolution Resolve(string nodeKey)
    {
        var entry = map.ForNode(nodeKey);
        if (entry?.Status != MappingStatus.Matched) return new(null, entry?.Status.ToString() ?? "MissingTarget");
        var source = entry.Node(options.RecognitionLanguage); var target = entry.Node(options.SpeechLanguage);
        if (source?.Key != nodeKey || target is null) return new(null, "SourceLanguageMismatch");
        var text = target.Texts.Single().Template.Render(context).Replace("\\n", " ").Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text) || Regex.IsMatch(text, @"\{[^}]*\}")) return new(null, "UnrenderedTemplate");
        string character;
        VoiceProfile? profile;
        var status = "Ready";
        if (automaticRouting is not null && entry.Chs is not null)
        {
            // The paired CHS role is the canonical cross-language identity, even when OCR reads JP.
            // Missing voice bindings use a language-appropriate fallback, never a text translation.
            var routed = automaticRouting.Resolve(entry.Chs, options.SpeechLanguage, context.Gender);
            character = routed.CharacterId; profile = routed.Profile; status = routed.Reason;
            if (profile is null) return new(null, status);
        }
        else
        {
            var legacyCharacter = profiles.Character(target, options.SpeechLanguage, context.Gender);
            if (legacyCharacter is null) return new(null, "Unresolved / unconfirmed character");
            character = legacyCharacter;
            profile = profiles.Profile(character, options.SpeechLanguage);
            if (profile is null) return new(null, "Missing target-language VoiceProfile");
        }
        var errors = profile.Validate();
        if (errors.Count > 0) return new(null, string.Join("; ", errors));
        return new(new("yatta", source.ChapterId, source.StoryId, source.TaskId, source.Id, source.Key, target.Key,
            QuestLanguages.Normalize(options.SpeechLanguage), text, character, target.Speaker?.SpeakerKey ?? "unknown", map.Fingerprint, profile), status);
    }
}
