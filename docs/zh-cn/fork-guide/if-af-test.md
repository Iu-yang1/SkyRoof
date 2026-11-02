# IF-AF-Test：12 kHz IF → SSB / CW / FM → WSJT-X

> 仅适用于 `IF-AF-Test` 实验分支。默认关闭；IF→AF 解调器本身不发送 PTT/CAT；独立的 AFC Auto 模块可在用户启用后通过 CAT 修改接收频率。尚未合并至正式 Release。已使用 IC-9700 实际 USB 12 kHz IF 与原机 LAN AF 双路录音对 USB 解调作离线对照；LSB/CW/FM 及 VB-CABLE 真实环回、WSJT-X 译码仍需分别做实机验收。

## 实现与信号处理

- 实际输入：48 kS/s **实数** 12-kHz IF，绝非 I/Q。固定四相 NCO 将中心频率搬移到 0 Hz。
- SSB：257-tap 复数 Blackman FIR 选择正/负边带；USB、LSB 可通过 7 档 AF 通带预设选择 **1.2/1.8/2.1/2.4/2.7/3.0/3.3 kHz**，低截止点 180 Hz，避免实数乘积检波导致反边带噪声折叠。
- CW：独立正/负 CW 通道，中心 AF 音调固定 700 Hz，可选 **100/200/300/400/500/800/1000 Hz** 7 档带宽；窄带采用复数 FIR 与两级低计算量 IIR 音频带通协同滤波。
- FM：129-tap 复数基带低通、相邻样本共轭乘积鉴频、7.5 Hz 去直流和 95-tap AF 低通；提供 **6.25/12 kHz 标称 IF 双边带宽**两档，配套 IF/AF 截止频率与鉴频频偏参数。受 FIR 过渡带影响，标称带宽并非具有陡峭边缘的矩形通带。
- 数字输出：48 kS/s 单声道浮点 AF，独立 OutputSoundcard<float> 输出到 Windows **虚拟声卡播放端点**。不借用 SDR Slicer → VAC 输出流。
- **IF→AF 解调器**不会修改 IC-9700 的发射、CAT、卫星数据库或手动多普勒设置；独立 **AFC Auto** 功能会对 RX CAT 进行受限频率修正，不控制 TX 或 PTT。

