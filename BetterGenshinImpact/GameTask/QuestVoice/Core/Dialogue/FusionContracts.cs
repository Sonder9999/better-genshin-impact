using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public enum EyeState { Hidden, Translucent, Highlighted }
public enum OptionEvidenceState { None, Suspected, Confirmed }
public sealed record UIGateResult(double TimestampSeconds, EyeState Eye, double EyeScore, int OptionRows,
    bool DialogueVisible, bool ChoiceWaiting, bool TargetKnown, bool AllowVisualCuts, bool AllowTargetAudio, bool BlackTransition,
    int CandidateOptionRows = 0, OptionEvidenceState OptionEvidence = OptionEvidenceState.None);

public enum FusionMode { FullFusion, NoUIGate, NoVision, NoQuestGraph }
public enum FusionVisualKind { PossibleClear, NewLineStarted, BoundaryConfirmed }
public enum FusionState { NoDialogue, DialogueActive, Transitioning, OptionWait, UnknownContext, Recovering, Finished }
public enum FusionEventKind { CutTriggered, AudioPlayCommand, AudioPrepareCommand, AudioStopCommand, OptionWait, DialogueEnd, GateConflict, AudioBoundaryStopCommand, ContextRecovered, ChoiceContinuationConfirmed }

public sealed record FusionOptions(FusionMode Mode = FusionMode.FullFusion, int PrefetchDepth = 2,
    double EvidenceWindowMs = 750, double CoalescingMs = 50, RecoveryOptions? Recovery = null,
    ChoiceContinuationOptions? ChoiceContinuation = null);
public sealed record FusionVisualSignal(string Method, FusionVisualKind Kind, string BoundaryId,
    double SourceMs, double AvailableMs, double Score);
public sealed record FusionSemanticEvidence(string Method, double SourceMs, double AvailableMs,
    string? Text, string? NodeKey, double? Confidence, double Score = 1, double Margin = 1, string? Speaker = null)
{
    // Windows OCR supplies no confidence. These are existing text eligibility gates, not probabilities.
    public bool SupportsIdentity => Confidence is { } value ? value >= .55 : Method == "D" && !string.IsNullOrWhiteSpace(Text);
    public bool SupportsStrongEvidence => Confidence is { } value ? value >= .9 : Method == "D" && Text is not null && SemanticMatcher.Normalize(Text).Length >= 4;
}
// TimestampMs always identifies the original video frame. AvailableAtMs may be later
// when OCR/decoding completes; it is not a replacement for the frame's source PTS.
public sealed record FusionObservation(string ClipId, int FrameIndex, double TimestampMs, UIGateResult Gate,
    IReadOnlyList<FusionVisualSignal> Visual, IReadOnlyList<FusionSemanticEvidence> Semantic,
    string? Language = null, int? SelectedOption = null, double? AvailableAtMs = null);
public sealed record FusionEvent(string EventId, string DecisionId, long SessionId, string ClipId, FusionEventKind Kind,
    double TimestampMs, double SourceMs, string? NodeKey, string? LineId, string? AudioPath, string Reason,
    IReadOnlyList<string>? RecoveryPath = null, IReadOnlyList<IReadOnlyList<string>>? ChoicePaths = null);
public sealed record FusionResult(FusionState State, string? CurrentNodeKey, IReadOnlyList<QuestLine> Prefetch,
    IReadOnlyList<FusionEvent> Events, string Reason);
