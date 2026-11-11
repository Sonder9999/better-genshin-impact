using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuestVoice.Core;

public sealed record AudioExpectedLine(string EventId, string NodeId, long Generation, double ConfirmedAtMs,
    double? InvalidatedAtMs = null, bool AllowStart = true);
public sealed record AudioMetricSummary(int Starts, int WrongNodeStarts, int StaleStarts, int DuplicateStarts,
    int MeasuredStarts, int UnmeasuredStarts, double? StartLatencyP50Ms, double? StartLatencyP95Ms,
    double? CancelDispatchP50Ms, double? CancelDispatchP95Ms, double? ReadyP50Ms, double? ReadyP95Ms,
    double DeviceTailMs, double DeviceOverlapMs, double? AcousticLatencyP95Ms, string Scope);
public sealed record AudioSourceRecord(string NodeId, string Path, string Sha256, long Bytes, double DurationMs, bool Unchanged);
public sealed record AudioLoadRecord(string Path, double DecodeMs, double ArtificialHoldMs, long DecodedBytes);
public sealed record AudioScenarioReport(string Name, bool Passed, string Observation, double ElapsedMs);
public sealed record AudioPolicyReport(double FadeOutMs, AudioMetricSummary Metrics, WaveOutSnapshot Device,
    IReadOnlyList<AudioScenarioReport> Scenarios, IReadOnlyList<AudioLogEntry> Trace,
    IReadOnlyList<AudioExpectedLine> Expectations, IReadOnlyList<double> CancelDispatchMs, IReadOnlyList<AudioLoadRecord> Loads);
public sealed record AudioExperimentReport(int SchemaVersion, string Status, string TimeMode, string SourcePtsScope,
    IReadOnlyList<AudioSourceRecord> Sources, IReadOnlyList<AudioPolicyReport> Policies,
    double WallMs, double CpuMs, long PeakWorkingSetBytes, IReadOnlyList<string> Limitations);

