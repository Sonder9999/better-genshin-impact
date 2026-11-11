using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuestVoice.Core;
using QuestVoice.Games.Genshin;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>One click loads the whole series; rows are choices, not folders.</summary>
public sealed record QuestVoiceCatalogRow(
    string Key, string Kind, string Title, string Detail, IReadOnlyList<string> ChapterIds,
    YattaChapter? Chapter = null)
{
    public string DisplayKind => Kind == "series" ? "系列" : "章节";
}

public partial class QuestVoiceStoryWorkspace
{
    private static readonly Regex ChapterSuffix = new(
        @"[\s·・:：-]*(序章|终章|終章|最终章|最終章|第[一二三四五六七八九十百零〇0-9]+章)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<DialogueLanguageMap> _loadedPairs = [];
    private bool _rebuildingCatalog;
    private bool _selectingInternally;
    public ObservableCollection<QuestVoiceCatalogRow> CatalogRows { get; } = [];
    public ObservableCollection<QuestVoiceTranscriptRow> TranscriptRows { get; private set; } = [];

    [ObservableProperty] private QuestVoiceCatalogRow? _selectedCatalogRow;
    [ObservableProperty] private QuestVoiceTranscriptRow? _selectedTranscriptRow;
    [ObservableProperty] private string _activeSelectionLabel = "请选择剧情系列或章节";
    [ObservableProperty] private string _taskFilterStatus = "选中系列将加载全部章节及其任务";
    /// <summary>All loaded dialogue rows are the P1 synthesis scope.</summary>
    public IEnumerable<QuestVoiceDialogueRow> VisibleLines => Lines;
    public IReadOnlyList<DialogueLanguageMap> LoadedPairs => _loadedPairs;

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

    /// <summary>Chronological chapter order (序章, 第一章, 第二章...) instead of lexical Chinese character order.</summary>
    private static int ChapterOrder(YattaChapter chapter)
    {
        var label = ChapterNum(chapter);
        if (label.EndsWith("序章", StringComparison.Ordinal)) return 0;
        if (label.EndsWith("终章", StringComparison.Ordinal) ||
            label.EndsWith("終章", StringComparison.Ordinal)) return 1000;
        var match = Regex.Match(label, @"第([一二三四五六七八九十百零〇0-9]+)章$");
        if (!match.Success) return 500;
        var value = match.Groups[1].Value;
        if (int.TryParse(value, out var number)) return number;
        const string digits = "一二三四五六七八九";
        static int Digit(char c) => c switch
        {
            '一' => 1, '二' => 2, '三' => 3, '四' => 4, '五' => 5,
            '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => 0
        };
        if (value.Length == 1) return value == "十" ? 10 : Digit(value[0]);
        var tens = value.IndexOf('十');
        if (tens >= 0)
        {
            var prefix = tens == 0 ? 1 : Digit(value[0]);
            var ones = tens + 1 == value.Length ? 0 : Digit(value[tens + 1]);
            return prefix * 10 + ones;
        }
        return 500;
    }
    private static bool Matches(YattaChapter c, string term) =>
        c.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        ChapterNum(c).Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>No nested clicks: series and standalone chapters share the same list.</summary>
    private void RefreshCatalogRows()
    {
        if (_rebuildingCatalog) return;
        _rebuildingCatalog = true;
        try
        {
            var term = ChapterSearch.Trim();
            var all = Chapters.ToArray();
            var next = new List<QuestVoiceCatalogRow>();
            foreach (var group in all.Where(c => SeriesName(c).Length > 0)
                         .GroupBy(SeriesName, StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var chapters = group.OrderBy(ChapterOrder)
                    .ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
                var matchesSeries = term.Length == 0 ||
                    group.Key.Contains(term, StringComparison.OrdinalIgnoreCase);
                if (matchesSeries || chapters.Any(c => Matches(c, term)))
                    next.Add(new("series/" + group.Key, "series", group.Key,
                        chapters.Length + " 章",
                        chapters.Select(c => c.Id).ToArray()));
                // Searching a specific chapter can still choose only that chapter;
                // normal browsing never requires expanding series.
                if (term.Length > 0 && !matchesSeries)
                    foreach (var c in chapters.Where(c => Matches(c, term)))
                        next.Add(new("chapter/" + c.Id, "chapter", c.Title,
                            ChapterNum(c), [c.Id], c));
            }
            foreach (var c in all.Where(c => SeriesName(c).Length == 0)
                         .OrderBy(c => c.Title, StringComparer.CurrentCulture))
            {
                if (term.Length > 0 && !Matches(c, term)) continue;
                next.Add(new("chapter/" + c.Id, "chapter", c.Title, "", [c.Id], c));
            }

            var currentKey = SelectedCatalogRow?.Key;
            CatalogRows.Clear();
            foreach (var row in next) CatalogRows.Add(row);
            // Preserve the selected series across search updates without an accidental reload.
            var restored = next.FirstOrDefault(r => r.Key == currentKey);
            if (!ReferenceEquals(SelectedCatalogRow, restored))
                SelectedCatalogRow = restored;
        }
        finally { _rebuildingCatalog = false; }
    }

    partial void OnSelectedCatalogRowChanged(QuestVoiceCatalogRow? row)
    {
        if (_rebuildingCatalog || _selectingInternally || row is null || IsGenerating) return;
        if (row.ChapterIds.Count == 0 || row.ChapterIds.Count > 100)
        {
            ChapterStatus = "系列章节数无效（最多一次加载 100 章）。";
            return;
        }
        var chapters = row.ChapterIds.Select(id => Chapters.FirstOrDefault(c => c.Id == id)).ToArray();
        if (chapters.Any(c => c is null))
        {
            ChapterStatus = "目录已更新，章节不存在，请刷新目录重试。";
            return;
        }
        _config.SelectedChapterIds = row.ChapterIds.ToArray();
        _config.SelectedChapterId = row.ChapterIds[0]; // legacy single-chapter fallback
        _selectingInternally = true;
        try { SelectedChapter = chapters[0]; }
        finally { _selectingInternally = false; }
        ActiveSelectionLabel = row.Title;
        _ = LoadSelectionAsync(chapters.Select(c => c!).ToArray(), row.Title);
    }

    /// <summary>Retry the same whole-series selection after a transient Yatta failure; never accept a partial subset.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ReloadSelectedSelectionAsync()
    {
        if (IsGenerating || SelectedCatalogRow is not { } row) return;
        var chapters = row.ChapterIds.Select(id => Chapters.FirstOrDefault(c => c.Id == id)).ToArray();
        if (chapters.Any(c => c is null))
        {
            ChapterStatus = "无法重试：章节目录已变化，请刷新目录。";
            return;
        }
        await LoadSelectionAsync(chapters.Select(c => c!).ToArray(), row.Title);
    }
    private void UpdateCurrentScope()
    {
        ActiveSelectionLabel = SelectedCatalogRow?.Title ?? "请选择剧情";
        OnPropertyChanged(nameof(VisibleLines));
    }
    /// <summary>Restore a whole series when legacy settings only named its first chapter.</summary>
    private void OnCatalogLoaded()
    {
        RefreshCatalogRows();
        var saved = _config.SelectedChapterIds is { Length: > 0 }
            ? _config.SelectedChapterIds
            : string.IsNullOrWhiteSpace(_config.SelectedChapterId) ? [] : [_config.SelectedChapterId];
        if (saved.Length == 0) return;
        var row = CatalogRows.FirstOrDefault(r => r.ChapterIds.SequenceEqual(saved, StringComparer.Ordinal))
            ?? CatalogRows.FirstOrDefault(r => r.Kind == "series" && r.ChapterIds.Contains(saved[0]))
            ?? CatalogRows.FirstOrDefault(r => r.ChapterIds.Count == 1 && r.ChapterIds[0] == saved[0]);
        if (row is not null) SelectedCatalogRow = row;
    }
}
