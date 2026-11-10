using QuestVoice.Core;

namespace QuestVoice.Games.Genshin;

/// <summary>
/// Full-capability Game Adapter for Genshin Impact (原神).
/// Owns Genshin ROIs, UIGate crossed-eye/option-bubble scene detection, Yatta script catalog,
/// cross-language (CHS <-> JP) node mapping, and pre-synthesis capability.
/// </summary>
public sealed class GenshinGameAdapter :
    IGameAdapter,
    IDialogueSceneDetector,
    IDialogueExtractor,
    ISpeakerResolver,
    IScriptCatalogProvider,
    ILanguageTextMapper,
    IChoiceDetector,
    IPreSynthesisSource
{
    public static readonly NormRect DefaultDialogueRoi = NormRect.From(0.03, 0.77, 0.97, 0.95);
    public static readonly NormRect DefaultOcrRoi = NormRect.From(0.03, 0.77, 0.97, 0.95);
    public static readonly NormRect DefaultBodyVisualRoi = NormRect.From(0.04, 0.835, 0.96, 0.95);
    // Historical glyph evaluation uses a tighter body-only range than live OCR.
    // Kept as a named adapter default so other adapters/tests can supply their own ROI.
    public static readonly NormRect DefaultRecognitionBodyROI = NormRect.From(0.15, 0.84, 0.85, 0.925);
    public static readonly NormRect DefaultNameRoi = NormRect.From(0.28, 0.78, 0.72, 0.83);
    public static readonly NormRect DefaultEyeRoi = NormRect.From(0.10, 0.0, 0.25, 0.09);
    public static readonly NormRect DefaultChoiceRoi = NormRect.From(0.58, 0.38, 0.98, 0.82);

    private readonly UIGate uiGate = new();
    private Func<string, string?>? roleMatcher;

    public string GameId => "genshin";
    public string DisplayName => "原神";
    public override string ToString() => DisplayName;
    public string StatusSummary => "已验证：支持实时对话门控、Yatta 在线剧情图、中日原文同 ID 映射与 P1 预合成";
    public bool IsVerified => true;

    public GameCapabilities Capabilities { get; } = new(
        SupportsSceneDetection: true,
        SupportsChoiceDetection: true,
        SupportsScriptCatalog: true,
        SupportsScriptResolution: true,
        SupportsCrossLanguageMapping: true,
        SupportsPreSynthesis: true,
        SupportsConfigurableRoi: false);

    public IReadOnlyList<string> DefaultProcessNames { get; } = ["YuanShen", "GenshinImpact"];

    public IDialogueSceneDetector SceneDetector => this;
    public IDialogueExtractor Extractor => this;
    public ISpeakerResolver SpeakerResolver => this;
    public IScriptCatalogProvider? ScriptCatalog => this;
    public ILanguageTextMapper? LanguageMapper => this;
    public IChoiceDetector? ChoiceDetector => this;
    public IPreSynthesisSource? PreSynthesisSource => this;

    // IDialogueExtractor
    public NormRect DialogueRoi => DefaultDialogueRoi;
    public NormRect NameRoi => DefaultNameRoi;
    public NormRect BodyRoi => DefaultBodyVisualRoi;
    public NormRect? EyeRoi => DefaultEyeRoi;
    public NormRect? ChoiceRoi => DefaultChoiceRoi;

    // IDialogueSceneDetector
    public UIGateResult ObserveScene(VisualFrame frame, bool targetKnown = false)
        => uiGate.Observe(frame, targetKnown);

    public void Reset() => uiGate.Reset();

    // ISpeakerResolver
    public void BindRoleMatcher(Func<string, string?>? matcher) => roleMatcher = matcher;

    public (string CharacterId, string DisplayRole, string Evidence) ResolveSpeaker(
        string ocrSpeaker, string? currentQuestRole = null, TravelerGender? gender = null)
    {
        var raw = (ocrSpeaker ?? "").Trim();
        var matched = raw.Length > 0 ? roleMatcher?.Invoke(raw) ?? "" : "";
        if (matched.Length > 0)
            return (matched, matched, "Observed");
        if (!string.IsNullOrWhiteSpace(currentQuestRole))
            return (currentQuestRole, currentQuestRole, "Inherited");
        if (raw.Length > 0 && !raw.All(c => c is '?' or '？'))
            return ("default_npc", raw, "OcrOnly");
        return ("unknown", "", "Unknown");
    }

    // IScriptCatalogProvider
    public string SourceKey => "yatta";
    public IQuestDataSource CreateDataSource(string? versionHash = null) => new YattaQuestDataSource(versionHash);

    // ILanguageTextMapper
    public bool CanMap(string recognitionLanguage, string voiceLanguage)
    {
        try
        {
            _ = QuestLanguages.Normalize(recognitionLanguage);
            _ = QuestLanguages.Normalize(voiceLanguage);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public DialogueLanguageMap CreateLanguageMap(QuestDocument chs, QuestDocument jp, string? chsVersion = null, string? jpVersion = null)
        => new(chs, jp, chsVersion, jpVersion, SourceKey);

    // IChoiceDetector
    public bool IsChoiceWaiting(UIGateResult gate) => gate.ChoiceWaiting || gate.OptionRows > 0;

    // IPreSynthesisSource
    public bool SupportsPreSynthesis => true;
}
