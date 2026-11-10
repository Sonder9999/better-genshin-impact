namespace QuestVoice.Core;

public sealed class FusionEngine
{
    private readonly QuestGraph graph;
    private readonly QuestSession session;
    private readonly FusionOptions options;
    private readonly Dictionary<string, SimulatedAudioClip> audioMetadata;
    private readonly SemanticMatcher matcher = new();
    private readonly QuestRelocalizer? relocalizer;
    private readonly ChoiceContinuation? choiceContinuation;
    private double lastReliableMs=double.NegativeInfinity;
    private readonly HashSet<string> clearIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> firstIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> boundaryIds = new(StringComparer.Ordinal);
    private bool awaitingBoundaryIdentity, awaitingFreshFirst;
    private readonly List<FusionEvent> contextCommands = [];
    private FusionVisualSignal? pendingFirst;
    private FusionState state = FusionState.NoDialogue;
    private string? clipId, lastPlayedKey;
    private double lastTime = double.NegativeInfinity, lastCutSource = double.NegativeInfinity;
    private double lastCutTime = double.NegativeInfinity, closedAt = double.NegativeInfinity;
    private bool clearOpen, recovering, dialogueSeen;
    private long epoch = 1, eventSequence, decisionSequence;
    private bool UseUi => options.Mode != FusionMode.NoUIGate;
    private bool UseVision => options.Mode != FusionMode.NoVision;
    private bool UseGraph => options.Mode != FusionMode.NoQuestGraph;

