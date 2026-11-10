namespace QuestVoice.Core;

public sealed record ChoiceContinuationOptions
{
    public int MaxEdges {get;init;}=12;
    public int MaxNodes {get;init;}=64;
    internal void Validate()
    {
        if(MaxEdges is <1 or >64 || MaxNodes is <1 or >256)throw new ArgumentException("Invalid choice continuation limits");
    }
}

internal sealed record CommonChoiceCandidate(QuestLine Line,IReadOnlyList<IReadOnlyList<string>> Paths);

/// <summary>Observes common downstream dialogue without guessing which option was selected.</summary>
internal sealed class ChoiceContinuation
{
    private readonly ChoiceContinuationOptions options;
    private readonly SemanticMatcher matcher=new();
    private string? choice,candidate;
    private double lastSource=double.NegativeInfinity,lastObservation=double.NegativeInfinity;
    private double sourceFloor=double.NegativeInfinity;
    public ChoiceContinuation(ChoiceContinuationOptions options){options.Validate();this.options=options;}
    private void ClearCandidate(){choice=candidate=null;lastSource=lastObservation=double.NegativeInfinity;}
    public void Reset(){ClearCandidate();sourceFloor=double.NegativeInfinity;}
    public void Interrupt(double sourceMs){ClearCandidate();sourceFloor=Math.Max(sourceFloor,sourceMs);}
    private void RejectSemantic(FusionObservation o)
    {
        // A returned OCR result describes its source frame, not the frame at which it
        // arrived. Advancing to arrival time would permanently reject a delayed stream.
        var source=o.Semantic.Where(e=>e.Method=="D").Select(e=>e.SourceMs).DefaultIfEmpty(o.TimestampMs).Max();
        Interrupt(source);
    }
    public bool TryConfirm(QuestSession session,FusionObservation o,string? resolvedKey,double earliestSource,double window,
        out string reason,out IReadOnlyList<IReadOnlyList<string>>? paths,out double confirmedSource)
    {
        paths=null;confirmedSource=0;
        var candidates=session.CommonChoiceCandidates(options.MaxEdges,options.MaxNodes);
        var target=candidates.SingleOrDefault(c=>c.Line.NodeKey==resolvedKey);
        if(target is null){RejectSemantic(o);reason="choice_requires_common_successor";return false;}
        var evidence=o.Semantic.Where(e=>e.Method=="D" && e.SupportsStrongEvidence && e.Text is not null &&
            SemanticMatcher.Normalize(e.Text).Length>=6 && e.SourceMs>Math.Max(earliestSource,sourceFloor) && o.TimestampMs-e.SourceMs<=window &&
            e.Speaker is not null && SemanticMatcher.Normalize(e.Speaker)==SemanticMatcher.Normalize(target.Line.Role) &&
            (o.Language is null || target.Line.Language==o.Language)).OrderByDescending(e=>e.SourceMs).FirstOrDefault();
        if(evidence is null || matcher.Match(evidence.Text!,candidates.Select(c=>c.Line.ToCandidate()).ToArray()).Key!=target.Line.NodeKey)
        {RejectSemantic(o);reason="choice_requires_observed_text_and_speaker";return false;}
        if(choice!=session.CurrentKey || candidate!=target.Line.NodeKey || o.TimestampMs-lastObservation>600)
        {
            choice=session.CurrentKey;candidate=target.Line.NodeKey;lastSource=evidence.SourceMs;lastObservation=o.TimestampMs;
            reason="choice_continuation_pending_evidence";return false;
        }
        if(evidence.SourceMs<=lastSource){reason="choice_repeated_source_frame";return false;}
        lastSource=evidence.SourceMs;lastObservation=o.TimestampMs;
        var decision=session.ResumeCommonChoiceNode(candidate,options.MaxEdges,options.MaxNodes,out paths);
        if(decision.Accepted)confirmedSource=evidence.SourceMs;
        reason=decision.Reason;Reset();return decision.Accepted;
    }
}
