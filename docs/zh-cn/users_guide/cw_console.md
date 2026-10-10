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

- **SDR**：直接复用 SkyRoof 现有 48 kHz Slicer 音频；
- **WASAPI Capture**：打开选定的 Windows 录音端点；
- **RS-BA1 Loopback**：对选定的 Windows 播放端点做 loopback capture，
  自动混合为单声道并重采样到 48 kHz。

RS-BA1 Loopback 捕获的是**整个播放端点的混音**，不是只隔离 Remote
Utility 进程。若希望只接收 RS-BA1 音频，建议给 Remote Utility 使用独立的
播放端点。

切换音频源，或者在同一种源中换到另一个设备 ID，都会开始新的 CW timeline；
旧设备的 Track/Transcript 不会和新设备混在一起。

CW 接收默认关闭。确认输入源后，在面板中按 **Start**。

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

表格和瀑布优先显示稳定的多帧身份：

- **H123**：AssociationHint 稳定身份；
- **T17**：尚无 Hint 时使用临时 TrackId。

因此两个 CW 载波交叉时，即使底层 Track 实例发生重建，UI 也尽量保持同一
视觉身份。

### 瀑布叠加

CW 瀑布跟随当前 Scanner AF 范围。轨迹叠加显示：

- Active / Hold / Ambiguous；
- 当前选中的 Lane；
- Kalman 频率状态的半透明 **±2σ** 不确定度带。

±2σ 是跟踪置信度，不是接收滤波器带宽。

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

## HamNoise 状态

HamNoise Classic 和 CW V2 目前只保留为 **benchmark-only 研究后端**，
不出现在 CW Console，也不随正式安装包发布。

正式解码仍使用 raw wideband + activity-aware 多 Track 分离链。

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
