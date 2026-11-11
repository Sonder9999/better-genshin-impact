using System.Globalization;
using System.Text;

namespace QuestVoice.Core;

internal static class FinalReportMarkdown
{
    public static string Summary(FinalProjectReport report)
    {
        var b = new StringBuilder("# TestPreVoice 最终研究报告 / Phase 8 · Task 9\n\n");
        b.AppendLine("**结论：工程测试与评测执行已有证据，当前没有可证明达到生产识别质量的方案。** Phase 1–7 机器结果已只读汇总；冻结事件尚未正式发布，准确率、切句 P95 和四维 Pareto 边界不可判定。L1 目标 WAV 启动为 0 次，不能替代有分母的正式误播率。\n");
        b.AppendLine("## Phase 1–7 实测汇总\n");
        b.AppendLine("| 阶段 | 实际输入/观察 | 结论边界 |\n| --- | --- | --- |");
        b.AppendLine("| 1 素材审计 | 4 媒体、6 JSON、110 任务、1990 节点、2000 边；1000/1000 WAV 格式有效；2000/2000 双语绑定覆盖；51 问题 | 双语绑定不是独立音频数；48 重复文本、3 文件名/章节错位保留 |\n| 2 真值与 ROI | Dev 3163 帧；Validation 5617 原始帧；20 冻结事件 pending/excluded，正式覆盖 0 | 覆盖区间完整不等于有效事件标签 |\n| 3 A/B/C | Dev 候选 12/10/5；S1 探索性首字 F1 0.4/0.4/0；首字 P95 0/0/N/A | 仅一次复核、3 首字样本；清空与首字分别计分 |\n| 4 D/E/F | D/E 可靠确认 0/0；OCR 367 次；F 屏蔽 947/3163 帧；回放 165.147s | 屏蔽数没有正确性标签；字体模板路线暂不推荐 |\n| 5 剧情图 | 1990 节点、2000 边、110 任务、10 选择节点；14 空台词节点禁止播放；58/58 QuestTests | 图边完备，文本完备性 false；按图后继定位，不能 index++ |\n| 6 Fusion | 四消融均处理 3163 帧；Full/No-UIGate/No-Vision 首字 0，No-QuestGraph 15；音频开始均 0 | No-QuestGraph 只有视觉身份；S1 探索性 P95 66.756ms、3 样本，不能代表可靠配音 |\n| 7 冻结验证 | S2 754、S3 663、L1 1500、L2 后半 2700，共 5617 帧/93.685644s；原有 11 入口退出 0 | 算法参数未调验证集；正式 P/R/F1、身份准确率、错播率均 N/A |\n");
        b.AppendLine("每阶段完整数值、参数、消融与来源均在 [FINAL_REPORT.json](FINAL_REPORT.json) 的 `phases` 中。既有研究预测与冻结标签未改写。\n");
        b.AppendLine("## 七方法同输入对照\n");
        b.AppendLine("准确率按任务解释：事件使用 P/R/F1，节点使用 top-1，UI 使用独立状态标签；不同任务不能合并为同一准确率。下表为 native 原始帧，计算耗时单位 ms/帧。\n");
        b.AppendLine("| 方法 | 任务语义 | 目标事件数 | 正式准确率/F1 | 切句 P95 ms | 计算 P50 ms | 计算 P95 ms | CPU ms / 内存 |\n| --- | --- | ---: | --- | --- | ---: | ---: | --- |");
        foreach (var m in report.Methods)
            b.AppendLine($"| {m.Name} | {m.Task} | {m.TargetEventCount} | N/A | {N(m.CutLatencyP95Ms)} | {N(m.ComputeP50Ms)} | {N(m.ComputeP95Ms)} | N/A / N/A |");
        b.AppendLine("\nA/B 是旧字清空，C 是结构候选，D/E 是节点确认，F 是 UI 事件，Fusion 是确认首字。Fusion 原始状态事件 18 条，其中首字事件 0，不能把状态数量写成切句数量。共享 glyph/OCR 耗时在 D/E/Fusion 中有交叠，不能相加。\n");
        b.AppendLine("## Pareto 分析与备选方案\n");
        b.AppendLine("采用四维支配规则：同任务、完整测量；准确率最大化，非负切句 P95、CPU、内存最小化；全部不劣且至少一维严格更好才支配。相同点及质量/延迟权衡点保留。null 不插值、不补 0，不跨任务比较。算法的方向、严格性、缺失值及任务隔离均有 RED→GREEN 测试。\n");
        b.AppendLine($"**本次完整四维结果：合格点 {report.Pareto.Eligible.Count}，边界点 {report.Pareto.Frontier.Count}，缺失维度排除 {report.Pareto.Excluded.Count}。因此无法宣布全局 Pareto 最优方法。** JSON 保存每个排除原因与支配关系；这不是空算法，而是数据不足时的正确结果。\n");
        b.AppendLine("可比较的计算成本子问题：A/B 同为清空候选，A 的 P95 0.4644ms 低于 B 的 0.6000ms；D/E 同为身份基线，E 的 1.3831ms 低于 D 的 2.0965ms，但两者均 0 可靠确认。单一成本更小不证明质量更好。C 的结构候选、F 的 UI 和 Fusion 的端到端任务单列。\n");
        b.AppendLine("首选架构方向为 A/B/C 快速候选 + F 场景门控 + 经验证的日语 OCR + QuestGraph + generation AudioManager。当前 OCR 与完整 Fusion 尚无启用证据，应继续作为独立实验。第二备选为 A + F + 已可靠锁定且唯一后继的剧情图；只有观察到新句首字、身份可靠时才播，分支/恢复依赖可靠语义信号，未知时静音。清空召回高不能直接授权播放。\n");
        b.AppendLine("## 采样、资源与真实性\n");
        b.AppendLine("| 原始采样 ID | 帧数 | 独立执行 | 实际 FPS | L1 目标启动 | OCR 次数 |\n| --- | ---: | --- | ---: | ---: | ---: |");
        foreach (var s in report.Sampling)
            b.AppendLine($"| {s.Name} | {s.Frames} | {(s.IndependentExecution ? "是" : "派生复用")} | {s.EffectiveFps.ToString("F4", CultureInfo.InvariantCulture)} | {s.L1TargetStarts} | {s.OcrCalls} |");
        b.AppendLine("\nID `60` 是原始网格控制，不是完整 60FPS；`60_source_reference` 复用 native，资源不重复累计。没有插帧，没有用未来帧或真值初始化识别器。\n");
        b.AppendLine($"机器 JSON：共享 .NET 墙钟 {report.SharedResources.WallMs / 1000:F6}s，CPU {report.SharedResources.CpuMs / 1000:F6}s，观测峰值工作集 {report.SharedResources.PeakWorkingSetBytes / 1048576d:F2}MiB，逻辑处理器 {report.SharedResources.LogicalProcessors}。范围包含模板生成、四独立采样与汇总，FFmpeg 逐片另列。不能拆分为单方法 CPU/内存，也不能以离线计算 P95 代替字幕首字到实际出声延迟。\n");
        foreach (var c in report.Conflicts)
            b.AppendLine($"来源冲突 `{c.Field}`：JSON={c.JsonValue.ToString("G", CultureInfo.InvariantCulture)}，旧 Markdown={c.MarkdownValue.ToString("G", CultureInfo.InvariantCulture)}。本报告使用 JSON 的 102.890625s，原 Markdown 的 111.66s 保留，未改写来源。\n");
        b.AppendLine("## 示例、交接与复现\n");
        b.AppendLine("关键限制示例：S1 A/B 首字只有 1/3 命中，不能以清空 2/2 代替首字质量；D/E 零确认使 Fusion 保持 UNKNOWN；L1 路人期间零目标启动同时可能掩盖漏播。所有未计分候选、OCR 原文及视频 PTS 位于 [验证结果](validation_eval.json) 的轨迹链接与 unscored 清单。没有合法标签时不伪造 TP/FP/FN。S1 与 L2 后半有相同台词/场景，时间划分不能证明内容独立。\n");
        b.AppendLine("零侵入集成的接口、取消语义、性能预算和人工验收见 [SDD 技术交接](../SDD/20-TASK9-FINAL-REPORT.md)。只交付本实验原型与指南，未改 QuestVoice、TTS、生产播放协调器或主分支。\n");
        b.AppendLine("```powershell\n# 仅在 D 盘 test_pre_voice 中执行\n. ./.runtime/task4-env.ps1\n$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $pwd 'cache/nuget/plugins'\n$env:DOTNET_EnableDiagnostics = '0'\n$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'\ndotnet run --project tests/TestPreVoice.ReportTests -c Release -p:UseSharedCompilation=false -p:BuildInParallel=false\ndotnet run --project tools/TestPreVoice.FinalReport -c Release -p:UseSharedCompilation=false -p:BuildInParallel=false\n```\n");
        b.AppendLine("生成过程只读取既有 JSON/标签，不重解码、不下载、不用 Python。三个新报告的逐项字节数与总量由 CLI 输出记录在 `logs/task9/delivery.json`；没有复制视频或 WAV。每个来源的完整 SHA256/字节数在 `provenance`。原有 138 个受保护文件另由阶段完整性记录核对；系统组件内部活动未作进程级审计。\n");
        b.AppendLine("需要独立人工验收的研究项：发布合法冻结标签、内容去重、可靠日语 OCR、真实 UI/身份标签、真实速度视频与声卡/听感同步。Phase 9 可先以可靠确认的定向事件验证音频截断机制；不能把该音频工程成功用作识别准确率证据。\n");
        return b.ToString();
    }

