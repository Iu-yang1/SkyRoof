# CW Console：多路 Pileup 解码与自动拍发实施规划

**状态：接收核心正在量化验证。** 仓库现已具备多载波检测/跟踪、双分辨率 Frame-level Ridge Scanner、约 360 ms fixed-lag beam/MHT、多 Lane 独立 ONNX/CTC 推理、活动感知干扰 mask、MergeGroup fallback，以及按 CTC 时间位置进行的连续 Transcript 协调。尚未完成的是 CW Console 的实时 SDR/WASAPI/RS-BA1 PCM 接线、HamNoise 集成，以及任何电台发射功能。

参考：[deepcw-engine](https://github.com/e04/deepcw-engine) 是**正式 CW 解码模型**，包含 model.onnx、model.onnx.json 与推理示例；[web-deep-cw-decoder](https://github.com/e04/web-deep-cw-decoder) 提供多路检测与 Pileup 结构参考；[HamNoise](https://github.com/e04/HamNoise) 提供可选神经降噪 C 核心。英文方案见 [English plan](../../fork-guide/cw-console-plan.md)。

## 核心约束

- 完整 Pileup = 多路频点自动检测 + 稳定 Track ID + **宽带时频前端 / 每路独立分量抽取 + DeepCW 模型推理** + 每路独立连续文字；只识别多峰而只解码一路不能验收。
- deepcw-engine 的公开模型采用 3,200 Hz 音频、FFT 256 / hop 48、400–1200 Hz 65 bins、[1,1,T,65] 张量、42 类 CTC 文字。须读取模型元数据，不混用网页端 9,600 Hz 或尚未随独立引擎发布的检测/窄带专用模型。
- 无法保证分离同频同时间重叠的两个 CW 电台。遇到不可分离信号应标记干扰，不编造第二路转录。
- Icom LAN Scope 数据是频谱帧，不是连续 PCM。RS-BA1 接收音频应走用户选定的 WASAPI / Loopback，不再争用 CI-V/LAN 音频连接。
- SkyRoof 与 HamNoise、deepcw-engine 均使用 AGPL-3.0 系列许可，应保留声明和模型版权；网页端 UI 参考布局但不复制授权未明的 React 源码。

## Pileup：多目标跟踪 + 多分量时频分离

Pileup 不再按“每帧峰值最近邻 + 固定矩形滤波”设计，而采用以下组合：

1. **多目标 ridge 状态**：每个 CW 分量维护 \`[f, df/dt]\` 和协方差，用 Kalman 常速度模型跟踪音频频率与 Doppler 漂移。
2. **全局数据关联**：每个分析时刻对全部 Lane 与全部候选峰统一求最小代价关联（GNN），避免强台/弱台功率交换造成 Track ID 互换。
3. **出生、QSB coast、死亡**：新峰建立 tentative track；短时漏检继续预测而不立即删除；超时后才终止。近同频只出现一个峰时视为 merged measurement，未被分配的另一轨继续 coast。
4. **Ambiguous / collision**：预测 ridge 间距低于可分辨阈值时保留两个 Track ID，但标记 Ambiguous；UI 和日志不能把这一状态宣称为已可靠分离。
5. **activity-aware competing mask**：每条 Confirmed Track 另外维护逐帧载波活动概率，由 ridge 能量经两状态 HMM 得到；Confirmed 但当前停键的台站不会继续无条件瓜分其它 Lane 的时频能量。软掩膜仍沿预测 ridge 使用 Gaussian/Wiener-like 权重，并结合 Kalman 频率不确定度。
6. **AllDetectedTracks 与 DecodeSelectedTracks 分离**：全部可靠 Track 都参加干扰/竞争 mask；只有按资源预算选中的默认 5、最多 8 路执行 ONNX，未选中的强邻台不会从分母中消失。
7. **校准的宽带共享 STFT**：不再先把整段接收音频降到 3.2 kHz。48 kHz PCM 使用 3840 点 FFT / 720 点 hop，对应与 DeepCW 官方 256 / 48 完全相同的 80 ms 窗、15 ms hop 和 12.5 Hz 格点，并按 FFT 长度比例校准幅度后再映射到模型 400–1200 Hz 输入。因此 1600 Hz 以上的 AF CW 载波不会在抗混叠阶段提前丢失。
8. **MergeGroup fallback**：有限分辨率导致两个 ridge 形成一个峰时，tracker 内部仍显式保留 MergeGroupId、pre-merge ridge anchor，并在共享单峰期间膨胀协方差，作为 MHT 之外的短时保护层。
9. **双分辨率 Frame Scanner**：Fast STFT 使用 80 ms 窗 / 15 ms hop，仅提供活动状态和短 ridge portion 连续性证据，明确禁止作为逐帧 Kalman measurement；Precision STFT 使用 240 ms 窗 / 120 ms hop，输出采样序号对齐的频率、SNR、分辨率、测量方差和活动概率，是唯一送入 tracker 的帧级测量流。
10. **相关性与 chirp 感知的测量方差**：Precision measurement sigma 会针对重叠窗相关性和 240 ms 窗内残余 chirp smear 膨胀；可选输入公共 Doppler rate，在 STFT 前进行去 chirp，输出频率再映射回原始 AF 频率轴。
11. **真正的 fixed-lag 多帧关联**：Precision batch 在进入 Kalman/GNN 前先通过默认 3 个未来 batch（约 360 ms）的 bounded beam/MHT。它同时保留多组全局关联假设，根据频率创新、局部速度、速度变化、SNR、活动概率和 ridge-portion 连续性累计代价；只在 lag 到期后为最老 batch 提交稳定的 `AssociationHintId`。新轨至少需要两帧未来支持，单帧 clutter 不允许直接 birth。
12. **借鉴 RRP 的短可靠 ridge portion，而非照搬 MATLAB**：Fast 局部极大值先按前后连续性连接成短 ridge portion，再交给长期跟踪；不移植 Laurent/Meignen 的 basin/spline 优化，也不采用其 spline 不允许交叉的限制，因此 SkyRoof 仍支持真实的频率交叉。

该设计吸收了以下公开研究的思路，但不是逐行复现论文算法：

- Wang, Jiang, Zhang, *Random finite set approach to analyzing, detecting, and tracking dynamic time-frequency spectra*（2019，DOI 10.7527/S1000-6893.2018.22600）：将动态时频谱建模为多目标跟踪，处理分量出生/消失、弱分量与近邻模式。
- Meignen, Pham, McLaughlin, *On Demodulation, Ridge Detection, and Synchrosqueezing for Multicomponent Signals*（IEEE TSP 2017，DOI 10.1109/TSP.2017.2656838）：ridge 提取、demodulation 与 mode reconstruction。
- Laurent, Meignen, *A Novel Ridge Detector for Nonstationary Multicomponent Signals*（IEEE TSP 2021，DOI 10.1109/TSP.2021.3085113）：噪声下稳健 ridge detection / mode retrieval。
- Meignen, Laurent, Oberlin, *One or Two Ridges? An Exact Mode Separation Condition for the Gabor Transform*（IEEE SPL 2022，DOI 10.1109/LSP.2022.3226948）：STFT 分辨率不足时两个纯音本来就可能只形成一条 ridge，因此软件必须显式表示“不可分辨/合并”，不能强制生成两个解码结果。
- García-Fernández et al., *Bayesian Multi-Target Tracking With Merged Measurements Using Labelled Random Finite Sets*（IEEE TSP 2015，DOI 10.1109/TSP.2015.2393843）：近邻目标产生 merged measurement 时应保持轨迹存在性，而不是直接删除未匹配轨迹。

13. **Incremental CTC / Transcript**：DeepCW 保留每个字符的 CTC `OutputFrame`、输出帧总数和 top-vs-runner-up 置信度；重叠解码窗口按绝对时间进行顺序约束对齐和多窗口投票。文本分为不可回退的 `CommittedText` 与可纠正的 `ProvisionalText`，至少两个独立窗口确认且超过 commit lag 后才进入稳定前缀。优先用 `AssociationHintId` 作为 Lane 身份，因此 TrackId 重建或 crossing 后文字仍跟随同一多帧轨迹。

SkyRoof 当前采用 **Frame Ridge Scanner → bounded fixed-lag beam/MHT → labelled Kalman/GNN → MergeGroup fallback**，而不是完整 GM-PHD/GLMB。默认只保留约 360 ms 的未来 Precision observation 和有限数量全局假设，因此能在 5–8 路桌面场景中处理 crossing / merge，又不会引入完整 GLMB 的状态爆炸。只有端到端基准仍显示明显 ID switch 时，才需要考虑更重的 MHT/GLMB。

## 量化验收与真实 ONNX 基准

CW 算法变更除了普通单元测试，还必须运行专门的真实 `deepcw-engine` ONNX Pileup benchmark。第一层采用 oracle Track，把“分离器 + 模型”的误差与 detector/tracker 的 ID 错误隔离开，分别记录 mask 后与 unmasked baseline 的 CER、WER、完整呼号识别率和 Real-Time Factor。场景覆盖固定 5 / 10 / 15 / 25 / 40 Hz 间隔、强弱台功率差、0–20 Hz/s Doppler，以及 1600 Hz 以上宽带 AF Lane。

Frame-level Scanner 另外通过单元测试验证单调 sample-index 时间轴、Fast/Precision 分工、近频分辨、静默期间 coast 时间推进和 Doppler 去 chirp。连续 Transcript 还用真实 ONNX 的 40 Hz 双 Lane、6 秒窗/1 秒 hop 滑动基准同时记录 naive 字符串拼接 CER 与稳定 Transcript CER/WER，从而量化窗口去重和字符纠错的实际收益。下一层端到端 corpus benchmark 将继续统计 detection recall / false alarm、Frequency RMSE、ID switch、最终 CER/WER、呼号准确率和延迟。**Track ID 更稳定并不自动等于解码更好**：如果 fixed-lag 降低 ID switch 却明显增加 CER 或实时延迟，则不能作为默认配置直接验收。

## 完整工作流程表

| 编号 | 模块 / 依赖 | 必须实现 | 验收 |
|---|---|---|---|
| 00 | 冻结基线 | SkyRoof/SkyCAT SHA、现有 CI、许可和回滚计划 | FT4、频谱、转台仍正常 |
| 01 | 音频输入 | SDR Slicer、WASAPI、RS-BA1 播放设备 Loopback | 切换/拔插无卡死；音频线程不堵塞 |
| 02 | PCM 引擎 | Float32、有界队列、采样序号单调时间轴、保留 48 kHz 宽带 PCM；仅在每路模型入口映射到 DeepCW 特征域 | 断流恢复、积压受限、>1.6 kHz Lane 不丢失 |
| 03 | Frame Ridge Scanner | Fast 80/15 ms ridge portions + Precision 240/120 ms observation、sample-index 时间轴、Doppler 去 chirp | Fast 帧不反复压缩 Kalman 协方差；近频与静默 coast 均有回归测试 |
| 04 | 多帧关联 + 载波跟踪 | 3-batch / ~360 ms beam-MHT、AssociationHintId、Kalman/GNN、MergeGroup fallback、最多 8 路 | crossing、单峰 merge、单帧 clutter、ID swap 回归测试 |
| 05 | 多路隔离 | 每 Lane 50–300 Hz 带通、NCO 频移到 DeepCW 可识别音调 | 3–5 路可独立提取 |
| 06 | 多路推理 + 连续转录 | ONNX Runtime + 元数据 STFT/log1p/CTC；OutputFrame 时间对齐；Committed/Provisional；AssociationHintId 归属 | 重叠窗口不重复；误字可在提交前纠正；重复字符不误合并；每 Lane 独立 |
| 07 | 性能调度 | 默认最多 5 路，上限 8；选中路优先，超过容量主动降级 | CPU/RAM/延迟有上限 |
| 08 | 降噪 | HamNoise C DLL、Raw/BYPASS/Wet-Dry、监听链 | 前后 CER 可对比，一键旁路 |
| 09 | WinForms UI | DockContent、工具栏、音频瀑布、Pileup 表、RX 转录、TX 宏及状态栏 | GitHub Light/Dark 与粉蓝白主题，停靠布局保存 |
| 10 | SkyCAT CW | 受控 CW_SEND/CW_ABORT/CW_SPEED；IC-9700 CI-V 17 和 17 FF | 30 字符分包、异常、ACK、超时模拟测试 |
| 11 | TX 状态机 | Idle→Armed→Queued→Sending→Stopping/Failed，F1–F8 宏、WPM、Break-in | 默认 TX 关；STOP 优先清队列；ACK 不冒充拍发完成 |
| 12 | 卫星联动 | SAT MAIN 接收 / SUB 发射、CW/CW-R、当前上下行及 Doppler、PTT 所有权 | 错 VFO/外部 PTT/切卫星阻止 TX |
| 13 | 综合验收 | AWGN、QSB、1/10/30 Hz 扩展、0–20 Hz/s 漂移、3/5/8 路 CER | Windows CI + 模拟 CAT + 实机分阶段验证 |

**里程碑：** 00→01→02→03→04→05→06→07→09 完成**真实多路接收**；02→08 提供降噪；10→11→12 才启用发射；13 负责全量回归。只完成 04 不等于支持 Pileup 解码。

## CW Console UI 规范

| 布局 | 内容 | 交互 |
|---|---|---|
| 顶栏（约 38px） | RX Start/Stop、源选择、Raw/HamNoise、Single/Pileup、模型状态 | RX 与 TX 完全分开 |
| 音频频谱/瀑布（约 150px） | 100–2000 Hz、频点标签、选中路高亮 | 左键锁定/选择 lane；右键解锁/静音 |
| Pileup 表（主要区域） | Lane、AF Hz、检测 SNR、漂移 Hz/s、状态、最近呼号/文本 | 3–8 路独立文字；按频率/SNR 排序不改变 ID |
| RX Transcript（可拖动） | 所选通道确定/暂定文字、UTC、Copy、QSO Entry | 暂定文字不得用于自动拍发 |
| TX Composer（底部） | 文本、F1–F8 宏、WPM、Break-in、Arm、Send、醒目的 Abort | 窄窗也必须保留 Abort；未 Arm 禁止发送 |
| 状态栏 | 有效采样率、模型状态、延迟、活动 Lane 数、CAT/TX 状态 | 错误与降级清晰可见 |

基于现有 SkyRoof Context/MainForm/Ft4ConsolePanel 模式注册一个 DockContent；使用现有主题调色板，不复制网页 JSX。持久化音频源、宏、路数、门限和布局，但**不持久化 TX Armed 状态**。

## 解码管线

SDR PCM / 选定 WASAPI / RS-BA1 Loopback → 48 kHz 有界宽带音频 Hub →（可选 HamNoise）→ Fast 80/15 ms ridge portions + Precision 240/120 ms observations → 3-batch bounded beam/MHT → 带 AssociationHintId 的 Precision batch → CwPileupTrackManager / Kalman/GNN / MergeGroup fallback（空 batch 也推进 coast 时间）→ 校准宽带 STFT + activity-aware all-track soft mask（经典解码器/监听可另走每 Lane DDC）→ deepcw-engine ONNX → 带 OutputFrame/置信度的 CTC → Incremental Transcript（稳定前缀 + provisional 后缀）→ Pileup 列表/选中路文本/QSO 辅助。

候选 AF 频率不等于 RF 下行频率；正确的接收音调到射频转换取决于 CW/CW-R 与解调方式。音频推理不得直接操作 TX VFO。

## 发射安全和验证

SkyCAT 现有 4532 主 CAT 通道独占 RS-BA1 虚拟 COM 并串行仲裁，新增受限 CW 命令，不给 4537 Remote Control Switch 发送/PPT/任意 CI-V 权限。IC-9700 文本自动拍发须遵照 CI-V 17 ASCII 最多 30 字符和 17 FF Abort，设备 ACK 不代表真正发完。失联不能确认 Abort 被电台收到时应显示“TX 状态未知”，禁止自动恢复排队发射。必须人工解锁并确认正确卫星、TX VFO、模式、PTT 所有权后方可发送。

验收用多个混合 CW WAV 测 per-lane CER、误检率、丢路率、串扰率及音频->文字延迟。涵盖 10/20/30/40 WPM，3/5/8 路，低 SNR、QSB、频率扩展、多普勒、邻频及不可分离同频，并作 Raw/HamNoise 对照。CI-V 用模拟串口先验收，真实 RF 发射必须另行实机验证。

## 建议 PR 顺序

1. Track Manager + 多路 DeepCW 接收核心 + 可靠性修复与真实模型 benchmark（**PR #41 / #42 已合并**）。
2. 双分辨率 Frame Ridge Scanner、sample-index 时间轴、Precision-only Kalman 更新（**PR #43**）。
3. bounded fixed-lag beam/MHT、多帧 crossing/merge 关联与 AssociationHintId（**PR #44**）。
4. Incremental CTC / Transcript 时间对齐、重叠窗口投票与真实 ONNX streaming benchmark（**PR #45**）。
5. 实时 SDR/WASAPI/RS-BA1 PCM 接线。
6. CW Console + 性能调度 + HamNoise（完整停靠 UI 和本地降噪）。
7. SkyCAT CW CI-V 协议（白名单/错误处理/模拟器）。
8. 安全 TX + SAT 与 CI-V 协同（受控实机验收后启用）。
9. 端到端 WAV corpus 指标、中英文用户指南、许可证与正式发布。
