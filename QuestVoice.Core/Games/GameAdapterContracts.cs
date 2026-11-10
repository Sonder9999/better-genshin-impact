namespace QuestVoice.Core;

public sealed record GameCapabilities(
    bool SupportsSceneDetection = true,
    bool SupportsChoiceDetection = false,
    bool SupportsScriptCatalog = false,
    bool SupportsScriptResolution = false,
    bool SupportsCrossLanguageMapping = false,
    bool SupportsPreSynthesis = false,
    bool SupportsConfigurableRoi = false);

public sealed record DialogueObservation(
    string GameId,
    string OcrSpeaker,
    string OcrBody,
    string RecognitionLanguage,
    NormRect NameRoi,
    NormRect BodyRoi,
    double SourceMs,
    double AvailableMs,
    long? SceneInstanceId,
    bool IsReliable);

public sealed record DialogueResolution(
    string GameId,
    string? DialogueNodeKey,
    string? DialogueId,
    string CharacterId,
    string SpeakerDisplay,
    string SourceLanguage,
    string TargetLanguage,
    string TargetText,
    string EvidenceSource,
    string ConfidenceState,
    bool IsTrustedQuestNode);

public interface IDialogueSceneDetector
{
    UIGateResult ObserveScene(VisualFrame frame, bool targetKnown = false);
    void Reset();
}

public interface IDialogueExtractor
{
    NormRect DialogueRoi { get; }
    NormRect NameRoi { get; }
    NormRect BodyRoi { get; }
    NormRect? EyeRoi { get; }
    NormRect? ChoiceRoi { get; }
}

public interface ISpeakerResolver
{
    (string CharacterId, string DisplayRole, string Evidence) ResolveSpeaker(
        string ocrSpeaker, string? currentQuestRole = null, TravelerGender? gender = null);
}

public interface IScriptCatalogProvider
{
    string SourceKey { get; }
    IQuestDataSource CreateDataSource(string? versionHash = null);
}

public interface IScriptResolver
{
    DialogueResolution ResolveFromScript(
        DialogueObservation observation,
        string? confirmedNodeKey,
        string targetLanguage,
        QuestTextContext context);
}

public interface ILanguageTextMapper
{
    bool CanMap(string recognitionLanguage, string voiceLanguage);
    DialogueLanguageMap CreateLanguageMap(QuestDocument chs, QuestDocument jp, string? chsVersion = null, string? jpVersion = null);
}

public interface IChoiceDetector
{
    bool IsChoiceWaiting(UIGateResult gate);
}

public interface IPreSynthesisSource
{
    bool SupportsPreSynthesis { get; }
}

/// <summary>
/// Capability-composed Game Adapter contract. Optional capabilities are explicitly exposed
/// rather than faked with empty implementations.
/// </summary>
public interface IGameAdapter
{
    string GameId { get; }
    string DisplayName { get; }
    string StatusSummary { get; }
    bool IsVerified { get; }
    GameCapabilities Capabilities { get; }
    IReadOnlyList<string> DefaultProcessNames { get; }
    IDialogueSceneDetector SceneDetector { get; }
    IDialogueExtractor Extractor { get; }
    ISpeakerResolver SpeakerResolver { get; }
    IScriptCatalogProvider? ScriptCatalog { get; }
    ILanguageTextMapper? LanguageMapper { get; }
    IChoiceDetector? ChoiceDetector { get; }
    IPreSynthesisSource? PreSynthesisSource { get; }
}

public sealed record GameProfileConfig(
    int SchemaVersion,
    string GameId,
    string DisplayName,
    string[] ProcessNames,
    string DefaultRecognitionLanguage,
    string DefaultVoiceLanguage,
    double NameRoiLeft,
    double NameRoiTop,
    double NameRoiRight,
    double NameRoiBottom,
    double BodyRoiLeft,
    double BodyRoiTop,
    double BodyRoiRight,
    double BodyRoiBottom)
{
    public const int CurrentSchemaVersion = 1;
    public NormRect GetNameRoi() => NormRect.From(NameRoiLeft, NameRoiTop, NameRoiRight, NameRoiBottom);
    public NormRect GetBodyRoi() => NormRect.From(BodyRoiLeft, BodyRoiTop, BodyRoiRight, BodyRoiBottom);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Unsupported GameProfile schemaVersion={SchemaVersion} (expected {CurrentSchemaVersion}).");
        if (string.IsNullOrWhiteSpace(GameId) || GameId.Length > 48)
            errors.Add("GameId is required and must be <= 48 chars.");
        try { _ = GetNameRoi(); } catch (Exception e) { errors.Add("Invalid NameRoi: " + e.Message); }
        try { _ = GetBodyRoi(); } catch (Exception e) { errors.Add("Invalid BodyRoi: " + e.Message); }
        return errors;
    }
}