    public static string Guide(FinalProjectReport report) => $$"""
        # Task 9 / Phase 8 交接与 QuestVoice 零侵入架构指南

        机器报告为 [FINAL_REPORT.json](../results/FINAL_REPORT.json)，研究结论为 [FINAL_REPORT.md](../results/FINAL_REPORT.md)。已聚合 {{report.Phases.Count}} 个阶段、{{report.Methods.Count}} 个方法；完整 Pareto 合格点 {{report.Pareto.Eligible.Count}}。正式质量仍受 pending/excluded 标签限制。当前原型不具备自动启用生产配音的证据。

        ## 模块边界

        ```mermaid
        flowchart LR
          Frame[原始 PTS 帧与 ROI] --> Fast[A/B/C 视觉候选]
          Frame --> Gate[F 对话与选项状态]
          Frame --> OCR[按需日语 OCR]
          Fast --> Fusion[FusionEngine]
          Gate --> Fusion
          OCR --> Graph[QuestGraph / LineResolver]
          Graph --> Fusion
          Fusion --> Event[不可变确认事件]
          Event --> Adapter[独立适配器]
          Adapter --> Audio[AudioManager / generation]
          Index[只读 WAV 索引与有界缓存] --> Audio
          Audio --> Device[可关闭的原生音频设备]
        ```

        视觉切句检测器回答“何时”，图/文本定位器回答“哪一句”，F 回答“什么 UI”；三者不能互相代替。AudioManager 只消费可靠确认事件，不依赖 OCR、游戏自动操作、TTS 或 BetterGI 运行时。原型先在本实验根完成，未来正式接入仅增设宿主适配层与独立开关，保留 QuestVoice 当前实时合成协调器的既有语义。

        可只读借鉴 BetterGI 的日语 OCR、UI/ROI 捕获、角色配置和 QuestVoice 索引能力；不要整体引用生产程序集或复制其全局缓存/服务初始化。适配层把这些能力转为原型契约，目录、日志与依赖显式注入。当前不修改主仓、不创建 PR、不 merge/push。

        ## 事件与取消协议

        | 输入事件 | 音频动作 | 必须保留的保证 |
        | --- | --- | --- |
        | CandidateClear | 有界、可取消预取候选 | 不打断 A、不自动播放 B |
        | ConfirmedNewLine | 同步递增 generation 并作废旧请求，再异步读取最新 WAV | A 不等播完；B 的返回重新检查 generation |
        | UnknownContext / NonQuestDialogue | 立即撤销、停止、静音 | 不将旧任务音频带入 NPC |
        | ChoiceWaiting | 保持当前有效句或自然结束 | 不重播、不推进图、不循环 |
        | DialogueExit / UserStop | 取消加载/预取、设备队列与缓存，清理会话 | 重入必须重新定位；旧回调不可复活 |
        | Pause / Resume | 只操作仍有效的当前 generation | 新句到来后 Resume 不复活 A |
        | 定位纠错 | 撤销错误 generation；正确节点经确认后独立开始 | 已听见的错音仍计错播，不能抹去 |

        适配层带 clipId、sourcePtsMs、nodeId、wavePath、eventId 和单调事件序号；同节点 OCR 重报/逐字增长去重。每个控制事件及时派发，异步读取不阻塞视觉线程，不用 FIFO 排队补播过时台词。读完、预取完成、设备回调均检查有效 generation。A→B→C 时 B 永远不能在 C 之后补播。

        预合成 WAV 与现有实时 TTS 的准备条件不同：可靠 B 无 WAV/损坏时停止 A 后静音并记录 Failed，不延长 A 冒充 B。普通台词与 player_optN 变体保持索引键隔离；空台词禁止播放，重复文本通过图 node ID 和上下文区分。

        ## Phase 9 设备与性能预算

        独立 .NET 8 BCL 原型采用 Windows WinMM waveOut P/Invoke，无额外包。可替换设备接口允许确定性 PCM 渲染测试；默认真实播放关闭，显式设备回放才测设备时钟。WAV 只读解码/重采样与输出线程解耦。音频使用单调墙钟和设备样本时钟，视频保留原始 PTS。

        预算需实测：48kHz 立体声输出、5ms 块、最多 10ms 已排队音频；默认淡出 20ms、允许配置 0–30ms；最多一个当前有效节点及一个短淡出尾段，不能长交叉淡化或叠加多段旧尾音。有界解码/缓存，初始缓存预算 32MiB；文件版本参与缓存键，不常驻约千个 WAV。确认接口先失效旧世代，再异步加载，不能等待文件读完才取消 A。

        日志区分 prepare/start/fade/stop/cancel/finish/error、generation、事件来源 PTS、已播/完整时长、取消原因、设备 outputClockMs 与 observedLatencyMs。设备的 Play 调用、PCM 生成或提交时间都不能冒充真实出声。waveOut 样本位置只能作为设备输出观察；没有麦克风/回环测量时，声学扬声器延迟保持未测。

        专项样本：真实 WAV 正常结束；A 50%→B；A→B→C 的迟到加载；仅 CandidateClear；重复节点/不同选项；无源/坏 WAV；选项等待；UnknownContext/NPC/退出/用户急停；暂停后新句与恢复；设备错误和销毁后回调。两种策略（立即截断与短淡出）分别记录重复/过期启动、尾音残留、交叠、启动/取消 P50/P95、加载/解码开销。确定性渲染与真实设备报告分别标注。

        ## 回归与未来落地条件

        复现报告：环境重定向后运行 `tests/TestPreVoice.ReportTests` 和 `tools/TestPreVoice.FinalReport`。Task 10 在独立 SDD 计划中先提供可审查设计、RED 日志，再实现 `src/TestPreVoice.Core/AudioManager.cs` 和 `tests/TestPreVoice.AudioTests`。最终将原 11 个测试入口、新 ReportTests、AudioTests 逐一串行回归并保留全部退出码。

        正式启用前仍需发布冻结事件/UI/节点真值、解决当前 D/E 无可靠确认、检查场景重复、进行真实速度视频回放与听感验收。音频截断单元测试不能证明识别准确率；零目标启动也不能证明零漏播。未测项在交接时保留，不把实验工程完成写成业务质量达标。
        """ + "\n";

    private static string N(double? value) => value?.ToString("F4", CultureInfo.InvariantCulture) ?? "N/A";
}
