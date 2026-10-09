# CAT、IC-9700 远控、PTT 与 FT4

**语言：** [English](../../fork-guide/radio-and-ft4.md) | 简体中文

## CAT 控制架构

SkyRoof 保留兼容 **Hamlib rigctld 的 CAT 接口**；IC-9700 卫星模式推荐使用增强后的 [SkyCAT fork](https://github.com/Iu-yang1/SkyCAT)。在 **Tools → Settings → CAT Control** 设置 RX CAT / TX CAT，使用本机地址 `127.0.0.1`、端口 **4532**。单台电台可以同时作为 RX/TX；仅跟踪卫星时也允许关闭 CAT。

示例（`COM9` 必须替换为 RS-BA1 分配给 CI-V 的真实虚拟 COM）：

```powershell
skycatd.exe -m IC-9700 -r COM9 -s 115200
```

- SkyRoof CAT：**4532**
- WSJT-X 的受限 SkyCAT rigctl 代理：**4534**
- SkyCAT 原生二进制频谱：**4535**
- [Remote Control Switch](https://github.com/Iu-yang1/IC-9700-Remote-Control-Switch) 辅助设置：**4537**

**SkyRoof 不应连接到 4534/4535/4537 作为普通 CAT。** SkyCAT 独占 COM 并通过命令锁实现串行访问，其他程序不得直接再次打开同一虚拟串口。详细配置见 [SkyCAT 中文文档](https://iu-yang1.github.io/SkyCAT/zh-cn/skycatd.html)。

## CAT、CTCSS 与安全 PTT（PR #5、#10–11、#17、#34–36）

fork 对 CAT 写入应答、超时、重连以及收发转换增加了保护。SkyCAT 的 WSJT-X 代理通过共享 **PTT 租约** 阻止多个客户端同时抢占 PTT；WSJT-X 代理请求的频率、模式、VFO、SAT、CTCSS 等写入会**返回兼容性确认，但不实际转发到电台**，确保 SkyRoof 是多普勒调谐的主要控制者。

**Physical PTT Key** 可以绑定 USB HID 脚踏开关映射的 F13–F24 等键，按下发射、松开接收；**Suppress PTT Key** 用于避免该键继续传入前台应用。请先验证按键映射与可靠松开发射；键盘 PTT 不能替代独立的硬件发射安全保护。FT4 工作线程异常、CAT 断线等路径也补充了失效保护，但仍建议具备随时手动解除发射的方法。

CTCSS 要配置在**上行发射通道**的中继亚音上，不应想当然地给接收也打开 Tone Squelch。多个控制软件并行时应关注实际电台的 Tone 状态。

## FT4、Windows 音频和增益（PR #16、#35）

原有 [FT4 Console](../../users_guide/ft4_console_panel.md) 保留，fork 将活跃 Windows WASAPI 音频后端从 CSCore 迁移至 **NAudio**，并增强 FT4 PTT 异常退出处理。使用 RS-BA1 音频时，必须在 Windows 中选择实际的录音和播放设备；这和 WSJT-X CAT 代理不是同一个配置步骤。

上方工具栏的两类增益需要严格区分：

- **RF GAIN**：IC-9700 由 SkyCAT 发出真实 RF 增益设置/读取，并不是 Windows 喇叭音量。
- **AF GAIN**：RS-BA1 模式通过指定的 **Windows 播放音频会话**调音量；AF GAIN 标题可以选择具体 RS-BA1 进程及播放端点，避免改错其他程序的音量。它不是射频发射功率调节。

使用本地 SDR 时仍可使用原 SDR 的 RF/AF 增益路径。Remote Control Switch 中的 **RF POWER** 是电台发射功率，不等于 RF GAIN 或 AF GAIN。

## Remote Control Switch 与 RS-BA1 并行

先连接 RS-BA1，让 **SkyCAT 作为唯一 CI-V 虚拟 COM 持有者**，然后在 Remote Control Switch 中选择 **SkyCAT TCP**，保存 `127.0.0.1:4537`。该专用端口只允许 DATA OFF/DATA MOD、USB AF/IF、压缩器、压缩等级、CW 速度与发射功率等辅助设置；**不能**写入 SAT、PTT、VFO、频率或工作模式，这些由 SkyRoof 控制。

其他说明：[CAT 设置](../../users_guide/setting_up_cat_control.md) · [FT4 Console](../../users_guide/ft4_console_panel.md) · [变频器](../../users_guide/setting_up_transverter.md)。
