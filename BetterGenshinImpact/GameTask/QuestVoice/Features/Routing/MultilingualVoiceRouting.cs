using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using BetterGenshinImpact.GameTask.QuestVoice.Domain;

namespace BetterGenshinImpact.GameTask.QuestVoice.Genshin;

/// <summary>
/// Read-only adapter for BetterGI's existing speaker/voice catalog.
/// The application owns only a small language overlay; no UI-level role confirmation is needed.
/// Supports aggregate models shared across multiple reference audios and emotion tags.
/// </summary>
public sealed record VoiceAssetSettings(string Id, string Name, string OriginalLanguage,
    string ProviderId, string InstanceId, string GptWeights, string SovitsWeights,
    string ReferenceAudio, string PromptText, string PromptLanguage, string? PresetVoiceId = null);

public sealed class MultilingualVoiceRouting : IVoiceRoutingProvider
{
    private readonly object overrideGate = new();
    private string overrideFile = "";
    private string voiceAssetsFile = "";
    private Dictionary<string, VoiceAssetSettings> voiceEdits = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> selectedVoices = new(StringComparer.OrdinalIgnoreCase);
    private sealed record VoiceAsset(string Id, string DisplayName, string ProviderId, string InstanceId,
        string GptWeights, string SovitsWeights, string ReferenceAudio, string PromptText,
        string PromptLanguage, IReadOnlyList<ReferenceAudioEntry>? ExtraReferences = null, string? PresetVoiceId = null);
    private sealed record Speaker(string Id, string? VoiceId, string[] Names, string[] Aliases, string[] Corrections);
    private readonly Speaker[] speakers;
    private readonly Dictionary<string, VoiceAsset> chineseVoices;
    private readonly Dictionary<string, VoiceAsset> japaneseVoices;
    private readonly Dictionary<string, string> japaneseOverrides;
    private readonly string[] playerNames;
    private readonly string? protagonistId, narratorVoiceId, chineseFallbackId, japaneseFallbackId;
    public string CatalogPath { get; }
    public int SpeakerCount => speakers.Length;
    public int ChineseVoiceCount => chineseVoices.Count;
    public int JapaneseVoiceCount => japaneseVoices.Count;
    public int PendingModelCount { get; private set; }
    public int BindingVersion { get; private set; } = 1;
    public event Action<int>? BindingVersionChanged;

    public IReadOnlyList<(string Id, string DisplayName)> AllSpeakers =>
        speakers.Select(s => (s.Id, s.Names.FirstOrDefault() ?? s.Id)).ToArray();

    public IReadOnlyList<RegisteredVoice> AvailableVoices(string targetLanguage)
    {
        var language = QuestLanguages.Normalize(targetLanguage);
        return chineseVoices.Values.Select(v => ToChoice(v, "chs", language))
            .Concat(japaneseVoices.Values.Select(v => ToChoice(v, "jpn", language)))
            .OrderByDescending(v => v.OriginalLanguage == language)
            .ThenBy(v => v.Name, StringComparer.CurrentCulture)
            .ToArray();
    }
    private static RegisteredVoice ToChoice(VoiceAsset v, string sourceLanguage, string targetLanguage)
    {
        var name = string.IsNullOrWhiteSpace(v.DisplayName) ? "未命名声线" : v.DisplayName;
        var prefix = sourceLanguage == "chs" ? "中文 · " : "日语 · ";
        var cross = sourceLanguage == targetLanguage ? "" : "（跨语言，需确认模型支持）";
        var modelId = SpeechHash.Of((v.GptWeights + "|" + v.SovitsWeights).ToLowerInvariant())[..10];
        return new(v.Id, prefix + name + cross, sourceLanguage,
            (v.ProviderId == "gpt-sovits" ? File.Exists(v.GptWeights) && File.Exists(v.SovitsWeights) &&
            File.Exists(v.ReferenceAudio) && !string.IsNullOrWhiteSpace(v.PromptText) : !string.IsNullOrWhiteSpace(v.PresetVoiceId)),
            v.ProviderId, modelId);
    }
    public string? VoiceOverride(QuestNode chineseNode, string language)
    {
        lock (overrideGate)
            return selectedVoices.GetValueOrDefault(OverrideKey(chineseNode, language));
    }
    public string? VoiceOverrideBySpeakerKey(string speakerKey, string language)
    {
        lock (overrideGate)
            return selectedVoices.GetValueOrDefault(QuestLanguages.Normalize(language) + "/" + speakerKey);
    }
    public void SetVoiceOverride(QuestNode chineseNode, string language, string? voiceId)
        => SetVoiceOverrideBySpeakerKey(SpeakerKey(chineseNode), language, voiceId);

