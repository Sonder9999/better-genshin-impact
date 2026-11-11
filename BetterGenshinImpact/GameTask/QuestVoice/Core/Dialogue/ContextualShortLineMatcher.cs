using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

/// <summary>Exact short identities require an observed speaker and a constrained local scope.</summary>
public sealed class ContextualShortLineMatcher
{
    public TextMatch Match(string text,string? observedSpeaker,IReadOnlyList<LineCandidate> localCandidates)
    {
        var normalized=SemanticMatcher.Normalize(text);
        if(normalized.Length>4)return new(null,null,0,0,"not_short_text");
        if(string.IsNullOrWhiteSpace(observedSpeaker))return new(null,null,0,0,"speaker_unobserved");
        if(normalized.Length==0 && (string.IsNullOrWhiteSpace(text) || !text.Any(c=>c is '…' or '.' or '。')
            || text.Any(c=>!char.IsWhiteSpace(c) && !"….。".Contains(c))))return new(null,null,0,0,"not_silent_text");
        var role=SemanticMatcher.Normalize(observedSpeaker);
        var exact=localCandidates.Where(x=>SemanticMatcher.Normalize(x.Role)==role && SemanticMatcher.Normalize(x.Text)==normalized)
            .DistinctBy(x=>x.Key).ToArray();
        return exact.Length==1 ? new(exact[0].Key,exact[0].Id,1,1,normalized.Length==0?"contextual_silent":"contextual_short")
            :new(null,null,exact.Length>0?1:0,0,exact.Length>1?"ambiguous_short_text":"no_local_short_match");
    }
}
