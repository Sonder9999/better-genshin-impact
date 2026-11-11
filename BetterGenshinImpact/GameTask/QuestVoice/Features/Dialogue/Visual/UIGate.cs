using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.QuestVoice.Domain;
namespace BetterGenshinImpact.GameTask.QuestVoice.Genshin;


public sealed class UIGate
{
    private double lastTime = double.NegativeInfinity;
    private int previousControlRows, consistentControlFrames;
    private sealed record OptionRow(double X, double Y, double Width);
    private OptionRow[] previousLayout = [];
    public UIGateResult Observe(VisualFrame frame, bool targetKnown = false)
    {
        if (frame.TimestampSeconds <= lastTime) throw new ArgumentException("UI frames require increasing PTS.");
        var gap = frame.TimestampSeconds - lastTime;
        lastTime = frame.TimestampSeconds;
        var (score, brightness) = MeasureEye(frame);
        if (score < .30)
        {
            // A pale dialogue button can merge with a bright wall or sky, defeating the
            // bright-component pass even though the dark crossed-eye mark is still drawn.
            // Fall back to the mark itself only when the bright pass found no usable anchor.
            var (darkScore, darkBrightness) = MeasureDarkEye(frame);
            if (darkScore > score) { score = darkScore; brightness = darkBrightness; }
        }
        var eye = score < .30 ? EyeState.Hidden : brightness >= 205 ? EyeState.Highlighted : EyeState.Translucent;
        var (candidateRows, layout) = CountOptionRows(frame);
        var controlRows = layout.Length;
        var visible = eye != EyeState.Hidden;
        var black = IsBlackScene(frame);
        consistentControlFrames = visible && !black && controlRows > 0
            ? gap <= .45 && controlRows == previousControlRows && layout.Zip(previousLayout).All(pair =>
                Math.Abs(pair.First.X-pair.Second.X)<=frame.Width*.01 &&
                Math.Abs(pair.First.Y-pair.Second.Y)<=frame.Height*.01 &&
                Math.Abs(pair.First.Width-pair.Second.Width)<=frame.Width*.03) ? consistentControlFrames + 1 : 1 : 0;
        previousControlRows = controlRows;
        previousLayout = layout;
        var choice = consistentControlFrames >= 2;
        var evidence = choice ? OptionEvidenceState.Confirmed : candidateRows > 0 ? OptionEvidenceState.Suspected : OptionEvidenceState.None;
        return new(frame.TimestampSeconds, eye, score, choice ? controlRows : 0, visible, choice, targetKnown,
            visible && !choice && !black, visible && !choice && !black && targetKnown, black, candidateRows, evidence);
    }
    public void Reset() { lastTime = double.NegativeInfinity; previousControlRows = consistentControlFrames = 0; previousLayout = []; }

