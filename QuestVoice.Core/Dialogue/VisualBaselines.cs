namespace QuestVoice.Core;

public sealed class VisualFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgb { get; }
    public double TimestampSeconds { get; }

    public VisualFrame(int width, int height, byte[] rgb, double timestampSeconds)
    {
        if (width <= 0 || height <= 0 || rgb is null || rgb.Length != (long)width * height * 3 ||
            !double.IsFinite(timestampSeconds) || timestampSeconds < 0)
            throw new ArgumentException("Invalid RGB24 frame or timestamp.");
        Width = width; Height = height; Rgb = rgb; TimestampSeconds = timestampSeconds;
    }
}

public sealed record BaselineEvent(string Method, double TimestampSeconds, double OnsetSeconds,
    double Score, double PreviousFeature, double CurrentFeature);

public sealed record FallDetectorOptions(double MinimumPresence = .004, double MaxRemainingRatio = .35,
    int MinStableFrames = 3, int ConfirmationFrames = 2, double CooldownSeconds = .15, int HistoryFrames = 8);

public interface IVisualBaseline
{
    string Method { get; }
    BaselineEvent? Observe(VisualFrame frame);
    void Reset();
}

// Only current/past frames enter this state machine. Detection time includes confirmation delay.
internal sealed class FallingFeatureState
{
    private readonly FallDetectorOptions options;
    private readonly Queue<double> history = new();
    private double lastTime = double.NegativeInfinity, lastHit = double.NegativeInfinity;
    private double pendingBaseline, onset;
    private int pending;

    public FallingFeatureState(FallDetectorOptions options)
    {
        if (!double.IsFinite(options.MinimumPresence) || options.MinimumPresence <= 0 ||
            !double.IsFinite(options.MaxRemainingRatio) || options.MaxRemainingRatio <= 0 || options.MaxRemainingRatio >= 1 ||
            options.MinStableFrames < 1 || options.ConfirmationFrames < 1 || options.HistoryFrames < options.MinStableFrames ||
            !double.IsFinite(options.CooldownSeconds) || options.CooldownSeconds < 0)
            throw new ArgumentException("Invalid fall detector options.");
        this.options = options;
    }

    public BaselineEvent? Observe(string method, double time, double feature)
    {
        if (time <= lastTime) throw new ArgumentException("Frame timestamps must strictly increase; reset between clips.");
        lastTime = time;
        var reference = pending > 0 ? pendingBaseline : history.Count == 0 ? 0 : Median(history);
        var armed = pending > 0 || history.Count >= options.MinStableFrames;
        if (armed && feature <= reference * options.MaxRemainingRatio)
        {
            if (pending++ == 0) { onset = time; pendingBaseline = reference; }
            if (pending >= options.ConfirmationFrames)
            {
                history.Clear(); pending = 0;
                if (time - lastHit < options.CooldownSeconds) return null;
                lastHit = time;
                return new BaselineEvent(method, time, onset, Math.Clamp(1 - feature / reference, 0, 1), reference, feature);
            }
            return null;
        }
        pending = 0;
        if (feature >= options.MinimumPresence)
        {
            history.Enqueue(feature);
            while (history.Count > options.HistoryFrames) history.Dequeue();
        }
        else history.Clear();
        return null;
    }

    public void Reset()
    {
        history.Clear(); pending = 0; lastTime = lastHit = double.NegativeInfinity;
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values[values.Length / 2];
    }
}

public sealed class BrightPixelDetector : IVisualBaseline
{
    private readonly FallingFeatureState state;
    public string Method => "A";
    public BrightPixelDetector(FallDetectorOptions? options = null) => state = new(options ?? new());
    public BaselineEvent? Observe(VisualFrame frame) => state.Observe(Method, frame.TimestampSeconds, Measure(frame));
    public void Reset() => state.Reset();

    public static double Measure(VisualFrame frame)
        => (double)VisualFeatures.Foreground(frame).Count(value => value) / (frame.Width * frame.Height);
}

