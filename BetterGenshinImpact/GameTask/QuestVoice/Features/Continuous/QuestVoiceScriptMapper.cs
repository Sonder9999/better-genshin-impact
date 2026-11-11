using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using QuestVoice.Core;
using QuestVoice.Games.Genshin;
using Serilog;

namespace BetterGenshinImpact.GameTask.QuestVoice.Features.Continuous;

/// <summary>从严格双语节点配对得到的可信目标台词，不包含 OCR 原文推测或机器翻译。</summary>
public sealed record QuestVoiceScriptMatch(string NodeKey, string TargetText, string Language, string Speaker);

/// <summary>
/// 原神在线剧本的只读 CHS→JP 映射。只允许当前选定章节内、双语严格配对且文本唯一的长句。
/// 短句、重句、结构节点、角色冲突均不自动匹配，避免把相似的剧情台词当作同一个节点。
/// </summary>
public sealed class QuestVoiceScriptMapper
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private sealed record ScriptRow(string Key, string Speaker, string Chinese, string Target);
    private readonly Dictionary<string, ScriptRow[]> _byChinese;

    /// <summary>加载到的可信台词数量，供诊断与验证使用。</summary>
    public int SpeakableLines { get; }
    /// <summary>用作本轮实际合成的目标语言。</summary>
    public string TargetLanguage { get; }

    private QuestVoiceScriptMapper(IEnumerable<ScriptRow> rows, string language)
    {
        TargetLanguage = language;
        var accepted = rows.Where(row => !string.IsNullOrWhiteSpace(row.Target)).ToArray();
        SpeakableLines = accepted.Length;
        _byChinese = accepted.GroupBy(row => Normalize(row.Chinese), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// 中文 OCR 必须与唯一原文对应；观察到角色时还需角色名严格相同。
    /// 长句无角色信息时仅在整个章节全局唯一时接受；不提供语义近似猜测。
    /// </summary>
    public QuestVoiceScriptMatch? TryMatch(string chineseOcr, string? observedSpeaker)
    {
        var normalized = Normalize(chineseOcr);
        if (normalized.Length < 6 || !_byChinese.TryGetValue(normalized, out var candidates))
            return null;

        ScriptRow[] matches;
        if (!string.IsNullOrWhiteSpace(observedSpeaker))
        {
            var name = Normalize(observedSpeaker);
            matches = candidates.Where(row => Normalize(row.Speaker) == name).ToArray();
        }
        else if (normalized.Length >= 10)
        {
            matches = candidates;
        }
        else return null;

        if (matches.Length != 1) return null;
        var selected = matches[0];
        return new(selected.Key, selected.Target, TargetLanguage == "jpn" ? "ja" : "zh", selected.Speaker);
    }

    /// <summary>统一全角、空格和标点；不删除实义文字，不把短句送入高风险模糊检索。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Normalize(NormalizationForm.FormKC);
        return string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)));
    }

    /// <summary>用已严格校验的 CHS/JP 配对建立当前会话的只读映射，便于离线验证。</summary>
    public static QuestVoiceScriptMapper FromMapping(DialogueLanguageMap mapping, string nickname, bool femaleTraveler)
    {
        var context = new QuestTextContext(nickname,
            femaleTraveler ? TravelerGender.Female : TravelerGender.Male);
        string Render(QuestNode? node) => node?.Texts.FirstOrDefault()?.Template.Render(context)?.Trim() ?? "";
        var rows = mapping.Entries
            .Where(row => row.Status == MappingStatus.Matched &&
                row.Chs is { Type: "SingleDialog" } && row.Jp is { Type: "SingleDialog" })
            .Select(row => new ScriptRow(row.Chs!.Key, row.Chs.Role, Render(row.Chs), Render(row.Jp)))
            .Where(row => !string.IsNullOrWhiteSpace(row.Chinese) && !string.IsNullOrWhiteSpace(row.Target));
        return new QuestVoiceScriptMapper(rows, "jpn");
    }

    /// <summary>从 Yatta 缓存加载当前选中章节，超时不阻止实时 P0 启动，失败退回原文本合成。</summary>
    public static async Task<QuestVoiceScriptMapper?> TryLoadAsync(
        QuestVoiceConfig config, string programDirectory, CancellationToken token)
    {
        if (!config.ScriptLanguageRoutingEnabled || config.SpeechLanguage != "jpn" ||
            string.IsNullOrWhiteSpace(config.SelectedChapterId)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var source = new YattaQuestDataSource();
            var store = new QuestJSONStore(System.IO.Path.Combine(programDirectory, "User", "QuestVoice"), "cache/quests");
            var loader = new QuestDataLoader(source, Http, store);
            var mapping = await QuestLanguagePairLoader.LoadAsync(loader, config.SelectedChapterId,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var matcher = FromMapping(mapping, config.TravelerNickname, config.FemaleTraveler);
            Log.Information("QuestVoice Yatta CHS→JP script ready chapter={Chapter} lines={Count}",
                config.SelectedChapterId, matcher.SpeakableLines);
            return matcher.SpeakableLines == 0 ? null : matcher;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Log.Warning(error, "QuestVoice Yatta script route unavailable chapter={Chapter}; P0 falls back to original text",
                config.SelectedChapterId);
            return null;
        }
    }
}
