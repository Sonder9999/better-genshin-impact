namespace QuestVoice.Core;

public enum SourcePtsTrust { Trusted, MonotonicAdjusted, SyntheticFallback, Unknown }
public readonly record struct SourceFrameTime(double SchedulingMs, double? RawSourceMs, SourcePtsTrust Trust)
{
    public bool IsTrusted => Trust == SourcePtsTrust.Trusted && RawSourceMs is >= 0 && double.IsFinite(RawSourceMs.Value);
}
public sealed record TextMask(byte[] Pixels, int Width, int Height, int CoreCount, int EdgeCount,
    int LineWidth, int LineHeight, int OccupiedRows)
{
    public double Density => EdgeCount / (double)Math.Max(1, Width * Height);
}
public sealed record DialogueVisualFeatures(TextMask Body, TextMask Name)
{
    // Tiny captions need no minimum duration or gold name; UI gate supplies independent context.
    public bool Present => Body.EdgeCount >= Math.Max(6, Body.Width * Body.Height * .00035) &&
        Body.LineWidth >= 2 && Body.LineHeight >= 2 && Body.Density < .18;
    public bool Valid => Body.Width >= 8 && Body.Height >= 8 && Name.Width >= 8 && Name.Height >= 4;
    public int Quality => Body.EdgeCount + Name.EdgeCount / 4;
    public ulong Signature => unchecked(SubtitleMaskComparer.Signature(Body.Pixels) * 1099511628211UL ^ SubtitleMaskComparer.Signature(Name.Pixels));
    public static DialogueVisualFeatures Extract(VisualFrame body, VisualFrame name) =>
        new(ExtractMask(body.Rgb, body.Width, body.Height, false), ExtractMask(name.Rgb, name.Width, name.Height, true));

    private static TextMask ExtractMask(ReadOnlySpan<byte> rgb, int width, int height, bool gold)
    {
        if (width < 1 || height < 1 || rgb.Length != checked(width * height * 3)) throw new ArgumentException("Invalid RGB ROI");
        var mask = new byte[width * height];
        int core = 0, edge = 0, left = width, right = -1, top = height, bottom = -1, rows = 0;
        var radius = Math.Clamp((int)Math.Round(width / 600d * 3), 1, 6);
        for (var y = 0; y < height; y++)
        {
            var rowPresent = false;
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                int r = rgb[offset], g = rgb[offset + 1], b = rgb[offset + 2];
                var color = gold ? r > 157 && g > 92 && b < 162 && r > g + 9 && g > b + 22 :
                    r > 180 && g > 180 && b > 180 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 29;
                if (!color) continue;
                core++;
                var outlined = false;
                for (var distance = 1; distance <= radius && !outlined; distance++)
                {
                    outlined = Dark(rgb, width, height, x - distance, y) || Dark(rgb, width, height, x + distance, y) ||
                        Dark(rgb, width, height, x, y - distance) || Dark(rgb, width, height, x, y + distance);
                }
                if (!outlined) continue;
                mask[y * width + x] = 1; edge++; rowPresent = true;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            if (rowPresent) rows++;
        }
        return new(mask, width, height, core, edge, Math.Max(0, right - left + 1), Math.Max(0, bottom - top + 1), rows);
    }
    private static bool Dark(ReadOnlySpan<byte> rgb, int width, int height, int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return false;
        var p = (y * width + x) * 3;
        return (77 * rgb[p] + 150 * rgb[p + 1] + 29 * rgb[p + 2]) / 256 < 113;
    }
}

// REIMPLEMENT_INDEPENDENT: set intersection/union mathematics; no Subtitle Edit source copied.
public static class SubtitleMaskComparer
{
    public readonly record struct Difference(double Jaccard, double Containment, int Added, int Removed, int PreviousCount, int CurrentCount);
    public static Difference Compare(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (before.Length != after.Length) return new(0, 0, after.Count((byte)1), before.Count((byte)1), before.Count((byte)1), after.Count((byte)1));
        int kept = 0, removed = 0, added = 0;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] != 0) { if (after[i] != 0) kept++; else removed++; }
            else if (after[i] != 0) added++;
        }
        var union = kept + removed + added;
        return new(union == 0 ? 1 : kept / (double)union, kept + removed == 0 ? 1 : kept / (double)(kept + removed),
            added, removed, kept + removed, kept + added);
    }
    public static ulong Signature(ReadOnlySpan<byte> mask)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in mask) hash = unchecked((hash ^ value) * 1099511628211UL);
        return hash;
    }
}