    public void SetVoiceOverrideBySpeakerKey(string speakerKey, string language, string? voiceId)
    {
        if (!string.IsNullOrWhiteSpace(voiceId) && !chineseVoices.ContainsKey(voiceId) &&
            !japaneseVoices.ContainsKey(voiceId))
            throw new InvalidOperationException("所选声线已不在本地声线库中。");
        var key = QuestLanguages.Normalize(language) + "/" + speakerKey;
        int nextVersion;
        lock (overrideGate)
        {
            var updated = new Dictionary<string, string>(selectedVoices, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(voiceId)) updated.Remove(key);
            else updated[key] = voiceId;
            if (string.IsNullOrWhiteSpace(overrideFile)) throw new InvalidOperationException("私人声线配置未初始化。");
            Directory.CreateDirectory(Path.GetDirectoryName(overrideFile)!);
            var temp = overrideFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, overrideFile, true);
                selectedVoices = updated;
                nextVersion = ++BindingVersion;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        BindingVersionChanged?.Invoke(nextVersion);
    }
    public string SpeakerKey(QuestNode chineseNode)
    {
        var speaker = FindSpeaker(chineseNode.Role, null);
        return speaker?.Id ?? "role:" + chineseNode.Role.Trim();
    }
    private string OverrideKey(QuestNode chineseNode, string language)
        => QuestLanguages.Normalize(language) + "/" + SpeakerKey(chineseNode);
    /// <summary>Human-facing display name; catalog IDs remain only in cache and diagnostics.</summary>
    public string VoiceDisplayName(string voiceId)
    {
        if (chineseVoices.TryGetValue(voiceId, out var chinese))
            return string.IsNullOrWhiteSpace(chinese.DisplayName) ? "中文声线" : chinese.DisplayName;
        if (japaneseVoices.TryGetValue(voiceId, out var japanese))
            return string.IsNullOrWhiteSpace(japanese.DisplayName) ? "日语声线" : japanese.DisplayName;
        return "未配置";
    }

    private MultilingualVoiceRouting(string catalogPath, Speaker[] speakers,
        Dictionary<string, VoiceAsset> chineseVoices, Dictionary<string, VoiceAsset> japaneseVoices,
        Dictionary<string, string> japaneseOverrides, string[] playerNames, string? protagonistId,
        string? narratorVoiceId, string? chineseFallbackId, string? japaneseFallbackId)
    {
        CatalogPath = catalogPath;
        this.speakers = speakers;
        this.chineseVoices = chineseVoices;
        this.japaneseVoices = japaneseVoices;
        this.japaneseOverrides = japaneseOverrides;
        this.playerNames = playerNames;
        this.protagonistId = protagonistId;
        this.narratorVoiceId = narratorVoiceId;
        this.chineseFallbackId = chineseFallbackId;
        this.japaneseFallbackId = japaneseFallbackId;
    }

