using System.Text.Json;

namespace QuestVoice.Core;

public static class FusionCacheReader
{
    private static readonly JsonSerializerOptions Import = new() { PropertyNameCaseInsensitive = true };
    public static FusionCachedFrame[] ReadFrames(string framePath, string ocrPath)
    {
        var ocr = new Dictionary<(string, int), JsonElement>();
        foreach (var row in Rows(ocrPath))
        {
            var key = (row.GetProperty("clip_id").GetString()!, row.GetProperty("frame_index").GetInt32());
            if (!ocr.TryAdd(key, row)) throw new InvalidDataException("Duplicate cached OCR frame.");
        }
        var frames = new List<FusionCachedFrame>(); var expected = new Dictionary<string, int>();
        var last = new Dictionary<string, double>(); var consumedOcr = new HashSet<(string, int)>();
        foreach (var row in Rows(framePath))
        {
            var clip = row.GetProperty("clip_id").GetString()!; var index = row.GetProperty("frame_index").GetInt32();
            var time = row.GetProperty("local_pts_ms").GetDouble(); RequireDev(clip, time);
            if (index != expected.GetValueOrDefault(clip) || last.TryGetValue(clip, out var previous) && previous >= time)
                throw new InvalidDataException("Cached frame sequence is incomplete, repeated or unordered.");
            expected[clip] = index + 1; last[clip] = time;
            if (!Enum.TryParse<EyeState>(row.GetProperty("eye_state").GetString(), out var eye)) throw new InvalidDataException("Unknown cached eye state.");
            var gate = new UIGateResult(time / 1000, eye, row.GetProperty("eye_score").GetDouble(), row.GetProperty("option_rows").GetInt32(),
                Bool(row, "dialogue_visible"), Bool(row, "choice_waiting"), Bool(row, "target_known"), Bool(row, "allow_visual_cuts"),
                Bool(row, "allow_target_audio"), Bool(row, "black_transition"));
            var semantic = new List<FusionSemanticEvidence>(); var textOnly = new List<FusionSemanticEvidence>();
            if (ocr.TryGetValue((clip, index), out var read))
            {
                consumedOcr.Add((clip, index));
                var request = read.GetProperty("request_pts_ms").GetDouble(); var returned = read.GetProperty("return_pts_ms").GetDouble();
                var confidence = read.GetProperty("confidence").GetDouble(); var text = read.GetProperty("raw_text").GetString()!;
                if (!double.IsFinite(request) || !double.IsFinite(returned) || Math.Abs(request - time) > .000001 || returned < request - .000001 ||
                    returned > time + .000001 || !double.IsFinite(confidence) || confidence is < 0 or > 1)
                    throw new InvalidDataException($"Cached OCR not available: {clip}/{index}, request={request:R}, return={returned:R}, frame={time:R}, confidence={confidence:R}");
                // seconds->milliseconds round trips can differ by 1e-13 ms. Resolve only
                // equivalent original-frame times; meaningful future returns still fail.
                request = time;
                if (Math.Abs(returned - time) <= .000001) returned = time;
                if (Bool(read, "prefix_requested")) semantic.Add(new("D", request, returned, text[..Math.Min(12, text.Length)], null, confidence));
                if (Bool(read, "full_requested")) textOnly.Add(new("D", request, returned, text, null, confidence));
            }
            if (row.TryGetProperty("e", out var template) && template.ValueKind == JsonValueKind.Object)
            {
                var match = template.Deserialize<TextMatch>(Import)!;
                if (match.Key is not null) semantic.Add(new("E", time, time, null, match.Key, 1, match.Score, match.Margin));
            }
            var observation = new FusionObservation(clip, index, time, gate, [], semantic.AsReadOnly(), clip == "S1" ? "chs" : "jpn");
            frames.Add(new(observation, row.GetProperty("glyph_count").GetInt32(), textOnly.AsReadOnly(),
                Number(row, "gate_ms"), Number(row, "glyph_ms"), Number(row, "ocr_ms"), Number(row, "template_ms")));
        }
        if (consumedOcr.Count != ocr.Count) throw new InvalidDataException("OCR cache contains frames absent from the Dev frame stream.");
        return frames.ToArray();
    }
    public static FusionCachedCandidate[] ReadCandidates(string reportPath)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var result = new List<FusionCachedCandidate>(); var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in report.RootElement.GetProperty("methods").EnumerateArray())
        {
            var name = method.GetProperty("method").GetString()!;
            var predictions = method.GetProperty("predictions").EnumerateArray().ToArray();
            if (name is not ("A" or "B" or "C") || !methods.Add(name) || predictions.Length != method.GetProperty("prediction_count").GetInt32())
                throw new InvalidDataException("Invalid cached visual method or candidate count.");
            for (var index = 0; index < predictions.Length; index++)
            {
                var p = predictions[index]; var clip = p.GetProperty("clip_id").GetString()!;
                var available = p.GetProperty("decision_ms").GetDouble(); var source = p.GetProperty("onset_ms").GetDouble();
                RequireDev(clip, available);
                if (!double.IsFinite(source) || source < 0 || source > available) throw new InvalidDataException("Invalid cached visual onset.");
                result.Add(new(clip, new(name, FusionVisualKind.PossibleClear, $"cached/{name}/{clip}/{index}", source, available, 1)));
            }
        }
        if (methods.Count != 3) throw new InvalidDataException("Dev cache must retain all A/B/C methods.");
        return result.ToArray();
    }
    public static QuestGraph ReadQuestGraph(string snapshotPath, string edgePath)
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        var edges = JsonSerializer.Deserialize<QuestEdge[]>(File.ReadAllText(edgePath), Import)!;
        var documents = new List<QuestDocument>();
        foreach (var d in snapshot.RootElement.GetProperty("documents").EnumerateArray())
        {
            var nodes = d.GetProperty("nodes").Deserialize<QuestNode[]>(Import)!; var keys = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            documents.Add(new(d.GetProperty("sourcePath").GetString()!, d.GetProperty("language").GetString()!, d.GetProperty("chapterId").GetString()!,
                d.GetProperty("raw").Clone(), d.GetProperty("info").Clone(), d.GetProperty("tasks").Deserialize<QuestTask[]>(Import)!,
                nodes, edges.Where(e => keys.Contains(e.FromKey)).ToArray(), []));
        }
        return new(documents);
    }
    public static SimulatedAudioClip[] ReadAudioClips(QuestGraph graph, string audioPath)
    {
        var entries = JsonSerializer.Deserialize<AudioEntry[]>(File.ReadAllText(audioPath), Import)!;
        var audio = entries.Where(e => e.Id is not null).GroupBy(e => (e.Id!, e.OptionNumber)).ToDictionary(g => g.Key, g => g.ToArray());
        return graph.Nodes.Where(n => n.Type == "SingleDialog" && graph.CanActivate(n.Key)).SelectMany(n => graph.LineIndex.ForNode(n.Key))
            .Select(line => (Line: line, Audio: audio.GetValueOrDefault((line.Id, line.OptionNumber)) ?? []))
            .Where(p => p.Audio.Length == 1 && p.Audio[0].Wave.IsValid && p.Audio[0].Wave.DurationSeconds is { } duration && double.IsFinite(duration) && duration > 0)
            .Select(p => new SimulatedAudioClip(p.Line.NodeKey, p.Line.Id, p.Audio[0].Path, p.Audio[0].Wave.DurationSeconds!.Value * 1000)).ToArray();
    }
    private static IEnumerable<JsonElement> Rows(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }
    private static bool Bool(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
    private static double Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) ? value.GetDouble() : 0;
    private static void RequireDev(string clip, double time)
    {
        var end = clip switch { "S1" => 7724.667, "L2" => 45000d, _ => throw new InvalidDataException("Only S1 and L2 first half are Dev.") };
        if (!double.IsFinite(time) || time < 0 || time >= end) throw new InvalidDataException("Cached evidence is outside Dev.");
    }
}
