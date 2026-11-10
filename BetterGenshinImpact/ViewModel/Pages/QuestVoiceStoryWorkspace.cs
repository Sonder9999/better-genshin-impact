using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.QuestVoice;
using BetterGenshinImpact.GameTask.QuestVoice.Features.PreSynthesis;
using BetterGenshinImpact.Service;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuestVoice.Core;
using QuestVoice.Games.Genshin;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>双语节点显示行，状态由实际 P1 请求更新。</summary>
public partial class QuestVoiceDialogueRow(DialogueMapping mapping, string speaker, string chinese, string japanese)
    : ObservableObject
{
    /// <summary>来源节点身份，供结果与剧情选择关联。</summary>
    public DialogueMapping Mapping { get; } = mapping;
    /// <summary>来源图中唯一节点键。</summary>
    public string Key => Mapping.Chs?.Key ?? Mapping.Jp!.Key;
    /// <summary>发言角色。</summary>
    public string Speaker { get; } = speaker;
    /// <summary>简体中文台词。</summary>
    public string Chinese { get; } = chinese;
    /// <summary>日文台词。</summary>
    public string Japanese { get; } = japanese;
    /// <summary>只有结构一致且唯一的双语对白才允许预合成。</summary>
    public bool CanSynthesize => Mapping.Status == MappingStatus.Matched && !string.IsNullOrWhiteSpace(Chinese) && !string.IsNullOrWhiteSpace(Japanese);
    /// <summary>逐项合成结果。</summary>
    [ObservableProperty] private string _state = mapping.Status == MappingStatus.Matched ? "待生成" : mapping.Status.ToString();
}

