using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record ParetoPoint(string Id, string Task, double? Accuracy, double? CutP95Ms,
    double? CpuMs, long? MemoryBytes);
public sealed record ParetoResult(IReadOnlyList<string> Eligible, IReadOnlyList<string> Frontier,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Excluded,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DominatedBy);

public static class ParetoAnalysis
{
    public static ParetoResult Analyze(IReadOnlyList<ParetoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var eligible = new List<ParetoPoint>();
        var excluded = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var point in points)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Id) || string.IsNullOrWhiteSpace(point.Task))
                throw new ArgumentException("A measurement requires a nonempty ID and task.", nameof(points));
            if (!ids.Add(point.Id)) throw new ArgumentException("Duplicate measurement ID: " + point.Id, nameof(points));
            Validate(point.Accuracy, 1, "accuracy");
            Validate(point.CutP95Ms, double.MaxValue, "cut P95");
            Validate(point.CpuMs, double.MaxValue, "CPU");
            if (point.MemoryBytes < 0) throw new ArgumentException("Memory must be nonnegative.", nameof(points));
            var missing = new List<string>();
            if (!point.Accuracy.HasValue) missing.Add("accuracy unavailable");
            if (!point.CutP95Ms.HasValue) missing.Add("cut P95 unavailable");
            if (!point.CpuMs.HasValue) missing.Add("per-method CPU unavailable");
            if (!point.MemoryBytes.HasValue) missing.Add("per-method memory unavailable");
            if (missing.Count > 0) excluded.Add(point.Id, missing);
            else eligible.Add(point);
        }
        var dominated = eligible.ToDictionary(p => p.Id,
            p => (IReadOnlyList<string>)eligible.Where(other => other.Id != p.Id && Dominates(other, p))
                .Select(other => other.Id).ToArray(), StringComparer.Ordinal);
        return new(eligible.Select(p => p.Id).ToArray(),
            eligible.Where(p => dominated[p.Id].Count == 0).Select(p => p.Id).ToArray(), excluded, dominated);
    }

    private static void Validate(double? value, double maximum, string dimension)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value < 0 || value > maximum))
            throw new ArgumentException($"Invalid {dimension} measurement.");
    }

    private static bool Dominates(ParetoPoint a, ParetoPoint b) => a.Task == b.Task
        && a.Accuracy >= b.Accuracy && a.CutP95Ms <= b.CutP95Ms && a.CpuMs <= b.CpuMs && a.MemoryBytes <= b.MemoryBytes
        && (a.Accuracy > b.Accuracy || a.CutP95Ms < b.CutP95Ms || a.CpuMs < b.CpuMs || a.MemoryBytes < b.MemoryBytes);
}
