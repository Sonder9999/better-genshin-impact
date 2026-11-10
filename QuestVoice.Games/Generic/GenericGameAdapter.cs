using QuestVoice.Core;

namespace QuestVoice.Games.Generic;

/// <summary>
/// Generic Game Adapter for arbitrary games without an online quest catalog.
/// Users calibrate NameRoi and BodyRoi; performs causal text-change detection -> OCR -> TTS
/// without inventing fake quest IDs or claiming script/pre-synthesis capabilities.
/// </summary>
public sealed class GenericGameAdapter :
    IGameAdapter,
    IDialogueSceneDetector,
    IDialogueExtractor,
    ISpeakerResolver
{
    private NormRect nameRoi;
    private NormRect bodyRoi;
    private NormRect dialogueRoi;
    private double lastTime = double.NegativeInfinity;

    public GenericGameAdapter(GameProfileConfig? profile = null)
    {
        var cfg = profile ?? CreateDefaultProfile();
        nameRoi = cfg.GetNameRoi();
        bodyRoi = cfg.GetBodyRoi();
        dialogueRoi = NormRect.From(
            Math.Min(nameRoi.Left, bodyRoi.Left),
            Math.Min(nameRoi.Top, bodyRoi.Top),
            Math.Max(nameRoi.Right, bodyRoi.Right),
            Math.Max(nameRoi.Bottom, bodyRoi.Bottom));
        DefaultProcessNames = cfg.ProcessNames;
    }

    public static GameProfileConfig CreateDefaultProfile() => new(
        SchemaVersion: GameProfileConfig.CurrentSchemaVersion,
        GameId: "generic",
        DisplayName: "通用游戏 (自定义 ROI)",
        ProcessNames: [],
        DefaultRecognitionLanguage: "chs",
        DefaultVoiceLanguage: "chs",
        NameRoiLeft: 0.25,
        NameRoiTop: 0.76,
        NameRoiRight: 0.75,
        NameRoiBottom: 0.82,
        BodyRoiLeft: 0.08,
        BodyRoiTop: 0.82,
        BodyRoiRight: 0.92,
        BodyRoiBottom: 0.94);

    public string GameId => "generic";
    public string DisplayName => "通用游戏";
    public override string ToString() => DisplayName;
    public string StatusSummary => "通用模式：通过校准角色名与正文 ROI 直接执行 OCR → 声线路由 → 实时 TTS，无外部剧情库依赖";
    public bool IsVerified => true;

    public GameCapabilities Capabilities { get; } = new(
        SupportsSceneDetection: true,
        SupportsChoiceDetection: false,
        SupportsScriptCatalog: false,
        SupportsScriptResolution: false,
        SupportsCrossLanguageMapping: false,
        SupportsPreSynthesis: false,
        SupportsConfigurableRoi: true);

    public IReadOnlyList<string> DefaultProcessNames { get; private set; }

    public IDialogueSceneDetector SceneDetector => this;
    public IDialogueExtractor Extractor => this;
    public ISpeakerResolver SpeakerResolver => this;
    public IScriptCatalogProvider? ScriptCatalog => null;
    public ILanguageTextMapper? LanguageMapper => null;
    public IChoiceDetector? ChoiceDetector => null;
    public IPreSynthesisSource? PreSynthesisSource => null;

    public NormRect DialogueRoi => dialogueRoi;
    public NormRect NameRoi => nameRoi;
    public NormRect BodyRoi => bodyRoi;
    public NormRect? EyeRoi => null;
    public NormRect? ChoiceRoi => null;

    public void UpdateRoi(NormRect nextNameRoi, NormRect nextBodyRoi)
    {
        nameRoi = nextNameRoi;
        bodyRoi = nextBodyRoi;
        dialogueRoi = NormRect.From(
            Math.Min(nameRoi.Left, bodyRoi.Left),
            Math.Min(nameRoi.Top, bodyRoi.Top),
            Math.Max(nameRoi.Right, bodyRoi.Right),
            Math.Max(nameRoi.Bottom, bodyRoi.Bottom));
    }

    public UIGateResult ObserveScene(VisualFrame frame, bool targetKnown = false)
    {
        if (frame.TimestampSeconds <= lastTime)
            throw new ArgumentException("Generic scene frames require increasing PTS.");
        lastTime = frame.TimestampSeconds;
        var lit = 0; var count = 0;
        for (var y = (int)(frame.Height * .12); y < frame.Height * .95; y += Math.Max(1, frame.Height / 36))
            for (var x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 64))
            {
                var p = (y * frame.Width + x) * 3; count++;
                if (Math.Max(frame.Rgb[p], Math.Max(frame.Rgb[p + 1], frame.Rgb[p + 2])) > 20) lit++;
            }
        var black = count == 0 || (double)lit / count < .02;
        return new UIGateResult(
            TimestampSeconds: frame.TimestampSeconds,
            Eye: black ? EyeState.Hidden : EyeState.Highlighted,
            EyeScore: black ? 0.0 : 1.0,
            OptionRows: 0,
            DialogueVisible: !black,
            ChoiceWaiting: false,
            TargetKnown: targetKnown,
            AllowVisualCuts: !black,
            AllowTargetAudio: !black && targetKnown,
            BlackTransition: black);
    }

    public void Reset() => lastTime = double.NegativeInfinity;

    public (string CharacterId, string DisplayRole, string Evidence) ResolveSpeaker(
        string ocrSpeaker, string? currentQuestRole = null, TravelerGender? gender = null)
    {
        var cleaned = (ocrSpeaker ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.All(c => c is '?' or '？'))
            return ("default_npc", "未知角色", "Unknown");
        return ("role:" + cleaned, cleaned, "OcrDirect");
    }
}
