# CW Console 用户指南

CW Console 是 SkyRoof 的多路 CW 接收与文本拍发面板。它把多载波跟踪、
DeepCW 文本解码、AF 瀑布、多 Lane 连续 Transcript，以及按 Send 时自动
进行发射联锁检查的 IC-9700 Command 17 文本 keyer 放在同一个 DockContent 中。

接收与发射权限严格分离。**解码出来的文字不会自动发射，也不会自动选择
发射频率。**

## 打开面板

使用 **View > CW Console** 打开。关闭面板不会停止后台 CW 接收，但会停止并
Disarm CW TX。

CW Console 可以和其他 SkyRoof 面板一样停靠和恢复布局。

## 选择接收音频

CW Console 支持三种输入：

- **SkyRoof SDR audio**：直接复用 SkyRoof 现有 48 kHz Slicer AF 音频；
- **IC-9700 USB AF input**：打开 Windows **录音/输入**端点。新配置会优先
  选择名称中含 **USB Audio CODEC** 或 **ICOM** 的设备；IC-9700 USB 直连时
  通常就是“麦克风 (USB Audio CODEC)”。但这里要求电台的 **USB AF/IF
  Output = AF**；如果电台 USB 输出的是 IF，不应把它送给这条 CW AF 解码链；
- **RS-BA1 playback loopback**：对 Windows **播放/Render** 端点做 loopback
  capture。这里应选择 RS-BA1 Remote Utility 实际正在播放到的扬声器/虚拟
  音频设备，而不是电台的麦克风 Capture 端点。

RS-BA1 Loopback 捕获的是**整个播放端点的混音**，不是只隔离 Remote
Utility 进程。若希望只接收 RS-BA1 音频，建议给 Remote Utility 使用独立的
播放端点。

切换音频源，或者在同一种源中换到另一个设备 ID，都会开始新的 CW timeline；
旧设备的 Track/Transcript 不会和新设备混在一起。

CW 接收默认关闭。确认输入源后，在面板中按 **Start**。

简单判断：**USB 线直接取得电台解调 AF** 时选 **IC-9700 USB AF input**；
使用 **RS-BA1 / LAN 收音**时选 **RS-BA1 playback loopback**。状态行会显示
当前真正打开的 Windows 设备名称。

## CW Skimmer V1 横向工作区

CW Console 使用 **左侧大频谱、右侧多路 Pileup 消息、底部选中 Lane 的 RX
解码框与常驻 TX 工具条**。原七列表格已从主界面移除；新的 RX 详情框体积
紧凑，保留 CW Skimmer 风格的选中信号抄收体验。

- **顶部**：Start/Stop RX、音频源、只影响显示的 Raw/HamNoise 选项、
  CW Settings、解码器与 Worker 状态。
- **左侧**：**左边竖直 AF 频率刻度**、紧邻的实时 spectrum trace、以及
  高对比度 CW 瀑布。每次新增 FFT 帧在**右边加入新的时间列**，历史画面随时间
  **向左横向滚动**；H/T Lane 标记改成对应 AF 的水平线。
  拖动中央分隔条即可调整频谱和消息区的宽度。
- **右侧**：8 个稳定编号的 Pileup 消息卡，支持纵向滚动。每卡显示
  Lane 身份、AF Hz、SNR、状态，以及可**自动换行、选择和独立滚动**的
  完整解码文本。临时 Provisional 后缀用单独颜色表示，长报文不再截断，
  仍可用 Copy 复制全文。短暂 QSB、Hold 或载波
  交叉都不会让卡片按频率重新排序。点击消息卡或瀑布轨迹可以双向联动选中。
- **CW TX 上方**：新增 **CW RX — Selected Lane**，复用 TX GroupBox
  的视觉样式。点击右侧 Pileup 卡片或频谱中的 Lane，即时显示对应
  **Slot 编号、H/T 身份、AF 频率、SNR、Active/Hold/Ambiguous/Grace 状态**。
  完整解码消息可自动换行、选中复制、独立滚动；未确认的 Provisional 字符
  与已经确认的 Committed 文本使用不同颜色。**Copy RX** 复制原始接收文字；
  音频时间线重置、选中 Lane 消失后清除旧内容。
