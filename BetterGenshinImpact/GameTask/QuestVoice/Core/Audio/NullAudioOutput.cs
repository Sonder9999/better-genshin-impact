using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed class NullAudioOutput : IAudioOutput
{
    public double? OutputClockMs => null;
    public void Initialize(AudioFormat format, AudioRenderCallback render, Action<AudioOutputEvent> observe)
    { PCMWave.ValidateFormat(format); ArgumentNullException.ThrowIfNull(render); ArgumentNullException.ThrowIfNull(observe); }
    public void Flush(long generation) { }
    public void Pause(long controlVersion = 0) { }
    public void Resume(long controlVersion = 0) { }
    public void Dispose() { }
}
