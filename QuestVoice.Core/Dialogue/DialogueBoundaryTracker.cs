namespace QuestVoice.Core;

public sealed record DialogueBoundary(string Id,double SourceMs,double AvailableMs,string Reason);

/// <summary>Tracks visible line endings independently of quest identity. It never chooses a node.</summary>
public sealed class DialogueBoundaryTracker
{
    private string body="",speaker="",candidate="",candidateSpeaker="";
    private double lastTime=double.NegativeInfinity,candidateAt;
    private long sequence;
    public DialogueBoundary? Observe(double timeMs,string rawBody,string observedSpeaker,bool visible,bool choice,
        PtsBracket? visualStart = null, bool ptsTrusted = true, double? observedSourceMs = null)
    {
        if (!ptsTrusted) { Reset(); return null; }
        if(!double.IsFinite(timeMs) || timeMs<0 || timeMs<=lastTime)throw new ArgumentException("Boundary observations require increasing PTS");
        var gap=timeMs-lastTime;lastTime=timeMs;
        var textSourceMs = observedSourceMs ?? timeMs;
        if(!visible){body=speaker=candidate=candidateSpeaker="";return null;}
        var text=SemanticMatcher.Normalize(rawBody);var role=SemanticMatcher.Normalize(observedSpeaker);
        if(text.Length==0 && !string.IsNullOrWhiteSpace(rawBody) && rawBody.Any(c=>c is '…' or '.')
            && rawBody.All(c=>char.IsWhiteSpace(c) || "….。".Contains(c)))text="…";
        if(choice || text.Length==0){candidate=candidateSpeaker="";return null;}
        if (visualStart is { Trusted: true, BeforeMs: not null, AfterMs: not null } && visualStart.AfterMs <= timeMs &&
            body.Length > 0 && !SameLine(body, text))
        {
            body = text; if (role.Length > 0) speaker = role; candidate = candidateSpeaker = "";
            return new($"line-{++sequence}", visualStart.AfterMs.Value, timeMs, "visual_bracket_and_observed_body_replaced");
        }
        if(body.Length==0)
        {
            body=text;speaker=role;return new($"line-{++sequence}",textSourceMs,timeMs,"initial_visible_line");
        }
        if(SameLine(body,text))
        {
            if(text.Length>=body.Length)body=text;
            if(role.Length>0)speaker=role;
            candidate=candidateSpeaker="";return null;
        }
        var changedRole=role.Length>0 && speaker.Length>0 && role!=speaker;
        var stable=gap<=450 && textSourceMs > candidateAt && candidate.Length>0 && SameLine(candidate,text) &&
            (candidateSpeaker.Length==0 || role.Length==0 || role==candidateSpeaker);
        if(!changedRole && !stable)
        {candidate=text;candidateSpeaker=role;candidateAt=textSourceMs;return null;}
        var source=stable?candidateAt:textSourceMs;
        body=text;if(role.Length>0)speaker=role;candidate=candidateSpeaker="";
        return new($"line-{++sequence}",source,timeMs,changedRole?"observed_speaker_and_body_changed":"stable_body_replaced");
    }
    public void Reset() { body=speaker=candidate=candidateSpeaker="";lastTime=double.NegativeInfinity; }
    private static bool SameLine(string a,string b)
    {
        if(a==b || a.StartsWith(b,StringComparison.Ordinal) || b.StartsWith(a,StringComparison.Ordinal))return true;
        if (Math.Min(a.Length,b.Length) < 6) return false;
        if(Math.Min(a.Length,b.Length)>=6 && (a.Contains(b,StringComparison.Ordinal) || b.Contains(a,StringComparison.Ordinal)))return true;
        var prefix=0;while(prefix<Math.Min(a.Length,b.Length) && a[prefix]==b[prefix])prefix++;
        var suffix=0;while(suffix<Math.Min(a.Length,b.Length)-prefix && a[^(suffix+1)]==b[^(suffix+1)])suffix++;
        return (prefix+suffix)/(double)Math.Max(a.Length,b.Length)>=.72;
    }
}