    public static MultilingualVoiceRouting? Load(string applicationRoot)
    {
        var settingsPath = Path.Combine(applicationRoot, "config", "multilingual-routing.json");
        if (!File.Exists(settingsPath)) return null;
        using var settingsDocument = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var settings = settingsDocument.RootElement;
        var catalogField = RequiredString(settings, "BetterGiRouting");
        var catalogPath = FullPath(Path.GetDirectoryName(settingsPath)!, catalogField);
        using var catalogDocument = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var catalog = catalogDocument.RootElement;
        if (RequiredString(catalog, "Game") != "genshin")
            throw new InvalidDataException("Only BetterGI Genshin routing catalogs are supported.");
        if (catalog.GetProperty("Version").GetInt32() != 2)
            throw new InvalidDataException("BetterGI routing catalog version 2 is required.");

        var catalogDirectory = Path.GetDirectoryName(catalogPath)!;
        var speakerList = catalog.GetProperty("Speakers").EnumerateArray()
            .Select(s => new Speaker(RequiredString(s, "Id"), OptionalString(s, "VoiceId"),
                Strings(s, "Names"), Strings(s, "Aliases"), Strings(s, "Corrections"))).ToArray();
        var chineseVoices = catalog.GetProperty("Voices").EnumerateArray()
            .Select(v => Voice(v, catalogDirectory, "chs"))
            .ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var languages = settings.GetProperty("Languages");
        var chineseFallback = OptionalString(languages.GetProperty("chs"), "FallbackVoiceId")
            ?? OptionalString(catalog, "FallbackVoiceId");
        var japaneseFallback = OptionalString(languages.GetProperty("jpn"), "FallbackVoiceId");
        var jap = settings.GetProperty("JapaneseVoices").EnumerateObject()
            .Select(kv => Voice(kv.Value, Path.GetDirectoryName(settingsPath)!, "jpn", kv.Name))
            .ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var pending = 0;
        // Generated discovery catalog lives in Release/config, separately from the user's
        // BetterGI routing and manually customized language configuration.
        var importedFile = Path.Combine(applicationRoot, "config", "imported-voices.json");
        if (File.Exists(importedFile))
        {
            using var importDoc = JsonDocument.Parse(File.ReadAllText(importedFile));
            var importRoot = importDoc.RootElement;
            if (importRoot.TryGetProperty("PendingModels", out var pendingItems) &&
                pendingItems.ValueKind == JsonValueKind.Array)
                pending = pendingItems.GetArrayLength();
            if (importRoot.TryGetProperty("Voices", out var importedVoices) &&
                importedVoices.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in importedVoices.EnumerateArray())
                {
                    var lang = RequiredString(model, "OriginalLanguage");
                    var dictionary = lang switch
                    {
                        "chs" => chineseVoices,
                        "jpn" => jap,
                        _ => throw new InvalidDataException("Unsupported voice language in imported registry: " + lang)
                    };
                    var voice = Voice(model, Path.GetDirectoryName(importedFile)!, lang);
                    if (!dictionary.TryAdd(voice.Id, voice))
                        throw new InvalidDataException("Duplicate imported voice id: " + voice.Id);
                }
            }
        }
        var overrides = settings.GetProperty("JapaneseVoiceOverrides").EnumerateObject()
            .ToDictionary(kv => kv.Name, kv => kv.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        var routing = new MultilingualVoiceRouting(catalogPath, speakerList, chineseVoices, jap, overrides,
            Strings(catalog, "PlayerNames"), OptionalString(catalog, "ProtagonistId"),
            OptionalString(catalog, "NarratorVoiceId"), chineseFallback, japaneseFallback);
        routing.PendingModelCount = pending;
        routing.overrideFile = Path.Combine(applicationRoot, "config", "voice-overrides.json");
        if (File.Exists(routing.overrideFile))
            routing.selectedVoices = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(routing.overrideFile)) is { } saved
                    ? new(saved, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        routing.voiceAssetsFile = Path.Combine(applicationRoot, "config", "voice-asset-overrides.json");
        if (File.Exists(routing.voiceAssetsFile))
        {
            var edits = JsonSerializer.Deserialize<Dictionary<string, VoiceAssetSettings>>(
                File.ReadAllText(routing.voiceAssetsFile));
            if (edits is not null)
                foreach (var setting in edits.Values)
                {
                    if (routing.GetVoiceAssetSettings(setting.Id) is null) continue;
                    routing.ApplyVoiceAsset(setting);
                    routing.voiceEdits[setting.Id] = setting;
                }
        }
        return routing;
    }

