using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterGenshinImpact.GameTask.QuestVoice.Core.Routing;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>
/// 当前 Yatta 剧情只提供角色姓名来源；所有声线绑定仍写回唯一的 routing.json 草稿。
/// 自动匹配采用明确的角色名/语言后缀，拒绝同名歧义，不以模糊相似度猜测角色。
/// </summary>
public partial class QuestVoiceManagementViewModel
{
    /// <summary>从当前双语剧情得到的去重角色姓名，切换剧情不丢失既有角色档案。</summary>
    private readonly HashSet<string> _storyNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本次匹配目标语言，仅用于筛选显式标记语言的声线名称。</summary>
    private string _storyLanguage = "jpn";

    /// <summary>角色列表可切换仅显示当前剧情，不影响下方绑定或文件内容。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredSpeakers))]
    private bool _showCurrentStoryOnly;

    /// <summary>从已加载目录中找出当前章节出现的已建档角色。</summary>
    public IEnumerable<QuestVoiceManagementSpeaker> CurrentStorySpeakers => Speakers.Where(IsStorySpeaker);

    /// <summary>未建档者仍以原始游戏名显示，必须点击按钮才会写入草稿。</summary>
    public IEnumerable<string> MissingStorySpeakers => _storyNames
        .Where(name => !Speakers.Any(item => NamesOf(item).Contains(name, StringComparer.OrdinalIgnoreCase)))
        .OrderBy(name => name, StringComparer.CurrentCulture);

    /// <summary>当前剧情的角色数、已建档数和待建档数。</summary>
    public string CurrentStorySummary
    {
        get
        {
            var linked = CurrentStorySpeakers.Count();
            return $"当前剧情角色 {_storyNames.Count} · 已建档 {linked} · 待建档 {MissingStorySpeakers.Count()}";
        }
    }

    /// <summary>当前剧情筛选叠加用户搜索，不改变任何持久数据。</summary>
    private bool IsStorySpeaker(QuestVoiceManagementSpeaker speaker) =>
        NamesOf(speaker).Any(name => _storyNames.Contains(name));

    /// <summary>严格比较角色正式名、别名和 ID，避免「派蒙」误匹配到包含这个词的另一人。</summary>
    private static IEnumerable<string> NamesOf(QuestVoiceManagementSpeaker speaker) =>
        speaker.Names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(speaker.Aliases.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Append(speaker.Id.Trim());

    /// <summary>由唯一全局剧情状态广播姓名；无需重新读取 Yatta 数据或 routing.json。</summary>
    public void SetCurrentStorySpeakers(IEnumerable<string> names, string language)
    {
        _storyNames.Clear();
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
            _storyNames.Add(name.Trim());
        _storyLanguage = language is "jpn" or "chs" ? language : "jpn";
        NotifyStorySpeakersChanged();
    }

    /// <summary>管理草稿发生改变时刷新当前章节的过滤列表和未匹配状态。</summary>
    private void NotifyStorySpeakersChanged()
    {
        OnPropertyChanged(nameof(CurrentStorySpeakers));
        OnPropertyChanged(nameof(MissingStorySpeakers));
        OnPropertyChanged(nameof(CurrentStorySummary));
        OnPropertyChanged(nameof(FilteredSpeakers));
    }

    /// <summary>用户显式选择后，把未建档的剧情角色创建为独立草稿；不自动分配声线。</summary>
    [RelayCommand(CanExecute = nameof(CanEditDraft))]
    private void AddMissingStorySpeakers() => Edit(() =>
    {
        var missing = MissingStorySpeakers.ToArray();
        foreach (var name in missing)
        {
            var id = UniqueId("story", Speakers.SelectMany(x => new[] { x.Id, x.OriginalId }));
            AddSpeakerItem(new QuestVoiceManagementSpeaker(
                new SpeakerDefinition(id, [name], Array.Empty<string>(), Array.Empty<string>(), null)));
        }

        if (missing.Length > 0)
        {
            MarkDraftChanged();
            NotifyLists();
        }
        EditStatus = missing.Length == 0
            ? "当前剧情中的角色已全部建档。"
            : $"已添加 {missing.Length} 位角色的待绑定档案；未猜测任何声线。";
    });

    /// <summary>
    /// 只绑定明确同名或附有本次语言标记的唯一声音；有多条候选、已有绑定或名称不全时保持不变。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditDraft))]
    private void AutoBindCurrentStoryVoices() => Edit(() =>
    {
        var changed = 0;
        var ambiguous = 0;
        foreach (var speaker in CurrentStorySpeakers.Where(item => string.IsNullOrWhiteSpace(item.VoiceId)).ToArray())
        {
            var names = NamesOf(speaker).Where(name => name != speaker.Id).ToArray();
            var candidates = Profiles.Where(profile =>
                names.Any(name => IsExplicitVoiceMatch(profile.Name, name, _storyLanguage))).ToArray();
            if (candidates.Length > 1) { ambiguous++; continue; }
            if (candidates.Length != 1) continue;
            // 只绑定已存在于草稿中的有效 Profile；资源校验仍由原有自动保存器执行。
            speaker.VoiceId = candidates[0].Id;
            changed++;
        }

        if (changed > 0) { MarkDraftChanged(); NotifyLists(); }
        EditStatus = $"本次按名称精确绑定 {changed} 位角色；有 {ambiguous} 位出现多条候选，已保留待手动选择。";
    });

    /// <summary>语言后缀必须独立于角色名；不把角色名子串误判为完整声线。</summary>
    private static bool IsExplicitVoiceMatch(string voiceName, string role, string language)
    {
        var candidate = voiceName.Trim();
        if (string.Equals(candidate, role, StringComparison.OrdinalIgnoreCase)) return true;
        var suffixes = language == "jpn"
            ? new[] { "_JA", "_JP", "-JA", "-JP", " 日语", " 日本語" }
            : new[] { "_ZH", "_CHS", "-ZH", "-CHS", " 中文", " 汉语" };
        return suffixes.Any(suffix =>
            string.Equals(candidate, role + suffix, StringComparison.OrdinalIgnoreCase));
    }
}
