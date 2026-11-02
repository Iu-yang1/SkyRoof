# IF→AF 接收：IC-9700 到 WSJT-X

SkyRoof 提供可选的 **48 kS/s、12 kHz 实数（非 I/Q）USB IF → SSB/CW/FM → 单声道 AF → Windows 虚拟声卡**接收链路。解调器不会将音频转接至电台发射端，也不会发送 CAT/PTT 指令。独立启用的 AFC 仅执行受限 RX CAT 频率校正。

## 配置

1. IC-9700：**USB AF/IF Output = IF**，Windows USB 录音端点设 48 kHz，关闭声音增强或系统自动麦克风增益。
2. SkyRoof 设置 → **12 kHz IF → AF / WSJT-X**：选择 USB IF 输入、正确的 Left/Right 声道及 FollowRadio 或固定模式。FollowRadio 跟随 SkyRoof 的 RX/下行状态。
3. 安装包可选安装原厂基础版 VB-CABLE，或使用已安装的原厂虚拟声卡。AutoSignedCable 按名称匹配播放端 **CABLE Input** 和录音端 **CABLE Output**（不构成对已安装驱动的密码学认证）。手动模式允许选择其他专用虚拟声卡播放端，**严禁选择 Speakers (USB Audio CODEC) 等电台发射端点**。
4. 启用 IF→AF；WSJT-X → Audio → Input 选择 **CABLE Output**。不要同时让原 SDR Output Stream 写入同一端点。驱动要求重启时应重启。
5. 状态栏 IF/DDC/AF/V-MIC 可供监视接收链路，单击查看简要输入输出样本、幅度、丢块、欠载和故障文本。**V-MIC 绿灯不代表 WSJT-X 已成功解码**。

## DSP 与性能边界

- 四相 12 kHz NCO，257-tap 复数 SSB FIR（1.2～3.3 kHz 标称通带），700 Hz CW 中心滤波（100～1000 Hz）。
- FM：129-tap 复数 IF FIR、相位差鉴频、去直流、95-tap AF FIR。6.25 kHz 预设对应 IF/AF 截止约 3.0/2.1 kHz、频偏标定 900 Hz；12.5 kHz 预设约 5.5/3.0 kHz、2.5 kHz 频偏。预设不等于矩形频谱掩模。
- FM 独立增益、可选语音去加重与静噪（默认 OFF），SSB/CW 有界慢速 AGC 及峰值保护；兼容来源时 AFC/SSTV 共享原始 IF，CW 模块并非自动 raw-IF 客户端。
- 48 kHz 输入、每帧 480 样本、8 帧有界队列；WASAPI 输出请求缓冲可选 20/40/80/200 ms，**不代表已测量的设备或 WSJT-X 端到端时延**。

## 异常恢复与 AFC

DSP 初始化异常会明确告警，不再允许采集正常而解调线程静默退出。丢块将重置连续解调状态；持续丢块时可加大输出缓冲或降低 CPU 并发负担。SO-50 弱 FM AFC 要求低质量候选至少三次、间隔不小于 80 ms 且相互一致后才可提交 RX 校正；默认 HoldLast 保留最后可信 RX AFC 偏移，轨道多普勒继续运行。TX/PTT 安全互锁不改变。

## 许可与验收

正式安装包仅可选捆绑经验证、未经修改的 **VB-Audio 基础版 VB-CABLE** Donationware，保留厂商来源及捐赠说明，许可见 https://vb-audio.com/Services/licensing.htm 和 https://vb-cable.com/ 。专业及机构用途须遵守厂商授权。正式安装包不包含未受信任的自研 SysVAD 驱动。

xUnit 模拟信号测试与 Windows CI 不等于真实 IC-9700 射频灵敏度、音频端到端时延或 WSJT-X 解码性能已经验收。发布版移除了实验性 USB IF / LAN AF 双轨对照录音器。
