namespace QuestVoice.Core;

public sealed class AudioWaveCache
{
    private sealed record Key(string Node, string Variant, string Path, long Length, long Modified, AudioFormat Format);
    private sealed record Entry(PCMWave Wave, LinkedListNode<Key> Order);
    private readonly object gate = new();
    private readonly long budget;
    private readonly Dictionary<Key, Entry> entries = [];
    private readonly LinkedList<Key> recent = new();
    private long bytes, epoch;
    public long Bytes { get { lock (gate) return bytes; } }
    public int Count { get { lock (gate) return entries.Count; } }
    public AudioWaveCache(long budgetBytes)
    {
        if (budgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        budget = budgetBytes;
    }
    public Task<PCMWave> LoadAsync(AudioLine line, AudioFormat format, IWaveLoader loader,
        long maxDecodedBytes, CancellationToken cancellationToken = default) => LoadCoreAsync(line, format, loader, maxDecodedBytes, cancellationToken);
    public PCMWave? TryGetReady(AudioLine line, AudioFormat format, long maxDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.WavePath is null || maxDecodedBytes <= 0) return null;
        try
        {
            var file = new FileInfo(Path.GetFullPath(line.WavePath));
            var key = new Key(line.NodeId, line.Variant, file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, format);
            lock (gate)
            {
                if (!entries.TryGetValue(key, out var cached) || cached.Wave.ByteLength > maxDecodedBytes) return null;
                recent.Remove(cached.Order); recent.AddLast(cached.Order); return cached.Wave;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private async Task<PCMWave> LoadCoreAsync(AudioLine line, AudioFormat format, IWaveLoader loader, long maximum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(line); ArgumentNullException.ThrowIfNull(loader);
        if (line.WavePath is null) throw new InvalidDataException("No WAV source.");
        var file = new FileInfo(Path.GetFullPath(line.WavePath));
        var key = new Key(line.NodeId, line.Variant, file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, format);
        long startedEpoch;
        lock (gate)
        {
            startedEpoch = epoch;
            if (entries.TryGetValue(key, out var cached))
            {
                CheckWave(cached.Wave, format, maximum);
                recent.Remove(cached.Order); recent.AddLast(cached.Order);
                return cached.Wave;
            }
        }
        var wave = await loader.LoadAsync(file.FullName, format, maximum, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        CheckWave(wave, format, maximum);
        file.Refresh();
        lock (gate)
        {
            if (epoch != startedEpoch || !file.Exists || file.Length != key.Length || file.LastWriteTimeUtc.Ticks != key.Modified || wave.ByteLength > budget)
                return wave;
            foreach (var old in entries.Keys.Where(k => k.Node == key.Node && k.Variant == key.Variant && k.Path == key.Path && k.Format == key.Format).ToArray()) Remove(old);
            while (bytes + wave.ByteLength > budget && recent.First is not null) Remove(recent.First.Value);
            entries.Add(key, new(wave, recent.AddLast(key))); bytes += wave.ByteLength;
        }
        return wave;
    }

    public void Clear()
    {
        lock (gate) { epoch++; entries.Clear(); recent.Clear(); bytes = 0; }
    }
    private void Remove(Key key)
    {
        var entry = entries[key]; bytes -= entry.Wave.ByteLength; recent.Remove(entry.Order); entries.Remove(key);
    }
    private static void CheckWave(PCMWave wave, AudioFormat format, long maximum)
    {
        if (wave is null || wave.Format != format || wave.ByteLength > maximum)
            throw new InvalidDataException("Loader returned an incompatible or oversized PCM wave.");
    }
}
