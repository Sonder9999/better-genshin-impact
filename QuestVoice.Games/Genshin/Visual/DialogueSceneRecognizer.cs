using QuestVoice.Core;
using System.Diagnostics;

namespace QuestVoice.Games.Genshin;

public sealed record DialogueSceneObservation(UIGateResult Gate, int GlyphCount, OCRObservation? Ocr,
    TextMatch? TextMatch, LineMatchEvent? TextEvent, TextMatch? FullTextMatch, LineMatchEvent? FullTextEvent,
    TextMatch? TemplateMatch, LineMatchEvent? TemplateEvent, double GateMs, double GlyphMs, double OcrMs, double TemplateMs);

public sealed class DialogueSceneRecognizer
{
    private readonly NormRect bodyROI;
    private readonly IReadOnlyList<LineCandidate> candidates;
    private readonly CandidateGraph graph;
    private readonly GlyphOCR ocr;
    private readonly GlyphLineMatcher templates;
    private readonly UIGate gate = new();
    private readonly OCRSchedule schedule = new();
    private readonly SemanticMatcher matcher = new();
    private readonly SemanticLineDetector textEvents = new();
    private string? textKey, fullKey, templateKey;
    private double lastFull = double.NegativeInfinity, lastTemplate = double.NegativeInfinity;
    private double previousDensity;
    private IReadOnlyList<float[]> previousGlyphs = [];
    public DialogueSceneRecognizer(IReadOnlyList<LineCandidate> candidates, IReadOnlyList<CandidateLink> links,
        GlyphOCR ocr, GlyphLineMatcher templates, NormRect? bodyROI = null)
    {
        this.bodyROI = bodyROI ?? GenshinGameAdapter.DefaultRecognitionBodyROI;
        this.candidates = candidates;
        graph = new(candidates, links);
        this.ocr = ocr;
        this.templates = templates;
    }
    public DialogueSceneObservation Observe(VisualFrame frame)
    {
        var start = Stopwatch.GetTimestamp(); var ui = gate.Observe(frame);
        var gateMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        // A game adapter owns its subtitle geometry; experiments may override this ROI.
        // Do not bind the recognizer to a test phase or named quest.
        var body = VisualRecognitionEvaluation.Crop(frame, bodyROI.Left, bodyROI.Top, bodyROI.Right, bodyROI.Bottom);
        var density = BrightPixelDetector.Measure(body);
        var changed = Math.Abs(density - previousDensity) >= Math.Max(.0005, previousDensity * .12);
        previousDensity = density;
        var glyphs = GlyphFeatures.Extract(body, 128);
        changed |= glyphs.Count != previousGlyphs.Count || Enumerable.Range(0, Math.Min(6, Math.Min(glyphs.Count, previousGlyphs.Count)))
            .Any(i => GlyphFeatures.Similarity(glyphs[i], previousGlyphs[i]) < .9);
        previousGlyphs = glyphs;
        var glyphMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var time = frame.TimestampSeconds;
        var prefixDue = schedule.ShouldRead(time, glyphs.Count >= 3, changed);
        var fullDue = glyphs.Count >= 3 && time - lastFull >= .2;
        OCRObservation? observation = null; TextMatch? textMatch = null, fullMatch = null, templateMatch = null;
        LineMatchEvent? textEvent = null, fullEvent = null, templateEvent = null;
        double ocrMs = 0, templateMs = 0;
        if (prefixDue || fullDue)
        {
            start = Stopwatch.GetTimestamp(); var recognized = ocr.Read(glyphs);
            ocrMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            // Offline replay pauses video PTS during synchronous inference. Wall cost is separate;
            // identical request/return PTS must never be advertised as real-time OCR latency.
            observation = new(time, time, recognized.Text, recognized.Confidence, ocrMs);
            if (prefixDue)
            {
                var scope = textKey is null ? candidates : candidates.Where(c => c.Key == textKey)
                    .Concat(graph.Successors(textKey, 2)).DistinctBy(c => c.Key).ToArray();
                var prefix = observation with { Text = observation.Text[..Math.Min(12, observation.Text.Length)] };
                textMatch = matcher.Match(prefix.Text, scope);
                textEvent = textEvents.Observe(prefix, scope);
                if (textEvent is not null) textKey = textEvent.Key;
            }
            if (fullDue)
            {
                lastFull = time; fullMatch = matcher.MatchFull(observation.Text, candidates);
                if (observation.Confidence >= .55 && fullMatch.Key is not null && fullMatch.Key != fullKey)
                { fullKey = fullMatch.Key; fullEvent = new(fullKey, fullMatch.Id!, time, time, fullMatch.Score); }
            }
        }
        if (glyphs.Count >= 3 && time - lastTemplate >= .1)
        {
            lastTemplate = time; start = Stopwatch.GetTimestamp(); templateMatch = templates.Match(glyphs);
            templateMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (templateMatch.Key is not null && templateMatch.Key != templateKey)
            { templateKey = templateMatch.Key; templateEvent = new(templateKey, templateMatch.Id!, time, time, templateMatch.Score); }
        }
        return new(ui, glyphs.Count, observation, textMatch, textEvent, fullMatch, fullEvent, templateMatch, templateEvent,
            gateMs, glyphMs, ocrMs, templateMs);
    }
}
