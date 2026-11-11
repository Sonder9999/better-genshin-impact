using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestVoice.Core;

public sealed record FusionReportFiles(string JsonPath, string MarkdownPath);
public static class FusionReportWriter
{
    public static FusionReportFiles Write(string root, string outputDirectory, IReadOnlyList<FusionReplayRun> runs,
        IReadOnlyList<FusionTruth> formalTruth, IReadOnlyList<EvaluationWindow> formalCoverage,
        IReadOnlyList<FusionTruth> exploratoryTruth, IReadOnlyList<EvaluationWindow> exploratoryCoverage,
        object metadata)
    {
        if (runs.Count != 4 || runs.Select(r => r.Mode).Distinct().Count() != 4 || runs.Any(r => !Enum.IsDefined(r.Mode)) ||
            runs.Any(r => !r.FrameIds.SequenceEqual(runs[0].FrameIds)))
            throw new InvalidDataException("Report requires four independent modes consuming identical original frames.");
        var policy = new PathPolicy(root); var directory = policy.RequireOutput(outputDirectory);
        var pretty = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        pretty.Converters.Add(new JsonStringEnumConverter());
        var compact = new JsonSerializerOptions(pretty) { WriteIndented = false };
        var summaries = runs.OrderBy(r => r.Mode).Select(r => (Run: r,
            Formal: FusionEvaluation.Evaluate(r.Decisions, r.Playback, formalTruth, formalCoverage),
            Exploratory: FusionEvaluation.Evaluate(r.Decisions, r.Playback, exploratoryTruth, exploratoryCoverage))).ToArray();
        var methods = new List<object>();
        foreach (var summary in summaries)
        {
            var run = summary.Run; var prefix = Path.Combine(directory, "fusion", Slug(run.Mode));
            var decisions = policy.RequireOutput(prefix + "-decisions.jsonl");
            var playback = policy.RequireOutput(prefix + "-playback.jsonl");
            var frames = policy.RequireOutput(prefix + "-frames.jsonl");
            WriteRows(decisions, run.Decisions, compact); WriteRows(playback, run.Playback, compact); WriteRows(frames, run.Frames, compact);
            methods.Add(new
            {
                name = Name(run.Mode), mode = run.Mode, frame_count = run.FrameCount,
                consumed_frames_sha256 = HashFrames(run.FrameIds), cut_count = run.Decisions.Count(e => e.Kind == FusionEventKind.CutTriggered),
                audio_start_count = run.Playback.Count(e => e.Kind == SimulatedPlaybackKind.Started),
                false_detection_rate = summary.Formal.FalseDetectionRate, cut_latency_p95_ms = summary.Formal.Cut.LatencyP95Ms,
                missed_playback_rate = summary.Formal.MissedPlaybackRate, false_cut_interruption_rate = summary.Formal.FalseCutInterruptionRate,
                playback_alignment_p95_ms = summary.Formal.PlaybackAlignmentP95Ms,
                formal = summary.Formal, exploratory = summary.Exploratory, parameters = run.Parameters ?? new(run.Mode),
                state_counts = run.Frames.GroupBy(f => f.State).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                processing = new { calls = run.FrameCount, fusion_wall_p50_ms = BaselineEvaluation.Percentile(run.Frames.Select(f => f.FusionMs), .5),
                    fusion_wall_p95_ms = BaselineEvaluation.Percentile(run.Frames.Select(f => f.FusionMs), .95), fusion_wall_total_ms = run.Frames.Sum(f => f.FusionMs),
                    clock = "ideal offline causal media clock; upstream feature inference is cached; no sound-card or real-time end-to-end measurement" },
                clips = run.Clips,
                traces = new { decisions = Relative(root, decisions), playback = Relative(root, playback), frames = Relative(root, frames) }
            });
        }
        var report = new
        {
            schema_version = 1, phase = 6, task = 7, generated_utc = DateTimeOffset.UtcNow,
            status = formalCoverage.Count == 0 ? "completed_with_annotation_limitations" : "completed",
            dataset = new { split = "dev", frame_count = runs[0].FrameCount, clips = runs[0].Clips.Select(c => new { c.ClipId, c.FrameCount, c.FirstPtsMs, c.LastPtsMs, c.DevEndMs }),
                eligible_truth_rows = formalTruth.Count, formal_coverage = formalCoverage, exploratory_truth_rows = exploratoryTruth.Count,
                exploratory_coverage = exploratoryCoverage, identical_ablation_frames = true,
                original_frames_sha256 = HashFrames(runs[0].FrameIds) },
            metric_definitions = new
            {
                cut = "nearest truth owns each cut, one-to-one, same clip, half-open coverage, +/-100ms decision PTS; clear is not first-character credit",
                false_detection_rate = "FP / covered cut predictions; null when no covered predictions",
                cut_latency_p95_ms = "nearest-rank P95 of matched decision PTS minus truth first-visible PTS; early triggers stay signed",
                missed_playback_rate = "unmatched truth / covered eligible first-visible truth; only actual simulated starts linked to matched cut and correct available line/node identity count",
                false_cut_interruption_rate = "early replacement stops caused by covered unmatched cuts / covered actual simulated starts",
                playback_alignment = "actual scheduled simulation start PTS minus matching first-visible truth PTS; separate from detection and CPU time",
                unavailable = "no eligible reviewed coverage means null, never zero-error claims; single-review S1 is exploratory only",
                no_quest_graph = "pure visual anonymous cuts; no target-WAV identity or artificial audio bindings",
                no_vision = "periodic raw full-OCR control plus graph; excludes A/B/C first/clear decisions and E templates",
                event_identity = "event_id and decision_id are scoped by clip_id and session_id"
            },
            provenance = metadata, methods
        };
        var jsonPath = policy.RequireOutput(Path.Combine(directory, "fusion_eval.json"));
        var markdownPath = policy.RequireOutput(Path.Combine(directory, "fusion_eval.md"));
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(jsonPath + ".tmp", JsonSerializer.Serialize(report, pretty) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(jsonPath + ".tmp", jsonPath, true);
        var md = new StringBuilder("# Phase 6 / Task 7 — Fusion Dev evaluation\n\n");
        md.AppendLine($"四组方案分别消费全部 **{runs[0].FrameCount}** 个原始 Dev 帧，输入帧序列 SHA256 完全一致。S1 全段及 L2 前半段按原始 PTS 回放。\n");
        if (formalCoverage.Count == 0)
            md.AppendLine("正式误检率、切句延迟 P95 和漏播率为 **N/A**：冻结标注没有有效的正式计分覆盖。零触发不能证明低误检；S1 单次审阅候选仅用于下方探索性统计。\n");
        md.AppendLine("| 方案 | 帧数 | 切句 | 模拟开始 | 正式误检率 | 正式切句 P95 ms | 正式漏播率 |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var summary in summaries)
            md.AppendLine($"| {Name(summary.Run.Mode)} | {summary.Run.FrameCount} | {summary.Run.Decisions.Count(e => e.Kind == FusionEventKind.CutTriggered)} | {summary.Formal.PlaybackStarts} | {Rate(summary.Formal.FalseDetectionRate)} | {Number(summary.Formal.Cut.LatencyP95Ms)} | {Rate(summary.Formal.MissedPlaybackRate)} |");
        md.AppendLine("\n探索性 S1 统计，包含尚待复审的首字候选，不能用来宣称全 Dev 或验证集精度。\n");
        md.AppendLine("| 方案 | TP / FP / FN | 探索误检率 | 切句 P95 ms | 漏播率 | 误切打断率 |\n| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var summary in summaries)
            md.AppendLine($"| {Name(summary.Run.Mode)} | {summary.Exploratory.Cut.TruePositives} / {summary.Exploratory.Cut.FalsePositives} / {summary.Exploratory.Cut.FalseNegatives} | {Rate(summary.Exploratory.FalseDetectionRate)} | {Number(summary.Exploratory.Cut.LatencyP95Ms)} | {Rate(summary.Exploratory.MissedPlaybackRate)} | {Rate(summary.Exploratory.FalseCutInterruptionRate)} |");
        md.AppendLine("\nFull Fusion 等待可靠文本或已确认剧情身份；缓存 D/E 未建立可用定位时保持 UNKNOWN/静音。No-UIGate 仅撤除 F，No-Vision 使用周期全文 OCR 和图，No-QuestGraph 只输出匿名视觉切句，不能绑定目标 WAV。\n");
        md.AppendLine("A/B/C 缓存的下降/结构候选保留为清空准备信号；额外首字证据来自当前帧 glyph-count 从 0 变为非 0，正常增长不重复切句。ROI、OCR 字体及分割限制继承前序缓存；没有使用人工台词或未来帧初始化剧情。\n");
        md.AppendLine("模拟 WAV 启动延迟为 0ms 的理想时钟；调度支持非零延迟并有专门测试。新句可停止未完成音频，过期请求取消且不补播。墙钟耗时仅测新融合代码；先前视觉/OCR 成本另保留在 JSON provenance。未测真实声卡、机器端到端延迟、排队或丢帧。\n");
        md.AppendLine("参数及每帧状态、候选、拒绝原因、准备/播放/停止/取消/完成时间戳见 JSON 和同目录 fusion/*.jsonl。输入和冻结真值哈希必须在运行前后保持一致。\n");
        File.WriteAllText(markdownPath, md.ToString(), new UTF8Encoding(false));
        return new(jsonPath, markdownPath);
    }
    public static string Name(FusionMode mode) => mode switch
    { FusionMode.FullFusion => "Full Fusion", FusionMode.NoUIGate => "No-UIGate", FusionMode.NoVision => "No-Vision", FusionMode.NoQuestGraph => "No-QuestGraph", _ => throw new ArgumentException("Unknown mode") };
    private static string Slug(FusionMode mode) => mode switch
    { FusionMode.FullFusion => "full-fusion", FusionMode.NoUIGate => "no-ui-gate", FusionMode.NoVision => "no-vision", FusionMode.NoQuestGraph => "no-quest-graph", _ => throw new ArgumentException("Unknown mode") };
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string HashFrames(IEnumerable<string> frames) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', frames)))).ToLowerInvariant();
    private static string Rate(double? value) => value?.ToString("P2", CultureInfo.InvariantCulture) ?? "N/A";
    private static string Number(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A";
    private static void WriteRows<T>(string path, IEnumerable<T> rows, JsonSerializerOptions options)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, options));
    }
}
