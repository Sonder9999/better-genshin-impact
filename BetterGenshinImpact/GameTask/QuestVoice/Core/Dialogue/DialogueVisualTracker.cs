using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public enum SubtitleVisualState { NoText, TextEntering, TextStable, TextRevealing, TextChanging, TextLeaving, Unknown }
public sealed record PtsBracket(double? BeforeMs, double? AfterMs, bool Trusted)
{
    public double? UncertaintyMs => Trusted && BeforeMs.HasValue && AfterMs.HasValue ? Math.Max(0, AfterMs.Value - BeforeMs.Value) : null;
}
public sealed record DialogueROIFrame(int FrameIndex, SourceFrameTime Time, VisualFrame NameContext, VisualFrame Body, int Quality);
public sealed class SubtitleVisualInstance
{
    public required long InstanceId { get; init; }
    public required PtsBracket StartBracket { get; init; }
    public PtsBracket? EndBracket { get; internal set; }
    public SourcePtsTrust PtsTrust { get; internal set; }
    public int ObservedFrames { get; internal set; }
    public int OcrCalls { get; set; }
    public bool HasOcrText { get; set; }
    public SubtitleVisualState State { get; internal set; }
    public string? CloseReason { get; internal set; }
    public SubtitleMaskComparer.Difference MaskChangeEvidence { get; internal set; }
    public SubtitleMaskComparer.Difference SpeakerMaskEvidence { get; internal set; }
    private readonly List<DialogueROIFrame> frames = [];
    private readonly List<(int Index, double? SourceMs, int Quality)> frameInfo = [];
    public IReadOnlyList<(int Index, double? SourceMs, int Quality)> RepresentativeInfo => frameInfo;
    public double? FirstFramePtsMs => frameInfo.Count > 0 ? frameInfo[0].SourceMs : null;
    public double? BestFramePtsMs => frameInfo.Count > 0 ? frameInfo.OrderByDescending(f => f.Quality).First().SourceMs : null;
    public double? MiddleFramePtsMs => frameInfo.Count == 3 ? frameInfo.OrderBy(f => f.Index).ElementAt(1).SourceMs : null;
    public IReadOnlyList<DialogueROIFrame> RepresentativeFrames => frames;
    public void KeepRepresentative(DialogueROIFrame frame)
    {
        if (frames.Any(f => f.FrameIndex == frame.FrameIndex) || frames.Count >= 3) return;
        frames.Add(frame);
        frameInfo.Add((frame.FrameIndex, frame.Time.RawSourceMs, frame.Quality));
    }
    public void ReleaseFrames() => frames.Clear();
}
public sealed record VisualTrackingResult(SubtitleVisualInstance? Active, SubtitleVisualInstance? Closed, bool NewInstance,
    SubtitleVisualState State, bool Present, bool OcrProgress);

/// <summary>Continuous causal observations only. Blank grace compares against the last nonblank mask.</summary>
public sealed class DialogueVisualTracker(int blankFrames = 1, double blankGraceMs = 450)
{
    private DialogueVisualFeatures? previousText;
    private SourceFrameTime? lastText, lastBlank, firstBlank;
    private int blanks;
    private long nextId;
    public SubtitleVisualInstance? Active { get; private set; }
    public long Opened { get; private set; }
    public long Closed { get; private set; }
    public long Unknown { get; private set; }
    public long Transitions { get; private set; }
    public void Reset()
    {
        Active?.ReleaseFrames(); Active = null; previousText = null;
        lastText = lastBlank = firstBlank = null; blanks = 0;
    }
    public SubtitleVisualInstance? Flush(string reason)
    {
        var closed = Close(null, reason); Reset(); return closed;
    }
    public VisualTrackingResult Observe(int frameIndex, SourceFrameTime time, DialogueVisualFeatures features, bool uiVisible)
    {
        if (!time.IsTrusted || !features.Valid)
        {
            Unknown++;
            var ended = Close(new(null, null, false), "untrusted_pts_or_roi"); Reset();
            return new(null, ended, false, SubtitleVisualState.Unknown, features.Present, false);
        }
        var present = uiVisible && features.Present;
        SubtitleVisualInstance? closed = null;
        if (!present)
        {
            lastBlank = time;
            if (Active is null) return new(null, null, false, SubtitleVisualState.NoText, false, false);
            firstBlank ??= time; blanks++;
            Active.State = SubtitleVisualState.TextLeaving;
            // Never finish on the first missing frame, even if sampling gaps are large.
            if (blanks > Math.Max(1, blankFrames))
                closed = Close(Bracket(lastText, firstBlank), "blank_grace_expired");
            return new(Active, closed, false, Active?.State ?? SubtitleVisualState.NoText, false, false);
        }
        var difference = previousText is null ? default : SubtitleMaskComparer.Compare(previousText.Body.Pixels, features.Body.Pixels);
        var nameDifference = previousText is null ? default : SubtitleMaskComparer.Compare(previousText.Name.Pixels, features.Name.Pixels);
        var erasing = previousText is not null && difference.Removed >= Math.Max(3, difference.PreviousCount * .30) &&
            difference.Added < Math.Max(3, difference.PreviousCount * .10);
        var rewrite = previousText is not null && (previousText.Body.Width != features.Body.Width || previousText.Body.Height != features.Body.Height ||
            !erasing && difference.Containment < .60 && difference.Jaccard < .60 && difference.Removed >= Math.Max(3, difference.PreviousCount * .30));
        if (Active is not null && (rewrite || firstBlank is not null && time.SchedulingMs - firstBlank.Value.SchedulingMs > blankGraceMs))
        {
            closed = Close(Bracket(lastText, firstBlank ?? time), rewrite ? "body_reset" : "blank_gap");
            Transitions++;
        }
        var opened = Active is null;
        if (opened)
        {
            Active = new() { InstanceId = ++nextId, StartBracket = Bracket(rewrite ? lastText : lastBlank, time),
                PtsTrust = time.Trust, State = rewrite ? SubtitleVisualState.TextChanging : SubtitleVisualState.TextEntering };
            Opened++;
        }
        else Active!.State = erasing ? SubtitleVisualState.TextLeaving : difference.Containment >= .85 && difference.Added >= Math.Max(3, difference.PreviousCount * .12)
            ? SubtitleVisualState.TextRevealing : SubtitleVisualState.TextStable;
        Active!.ObservedFrames++;
        Active.MaskChangeEvidence = difference; Active.SpeakerMaskEvidence = nameDifference;
        var progress = opened || difference.Added + difference.Removed >= Math.Max(3, difference.PreviousCount * .10) ||
            nameDifference.Added + nameDifference.Removed >= Math.Max(3, nameDifference.PreviousCount * .20);
        if (!erasing) previousText = features;
        lastText = time; blanks = 0; firstBlank = null;
        return new(Active, closed, opened, Active.State, true, progress);
    }
    private SubtitleVisualInstance? Close(PtsBracket? end, string reason)
    {
        var result = Active;
        if (result is null) return null;
        result.EndBracket = end ?? new(lastText?.RawSourceMs, null, false);
        result.CloseReason = reason; result.ReleaseFrames(); Active = null; Closed++;
        return result;
    }
    private static PtsBracket Bracket(SourceFrameTime? before, SourceFrameTime? after) =>
        new(before?.RawSourceMs, after?.RawSourceMs, before?.IsTrusted == true && after?.IsTrusted == true);
}