设计参考：[GNU Radio Frequency Xlating FIR Filter](https://wiki.gnuradio.org/index.php/Frequency_Xlating_FIR_Filter)、[GNU Radio 源码](https://github.com/gnuradio/gnuradio/blob/main/gr-filter/lib/freq_xlating_fir_filter_impl.cc)、[SoapySDR Device/Stream API](https://pothosware.github.io/SoapySDR/doxygen/latest/classSoapySDR_1_1Device.html)。采用其**频移-滤波、明确复数/实数样本格式、独占流所有权和显式采样率**理念；不声称直接复用 GNU Radio/SoapySDR 的软件或代码。

## 设置

1. IC-9700 `USB AF/IF Output = IF`，Windows 将 USB 录音端点设为 48 kHz。推荐不启用 Windows 音效增强、自动增益。
2. SkyRoof → Settings → **12 kHz IF -> AF / WSJT-X (Test)**，先选择输入为 `Microphone (USB Audio CODEC)`，输入声道默认 **Left**；不再强制将立体声平均。
3. 使用实验分支的 **VB-CABLE 集成安装包**，交互式安装默认勾选安装原厂基础版驱动；经 UAC 授权后按要求重启。旧版或未勾选时，可通过 Settings → **安装 VB-CABLE** 操作。`AudioRoute=AutoSignedCable` 自动选择播放端 **CABLE Input**；**不需要手动选择 `Virtual audio render device`**。仅使用手动 `ExistingVirtualCable` 时才选择该设备。严禁选 `Speakers (USB Audio CODEC)`，以防误接入 IC-9700 发射音频输入。
4. 重启后确认 Windows 具有 **CABLE Input（播放）** 与 **CABLE Output（录音）** 两个端点，再设置 `Enable USB IF -> AF = true`、`Demodulation = FollowRadio`；该选项跟随 SkyRoof 当前接收/下行模式，不等于直接读取电台手动改变的 CI-V 模式。对于线性转发器可显式选择 USB/LSB；CW-LSB 需手动选择。
5. WSJT-X → Settings → Audio → Input 选 **CABLE Output**；发射 Output 可按电台 USB 调制输入单独设置。WSJT-X CAT 建议通过 SkyCAT 4534 代理，SkyRoof 独立管理卫星多普勒。

**输入与共享：** IF-AF 服务启动后，AFC/SSTV 如果选择同一 USB IF 录音端点，将订阅共享的原始 PCM；不同设备或 RS-BA1 回环仍走原来的独立来源。CW 解码器当前并未直接订阅共享 raw IF，可按需要从经 VAC 解调的 AF 输入接收，不能视为所有 CW 路径已统一。

**输出与风险：** 原 SkyRoof `Output Stream / Audio to VAC` 与新 IF-AF 为不同模块，不能使用同一个 VAC 设备。SSB/CW 使用独立的慢速有界 AGC，FM 默认输出增益 −15 dB；注意 WSJT-X Input 电平和系统声卡削波。关闭实验功能即可恢复原有路线。

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

## 新增：快速、可恢复的卫星 AFC（FM / CW / SSB）

`IF-AF-Test` 已重构 AFC，正常卫星多普勒预测仍由原模型控制，AFC 只校正 RX 残余频偏；TX、PTT、转发器偏移、数据库保持原逻辑。

| 模式 | IF 最小观测 | DSP 窗口 | 测量节拍 | 短时预测上限 | 跟踪依据 |
| --- | ---: | ---: | ---: | ---: | --- |
| FM（SO-50 等） | 400 ms | 500 ms | 125 ms | 1.5 s | 正交相位鉴频后稳健估计载波中心 |
| CW | 375 ms | 550 ms | 125 ms | 2.5 s | 已选载波 4096/8192 点自适应 FFT、窄频率门 |
| SSB，稳定导频 | 375 ms | 600 ms | 150 ms | 2.0 s | 选定的窄带导频；没有导频不可开启绝对 Auto |
| SSB，无导频 | 750 ms | 1000 ms | 250 ms | 无 | 仅频谱相对漂移建议，始终 Assist |

> 窗口并非额外等待时间：采集到首个最小 IF 样本后即可开始频率测量；软件需要运行 FFT/DSP/CAT 后才可能提交修正。以上为算法配置，不是实测的锁定时间。

控制环采用质量门限和预测器的分离设计：新的真实载波才能产生 CAT AFC 增量；短时间失声时，显示带限频偏预测，**不依赖噪声继续调整频率**。默认 `HoldLast` 保留最后确认的 AFC RX 附加修正，轨道多普勒继续运行；仅显式选择 `GentleRelease` 时才在失去参考 30 秒后以 10 Hz/s 缓慢释放。发射 PTT 时冻结修正及释放。

发散处理不再永久进入 `Fault`。仅当独立、带时间戳的 AFC 自身 CAT 写频读回证明误差持续增大时，控制器才将累计修正折半回退并进入重新捕获。自动 RF/IF 方向必须通过多次一致的真实 CAT/IF 频移证据才能反转；`RfIfSign=±1` 属人工覆盖，出现矛盾时不会擅自反转人工设置，而提示需要检查并先回退旧修正。

日志增补 `AFC trace` 和 `AFC watchdog`，记录模式、状态、质量、测量 IF、预测 IF、误差、AFC 修正、CAT 读回、是否确认 AFC 自己的频率写入及 PTT。这有助于下一次 SO-50 故障直接定位。

重要局限：普通抑制载波 SSB 语音没有唯一绝对载波参考；不应将“语音频谱边界”当成绝对频率误差。此外，实际 12-kHz IF 方向、CAT 延时、信号幅度和多普勒动态仍需使用同步双路录音及真实 IC-9700 验证。

## AFC 最小 IF 采样时长：可选捕获策略

在 **AFC → 高级设置 → 捕获策略（速度 / 灵敏度）** 中选择；修改后即时更新采样器并清理旧分析窗口，USB IF 设备无需重连。

| 策略 | FM 初次观测 | CW 初次观测 | SSB 有导频初次观测 | 适用情况 |
| --- | ---: | ---: | ---: | --- |
| **Adaptive（默认）** | 250 ms | 250 ms | 250 ms | 快速开始，后续自动延长分析窗口、提高频率分辨率 |
| Fast | 250 ms | 250 ms | 300 ms | 短促的 FM/CW 信号、低 CPU 占用 |
| Balanced | 400 ms | 375 ms | 375 ms | 之前的稳定默认观测门限 |
| Sensitive | 500 ms | 375 ms | 375 ms | 弱信号，允许更多统计时间以提高可靠性 |

说明：这些值是**启用首次 DSP 测量的最小缓存长度**，不是实测锁定耗时，也不能代替载波确认、CAT 读回和控制环限幅。普通无导频 SSB 始终只提供辅助信息，不会变成绝对 Auto。采集线程限制为最多一个测频任务运行；Adaptive 保留短窗 4096 点质量判断、较长窗 8192/16384 点高精度载波估计，以及 FM 的重叠短时对照；多个重叠窗口不是统计独立的多个 CAT 确认。性能要求高时可切换 Fast，避免 i5 等设备 CPU 负载过高。

## IF-AF-Audio device architecture

历史自研 IF-AF-Audio WaveRT 研究档案见 [设计文档](../../../drivers/IF-AF-Audio/DESIGN.md)。**当前发行方案不是自研签名 WDK 驱动**，而是嵌入并校验 VB-Audio 原厂基础版驱动 ZIP，通过官方安装器完成部署。


## SO-50 AFC 修复：弱 FM 验证与丢失信号保持

先前 FM 中存在分层置信度拒绝：测频器只接受 ≥40% 的置信度、控制器和预测器需要 ≥60%。即使有低标准差的候选（例如 −694 Hz、σ≈17 Hz、质量26%）也可能一直显示候选，RX AFC 为 0 Hz。

实验分支现在采用**低质量候选 + 时序一致性**：FM 估计器可提交置信度 ≥22%、足够信噪指标且有限 σ 的候选；当其质量低于标准的 60% 但 σ≤45 Hz，须至少连续 **3 个时间间隔≥80 ms 且相邻频偏差≤max(55 Hz, 2.5σ)** 的观测才允许通过伺服器的有效测量验证。重叠短窗有相同采样，故不宣称三次是统计独立；单帧低质量仍不得写 CAT。连续候选不一致、质量更低、幅度不合理或者无信号时自动重新累积。普通 CW/SSB 的置信度门槛未降低。

### 丢失信号后的修正保持（重要行为变更）

旧代码超过约1.5 s FM 预测后以 **160 Hz/s** 把已确认的 RX AFC 偏移归零。现在新增高级设置 **信号丢失后的 AFC 修正策略**：

- **HoldLast（默认，SO-50 推荐）**：保持最后可信的 RX AFC **附加频偏**不变；SkyRoof 的原有卫星轨道多普勒修正照常变化；有新的可信载波时从原偏移恢复闭环。语音静音、QSB、弱信号拒绝、CW 电键间隙不触发频率归零。UI 可显示 Hold / Lost，但不代表 RX 校正量重置。
- **GentleRelease（明确选用才启用）**：无可信载波 **30 秒后**，以最多 **10 Hz/s** 缓慢释放到 0；PTT 时冻结这一释放。
- 用户手动改频、切卫星／切接收模式、禁用 AFC／重建输入或目标仍会重置对应的动态 AFC 校正；发散时经真实 AFC CAT 读回验证的方向冲突仍可进行有界回退，不属于“无信号自动归零”。

例如，AFC 曾修正 −800 Hz，信号消失 10 秒：默认 HoldLast 下仍保持 −800 Hz，轨道多普勒继续计算。截图中的 26% 候选只有在连续多次可信一致后才会被标记为“**弱 FM 已时序确认**”；显示的原始单窗质量不会被伪造为 60%。

实际误锁率、动态多普勒、环路 CAT 方向仍需要 IC-9700 双路 WAV 和真实通联验证。

## 已签名第三方虚拟声卡方案（当前 IF-AF-Test）

已取消在 SkyRoof 安装包中编译、携带或自动安装自研 SysVAD 内核驱动。
现统一使用 Windows 中已安装的第三方虚拟音频驱动，经 WASAPI 连接：

- **优先：[VB-CABLE 官方下载](https://vb-audio.com/Cable/)**。
  SkyRoof 将 48 kHz 单声道 float32 AF 送往 *播放端* `CABLE Input`，
  WSJT-X 在 Audio → Input 选择 *录音端* `CABLE Output`。
  Windows 共享音频引擎可能按设备原生格式进行转换。
- **备选：[Virtual Audio Cable Lite](https://vac.muzychenko.net/en/)**。
  默认是播放和录音端的 `Line 1 (Virtual Audio Cable)`，须使用原厂包并遵守
  Lite 版非商业用途分发限制。
- 新用户默认 `AudioRoute=AutoSignedCable`：仅自动选取**完整且唯一**
  的厂商播放+录音端点对；`ExistingVirtualCable` 保留手动选择，
  适合改过名称或使用其他虚拟声卡的情况。旧配置枚举值保持兼容。
- **禁止静默回退到 IC-9700 USB Audio CODEC 发射端、默认系统扬声器**。
  找不到完整端点时直接报错。设备名可用于识别，但不能当作密码学
  签名验证；内核驱动能否装载由 Windows 系统驱动策略决定。
- 设置窗口提供“安装 VB-CABLE”“卸载 VB-CABLE”“重新检测音频端点”。
  实验安装包携带完整、未经修改的原厂 ZIP（其中自然包含厂商原有的 INF/SYS/CAT）；不拆改、不重新签署驱动文件。安装须经 UAC 授权，卸载须用户确认且 SkyRoof 记录为自己安装的实例，绝不自动移除其他软件已安装的声卡。
- 避免 IF-AF 与原有 SkyRoof Slicer → VAC 同时写入同一虚拟声卡；
  用户上传的 USB IF/LAN AF 录音不等同于已验证第三方 cable 环回性能。

历史自研驱动资料仍保留在 `drivers/IF-AF-Audio/` 供研究，但默认构建及
实验驱动打包工作流已停止。当前实验安装包包含 SkyRoof 及**未经修改的基础版 VB-CABLE 官方 ZIP**；交互式安装时默认勾选官方驱动自动安装，可取消，静默 CI 冒烟测试不会安装音频驱动。完成后须重启 Windows。


**2026-11-01 一体化打包方案：**VB-Audio 官网允许在 Donationware 条件成立时将基础版 VB-CABLE 原始包嵌入应用安装器并静默安装，须明确告知来源和支持／捐赠渠道（详见 https://vb-audio.com/Services/licensing.htm）。SkyRoof 使用原包 `VBCABLE_Driver_Pack45.zip`，发行时不提取重签名内核驱动；校验原始 SHA256 与原厂签名后由原厂 `VBCABLE_Setup_x64.exe` 安装。卸载仅允许操作者确认且 SkyRoof 实际安装过的实例，避免误删其他软件依赖。

## 2026-11-01 源码复审及实机验收顺序

1. **初始安全检查：**确认电台 `USB AF/IF Output=IF`，Windows USB IF 为 48 kHz；SkyRoof 的 IF→AF 输入选 IC-9700 USB Audio CODEC 的真实 IF 声道，默认 Left；AFC 独立采集默认也为 Left，若选 Right 请在 AFC 中显式指定。严禁将 CABLE Output 选作 IF 输入，严禁将 IC-9700 USB 扬声器选作解调 AF 输出。
2. **USB 与 LSB：**用强度稳定的接收信号轮流切换 USB/LSB；WSJT-X Input 使用 CABLE Output。检查 IF Input/Output Samples 是否单调增长、DroppedBlocks/ClippedSamples 是否保持 0，确认错边带不应产生与正确边带同样的声音。
3. **CW 与 FM：**CW 测试 250–1150 Hz（建议 700 Hz）音调，另测反向 CW 需手动选 `CwLsb`；FM 分别试 2.5 kHz/5 kHz 偏移参数，避免发射端真实频偏与参数不一致。记录监听和音频水位，勿以响度判断解调灵敏度。
4. **AFC：**从 Assist 开始观察 IF 峰和 CAT 读回，确认模式映射正确；FM 短发射应先捕获载波、随后才有限速校正。无稳定导频的 SSB 不允许绝对 Auto。启用 Auto 前核对 CAT 方向，发射 PTT 时 RX 校正冻结；信号消失 10 s 的 `HoldLast` 不应归零。
5. **音频冲突/丢块：**故意选择相同 CABLE Input 给旧 SDR Audio-to-VAC 与 IF→AF，启动应被拒绝；错误选择 CABLE Output 作 IF 捕获也应被拒绝。故意制造高 CPU 负载观察欠载/丢块计数，恢复后无陈旧音频重放。更改 AFC 捕获声道后须重新开启该捕获链。
6. **端到端：**记录 VB-CABLE 捕获 WAV 与原始 LAN AF（使用独立设备录制，避免混音回环）并对齐包络，检查 WSJT-X FT4/FT8/Q65 信号水位、音频频偏与解码结果。需按多个独立周期统计，不能只凭一次成功判定等效接收性能。

在 2026-11-01 原始 IC-9700 **USB 模式** IF/LAN AF 对照录音的 50–80 s 片段中，离线 USB 复数滤波 AF 与 LAN AF 短时包络相关约 **0.953**（相对时移约 **+320 ms**）；换用 LSB 解调得到约 **0.018**。这证明了该录音的 USB 频谱方向与软件 USB 选边带的一致性，但既不是第三方虚拟声卡驱动环回测试，也不是对 LSB/CW/FM 独立实机验收。

## VB-CABLE 安装失败（退出码 1）的现场诊断

如果设置窗口提示 `Vendor driver manager exit code: 1`，不要自行禁用驱动签名检查，也不要改用未经签名的自编译虚拟声卡驱动。此退出码来自管理 PowerShell 的异常路径，并不能单凭数字区分官方驱动安装、PnP 部分注册和哈希校验失败。

从修复后的实验安装包重新安装 SkyRoof 后，通过“设置 → 安装 VB-CABLE”重试。脚本将在 `SkyRoof\\ThirdParty\\VB-CABLE\\VB-CABLE-manager.log`（实际位于 SkyRoof 安装目录下）保存当前一次安装的完整诊断，并在失败弹窗显示 `VB-CABLE FAILED at stage [...]`、异常描述和原厂 Setup 退出码。日志可能写入管理员权限受保护的安装目录，正常用户只需读取，不应手动修改原始 ZIP、CAT 或 SYS。

- `Bundled archive and SHA256`：检查安装目录下原始包及 `.sha256` 清单是否完整；重新运行官方集成安装包可恢复二者。
- `Original setup Authenticode signature` / `Original driver catalog signature`：检查 Windows 证书链、时间与设备策略，勿绕过签名验证。
- `Original vendor setup (Install)`：原厂安装器返回失败。收集管理日志与 `C:\\Windows\\INF\\setupapi.dev.log`，排查 Windows 驱动安装错误。
- `Windows device discovery`：检查 `Get-PnpDevice -Class MEDIA` 与 `Get-PnpDevice -Class AudioEndpoint`，确认设备状态。
- `Partial VB-CABLE installation detected`：先重启 Windows；如仍存在半安装状态，再使用原厂 setup 界面或 Windows 设备管理器处理，不自动卸载来源不明的共享驱动。

安装成功后重启，再确认 Windows “播放”中有 **CABLE Input**、“录音”中有 **CABLE Output**，状态正常；随后在 SkyRoof 重新检测音频端点。GitHub CI 只验证签名、管理脚本与安装器内容，不在 CI 中加载内核驱动。

## 2026-11-01：IF→AF 配置与状态栏统一

`AF audio route` 只保留两个有效选项：`AutoSignedCable`（自动识别 VB-CABLE / VAC Lite 成对录音与播放端点，默认）和 `ExistingVirtualCable`（明确指定虚拟播放端点，录音端不自动验证）。旧版配置中的数值 `0` 或字符串 `PreferIfAfAudio` 会在加载时迁移为 `AutoSignedCable`，不会再显示已停止使用的自研驱动选项。既有数值 `1` 与 `2` 不改变含义。

主窗口底部状态栏使用与 RX CAT 相同的 Webdings 圆灯 + Segoe UI 文本样式，分为 `IF`、`DDC`、`AF` 和 `V-MIC`。灰灯表示未启用；绿灯仅代表该本地阶段工作；橙灯代表启动中、短时欠载/丢块或无法核验完整端点对；红灯表示停止或明确故障。手动 `ExistingVirtualCable` 模式下，即使输出在播放，`V-MIC` 也保持橙色，因为程序未证明对应录音端点存在。绿灯**不能证明 WSJT-X 已收到或解码音频**。点击任一状态灯、名称或 `Q/P` 指标可打开完整 IF→AF 诊断。Q 是软件队列时延估计，P 是请求的 WASAPI 播放周期，并非端到端实测延迟。

## 2026-11-01：AF/V-MIC 橙灯与实际削波区分

`Clipped since start` 是自 IF→AF 本轮启用以来、被软件限制在 ±0.999 的**累计输出样本数**。即使之后信号恢复，历史计数也不会下降。原先状态条直接根据 `ClippedSamples > 0` 判定橙灯，导致一次削波后 AF/V-MIC 永远无法自动恢复绿灯。现在状态条比较前后快照中的**新增削波样本**，在最后一次检测到新增削波后维持橙灯约 2 秒；没有新增削波并且其余处理状态正常时恢复绿灯。累计历史计数仍保留，不得清零以掩盖故障。

点击状态灯的诊断窗增加输出 AF 最近处理块的 `AF RMS`、**限幅前** `AF peak`、最近约 0.5 秒状态刷新间隔内的新增 `Clipped` 数量/比例及最后一个约 10 ms PCM 块的限幅样本数。这些短窗指标不等于整个过境的统计，不应拿输入 RMS（当前短块）去解释此前多分钟累积的削波。若 `AF peak (BEFORE limit)` 达到或超过 0 dBFS、短窗新增削波持续增加，应检查 USB IF 输入电平、Windows 增益、模式、AF gain，以及真实经 CABLE Output 录制的 PCM；必要时暂将 AF gain 调低 6–12 dB 用于诊断。正常 USB FIR 解调器不会在输入约 −38 dBFS 时无缘无故产生数百万削波；此问题需要根据新指标及 IF WAV 才能断定根因。

## 解调链路电平控制重构：SSB/CW AGC 与 FM 独立标定

自本次版本起，`12 kHz IF → DDC → 模式解调` 仍保持原有 48 kS/s 接收流程。音频电平控制仅作用于**解调后的 AF**，不会更改发送频率、射频/IF 样本或 AFC/SSTV 订阅的共享原始数据。

- **SSB / USB-D / LSB-D / CW**：采用慢速、增益有界的 RMS AGC（默认启用，目标 −24 dBFS，最大额外放大 +24 dB）。增益下降快、恢复慢；低于 −80 dBFS 的静音块冻结增益，避免 CW key-up 或 FT4/FT8 静音期导致无限噪声提升。可分别调整 `SSB output trim (dB)` 与 `CW output trim (dB)`，也可关闭 AGC 进行幅度线性对照。
- **FM / FM-D**：沿用基于相位差和设定 `FM peak deviation` 的鉴频公式，输出增益独立为 `FM output gain (dB)`（默认 −15 dB），避免把 FM 的归一化幅度和 SSB 的电平混为一谈。实际频偏与设置不同仍可能产生音量/失真差异；峰值保护不能替代 FM 限幅与弱信号性能的实测。
- **峰值保护**：在 10 ms AF 处理块内，根据最大样本幅度将峰值限制在 0.89（约 −1 dBFS），通过对全块应用同一系数避免硬截断样本；会产生峰值触发次数与瞬时保护衰减的诊断计数。它不是长时间窗真峰值/Lookahead Limiter，也不保证真实声卡端到端没有失真。频繁触发需继续检查录音。
- **旧设置迁移**：历史单一 `GainDb` 不再显示或序列化。旧 FM 音量 −15 dB 或更低会保留，旧 0～−14 dB 的 FM 增益按安全策略迁至 −15 dB。新的 SSB/CW 增益独立，不再受旧 `GainDb=-15` 拖累。新属性写入 `Settings.json` 并作为模式切换时的稳定配置。

每种模式建议先记录 120 秒实际音频，再比较 `AF RMS`、`AF peak (BEFORE ceiling)`、`AF applied gain`、`Peak-limited blocks`、`Clipped`。在 FM 中测试实际 2.5/5 kHz 频偏；在 USB/LSB 中用稳定 CW/语音和 WSJT-X FT4/FT8 对照，确认 AGC 不引起可见的幅度泵吸或符号失真。CI 仅能证明合成 IF 信号下正确，不等于 9700 现场数据通过。

## 简化的带宽预设（IF-AF-Test）

设置页 `Receive filters` 仅显示三个下拉框，不要求用户手动计算截止频率。更改配置后点击 **Apply**，软件重新构建本地音频滤波器（会短暂重启 IF→AF 链路），不会改变 IC-9700 的硬件 IF 滤波器，也不触碰 AFC 原始 IF 输入。

| 模式 | 下拉预设 | 默认 | 实际含义 |
|---|---|---|---|
| SSB（USB/LSB） | 1.2 / 1.8 / 2.1 / 2.4 / 2.7 / 3.0 / 3.3 kHz | 3.0 kHz | 180 Hz 起始的目标 AF 通带宽度；选择 1.2 kHz 会影响高频数字音调和高音语音 |
| CW（双边带） | 100 / 200 / 300 / 400 / 500 / 800 / 1000 Hz | 500 Hz | 以 700 Hz CW 音调为中心的音频带通；100/200 Hz 适合稳定的窄带信号 |
| FM | 6.25 / 12 kHz | 12 kHz | 标称 IF 双边带宽，分别对应约 3.125/6.0 kHz 复数 IF 低通截止，1.7/3.0 kHz AF 低通，默认频偏归一化 1/2.5 kHz |

**调谐注意：** 这些是软件滤波器名义参数，不是精确通带的 −3 dB 实测带宽，也不是电台信道间隔。CW 固定 700 Hz 中心，如果电台的 CW 音调偏离中心、特别是使用 100/200 Hz 档，应先核对接收音调。WSJT-X FT4/FT8 通常优先 SSB 3.0/3.3 kHz，并检查软件瀑布图边缘衰减。FM 6.25 kHz 档只适用于较窄频偏/占用带宽的信号，对普通宽频偏 FM 应选择 12 kHz 并验证实际 AF。旧配置的 SSB 截止频率可映射到就近预设。

## P0–P3 接收链 DSP 迭代（2026-11-02）

**P0 — 带宽校准：** 257-tap Blackman 复数 SSB FIR 与 FM IF/AF 低通现在用响应函数反求设计截止频率，确保预设名义边缘接近实际 −3 dB，而不是 sinc 的过渡带中点。CW 的两级 IIR 带通获得约 1.56 倍设计补偿并配合固定宽带的复数预选器，以避免原先 CW 500 Hz 档实际仅约一半带宽的问题。FM 仍保留兼容枚举 `Normal12k` 的持久化数值 12000，但设置页显示 **12.5 kHz 信道间隔**；按照 SDRangel NFM 预设，6.25/12.5 kHz 档分别匹配 6.0/11 kHz IF 通带、2.1/3.0 kHz AF 截止及 ±0.9/2.5 kHz 预期频偏。使用 Blackman FIR 的有限过渡带，标称值不等于实机测量的矩形占用频宽。

**P1 — 解调与增益分离：** 自动 FollowRadio 遇到 `USB_D`/`LSB_D` 时使用 `UsbDigital`/`LsbDigital` 内部模式，直接使用 100–3500 Hz 的宽 AF 选择边带，不运行语音 SSB/CW RMS AGC 和语音陷波。普通模拟 USB/LSB 继续执行有上限的慢速 AGC。输出仍保留约 −1 dBFS 的峰值保护。新增的 FM 语音静噪和去加重**默认关闭**：静噪是经低通后的 IF 平均功率门控（50ms 时间常数、约 10ms 渐变开门），可选阈值，不能当作可靠的弱载波/数字模式判断；去加重提供 Off、50μs、75μs，**卫星数据/非语音 FM 建议保持 Off**。模拟 SSB 陷波提供 Off、1 kHz、1.5 kHz 固定陷波，数字模式自动旁路。后两者属于可选试听处理，不保证自动识别干扰。

**P2 — 实时性能和峰值保护：** 因 IF 中心频率 12 kHz 恰好是 48 kHz 的四分之一，SSB/CW 复数 NCO+FIR 可在配置时为四个相位预计算实值卷积权重，实时路径减少复数乘法，保持波形数学等价；FM 仍保留独立的复数鉴频。AF 工作块通过 `ArrayPool<float>` 复用，仅在同步写入 WASAPI 缓冲后释放。峰值保护对过大峰值立即降低增益、约 150ms 平滑释放，避免限制器下一 10ms 帧直接反弹引发泵吸。诊断窗口添加每个处理块的 `DSP time`，请结合 10ms 输入块比较。此轮**没有实施抽取/重采样大规模改造**，须先证明现有 CPU 负载需要该风险较高的步骤。

**P3 — 可选音频增强：** 实现可关闭的模拟 SSB 1/1.5 kHz 窄陷波、FM 50/75μs 语音去加重和 FM 功率门控静噪，并通过模拟测试检查关闭时行为不变。自动干扰追踪和谱减/神经网络降噪**尚未部署**；这类功能必须先得到弱信号、Q65/FT4/CW 点划损失率的实测证据。参考 [GNU Radio FIR 设计](https://www.gnuradio.org/doc/doxygen/classgr_1_1filter_1_1firdes.html)、[SDRangel NFM](https://github.com/f4exb/sdrangel/blob/master/plugins/channelrx/demodnfm/readme.md)、[GNU Radio FM Deemphasis](https://wiki.gnuradio.org/index.php/FM_Deemphasis) 及 [Power Squelch](https://wiki.gnuradio.org/index.php/Power_Squelch)。

**验收边界：** 所有增加的 DSP 测试为合成 48k/12k IF、模拟声卡路径或频率响应测试；它们不是 IC-9700 真正的硬件 ENOB、SINAD、QSO 成功率、端到端延迟的证明。需另行录制 `CABLE Output` 并结合原始 IF/LAN AF WAV 比较幅度、频响、CW 包络、FM 无信号状态和数字解码。
