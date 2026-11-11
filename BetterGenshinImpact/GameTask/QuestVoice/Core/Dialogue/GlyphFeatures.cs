using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Text;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public static class GlyphFeatures
{
    public static IReadOnlyList<float[]> Extract(VisualFrame frame, int maxGlyphs = 16)
    {
        var mask = Mask(frame); var w = frame.Width; var h = frame.Height;
        var rowActive = new bool[h];
        for (var y = 0; y < h; y++)
        {
            var count = 0; for (var x = 0; x < w; x++) if (mask[y * w + x]) count++;
            rowActive[y] = count > 0;
        }
        var output = new List<float[]>();
        foreach (var (top, bottom) in Runs(rowActive, 2))
        {
            var height = bottom - top + 1;
            if (height is < 4 or > 48) continue;
            var columns = new bool[w];
            for (var x = 0; x < w; x++) for (var y = top; y <= bottom; y++) columns[x] |= mask[y * w + x];
            foreach (var (left, right) in Runs(columns, 0))
            {
                if (right - left + 1 > height * 2 || right - left < 1) continue;
                output.Add(Encode(mask, w, left, top, right, bottom));
                if (output.Count == maxGlyphs) return output;
            }
        }
        return output;
    }
    public static float[] Single(VisualFrame frame)
    {
        var mask = Mask(frame); var left = frame.Width; var top = frame.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width; x++)
            if (mask[y * frame.Width + x]) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return right < left ? new float[256] : Encode(mask, frame.Width, left, top, right, bottom);
    }
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length != 256 || b.Length != 256) throw new ArgumentException("Expected 16x16 normalized features.");
        var sum = Vector<float>.Zero;
        for (var i = 0; i < 256; i += Vector<float>.Count) sum += new Vector<float>(a, i) * new Vector<float>(b, i);
        return Math.Clamp(Vector.Dot(sum, Vector<float>.One), 0, 1);
    }
    private static bool[] Mask(VisualFrame f)
    {
        var mask = new bool[f.Width * f.Height];
        for (var i = 0; i < mask.Length; i++)
        {
            var r = f.Rgb[i * 3]; var g = f.Rgb[i * 3 + 1]; var b = f.Rgb[i * 3 + 2];
            var min = Math.Min(r, Math.Min(g, b)); var max = Math.Max(r, Math.Max(g, b));
            mask[i] = min >= 180 && max - min <= 55;
        }
        return mask;
    }
    private static IEnumerable<(int Start, int End)> Runs(bool[] values, int allowedGap)
    {
        var start = -1; var last = -1;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i]) { if (start < 0) start = i; last = i; }
            if (start >= 0 && (i - last > allowedGap || i == values.Length - 1))
            { yield return (start, last); start = -1; }
        }
    }
    private static float[] Encode(bool[] mask, int stride, int left, int top, int right, int bottom)
    {
        // Recompute ink bounds so padding in a segmented row cannot change a glyph feature.
        var l = right + 1; var r = left - 1; var t = bottom + 1; var b = top - 1;
        for (var y = top; y <= bottom; y++) for (var x = left; x <= right; x++)
            if (mask[y * stride + x]) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
        var output = new float[256]; if (r < l) return output;
        var width = r - l + 1; var height = b - t + 1; var scale = 14d / Math.Max(width, height);
        var offsetX = (16 - width * scale) / 2; var offsetY = (16 - height * scale) / 2;
        for (var y = t; y <= b; y++) for (var x = l; x <= r; x++)
            if (mask[y * stride + x])
            {
                var ox = Math.Clamp((int)(offsetX + (x - l) * scale), 0, 15);
                var oy = Math.Clamp((int)(offsetY + (y - t) * scale), 0, 15);
                output[oy * 16 + ox] += 1;
            }
        var norm = Math.Sqrt(output.Sum(v => v * v));
        if (norm > 0) for (var i = 0; i < output.Length; i++) output[i] /= (float)norm;
        return output;
    }
}
public sealed record GlyphLineTemplate(LineCandidate Candidate, IReadOnlyList<float[]> Glyphs);
public sealed class GlyphLineMatcher
{
    private readonly GlyphLineTemplate[] templates;
    public GlyphLineMatcher(IReadOnlyList<GlyphLineTemplate> templates) => this.templates = templates
        .Select(t => new GlyphLineTemplate(t.Candidate, t.Glyphs.Select(g => g.ToArray()).ToArray())).ToArray();
    public TextMatch Match(IReadOnlyList<float[]> glyphs)
    {
        if (glyphs.Count < 3) return new(null, null, 0, 0, "insufficient_glyphs");
        var ranked = templates.Where(t => t.Glyphs.Count >= 3).Select(t =>
        {
            var count = Math.Min(6, Math.Min(glyphs.Count, t.Glyphs.Count));
            var score = Enumerable.Range(0, count).Average(i => GlyphFeatures.Similarity(glyphs[i], t.Glyphs[i]));
            return (t.Candidate, Score: score);
        }).OrderByDescending(t => t.Score).ThenBy(t => t.Candidate.Key, StringComparer.Ordinal).ToArray();
        if (ranked.Length == 0) return new(null, null, 0, 0, "no_templates");
        var best = ranked[0]; var margin = best.Score - (ranked.Length > 1 ? ranked[1].Score : 0);
        var reason = best.Score < .80 ? "low_template_similarity" : margin < .025 ? "ambiguous_template" : "unique_template";
        return new(reason == "unique_template" ? best.Candidate.Key : null, reason == "unique_template" ? best.Candidate.Id : null,
            best.Score, margin, reason);
    }
}
public sealed record GlyphOCRResult(string Text, double Confidence);
public sealed class GlyphOCR
{
    private readonly KeyValuePair<char, float[]>[] alphabet;
    public GlyphOCR(IReadOnlyDictionary<char, float[]> alphabet) => this.alphabet = alphabet.Select(p => KeyValuePair.Create(p.Key, p.Value.ToArray())).ToArray();
    public GlyphOCRResult Read(IReadOnlyList<float[]> glyphs)
    {
        var text = new StringBuilder(); double confidence = 0;
        foreach (var glyph in glyphs)
        {
            var best = 0d; var second = 0d; var character = '\uFFFD';
            foreach (var entry in alphabet)
            {
                var score = GlyphFeatures.Similarity(glyph, entry.Value);
                if (score > best) { second = best; best = score; character = entry.Key; }
                else if (score > second) second = score;
            }
            if (best < .65 || best - second < .015) text.Append('\uFFFD');
            else { text.Append(character); confidence += best; }
        }
        return new(text.ToString(), glyphs.Count == 0 ? 0 : confidence / glyphs.Count);
    }
}