    /// <summary>Always establish a shared identity from the paired CHS node, never by translating JP names.</summary>
    public (string CharacterId, VoiceProfile? Profile, string Reason) Resolve(
        QuestNode chineseNode, string targetLanguage, TravelerGender? gender)
    {
        var language = QuestLanguages.Normalize(targetLanguage);
        var speaker = FindSpeaker(chineseNode.Role, gender);
        var characterId = speaker?.Id ?? "default_npc";
        string? requestedVoice = null;
        var voices = language == "chs" ? chineseVoices : japaneseVoices;
        var fallbackId = language == "chs" ? chineseFallbackId : japaneseFallbackId;
        if (language == "chs")
        {
            requestedVoice = speaker?.VoiceId;
            if (speaker is null && IsNarration(chineseNode.Role)) requestedVoice = narratorVoiceId;
        }
        else if (speaker is not null)
        {
            if (!japaneseOverrides.TryGetValue(speaker.Id, out requestedVoice))
                requestedVoice = FindJapaneseVoice(speaker);
        }

        // Explicit per-role selection overrides the automatic route for every chapter.
        // Never silently replace a selected voice if its assets are missing.
        var overrideId = VoiceOverride(chineseNode, language);
        if (!string.IsNullOrWhiteSpace(overrideId))
        {
            VoiceAsset? overrideAsset = chineseVoices.GetValueOrDefault(overrideId)
                ?? japaneseVoices.GetValueOrDefault(overrideId);
            if (overrideAsset is null)
                return (characterId, null, "SelectedVoiceUnavailable");
            var selectedProfile = Profile(overrideAsset, characterId, language);
            var selectedIssues = selectedProfile.Validate();
            return selectedIssues.Count == 0
                ? (characterId, selectedProfile, "SelectedVoice")
                : (characterId, null, "SelectedVoiceAssetsUnavailable: " + string.Join("; ", selectedIssues));
        }

        var usedFallback = false;
        var reason = speaker is null ? "UnknownCharacter" : "VoiceNotConfigured";
        VoiceAsset? asset = null;
        if (!string.IsNullOrWhiteSpace(requestedVoice) && voices.TryGetValue(requestedVoice, out var selected))
        {
            asset = selected;
            reason = "CharacterVoice";
        }
        if (asset is not null && !Valid(asset, language, characterId) && reason == "CharacterVoice")
            return (characterId, null, "CharacterVoiceAssetsUnavailable: " + asset.DisplayName);
        if (asset is null || !Valid(asset, language, characterId))
        {
            if (asset is not null) reason = "VoiceAssetsUnavailable";
            usedFallback = true;
            if (string.IsNullOrWhiteSpace(fallbackId) || !voices.TryGetValue(fallbackId, out asset))
                return (characterId, null, "Missing " + language + " fallback voice: " + fallbackId);
        }
        var profile = Profile(asset!, characterId, language);
        var issues = profile.Validate();
        if (issues.Count != 0)
            return (characterId, null, "Invalid " + language + " " + (usedFallback ? "fallback" : "character") + " voice: " + string.Join("; ", issues));
        return (characterId, profile, usedFallback ? "Fallback: " + reason + " → " + profile.VoiceId : "CharacterVoice");
    }