    // Contrast of a pale circular UI button with a dark diagonal internal mark. This is an anchor
    // baseline, not alpha recovery: Highlighted/Translucent are observed brightness classes.
    private static (double Score, double Brightness) MeasureEye(VisualFrame f)
    {
        var left = (int)(f.Width * .115); var width = (int)(f.Width * .245) - left;
        var height = (int)(f.Height * .085);
        var centers = new List<(double X, double Y, double Radius)>(); var queue = new Queue<int>();
        // Separate bright and dim passes prevent an opaque icon from joining a midtone backdrop.
        foreach (var threshold in new[] { 180, 100 })
        {
        var mask = new bool[width * height];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var p = (y * f.Width + x + left) * 3;
            var lo = Math.Min(f.Rgb[p], Math.Min(f.Rgb[p + 1], f.Rgb[p + 2]));
            var hi = Math.Max(f.Rgb[p], Math.Max(f.Rgb[p + 1], f.Rgb[p + 2]));
            mask[y * width + x] = lo >= threshold && hi - lo <= 45;
        }
        for (var p = 0; p < mask.Length; p++)
        {
            if (!mask[p]) continue;
            mask[p] = false; queue.Enqueue(p);
            var minX = p % width; var maxX = minX; var minY = p / width; var maxY = minY; var count = 0;
            while (queue.TryDequeue(out var q))
            {
                var x = q % width; var y = q / width; count++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var n = ny * width + nx;
                    if (mask[n]) { mask[n] = false; queue.Enqueue(n); }
                }
            }
            var w = maxX - minX + 1; var h = maxY - minY + 1;
            var cx = left + (minX + maxX) / 2d; var cy = (minY + maxY) / 2d;
            if (h >= f.Height * .018 && h <= f.Height * .05 && w >= h * .8 && w <= h * 1.2 &&
                count >= w * h * .3 && cx >= f.Width * .135 && cx <= f.Width * .205 && cy >= f.Height * .035 && cy <= f.Height * .055)
                centers.Add((cx, cy, (w + h - 2) / 4d));
        }
        }
        double best = 0, brightness = 0;
        foreach (var (x, y, radius) in centers)
            {
                double ring = 0, outside = 0, inside = 0;
                for (var i = 0; i < 16; i++)
                {
                    var angle = i * Math.PI / 8;
                    ring += Sample(.78); outside += Sample(1.35); inside += Sample(.32);
                    double Sample(double r) => At(Math.Cos(angle) * radius * r, Math.Sin(angle) * radius * r);
                }
                ring /= 16; outside /= 16; inside /= 16;
                double diagonal = 0, perpendicular = 0;
                foreach (var t in new[] { -.45, -.35, -.25, .25, .35, .45 })
                { diagonal += At(t * radius, -t * radius); perpendicular += At(t * radius, t * radius); }
                var slash = (perpendicular - diagonal) / 6;
                var directional = slash / 80;
                var score = Math.Clamp(Math.Min(Math.Min((ring - outside) / 80, (ring - inside) / 45), directional), 0, 1);
                if (score < .30) continue;
                if (score > best || score == best && ring > brightness) { best = score; brightness = ring; }
                double At(double dx, double dy)
                {
                    var px = Math.Clamp((int)Math.Round(x + dx), 0, f.Width - 1);
                    var py = Math.Clamp((int)Math.Round(y + dy), 0, f.Height - 1);
                    var p = (py * f.Width + px) * 3;
                    var lo = Math.Min(f.Rgb[p], Math.Min(f.Rgb[p + 1], f.Rgb[p + 2]));
                    var hi = Math.Max(f.Rgb[p], Math.Max(f.Rgb[p + 1], f.Rgb[p + 2]));
                    return hi - lo <= 55 ? lo : lo * .5;
                }
            }
        return (best, brightness);
    }

    // Fallback anchor for the same crossed-eye button when its pale disc merges with a
    // bright backdrop. It scans the documented anchor for a bright ring crossed by a dark
    // diagonal mark; a brighter perpendicular keeps history/play glyphs and scene edges out.
    private static (double Score, double Brightness) MeasureDarkEye(VisualFrame f)
    {
        var x0 = (int)(f.Width * .160); var x1 = (int)(f.Width * .195);
        var y0 = (int)(f.Height * .038); var y1 = (int)(f.Height * .052);
        var minRadius = Math.Max(5, (int)(f.Height * .014));
        var maxRadius = Math.Max(minRadius, (int)(f.Height * .028));
        var stepX = Math.Max(1, f.Width / 480); var stepY = Math.Max(1, f.Height / 360);
        double best = 0, bestBrightness = 0;
        for (var cy = y0; cy <= y1; cy += stepY)
            for (var cx = x0; cx <= x1; cx += stepX)
                for (var radius = minRadius; radius <= maxRadius; radius++)
                {
                    double ring = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        var angle = i * Math.PI / 8;
                        ring += Lum(cx, cy, Math.Cos(angle) * radius * .85, Math.Sin(angle) * radius * .85);
                    }
                    ring /= 16;
                    if (ring < 170) continue;
                    double diagonal = 0, perpendicular = 0;
                    var darkDiagonal = 0; var brightPerpendicular = 0;
                    foreach (var t in new[] { -.6, -.45, -.3, .3, .45, .6 })
                    {
                        var d = Lum(cx, cy, t * radius, -t * radius);
                        var p = Lum(cx, cy, t * radius, t * radius);
                        diagonal += d; perpendicular += p;
                        if (d <= ring - 50) darkDiagonal++;
                        if (p >= d + 15 || p >= ring - 30) brightPerpendicular++;
                    }
                    if (darkDiagonal < 4 || brightPerpendicular < 4) continue;
                    diagonal /= 6; perpendicular /= 6;
                    var score = Math.Clamp(Math.Min((ring - diagonal) / 80, (perpendicular - diagonal) / 55), 0, 1);
                    if (score > best) { best = score; bestBrightness = ring; }
                }
        return (best, bestBrightness);

        double Lum(double centerX, double centerY, double dx, double dy)
        {
            var px = Math.Clamp((int)Math.Round(centerX + dx), 0, f.Width - 1);
            var py = Math.Clamp((int)Math.Round(centerY + dy), 0, f.Height - 1);
            var p = (py * f.Width + px) * 3;
            var lo = Math.Min(f.Rgb[p], Math.Min(f.Rgb[p + 1], f.Rgb[p + 2]));
            var hi = Math.Max(f.Rgb[p], Math.Max(f.Rgb[p + 1], f.Rgb[p + 2]));
            return hi - lo <= 55 ? lo : lo * .5;
        }
    }

    private static bool IsBlackScene(VisualFrame f)
    {
        var lit = 0; var count = 0;
        for (var y = (int)(f.Height * .12); y < f.Height * .95; y += Math.Max(1, f.Height / 36))
            for (var x = 0; x < f.Width; x += Math.Max(1, f.Width / 64))
            {
                var p = (y * f.Width + x) * 3; count++;
                if (Math.Max(f.Rgb[p], Math.Max(f.Rgb[p + 1], f.Rgb[p + 2])) > 20) lit++;
            }
        return count == 0 || (double)lit / count < .02;
    }

    // Group compact bright neutral connected components into horizontally aligned option text.
    // A single option is a legitimate wait state. Large bright scene objects are rejected.
    private static (int Candidates, OptionRow[] Controls) CountOptionRows(VisualFrame f)
    {
        var left = (int)(f.Width * .60); var top = (int)(f.Height * .40);
        var width = (int)(f.Width * .96) - left; var height = (int)(f.Height * .80) - top;
        var mask = new bool[width * height];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var p = ((y + top) * f.Width + x + left) * 3;
            var lo = Math.Min(f.Rgb[p], Math.Min(f.Rgb[p + 1], f.Rgb[p + 2]));
            var hi = Math.Max(f.Rgb[p], Math.Max(f.Rgb[p + 1], f.Rgb[p + 2]));
            mask[y * width + x] = lo >= 180 && hi - lo <= 45;
        }
        var components = new List<(int X, int Y, int Right, int Height, bool Bubble)>();
        var queue = new Queue<int>();
        for (var p = 0; p < mask.Length; p++)
        {
            if (!mask[p]) continue;
            mask[p] = false; queue.Enqueue(p);
            var minX = p % width; var maxX = minX; var minY = p / width; var maxY = minY; var count = 0;
            while (queue.TryDequeue(out var q))
            {
                var x = q % width; var y = q / width; count++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var n = ny * width + nx;
                    if (mask[n]) { mask[n] = false; queue.Enqueue(n); }
                }
            }
            var h = maxY - minY + 1; var w = maxX - minX + 1;
            if (count >= 4 && h >= f.Height * .012 && h <= f.Height * .045 && w <= f.Height * .045)
                components.Add((minX, (minY + maxY) / 2, maxX, h,
                    h>=f.Height*.02 && w>=h && w<=h*1.6 && HasBubbleDots(f,left+minX,top+minY,w,h)));
        }
        var rows = new List<List<(int X, int Y, int Right, int Height, bool Bubble)>>();
        foreach (var c in components.OrderBy(c => c.Y))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r.Average(v => v.Y) - c.Y) <= f.Height * .013);
            if (row is null) rows.Add([c]); else row.Add(c);
        }
        var candidates = rows.Where(r => r.Count >= 4 && r.Max(c => c.Right) - r.Min(c => c.X) >= f.Width * .05).ToArray();
        return (candidates.Length, candidates.Where(r => r.Any(c=>c.Bubble && c.X<=r.Min(x=>x.X)+f.Width*.045)
            && HasOptionBackground(f, r.Min(c=>c.X)+left,r.Max(c=>c.Right)+left,r.Average(c=>c.Y)+top,r.Average(c=>c.Height)))
            .Select(r=>new OptionRow(r.Min(c=>c.X)+left,r.Average(c=>c.Y)+top,r.Max(c=>c.Right)-r.Min(c=>c.X))).ToArray());
    }

    // The speech control has a connected pale bubble with three enclosed dark dots.
    // Counting enclosed components avoids confusing an F key, ordinary glyphs or bright bars
    // with the control icon. Geometry and the surrounding pill are checked independently.
    private static bool HasBubbleDots(VisualFrame f,int left,int top,int width,int height)
    {
        var seen=new bool[width*height];var holes=new List<(int Area,double X,double Y)>();var q=new Queue<int>();
        bool White(int x,int y)
        {
            var p=((top+y)*f.Width+left+x)*3;
            var lo=Math.Min(f.Rgb[p],Math.Min(f.Rgb[p+1],f.Rgb[p+2]));var hi=Math.Max(f.Rgb[p],Math.Max(f.Rgb[p+1],f.Rgb[p+2]));
            return lo>=180 && hi-lo<=45;
        }
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var k=y*width+x;if(seen[k] || White(x,y))continue;
            seen[k]=true;q.Enqueue(k);var border=false;var area=0;double sumX=0,sumY=0;
            while(q.TryDequeue(out var p))
            {
                var xx=p%width;var yy=p/width;area++;sumX+=xx;sumY+=yy;
                border|=xx==0 || yy==0 || xx==width-1 || yy==height-1;
                foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
                {
                    var nx=xx+dx;var ny=yy+dy;
                    if(nx<0 || nx>=width || ny<0 || ny>=height)continue;
                    var n=ny*width+nx;if(seen[n] || White(nx,ny))continue;seen[n]=true;q.Enqueue(n);
                }
            }
            if(!border && area>=Math.Max(3,height*height*.004))holes.Add((area,sumX/area,sumY/area));
        }
        if(holes.Count!=3)return false;
        var dots=holes.OrderBy(x=>x.X).ToArray();
        return dots.Max(x=>x.Y)-dots.Min(x=>x.Y)<height*.16 && dots[^1].X-dots[0].X>width*.25
            && dots.Max(x=>x.Area)<=dots.Min(x=>x.Area)*2.5;
    }

    // Option text sits inside a wide, dark neutral control. Bright scenery can form glyph-like
    // rows, but is not evidence of a control without this surrounding horizontal support.
    private static bool HasOptionBackground(VisualFrame f, int first, int last, double centerY, double glyphHeight)
    {
        var supported = 0; var count = 0;
        foreach (var dy in new[] { -1.05, 1.05 })
            for (var i = 0; i < 24; i++)
            {
                var x = Math.Clamp(first + (last-first)*i/23, 0, f.Width-1);
                var y = Math.Clamp((int)Math.Round(centerY + dy*glyphHeight),0,f.Height-1);
                var p=(y*f.Width+x)*3;
                var lo=Math.Min(f.Rgb[p],Math.Min(f.Rgb[p+1],f.Rgb[p+2]));
                var hi=Math.Max(f.Rgb[p],Math.Max(f.Rgb[p+1],f.Rgb[p+2]));
                count++; if(hi<175 && hi-lo<42) supported++;
            }
        return supported >= count*.8;
    }
}