public static class AudioEvaluation
{
    public static AudioMetricSummary Compute(IReadOnlyList<AudioLogEntry> logs, IReadOnlyList<AudioExpectedLine> expected,
        IReadOnlyList<double> cancelDispatchMs, WaveOutSnapshot device)
    {
        ArgumentNullException.ThrowIfNull(logs); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(cancelDispatchMs);
        if (device.Format.SampleRate <= 0 || expected.Any(e => e is null || string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.NodeId)
            || e.Generation <= 0 || !double.IsFinite(e.ConfirmedAtMs) || e.ConfirmedAtMs < 0
            || e.InvalidatedAtMs.HasValue && (!double.IsFinite(e.InvalidatedAtMs.Value) || e.InvalidatedAtMs < e.ConfirmedAtMs))
            || expected.GroupBy(e => e.EventId, StringComparer.Ordinal).Any(g => g.Count() != 1)
            || cancelDispatchMs.Any(ms => !double.IsFinite(ms) || ms < 0))
            throw new ArgumentException("Invalid audio evaluation samples.");
        var byEvent = expected.ToDictionary(e => e.EventId, StringComparer.Ordinal);
        var starts = logs.Where(e => e.Command == "start").ToArray();
        var latency = new List<double>(); var wrong = 0; var stale = 0;
        foreach (var start in starts)
        {
            var known = byEvent.TryGetValue(start.EventId, out var target);
            var wrongNode = !known || start.NodeId != target!.NodeId;
            var old = !known || !target!.AllowStart || start.Generation != target.Generation
                || start.WallClockMs < target.ConfirmedAtMs || target.InvalidatedAtMs.HasValue && start.WallClockMs >= target.InvalidatedAtMs;
            if (wrongNode) wrong++;
            if (old) stale++;
            if (!wrongNode && !old && start.OutputClockMs.HasValue && start.ObservedLatencyMs is double ms && double.IsFinite(ms) && ms >= 0) latency.Add(ms);
        }
        var duplicate = starts.GroupBy(s => s.Generation).Sum(g => Math.Max(0, g.Count() - 1));
        var ready = logs.Where(e => e.Command == "ready" && e.LoadMs.HasValue && double.IsFinite(e.LoadMs.Value) && e.LoadMs >= 0).Select(e => e.LoadMs!.Value).ToArray();
        return new(starts.Length, wrong, stale, duplicate, latency.Count, starts.Length - latency.Count,
            BaselineEvaluation.Percentile(latency, .5), BaselineEvaluation.Percentile(latency, .95),
            BaselineEvaluation.Percentile(cancelDispatchMs, .5), BaselineEvaluation.Percentile(cancelDispatchMs, .95),
            BaselineEvaluation.Percentile(ready, .5), BaselineEvaluation.Percentile(ready, .95),
            device.PlayedTailFrames * 1000d / device.Format.SampleRate, device.PlayedOverlapFrames * 1000d / device.Format.SampleRate,
            null, "Controlled confirmed-line expectations; device sample clock, not frozen-video accuracy or acoustic speaker latency.");
    }
    public static IReadOnlyList<string> Write(string root, string outputDirectory, AudioExperimentReport report)
    {
        root = Path.GetFullPath(root);
        var policy = new PathPolicy(root); var directory = policy.RequireOutput(Path.Combine(root, outputDirectory));
        var paths = new[] { "audio_eval.json", "audio_eval.md" }.Select(name => policy.RequireOutput(Path.Combine(directory, name))).ToArray();
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        File.WriteAllText(paths[0], JsonSerializer.Serialize(report, options) + "\n", new UTF8Encoding(false));
        File.WriteAllText(paths[1], Markdown(report), new UTF8Encoding(false));
        return paths;
    }
    private static string Markdown(AudioExperimentReport report)
    {
        var b = new StringBuilder("# Phase 9 / Task 10 真实 WAV 截断接替报告\n\n");
        b.AppendLine($"状态：`{report.Status}`。时间模式：{report.TimeMode}。source PTS 范围：{report.SourcePtsScope}。\n");
        b.AppendLine("设备样本位置观察与 PCM 生成、Play 调用分别记录；没有麦克风/回环录音，因此声学扬声器延迟与听感均未测量。控制事件用于音频同步验收，不构成冻结视频准确率标签。\n");
        b.AppendLine("| 策略 | 场景通过 | 设备开始 | 错节点 / 迟到 / 重复 | 启动 P50 / P95 ms | 取消派发 P50 / P95 ms | 实际消费尾段 / 交叠 ms |\n| --- | ---: | ---: | --- | --- | --- | --- |");
        foreach (var p in report.Policies)
        {
            var m = p.Metrics;
            b.AppendLine($"| {N(p.FadeOutMs)}ms 淡出 | {p.Scenarios.Count(s => s.Passed)}/{p.Scenarios.Count} | {m.Starts} | {m.WrongNodeStarts}/{m.StaleStarts}/{m.DuplicateStarts} | {N(m.StartLatencyP50Ms)}/{N(m.StartLatencyP95Ms)} | {N(m.CancelDispatchP50Ms)}/{N(m.CancelDispatchP95Ms)} | {N(m.DeviceTailMs)}/{N(m.DeviceOverlapMs)} |");
        }
        b.AppendLine("\n启动：可靠确认到设备首样本的单调墙钟观察，包含读取/排队与观察轮询。取消派发：确认/急停接口完成同步撤销与 waveOutReset 的调用耗时，不是麦克风测得的尾音停止时刻。尾段/交叠：仅统计已提交且设备样本位置实际消费的帧，按策略整轮累计，单次转换仍受0–30ms限制；不是两段可听语音的声学交叠。\n");
        foreach (var p in report.Policies)
        {
            b.AppendLine($"## {N(p.FadeOutMs)}ms 策略明细\n");
            b.AppendLine($"设备 `{p.Device.DeviceId}:{p.Device.DeviceName}`；块 {p.Device.BufferFrames} 帧，{p.Device.Buffers} 缓冲；峰值设备队列 {p.Device.MaxQueuedFrames * 1000d / p.Device.Format.SampleRate:F3}ms；reset {p.Device.Resets} 次；丢弃旧渲染块 {p.Device.DiscardedBlocks}。ready P50/P95 {N(p.Metrics.ReadyP50Ms)}/{N(p.Metrics.ReadyP95Ms)}ms 包含调度/缓存，真实文件解码与人为迟到注入在 JSON `loads` 中分别记录。\n");
            b.AppendLine("| 场景 | 结果 | 实测观察 | 耗时 ms |\n| --- | --- | --- | ---: |");
            foreach (var s in p.Scenarios) b.AppendLine($"| {s.Name} | {(s.Passed ? "通过" : "失败")} | {s.Observation.Replace('|', '/')} | {N(s.ElapsedMs)} |");
            b.AppendLine();
        }
        b.AppendLine("## 输入与资源\n");
        foreach (var source in report.Sources)
            b.AppendLine($"- `{source.NodeId}`：{source.DurationMs:F3}ms，{source.Bytes} bytes；只读 SHA256 `{source.Sha256}`；前后不变={source.Unchanged}；路径 `{source.Path}`。");
        b.AppendLine($"\n.NET 墙钟 {N(report.WallMs)}ms、CPU {N(report.CpuMs)}ms、观测峰值工作集 {report.PeakWorkingSetBytes / 1048576d:F3}MiB；范围为本 CLI，包含两种策略、真实速度等待、加载/解码与记录，不是单个方法独立资源。\n");
        b.AppendLine("完整期望时间窗、generation、媒体 PTS、波形路径、设备开始/结束、取消原因与加载记录见 [audio_eval.json](audio_eval.json)。缺源、未知/NPC/退出、取消和老回调均以静音为安全结果；自然结束不自动重播。\n");
        foreach (var limitation in report.Limitations) b.AppendLine("- " + limitation);
        b.AppendLine("\n复现（仅在实验根，先加载 `.runtime/task4-env.ps1` 并重定向 NuGet 插件缓存/关闭诊断）：`dotnet run --project tools/TestPreVoice.AudioEval -c Release -p:UseSharedCompilation=false -p:BuildInParallel=false -- --device --device-id 0`。默认无 `--device` 时不会开启声卡。专项测试 `tests/TestPreVoice.AudioTests -- --native`，全项目串行回归见 `tools/Run-Task10.ps1`。\n");
        return b.ToString();
    }
    private static string N(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A";
}