    public FusionEngine(QuestGraph graph, FusionOptions? options = null, IReadOnlyList<SimulatedAudioClip>? audioClips = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        this.graph = graph;
        this.options = options ?? new();
        relocalizer=this.options.Recovery is null?null:new QuestRelocalizer(this.options.Recovery);
        choiceContinuation=this.options.ChoiceContinuation is null?null:new ChoiceContinuation(this.options.ChoiceContinuation);
        if (!Enum.IsDefined(this.options.Mode) || this.options.PrefetchDepth is < 1 or > 3 ||
            !double.IsFinite(this.options.EvidenceWindowMs) || this.options.EvidenceWindowMs <= 0 ||
            !double.IsFinite(this.options.CoalescingMs) || this.options.CoalescingMs < 0)
            throw new ArgumentException("Invalid fusion settings.");
        var audio = audioClips ?? [];
        if (audio.Any(c => c is null || !double.IsFinite(c.DurationMs) || c.DurationMs <= 0 || string.IsNullOrWhiteSpace(c.AudioPath) ||
            graph.LineIndex.ForNode(c.NodeKey).SingleOrDefault(l => l.OptionNumber is null)?.Id != c.LineId) ||
            audio.GroupBy(c => c.NodeKey, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new ArgumentException("Audio metadata must bind a unique known spoken node.");
        audioMetadata = audio.ToDictionary(c => c.NodeKey, StringComparer.Ordinal);
        session = graph.CreateSession();
    }
    public QuestPlaybackState QuestState => session.State;
    public string? CurrentKey => session.CurrentKey;
    public string? LastPlayedKey => lastPlayedKey;
    public double? PendingFirstSourceMs => pendingFirst?.SourceMs;
    public double LastCutSourceMs => lastCutSource;

    /// <summary>Explicitly confirmed external identity; it is never inferred from UI or a WAV list.</summary>
    public QuestDecision ConfirmContext(string nodeKey)
    {
        var previousContext = lastTime >= 0 && (session.CurrentKey is not null || dialogueSeen || lastPlayedKey is not null);
        var decision = session.StartAt(nodeKey);
        if (decision.Accepted)
        {
            epoch++; closedAt = lastTime; lastCutSource = lastTime; lastCutTime = double.NegativeInfinity;
            pendingFirst = null; lastPlayedKey = null; clearOpen = recovering = awaitingBoundaryIdentity = awaitingFreshFirst = false;
            boundaryIds.Clear();
            relocalizer?.Reset();choiceContinuation?.Reset();lastReliableMs=lastTime;
            clearIds.Clear(); firstIds.Clear(); state = FusionState.DialogueActive;
            if (previousContext && clipId is not null)
            {
                var line = CurrentLine();
                // Flush this control before advancing the next observation's player clock,
                // preserving the known confirmation PTS even across event-free frames.
                contextCommands.Add(new($"{epoch}:e{++eventSequence}", DecisionId(), epoch, clipId, FusionEventKind.AudioStopCommand,
                    lastTime, lastTime, line?.NodeKey, line?.Id, line?.AudioPath, "explicit_context_replaced"));
            }
        }
        return decision;
    }

    public FusionResult Observe(FusionObservation observation)
    {
        Validate(observation); // Validate the complete frame before committing any state or clock.
        lastTime = observation.TimestampMs; clipId ??= observation.ClipId;
        var events = new List<FusionEvent>(contextCommands);
        contextCommands.Clear();
        if (pendingFirst is not null && lastTime - pendingFirst.SourceMs > options.EvidenceWindowMs)
        { pendingFirst = null; awaitingFreshFirst = true; }

        if (UseUi && (!observation.Gate.DialogueVisible || observation.Gate.Eye == EyeState.Hidden))
        {
            if (dialogueSeen || session.CurrentKey is not null || pendingFirst is not null)
                End(observation, events, "dialogue_ui_exited");
            state = FusionState.NoDialogue;
            return Result(events, "no_dialogue_ui");
        }
        dialogueSeen = true;
        if (UseUi && observation.Gate.BlackTransition)
        {
            choiceContinuation?.Interrupt(lastTime);
            relocalizer?.Reset();
            pendingFirst = null;
            state = session.CurrentKey is null ? FusionState.UnknownContext : FusionState.DialogueActive;
            return Result(events, "black_transition_suppressed");
        }
        var optionConflict = UseUi && (observation.Gate.ChoiceWaiting || observation.Gate.OptionRows > 0)
            && StrongSuccessorConflictsWithOption(observation);
        if(optionConflict)
            Emit(events,observation,FusionEventKind.GateConflict,lastTime,null,"option_visual_semantic_conflict");
        if (UseUi && !optionConflict && (observation.Gate.ChoiceWaiting || observation.Gate.OptionRows > 0))
        {
            choiceContinuation?.Interrupt(lastTime);
            relocalizer?.Reset();
            EnterChoice();
            if (observation.SelectedOption is { } selected && UseGraph)
            {
                var selection = session.SelectOption(selected);
                if (selection.Accepted && session.State == QuestPlaybackState.Finished)
                { End(observation, events, "terminal_option"); return Result(events, "terminal_option"); }
            }
            if (state != FusionState.OptionWait)
                Emit(events, observation, FusionEventKind.OptionWait, lastTime, null, "ui_choice_waiting");
            state = FusionState.OptionWait; pendingFirst = null;
            return Result(events, "ui_choice_waiting");
        }
        if (UseUi && !optionConflict && !observation.Gate.AllowVisualCuts)
        {
            choiceContinuation?.Interrupt(lastTime);
            relocalizer?.Reset();
            pendingFirst = null;
            return Result(events, "ui_gate_rejected");
        }
        if (UseGraph && observation.SelectedOption is { } option)
        {
            var choice = session.SelectOption(option);
            if(choice.Accepted)choiceContinuation?.Reset();
            else choiceContinuation?.Interrupt(lastTime);
            if (choice.Accepted && session.State == QuestPlaybackState.Finished)
            { End(observation, events, "terminal_option"); return Result(events, "terminal_option"); }
            if (!choice.Accepted) return Result(events, choice.Reason);
        }
        string? continuedKey=null;double continuedSource=0;
        if (UseGraph && session.State == QuestPlaybackState.OptionWait)
        {
            relocalizer?.Reset();
            var continuationReason="graph_selection_required";
            var identity=ResolveSemantic(observation);
            var continued=false;IReadOnlyList<IReadOnlyList<string>>? paths=null;
            if(identity.Conflict){choiceContinuation?.Interrupt(lastTime);continuationReason="choice_semantic_modalities_disagree";}
            else if(choiceContinuation is not null)
                continued=choiceContinuation.TryConfirm(session,observation,identity.Key,Math.Max(closedAt,lastCutSource),options.EvidenceWindowMs,out continuationReason,out paths,out continuedSource);
            if(!continued)
            {
                if (state != FusionState.OptionWait)
                    Emit(events, observation, FusionEventKind.OptionWait, lastTime, null, continuationReason);
                state = FusionState.OptionWait; pendingFirst = null;
                return Result(events, continuationReason);
            }
            // This observation has independently proved the current identity. Old pre-choice
            // boundary/correction state cannot require that same identity to change again.
            awaitingBoundaryIdentity=awaitingFreshFirst=false;pendingFirst=null;clearOpen=false;recovering=false;
            continuedKey=session.CurrentKey;
            Emit(events,observation,FusionEventKind.ChoiceContinuationConfirmed,continuedSource,CurrentLine(),continuationReason,choicePaths:paths);
        }

        if(continuedKey is not null)
            pendingFirst=new("D",FusionVisualKind.NewLineStarted,$"choice/{continuedKey}/{continuedSource:R}",continuedSource,lastTime,1);
        else if (UseVision) ObserveVision(observation, events);
        if (!UseGraph)
        {
            if (pendingFirst is not null) Trigger(observation, events, null, "visual_only_anonymous_boundary");
            else state = clearOpen ? FusionState.Transitioning : FusionState.DialogueActive;
            return Result(events, "quest_graph_ablated");
        }

        var resolved = continuedKey is null?ResolveSemantic(observation):
            (Key:continuedKey,Conflict:false,Reason:"observed_common_choice_successor",SourceMs:continuedSource);
        if (resolved.Conflict) {relocalizer?.Reset();return Recover(observation, events, resolved.Reason);}
        if (resolved.Key is not null)
        {
            var before = session.CurrentKey;
            if(awaitingBoundaryIdentity && resolved.Key==before && continuedKey is null)
            {relocalizer?.Reset();state=FusionState.Transitioning;return Result(events,"await_boundary_identity");}
            var relocated=false;
            if (!ConfirmNode(resolved.Key, observation.Language))
            {
                var recoveryReason="semantic_outside_graph_constraints";
                var fresh=observation with {Semantic=observation.Semantic.Where(x=>x.SourceMs>closedAt && x.SourceMs>=lastCutSource
                    && lastTime-x.SourceMs<=options.EvidenceWindowMs).ToArray()};
                if(relocalizer is null || !relocalizer.TryConfirm(session,fresh,resolved.Key,lastReliableMs,out recoveryReason))
                    return Recover(observation, events, recoveryReason);
                relocated=true;
                Emit(events,observation,FusionEventKind.ContextRecovered,resolved.SourceMs,CurrentLine(),recoveryReason,recoveryPath:session.LastRecoveryPath);
            }
            lastReliableMs=lastTime;relocalizer?.Reset();
            recovering = false;
            if (session.State == QuestPlaybackState.OptionWait)
            {
                state = FusionState.OptionWait; pendingFirst = null;
                Emit(events, observation, FusionEventKind.OptionWait, resolved.SourceMs, null, "confirmed_choice_node");
                return Result(events, "confirmed_choice_node");
            }
            state = FusionState.DialogueActive;
            if (session.CurrentKey != before) clearOpen = false;
            if (UseVision && pendingFirst is null && before != session.CurrentKey && lastPlayedKey is not null && !awaitingBoundaryIdentity && !relocated)
            {
                // A returned identity correction invalidates the inferred sound immediately.
                // It does not invent a second onset or replay until fresh first characters.
                state = FusionState.Recovering;
                Emit(events, observation, FusionEventKind.AudioStopCommand, resolved.SourceMs, CurrentLine(), "semantic_identity_corrected_await_first");
                return Result(events, "semantic_identity_corrected_await_first");
            }
            if (lastPlayedKey != session.CurrentKey)
            {
                // Later identity evidence cannot replace an onset that already expired.
                // Keep the inherited-text bootstrap available only before such a boundary.
                if (UseVision && pendingFirst is null && awaitingFreshFirst)
                    return Result(events, "await_fresh_first_characters");
                // Semantic confirmation is the authoritative identity. If the line was already
                // on screen before the first visual onset (e.g. playback starts mid-line), use
                // the confirmation PTS itself as the causal first-character boundary. This is
                // still a validated OCR/graph decision, never filename or index adjacency.
                pendingFirst ??= new("D", FusionVisualKind.NewLineStarted,
                    $"semantic/{resolved.Key}/{resolved.SourceMs:R}", resolved.SourceMs, lastTime, 1);
                Trigger(observation, events, CurrentLine(),
                    UseVision ? "vision_semantic_graph_agreement" : "semantic_confirmation");
            }
            return Result(events, "semantic_confirmed");
        }
        relocalizer?.Reset();
        if (session.CurrentKey is null)
        { state = FusionState.UnknownContext; return Result(events, "target_identity_unknown"); }
        if (recovering)
        { state = FusionState.Recovering; return Result(events, "await_recovery_evidence"); }
        if(awaitingBoundaryIdentity)
        { state=FusionState.Transitioning;return Result(events,"await_boundary_identity"); }
        if (pendingFirst is null)
        {
            state = clearOpen ? FusionState.Transitioning : FusionState.DialogueActive;
            return Result(events, "await_first_characters");
        }
        if (lastPlayedKey != session.CurrentKey && !clearOpen && CurrentLine() is { } current)
        {
            Trigger(observation, events, current, "first_characters_confirmed_context");
            return Result(events, "first_characters_confirmed_context");
        }
        // Prefetch can traverse unselected choices. ConfirmLine additionally enforces a direct,
        // selected edge, so even converging choices cannot accidentally authorize playback.
        var next = session.Prefetch(1);
        if (next.Count == 1 && session.ConfirmLine(next[0].NodeKey).Accepted)
        {
            Trigger(observation, events, next[0], "first_characters_unique_successor");
            return Result(events, "first_characters_unique_successor");
        }
        state = FusionState.Transitioning;
        return Result(events, "await_unique_branch_or_semantic_confirmation");
    }

    private void ObserveVision(FusionObservation o, List<FusionEvent> events)
    {
        foreach(var boundary in o.Visual.Where(x=>x.Kind==FusionVisualKind.BoundaryConfirmed && FreshVisual(x)))
        {
            if(!boundaryIds.Add(boundary.BoundaryId) || boundary.SourceMs<=lastCutSource)continue;
            // A stable OCR typo is not a new presentation when authoritative long-text
            // matching still uniquely identifies the current line in its candidate scope.
            if(session.CurrentKey is not null && o.Semantic.Any(x=>x.Method=="D" && x.SupportsStrongEvidence && x.Text is not null
                && SemanticMatcher.Normalize(x.Text).Length>=6 && (x.Speaker is null || x.Speaker==CurrentLine()?.Role))
                && ResolveSemantic(o).Key==session.CurrentKey)continue;
            pendingFirst=null;clearOpen=false;
            if(lastPlayedKey is not null)
            {
                awaitingBoundaryIdentity=true;
                Emit(events,o,FusionEventKind.AudioBoundaryStopCommand,boundary.SourceMs,CurrentLine(),"confirmed_line_boundary");
            }
        }
        foreach (var signal in o.Visual.Where(s => FreshVisual(s) &&
            s.Kind == FusionVisualKind.PossibleClear))
        {
            if (!clearIds.Add(signal.BoundaryId)) continue;
            clearOpen = true;
            if (UseGraph) session.BeginTransition();
            var decision = DecisionId();
            foreach (var line in Prefetch())
                Emit(events, o, FusionEventKind.AudioPrepareCommand, signal.SourceMs, line, "possible_clear_prepare_only", decision);
        }
        // Even an onset delivered after its evidence window rules out inherited-text
        // startup. Ignore old epochs, consumed boundaries and untrusted signals.
        if (pendingFirst is null && o.Visual.Any(s => s.Kind == FusionVisualKind.NewLineStarted &&
            s.Score >= .5 && s.SourceMs > closedAt && lastTime - s.SourceMs > options.EvidenceWindowMs &&
            s.SourceMs > lastCutSource && !firstIds.Contains(s.BoundaryId) &&
            s.SourceMs - lastCutSource > options.CoalescingMs && lastTime - lastCutTime >= options.CoalescingMs))
            awaitingFreshFirst = true;
        var first = o.Visual.Where(s => FreshVisual(s) && s.Kind == FusionVisualKind.NewLineStarted &&
            s.SourceMs > lastCutSource && !firstIds.Contains(s.BoundaryId) &&
            s.SourceMs - lastCutSource > options.CoalescingMs && lastTime - lastCutTime >= options.CoalescingMs)
            .OrderBy(s => s.SourceMs).FirstOrDefault();
        if (first is not null && (pendingFirst is null || first.SourceMs > pendingFirst.SourceMs))
        { pendingFirst = first; awaitingFreshFirst = false; }
    }
    private bool FreshVisual(FusionVisualSignal signal) => signal.Score >= .5 && signal.SourceMs > closedAt &&
        lastTime - signal.SourceMs <= options.EvidenceWindowMs;

    private bool StrongSuccessorConflictsWithOption(FusionObservation o)
    {
        if(session.CurrentKey is null || session.State==QuestPlaybackState.OptionWait)return false;
        var edges=graph.Outgoing(session.CurrentKey).Where(x=>x.Status=="resolved" && x.TargetKey is not null).ToArray();
        if(edges.Any(x=>graph.Node(x.TargetKey!)?.Type=="MultiDialog"))return false;
        var candidates=edges.SelectMany(x=>graph.LineIndex.ForNode(x.TargetKey!)).Where(x=>x.OptionNumber is null).Select(x=>x.ToCandidate()).ToArray();
        return o.Semantic.Any(x=>x.Method=="D" && x.SupportsStrongEvidence && x.Text is not null && SemanticMatcher.Normalize(x.Text).Length>=6
            && x.SourceMs>closedAt && x.SourceMs>=lastCutSource && lastTime-x.SourceMs<=options.EvidenceWindowMs
            && matcher.Match(x.Text,candidates).Key is not null);
    }

    private (string? Key, bool Conflict, string Reason, double SourceMs) ResolveSemantic(FusionObservation o)
    {
        var scope = session.CurrentKey is null ? SpokenLines(o.Language) : graph.LineIndex.ForNode(session.CurrentKey)
            .Concat(session.Prefetch(options.PrefetchDepth)).Where(l => l.OptionNumber is null).ToArray();
        var keys = new List<(string Key, double Source)>();
        foreach (var evidence in o.Semantic.Where(e => e.SupportsIdentity && e.SourceMs > closedAt && e.SourceMs >= lastCutSource &&
            lastTime - e.SourceMs <= options.EvidenceWindowMs).GroupBy(e => e.Method)
            .Select(g => g.OrderByDescending(e => e.SourceMs).ThenByDescending(e => e.AvailableMs).First()))
        {
            string? key = null;
            if (evidence.Method == "D" && evidence.Text is not null)
            {
                var shortText=SemanticMatcher.Normalize(evidence.Text).Length<=4;
                if(shortText && session.CurrentKey is not null)
                {
                    var local=graph.LineIndex.ForNode(session.CurrentKey).Concat(graph.Outgoing(session.CurrentKey)
                        .Where(x=>x.Status=="resolved" && x.TargetKey is not null && graph.Node(x.TargetKey)?.Type=="SingleDialog")
                        .SelectMany(x=>graph.LineIndex.ForNode(x.TargetKey!))).Where(x=>x.OptionNumber is null).Select(x=>x.ToCandidate()).ToArray();
                    key=new ContextualShortLineMatcher().Match(evidence.Text,evidence.Speaker,local).Key;
                    if(key!=session.CurrentKey && pendingFirst is null && !awaitingBoundaryIdentity)key=null;
                }
                else if(!shortText)key = matcher.Match(evidence.Text, scope.Select(l => l.ToCandidate()).ToArray()).Key;
                // A strong, uniquely recognized outside node is conflict/recovery evidence;
                // it never silently enlarges the graph window or switches tasks.
                if (key is null && session.CurrentKey is not null && !shortText)
                    key = matcher.Match(evidence.Text, SpokenLines(o.Language).Select(l => l.ToCandidate()).ToArray()).Key;
            }
            else if (evidence.NodeKey is not null && evidence.Score >= (evidence.Method == "E" ? .80 : .78) &&
                evidence.Margin >= (evidence.Method == "E" ? .025 : .08)) key = evidence.NodeKey;
            if (key is not null) keys.Add((key, evidence.SourceMs));
        }
        var distinct = keys.Select(k => k.Key).Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length switch
        {
            0 => (null, false, "no_reliable_semantic_evidence", lastTime),
            1 => (distinct[0], false, "semantic_unique", keys.Min(k => k.Source)),
            _ => (null, true, "semantic_modalities_disagree", keys.Min(k => k.Source))
        };
    }

    private QuestLine[] SpokenLines(string? language) => graph.Nodes.Where(n => n.Type == "SingleDialog" && graph.CanActivate(n.Key))
        .SelectMany(n => graph.LineIndex.ForNode(n.Key)).Where(l => language is null || l.Language == language).ToArray();
    private bool ConfirmNode(string key, string? language)
    {
        if (!graph.CanActivate(key) || language is not null && graph.LineIndex.ForNode(key).Any(l => l.Language != language)) return false;
        if (session.CurrentKey is null) return session.StartAt(key).Accepted;
        return session.ConfirmLine(key).Accepted || session.Recover(key, options.PrefetchDepth).Accepted;
    }
    private void EnterChoice()
    {
        if (!UseGraph || session.CurrentKey is null || graph.Node(session.CurrentKey)!.Type == "MultiDialog") return;
        var targets = graph.Outgoing(session.CurrentKey).Where(e => e.Status == "resolved" && e.TargetKey is not null &&
            graph.Node(e.TargetKey)?.Type == "MultiDialog").Select(e => e.TargetKey!).Distinct().ToArray();
        if (targets.Length == 1) session.ConfirmLine(targets[0]);
    }
    private FusionResult Recover(FusionObservation o, List<FusionEvent> events, string reason)
    {
        recovering = true; clearOpen = false; pendingFirst = null; state = FusionState.Recovering;
        Emit(events, o, FusionEventKind.AudioStopCommand, lastTime, CurrentLine(), reason);
        return Result(events, reason);
    }
    private void Trigger(FusionObservation o, List<FusionEvent> events, QuestLine? line, string reason)
    {
        line = Bind(line);
        var first = pendingFirst!; var decision = DecisionId();
        Emit(events, o, FusionEventKind.CutTriggered, first.SourceMs, line, reason, decision);
        if (line is not null)
        {
            Emit(events, o, line.AudioStatus == "covered" && line.AudioPath is not null
                ? FusionEventKind.AudioPlayCommand : FusionEventKind.AudioStopCommand,
                first.SourceMs, line, line.AudioStatus == "covered" ? reason : line.AudioStatus, decision);
            lastPlayedKey = line.NodeKey;
        }
        firstIds.Add(first.BoundaryId);
        foreach (var signal in o.Visual.Where(s => s.Kind == FusionVisualKind.NewLineStarted &&
            Math.Abs(s.SourceMs - first.SourceMs) <= options.CoalescingMs)) firstIds.Add(signal.BoundaryId);
        lastCutSource = first.SourceMs; lastCutTime = lastTime;
        pendingFirst = null; clearOpen = awaitingBoundaryIdentity = false; state = FusionState.DialogueActive;
    }
    private void End(FusionObservation o, List<FusionEvent> events, string reason)
    {
        Emit(events, o, FusionEventKind.DialogueEnd, lastTime, CurrentLine(), reason);
        session.Exit(); closedAt = lastTime; epoch++; dialogueSeen = false; recovering = clearOpen = false;
        awaitingBoundaryIdentity=awaitingFreshFirst=false;boundaryIds.Clear();
        relocalizer?.Reset();choiceContinuation?.Reset();lastReliableMs=double.NegativeInfinity;
        pendingFirst = null; lastPlayedKey = null; clearIds.Clear(); firstIds.Clear(); state = FusionState.Finished;
    }
    private QuestLine? CurrentLine() => session.CurrentKey is null ? null :
        Bind(graph.LineIndex.ForNode(session.CurrentKey).SingleOrDefault(l => l.OptionNumber is null));
    private IReadOnlyList<QuestLine> Prefetch() => UseGraph ? Array.AsReadOnly(session.Prefetch(options.PrefetchDepth).Select(l => Bind(l)!).ToArray()) : [];
    private QuestLine? Bind(QuestLine? line) => line is not null && audioMetadata.TryGetValue(line.NodeKey, out var clip)
        ? line with { AudioPath = clip.IsValid ? clip.AudioPath : null, AudioStatus = clip.IsValid ? "covered" : "invalid-audio" } : line;
    private string DecisionId() => $"{epoch}:d{++decisionSequence}";
    private void Emit(List<FusionEvent> events, FusionObservation o, FusionEventKind kind, double source,
        QuestLine? line, string reason, string? decision = null,IReadOnlyList<string>? recoveryPath=null,IReadOnlyList<IReadOnlyList<string>>? choicePaths=null) => events.Add(new($"{epoch}:e{++eventSequence}",
            decision ?? DecisionId(), epoch, o.ClipId, kind, lastTime, source, line?.NodeKey, line?.Id, line?.AudioPath, reason,recoveryPath,choicePaths));
    private FusionResult Result(List<FusionEvent> events, string reason) =>
        new(state, session.CurrentKey, Array.AsReadOnly(Prefetch().ToArray()), events.AsReadOnly(), reason);

    private void Validate(FusionObservation o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var availableNow = o.AvailableAtMs ?? o.TimestampMs;
        if (string.IsNullOrWhiteSpace(o.ClipId) || o.FrameIndex < 0 || !double.IsFinite(o.TimestampMs) || o.TimestampMs < 0 ||
            !double.IsFinite(availableNow) || availableNow < o.TimestampMs ||
            o.TimestampMs <= lastTime || clipId is not null && clipId != o.ClipId || o.Gate is null || o.Visual is null || o.Semantic is null ||
            !double.IsFinite(o.Gate.TimestampSeconds) || Math.Abs(o.Gate.TimestampSeconds * 1000 - o.TimestampMs) > .000001 ||
            !Enum.IsDefined(o.Gate.Eye) || !Unit(o.Gate.EyeScore) || o.Gate.OptionRows < 0 || o.SelectedOption is <= 0)
            throw new ArgumentException("Fusion requires an increasing original PTS and matching current UI frame.");
        foreach (var signal in o.Visual)
            if (signal is null || !ValidVisualMethod(signal) || !Enum.IsDefined(signal.Kind) ||
                string.IsNullOrWhiteSpace(signal.BoundaryId) || signal.SourceMs > o.TimestampMs ||
                !Causal(signal.SourceMs, signal.AvailableMs, availableNow) || !Unit(signal.Score))
                throw new ArgumentException(signal is null
                    ? "Invalid or future visual evidence: null signal."
                    : $"Invalid or future visual evidence: method={signal.Method} kind={signal.Kind} id={signal.BoundaryId} " +
                      $"source={signal.SourceMs:R} available={signal.AvailableMs:R} now={availableNow:R} score={signal.Score:R}.");
        foreach (var evidence in o.Semantic)
            if (evidence is null || evidence.Method is not ("D" or "E") || evidence.SourceMs > o.TimestampMs ||
                !Causal(evidence.SourceMs, evidence.AvailableMs, availableNow) ||
                (evidence.Confidence.HasValue && !Unit(evidence.Confidence.Value)) || !Unit(evidence.Score) || !Unit(evidence.Margin))
                throw new ArgumentException(evidence is null
                    ? "Invalid or future semantic evidence: null evidence."
                    : $"Invalid or future semantic evidence: method={evidence.Method} source={evidence.SourceMs:R} " +
                      $"available={evidence.AvailableMs:R} now={availableNow:R} confidence={evidence.Confidence:R} " +
                      $"score={evidence.Score:R} margin={evidence.Margin:R}.");
    }
    // Clear candidates belong to the independent A/B/C visual baselines. A first-character
    // onset may also come from the D text pipeline (OCR change) and must remain observable.
    private static bool ValidVisualMethod(FusionVisualSignal signal) =>
        signal.Kind == FusionVisualKind.PossibleClear
            ? signal.Method is "A" or "B" or "C"
            : signal.Method is "A" or "B" or "C" or "D";
    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool Causal(double source, double available, double now) =>
        double.IsFinite(source) && double.IsFinite(available) && source >= 0 && available >= source && available <= now;
}
