using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record LineCandidate(string Key, string Id, string Text, string Role = "");
public sealed record CandidateLink(string FromKey, string ToKey);
public sealed record TextMatch(string? Key, string? Id, double Score, double Margin, string Reason);
public sealed record OCRObservation(double RequestSeconds, double ReturnSeconds, string Text, double Confidence, double ElapsedMs);
public sealed record LineMatchEvent(string Key, string Id, double DecisionSeconds, double RequestSeconds, double Score);

public sealed class SemanticMatcher
{
    public TextMatch MatchFull(string text, IReadOnlyList<LineCandidate> candidates)
    {
        var length = Normalize(text).Length;
        return Match(text, candidates.Where(c => Math.Abs(Normalize(c.Text).Length - length) <= length * .1).ToArray());
    }
    public static string Normalize(string value)
    {
        // Genshin text carries inline control markup: ruby readings ({RUBY#[D]...}),
        // player-name placeholders and a leading voice marker. None of it is drawn as
        // dialogue text, so strip it before matching or OCR would be compared against
        // ruby readings it never sees. NFKC is applied first because full-width braces
        // and punctuation fold to their ASCII forms.
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '{')
            {
                var close = normalized.IndexOf('}', i + 1);
                if (close >= 0) { i = close; continue; }
            }
            if (c is '#' or '{' or '}') continue;
            // NFKC expands U+2026 to "...", and OCR may render the same pause as middle dots.
            // Ignore punctuation consistently so renderer/OCR variants do not shift the prefix.
            if (char.IsWhiteSpace(c) || "「」『』\"'、。，．,……・･.!！?？".Contains(c)) continue;
            builder.Append(c);
        }
        return builder.ToString();
    }

    public TextMatch Match(string text, IReadOnlyList<LineCandidate> candidates)
    {
        var observed = Normalize(text);
        if (observed.Length < 3) return new(null, null, 0, 0, "insufficient_prefix");
        var ranked = candidates.Select(c => (Candidate: c, Score: Score(observed, Normalize(c.Text))))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Candidate.Key, StringComparer.Ordinal).ToArray();
        if (ranked.Length == 0) return new(null, null, 0, 0, "no_candidates");
        var best = ranked[0]; var margin = best.Score - (ranked.Length > 1 ? ranked[1].Score : 0);
        var reason = best.Score < .78 ? "low_similarity" : margin < .08 ? "ambiguous_prefix" : "unique_prefix";
        return new(reason == "unique_prefix" ? best.Candidate.Key : null,
            reason == "unique_prefix" ? best.Candidate.Id : null, best.Score, margin, reason);
    }

    /// <summary>Read-only candidate ranking for the diagnostic UI; identity rules remain in Match.</summary>
    public IReadOnlyList<TextMatch> Rank(string text, IReadOnlyList<LineCandidate> candidates, int take = 6)
    {
        if (take is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(take));
        var observed = Normalize(text);
        if (observed.Length < 3 || candidates.Count == 0) return Array.Empty<TextMatch>();
        var scored = candidates.Select(c => (Candidate: c, Score: Score(observed, Normalize(c.Text))))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Candidate.Key, StringComparer.Ordinal).ToArray();
        var result = new List<TextMatch>();
        for (var i = 0; i < Math.Min(take, scored.Length); i++)
        {
            var best = scored[i];
            // Margin is measured against the strongest different candidate, not the next row.
            var other = scored.Where((s, index) => index != i).Select(s => s.Score).DefaultIfEmpty(0).Max();
            var margin = best.Score - other;
            var reason = best.Score < .78 ? "low_similarity" : margin < .08 ? "ambiguous_prefix" : "unique_prefix";
            result.Add(new(best.Candidate.Key, best.Candidate.Id, best.Score, margin, reason));
        }
        return result.AsReadOnly();
    }

    private static double Score(string observed, string candidate)
    {
        if (candidate.Length == 0) return 0;
        var prefix = candidate[..Math.Min(candidate.Length, observed.Length)];
        var previous = Enumerable.Range(0, prefix.Length + 1).ToArray();
        for (var i = 1; i <= observed.Length; i++)
        {
            var current = new int[prefix.Length + 1]; current[0] = i;
            for (var j = 1; j <= prefix.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                previous[j - 1] + (observed[i - 1] == prefix[j - 1] ? 0 : 1));
            previous = current;
        }
        var edit = 1d - previous[^1] / (double)Math.Max(observed.Length, prefix.Length);
        var bag = prefix.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var shared = 0;
        foreach (var c in observed) if (bag.TryGetValue(c, out var count) && count > 0) { shared++; bag[c]--; }
        // A character multiset provides limited OCR tolerance; word order still dominates.
        return .8 * edit + .2 * (2d * shared / (observed.Length + prefix.Length));
    }
}
public sealed class CandidateGraph
{
    private readonly Dictionary<string, LineCandidate> nodes;
    private readonly Dictionary<string, string[]> links;
    public CandidateGraph(IReadOnlyList<LineCandidate> nodes, IReadOnlyList<CandidateLink> links)
    {
        this.nodes = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        this.links = links.GroupBy(e => e.FromKey).ToDictionary(g => g.Key, g => g.Select(e => e.ToKey).Distinct().ToArray());
    }
    public IReadOnlyList<LineCandidate> Successors(string key, int depth = 1)
    {
        if (depth is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(depth));
        if (!nodes.ContainsKey(key)) return [];
        var visited = new HashSet<string>(StringComparer.Ordinal) { key };
        var queue = new Queue<(string Key, int Depth)>(); queue.Enqueue((key, 0));
        var result = new List<LineCandidate>();
        while (queue.TryDequeue(out var item))
        {
            if (item.Depth >= depth || !links.TryGetValue(item.Key, out var next)) continue;
            foreach (var target in next)
                if (visited.Add(target))
                {
                    if (nodes.TryGetValue(target, out var node)) result.Add(node);
                    // Player choices and other non-spoken nodes remain traversal vertices.
                    queue.Enqueue((target, item.Depth + 1));
                }
        }
        return result;
    }
}
public sealed class SemanticLineDetector
{
    private readonly SemanticMatcher matcher = new();
    private double lastTime = double.NegativeInfinity;
    private string? lastKey;
    public LineMatchEvent? Observe(OCRObservation ocr, IReadOnlyList<LineCandidate> candidates)
    {
        if (!double.IsFinite(ocr.RequestSeconds) || !double.IsFinite(ocr.ReturnSeconds) || ocr.RequestSeconds < 0 ||
            ocr.ReturnSeconds < ocr.RequestSeconds || ocr.ReturnSeconds <= lastTime ||
            !double.IsFinite(ocr.Confidence) || ocr.Confidence is < 0 or > 1 || !double.IsFinite(ocr.ElapsedMs) || ocr.ElapsedMs < 0)
            throw new ArgumentException("Invalid causal OCR observation.");
        lastTime = ocr.ReturnSeconds;
        if (ocr.Confidence < .55) return null;
        var match = matcher.Match(ocr.Text, candidates);
        if (match.Key is null || match.Key == lastKey) return null;
        lastKey = match.Key;
        return new(match.Key, match.Id!, ocr.ReturnSeconds, ocr.RequestSeconds, match.Score);
    }
    public void Reset() { lastTime = double.NegativeInfinity; lastKey = null; }
}
public sealed class OCRSchedule
{
    private double lastSeen = double.NegativeInfinity, lastRead = double.NegativeInfinity;
    public bool ShouldRead(double time, bool hasText, bool changed)
    {
        if (!double.IsFinite(time) || time < 0 || time <= lastSeen) throw new ArgumentException("OCR scheduler requires increasing PTS.");
        lastSeen = time;
        if (!hasText || time - lastRead < .1 || !changed && time - lastRead < .5) return false;
        lastRead = time;
        return true;
    }
}
