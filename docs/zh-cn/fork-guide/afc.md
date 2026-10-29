# FM / CW / SSB 多普勒辅助 AFC

**语言：** [English](../../fork-guide/afc.md) | 简体中文

AFC 使用单独的 12 kHz IF PCM 输入，在 SkyRoof 原有卫星多普勒与人工
调谐结果上叠加**临时 RX 修正量**，不修改转发器频率、永久手动修正、
RIT/XIT 或 TX 上行频率。有关 IC-9700 USB/LAN 12 kHz IF 输出，见
[Icom 官方说明书](https://icomuk.co.uk/files/icom/PDF/productManual/IC-9700_ENG_Basic_6.pdf)。

## 使用步骤

1. 在 IC-9700 设置 `SET > Connectors > USB AF/IF Output >
   Output Select > IF`。选择承载 IF 的 USB 声卡输入。
   **12 kHz 是实际模拟中频，不是采样率或复数 IQ。**
2. 在 SkyRoof 的**下行频率文字上右键**，打开 **FM / CW / SSB AFC...**，
   或双击下行频率。选择正确的 Windows IF 采集设备，点 **Start IF**。
3. **Assist**：只读频率观测，不改变 CAT。**Auto**：有可靠观测和
   有效频率方向时允许仅对 RX 进行自动补偿；未确认方向不会盲目写频。
   不要求每次打开软件都重新进行人工标定。
4. CW：在 **Target IF offset (Hz)** 中选中目标频率，按 **Lock Here**
   锚定当前音调。多台 CW 同时存在时，应避免选择错误的邻近轨迹。
5. SSB：普通抑制载波话音无法唯一确定绝对频率。只有实际存在稳定
   导频/CW 信标时，才开启 **SSB reference available** 并允许 Auto。
   无参考时保持 Assist。

## 数据通道和安全规则

- **CI-V 27 00 频谱不是 IQ**，不能作为该 AFC 的 IF 输入。
- Windows WASAPI 输入使用 48 kS/s 实数 PCM，再数字下变频、
  FIR 滤波和抽取为复数基带。
- 原 CW Console 需要的是**解调 AF 音频**；若 USB 被设置为 IF，
  CW 解码应改用独立的 AF 通路，例如正确配置的 RS-BA1 播放回环。
- Icom 手册确定了 12 kHz IF 输出，但未给出所有模式及滤波设置下
  一致的 IF/CAT 频率方向。将 **RF/IF direction = 0** 保持默认时，
  AFC 会尝试从已锁定信号及 CAT 调谐读回的相对位移自动推断。
  `+1`、`-1` 只用于已确认的频率映射。
- 仅由现有 SkyRoof/SkyCAT CAT 控制链调谐 RX，AFC 不另建第二个
  远程控制会话，不修改 TX。
- PTT、音频时间线改变、模式切换和信号丢失会冻结或重置 AFC
  更新。频率方向未确认时不执行自动 CAT 修正。

## 模式能力

| 模式 | 频率估计 | 当前策略 |
|---|---|---|
| FM | 多时间窗鉴频均值、稳定性和置信度 | 间歇 FM 失号保持，低速修正 RX |
| CW | IF 窄带 FFT 与峰值插值 | 跟随所选 IF 频点，不随意改为最强台 |
| SSB | 明确的稳定参考音频峰 | 有参考可 Auto，无参考仅 Assist |

## 验证边界

仓库提供合成 IF 的 xUnit 测试，覆盖 CW 音调、SSB 无载波、
RF/IF 频率方向未知、信号失效、PTT 以及音频源代次切换。
**这些测试并不等于 IC-9700/IC-705 实机验收**。如需定量证明
不同固件与滤波带宽的表现，需要录制已知 RF 输入、12 kHz IF
PCM，以及同时刻的 CI-V RX 频率读回。

参考：
[STRF](https://github.com/cbassa/strf)、
[SDRangel Frequency Tracker](https://github.com/f4exb/sdrangel/blob/master/plugins/channelrx/freqtracker/readme.md)、
[Rife–Boorstyn 1974](https://doi.org/10.1109/TIT.1974.1055282)、
[Kay 1989](https://doi.org/10.1109/29.45547)、
[Boashash 1992](https://doi.org/10.1109/5.135378)。