public sealed record SsimDetectorOptions(int MaxDisplacement = 2, int WindowSize = 8,
    double MaxSimilarity = .70, double StabilitySimilarity = .95, double MinimumLocalVariance = .0005,
    int MinStableFrames = 3, int ConfirmationFrames = 2, double CooldownSeconds = .15);

/// <summary>Local luminance SSIM, with translation search and stable old/new structure confirmation.</summary>
public sealed class SsimDisplacementDetector : IVisualBaseline
{
    private readonly SsimDetectorOptions options;
    private LumaFrame? reference;
    private LumaFrame? candidate;
    private int stableFrames;
    private int confirmations;
    private double onset;
    private double lastTime = double.NegativeInfinity;
    private double lastEvent = double.NegativeInfinity;
    public string Method => "C";

    public SsimDisplacementDetector(SsimDetectorOptions? options = null)
    {
        this.options = options ?? new();
        var o = this.options;
        if (o.MaxDisplacement is < 0 or > 8 || o.WindowSize is < 2 or > 64 ||
            !double.IsFinite(o.MaxSimilarity) || o.MaxSimilarity is <= 0 or >= 1 ||
            !double.IsFinite(o.StabilitySimilarity) || o.StabilitySimilarity <= o.MaxSimilarity || o.StabilitySimilarity > 1 ||
            !double.IsFinite(o.MinimumLocalVariance) || o.MinimumLocalVariance is <= 0 or >= .25 ||
            o.MinStableFrames < 1 || o.ConfirmationFrames < 1 ||
            !double.IsFinite(o.CooldownSeconds) || o.CooldownSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public BaselineEvent? Observe(VisualFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.TimestampSeconds <= lastTime)
            throw new ArgumentException("Frame timestamps must strictly increase; reset between clips.");
        if (reference is not null && (reference.Width != frame.Width || reference.Height != frame.Height))
            throw new ArgumentException("Frame dimensions must remain constant; reset between clips.");
        var current = ToLuma(frame);
        lastTime = frame.TimestampSeconds;
        if (reference is null)
        {
            SetReference(current);
            return null;
        }

        var similarity = Compare(reference, current, options);
        if (similarity >= options.StabilitySimilarity)
        {
            candidate = null;
            confirmations = 0;
            if (HasStructure(reference, options)) stableFrames++;
            return null;
        }
        if (stableFrames < options.MinStableFrames || similarity > options.MaxSimilarity ||
            frame.TimestampSeconds - lastEvent < options.CooldownSeconds)
        {
            SetReference(current);
            return null;
        }

        // Appending glyphs can lower SSIM sharply while all old glyph edges survive.
        // Require signed edge retention, so a flat bright flash cannot masquerade as text growth.
        if (RetainsOldEdges(reference, current, options.MaxDisplacement))
        {
            SetReference(current);
            return null;
        }

        // A changing new shape (e.g. typewriter animation) cannot confirm a transition.
        if (candidate is null || Compare(candidate, current, options) < options.StabilitySimilarity)
        {
            candidate = current;
            onset = frame.TimestampSeconds;
            confirmations = 1;
        }
        else confirmations++;
        if (confirmations < options.ConfirmationFrames) return null;

        var result = new BaselineEvent(Method, frame.TimestampSeconds, onset, 1 - similarity, 1, similarity);
        lastEvent = frame.TimestampSeconds;
        SetReference(current);
        return result;
    }

    public void Reset()
    {
        reference = candidate = null;
        stableFrames = confirmations = 0;
        onset = 0;
        lastTime = lastEvent = double.NegativeInfinity;
    }

    public static double Compare(VisualFrame first, VisualFrame second, int maxDisplacement = 2)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Width != second.Width || first.Height != second.Height)
            throw new ArgumentException("SSIM requires matching frame dimensions.");
        var detector = new SsimDisplacementDetector(new SsimDetectorOptions(MaxDisplacement: maxDisplacement));
        return Compare(ToLuma(first), ToLuma(second), detector.options);
    }

    private void SetReference(LumaFrame frame)
    {
        reference = frame;
        stableFrames = HasStructure(frame, options) ? 1 : 0;
        candidate = null;
        confirmations = 0;
    }

    private sealed record LumaFrame(int Width, int Height, float[] Pixels);

    private static LumaFrame ToLuma(VisualFrame frame)
    {
        // Own the snapshot: decoders may reuse their RGB buffers after Observe returns.
        var pixels = new float[frame.Width * frame.Height];
        for (var p = 0; p < pixels.Length; p++)
        {
            var i = p * 3;
            pixels[p] = (float)((.2126 * frame.Rgb[i] + .7152 * frame.Rgb[i + 1] + .0722 * frame.Rgb[i + 2]) / 255);
        }
        return new(frame.Width, frame.Height, pixels);
    }

    private static bool HasStructure(LumaFrame frame, SsimDetectorOptions o)
    {
        for (var top = 0; top < frame.Height; top += o.WindowSize)
            for (var left = 0; left < frame.Width; left += o.WindowSize)
            {
                double sum = 0, squares = 0;
                var count = 0;
                for (var y = top; y < Math.Min(top + o.WindowSize, frame.Height); y++)
                    for (var x = left; x < Math.Min(left + o.WindowSize, frame.Width); x++)
                    {
                        var value = frame.Pixels[y * frame.Width + x];
                        sum += value;
                        squares += value * value;
                        count++;
                    }
                if (squares / count - Math.Pow(sum / count, 2) >= o.MinimumLocalVariance) return true;
            }
        return false;
    }

    private static double Compare(LumaFrame a, LumaFrame b, SsimDetectorOptions o)
    {
        var radius = Math.Min(o.MaxDisplacement, (Math.Min(a.Width, a.Height) - 1) / 2);
        var best = SimilarityAtOffset(a, b, o, radius, 0, 0);
        if (best >= 1 - 1e-9) return 1;
        for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                best = Math.Max(best, SimilarityAtOffset(a, b, o, radius, dx, dy));
                if (best >= 1 - 1e-9) return 1;
            }
        return best;
    }

    private static bool RetainsOldEdges(LumaFrame oldFrame, LumaFrame current, int maxDisplacement)
    {
        var radius = Math.Min(maxDisplacement, Math.Max(0, (Math.Min(oldFrame.Width, oldFrame.Height) - 3) / 2));
        var width = oldFrame.Width;
        for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                double total = 0, retained = 0;
                for (var y = radius + 1; y < oldFrame.Height - radius - 1; y++)
                    for (var x = radius + 1; x < width - radius - 1; x++)
                    {
                        var i = y * width + x;
                        var gx = oldFrame.Pixels[i + 1] - oldFrame.Pixels[i - 1];
                        var gy = oldFrame.Pixels[i + width] - oldFrame.Pixels[i - width];
                        var energy = gx * gx + gy * gy;
                        if (energy < .04) continue;
                        total += energy;
                        var j = (y + dy) * width + x + dx;
                        var cx = current.Pixels[j + 1] - current.Pixels[j - 1];
                        var cy = current.Pixels[j + width] - current.Pixels[j - width];
                        var error = (cx - gx) * (cx - gx) + (cy - gy) * (cy - gy);
                        if (error <= .0625 * energy) retained += energy;
                    }
                if (total >= 4 && retained / total >= .95) return true;
            }
        return false;
    }

    private static double SimilarityAtOffset(LumaFrame a, LumaFrame b, SsimDetectorOptions o, int margin, int dx, int dy)
    {
        double sum = 0;
        var windows = 0;
        // Fixed common support keeps every translation candidate comparable.
        for (var top = margin; top < a.Height - margin; top += o.WindowSize)
            for (var left = margin; left < a.Width - margin; left += o.WindowSize)
            {
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                var count = 0;
                var bottom = Math.Min(top + o.WindowSize, a.Height - margin);
                var right = Math.Min(left + o.WindowSize, a.Width - margin);
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        double av = a.Pixels[y * a.Width + x];
                        double bv = b.Pixels[(y + dy) * b.Width + x + dx];
                        sx += av; sy += bv; sxx += av * av; syy += bv * bv; sxy += av * bv;
                        count++;
                    }
                var mx = sx / count;
                var my = sy / count;
                var vx = Math.Max(0, sxx / count - mx * mx);
                var vy = Math.Max(0, syy / count - my * my);
                // Ignore flat background windows so a small subtitle is not diluted by the ROI area.
                if (Math.Max(vx, vy) < o.MinimumLocalVariance) continue;
                var covariance = sxy / count - mx * my;
                const double c1 = .01 * .01, c2 = .03 * .03;
                sum += ((2 * mx * my + c1) * (2 * covariance + c2)) /
                    ((mx * mx + my * my + c1) * (vx + vy + c2));
                windows++;
            }
        return windows == 0 ? 1 : Math.Clamp(sum / windows, -1, 1);
    }
}