- **底部**：Send（自动发射预检）、红色 STOP、单行 CW 编辑框、
  WPM 设置与实际回读、F1–F8 宏按钮。窗口较窄时按钮行单独横向滚动。

新的 RX 解码框**只读且绝不自动发送**：选中 Lane 或复制文字不会改变 TX
VFO、开启 CW 拍发或绕过卫星发射联锁。信息复用已有的 250 ms UI 更新，
不另起 FFT 或 DeepCW 解码任务。

**竖直 AF 导航**：使用瀑布右侧的竖直滚动条上下平移可见频率范围，
Zoom 设置 1–8 倍显示缩放；Ctrl+滚轮调整缩放，普通滚轮沿竖直 AF 轴平移。
瀑布水平方向表示**时间历史向左滚动**，绝不是电台调谐操作。
这些操作**只改变显示范围**，不调电台频率，不改原始 PCM、Ridge Scanner、
DeepCW、卫星 Doppler 或 TX 安全联锁。HamNoise 也仍然只影响显示。

## 安装 DeepCW 模型

DeepCW 只在用户明确操作后下载。SkyRoof 固定到不可变 revision：

`8e264d243bbd4467bd19f3f28292219405b47e0e`

模型来自 [e04/deepcw-engine](https://github.com/e04/deepcw-engine)，许可为
AGPL-3.0-only，不包含在 SkyRoof 安装包中。

模型状态区域会显示是否已安装，以及 ONNX 推理是否可用。

## Pileup 接收链

正式接收链为：

`PCM → 双分辨率 Ridge Scanner → 约 360 ms fixed-lag 多帧关联 → labelled Kalman/GNN → activity-aware 全 Track 分离 → DeepCW ONNX/CTC → Incremental Transcript`

Tracker 与 ONNX 推理解耦。Tracker 通常约每 120 ms 更新；DeepCW 使用滚动
音频窗口和约 1 s decode hop。若上一轮 ONNX 尚未完成，新到的旧窗口会被
跳过，而不是无限排队。

### Lane 身份

Pileup 消息卡和瀑布优先显示稳定的多帧身份：

- **H123**：AssociationHint 稳定身份；
- **T17**：尚无 Hint 时使用临时 TrackId。

因此两个 CW 载波交叉时，即使底层 Track 实例发生重建，UI 也尽量保持同一
视觉身份。

### CW Skimmer 风格频谱与稳定槽位

频谱/瀑布改为暗底、高对比的 CW Skimmer 风格：左侧 AF 频率刻度与旁边的
实时 spectrum trace 对齐，时间列由右向左滚动；噪底保持暗色，不再每帧把噪声
自动拉到满动态范围；窄 CW 载波随强度由绿色逐步
过渡到黄/白色，频率网格贯穿 spectrum 与 waterfall。

轨迹叠加也改得更轻：只有**当前选中的 Lane**才显示完整的 ±2σ 频率不确定度
带，避免大量半透明色块遮住真实频谱。

右侧 Pileup 消息区使用 8 个固定编号 Lane 卡片。只要相同的
AssociationHint/Track 身份仍然存在，它就保留在原卡片；短暂 Hold/QSB
进入 Grace 状态，其他卡片不会因为频率变化而移动。真正消失数秒后才释放槽位。

点击瀑布上的 Lane 只会选择已经存在的 Lane，**不会调电台，也不会修改 AF/RF
频率**。

## 连续 Transcript

每个 Lane 的文本分为：

- **Committed**：已稳定、不会再回退的前缀；
- **Provisional**：仍允许后续重叠窗口纠正的最新后缀。

SkyRoof 会利用 CTC OutputFrame 的时间位置对齐多个重叠窗口，并在获得重复
证据后再提交字符，避免简单拼接造成重复文字。

识别到呼号或 provisional 文本仍然只是接收信息，不会触发自动回复。

## 启用 CW 发射

CW TX 有两个独立门：

1. **Settings > CW Console > Enable CW Transmit** 必须打开；
2. 点击 **Send** 或 **Shift+F1…F8** 才会明确发起一次 CW 拍发请求；
   界面不再设置 Arm TX 按钮。每次请求都会自动取得最新卫星/发射上下文，
   并执行原有发射联锁、模式和硬件预检。内部 Arm 状态不会持久化。

SkyRoof 不会自动：

- 切换 CW/CW-R；
- 打开 Semi/Full BK-IN；
- 在此 keyer 路径自动控制 PTT；
- 根据 RX Lane 自动选择 TX 频率。

CW 发射能力现在默认启用，但**接收和解码不会自动发射**。每次实际发射
仍须操作员明确按下 Send，并通过 CW/CW-R、Semi/Full BK-IN、
SkyCAT lease 和频率 interlock 检查。

Send 前请自行把电台设置为 CW 或 CW-R，并打开 Semi/Full BK-IN。

## SkyCAT keyer

文本拍发使用 SkyCAT 独立的本机回环 Command-17 端点，默认：

`127.0.0.1:4538`

SkyCAT 持有硬件 lease，并把 CW keyer 与其他 TX 使用者串行化。若客户端断开，
SkyCAT 也会执行 Command 17 `FF` fail-safe STOP。

发射区域提供 **6–48 WPM** 速度控件。点击 **Set WPM** 后，SkyRoof 通过
SkyCAT `SETWPM` 写入 IC-9700 CI-V `14 0C`，并要求电台立即回读一致后才
视为成功。内部已完成预检且空闲时，Console 会约每秒刷新一次 `STATUS`，
因此从电台前面板修改 KEY SPEED 后可以更新显示。

发送期间 Console 会显示依据已校验 IC-9700 KEYRAW 换算的实际 WPM 和 watchdog
倒计时。

## 卫星 TX interlock

卫星 CW 发射增加一层上下文与实际 VFO 校验。

每次明确点击 Send 时，SkyRoof 记录当前卫星、转发器、no-Doppler uplink 位置、uplink mode
和 transverter 映射。真正 Send 之前：

- 只冻结 SkyRoof 自己的 TX CAT 频率/mode/CTCSS 写入；
- RX 和 Doppler 计算继续；
- SkyCAT 重新读取实际 TX VFO；
- 通过原子 `SENDHZ expectedHz toleranceHz text` 发送。

如果实际 TX VFO 超出允许误差，Command 17 之前就会拒绝发送。

消息正在拍发时，如果用户改变卫星、转发器、no-Doppler 调谐位置、uplink
mode 或 transverter 上下文，系统会 STOP 并 Disarm。正常 Doppler 演化不会
触发该保护。

线性转发器要求 uplink 位于修正后的公布通带；单频 uplink 使用修正 base
附近的有限安全窗口。**这些软件检查不等于法规/执照判定**，实际发射权限仍由
操作员按当地法规和执照负责。

## 发送文本

Composer 最多接受 30 个 Command-17 字符。

直接按 **Send**，Console 会自动准备联锁并执行完整电台状态 preflight。消息 lease
一直保持到 STOP 或 watchdog 结束。

Watchdog 根据 IC-9700 KEYRAW 对应的键速估算 Morse 时长并加入安全裕量；若
消息没有在限定时间内结束，会执行 STOP。

醒目的红色 **STOP** 独立于 composer；只要对 TX 状态有疑问，应优先使用
STOP。

## F1-F8 消息宏

**右键点击 F1–F8 任意按钮**即可原地编辑并保存消息到 Settings.json；
也可在 **Settings > CW Console > CW Message Macros** 中编辑。
默认全部为空。

- **F1…F8** 或点击按钮：只把宏装入 composer；
- **Shift+F1…Shift+F8**：显式通过完整 TX 安全状态机发送该宏；
- Ctrl/Alt 修饰的功能键不会被解释为 CW 发射快捷键。

第一次 Shift+Fn 正在异步 preflight 时，键盘自动重复不会排队多个发送请求。

宏仍必须经过 Enable TX、CW/BK-IN、卫星 TXHZ/SENDHZ、SkyCAT lease、
watchdog、STOP 和断线 fail-safe。不存在宏队列，也不存在自动回复。

## 可选 HamNoise 频谱清理

正式安装包现在包含固定 revision 的 AGPL HamNoise 后端，但它**只处理显示**。
CW Console 的 Spectrum 下拉框可选：

- **Raw**：原始显示 PCM；
- **HamNoise Classic**；
- **HamNoise CW V2**。

HamNoise 只处理一份给 spectrum/waterfall 使用的不可变副本。Ridge Scanner、
fixed-lag tracker、DeepCW、Transcript 与 TX 始终使用未经 HamNoise 处理的原始
PCM。因此打开/关闭 HamNoise 只会改变“你看到的频谱”，不会改变 detector 或
DeepCW 的判决。

另外，为解决空频谱上出现虚假高 SNR Lane/乱码的问题，正式解码链会独立检查
局部频谱突出度和真实 CW 键控 on/off 证据，只有物理上像 CW 的 confirmed lane
才进入 ONNX；低 margin 的 CTC 字符也会在进入 transcript 投票前被丢弃。

## 第一次实机 RF 验收

第一次发射建议先使用 dummy load 或其他受控、低功率环境。

建议顺序：

1. 在修改设置前先确认 RF 安全状态；
2. 自行把 IC-9700 TX VFO 设置为 CW/CW-R；
3. 自行打开 Semi/Full BK-IN；
4. 核对 Console 显示的预期卫星/transverter TX VFO；
5. 在 Settings 打开 **CW Transmit**；
6. 直接按 Send 发送很短的测试消息（自动执行预检）；
8. 按 STOP，确认拍发立即终止；
9. 确认 lease 释放后 Doppler TX 调谐能够 catch-up；
10. 最后才进行符合本地法规、执照和 band plan 的实际通联测试。

## 录音回归测试

SkyRoof 提供离线 WAV corpus runner，可对完整接收链做版本回归。格式见
[Recorded CW corpus](https://github.com/Iu-yang1/SkyRoof/tree/master/benchmarks/cw-recorded-corpus)。

truth 的参考 AF 频率只在解码完成后用于评分配对，不会提供给 detector/tracker。

## 常见问题

### Model not installed

在 CW Console 中执行模型安装，并检查是否能访问固定的 deepcw-engine revision。

### 没有出现 Lane

确认 CW RX 已 Start、音频源正确、CW 音调位于 Scanner AF 范围内。使用
RS-BA1 Loopback 时还要确认 Remote Utility 确实输出到所选播放端点。

### 文字似乎跑到了错误 Lane

查看 H/T 身份、Ambiguous 状态以及 ±2σ 区域。强重叠、merged peak 或很小的
载波间隔会暂时增加身份不确定度。

### Send 预检失败

逐项检查：

- **Enable CW Transmit** 已打开；
- SkyCAT keyer 端口可访问；
- TX VFO 已经是 CW/CW-R；
- Semi/Full BK-IN 已打开；
- 没有其他 TX lease；
- 卫星操作时当前转发器/uplink 上下文有效。

### Satellite send 报 TX VFO mismatch

不要仅为了消除报错而扩大 tolerance。应先检查所选卫星/转发器、Base 修正、
transverter LO、用户 no-Doppler 调谐位置和电台实际 TX VFO。

## CW 接收 CPU 性能说明

本版本缓存 Ridge Scanner 的 Hann 窗函数，并对零多普勒速率跳过不必要的逐采样三角运算。CW 频谱仍以约 20 Hz 更新，但仅用于显示的 8192 点长 FFT 约每 4 帧刷新一次。DeepCW ONNX Runtime 使用一个会话，并限制模型内部 CPU 并行线程，避免与 120 ms 跟踪循环竞争。这些优化不改变 Decode Window、Hop、物理证据门限、跟踪协方差或卫星 TX interlock。

请在相同音频、相同最大 Lane 数、相同 Spectrum 模式下，比较 CPU、已完成/跳过的推理窗口及实际抄收效果。i5-10400 与 i7-14650HX 的 Windows 进程 CPU 百分比不能直接作为每次解码 CPU 成本比值；仍需在实际机器上做性能基准。


### 复用 FFT Plan 的实数 FFT（FFTW3f）

CW 接收现统一采用前向 **R2C 实数 FFT** 内核：包含零已知 Doppler Rate 的
Ridge Scanner、CW 多峰检测、DeepCW 模型采样率特征、共享宽带特征和频谱瀑布。
对于实数 AF PCM，只计算并读取 `0..N/2` 个非冗余复数频点，且保持原来
`FourierOptions.Matlab` 的**前向不归一化幅度定义**。非零 Doppler 去啁啾涉及
真正的复数基带信号，仍使用原来的双精度复数 FFT。DeepCW 的 Hann 窗、
频率映射、tensor 规格、门限和卫星发射联锁没有修改。

优先后端为 SkyRoof **原来就附带**的 `libfftw3f-3.dll`（FFTW3f）。
FFT 工作缓冲区通过 FFTW 自带的对齐分配器分配，使其可以使用该 DLL 实际
编译支持的 SIMD 内核；Plan 跨解码窗口复用，避免每帧重新规划。如果 DLL
缺失或无法加载相关 R2C 符号，则自动回退到 Math.NET 的原有实现。
是否确实利用 AVX2/AVX-512 取决于 FFTW 二进制构建与运行机器，
**不能仅凭 CPU 支持 AVX2 就认定 FFT 已使用 AVX2**。

本次参考的是
[kfrlib/fft-benchmark](https://github.com/kfrlib/fft-benchmark) 的
**同一 CPU、预热、多次测量、实数/复数分开比较**的方法，而不是将该项目
本身作为 FFT 库引入。测试框架为 MIT 许可证，实际 FFTW 代码沿用其 GPL
许可证，本次未引入 KFR、MKL 或 IPP 二进制文件。

每次 PR 的 Compile Check 会比较 256、2048、3840、4096、8192、
11520 点 R2C 与原复数 FFT 的同机中位耗时，并上传
`cw-fft-r2c-benchmark`（JSON Lines）。这只反映 FFT 变换成本，
**不能直接等同于整机 DeepCW 解码 CPU 占用率**。请在相同音频和设置下，
进一步比较 i5-10400 的总 CPU、skipped inference windows 和抄收准确率。


### 增量 STFT 缓存与 ONNX 推理监控

Ridge Scanner 对**已知多普勒速率为零**的帧按绝对 PCM 起始采样索引缓存
峰值观测结果。该缓存仅在音频来源为连续追加、不会覆写历史的实时接收前端
启用，音频时间线重置时清空。非零多普勒去啁啾仍使用原有复数 FFT。
80/15 ms 快速 STFT 与 240/120 ms 精密 STFT 的时间网格、Kalman
观测时间和测量协方差没有更改。

共享 DeepCW 宽带 STFT 采用**每个推理 Session 独立、至多 1600 帧**
的缓存。只有完全位于 PCM 窗口内部的帧才可复用；窗口首尾的反射填充
依赖本次窗口边界，必须重新计算。注意：6 秒解码窗口每约 1 秒推进，
但 STFT Hop 是 15 ms，1 秒并非 15 ms 的整数倍，因此不能用
“上一窗口第 N 帧”直接对应本窗口第 N 帧。实现仅在**绝对采样区间相同**
时复用，保持特征幅度、模型输入张量、判决门限和译码时间轴不变。
启用实验性 Window Denoiser 时跳过此缓存。

将鼠标悬停在 CW Console 顶部 **Worker** 状态文字上，可查看 Ridge STFT
和 DeepCW STFT 的缓存命中/重新计算帧数，以及 ONNX Runtime `Run()`
的平均毫秒数。ONNX 仍复用单个 CPU InferenceSession，保留受限的
内部线程数；连续输出张量可直接供 CTC 解码而无需复制一份大数组，
SessionOptions 初始化后会正确释放。此处没有修改 ONNX 模型、
改变 Batch 大小或声称 ONNX 内核本身获得同比例加速。
比较 i5-10400 优化前后的 CPU 占用时，应使用同一输入 PCM、
解码 Lane 数和设置，并同时关注 completed/skipped windows。


### CW 频谱与 HamNoise 显示流畅度

CW 频谱和瀑布改为**独立后台、只处理最新帧**的计算链。每个完成的显示帧
同时包含 2048 点瀑布 FFT 和 8192 点实时频谱 FFT，取消了此前对频谱
人为设置的约 **5 Hz** 刷新限制。计算完毕后，由 WinForms UI 线程仅负责
接收结果和绘制，不在 UI 定时器中同步执行 FFT 或神经网络。

启用 **Spectrum > HamNoise Classic / CW V2** 时，9.6 kHz 降采样、
HamNoise 原生推理以及升采样也在后台串行处理。后台最多存在**一个在处理
的显示帧**：假如 HamNoise 计算超过 50 ms，会跳过中间帧，而不是把历史帧
排队到主窗口中慢慢播放。这样可避免 CW Console 的计算拖慢主窗口其他控件
及 Icom LAN Spectrum 的消息循环。需要注意，这并不代表 HamNoise 必然
达到 20 FPS；实际可持续速率仍受其原生推理耗时和机器性能限制。

切换显示降噪模式、接收音频时间线重置时，旧代次/旧模式的后台结果不会
再被画到新瀑布上。关闭窗口时等待后台原生计算结束后再释放 FFTW 缓冲区，
但不会同步阻塞 UI 关闭。跟踪器与 DeepCW 仍保持独立，从未将显示降噪音频
回写到接收链或 CW TX。

瀑布图每列改用一次 GDI+ LockBits 写入，避免每次显示执行 512 次
SetPixel；轨迹标记随状态刷新，而非每 50 ms 强制请求重绘。
如果可选的 HamNoise 显示计算报错，会自动退回 Raw，不影响解码。

### CW 实时瀑布 30 FPS 目标与偶发卡顿诊断

CW 瀑布的 **Raw** 模式由原先每 **50 ms**（理论上限 20 FPS）改为
每 **33 ms** 请求一次更新（约 30.3 次/秒）。注意，这只是刷新目标，
**不是可保证的实际 FPS**。WinForms Timer 依赖 Windows UI 消息循环，
同时受控件绘制、音频回调、后台任务和系统调度抖动影响。FFT 和可选的
HamNoise 仍在无积压的独立后台显示链执行；8192 点左侧频谱线与每次
2048 点瀑布帧一起刷新，不再人为限制为 5 FPS。

鼠标悬停在 CW Console 顶部的 **Spectrum: Raw / HamNoise** 状态文字上，
可查看以下实测指标：

- **Painted waterfall FPS**：真正被 WinForms 绘制的新瀑布列的速率，
  而不是定时器触发次数，也不会把重复重绘算成新帧。
- **p95/max painted interval**：最近帧间隔的 95 分位及最长间隔（ms），
  用来定位肉眼看到的短暂停顿。
- **Last/mean background processing ms**：后台降采样、HamNoise、
  升采样及两次 FFT 的实际总计算时间。
- **Busy ticks / stale frames**：后台忙时跳过的轮询，以及音频时间线/
  显示模式切换后丢弃的过期结果。

音频源暂时没有提供新 PCM 时不会反复排队同一批数据；未变化的多路
CW 轨迹也不再每 250 ms 强制整幅重绘。较慢电脑上 HamNoise 完成率
可能仍低于 30 FPS，但不会积压旧显示帧或阻塞其他 SkyRoof 页面。
DeepCW 解码和 CW 发射联锁均未更改。

请在 i5-10400 上分别测试 Raw、HamNoise Classic、HamNoise CW V2，
运行至少 10 秒后比较实测 FPS、p95 帧间隔及平均后台耗时。
左侧谱线响应较快但 Painted FPS 偏低，往往更值得继续检查
UI 消息循环与绘制时间，而非单纯继续替换 FFT。


### 与 LAN 频谱共享 UI 消息线程时的帧率诊断

CW Console 和 Icom LAN Spectrum 使用同一个 WinForms UI 线程。
旧版 LAN 频谱每收到一帧 CI-V `27 00` 都独立投递
`BeginInvoke`，还会每帧重复设置多个下拉框，这可能在
CW 的 FFT/HamNoise 已移出 UI 线程后，仍然导致 CW 瀑布仅
**9 FPS** 或出现短暂停顿。现在 LAN 波形只保留最新的待绘制帧，
并避免无变化的频谱几何触发频繁控件刷新。

CW 后台完成 FFT 后还会主动发出帧就绪通知：距离上一帧已经
约 **32 ms** 时，尽快投递至 UI，而不是必须再等待一次
33 ms WinForms Timer；定时器仍作为兜底。两个调度入口均保持
单任务无积压策略。此修改提高响应及时性，但不保证在所有 CPU
或 HamNoise 模式下都能实现实际 30 FPS。

鼠标悬停 CW Console 顶部 **Spectrum** 状态文字，可比较
真实 Painted FPS、帧间隔 p95/最大值、GDI 绘制时间平均/p95
和后台 FFT/HamNoise 处理时间。如果 FFT 和 GDI 都只用几毫秒，
但 FPS 仍低，就应继续查 UI 消息排队和音频回调周期。


### IC-9700 USB AF 麦克风采集造成的 CW 瀑布刷新限制

实机截图显示 CW Raw 约 **9.4 FPS**，HamNoise Classic 约
**9.6 FPS**，但 GDI 绘制只有约 2–4 ms、后台 FFT/降噪
平均只需约 7–10 ms。这种现象不能用 FFT 运算慢解释。
审查发现原有通用 `InputSoundcard<T>` 设定 **200 ms 的
WASAPI 采集缓冲**，并以 **4800 个采样点**（48 kHz 下
100 ms）作为读取块上限；即使瀑布每 33 ms 请求刷新，
也不能产生比实际输入 PCM 更新更快的真实新帧。

本次**仅为 CW 麦克风采集实例**请求更短的 40 ms
WASAPI 缓冲，读取块上限改为 **1600 点（约 33.3 ms）**。
其他 SkyRoof 音频设备保持 200 ms / 4800 点默认值，
不会改变其他声音输出、接收解码或发射逻辑。
不同 Windows 音频设备可能强制较长共享模式周期，因此
**不能保证所有电脑必定达到 30 FPS**。

鼠标悬停 CW Console 顶部 `Spectrum: Raw` / HamNoise
状态文字，可同时读取实际 **Painted FPS、p95 帧间隔**、
**PCM input samples/block、last delivery interval (ms)**。
如果后者仍约 100–200 ms，优先检查 USB Audio CODEC
驱动或 Windows 的音频缓冲配置，而非继续调整 FFT。

同时移除了 LAN Spectrum 红色 RX 通带上的超长英文
说明悬停提示，避免遮挡频率和谱线。估算 IF 滤波器带宽
仍可在 Spectrum Settings 中调整，文档保留相关说明。


### 音频数据驱动 CW 瀑布刷新（40 ms WASAPI 修复后续）

实机截图中 CW 音频每块 1440 样本、PCM 交付间隔约
30.3 ms，但 Raw 模式真实绘制仅 **16.8 FPS**。
与此同时，LAN 频谱的完整瀑布行速率已达到 28.8 WF/s；
CW 的 GDI 绘制 p95 只有 3.9 ms，后台 FFT 平均仅
0.6 ms。这次瓶颈不宜继续归咎于 FFT 算法。

上一版 CW 只通过 **33 ms WinForms Timer** 提交新频谱任务；
FFT 完成后若距离上次提交不足 32 ms，完成通知还会被
直接跳过，等下一次定时器再处理。Windows 消息循环忙时，
定时器可能合并或推迟触发，让本来每 30 ms 到达的音频
出现约 50–115 ms 的不规律显示帧间隔。

现在采用**新 PCM 到达事件 + FFT 处理完成事件**
双驱动显示：两个来源都通过至多一个待执行的
`BeginInvoke` 通知 UI，音频回调线程不会执行 FFT
或任何 WinForms 绘制；后台仍仅允许一项在处理的
HamNoise/FFT 任务，不积压历史帧。移除 FFT 完成
回调的 32 ms 硬等待，仅保留 100 ms WinForms
Timer 用于通知丢失时兜底。

鼠标悬停 `Spectrum: Raw` 状态可查看累计 PCM 事件数、
FFT-ready 事件数和合并后的 UI 处理数，配合
Painted FPS、帧间隔 p95 判断是否真正达到约 30 FPS。
实际帧率仍受 Windows UI 消息循环和采集硬件影响，
需要实机验证；DeepCW 解码及 CW TX 不受本次修改影响。