    private string? FindJapaneseVoice(Speaker speaker)
    {
        // Only match exact, canonical Genshin display names; no fuzzy or JP-name guessing.
        // Imported voices are not trusted as character identities without this unique match.
        var names = speaker.Names.Select(n => n.Replace(" ", "", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = japaneseVoices.Values.Where(v =>
        {
            var label = v.DisplayName.Replace(" ", "", StringComparison.Ordinal);
            return label.StartsWith("原神·", StringComparison.Ordinal) &&
                   names.Contains(label["原神·".Length..]);
        }).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0].Id : null;
    }

    public VoiceAssetSettings? GetVoiceAssetSettings(string voiceId)
    {
        var a = chineseVoices.GetValueOrDefault(voiceId) ?? japaneseVoices.GetValueOrDefault(voiceId);
        if (a is null) return null;
        var lang = japaneseVoices.ContainsKey(voiceId) ? "jpn" : "chs";
        return new(a.Id, a.DisplayName, lang, a.ProviderId, a.InstanceId,
            a.GptWeights, a.SovitsWeights, a.ReferenceAudio, a.PromptText, a.PromptLanguage, a.PresetVoiceId);
    }

    public VoiceProfile? PreviewVoiceProfile(string voiceId, string language)
    {
        var asset = chineseVoices.GetValueOrDefault(voiceId) ?? japaneseVoices.GetValueOrDefault(voiceId);
        if (asset is null) return null;
        var profile = Profile(asset, "voice-preview", QuestLanguages.Normalize(language));
        return profile.Validate().Count == 0 ? profile : null;
    }

    private void ApplyVoiceAsset(VoiceAssetSettings settings)
    {
        var prior = chineseVoices.GetValueOrDefault(settings.Id) ?? japaneseVoices.GetValueOrDefault(settings.Id)
            ?? throw new InvalidOperationException("声线不存在，无法更新。");
        var lang = QuestLanguages.Normalize(settings.OriginalLanguage);
        var asset = prior with
        {
            DisplayName = settings.Name.Trim(),
            ProviderId = settings.ProviderId.Trim(),
            InstanceId = settings.InstanceId.Trim(),
            GptWeights = settings.GptWeights.Trim(),
            SovitsWeights = settings.SovitsWeights.Trim(),
            ReferenceAudio = settings.ReferenceAudio.Trim(),
            PromptText = settings.PromptText,
            PromptLanguage = QuestLanguages.Normalize(settings.PromptLanguage),
            PresetVoiceId = settings.PresetVoiceId
        };
        chineseVoices.Remove(settings.Id);
        japaneseVoices.Remove(settings.Id);
        (lang == "chs" ? chineseVoices : japaneseVoices)[settings.Id] = asset;
    }

    public void SaveVoiceAsset(VoiceAssetSettings settings)
    {
        if (GetVoiceAssetSettings(settings.Id) is null) throw new InvalidOperationException("未知声线。");
        if (string.IsNullOrWhiteSpace(settings.Name) || string.IsNullOrWhiteSpace(settings.ProviderId) ||
            string.IsNullOrWhiteSpace(settings.InstanceId)) throw new InvalidOperationException("请填写声线名称、引擎及实例。");
        QuestLanguages.Normalize(settings.OriginalLanguage);
        QuestLanguages.Normalize(settings.PromptLanguage);
        if (settings.ProviderId != "gpt-sovits" && string.IsNullOrWhiteSpace(settings.PresetVoiceId))
            throw new InvalidOperationException("请先填写所选引擎对应的音色编号。");
        if (string.IsNullOrWhiteSpace(voiceAssetsFile)) throw new InvalidOperationException("声线配置目录尚未初始化。");
        int version;
        lock (overrideGate)
        {
            var updated = new Dictionary<string, VoiceAssetSettings>(voiceEdits, StringComparer.OrdinalIgnoreCase)
            { [settings.Id] = settings };
            Directory.CreateDirectory(Path.GetDirectoryName(voiceAssetsFile)!);
            var tmp = voiceAssetsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, voiceAssetsFile, true);
                ApplyVoiceAsset(settings);
                voiceEdits = updated;
                version = ++BindingVersion;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        BindingVersionChanged?.Invoke(version);
    }

    public VoiceProfile? ResolveAmbient(string characterId, string language)
    {
        var normalized = QuestLanguages.Normalize(language);
        var speaker = speakers.FirstOrDefault(s => string.Equals(s.Id, characterId, StringComparison.OrdinalIgnoreCase));
        var explicitVoice = VoiceOverrideBySpeakerKey(characterId, normalized);
        var voiceId = !string.IsNullOrWhiteSpace(explicitVoice)
            ? explicitVoice
            : normalized == "chs" ? speaker?.VoiceId
                : (speaker is not null && japaneseOverrides.TryGetValue(speaker.Id, out var configured)
                    ? configured : speaker is null ? null : FindJapaneseVoice(speaker));
        var voices = normalized == "chs" ? chineseVoices : japaneseVoices;
        var fallback = normalized == "chs" ? chineseFallbackId : japaneseFallbackId;
        var matched = !string.IsNullOrWhiteSpace(voiceId);
        VoiceAsset? asset = null;
        if (matched)
        {
            asset = chineseVoices.GetValueOrDefault(voiceId!) ?? japaneseVoices.GetValueOrDefault(voiceId!);
            // A configured or unique character-specific voice must not silently become another character.
            if (asset is null || !Valid(asset, normalized, characterId)) return null;
        }
        if (asset is null && (fallback is null || !voices.TryGetValue(fallback, out asset))) return null;
        var profile = Profile(asset!, string.IsNullOrWhiteSpace(characterId) ? "default_npc" : characterId, normalized);
        return profile.Validate().Count == 0 ? profile : null;
    }

    private Speaker? FindSpeaker(string name, TravelerGender? gender)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().All(c => c is '?' or '？')) return null;
        var normalized = name.Trim();
        if ((playerNames.Any(n => Same(n, normalized)) || Same(normalized, "旅行者")) && protagonistId is not null)
            return speakers.FirstOrDefault(s => Same(s.Id, protagonistId));
        // Matching precedence follows BetterGI: canonical name, alias, then OCR correction.
        foreach (var selector in new Func<Speaker, string[]>[] { s => s.Names, s => s.Aliases, s => s.Corrections })
        {
            var matches = speakers.Where(s => selector(s).Any(n => Same(n, normalized))).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) return null; // Do not silently select an ambiguous speaker.
        }
        return null;
    }
    private static bool IsNarration(string name) => name is "旁白" or "叙述" or "旁白者";
    private static bool Same(string x, string y) => string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Valid(VoiceAsset a, string lang, string characterId)
        => Profile(a, characterId, lang).Validate().Count == 0;
    private static VoiceProfile Profile(VoiceAsset a, string characterId, string language) => new()
    {
        ProfileId = characterId + "/" + language + "/" + a.Id,
        CharacterId = characterId, Language = language, VoiceId = a.ProviderId == "gpt-sovits" ? a.Id : a.PresetVoiceId ?? "",
        ProviderId = a.ProviderId, InstanceId = a.InstanceId,
        GptWeights = a.GptWeights, SovitsWeights = a.SovitsWeights,
        ReferenceAudio = a.ReferenceAudio, PromptText = a.PromptText, PromptLanguage = a.PromptLanguage,
        ExtraReferences = a.ExtraReferences ?? [],
        Enabled = true
    };
    private static VoiceAsset Voice(JsonElement v, string dir, string lang, string? key = null)
    {
        var id = key ?? RequiredString(v, "Id");
        var instanceId = OptionalString(v, "InstanceId");
        var extraRefs = new List<ReferenceAudioEntry>();
        if (v.TryGetProperty("References", out var refs) && refs.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var r in refs.EnumerateArray())
            {
                var rPath = OptionalString(r, "Path") ?? OptionalString(r, "ReferenceAudioPath");
                var rPrompt = OptionalString(r, "PromptText");
                if (!string.IsNullOrWhiteSpace(rPath) && !string.IsNullOrWhiteSpace(rPrompt))
                    extraRefs.Add(new($"{id}#ref{++idx}", FullPath(dir, rPath), rPrompt,
                        OptionalString(r, "PromptLanguage") ?? lang, OptionalString(r, "Emotion") ?? "default"));
            }
        }
        return new(id, OptionalString(v, "Name") ?? (id == "jp.sakura" ? "夜乃樱" : ""),
            OptionalString(v, "ProviderId") ?? "gpt-sovits",
            string.IsNullOrWhiteSpace(instanceId) ? "local" : instanceId,
            FullPath(dir, RequiredString(v, "GptWeightsPath")),
            FullPath(dir, RequiredString(v, "SovitsWeightsPath")),
            FullPath(dir, RequiredString(v, "ReferenceAudioPath")),
            RequiredString(v, "PromptText"), OptionalString(v, "PromptLanguage") ?? lang,
            extraRefs, OptionalString(v, "PresetVoiceId"));
    }
    private static string FullPath(string dir, string path) => Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(dir, path));
    private static string RequiredString(JsonElement obj, string field) =>
        OptionalString(obj, field) ?? throw new InvalidDataException("Missing voice routing field: " + field);
    private static string? OptionalString(JsonElement obj, string field) =>
        obj.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    private static string[] Strings(JsonElement obj, string field) =>
        obj.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.Array
            ? prop.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];
}
