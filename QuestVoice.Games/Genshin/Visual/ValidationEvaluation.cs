using QuestVoice.Core;
namespace QuestVoice.Games.Genshin;

public sealed record ValidationEvent(string ClipId,string EventType,double DecisionMs,double OnsetMs,string? NodeId=null,string? Reason=null);
public sealed record ValidationLabel(string ClipId,string EventType,double TimestampMs,string? NodeId=null);
public static class ValidationEvaluation
{
    public static (double Start,double End) Bounds(string clip) => clip switch
    { "S2"=>(0,12603.911),"S3"=>(0,11081.733),"L1"=>(0,25000),"L2"=>(45000,90000),_=>throw new ArgumentException("Only S2/S3/L1/L2 second half are Validation.") };
    public static int[] SelectIndices(string clip,double[] pts)
    {
        ValidatePts(pts); var (start,end)=Bounds(clip);
        return Enumerable.Range(0,pts.Length).Where(i=>pts[i]>=start && pts[i]<end).ToArray();
    }
    public static int[] SampleIndices(double[] pts,int? fps)
    {
        ValidatePts(pts);
        if(fps is not null && fps is not (15 or 30 or 60)) throw new ArgumentException("Sampling is native/15/30/60 FPS only.");
        if(fps is null || pts.Length==0) return Enumerable.Range(0,pts.Length).ToArray();
        var result=new List<int>(); var slot=0;
        for(var i=0;i<pts.Length;i++)
        {
            if(pts[i]+.000001<pts[0]+slot*1000d/fps.Value) continue;
            result.Add(i);
            // Skip missed slots without creating a duplicate/interpolated frame.
            slot=(int)Math.Floor((pts[i]-pts[0]+.000001)*fps.Value/1000)+1;
        }
        return result.ToArray();
    }
    private static void ValidatePts(double[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        if(pts.Any(t=>!double.IsFinite(t)||t<0)||pts.Zip(pts.Skip(1)).Any(p=>p.First>=p.Second)) throw new ArgumentException("Expected increasing original PTS.");
    }
    public static EvaluationMetrics Score(IEnumerable<ValidationEvent> predictions,IEnumerable<ValidationLabel> truth,IEnumerable<EvaluationWindow> coverage,string eventType,double toleranceMs)
    {
        if(!double.IsFinite(toleranceMs)||toleranceMs<0) throw new ArgumentException("Invalid tolerance.");
        var windows=coverage.ToArray();
        // Reuse established coverage validation, but not its chronological greedy pairing.
        _=BaselineEvaluation.Evaluate([],[],windows,toleranceMs);
        int Window(string clip,double time)=>Array.FindIndex(windows,w=>w.ClipId==clip && time>=w.StartMs && time<w.EndMs);
        var all=predictions.Where(p=>p.EventType==eventType).ToArray(); var targets=truth.Where(t=>t.EventType==eventType).ToArray();
        if(all.Any(p=>!double.IsFinite(p.DecisionMs)||!double.IsFinite(p.OnsetMs)||p.OnsetMs<0||p.OnsetMs>p.DecisionMs)||targets.Any(t=>!double.IsFinite(t.TimestampMs)||t.TimestampMs<0)) throw new ArgumentException("Invalid causal event timestamps.");
        var scored=all.Where(p=>Window(p.ClipId,p.DecisionMs)>=0).ToArray();
        var pairs=new List<(int Prediction,int Truth,double Distance)>();
        for(var p=0;p<scored.Length;p++)
        {
            var nearest=Enumerable.Range(0,targets.Length).Where(i=>targets[i].ClipId==scored[p].ClipId)
                .OrderBy(i=>Math.Abs(targets[i].TimestampMs-scored[p].DecisionMs)).ThenBy(i=>targets[i].TimestampMs).FirstOrDefault(-1);
            if(nearest<0) continue;
            var distance=Math.Abs(targets[nearest].TimestampMs-scored[p].DecisionMs);
            if(distance<=toleranceMs && Window(targets[nearest].ClipId,targets[nearest].TimestampMs)==Window(scored[p].ClipId,scored[p].DecisionMs)) pairs.Add((p,nearest,distance));
        }
        var used=new HashSet<int>(); var matches=new List<EvaluationMatch>();
        foreach(var pair in pairs.OrderBy(p=>p.Distance).ThenBy(p=>scored[p.Prediction].DecisionMs))
        {
            if(!used.Add(pair.Truth)) continue;
            var p=scored[pair.Prediction]; var t=targets[pair.Truth]; matches.Add(new(p.ClipId,t.TimestampMs,p.DecisionMs,p.OnsetMs,p.DecisionMs-t.TimestampMs));
        }
        var tp=matches.Count; var fp=scored.Length-tp; var n=targets.Count(t=>Window(t.ClipId,t.TimestampMs)>=0); var fn=n-tp;
        var duration=windows.Sum(w=>w.EndMs-w.StartMs); var latency=matches.Select(m=>m.LatencyMs).ToArray();
        double? precision=scored.Length==0?null:(double)tp/scored.Length;
        return new(scored.Length,all.Length-scored.Length,n,tp,fp,fn,duration,precision,n==0?null:(double)tp/n,n==0?null:2d*tp/(2*tp+fp+fn),precision is null?null:1-precision,
            duration==0?null:fp*60000d/duration,latency.Count(t=>t<0),latency,BaselineEvaluation.Percentile(latency,.5),BaselineEvaluation.Percentile(latency,.9),BaselineEvaluation.Percentile(latency,.95),latency.Length==0?null:latency.Max(),matches.ToArray());
    }
}
