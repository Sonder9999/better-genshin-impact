using System.Buffers.Binary;
using System.Text;

namespace QuestVoice.Core;

public sealed class WaveFileLoader : IWaveLoader
{
    public Task<PCMWave> LoadAsync(string wavePath, AudioFormat outputFormat, long maxDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        PCMWave.ValidateFormat(outputFormat);
        if (maxDecodedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDecodedBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(wavePath);
        return Task.Run(() => Decode(path, outputFormat, maxDecodedBytes, cancellationToken), cancellationToken);
    }

    private static PCMWave Decode(string path, AudioFormat target, long maximum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64L * 1024 * 1024) throw new InvalidDataException("Encoded WAV exceeds 64 MiB limit.");
        var info = WaveReader.Read(path); // The first read handle prevents concurrent writes while metadata is verified.
        if (!info.IsValid) throw new InvalidDataException(string.Join("; ", info.Errors));
        if (info.Channels is not (1 or 2) || info.DataBytes == 0) throw new InvalidDataException("WAV must contain mono/stereo samples.");
        var sourceFrames = info.DataBytes / info.BlockAlign!.Value;
        var frames = Math.Ceiling(sourceFrames * (double)target.SampleRate / info.SampleRate!.Value);
        if (frames <= 0 || frames * target.Channels * sizeof(float) > maximum || frames * target.Channels > int.MaxValue)
            throw new InvalidDataException("Decoded WAV exceeds allocation budget.");
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        stream.Position = 12;
        byte[]? data = null;
        var tag = info.FormatTag!.Value;
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4)); var size = reader.ReadUInt32(); var start = stream.Position;
            if (name == "fmt " && tag == 65534)
            {
                stream.Position = start + 24;
                tag = new Guid(reader.ReadBytes(16)) == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)1;
            }
            if (name == "data") data = reader.ReadBytes(checked((int)size));
            stream.Position = start + size + (size & 1);
        }
        if (data is null || data.LongLength != info.DataBytes) throw new InvalidDataException("Truncated sample data.");
        var result = new float[checked((int)frames * target.Channels)];
        var channels = info.Channels!.Value; var bytes = info.BitsPerSample!.Value / 8;
        for (var frame = 0; frame < (int)frames; frame++)
        {
            if ((frame & 511) == 0) ct.ThrowIfCancellationRequested();
            var position = frame * (double)info.SampleRate.Value / target.SampleRate;
            var first = Math.Min((long)position, sourceFrames - 1); var next = Math.Min(first + 1, sourceFrames - 1);
            var fraction = position - Math.Floor(position);
            double Channel(int channel)
            {
                var a = Sample(data, checked((int)((first * channels + channel) * bytes)), tag, info.BitsPerSample.Value);
                var b = Sample(data, checked((int)((next * channels + channel) * bytes)), tag, info.BitsPerSample.Value);
                return a + (b - a) * fraction;
            }
            var left = Channel(0); var right = channels == 1 ? left : Channel(1);
            if (target.Channels == 1) result[frame] = (float)Math.Clamp((left + right) / 2, -1, 1);
            else
            {
                result[frame * 2] = (float)Math.Clamp(left, -1, 1);
                result[frame * 2 + 1] = (float)Math.Clamp(right, -1, 1);
            }
        }
        ct.ThrowIfCancellationRequested();
        return PCMWave.Own(target, result);
    }

    private static double Sample(byte[] data, int offset, ushort tag, ushort bits)
    {
        var span = data.AsSpan(offset);
        double sample;
        if (tag == 3)
            sample = bits == 32 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span));
        else sample = bits switch
        {
            8 => (span[0] - 128) / 128d,
            16 => BinaryPrimitives.ReadInt16LittleEndian(span) / 32768d,
            24 => Signed24(span) / 8388608d,
            32 => BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648d,
            _ => throw new InvalidDataException("Unsupported PCM depth.")
        };
        if (!double.IsFinite(sample)) throw new InvalidDataException("WAV contains nonfinite floating point samples.");
        return sample;
    }
    private static int Signed24(ReadOnlySpan<byte> span)
    {
        var value = span[0] | span[1] << 8 | span[2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xff000000) : value;
    }
}
