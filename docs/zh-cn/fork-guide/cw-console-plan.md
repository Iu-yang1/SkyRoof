# CW Console：多路 Pileup 解码与自动拍发实施规划

**状态：实施规划，不是已完成的用户功能。** 本开发分支仅增加多 CW 载波轨迹跟踪器及单元测试。DeepCW ONNX、WASAPI、HamNoise、CW 发射和 UI 尚未集成。

参考：[deepcw-engine](https://github.com/e04/deepcw-engine) 是**正式 CW 解码模型**，包含 model.onnx、model.onnx.json 与推理示例；[web-deep-cw-decoder](https://github.com/e04/web-deep-cw-decoder) 提供多路检测与 Pileup 结构参考；[HamNoise](https://github.com/e04/HamNoise) 提供可选神经降噪 C 核心。英文方案见 [English plan](../../fork-guide/cw-console-plan.md)。

## 核心约束

- 完整 Pileup = 多路频点自动检测 + 稳定 Track ID + **每路独立带通、频移与 DeepCW 模型推理** + 每路独立连续文字；只识别多峰而只解码一路不能验收。
- deepcw-engine 的公开模型采用 3,200 Hz 音频、FFT 256 / hop 48、400–1200 Hz 65 bins、[1,1,T,65] 张量、42 类 CTC 文字。须读取模型元数据，不混用网页端 9,600 Hz 或尚未随独立引擎发布的检测/窄带专用模型。
- 无法保证分离同频同时间重叠的两个 CW 电台。遇到不可分离信号应标记干扰，不编造第二路转录。
- Icom LAN Scope 数据是频谱帧，不是连续 PCM。RS-BA1 接收音频应走用户选定的 WASAPI / Loopback，不再争用 CI-V/LAN 音频连接。
- SkyRoof 与 HamNoise、deepcw-engine 均使用 AGPL-3.0 系列许可，应保留声明和模型版权；网页端 UI 参考布局但不复制授权未明的 React 源码。

## 完整工作流程表

| 编号 | 模块 / 依赖 | 必须实现 | 验收 |
|---|---|---|---|
| 00 | 冻结基线 | SkyRoof/SkyCAT SHA、现有 CI、许可和回滚计划 | FT4、频谱、转台仍正常 |
| 01 | 音频输入 | SDR Slicer、WASAPI、RS-BA1 播放设备 Loopback | 切换/拔插无卡死；音频线程不堵塞 |
| 02 | PCM 引擎 | Float32、有界队列、时间戳、抗混叠重采样到 9.6/3.2k | 断流恢复、积压受限 |
| 03 | 多路检测 | FFT 多峰、噪声底、锁定/释放阈值与点划特征 | 多 CW 候选可检出；纯载波不占满路数 |
| 04 | 载波跟踪 | ID、Doppler 漂移、QSB Hold、去重、最多 8 路 | **本 PR 实现基础模块与测试** |
| 05 | 多路隔离 | 每 Lane 50–300 Hz 带通、NCO 频移到 DeepCW 可识别音调 | 3–5 路可独立提取 |
| 06 | 多路推理 | ONNX Runtime + 按元数据 STFT/log1p/CTC、独立流式 pending/confirmed | 每 Lane 都解码文字，防止串台/重复 |
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

SDR PCM / 选定 WASAPI / RS-BA1 Loopback → 有界音频 Hub →（可选 HamNoise）→ CW 多峰检测 → CwPileupTrackManager → 每 Lane 独立 BPF+NCO+重采样 → deepcw-engine ONNX → 独立 CTC 转录 → Pileup 列表/选中路文本/QSO 辅助。

候选 AF 频率不等于 RF 下行频率；正确的接收音调到射频转换取决于 CW/CW-R 与解调方式。音频推理不得直接操作 TX VFO。

## 发射安全和验证

SkyCAT 现有 4532 主 CAT 通道独占 RS-BA1 虚拟 COM 并串行仲裁，新增受限 CW 命令，不给 4537 Remote Control Switch 发送/PPT/任意 CI-V 权限。IC-9700 文本自动拍发须遵照 CI-V 17 ASCII 最多 30 字符和 17 FF Abort，设备 ACK 不代表真正发完。失联不能确认 Abort 被电台收到时应显示“TX 状态未知”，禁止自动恢复排队发射。必须人工解锁并确认正确卫星、TX VFO、模式、PTT 所有权后方可发送。

验收用多个混合 CW WAV 测 per-lane CER、误检率、丢路率、串扰率及音频->文字延迟。涵盖 10/20/30/40 WPM，3/5/8 路，低 SNR、QSB、频率扩展、多普勒、邻频及不可分离同频，并作 Raw/HamNoise 对照。CI-V 用模拟串口先验收，真实 RF 发射必须另行实机验证。

## 建议 PR 顺序

1. Track Manager（本 PR，测试与此规划）。
2. Capture/Detector（PCM + FFT + 真实多路候选）。
3. Full Pileup DeepCW（每路滤波、ONNX、独立转录和 WAV 实测）。
4. CW Console + HamNoise（完整停靠 UI 和本地降噪）。
5. SkyCAT CW CI-V 协议（白名单/错误处理/模拟器）。
6. 安全 TX + SAT 与 CI-V 协同（实机验收后启用）。
7. 中英文用户指南、许可证和正式发布。
