using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.QuestVoice.Domain;
namespace BetterGenshinImpact.GameTask.QuestVoice.Genshin;

public sealed class ValidationSignals
{
    private string? clipId; private int previousIndex=-1,previousGlyphs; private double previousTime=double.NegativeInfinity;
    public IReadOnlyList<FusionVisualSignal> Append(string clip,int index,double time,int glyphs,IReadOnlyList<FusionVisualSignal> candidates)
    {
        var (start,end)=ValidationEvaluation.Bounds(clip);
        if(index<0 || time<start || time>=end || !double.IsFinite(time) || time<=previousTime || index<=previousIndex || glyphs<0 || clipId is not null && clipId!=clip ||
            candidates.Any(c=>c.Method is not ("A" or "B" or "C") || c.Kind!=FusionVisualKind.PossibleClear || !double.IsFinite(c.SourceMs)||!double.IsFinite(c.AvailableMs)||c.SourceMs<start||c.SourceMs>c.AvailableMs||Math.Abs(c.AvailableMs-time)>.000001)) throw new ArgumentException("Invalid causal validation frame or candidate.");
        var signals=candidates.ToList();
        if(clipId is not null && previousGlyphs==0 && glyphs>0) signals.Add(new("B",FusionVisualKind.NewLineStarted,$"validation/glyph/{clip}/{index}",time,time,1));
        clipId=clip; previousIndex=index; previousTime=time; previousGlyphs=glyphs;
        return signals.AsReadOnly();
    }
}
