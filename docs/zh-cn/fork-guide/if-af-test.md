# IF-AF-Test：12 kHz IF → SSB / CW / FM → WSJT-X

> 仅适用于 `IF-AF-Test` 实验分支。默认关闭；不改动电台 PTT/CAT，不合并至正式 Release。所有指标以软件仿真为主，尚未在真实 IC-9700 的 USB IF 端点测量。

## 实现与信号处理

- 实际输入：48 kS/s **实数** 12-kHz IF，绝非 I/Q。固定四相 NCO 将中心频率搬移到 0 Hz。
- SSB：257-tap 复数 Blackman FIR 选择正/负边带；USB、LSB 的通带分别在 ±(180–3300) Hz，避免实数乘积检波导致反边带噪声折叠。
- CW：独立正/负 CW 通道，约 250–1150 Hz。
- FM：129-tap 复数基带低通（约 8.8 kHz）、相邻样本共轭乘积鉴频、7.5 Hz 去直流和 95-tap AF 低通（约 3.4 kHz）。2500/5000 Hz 峰值频偏可配置。
- 数字输出：48 kS/s 单声道浮点 AF，独立 OutputSoundcard<float> 输出到 Windows **虚拟声卡播放端点**。不借用 SDR Slicer → VAC 输出流。
- **不会**修改 IC-9700 的发射、CAT、卫星数据库或手动多普勒设置。

