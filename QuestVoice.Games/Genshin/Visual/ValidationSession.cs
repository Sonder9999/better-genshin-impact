using QuestVoice.Core;
using System.Diagnostics;
namespace QuestVoice.Games.Genshin;

public sealed record ValidationFrameResult(DialogueSceneObservation Semantic,FusionResult Fusion,double[] BaselineMs,double SemanticTotalMs,double FusionMs);
public sealed record ValidationIdentity(string ClipId,double TimestampMs,string? NodeId,string? NodeKey,string Reason);

public sealed class ValidationSession
{
    private readonly string clip; private readonly DialogueSceneRecognizer semantic; private readonly FusionEngine fusion;
    private readonly ValidationSignals adapter=new(); private readonly SimulatedPlayer player;
    private readonly IVisualBaseline[] detectors=[new BrightPixelDetector(),new EdgeComponentDetector(),new SsimDisplacementDetector()];
    private double lastTime=double.NegativeInfinity; private int lastIndex=-1; private bool? visible,choice;
    public static readonly string[] Methods=["A","B","C","D","E","F","Fusion"];
    public int FrameCount { get; private set; }
    public Dictionary<string,List<double>> Timings { get; }=Methods.ToDictionary(m=>m,_=>new List<double>());
    public Dictionary<string,List<ValidationEvent>> Events { get; }=Methods.ToDictionary(m=>m,_=>new List<ValidationEvent>());
    public Dictionary<string,List<ValidationIdentity>> Identities { get; }=new(){["D"]=[],["E"]=[]};
    public List<FusionEvent> Decisions { get; }=[];
    public IReadOnlyList<SimulatedPlaybackEvent> Playback=>player.Trace;
    public int OcrCalls { get; private set; }
    public int ChoiceFrames { get; private set; }
    public int ChoiceStarts { get; private set; }
    public ValidationSession(string clip,QuestGraph graph,IReadOnlyList<LineCandidate> candidates,IReadOnlyList<CandidateLink> links,GlyphOCR ocr,GlyphLineMatcher templates,IReadOnlyList<SimulatedAudioClip> audio)
    {
        _=ValidationEvaluation.Bounds(clip); this.clip=clip; semantic=new(candidates,links,ocr,templates); fusion=new(graph,new(),audio); player=new(audio);
    }
    public ValidationFrameResult Observe(int index,VisualFrame visual,VisualFrame full)
    {
        var time=full.TimestampSeconds*1000; var (start,end)=ValidationEvaluation.Bounds(clip);
        if(index<=lastIndex || visual.Width!=336 || visual.Height!=98 || full.Width!=1280 || full.Height!=720 || visual.TimestampSeconds!=full.TimestampSeconds || time<start || time>=end || time<=lastTime) throw new ArgumentException("Expected matching increasing original validation frames.");
        var visualSignals=new List<FusionVisualSignal>(); var baselineMs=new double[3];
        for(var i=0;i<detectors.Length;i++)
        {
            var t=Stopwatch.GetTimestamp(); var hit=detectors[i].Observe(visual); baselineMs[i]=Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            Timings[detectors[i].Method].Add(baselineMs[i]);
            if(hit is null) continue;
            Events[hit.Method].Add(new(clip,hit.Method=="C"?"structure_candidate":"old_text_disappeared",time,hit.OnsetSeconds*1000,null,"frozen visual transition baseline"));
            visualSignals.Add(new(hit.Method,FusionVisualKind.PossibleClear,$"validation/{hit.Method}/{clip}/{index}",hit.OnsetSeconds*1000,time,hit.Score));
        }
        var clock=Stopwatch.GetTimestamp(); var o=semantic.Observe(full); var total=Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        Timings["D"].Add(Math.Max(0,total-o.GateMs-o.TemplateMs)); Timings["E"].Add(o.GlyphMs+o.TemplateMs); Timings["F"].Add(o.GateMs);
        if(o.Ocr is not null) OcrCalls++;
        AddSemantic("D",o.TextMatch,o.TextEvent,o.Ocr?.Confidence??0,time); AddSemantic("E",o.TemplateMatch,o.TemplateEvent,1,time);
        if(visible.HasValue && visible!=o.Gate.DialogueVisible) Events["F"].Add(new(clip,o.Gate.DialogueVisible?"dialogue_ui_enter":"dialogue_ui_exit",time,time,null,"eye anchor transition"));
        if(choice.HasValue && choice!=o.Gate.ChoiceWaiting) Events["F"].Add(new(clip,o.Gate.ChoiceWaiting?"choice_shown":"choice_resolved",time,time,null,"option rows transition"));
        visible=o.Gate.DialogueVisible; choice=o.Gate.ChoiceWaiting;
        var evidence=new List<FusionSemanticEvidence>();
        if(o.TextMatch is not null && o.Ocr is not null) evidence.Add(new("D",time,time,o.Ocr.Text[..Math.Min(12,o.Ocr.Text.Length)],null,o.Ocr.Confidence));
        if(o.TemplateMatch is { Key: not null } match) evidence.Add(new("E",time,time,null,match.Key,1,match.Score,match.Margin));
        var signals=adapter.Append(clip,index,time,o.GlyphCount,visualSignals);
        var playbackBefore=player.Trace.Count;
        clock=Stopwatch.GetTimestamp(); var r=fusion.Observe(new(clip,index,time,o.Gate,signals,evidence,"jpn")); player.Consume(r.Events,time);
        if(o.Gate.ChoiceWaiting)
        {
            ChoiceFrames++; ChoiceStarts+=player.Trace.Skip(playbackBefore).Count(p=>p.Kind==SimulatedPlaybackKind.Started);
        }
        var fusionMs=Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        Timings["Fusion"].Add(total+baselineMs.Sum()+fusionMs); Decisions.AddRange(r.Events);
        foreach(var e in r.Events)
            if(e.Kind is FusionEventKind.CutTriggered or FusionEventKind.DialogueEnd or FusionEventKind.AudioPlayCommand)
                Events["Fusion"].Add(new(clip,e.Kind==FusionEventKind.CutTriggered?"new_text_first_visible":e.Kind==FusionEventKind.DialogueEnd?"dialogue_ui_exit":"audio_play_command",e.TimestampMs,e.SourceMs,e.LineId,e.Reason));
        lastTime=time; lastIndex=index; FrameCount++;
        return new(o,r,baselineMs,total,fusionMs);
    }
    public void Complete() => player.AdvanceTo(ValidationEvaluation.Bounds(clip).End);
    private void AddSemantic(string method,TextMatch? match,LineMatchEvent? hit,double confidence,double time)
    {
        if(match is not null)
        {
            var accepted=VisualRecognitionEvaluation.AcceptedIdentityId(match,confidence);
            Identities[method].Add(new(clip,time,accepted,accepted is null?null:match.Key,match.Reason));
        }
        if(hit is not null) Events[method].Add(new(clip,"new_text_first_visible",hit.DecisionSeconds*1000,hit.RequestSeconds*1000,hit.Id,"node confirmation; may confirm inherited text"));
    }
}
