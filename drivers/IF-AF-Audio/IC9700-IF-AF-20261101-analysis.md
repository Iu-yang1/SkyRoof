# IC-9700 12 kHz IF / LAN AF 双轨录音分析（2026-11-01）

数据来自用户提交的 IF-AF_20261101T065525193Z_86ec40.zip；原始音频及 Windows 设备 ID 不上传公开仓库。

| 项目 | USB 12 kHz IF | LAN 原始 AF |
|---|---:|---:|
| 样本量 | 9,638,880 | 9,637,440 |
| 时长 | 200.810 s | 200.780 s |
| 格式 | 48 kHz / mono / float32 | 48 kHz / mono / float32 |
| RMS | 0.012553 | 0.057318 |
| 绝对峰值 | 0.151184 | 0.342285 |
| 非有限样本/≥1.0 削波样本 | 0 | 0 |
| CSV 块 offset 非连续 | 0 | 0 |
| 录音器报告的 DroppedBlocks | 0 | 0 |

Welch PSD（8192 点）表明 LAN AF 中约 **98.52%** 功率位于 300–3000 Hz。IF 在 12–14 kHz 左右具有与 AF 对应的频率结构，支持 IC-9700 12 kHz IF USB 解调模型。

将 IF 与 12 kHz 本振实数混频，350–2800 Hz Butterworth 带通，再对 AF 同样带通，在 10 ms RMS 分箱包络上做归一化互相关：包络相关峰值约 **0.895**；AF 相对 IF 的最佳包络偏移约 **+320 ms**。这一值混合了 LAN 接收链路与 Windows 音频回环等延迟，**不是 IF-AF-Audio 驱动延迟，也不是 SkyRoof IF-AF 端到端延迟**。

**证据等级与限制：**此 ZIP 提供 USB IF 和 LAN 原始 AF，未包含新 WaveRT 虚拟驱动播放→录音的音频环回。因此只能证明用户所记录的两路信号格式、连续性、频谱一致性和包络相关性，不能证明驱动安装、Windows 端点注册、虚拟麦克风低延迟/多客户端能力或者 WSJT-X 实际解码率。WASAPI callback UTC 非硬件同步时间戳。无 CSV gap 不等于硬件 USB 完全无丢包。

**后续验证门槛：**隔离 Windows 测试环境中经授权签名安装；枚举 render/capture 两个 MMDevice；测量 1 kHz 测试波的时间戳延迟；长时间 silent/underrun、reopen、双 capture 进程、休眠恢复和 CPU/DPC 负载；然后连接真实 IF-AF DSP/WSJT-X 译码。实验打包仅含开发 SYS/INF/CAT 文件，不自动装载驱动。
