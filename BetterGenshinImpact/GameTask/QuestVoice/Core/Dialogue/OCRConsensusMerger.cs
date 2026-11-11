using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public enum OCRConsensusState { Provisional, Consensus, Unresolved }
public sealed record OCREvidence(long InstanceId, int FrameIndex, SourceFrameTime Time, string RawText, string Body,
    string Speaker, bool Available, string? Error, int Quality, double ElapsedMs, double AvailableMs);
public sealed record OCRConsensusResult(OCRConsensusState State, OCREvidence? Selected, int AgreementCount, bool SpeakerConflict,
    IReadOnlyList<OCREvidence> Evidence);

/// <summary>No recognition confidence is inferred from agreement. Short captions require exact equality.</summary>
public static class OCRConsensusMerger
{
    public static OCRConsensusResult Merge(IReadOnlyList<OCREvidence> evidence)
    {
        if (evidence.Count > 3 || evidence.Select(e => e.InstanceId).Distinct().Count() > 1) throw new ArgumentException("Not one bounded instance");
        var valid = evidence.Where(e => e.Available && SemanticMatcher.Normalize(e.Body).Length > 0).ToArray();
        var speakers = valid.Select(e => SemanticMatcher.Normalize(e.Speaker)).Where(s => s.Length > 0).Distinct().ToArray();
        var conflict = speakers.Length > 1;
        if (valid.Length == 0) return new(OCRConsensusState.Unresolved, null, 0, conflict, evidence.ToArray());
        var best = valid.OrderByDescending(a => valid.Count(b => Agree(a.Body, b.Body))).ThenByDescending(a => SemanticMatcher.Normalize(a.Body).Length)
            .ThenByDescending(a => a.Quality).ThenByDescending(a => a.Time.SchedulingMs).First();
        var count = valid.Count(e => Agree(best.Body, e.Body));
        var state = conflict || count < valid.Length ? OCRConsensusState.Unresolved : count >= 2 ? OCRConsensusState.Consensus : OCRConsensusState.Provisional;
        return new(state, best, count, conflict, evidence.ToArray());
    }
    private static bool Agree(string left, string right)
    {
        var a = SemanticMatcher.Normalize(left); var b = SemanticMatcher.Normalize(right);
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) < 6) return false;
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)) return true;
        if (Math.Min(a.Length, b.Length) < 12) return false;
        var distance = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) distance[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = distance[0]; distance[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = distance[j]; distance[j] = Math.Min(Math.Min(distance[j] + 1, distance[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = old;
            }
        }
        return distance[^1] <= Math.Max(a.Length, b.Length) * .08;
    }
}
