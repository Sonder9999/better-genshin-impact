namespace QuestVoice.Core;

public sealed record RecoveryOptions
{
    public int MaxEdges { get; init; }=12;
    public int MaxCandidates { get; init; }=64;
    public double GraceMs { get; init; }=1200;
    public int Confirmations { get; init; }=2;
    internal void Validate()
    {
        if(MaxEdges is <1 or >64 || MaxCandidates is <1 or >256 || !double.IsFinite(GraceMs) || GraceMs<0 || Confirmations is <2 or >8)
            throw new ArgumentException("Invalid bounded recovery policy");
    }
}

/// <summary>Repeated real long-text evidence may relocate only along selected same-task paths.</summary>
internal sealed class QuestRelocalizer
{
    private readonly RecoveryOptions options;
    private readonly SemanticMatcher matcher=new();
    private string? candidate,root;
    private double lastSource=double.NegativeInfinity,lastObservation=double.NegativeInfinity;
    private int count;
    public QuestRelocalizer(RecoveryOptions options){options.Validate();this.options=options;}
    public void Reset(){candidate=root=null;count=0;lastSource=lastObservation=double.NegativeInfinity;}
    public bool TryConfirm(QuestSession session,FusionObservation observation,string desiredKey,double lastReliableMs,out string reason)
    {
        var lines=session.RecoveryCandidates(options.MaxEdges,options.MaxCandidates);
        var target=lines.SingleOrDefault(x=>x.NodeKey==desiredKey);
        if(target is null){Reset();reason="recovery_outside_task_or_unselected_branch";return false;}
        var evidence=observation.Semantic.Where(x=>x.Method=="D" && x.SupportsStrongEvidence && x.Text is not null &&
            SemanticMatcher.Normalize(x.Text).Length>=6 && !string.IsNullOrWhiteSpace(x.Speaker) &&
            SemanticMatcher.Normalize(x.Speaker)==SemanticMatcher.Normalize(target.Role))
            .OrderByDescending(x=>x.SourceMs).FirstOrDefault();
        if(evidence is null){Reset();reason="recovery_requires_observed_long_text_and_speaker";return false;}
        var match=matcher.Match(evidence.Text!,lines.Select(x=>x.ToCandidate()).ToArray());
        if(match.Key!=desiredKey){Reset();reason="recovery_ambiguous_or_unreliable_text";return false;}
        if(candidate!=desiredKey || root!=session.CurrentKey || observation.TimestampMs-lastObservation>600)
        {candidate=desiredKey;root=session.CurrentKey;count=0;lastSource=double.NegativeInfinity;}
        lastObservation=observation.TimestampMs;
        if(evidence.SourceMs<=lastSource){reason="recovery_repeated_source_frame";return false;}
        lastSource=evidence.SourceMs;count++;
        if(observation.TimestampMs-lastReliableMs<options.GraceMs){reason="recovery_grace_period";return false;}
        if(count<options.Confirmations){reason="recovery_pending_evidence";return false;}
        var decision=session.RelocateForward(desiredKey,options.MaxEdges,options.MaxCandidates);
        reason=decision.Accepted?"same_task_relocalized":decision.Reason;
        if(decision.Accepted)Reset();return decision.Accepted;
    }
}
