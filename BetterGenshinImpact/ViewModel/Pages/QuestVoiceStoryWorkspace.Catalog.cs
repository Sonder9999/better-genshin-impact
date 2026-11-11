using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuestVoice.Core;
using QuestVoice.Games.Genshin;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>Flattened, virtualized hierarchy row. A folder is navigation, not an implicit selection of every chapter.</summary>
public sealed record QuestVoiceCatalogRow(string Key, string Kind, string Title, string Detail,
    int Depth, YattaChapter? Chapter = null, string? StoryId = null, string? TaskId = null,
    bool CanExpand = false, bool Expanded = false)
{
    public string ExpandMarker => CanExpand ? (Expanded ? "−" : "+") : "·";
    public Thickness Indent => new(Math.Min(Depth, 3) * 18, 0, 0, 0);
    public string DisplayKind => Kind switch
    {
        "series" => "系列", "chapter" => "章节", "story" => "主任务",
        "task" => "子任务", _ => Kind
    };
}

/// <summary>
/// Shares the existing global chapter and actual Yatta documents; unlike a second selector,
/// expanding/searching here never fetches or generates audio. Task focus limits the P1 scope only.
/// </summary>
public partial class QuestVoiceStoryWorkspace
{
    private static readonly Regex ChapterSuffix = new(
        @"[\s·・:：-]*(序章|终章|終章|最终章|最終章|第[一二三四五六七八九十百零〇0-9]+章)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HashSet<string> _expandedCatalog = new(StringComparer.Ordinal);
    private bool _rebuildingCatalog;

    public ObservableCollection<QuestVoiceCatalogRow> CatalogRows { get; } = [];

    [ObservableProperty] private QuestVoiceCatalogRow? _selectedCatalogRow;
    [ObservableProperty] private string _activeStoryId = "";
    [ObservableProperty] private string _activeTaskId = "";
    [ObservableProperty] private string _activeSelectionLabel = "请选择左侧的章节";
    [ObservableProperty] private string _taskFilterStatus = "选定章节后展开可查看主任务与子任务";
    public IEnumerable<QuestVoiceDialogueRow> VisibleLines => Lines.Where(row =>
        (string.IsNullOrEmpty(ActiveStoryId) || row.Mapping.Chs?.StoryId == ActiveStoryId) &&
        (string.IsNullOrEmpty(ActiveTaskId) || row.Mapping.Chs?.TaskId == ActiveTaskId));

    private static string ChapterNum(YattaChapter chapter) =>
        chapter.Raw.ValueKind == JsonValueKind.Object &&
        chapter.Raw.TryGetProperty("chapterNum", out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    private static string SeriesName(YattaChapter chapter)
    {
        var chapterNum = ChapterNum(chapter);
        var name = ChapterSuffix.Replace(chapterNum, "").Trim(' ', '·', '・');
        return name.Length > 0 && name != chapterNum ? name : "";
    }

    private void RefreshCatalogRows()
    {
        if (_rebuildingCatalog) return;
        _rebuildingCatalog = true;
        try
        {
            var search = ChapterSearch.Trim();
            bool Searching(string s) => search.Length > 0 && s.Contains(search, StringComparison.OrdinalIgnoreCase);
            var next = new List<QuestVoiceCatalogRow>();
            var all = Chapters.ToArray();
            foreach (var grouping in all.Where(x => SeriesName(x).Length > 0)
                         .GroupBy(SeriesName, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.CurrentCulture))
            {
                var chapters = grouping.OrderBy(ChapterNum).ThenBy(x => x.Title, StringComparer.CurrentCulture).ToArray();
                var seriesKey = "series/" + grouping.Key;
                var matchingChapters = chapters.Where(c => Searching(c.Label) || Searching(ChapterNum(c))).ToArray();
                if (search.Length > 0 && !Searching(grouping.Key) && matchingChapters.Length == 0
                    && !chapters.Any(c => c.Id == SelectedChapter?.Id && LoadedTaskMatchesSearch())) continue;
                bool open = _expandedCatalog.Contains(seriesKey) || search.Length > 0;
                next.Add(new(seriesKey, "series", grouping.Key, chapters.Length + " 章",
                    0, null, CanExpand: true, Expanded: open));
                if (!open) continue;
                foreach (var chapter in chapters)
                    AppendChapter(next, chapter, 1, search, Searching(grouping.Key) || Searching(chapter.Label) || Searching(ChapterNum(chapter)));
            }

            foreach (var chapter in all.Where(x => SeriesName(x).Length == 0)
                         .OrderBy(x => x.Title, StringComparer.CurrentCulture))
                AppendChapter(next, chapter, 0, search, Searching(chapter.Label) || Searching(ChapterNum(chapter)));

            CatalogRows.Clear();
            foreach (var row in next) CatalogRows.Add(row);
            OnPropertyChanged(nameof(VisibleLines));
        }
        finally { _rebuildingCatalog = false; }
    }

    private bool LoadedTaskMatchesSearch() =>
        _pair is not null && _pair.Chs.Stories.Any(story =>
            story.Title?.Contains(ChapterSearch.Trim(), StringComparison.OrdinalIgnoreCase) == true ||
            story.Tasks.Any(task => task.Title?.Contains(ChapterSearch.Trim(), StringComparison.OrdinalIgnoreCase) == true));

    private void AppendChapter(List<QuestVoiceCatalogRow> rows, YattaChapter chapter, int depth,
        string search, bool ancestorMatches)
    {
        var selected = SelectedChapter?.Id == chapter.Id && _pair is not null;
        var taskMatch = selected && LoadedTaskMatchesSearch();
        if (search.Length > 0 && !ancestorMatches && !taskMatch) return;
        var key = "chapter/" + chapter.Id;
        var canExpand = selected && _pair!.Chs.Stories.Count > 0;
        var open = canExpand && (_expandedCatalog.Contains(key) || search.Length > 0);
        var number = ChapterNum(chapter);
        rows.Add(new(key, "chapter", chapter.Title, number.Length > 0 ? number : "点击加载双语任务",
            depth, chapter, CanExpand: canExpand, Expanded: open));
        if (!open) return;
        foreach (var story in _pair!.Chs.Stories)
        {
            var storyKey = key + "/story/" + story.StoryId;
            var storyMatch = story.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
            var matchingTasks = story.Tasks.Where(t => t.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (search.Length > 0 && !ancestorMatches && !storyMatch && matchingTasks.Length == 0) continue;
            var storyOpen = _expandedCatalog.Contains(storyKey) || search.Length > 0;
            rows.Add(new(storyKey, "story", story.Title ?? "未命名主任务",
                story.Tasks.Count + " 个子任务", depth + 1, chapter, story.StoryId,
                CanExpand: story.Tasks.Count > 0, Expanded: storyOpen));
            if (!storyOpen) continue;
            foreach (var task in story.Tasks)
            {
                if (search.Length > 0 && !ancestorMatches && !storyMatch &&
                    task.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) != true) continue;
                rows.Add(new(storyKey + "/task/" + task.TaskId, "task",
                    task.Title ?? "未命名子任务", "选中后聚焦该子任务的台词",
                    depth + 2, chapter, story.StoryId, task.TaskId));
            }
        }
    }

    [RelayCommand]
    private void ToggleCatalogRow(QuestVoiceCatalogRow? row)
    {
        if (row is not { CanExpand: true }) return;
        if (!_expandedCatalog.Add(row.Key)) _expandedCatalog.Remove(row.Key);
        RefreshCatalogRows();
    }

    partial void OnSelectedCatalogRowChanged(QuestVoiceCatalogRow? row)
    {
        if (_rebuildingCatalog || row is null || IsGenerating) return;
        // 组标题仅用于导航；展开动作统一由 [+] 按钮触发，防止点击按钮时与选中事件双重切换。
        if (row.Kind == "series") return;

        if (row.Chapter is not { } chapter) return;
        if (SelectedChapter?.Id != chapter.Id)
        {
            ActiveStoryId = row.StoryId ?? "";
            ActiveTaskId = row.TaskId ?? "";
            SelectedChapter = Chapters.FirstOrDefault(c => c.Id == chapter.Id) ?? chapter;
        }
        else
        {
            ActiveStoryId = row.StoryId ?? "";
            ActiveTaskId = row.TaskId ?? "";
            UpdateCurrentScope();
        }
    }

    partial void OnActiveTaskIdChanged(string value) => UpdateCurrentScope();
    partial void OnActiveStoryIdChanged(string value) => UpdateCurrentScope();

    private void UpdateCurrentScope()
    {
        ActiveSelectionLabel = SelectedChapter?.Title is { } title
            ? title + (ActiveTaskId.Length > 0 ? " · 当前子任务" : ActiveStoryId.Length > 0 ? " · 当前主任务" : " · 全章节")
            : "请选择左侧的章节";
        TaskFilterStatus = ActiveTaskId.Length > 0 ? $"已聚焦子任务 · {VisibleLines.Count()} 条台词"
            : ActiveStoryId.Length > 0 ? $"已聚焦主任务 · {VisibleLines.Count()} 条台词"
            : $"当前章节 · {Lines.Count} 条台词";
        OnPropertyChanged(nameof(VisibleLines));
    }

    private void OnCatalogLoaded()
    {
        _expandedCatalog.Clear();
        if (SelectedChapter is not null)
        {
            var series = SeriesName(SelectedChapter);
            if (series.Length > 0) _expandedCatalog.Add("series/" + series);
        }
        RefreshCatalogRows();
    }

    private void OnChapterContentLoaded()
    {
        if (SelectedChapter is { } chapter)
        {
            var series = SeriesName(chapter);
            if (series.Length > 0) _expandedCatalog.Add("series/" + series);
            _expandedCatalog.Add("chapter/" + chapter.Id);
        }
        RefreshCatalogRows();
        UpdateCurrentScope();
    }
}
