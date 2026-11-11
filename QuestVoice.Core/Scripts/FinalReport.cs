using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace QuestVoice.Core;

public sealed record ReportEvidence(string Path, string Sha256, long Bytes);
public sealed record PhaseReport(int Phase, string Name, JsonElement Measurements, IReadOnlyList<string> Evidence);
public sealed record MethodReport(string Name, string Task, int Frames, int EventCount, int TargetEventCount,
    double? Accuracy, double? Precision, double? Recall, double? F1, double? CutLatencyP95Ms,
    double? ComputeP50Ms, double? ComputeP95Ms, double? CpuMs, long? MemoryBytes,
    IReadOnlyList<string> EventSemantics, string ResourceScope);
public sealed record SamplingReport(string Name, int Frames, bool IndependentExecution,
    double EffectiveFps, int L1TargetStarts, int AllTargetStarts, int OcrCalls);
public sealed record SharedResourceReport(double WallMs, double CpuMs, long PeakWorkingSetBytes,
    int LogicalProcessors, string Scope);
public sealed record ReportConflict(string Field, string Source, double JsonValue, double MarkdownValue);
public sealed record FinalProjectReport(int SchemaVersion, string Status, DateTimeOffset GeneratedUtc,
    IReadOnlyList<ReportEvidence> Provenance, IReadOnlyList<PhaseReport> Phases,
    IReadOnlyList<MethodReport> Methods, IReadOnlyList<SamplingReport> Sampling,
    SharedResourceReport SharedResources, ParetoResult Pareto, IReadOnlyList<ReportConflict> Conflicts,
    IReadOnlyList<string> Limitations);