设计参考：[GNU Radio Frequency Xlating FIR Filter](https://wiki.gnuradio.org/index.php/Frequency_Xlating_FIR_Filter)、[GNU Radio 源码](https://github.com/gnuradio/gnuradio/blob/main/gr-filter/lib/freq_xlating_fir_filter_impl.cc)、[SoapySDR Device/Stream API](https://pothosware.github.io/SoapySDR/doxygen/latest/classSoapySDR_1_1Device.html)。采用其**频移-滤波、明确复数/实数样本格式、独占流所有权和显式采样率**理念；不声称直接复用 GNU Radio/SoapySDR 的软件或代码。

## 设置

1. IC-9700 `USB AF/IF Output = IF`，Windows 将 USB 录音端点设为 48 kHz。推荐不启用 Windows 音效增强、自动增益。
2. SkyRoof → Settings → **12 kHz IF -> AF / WSJT-X (Test)**，先选择输入为 `Microphone (USB Audio CODEC)`，输入声道默认 **Left**；不再强制将立体声平均。
3. 安装 VB-CABLE/虚拟声卡，在 `Virtual audio render device` 选 **CABLE Input**。不得选 `Speakers (USB Audio CODEC)`，该设备可能是 IC-9700 的 USB 发射调制输入，软件主动拒绝。
4. `Enable USB IF -> AF = true`；`Demodulation = FollowRadio`，对于线性转发器不一致的模式可选 USB/LSB，必要时手动选择 CW 或 FM。
5. WSJT-X → Settings → Audio → Input 选 **CABLE Output**；发射 Output 可按电台 USB 调制输入单独设置。WSJT-X CAT 建议通过 SkyCAT 4534 代理，SkyRoof 独立管理卫星多普勒。

**输入与共享：** IF-AF 服务启动后，AFC/SSTV 如果选择同一 USB IF 录音端点，将订阅共享的原始 PCM；不同设备或 RS-BA1 回环仍走原来的独立来源。CW 解码器当前并未直接订阅共享 raw IF，可按需要从经 VAC 解调的 AF 输入接收，不能视为所有 CW 路径已统一。

**输出与风险：** 原 SkyRoof `Output Stream / Audio to VAC` 与新 IF-AF 为不同模块，不能使用同一个 VAC 设备。启动 IF-AF 时默认增益 0 dB；注意 WSJT-X Input 电平和系统声卡削波。关闭实验功能即可恢复原有路线。

## 状态监测

主界面 VAC 状态灯显示 IF-AF 是否处于运行状态；悬停可检查 IF RMS dBFS、输入/输出样本数、工作模式、队列丢块、AF 削波次数、采样时间戳与软件队列时延。显示的 `Queue age` 不是包括 200 ms WASAPI 音频缓存/WSJT-X 内部缓存的端到端延迟。若选择输出设备错误或者音频端点不活动，默认拒绝启动而不是自动转到电台发射 USB 扬声器端点。

## 软件仿真验收

运行 `VE3NEA.Dsp.Tests` 测试工程内的 `IfAfDemodulatorTests`、`IfAfSimulatedHardwareTests`、`SstvIfAudioDemodulatorTests`。
覆盖合成 48k/12k IF USB/LSB 正负边带、反边带阻塞、有限幅度 16-bit PCM、AWGN、弱 CW、FM 频偏+漂移、分块连续性、NaN/Inf、立体声反相混合等。模拟信号链相当于**模拟电台已导出的 IF PCM**，不包含 IC-9700 未公开的 ADC 量化、内部自动增益/滤波或真实 USB/LAN 驱动时钟。

## 未验证项目

- IC-9700 的实际 IF 频谱方向、幅度和有效位数；真实 IF 和原机 AF 的同步对照（SNR、SINAD、IMD、AFC 收敛）。
- 干扰、瞬态强信号、USB 缺包及 Windows 48k 采集驱动错误。
- 硬件发送期间的 CAT/PTT 保护和 FT4/FT8/Q65 端到端解码灵敏度。

**不要将软件测试通过解读为 IC-9700 接收机灵敏度或动态范围已等同于原机 AF。**

## 新增：IC-9700 USB IF 与原机 LAN AF 双路对照录音

在 SkyRoof **Recorder 面板的 Record 下拉菜单**选择 **Record IC-9700 USB IF + LAN AF…**；也可从主窗口 **Tools → Record IC-9700 IF + LAN AF (Test)…** 打开独立录音窗。

1. 电台设置：`USB AF/IF Output = IF`，`LAN AF/IF Output = AF`；在 RS-BA1 Remote Utility 让 LAN AF 进入独立 Windows 播放端点（可选择 CABLE Input，但请勿与实验版 IF-AF 解调输出混用）。关闭静噪，避免音频中断。
2. 窗口内分别选择 **USB 12 kHz IF 录音设备（Windows Capture）** 与 **LAN / RS-BA1 AF 播放设备（Windows Render）**，选择 USB 左/右声道（默认 Left），指定保存目录。
3. 在备注框填写接收频率、模式、滤波器带宽、AGC、PREAMP、ATT、射频增益等；点击 **开始双路录音**，持续录制相同的 RF 信号 60–120 秒，点击 **停止并保存**。两路会尽可能同时启动，**不保证硬件样本同步**。
4. 保存目录格式为 `IfAfComparisons/IF-AF_YYYYMMDDTHHMMSSfffZ_xxxxxx/`。上传整个文件夹中的五个文件，不能仅发送其中一个 WAV。

| 文件 | 内容 |
| --- | --- |
| `ic9700_usb_12khz_if.wav` | USB 12 kHz IF，48 kHz / mono / IEEE float32，未经过 SkyRoof IF→AF 解调 |
| `ic9700_lan_original_af.wav` | RS-BA1/LAN 原机 AF，从所选播放端点 WASAPI Loopback 录制，同为 48 kHz / mono / float32 |
| `ic9700_usb_12khz_if.csv` | IF 每一块音频的采样偏移量、数量与 Windows 回调 UTC 时间 |
| `ic9700_lan_original_af.csv` | AF 每块音频的采样偏移量、数量与 Windows 回调 UTC 时间 |
| `capture.json` | 设备 ID/名称、录制起止 UTC、下行频率与模式、IF 输入声道、两个轨道的有效样本数、丢块、报错和人工备注 |

可不启用独立 `IF-AF` 的 `Enable USB IF -> AF`，此时录音器会单独采集 IF。如果实验解调服务正在使用**同一 USB 设备和声道**，则录音器直接订阅共享 IF PCM，避免重复打开声卡。

**计量边界：** WAV 保存的是应用实际接收到的、经 Windows 可选重采样/声道选择后的单声道 PCM，而不是 USB 端点原始比特流。UTC 时间戳来自 Windows 软件回调，不是设备硬件时间戳；两条时钟存在固定偏移或漂移属于正常现象，需要后期相关分析、对齐与重采样。RS-BA1 播放端点的其他 Windows 声音也会被混录；在纯静音时 WASAPI Loopback 可能不产生块。请保持设备独占用途并检查丢块统计。
