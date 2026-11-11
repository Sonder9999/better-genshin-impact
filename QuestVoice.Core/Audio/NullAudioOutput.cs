namespace QuestVoice.Core;

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
