using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public sealed record WaveOutSnapshot(uint DeviceId, string DeviceName, AudioFormat Format, int BufferFrames,
    int Buffers, long Generation, long QueuedFrames, long MaxQueuedFrames, long SubmittedBuffers,
    long DiscardedBlocks, long Resets, bool Paused, int NativeBuffers, bool Initialized,
    long PlayedTailFrames = 0, long PlayedOverlapFrames = 0, long SubmittedTailFrames = 0);
