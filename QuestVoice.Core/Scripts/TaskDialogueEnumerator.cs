namespace QuestVoice.Core;

public sealed record QuestTaskScope(string ChapterId, string StoryId, string TaskId)
{
    public string Key => System.Text.Json.JsonSerializer.Serialize(new[] { ChapterId, StoryId, TaskId });
    public override string ToString() => $"Chapter {ChapterId} · Story {StoryId} · Task {TaskId}";
    public bool Contains(QuestNode node) => node.ChapterId == ChapterId && node.StoryId == StoryId && node.TaskId == TaskId;
}
public sealed record TaskEnumeration(IReadOnlyList<QuestNode> Spoken, IReadOnlyList<(string NodeKey, string Reason)> Skipped,
    int Visited, string StartStatus);
public static class TaskDialogueEnumerator
{
    /// <summary>Unbounded read-only BFS through all resolved branches, scoped to original taskData contexts.</summary>
    public static TaskEnumeration Enumerate(QuestGraph graph, QuestTaskScope scope, string? confirmedNode = null,
        IReadOnlySet<string>? passed = null)
    {
        var spoken = new List<QuestNode>(); var skipped = new List<(string, string)>();
        var visited = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>();
        if (confirmedNode is not null)
        {
            var current = graph.Node(confirmedNode);
            if (current is null || !scope.Contains(current) || !graph.CanActivate(current.Key))
                return new([], [(confirmedNode, "Invalid confirmed node; no guessed progress")], 0, "InvalidCurrentNode");
            visited.Add(current.Key);
            foreach (var edge in graph.Outgoing(current.Key)) if (edge.Status == "resolved" && edge.TargetKey is not null) queue.Enqueue(edge.TargetKey);
        }
        else
        {
            var contexts = graph.Nodes.Where(scope.Contains).Select(n => n.TaskContext).Distinct().ToArray();
            foreach (var context in contexts)
            {
                var entry = graph.Entry(context);
                if (entry is null) skipped.Add((context, "Invalid initDialog")); else queue.Enqueue(entry.Key);
            }
        }
        while (queue.TryDequeue(out var key))
        {
            if (!visited.Add(key)) continue;
            var node = graph.Node(key);
            if (node is null || !scope.Contains(node)) { skipped.Add((key, "Outside task / missing target")); continue; }
            if (!graph.CanActivate(key)) { skipped.Add((key, "Unsafe node: cannot traverse")); continue; }
            if (passed?.Contains(key) == true) skipped.Add((key, "Already passed"));
            else if (node.Type == "SingleDialog") spoken.Add(node);
            else skipped.Add((key, "Player options are not speech"));
            foreach (var edge in graph.Outgoing(key))
            {
                if (edge.Status == "resolved" && edge.TargetKey is not null && graph.Node(edge.TargetKey)?.TaskContext == node.TaskContext)
                    queue.Enqueue(edge.TargetKey);
                else if (edge.Status != "terminal") skipped.Add((key, "Unsafe edge: " + edge.Status));
            }
        }
        return new(spoken.AsReadOnly(), skipped.AsReadOnly(), visited.Count, confirmedNode is null ? "Task initDialog (no confirmed progress)" : "Successors of confirmed node");
    }
}