public static class FinalReportBuilder
{
    public static FinalProjectReport Build(string root)
    {
        root = NormalizeRoot(root);
        var policy = new PathPolicy(root);
        var evidence = new List<ReportEvidence>();
        string Read(string relative)
        {
            var path = policy.RequireOutput(Path.Combine(root, relative));
            var bytes = File.ReadAllBytes(path);
            evidence.Add(new(relative, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength));
            return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        JsonElement Json(string relative)
        {
            using var doc = JsonDocument.Parse(Read(relative));
            return doc.RootElement.Clone();
        }
        const string auditPath = "results/audit-20261030T162805005-26d9266420c04b798532794338bc5036/manifest.json";
        var audit = Json(auditPath);
        var abc = Json("results/method_eval.json");
        var visualEvidence = File.Exists(Path.Combine(root, "results", "visual-recognition-evaluation.json"))
            ? "results/visual-recognition-evaluation.json" : "results/task5_eval.json";
        var def = Json(visualEvidence);
        var quest = Json("results/task6_eval.json");
        var fusion = Json("results/fusion_eval.json");
        var validation = Json("results/validation_eval.json");
        var coverage = Json("annotations/review-coverage.json");
        _ = Read("annotations/ground_truth.jsonl");
        var validationMarkdown = Read("results/validation_eval.md");
        var native = validation.GetProperty("runs").EnumerateArray().Single(r => Text(r, "sampling") == "native");
        var methods = native.GetProperty("methods").EnumerateArray().Select(Method).ToArray();
        var sampling = validation.GetProperty("runs").EnumerateArray().Select(r => new SamplingReport(
            Text(r, "sampling"), Int(r, "frame_count"), r.GetProperty("independent_execution").GetBoolean(),
            r.GetProperty("effective_fps").GetDouble(), Int(r, "l1_target_wav_starts"), Int(r, "all_target_wav_starts"),
            Int(r, "ocr_calls"))).ToArray();
        var processing = validation.GetProperty("processing");
        var shared = new SharedResourceReport(processing.GetProperty("wall_ms").GetDouble(),
            processing.GetProperty("cpu_ms").GetDouble(), processing.GetProperty("observed_peak_working_set_bytes").GetInt64(),
            Int(processing, "logical_processors"), Text(processing, "scope"));
        var phases = new PhaseReport[]
        {
            new(1, "素材与音频审计", audit.GetProperty("summary").Clone(), [auditPath]),
            new(2, "真值、ROI 与冻结划分", JsonSerializer.SerializeToElement(new
            {
                development_frame_count = Int(def.GetProperty("dataset"), "frame_count"),
                validation_frame_count = Int(validation.GetProperty("dataset"), "original_frame_count"),
                ground_truth_rows = Int(validation.GetProperty("dataset"), "ground_truth_rows"),
                eligible_truth_rows = Int(validation.GetProperty("dataset"), "eligible_truth_rows"),
                formal_coverage_ms = validation.GetProperty("annotation_audit").GetProperty("formal_coverage_ms").GetDouble(),
                truth_sha256 = Text(validation.GetProperty("dataset"), "truth_sha256"), review_coverage = coverage
            }), ["annotations/ground_truth.jsonl", "annotations/review-coverage.json"]),
            new(3, "A/B/C 开发集基线", JsonSerializer.SerializeToElement(new
            {
                dataset = abc.GetProperty("dataset"), replay_wall_ms = abc.GetProperty("replay_wall_ms"),
                methods = Select(abc.GetProperty("methods"), "method", "processed_frames", "prediction_count", "primary", "exploratory", "processing", "stable_subtitle_false_positives")
            }), ["results/method_eval.json"]),
            new(4, "D/E/F 语义、模板与 UI", JsonSerializer.SerializeToElement(new
            {
                dataset = def.GetProperty("dataset"), processing = def.GetProperty("processing"),
                methods = Select(def.GetProperty("methods"), "method", "kind", "processed_frames", "inference_calls", "prediction_count", "accuracy", "accuracy_reason", "primary", "exploratory_first_visible", "exploratory_identity"),
                comparison = def.GetProperty("comparison"), gate_enhancement = def.GetProperty("gate_enhancement")
            }), [visualEvidence]),
            new(5, "剧情图与只读音频索引", quest.GetProperty("graph").Clone(), ["results/task6_eval.json"]),
            new(6, "融合与模拟接替", JsonSerializer.SerializeToElement(new
            {
                dataset = fusion.GetProperty("dataset"),
                methods = Select(fusion.GetProperty("methods"), "name", "mode", "frame_count", "cut_count", "audio_start_count", "false_detection_rate", "cut_latency_p95_ms", "missed_playback_rate", "formal", "exploratory", "processing")
            }), ["results/fusion_eval.json"]),
            new(7, "冻结验证", JsonSerializer.SerializeToElement(new
            {
                original_frame_count = Int(validation.GetProperty("dataset"), "original_frame_count"),
                dataset = validation.GetProperty("dataset"), annotation_audit = validation.GetProperty("annotation_audit"),
                parameters_frozen = validation.GetProperty("parameters_frozen"), validation_tuning = validation.GetProperty("validation_tuning"),
                sampling, methods, shared_resources = shared, generalization_audit = validation.GetProperty("generalization_audit"),
                test_evidence = validation.GetProperty("test_evidence")
            }), ["results/validation_eval.json", "results/validation_eval.md"])
        };
        var conflicts = new List<ReportConflict>();
        var cpuMatch = Regex.Match(validationMarkdown, @"CPU\s+([0-9]+(?:\.[0-9]+)?)s");
        if (cpuMatch.Success && double.TryParse(cpuMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mdCpu)
            && Math.Abs(mdCpu - shared.CpuMs / 1000) > .001)
            conflicts.Add(new("validation_cpu_seconds", "results/validation_eval.md", shared.CpuMs / 1000, mdCpu));
        var points = methods.Select(m => new ParetoPoint(m.Name, m.Task, m.Accuracy ?? m.F1,
            m.CutLatencyP95Ms, m.CpuMs, m.MemoryBytes)).ToArray();
        return new(1, "completed_with_measurement_limitations", DateTimeOffset.UtcNow, evidence, phases, methods, sampling,
            shared, ParetoAnalysis.Analyze(points), conflicts,
            ["20 行冻结真值全部 pending/excluded，正式覆盖 0；正式准确率、召回率、切句 P95 不可评价。",
             "L1 目标 WAV 启动 0 次是观察数量；正式误播率缺少合法计分分母，不能写成已验证 0%。",
             "CPU/内存仅有共享进程测量；单方法资源 null，不均分、不以每帧计算时间冒充 CPU 或切句延迟。",
             "D/E 与 Full Fusion 未产生可靠剧情确认；安全静音同时可能漏播，当前不具备生产启用证据。",
             "60 source reference 复用 native；60 grid 实际约 44.87 FPS；不同任务语义与共享计算不能直接混合排名。",
             "S1 与 L2 验证段存在相同台词/场景；时间冻结不能证明内容独立，未宣称泛化。",
             "既有评测是离线因果回放；真实声卡、队列与首字端到端延迟未在 Phase 1–7 测量。"]);
    }
    public static IReadOnlyList<string> Write(string root, FinalProjectReport report)
    {
        root = NormalizeRoot(root);
        ArgumentNullException.ThrowIfNull(report);
        var policy = new PathPolicy(root);
        var paths = new[] { "results/FINAL_REPORT.json", "results/FINAL_REPORT.md", "SDD/20-TASK9-FINAL-REPORT.md" }
            .Select(p => policy.RequireOutput(Path.Combine(root, p))).ToArray();
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var content = new[] { JsonSerializer.Serialize(report, options) + "\n",
            FinalReportMarkdown.Summary(report), FinalReportMarkdown.Guide(report) };
        for (var i = 0; i < paths.Length; i++)
            File.WriteAllText(paths[i], content[i], new System.Text.UTF8Encoding(false));
        return paths;
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return full;
    }

    private static MethodReport Method(JsonElement row)
    {
        var name = Text(row, "method");
        var primary = row.GetProperty("primary");
        var entries = primary.EnumerateObject().ToArray();
        var metric = entries.Length == 1 ? entries[0].Value : default;
        var costs = row.GetProperty("processing");
        var task = name switch { "A" or "B" => "visual-clear", "C" => "visual-structure", "D" or "E" => "node-identity", "F" => "ui-state", _ => "confirmed-cut-and-playback" };
        var accuracy = row.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object
            ? Number(identity, "top1_accuracy") : null;
        return new(name, task, Int(row, "processed_frames"), Int(row, "prediction_count"),
            entries.Sum(p => Int(p.Value, "prediction_count") + Int(p.Value, "predictions_outside_coverage")),
            accuracy, Number(metric, "precision"), Number(metric, "recall"), Number(metric, "f1"), Number(metric, "latency_p95_ms"),
            Number(costs, "per_frame_p50_ms"), Number(costs, "per_frame_p95_ms"), Number(row, "cpu_ms"),
            row.GetProperty("peak_memory_bytes").ValueKind == JsonValueKind.Number ? row.GetProperty("peak_memory_bytes").GetInt64() : null,
            row.GetProperty("event_semantics").EnumerateArray().Select(s => s.GetString()!).ToArray(), Text(row, "resource_scope"));
    }

    private static object[] Select(JsonElement array, params string[] fields) => array.EnumerateArray()
        .Select(row => (object)fields.Where(f => row.TryGetProperty(f, out _)).ToDictionary(f => f, f => row.GetProperty(f).Clone())).ToArray();
    private static string Text(JsonElement row, string field) => row.GetProperty(field).GetString()!;
    private static int Int(JsonElement row, string field) => row.GetProperty(field).GetInt32();
    private static double? Number(JsonElement row, string field) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
