using QuestVoice.Core;
using QuestVoice.Games.Generic;
using QuestVoice.Games.Genshin;

namespace QuestVoice.Games;

public sealed record PlannedGameEntry(string GameId, string DisplayName, string StatusNote);

/// <summary>
/// Central registry for Game Adapters. Only verified/runnable adapters are exposed as active choices;
/// unverified games (Star Rail, Wuthering Waves) are listed strictly as future roadmap entries (Docs/01 & Docs/02).
/// </summary>
public sealed class GameAdapterRegistry
{
    private readonly Dictionary<string, IGameAdapter> adapters = new(StringComparer.OrdinalIgnoreCase);

    public GameAdapterRegistry()
    {
        var genshin = new GenshinGameAdapter();
        var generic = new GenericGameAdapter();
        adapters[genshin.GameId] = genshin;
        adapters[generic.GameId] = generic;
    }

    public GenshinGameAdapter Genshin => (GenshinGameAdapter)adapters["genshin"];
    public GenericGameAdapter Generic => (GenericGameAdapter)adapters["generic"];

    public IReadOnlyList<IGameAdapter> ActiveAdapters =>
        adapters.Values.OrderByDescending(a => a.GameId == "genshin").ThenBy(a => a.DisplayName).ToArray();

    public IReadOnlyList<PlannedGameEntry> PlannedUnverifiedGames { get; } =
    [
        new("starrail", "崩坏：星穹铁道 (Honkai: Star Rail)", "待样本验证（仅保留 References 场景分类参考，当前未启用）"),
        new("wutheringwaves", "鸣潮 (Wuthering Waves)", "后续规划（尚未建立已验证视觉门控样本，可先使用通用 ROI 模式）")
    ];

    public IGameAdapter GetAdapter(string gameId) =>
        adapters.TryGetValue(gameId, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"未知或尚未启用的游戏适配器：{gameId}");
}
