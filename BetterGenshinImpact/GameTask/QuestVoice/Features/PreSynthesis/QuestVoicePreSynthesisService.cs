using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.QuestVoice.Core.Configuration;
using BetterGenshinImpact.GameTask.QuestVoice.Core.Routing;
using BetterGenshinImpact.GameTask.QuestVoice.Core.Tts;
using BetterGenshinImpact.GameTask.QuestVoice.Features.Routing;
using BetterGenshinImpact.GameTask.QuestVoice.Features.Tts;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.GameTask.QuestVoice.Features.PreSynthesis;

/// <summary>单条剧情原文配音请求。</summary>
public sealed record QuestPreSynthesisLine(string Key, string Speaker, string Text);
/// <summary>后台预合成的逐条进度，不包含音频内容。</summary>
public sealed record QuestPreSynthesisProgress(string Key, string State, int Completed, int Total, int CacheHits, int Failed);
/// <summary>本次任务的真实合成统计。</summary>
public sealed record QuestPreSynthesisSummary(int Completed, int Total, int CacheHits, int Failed);

/// <summary>BetterGI 语音 Provider 驱动的安全离线预合成，独立 WAV 缓存，不引入视频测试功能。</summary>
public sealed class QuestVoicePreSynthesisService(
    QuestVoiceSessionFactory factory, QuestVoiceConfig config, QuestVoiceService liveService)
{
    /// <summary>缓存始终位于安装目录下的 User 区域。</summary>
    public string CacheDirectory => Path.Combine(factory.ProgramDirectory, "User", "QuestVoice", "cache", "pre-synthesis");

    /// <summary>串行生成当前选中原文，并使用设置编辑租约防止与实时配音竞争服务权重。</summary>
    public async Task<QuestPreSynthesisSummary> GenerateAsync(IReadOnlyList<QuestPreSynthesisLine> lines,
        string speechLanguage, Func<CancellationToken, Task> waitWhilePaused,
        IProgress<QuestPreSynthesisProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (speechLanguage is not ("jpn" or "chs"))
            throw new ArgumentException("仅支持 CHS/JP 剧情原文。", nameof(speechLanguage));
        Directory.CreateDirectory(CacheDirectory);
        using var pauseRealtime = liveService.BeginSettingsEdit();
        await factory.EnsureResourcesAsync(cancellationToken);
        RoutingCatalog? catalog = null;
        if (config.RoutingEnabled)
            catalog = (await new SpeakerCatalogLoader().LoadAsync(factory.RoutingCatalogPath,
                cancellationToken, factory.RoutingServerSidePaths)).Catalog;

        using var provider = factory.CreateRoutingProvider();
        var router = new SpeakerRouter();
        int completed = 0, hits = 0, failed = 0, failuresInARow = 0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await waitWhilePaused(cancellationToken);
            try
            {
                if (string.IsNullOrWhiteSpace(line.Text))
                    throw new InvalidDataException("当前目标语言台词为空。");
                RoutingVoice? mapped = null;
                if (catalog is not null)
                {
                    var route = router.Route(new SpeakerRoutingInput(line.Speaker, SpeakerNameStatus.Accepted), catalog);
                    mapped = catalog.Voices.FirstOrDefault(x => x.Id == route.VoiceId);
                    if (mapped is null && catalog.MissingVoicePolicy == MissingVoicePolicy.Fail)
                        throw new InvalidOperationException($"角色「{line.Speaker}」未分配声线；请先在角色映射中绑定。");
                    if (mapped is null && route.VoiceId is { Length: > 0 } missingId &&
                        missingId != RoutingCatalog.ConfiguredVoiceId)
                        throw new InvalidOperationException($"角色「{line.Speaker}」引用不存在的声线 {missingId}。");
                }

                var settings = mapped is null ? factory.CreateTtsSettings() : factory.CreateVoiceSettings(mapped);
                var language = speechLanguage == "jpn" ? "ja" : "zh";
                settings = settings with
                {
                    Protocol = (settings.Protocol ?? new TtsProtocolSettings()) with
                    { TextLanguage = language, PromptLanguage = language, StreamingMode = false, StreamPlayback = false }
                };
                var binding = new VoiceBinding(settings.Voice.Name, settings.Voice.GptWeightsPath,
                    settings.Voice.SovitsWeightsPath,
                    [new ReferenceAudio(settings.Voice.ReferenceAudioPath, settings.Voice.PromptText)],
                    settings.Provider);

                var fingerprint = string.Join("\n", new[]
                {
                    speechLanguage, line.Speaker, line.Text, settings.TtsServiceUrl.AbsoluteUri,
                    settings.Provider?.ProviderId ?? "", settings.Provider?.InstanceId ?? "",
                    settings.Provider?.VoiceId ?? "", settings.Extra?.Model ?? "",
                    settings.Extra?.Language ?? "", settings.Extra?.Format ?? "",
                    binding.Name, VersionOf(binding.GptWeightsPath), VersionOf(binding.SovitsWeightsPath),
                    VersionOf(settings.Voice.ReferenceAudioPath), settings.Voice.PromptText
                });
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
                var wavPath = Path.Combine(CacheDirectory, hash + ".wav");
                if (await IsValidCachedWavAsync(wavPath, cancellationToken))
                {
                    completed++; hits++; failuresInARow = 0;
                    progress.Report(new(line.Key, "缓存命中", completed, lines.Count, hits, failed));
                    continue;
                }

                progress.Report(new(line.Key, "正在合成", completed, lines.Count, hits, failed));
                // 不强制终止在途 GPT HTTP 请求，以免服务权重和排空状态不明。
                var result = await provider.SynthesizeAsync(settings,
                    new TtsRequest(line.Text, binding), null, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.SourceEncoding != TtsAudioEncoding.Pcm16Wav)
                    throw new InvalidDataException("所选引擎没有返回 PCM16 WAV，无法安全缓存。");
                _ = new WavProcessor().ValidateCompletedResponse(result.Wav);
                var temporary = wavPath + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    await File.WriteAllBytesAsync(temporary, result.Wav, cancellationToken);
                    File.Move(temporary, wavPath, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                completed++; failuresInARow = 0;
                progress.Report(new(line.Key, "已生成", completed, lines.Count, hits, failed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failed++; failuresInARow++;
                progress.Report(new(line.Key, "失败：" + error.Message, completed, lines.Count, hits, failed));
                if (error is VoiceException { BackendUnknown: true } || failuresInARow >= 5)
                    throw new InvalidOperationException("后端状态不确定或连续五条合成失败；已停止后续请求。", error);
            }
        }
        return new(completed, lines.Count, hits, failed);
    }

    /// <summary>模型仅比对文件大小、修改时间与路径，不反复哈希 GB 级权重。</summary>
    private static string VersionOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : path;
        }
        catch (Exception) { return path; }
    }

    /// <summary>只有现有 WAV 完整通过格式检查才标记缓存命中。</summary>
    private static async Task<bool> IsValidCachedWavAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        try
        {
            _ = new WavProcessor().ValidateCompletedResponse(await File.ReadAllBytesAsync(path, token));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VoiceException or InvalidDataException)
        {
            return false;
        }
    }
}
