using System.Text.Json;

namespace QuestVoice.Core;

/// <summary>
/// Machine-specific paths are stored in ignored config/paths.json, never in source.
/// Relative values resolve from the project root instead of the current directory.
/// External absolute paths are allowed only as values in the local config.
/// </summary>
public sealed class ProjectPathConfig
{
    private readonly Dictionary<string, string> values;
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "config", "paths.json");

    private ProjectPathConfig(string root, Dictionary<string, string> values)
    {
        Root = Path.GetFullPath(root);
        this.values = values;
    }

    public static ProjectPathConfig Load(string root)
    {
        root = Path.GetFullPath(root);
        var path = Path.Combine(root, "config", "paths.json");
        var values = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            : null;
        return new ProjectPathConfig(root,
            values is null ? new(StringComparer.OrdinalIgnoreCase)
                : new(values, StringComparer.OrdinalIgnoreCase));
    }

    public string Resolve(string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return ResolveValue(value);
    }

    public string Require(string key)
    {
        var value = Resolve(key);
        if (value.Length == 0)
            throw new InvalidOperationException($"Configure '{key}' in {FilePath}.");
        return value;
    }

    public string ResolveValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(value, Root);
    }

    public void Update(IReadOnlyDictionary<string, string> changes)
    {
        foreach (var entry in changes)
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                values[entry.Key] = string.Empty;
                continue;
            }
            var absolute = ResolveValue(entry.Value);
            var sameVolume = string.Equals(Path.GetPathRoot(Root),
                Path.GetPathRoot(absolute), StringComparison.OrdinalIgnoreCase);
            values[entry.Key] = sameVolume
                ? Path.GetRelativePath(Root, absolute).Replace('\\', '/')
                : absolute;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }

    public static string FindRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if ((File.Exists(Path.Combine(current.FullName, "QuestVoice.Desktop.runtimeconfig.json")) &&
                 File.Exists(Path.Combine(current.FullName, "QuestVoice.Desktop.dll"))) ||
                (File.Exists(Path.Combine(current.FullName, "TestPreVoice.Live.runtimeconfig.json")) &&
                 File.Exists(Path.Combine(current.FullName, "TestPreVoice.Live.dll"))))
                return current.FullName;
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                (File.Exists(Path.Combine(current.FullName, "src", "QuestVoice.Core", "QuestVoice.Core.csproj")) ||
                 File.Exists(Path.Combine(current.FullName, "src", "TestPreVoice.Core", "TestPreVoice.Core.csproj"))))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate QuestVoice project root from " + start);
    }
}
