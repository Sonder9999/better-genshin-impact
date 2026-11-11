using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record AudioPlaybackWindow(PCMWave Wave, double SuggestedAudioStartMs, double AppliedAudioStartMs, string? SkipReason)
{
    public static AudioPlaybackWindow Apply(PCMWave complete, double requestedStartMs)
    {
        if (requestedStartMs == 0) return new(complete, 0, 0, null);
        if (!double.IsFinite(requestedStartMs) || requestedStartMs < 0 || requestedStartMs >= complete.DurationMs)
            return new(complete, requestedStartMs, 0, "InvalidAudioStartMs");
        var firstFrame = (int)Math.Floor(requestedStartMs * complete.Format.SampleRate / 1000);
        if (complete.FrameCount - firstFrame < Math.Ceiling(complete.Format.SampleRate * .15))
            return new(complete, requestedStartMs, 0, "RemainingAudioTooShort");
        if (firstFrame <= 0) return new(complete, requestedStartMs, 0, "BelowOneSampleFrame");
        var playable = PCMWave.Own(complete.Format, complete.Samples.Span[(firstFrame * complete.Format.Channels)..].ToArray());
        return new(playable, requestedStartMs, firstFrame * 1000d / complete.Format.SampleRate, null);
    }
}
