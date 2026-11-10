using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestVoice.Core;

public enum QuestPlaybackState { Active, Transitioning, OptionWait, Finished }
public sealed record QuestDecision(bool Accepted, bool Changed, string Reason, string? NodeKey, QuestPlaybackState State);
public sealed record QuestLine(string Key, string NodeKey, string Id, string Text, string Role, string Language,
    int? OptionNumber, string? AudioPath, string AudioStatus)
{
    public LineCandidate ToCandidate() => new(Key, Id, Text, Role);
}

/// <summary>Read-only quest catalog; playback identity belongs to independent sessions.</summary>
public sealed class QuestGraph
{
    private readonly Dictionary<string, QuestNode> nodes;
    private readonly Dictionary<string, QuestTask> tasks;
    private readonly Dictionary<string, IReadOnlyList<QuestEdge>> outgoing;
    private readonly Dictionary<string, IReadOnlyList<QuestNode>> localIds;
    private readonly HashSet<string> unsafeNodes = new(StringComparer.Ordinal);

    public IReadOnlyList<QuestNode> Nodes { get; }
    public IReadOnlyList<QuestEdge> Edges { get; }
    public IReadOnlyList<QuestTask> Tasks { get; }
    public IReadOnlyList<AuditIssue> Issues { get; }
    public bool IsComplete { get; }
    public QuestLineIndex LineIndex { get; }

    public QuestGraph(IReadOnlyList<QuestDocument> documents, AudioIndex? audioIndex = null)
        : this(documents, audioIndex, []) { }

    private QuestGraph(IReadOnlyList<QuestDocument> documents, AudioIndex? audioIndex, IReadOnlyList<AuditIssue> loadIssues)
    {
        // Snapshot collections: callers cannot change catalog membership or option text after indexing.
        Nodes = Array.AsReadOnly(documents.SelectMany(d => d.Nodes)
            .Select(n => n with { Texts = Array.AsReadOnly(n.Texts.ToArray()) }).ToArray());
        Edges = Array.AsReadOnly(documents.SelectMany(d => d.Edges).ToArray());
        Tasks = Array.AsReadOnly(documents.SelectMany(d => d.Tasks).ToArray());
        nodes = Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        tasks = Tasks.ToDictionary(t => t.Key, StringComparer.Ordinal);
        outgoing = Edges.GroupBy(e => e.FromKey).ToDictionary(g => g.Key,
            g => (IReadOnlyList<QuestEdge>)Array.AsReadOnly(g.ToArray()), StringComparer.Ordinal);
        localIds = Nodes.GroupBy(n => LocalId(n.TaskContext, n.Id)).ToDictionary(g => g.Key,
            g => (IReadOnlyList<QuestNode>)Array.AsReadOnly(g.ToArray()), StringComparer.Ordinal);

        var issues = documents.SelectMany(d => d.Issues).Concat(loadIssues).ToList();
        if (documents.Count == 0) issues.Add(new("no-quest-documents", "directory", "No matching quest documents loaded"));
        if (documents.Count > 0 && (Nodes.Count == 0 || Tasks.Count == 0))
            issues.Add(new("empty-quest-graph", "documents", "Loaded documents contain no task graph"));
        foreach (var edge in Edges.Where(e => !nodes.ContainsKey(e.FromKey)))
            issues.Add(new("orphan-edge", edge.FromKey, "Edge source is absent from the graph"));
        foreach (var node in Nodes)
        {
            var edges = Outgoing(node.Key);
            if (node.Type is not ("SingleDialog" or "MultiDialog") || node.Texts.Count == 0 || node.Texts.Any(t => string.IsNullOrWhiteSpace(t.Text)) ||
                node.Type == "SingleDialog" && node.Texts.Count != 1)
            {
                unsafeNodes.Add(node.Key);
                issues.Add(new("invalid-dialog-node", node.Key, "Expected SingleDialog with one text or MultiDialog with numbered options"));
            }
            if (edges.Count != node.Texts.Count || edges.Select(e => e.OptionNumber).Distinct().Count() != edges.Count ||
                node.Texts.Any(t => !edges.Any(e => e.OptionNumber == t.OptionNumber)) ||
                edges.Any(e => !IsValidEdge(node, e)))
            {
                unsafeNodes.Add(node.Key);
                issues.Add(new("unsafe-node-edges", node.Key, "Outgoing edges are incomplete, ambiguous, unresolved or outside the task"));
            }
        }
        foreach (var task in Tasks)
        {
            var entry = Entry(task.Key);
            if (entry is null)
            {
                issues.Add(new("invalid-task-entry", task.Key, "initDialog must resolve to exactly one local node"));
                continue;
            }
            var reached = new HashSet<string>(StringComparer.Ordinal) { entry.Key };
            var queue = new Queue<string>(); queue.Enqueue(entry.Key);
            while (queue.TryDequeue(out var key))
                foreach (var edge in Outgoing(key))
                    if (edge.Status == "resolved" && edge.TargetKey is not null &&
                        nodes.TryGetValue(edge.TargetKey, out var target) && target.TaskContext == task.Key && reached.Add(target.Key))
                        queue.Enqueue(target.Key);
            foreach (var node in Nodes.Where(n => n.TaskContext == task.Key && !reached.Contains(n.Key)))
                issues.Add(new("unreachable-node", node.Key, "Node cannot be reached from its task initDialog"));
        }
        Issues = Array.AsReadOnly(issues.Distinct().ToArray());
        IsComplete = Issues.All(i => i.Code is "duplicate-id" or "duplicate-text");
        LineIndex = new QuestLineIndex(Nodes, documents, audioIndex);
    }