public sealed record EdgeComponentFeatures(double EdgeDensity, int Components);

public sealed class EdgeComponentDetector : IVisualBaseline
{
    private readonly FallingFeatureState edges, components;
    public string Method => "B";
    public EdgeComponentDetector(FallDetectorOptions? options = null)
    {
        var settings = options ?? new();
        edges = new(settings);
        components = new(settings with { MinimumPresence = 1 });
    }
    public BaselineEvent? Observe(VisualFrame frame)
    {
        var feature = Measure(frame);
        var edgeHit = edges.Observe(Method, frame.TimestampSeconds, feature.EdgeDensity);
        var componentHit = components.Observe(Method, frame.TimestampSeconds, feature.Components);
        return edgeHit is not null && componentHit is not null
            ? edgeHit with { Score = Math.Min(edgeHit.Score, componentHit.Score) } : null;
    }
    public void Reset() { edges.Reset(); components.Reset(); }

    public static EdgeComponentFeatures Measure(VisualFrame frame)
    {
        var mask = VisualFeatures.Foreground(frame);
        var width = frame.Width; var height = frame.Height;
        var edgeCount = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                if ((x > 0 && mask[p] != mask[p - 1]) || (x + 1 < width && mask[p] != mask[p + 1]) ||
                    (y > 0 && mask[p] != mask[p - width]) || (y + 1 < height && mask[p] != mask[p + width])) edgeCount++;
            }
        var visited = new bool[mask.Length];
        var queue = new Queue<int>();
        var count = 0;
        for (var p = 0; p < mask.Length; p++)
        {
            if (!mask[p] || visited[p]) continue;
            visited[p] = true; queue.Enqueue(p);
            var area = 0;
            while (queue.TryDequeue(out var current))
            {
                area++;
                var x = current % width; var y = current / width;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx; var ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        var neighbor = ny * width + nx;
                        if (!mask[neighbor] || visited[neighbor]) continue;
                        visited[neighbor] = true; queue.Enqueue(neighbor);
                    }
            }
            if (area >= 4) count++;
        }
        return new((double)edgeCount / mask.Length, count);
    }
}

internal static class VisualFeatures
{
    internal static readonly (int X, int Y)[] ContrastOffsets = [(-2, 0), (2, 0), (0, -2), (0, 2)];

    internal static bool[] Foreground(VisualFrame frame)
    {
        var mask = new bool[frame.Width * frame.Height];
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var index = (y * frame.Width + x) * 3;
                var r = frame.Rgb[index]; var g = frame.Rgb[index + 1]; var b = frame.Rgb[index + 2];
                var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
                if (min < 180 || max - min > 55) continue;
                var localMin = min;
                foreach (var (dx, dy) in VisualFeatures.ContrastOffsets)
                {
                    var nx = Math.Clamp(x + dx, 0, frame.Width - 1); var ny = Math.Clamp(y + dy, 0, frame.Height - 1);
                    var ni = (ny * frame.Width + nx) * 3;
                    localMin = Math.Min(localMin, Math.Min(frame.Rgb[ni], Math.Min(frame.Rgb[ni + 1], frame.Rgb[ni + 2])));
                }
                if (min - localMin >= 25) mask[y * frame.Width + x] = true;
            }
        return mask;
    }
}
