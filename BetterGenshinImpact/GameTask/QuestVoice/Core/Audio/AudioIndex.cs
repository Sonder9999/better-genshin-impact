using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed class AudioIndex
{
    public IReadOnlyList<AudioEntry> Entries { get; }
    public IReadOnlyList<AuditIssue> Issues { get; }
    private AudioIndex(IReadOnlyList<AudioEntry> entries, IReadOnlyList<AuditIssue> issues) { Entries = entries; Issues = issues; }

    public static AudioIndex Read(string directory)
    {
        var entries = new List<AudioEntry>(); var issues = new List<AuditIssue>();
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Where(p => Path.GetExtension(p).Equals(".wav", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new("audio-directory-read-failed", directory, $"{ex.GetType().Name}: {ex.Message}"));
            return new(entries, issues);
        }
        foreach (var path in paths)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^(\d+)(?:-player_opt([1-9]\d*))?$", RegexOptions.CultureInvariant);
            string? id = match.Success ? match.Groups[1].Value : null;
            int? option = null;
            if (match.Success && match.Groups[2].Success)
            {
                if (int.TryParse(match.Groups[2].Value, out var parsed)) option = parsed;
                else { id = null; match = Match.Empty; }
            }
            var kind = !match.Success ? "unknown" : option == null ? "ordinary" : "player-option";
            var wave = WaveReader.Read(path);
            entries.Add(new(Path.GetFullPath(path), id, option, kind, wave));
            if (kind == "unknown") issues.Add(new("unknown-audio-name", path, "Expected ID.wav or ID-player_optN.wav"));
            if (!wave.IsValid) issues.Add(new("invalid-wave", path, string.Join("; ", wave.Errors)));
        }
        foreach (var group in entries.Where(e => e.Id != null).GroupBy(e => (e.Id, e.OptionNumber)).Where(g => g.Count() > 1))
            issues.Add(new("duplicate-audio-id", directory, $"{group.Key.Id}/option={group.Key.OptionNumber}: {group.Count()} files"));
        return new(entries, issues);
    }

    public AudioEntry? Find(string id, int? optionNumber = null)
    {
        var matches = Entries.Where(e => e.Id == id && e.OptionNumber == optionNumber).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