    public static QuestGraph LoadDirectory(string directory, string? language = null, AudioIndex? audioIndex = null)
    {
        var documents = new List<QuestDocument>(); var issues = new List<AuditIssue>();
        // File prefixes select inputs; they never supply graph order or chapter identity.
        foreach (var path in Directory.EnumerateFiles(directory).Where(p =>
            Regex.IsMatch(Path.GetFileName(p), @"^(01|02|03)_.+\.json$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            try
            {
                var document = QuestReader.Read(path);
                if (language is null || document.Language == language) documents.Add(document);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            { issues.Add(new(error is JsonException ? "invalid-quest-json" : "quest-read-failed", path, error.Message)); }
        }
        return new(documents, audioIndex, issues);
    }

    public QuestSession CreateSession() => new(this);
    internal QuestNode? Node(string key) => nodes.GetValueOrDefault(key);
    internal bool CanActivate(string key) => nodes.ContainsKey(key) && !unsafeNodes.Contains(key);
    internal IReadOnlyList<QuestEdge> Outgoing(string key) => outgoing.GetValueOrDefault(key) ?? [];
    private bool IsValidEdge(QuestNode source, QuestEdge edge) => edge.Status == "terminal"
        ? edge.TargetKey is null && edge.TargetId is null
        : edge.Status == "resolved" && edge.TargetKey is not null && nodes.TryGetValue(edge.TargetKey, out var target) &&
            target.TaskContext == source.TaskContext && target.Id == edge.TargetId;
    internal QuestNode? Entry(string taskKey)
    {
        if (!tasks.TryGetValue(taskKey, out var task)) return null;
        var id = task.InitDialog.ValueKind switch
        {
            JsonValueKind.String => task.InitDialog.GetString(),
            JsonValueKind.Number when task.InitDialog.TryGetInt64(out var value) => value.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
        if (id is null || !localIds.TryGetValue(LocalId(taskKey, id), out var found) || found.Count != 1) return null;
        return found[0];
    }
    private static string LocalId(string context, string id) => JsonSerializer.Serialize(new[] { context, id });
}

/// <summary>Precomputed text/role/ID lookup. Audio availability never supplies quest identity.</summary>
public sealed class QuestLineIndex
{
    private readonly Dictionary<string, IReadOnlyList<QuestLine>> byId, byText, byRole, byNode;
    private readonly Dictionary<string, string> contexts;

    internal QuestLineIndex(IReadOnlyList<QuestNode> nodes, IReadOnlyList<QuestDocument> documents, AudioIndex? audioIndex)
    {
        contexts = nodes.ToDictionary(n => n.Key, n => n.TaskContext, StringComparer.Ordinal);
        var languages = documents.ToDictionary(d => d.SourcePath, d => d.Language, StringComparer.Ordinal);
        var audio = audioIndex?.Entries.Where(e => e.Id is not null).GroupBy(e => (e.Id!, e.OptionNumber))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var lines = nodes.SelectMany(node => node.Texts.Select(text =>
        {
            var option = node.Type == "MultiDialog" ? (int?)text.OptionNumber : null;
            var audioId = option is not null && node.Id.EndsWith("-player", StringComparison.Ordinal) ? node.Id[..^7] : node.Id;
            var matches = audio?.GetValueOrDefault((audioId, option)) ?? [];
            // A present WAV cannot supply the identity evidence missing from an empty script line.
            var status = string.IsNullOrWhiteSpace(text.Text) ? "empty-text" : audio is null ? "not-indexed" : matches.Length == 0 ? "missing-audio" :
                matches.Length > 1 ? "ambiguous-audio" : !matches[0].Wave.IsValid ? "invalid-audio" : "covered";
            var key = option is null ? node.Key : node.Key + "/option/" + text.OptionNumber.ToString(CultureInfo.InvariantCulture);
            return new QuestLine(key, node.Key, node.Id, text.Text, node.Role, languages[node.SourcePath], option,
                status == "covered" ? matches[0].Path : null, status);
        })).ToArray();
        byId = Index(lines, c => c.Id);
        byText = Index(lines, c => SemanticMatcher.Normalize(c.Text));
        byRole = Index(lines, c => c.Role);
        byNode = Index(lines, c => c.NodeKey);
    }

    public IReadOnlyList<QuestLine> FindById(string id, string? language = null, string? taskContext = null) =>
        Filter(byId.GetValueOrDefault(id) ?? [], null, language, taskContext);
    public IReadOnlyList<QuestLine> FindExact(string text, string? role = null, string? language = null, string? taskContext = null) =>
        Filter(byText.GetValueOrDefault(SemanticMatcher.Normalize(text)) ?? [], role, language, taskContext);
    public IReadOnlyList<QuestLine> FindByRole(string role, string? language = null, string? taskContext = null) =>
        Filter(byRole.GetValueOrDefault(role) ?? [], null, language, taskContext);
    public IReadOnlyList<QuestLine> ForNode(string nodeKey) => byNode.GetValueOrDefault(nodeKey) ?? [];

    private IReadOnlyList<QuestLine> Filter(IReadOnlyList<QuestLine> source, string? role, string? language, string? context) =>
        role is null && language is null && context is null ? source : Array.AsReadOnly(source.Where(c =>
            (role is null || c.Role == role) && (language is null || c.Language == language) &&
            (context is null || contexts[c.NodeKey] == context)).ToArray());
    private static Dictionary<string, IReadOnlyList<QuestLine>> Index(IEnumerable<QuestLine> source, Func<QuestLine, string> key) =>
        source.GroupBy(key).ToDictionary(g => g.Key, g => (IReadOnlyList<QuestLine>)Array.AsReadOnly(g.ToArray()), StringComparer.Ordinal);
}

/// <summary>Consumes confirmed node/choice evidence. It neither observes UI nor issues playback events.</summary>
public sealed class QuestSession
{
    private readonly QuestGraph graph;
    private readonly List<string> history = [];
    private int? selectedOption;
    public QuestPlaybackState State { get; private set; } = QuestPlaybackState.Finished;
    public string? CurrentKey { get; private set; }
    public QuestDecision LastDecision { get; private set; } = new(false, false, "no_context", null, QuestPlaybackState.Finished);
    internal QuestSession(QuestGraph graph) => this.graph = graph;

    public QuestDecision StartTask(string taskKey)
    {
        var entry = graph.Entry(taskKey);
        return entry is null ? Record(false, false, "invalid_task_entry") : StartAt(entry.Key);
    }
    /// <summary>Establish context from an externally confirmed full node key, including mid-task entry.</summary>
    public QuestDecision StartAt(string nodeKey)
    {
        if (!graph.CanActivate(nodeKey)) return Record(false, false, "invalid_start_node");
        history.Clear(); Activate(nodeKey);
        return Record(true, true, "context_started");
    }
    public QuestDecision BeginTransition()
    {
        if (State == QuestPlaybackState.Transitioning) return Record(true, false, "transition_pending");
        if (State != QuestPlaybackState.Active) return Record(false, false, State == QuestPlaybackState.OptionWait ? "selection_required" : "no_context");
        State = QuestPlaybackState.Transitioning;
        return Record(true, true, "possible_clear");
    }
    public QuestDecision ConfirmLine(string nodeKey)
    {
        if (CurrentKey is null) return Record(false, false, "no_context");
        if (nodeKey == CurrentKey) return Record(true, false, "same_node");
        if (!graph.CanActivate(nodeKey)) return Record(false, false, "unknown_or_unsafe_node");
        if (graph.Node(nodeKey)!.TaskContext != graph.Node(CurrentKey)!.TaskContext) return Record(false, false, "different_context");
        if (State == QuestPlaybackState.OptionWait) return Record(false, false, "selection_required");
        if (!CurrentEdges().Any(e => e.Status == "resolved" && e.TargetKey == nodeKey)) return Record(false, false, "not_expected_successor");
        Activate(nodeKey);
        return Record(true, true, "line_confirmed");
    }
    public QuestDecision ObserveLineId(string id)
    {
        if (CurrentKey is null) return Record(false, false, "no_context");
        var context = graph.Node(CurrentKey)!.TaskContext;
        var indexed = graph.LineIndex.FindById(id, taskContext: context);
        if (indexed.Count == 0) return Record(false, false, "unknown_line_id");
        var allowed = CurrentEdges().Where(e => e.Status == "resolved").Select(e => e.TargetKey).ToHashSet(StringComparer.Ordinal);
        allowed.Add(CurrentKey);
        var keys = indexed.Select(c => c.NodeKey).Distinct(StringComparer.Ordinal).Where(k => allowed.Contains(k)).ToArray();
        return keys.Length switch
        {
            0 => Record(false, false, "not_expected_successor"),
            1 => ConfirmLine(keys[0]),
            _ => Record(false, false, "ambiguous_line_id")
        };
    }
    public QuestDecision SelectOption(int optionNumber)
    {
        if (State != QuestPlaybackState.OptionWait || CurrentKey is null) return Record(false, false, "not_waiting_for_option");
        var edge = graph.Outgoing(CurrentKey).SingleOrDefault(e => e.OptionNumber == optionNumber);
        if (edge is null) return Record(false, false, "invalid_option");
        if (edge.Status == "terminal") return Finish("terminal_option");
        if (edge.Status != "resolved" || edge.TargetKey is null || !graph.CanActivate(edge.TargetKey)) return Record(false, false, "unsafe_option_target");
        selectedOption = optionNumber; State = QuestPlaybackState.Transitioning;
        return Record(true, true, "option_selected");
    }
    public QuestDecision CompleteTransition()
    {
        if (State != QuestPlaybackState.Transitioning || CurrentKey is null) return Record(false, false, "not_transitioning");
        var edges = CurrentEdges();
        return edges.Count == 1 && edges[0].Status == "terminal" ? Finish("terminal_edge") : Record(false, false, "successor_confirmation_required");
    }
    public QuestDecision Recover(string nodeKey, int maxSteps = 3)
    {
        ValidateDepth(maxSteps);
        if (CurrentKey is null) return Record(false, false, "no_context");
        if (nodeKey == CurrentKey) return Record(true, false, "same_node");
        if (!graph.CanActivate(nodeKey) || graph.Node(nodeKey)!.TaskContext != graph.Node(CurrentKey)!.TaskContext)
            return Record(false, false, "unknown_or_different_context");
        for (var index = history.Count - 2; index >= Math.Max(0, history.Count - 1 - maxSteps); index--)
            if (history[index] == nodeKey)
            {
                history.RemoveRange(index, history.Count - index); Activate(nodeKey);
                return Record(true, true, "history_recovered");
            }
        var visited = new HashSet<string>(StringComparer.Ordinal) { CurrentKey };
        var queue = new Queue<(string Key, int Depth)>();
        foreach (var edge in CurrentEdges()) if (edge.Status == "resolved" && edge.TargetKey is not null) queue.Enqueue((edge.TargetKey, 1));
        if (State == QuestPlaybackState.OptionWait) queue.Clear();
        while (queue.TryDequeue(out var item))
        {
            if (!visited.Add(item.Key) || !graph.CanActivate(item.Key)) continue;
            if (item.Key == nodeKey) { Activate(nodeKey); return Record(true, true, "successor_recovered"); }
            if (item.Depth >= maxSteps || graph.Node(item.Key)!.Type == "MultiDialog") continue;
            foreach (var edge in graph.Outgoing(item.Key))
                if (edge.Status == "resolved" && edge.TargetKey is not null) queue.Enqueue((edge.TargetKey, item.Depth + 1));
        }
        return Record(false, false, "outside_recovery_window");
    }
    public QuestDecision Exit() => Finish("dialogue_exited");

    internal IReadOnlyList<CommonChoiceCandidate> CommonChoiceCandidates(int maxEdges,int maxNodes)
    {
        if(maxEdges is <1 or >64 || maxNodes is <1 or >256)throw new ArgumentOutOfRangeException(nameof(maxEdges));
        if(State!=QuestPlaybackState.OptionWait || CurrentKey is null || !graph.CanActivate(CurrentKey))return [];
        var root=graph.Node(CurrentKey)!;
        if(root.Type!="MultiDialog")return [];
        var options=graph.Outgoing(root.Key);
        if(options.Count==0 || options.Count>maxNodes || options.Any(e=>e.Status!="resolved" || e.TargetKey is null))return [];
        var branches=new List<IReadOnlyList<string>>();var visitedBudget=0;
        foreach(var option in options)
        {
            var path=new List<string>{root.Key};var visited=new HashSet<string>(StringComparer.Ordinal){root.Key};var key=option.TargetKey!;
            for(int depth=1;depth<=maxEdges;depth++)
            {
                if(++visitedBudget>maxNodes)return [];
                if(!visited.Add(key) || !graph.CanActivate(key))break;
                var node=graph.Node(key)!;
                if(node.TaskContext!=root.TaskContext || node.Type!="SingleDialog")break;
                path.Add(key);
                var next=graph.Outgoing(key);
                if(next.Count!=1 || next[0].Status!="resolved" || next[0].TargetKey is null)break;
                key=next[0].TargetKey!;
            }
            branches.Add(path.AsReadOnly());
        }
        var common=branches[0].Skip(1).Where(key=>branches.All(p=>p.Contains(key,StringComparer.Ordinal)));
        return common.Select(key=>new CommonChoiceCandidate(graph.LineIndex.ForNode(key).Single(l=>l.OptionNumber is null),
            Array.AsReadOnly(branches.Select(p=>(IReadOnlyList<string>)Array.AsReadOnly(p.TakeWhile(n=>n!=key).Append(key).ToArray())).ToArray()))).ToArray();
    }
    internal QuestDecision ResumeCommonChoiceNode(string key,int maxEdges,int maxNodes,out IReadOnlyList<IReadOnlyList<string>>? paths)
    {
        var candidate=CommonChoiceCandidates(maxEdges,maxNodes).SingleOrDefault(c=>c.Line.NodeKey==key);
        paths=candidate?.Paths;
        if(candidate is null)return Record(false,false,"outside_common_choice_scope");
        Activate(key);return Record(true,true,"observed_common_choice_successor");
    }

    internal IReadOnlyList<string> LastRecoveryPath {get;private set;}=[];
    internal IReadOnlyList<QuestLine> RecoveryCandidates(int maxEdges,int maxCandidates)
        =>RecoveryCandidates(maxEdges,maxCandidates,new Dictionary<string,string>(StringComparer.Ordinal));
    private IReadOnlyList<QuestLine> RecoveryCandidates(int maxEdges,int maxCandidates,Dictionary<string,string> parents)
    {
        if(maxEdges is <1 or >64 || maxCandidates is <1 or >256)throw new ArgumentOutOfRangeException(nameof(maxEdges));
        if(CurrentKey is null || State==QuestPlaybackState.OptionWait)return [];
        var context=graph.Node(CurrentKey)!.TaskContext;var result=new List<QuestLine>();
        var visited=new HashSet<string>(StringComparer.Ordinal){CurrentKey};var queue=new Queue<(string Key,int Depth,string Parent)>();
        foreach(var e in CurrentEdges())if(e.Status=="resolved" && e.TargetKey is not null)queue.Enqueue((e.TargetKey,1,CurrentKey));
        while(queue.TryDequeue(out var item) && visited.Count<=maxCandidates)
        {
            if(!visited.Add(item.Key) || !graph.CanActivate(item.Key))continue;
            var node=graph.Node(item.Key)!;
            if(node.TaskContext!=context || node.Type=="MultiDialog")continue;
            parents[item.Key]=item.Parent;
            result.AddRange(graph.LineIndex.ForNode(item.Key).Where(x=>x.OptionNumber is null).Take(maxCandidates-result.Count));
            if(result.Count>=maxCandidates)break;
            if(item.Depth<maxEdges)foreach(var e in graph.Outgoing(item.Key))
                if(e.Status=="resolved" && e.TargetKey is not null)queue.Enqueue((e.TargetKey,item.Depth+1,item.Key));
        }
        return result.AsReadOnly();
    }
    internal QuestDecision RelocateForward(string key,int maxEdges,int maxCandidates)
    {
        var parents=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!RecoveryCandidates(maxEdges,maxCandidates,parents).Any(x=>x.NodeKey==key))return Record(false,false,"outside_selected_recovery_scope");
        var path=new List<string>{key};var cursor=key;
        while(parents.TryGetValue(cursor,out var parent)){path.Add(parent);cursor=parent;}
        path.Reverse();LastRecoveryPath=path.AsReadOnly();
        Activate(key);return Record(true,true,"same_task_relocalized");
    }

    /// <summary>Prepare possible spoken lines, including unselected branches. This does not authorize playback.</summary>
    public IReadOnlyList<QuestLine> Prefetch(int depth = 1)
    {
        ValidateDepth(depth);
        if (CurrentKey is null) return [];
        // Depth counts spoken lines. Choice vertices are transparent for preparing possible branches.
        var visited = new HashSet<string>(StringComparer.Ordinal) { CurrentKey };
        // Choice edges cost zero spoken lines. A priority queue visits each node at its shortest distance.
        var queued = new PriorityQueue<string, (int Depth, int Order)>(); var order = 0;
        var result = new List<QuestLine>();
        foreach (var edge in CurrentEdges()) if (edge.Status == "resolved" && edge.TargetKey is not null) queued.Enqueue(edge.TargetKey, (1, order++));
        while (queued.TryDequeue(out var key, out var distance))
        {
            if (!visited.Add(key) || !graph.CanActivate(key)) continue;
            var node = graph.Node(key)!; var choice = node.Type == "MultiDialog";
            if (!choice) result.AddRange(graph.LineIndex.ForNode(key));
            if (!choice && distance.Depth >= depth) continue;
            foreach (var edge in graph.Outgoing(key))
                if (edge.Status == "resolved" && edge.TargetKey is not null) queued.Enqueue(edge.TargetKey, (distance.Depth + (choice ? 0 : 1), order++));
        }
        return result.AsReadOnly();
    }

    private IReadOnlyList<QuestEdge> CurrentEdges()
    {
        if (CurrentKey is null) return [];
        var edges = graph.Outgoing(CurrentKey);
        return selectedOption is null ? edges : Array.AsReadOnly(edges.Where(e => e.OptionNumber == selectedOption).ToArray());
    }
    private void Activate(string key)
    {
        CurrentKey = key; selectedOption = null; history.Add(key);
        State = graph.Node(key)!.Type == "MultiDialog" ? QuestPlaybackState.OptionWait : QuestPlaybackState.Active;
    }
    private QuestDecision Finish(string reason)
    {
        var changed = CurrentKey is not null || State != QuestPlaybackState.Finished;
        CurrentKey = null; selectedOption = null; history.Clear(); State = QuestPlaybackState.Finished;
        return Record(true, changed, reason);
    }
    private QuestDecision Record(bool accepted, bool changed, string reason) =>
        LastDecision = new(accepted, changed, reason, CurrentKey, State);
    private static void ValidateDepth(int depth)
    { if (depth is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be between 1 and 3."); }
}
