# IF-AF-Audio 虚拟录音端点与低延迟链路设计

> 当前状态：**用户态低延迟链路与状态条已实现；独立的 Windows 虚拟音频驱动尚未开发、签名或安装**。只有 Windows 设备管理器和录音设备列表真实存在端点后，WSJT-X 才能选择 `IF-AF-Audio`。不要以软件内部队列冒充 Windows 虚拟声卡。

## 驱动分层设计

以微软 [SysVAD WaveRT 样例](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/) 为起点，另行建立 WDK 驱动项目：

- Windows 输出/渲染端点：**IF-AF-Audio Playback**，SkyRoof 作为唯一生产者向其写入 AF。
- Windows 输入/录音端点：**IF-AF-Audio**，WSJT-X、GNU Radio 等作为常规 WASAPI 捕获客户端打开它。
- 驱动内实现 render→capture 环形缓冲、QPC/采样帧计数与时钟连续性。格式初版限制为 48 kHz / mono / IEEE float32；可提供系统音频格式协商，同时避免多余重采样。
- 不共享 Icom USB Audio CODEC 的 TX 端点，不向 CI-V、PTT、CAT 发送指令，也不在应用启动时静默安装内核驱动。
- 系统启动、驱动停止、切换设备、关闭 SkyRoof 等情形，虚拟麦克风应输出**连续零值 PCM**（静音），不得重复旧音频数据。驱动需要独立处理读取指针、欠载/溢出和多客户端读取。
- 以 **WDK 构建、Windows Hardware Lab Kit (HLK) 测试、Microsoft Hardware Dev Center 签名**作为发行门槛。未通过可信签名的驱动不得被正式安装器静默部署，也不得要求用户关闭 Secure Boot 或禁用内核安全功能。

官方参考：[SysVAD](https://github.com/microsoft/Windows-driver-samples/tree/main/audio/sysvad)、[Windows WASAPI 低延迟](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)、[驱动签名政策](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)。

## SkyRoof 实验分支已落地的实现

### 配置

在 **设置 → 12 kHz IF → AF / WSJT-X (Test)**：

| 属性 | 说明 |
| --- | --- |
| `AudioRoute` | `ExistingVirtualCable`（默认，保持此前 VAC 兼容）或 `PreferIfAfAudio`（必须已安装上述专用驱动） |
| `DriverStatus` | 只读；核对 Windows 系统是否同时存在配对的真实播放和录音端点 |
| `LatencyPreset` | `Fast20Ms`／`Balanced40Ms`（默认）／`Stable80Ms`／`Compatible200Ms` |
| `Virtual audio render device` | 仅在 `ExistingVirtualCable` 模式使用；例如第三方已安装的 VB-CABLE 播放端点 |

若选 `PreferIfAfAudio` 但没有真实驱动，SkyRoof 会**明确拒绝启动 IF-AF**并提示缺失，不会向 USB Audio CODEC 或 ICOM_VAUDIO 偷偷回退。

### 应用链路

```text
IC-9700 USB 12 kHz IF
  ↓ WASAPI shared capture (20 ms requested buffer, 480-sample / 10 ms reader frames)
Single IF source (SSTV/AFC subscribers)
  ↓ bounded 8-frame queue (~80 ms max queued, drops counted)
Complex DDC → FM/SSB/CW AF (48-kHz mono float)
  ↓ WASAPI shared-mode output (requested 20/40/80/200 ms)
Real virtual playback device / existing third-party VAC
  ↓ corresponding Windows recording endpoint
WSJT-X Input
```

`OutputSoundcard` 的原默认 200 ms 保留给其他 SkyRoof 音频用途，只在实验 IF-AF 路径降低为可配置周期。NAudio/WASAPI 可能依据底层驱动实际缓冲进行调整，用户请求 20 ms 不代表硬件、操作系统和解码程序总延迟恰好为 20 ms。

### 状态条

SkyRoof 底部状态栏新增紧凑的 **IF › DDC › AF › V-MIC/VAC** 分段状态条；仅启用 IF-AF 时显示。

- IF：Windows USB IF 实际输入是否持续更新；
- DDC：解调后的 AF 是否流动；
- AF：输出播放设备是否正在运行；
- V-MIC／VAC：所选真实虚拟设备是否存在、可用；
- `Q`：**软件队列时延估计**，来自本机回调 UTC，非硬件同步；
- `P`：**WASAPI 请求的输出缓冲周期**，非已测延迟。

点击状态条可以查看样本量、削波、丢块、设备发现状态与队列等待信息。它**无法确认 WSJT-X 实际解码成功**；需要由 WSJT-X 接收端做单独验证。

## 硬件验收建议

先用 `ExistingVirtualCable` + `Balanced40Ms`（最稳妥），然后在声卡环境允许时尝试 `Fast20Ms`。通过新增 USB IF/LAN AF 双轨 WAV 和 WSJT-X 解码记录，比较端到端延迟、频偏、缓冲欠载、掉块、接收 SNR、FT4/FT8/Q65 解码率。虚拟驱动正式可用后再进行 IF-AF-Audio 原生端点验收。
