# CW Console 用户指南

CW Console 是 SkyRoof 的多路 CW 接收与文本拍发面板。它把多载波跟踪、
DeepCW 文本解码、AF 瀑布、多 Lane 连续 Transcript，以及显式 Arm 的
IC-9700 Command 17 文本 keyer 放在同一个 DockContent 中。

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

CW Console 按 **左侧大频谱、右侧多路 Pileup 消息、底部常驻 TX 工具条**
重新设计。原来占据中间位置的七列表格和独立的 Selected RX Transcript 大区域
已经从主界面移除。

- **顶部**：Start/Stop RX、音频源、只影响显示的 Raw/HamNoise 选项、
  CW Settings、解码器与 Worker 状态。
- **左侧**：实时 AF spectrum trace、高对比度 CW 瀑布、AF 频率刻度和 H/T
  Lane 标记。拖动中央分隔条即可调整频谱和消息区的宽度。
- **右侧**：8 个稳定编号的 Pileup 消息卡，支持纵向滚动。每卡显示
  Lane 身份、AF Hz、SNR、状态、C（Committed 稳定文本）及
  P（Provisional 可变文本），并提供 Copy 按钮。短暂 QSB、Hold 或载波
  交叉都不会让卡片按频率重新排序。点击消息卡或瀑布轨迹可以双向联动选中。
- **底部**：始终可用的 Arm、Send、红色 STOP、单行 CW 编辑框、
  WPM 设置与实际回读、F1–F8 宏按钮。窗口较窄时按钮行单独横向滚动。

**横向 AF 导航**：使用瀑布下方水平滚动条平移，用 Zoom 设置 1–8 倍显示
缩放；频谱获得焦点后，Ctrl+滚轮用于缩放，普通滚轮用于平移。
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

频谱/瀑布改为暗底、高对比的 CW Skimmer 风格：顶部显示实时 spectrum trace，
噪底保持暗色，不再每帧把噪声自动拉到满动态范围；窄 CW 载波随强度由绿色逐步
过渡到黄/白色，频率网格贯穿 spectrum 与 waterfall。

轨迹叠加也改得更轻：只有**当前选中的 Lane**才显示完整的 ±2σ 频率不确定度
带，避免大量半透明色块遮住真实频谱。

下方解码表使用 8 个固定编号 UI 槽位。只要同一 AssociationHint/Track 身份还
存在，它就保持在原来的行；短暂 Hold/QSB 进入 grace 状态，而不是让下方所有
Lane 每帧上下移动。真正消失数秒后才释放该槽位。

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
2. 用户必须在 CW Console 中按 **Arm TX**。

**Arm 状态永不持久化。** 每次新的应用/面板会话都从 Disarmed 开始。

SkyRoof 不会自动：

- 切换 CW/CW-R；
- 打开 Semi/Full BK-IN；
- 在此 keyer 路径自动控制 PTT；
- 根据 RX Lane 自动选择 TX 频率。

CW 发射能力现在默认启用，但**不会自动发射**；每次实际发射仍必须显式点击 **Arm TX**，并通过 CW/CW-R、Semi/Full BK-IN、SkyCAT lease 与频率 interlock 检查。

Arm 前请自行把电台设置成 CW 或 CW-R，并自行打开 Semi/Full BK-IN。

## SkyCAT keyer

文本拍发使用 SkyCAT 独立的本机回环 Command-17 端点，默认：

`127.0.0.1:4538`

SkyCAT 持有硬件 lease，并把 CW keyer 与其他 TX 使用者串行化。若客户端断开，
SkyCAT 也会执行 Command 17 `FF` fail-safe STOP。

发射区域提供 **6–48 WPM** 速度控件。点击 **Set WPM** 后，SkyRoof 通过
SkyCAT `SETWPM` 写入 IC-9700 CI-V `14 0C`，并要求电台立即回读一致后才
视为成功。TX 已 Arm 但空闲时，Console 会约每 1 秒刷新一次 `STATUS`，
因此从电台前面板修改 KEY SPEED 后无需重新 Arm 就能更新显示。

发送期间 Console 会显示依据已校验 IC-9700 KEYRAW 换算的实际 WPM 和 watchdog
倒计时。

## 卫星 TX interlock

卫星 CW 发射增加一层上下文与实际 VFO 校验。

Arm 时 SkyRoof 记录当前卫星、转发器、no-Doppler uplink 位置、uplink mode
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

TX 已 Arm 后按 **Send**。每次发送都会重新执行电台状态 preflight。消息 lease
一直保持到 STOP 或 watchdog 结束。

Watchdog 根据 IC-9700 KEYRAW 对应的键速估算 Morse 时长并加入安全裕量；若
消息没有在限定时间内结束，会执行 STOP。

醒目的红色 **STOP** 独立于 composer；只要对 TX 状态有疑问，应优先使用
STOP。

## F1-F8 消息宏

在以下位置编辑：

**Settings > CW Console > CW Message Macros**

默认全部为空。

- **F1…F8** 或点击按钮：只把宏装入 composer；
- **Shift+F1…Shift+F8**：显式通过完整 TX 安全状态机发送该宏；
- Ctrl/Alt 修饰的功能键不会被解释为 CW 发射快捷键。

第一次 Shift+Fn 正在异步 preflight 时，键盘自动重复不会排队多个发送请求。

宏仍必须经过 Enable TX、Arm、CW/BK-IN、卫星 TXHZ/SENDHZ、SkyCAT lease、
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
6. Arm TX；
7. 只发送一个很短的测试消息；
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

### Arm 失败

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