/// <summary>
/// BetterGI 原生设置窗口中的全局剧情状态；只加载 Yatta 正式数据，不接入任何视频测试入口。
/// </summary>
public partial class QuestVoiceStoryWorkspace : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly QuestVoiceSessionFactory _factory;
    private readonly QuestVoiceConfig _config;
    private readonly QuestVoicePreSynthesisService _preSynthesis;
    private readonly QuestDataLoader _loader;
    private readonly YattaQuestDataSource _source = new();
    private readonly string _catalogPath;
    private CancellationTokenSource? _selectionCancellation;
    private CancellationTokenSource? _batchCancellation;
    private bool _catalogLoaded;
    private DialogueLanguageMap? _pair;
    private bool _pauseRequested;

    /// <summary>单一全局配置，同步角色与引擎页面。</summary>
    public QuestVoiceConfig Config => _config;
    /// <summary>全局章节目录。</summary>
    public ObservableCollection<YattaChapter> Chapters { get; } = [];
    /// <summary>当前章节的中日双语节点，列表容器负责虚拟化。</summary>
    public ObservableCollection<QuestVoiceDialogueRow> Lines { get; } = [];
    /// <summary>搜索仅筛选视图，不改变全局剧情选择。</summary>
    public IEnumerable<YattaChapter> FilteredChapters => string.IsNullOrWhiteSpace(ChapterSearch)
        ? Chapters : Chapters.Where(c => c.Label.Contains(ChapterSearch.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>章节列表搜索文本。</summary>
    [ObservableProperty] private string _chapterSearch = "";
    /// <summary>当前全局剧情章节。</summary>
    [ObservableProperty] private YattaChapter? _selectedChapter;
    /// <summary>当前聚焦的台词。</summary>
    [ObservableProperty] private QuestVoiceDialogueRow? _selectedLine;
    /// <summary>目录与台词加载诊断。</summary>
    [ObservableProperty] private string _catalogStatus = "未载入剧情目录";
    /// <summary>当前章节状态。</summary>
    [ObservableProperty] private string _chapterStatus = "请从左侧选择剧情";
    /// <summary>所选节点前后上下文。</summary>
    [ObservableProperty] private string _focusContext = "选择任意对白查看前后关系";
    /// <summary>P1 队列进度文本。</summary>
    [ObservableProperty] private string _preSynthesisStatus = "尚未开始预合成";
    /// <summary>已经完成的条数（包括缓存命中）。</summary>
    [ObservableProperty] private int _completed;
    /// <summary>需要处理的总条数。</summary>
    [ObservableProperty] private int _total;
    /// <summary>已复用的 WAV 数量。</summary>
    [ObservableProperty] private int _cacheHits;
    /// <summary>生成失败的条数。</summary>
    [ObservableProperty] private int _failed;
    /// <summary>后台是否正在运行。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isGenerating;
    /// <summary>预合成时全局章节与语种不允许被改写。</summary>
    public bool IsEditable => !IsGenerating;
    /// <summary>用于右侧资源操作区。</summary>
    public string CacheDirectory => _preSynthesis.CacheDirectory;
    /// <summary>UI 显示进度，避免除以零。</summary>
    public double ProgressPercent => Total == 0 ? 0 : Math.Clamp(100d * Completed / Total, 0, 100);

    /// <summary>复用 BetterGI 的后台服务及配置，不新增独立 TTS 账号/设置。</summary>
    public QuestVoiceStoryWorkspace(QuestVoiceSessionFactory factory, QuestVoiceConfig config, QuestVoiceService live)
    {
        _factory = factory;
        _config = config;
        _preSynthesis = new(factory, config, live);
        _catalogPath = Path.Combine(factory.ProgramDirectory, "User", "QuestVoice", "cache", "catalog-chs.json");
        _loader = new QuestDataLoader(_source, Http, new QuestJSONStore(factory.ProgramDirectory, "User/QuestVoice/cache/quests"));
        config.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(QuestVoiceConfig.SpeechLanguage) or
                nameof(QuestVoiceConfig.TravelerNickname) or nameof(QuestVoiceConfig.FemaleTraveler))
                if (!IsGenerating) RebuildLines();
        };
    }

    /// <summary>搜索时无需发网络请求。</summary>
    partial void OnChapterSearchChanged(string value) => OnPropertyChanged(nameof(FilteredChapters));

    /// <summary>章节切换只发生于本次的一个全局选择。</summary>
    partial void OnSelectedChapterChanged(YattaChapter? value)
    {
        if (value is null || IsGenerating) return;
        _config.SelectedChapterId = value.Id;
        _ = LoadChapterAsync(value);
    }

    /// <summary>聚焦行向用户显示上下文关系。</summary>
    partial void OnSelectedLineChanged(QuestVoiceDialogueRow? value)
    {
        var index = value is null ? -1 : Lines.IndexOf(value);
        FocusContext = index < 0 ? "选择任意对白查看前后关系" :
            "前置：" + (index > 0 ? Lines[index - 1].Chinese : "章节起始") +
            "  →  当前：" + value!.Chinese +
            "  →  后续：" + (index + 1 < Lines.Count ? Lines[index + 1].Chinese : "章节结束");
    }

    /// <summary>首次打开设置窗口只加载一次目录；失败可手动刷新。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (!_catalogLoaded) await LoadCatalogAsync(false);
    }

    /// <summary>手动强制向 Yatta 请求最新章节目录。</summary>
    [RelayCommand]
    private Task RefreshCatalogAsync() => LoadCatalogAsync(true);

    /// <summary>先用已验证缓存，失败再联网；网络异常时保留已经有的目录。</summary>
    private async Task LoadCatalogAsync(bool forceRefresh)
    {
        CatalogStatus = "正在加载原神剧情目录…";
        try
        {
            byte[] bytes;
            bool cache = !forceRefresh && File.Exists(_catalogPath);
            if (cache)
            {
                try
                {
                    bytes = await File.ReadAllBytesAsync(_catalogPath);
                    _ = YattaQuestCatalogReader.Read(bytes);
                }
                catch (Exception) { cache = false; bytes = []; }
            }
            else bytes = [];

            if (!cache)
            {
                using var response = await Http.GetAsync(_source.GetCatalogUri("chs"));
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("剧情目录超过 16 MiB。");
                _ = YattaQuestCatalogReader.Read(bytes);
                Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
                var temporary = _catalogPath + "." + Guid.NewGuid().ToString("N");
                try { await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, _catalogPath, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }

            var records = YattaQuestCatalogReader.Read(bytes);
            Chapters.Clear();
            foreach (var chapter in records.OrderBy(x => x.Title, StringComparer.CurrentCulture))
                Chapters.Add(chapter);
            OnPropertyChanged(nameof(FilteredChapters));
            _catalogLoaded = true;
            CatalogStatus = $"{Chapters.Count} 章 · " + (cache ? "本地缓存" : "Yatta 最新目录");
            SelectedChapter = Chapters.FirstOrDefault(c => c.Id == _config.SelectedChapterId);
        }
        catch (Exception error)
        {
            CatalogStatus = "剧情目录加载失败：" + error.Message + "；可重试，已加载的目录会保留。";
        }
    }

    /// <summary>网络、解析、语言配对在后台执行；取消旧请求，不让迟到结果覆盖当前选择。</summary>
    private async Task LoadChapterAsync(YattaChapter chapter)
    {
        _selectionCancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _selectionCancellation = cts;
        ChapterStatus = "正在加载 CHS / JP 双语台词…";
        Lines.Clear();
        _pair = null;
        try
        {
            var pair = await QuestLanguagePairLoader.LoadAsync(_loader, chapter.Id, cancellationToken: cts.Token);
            if (cts.IsCancellationRequested || SelectedChapter?.Id != chapter.Id) return;
            _pair = pair;
            RebuildLines();
            ChapterStatus = $"{chapter.Title} · {Lines.Count} 条对白，{Lines.Count(x => x.CanSynthesize)} 条可安全配对";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (ReferenceEquals(cts, _selectionCancellation)) ChapterStatus = "加载失败：" + error.Message; }
        finally { if (ReferenceEquals(cts, _selectionCancellation)) _selectionCancellation = null; cts.Dispose(); }
    }

    /// <summary>重新渲染旅行者昵称/性别模板，不修改来源 Yatta 文本。</summary>
    private void RebuildLines()
    {
        if (_pair is null || IsGenerating) return;
        var selectedKey = SelectedLine?.Key;
        var context = new QuestTextContext(_config.TravelerNickname,
            _config.FemaleTraveler ? TravelerGender.Female : TravelerGender.Male);
        Lines.Clear();
        foreach (var mapping in _pair.Entries.Where(e => e.Chs is { Type: "SingleDialog" }))
        {
            var cn = mapping.Chs!;
            string Render(QuestNode? node) => node?.Texts.FirstOrDefault()?.Template.Render(context)?.Trim() ?? "";
            Lines.Add(new(mapping, cn.Role, Render(cn), Render(mapping.Jp)));
        }
        SelectedLine = Lines.FirstOrDefault(x => x.Key == selectedKey);
    }

    /// <summary>以全局章节为唯一来源生成所有已确认的双语对白。</summary>
    [RelayCommand]
    private async Task StartPreSynthesisAsync()
    {
        if (IsGenerating || _pair is null) { PreSynthesisStatus = "请先加载剧情章节及双语对白。"; return; }
        var entries = Lines.Where(x => x.CanSynthesize).ToArray();
        if (entries.Length == 0) { PreSynthesisStatus = "当前章节没有可安全配对的对白。"; return; }
        var requests = entries.Select(x => new QuestPreSynthesisLine(x.Key, x.Speaker,
            _config.SpeechLanguage == "jpn" ? x.Japanese : x.Chinese)).ToArray();
        _batchCancellation = new CancellationTokenSource();
        _pauseRequested = false;
        IsGenerating = true;
        Completed = CacheHits = Failed = 0; Total = requests.Length;
        OnPropertyChanged(nameof(ProgressPercent));
        PreSynthesisStatus = "P1 已启动，实时配音将暂时让出 TTS 服务。";
        var progress = new Progress<QuestPreSynthesisProgress>(update =>
        {
            var row = Lines.FirstOrDefault(x => x.Key == update.Key);
            if (row is not null) row.State = update.State;
            Completed = update.Completed; Total = update.Total;
            CacheHits = update.CacheHits; Failed = update.Failed;
            OnPropertyChanged(nameof(ProgressPercent));
            PreSynthesisStatus = $"P1 · {Completed}/{Total} · 缓存 {CacheHits} · 失败 {Failed} · {update.State}";
        });
        try
        {
            var result = await _preSynthesis.GenerateAsync(requests, _config.SpeechLanguage,
                async token => { while (_pauseRequested) await Task.Delay(200, token); },
                progress, _batchCancellation.Token);
            PreSynthesisStatus = $"完成 {result.Completed}/{result.Total}，缓存命中 {result.CacheHits}，失败 {result.Failed}。";
        }
        catch (OperationCanceledException) { PreSynthesisStatus = $"已停止，保留 {Completed} 条完成的 WAV。"; }
        catch (Exception error) { PreSynthesisStatus = "预合成已中断：" + error.Message; }
        finally
        {
            IsGenerating = false;
            _batchCancellation.Dispose();
            _batchCancellation = null;
            _pauseRequested = false;
        }
    }

    /// <summary>暂停新的语音任务；当前已经发送的 HTTP 请求继续安全排空。</summary>
    [RelayCommand] private void PausePreSynthesis() { _pauseRequested = true; PreSynthesisStatus = "暂停中：当前请求完成后暂停"; }
    /// <summary>恢复剩余的 P1 请求。</summary>
    [RelayCommand] private void ResumePreSynthesis() { _pauseRequested = false; PreSynthesisStatus = "继续生成剩余台词"; }
    /// <summary>取消未来请求，不在切换权重时强制中断后端。</summary>
    [RelayCommand] private void StopPreSynthesis() { _batchCancellation?.Cancel(); PreSynthesisStatus = "停止请求已收到，当前合成完成后退出"; }
}
