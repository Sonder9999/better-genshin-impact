using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public static class WaveReader
{
    public static WaveInfo Read(string path)
    {
        var errors = new List<string>();
        ushort? tag = null, channels = null, align = null, bits = null;
        uint? rate = null, byteRate = null;
        long dataBytes = 0;
        bool hasData = false, hasFmt = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            if (stream.Length < 12) throw new InvalidDataException("Truncated RIFF header");
            if (FourCc(reader) != "RIFF") throw new InvalidDataException("Expected little-endian RIFF");
            long end = reader.ReadUInt32() + 8L;
            if (FourCc(reader) != "WAVE") throw new InvalidDataException("Expected WAVE form");
            if (end < 12) throw new InvalidDataException("Invalid RIFF length");
            if (end > stream.Length) errors.Add("RIFF length exceeds file (truncated)");
            if (end < stream.Length) errors.Add("Bytes after declared RIFF end");
            var limit = Math.Min(end, stream.Length);
            while (stream.Position < limit)
            {
                if (limit - stream.Position < 8) { errors.Add("Truncated chunk header"); break; }
                var chunk = FourCc(reader);
                var size = reader.ReadUInt32();
                long start = stream.Position, chunkEnd = start + size, paddedEnd = chunkEnd + (size & 1);
                if (chunkEnd > limit) { errors.Add($"Truncated {chunk} chunk: declared {size} bytes"); break; }
                if (chunk == "fmt ")
                {
                    if (hasFmt) errors.Add("Duplicate fmt chunk");
                    hasFmt = true;
                    if (size < 16) errors.Add("fmt chunk shorter than 16 bytes");
                    else
                    {
                        tag = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadUInt32();
                        byteRate = reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                        ushort effectiveTag = tag.Value;
                        if (tag == 65534)
                        {
                            if (size < 40) errors.Add("Truncated extensible fmt");
                            else
                            {
                                var extension = reader.ReadUInt16(); var validBits = reader.ReadUInt16(); reader.ReadUInt32();
                                var subformat = new Guid(reader.ReadBytes(16));
                                effectiveTag = subformat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1 :
                                    subformat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
                                if (extension < 22 || extension + 18L > size || validBits == 0 || validBits > bits) errors.Add("Invalid extensible fmt fields");
                            }
                        }
                        if (effectiveTag != 1 && effectiveTag != 3) errors.Add($"Unsupported/invalid format tag {tag}");
                        if (channels == 0 || rate == 0 || byteRate == 0 || align == 0 || bits == 0) errors.Add("Zero fmt rate/channel/alignment/bit field");
                        if (bits % 8 != 0 || align != channels * (bits / 8)) errors.Add("Block alignment inconsistent with channels/bits");
                        if (byteRate != (long)rate * align) errors.Add("Byte rate inconsistent with sample rate/alignment");
                        if (effectiveTag == 1 && bits is not (8 or 16 or 24 or 32)) errors.Add("Unsupported PCM bit depth");
                        if (effectiveTag == 3 && bits is not (32 or 64)) errors.Add("Invalid float bit depth");
                    }
                }
                if (chunk == "data")
                {
                    if (hasData) errors.Add("Duplicate data chunk");
                    hasData = true; dataBytes += size;
                }
                if (paddedEnd > limit) { errors.Add($"Missing odd-byte padding after {chunk}"); break; }
                stream.Position = paddedEnd; // Seek over PCM; memory usage does not scale with audio length.
            }
            if (!hasFmt) errors.Add("Missing fmt chunk");
            if (!hasData) errors.Add("Missing data chunk");
            if (hasData && align > 0 && dataBytes % align != 0) errors.Add("Data length not aligned to sample frames");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { errors.Add($"{ex.GetType().Name}: {ex.Message}"); }
        return new WaveInfo(Path.GetFullPath(path), errors.Count == 0, tag, channels, rate, byteRate, align, bits,
            dataBytes, byteRate > 0 ? dataBytes / (double)byteRate.Value : null, errors);
    }

    private static string FourCc(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
