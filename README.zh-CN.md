# SkyRoof Plus

**语言：** [English](README.md) | 简体中文

**SkyRoof Plus** 是面向业余无线电与卫星通信爱好者的 Windows 桌面软件，基于 [VE3NEA/SkyRoof](https://github.com/VE3NEA/SkyRoof) 开发。本 Fork 已整合上游 v1.56，并在此基础上持续增加新功能。下方 **Fork 扩展功能** 仅说明本仓库维护的扩展，**不代表上游版本全部具备**。

[**下载最新版本**](https://github.com/Iu-yang1/SkyRoof-Plus/releases/latest) · [中文扩展指南](docs/zh-cn/fork-guide/overview.md) · [English Guide](docs/fork-guide/overview.md) · [在线文档](https://iu-yang1.github.io/SkyRoof/) · [更新日志](FORK-CHANGELOG.md) · [构建状态](https://github.com/Iu-yang1/SkyRoof-Plus/actions)

<p align="center">
  <img src="docs/images/skyroof_icon.png" alt="SkyRoof 图标" width="140">
</p>

## Fork 扩展功能

以下内容汇总截至 **SkyRoof Fork v1.64（2026 年 11 月 2 日）** 已整合的主要能力。具体设备设置、使用限制及验收进度以各功能文档为准。

| 领域 | 本 Fork 新增或改进的功能 | 详细文档 |
|---|---|---|
| **轨道来源与星历** | 支持自定义 SatNOGS、CelesTrak OMM、手动 TLE/OMM 数据源及后备下载；可使用 JPL DE440s/DE421 或本地 BSP 星历计算受支持的太阳系目标。 | [卫星数据](docs/zh-cn/fork-guide/satellite-data-and-tuning.md) |
| **频率与转发器数据库** | 上下行独立 Base 频率修正、持久化偏移及重置；可以创建、编辑、删除仅上行、仅下行或双向转发器记录；支持多普勒联动调谐。 | [频率与转发器](docs/zh-cn/fork-guide/satellite-data-and-tuning.md) |
| **CAT 与卫星远控** | 集成 IC-9700 的 SkyCAT/RS-BA1、受限 WSJT-X CAT 代理、CI-V/PTT 安全处理、CTCSS、人工 PTT 控制及 FT4 操作优化。 | [CAT 与 FT4](docs/zh-cn/fork-guide/radio-and-ft4.md) · [SkyCAT](docs/zh-cn/fork-guide/embedded-skycat.md) |
| **IC-9700 LAN 频谱** | 可停靠频谱与瀑布图、接收通带标记、平均与缩放等控制；支持 SkyCAT 数据源或通过 WinDivert 被动捕获 RS-BA1 数据。独立 Direct LAN 路径仍属**实验功能**。 | [频谱指南](docs/zh-cn/fork-guide/spectrum.md) |
| **IF → AF 解调与 WSJT-X** | 将 48 kS/s、**12 kHz 实数 IF** 转换为 SSB/CW/FM 音频，提供对应带宽、增益、有界缓冲及虚拟声卡输出；支持可选安装原版基础 VB-CABLE，状态栏显示 IF/DDC/AF/V-MIC 链路状态。 | [IF → AF 使用指南](docs/zh-cn/fork-guide/if-af-test.md) |
| **多普勒辅助 AFC** | 利用 FM 及 CW/导频信号进行接收频偏估计，提供置信度门控、受限的**仅接收 RX CAT** 频率修正、失号 HoldLast 机制与调试界面；不修改上行或 TX 频率。 | [AFC 指南](docs/zh-cn/fork-guide/afc.md) |
| **CW Console / Pileup** | Skimmer 风格横向瀑布图、多信号跟踪、最多八路接收槽位、DeepCW 辅助解码、连续消息记录、可选 HamNoise 处理，以及人工触发的 CI-V CW 发报和 F1–F8 宏。 | [CW 控制台](docs/zh-cn/users_guide/cw_console.md) |
| **SSTV Console** | 可停靠 SSTV 收发、图库和通联日志；提供 **43 种发射配置**、VIS 处理、AF/IF 接收、图片和多达八层文字模板、UTC 通联记录及 ADIF 导出。接收能力取决于相应制式解码器。 | [SSTV 指南](docs/zh-cn/fork-guide/sstv-console.md) |
| **旋转器与网络摄像头** | 基于实际反馈的多航点 PARK、跟踪与手动控制互锁、GS-232B/rotctld 增强、**RC-3040S 原生 LAN 控制**，以及支持硬件视频解码的 ONVIF 摄像头面板。 | [旋转器](docs/zh-cn/fork-guide/rotator-and-ui.md) · [RC-3040S](docs/zh-cn/fork-guide/rc3040s-lan.md) · [ONVIF](docs/zh-cn/fork-guide/onvif-camera.md) |
| **界面与可靠性** | GitHub Light/Dark、粉蓝白主题，设置窗口与部分控制界面支持简体中文/English 切换；修复布局/DPI 问题，支持配置备份恢复、敏感凭据保护及 Windows 自动化回归测试。 | [Fork 概览](docs/zh-cn/fork-guide/overview.md) · [更新日志](FORK-CHANGELOG.md) |

### IC-9700 + WSJT-X 接收链路示例

`IC-9700 USB 12 kHz 实数 IF → SkyRoof IF/DDC/SSB-CW-FM DSP → AF → VB-CABLE → WSJT-X`

将电台 **USB AF/IF Output** 设置为 **IF**，并选定 48 kHz Windows 录音端点及正确声道。在 SkyRoof 设置中启用 **12 kHz IF → AF / WSJT-X**，选择独立的虚拟播放端点（使用 VB-CABLE 时为 **CABLE Input**），最后在 WSJT-X 的录音输入中选择 **CABLE Output**。**严禁将该音频链路输出到电台的发射音频播放设备。** VB-CABLE 基础版为第三方 Donationware，请遵守[原厂许可条款](https://vb-audio.com/Services/licensing.htm)。

[IF → AF 详细配置与限制](docs/zh-cn/fork-guide/if-af-test.md) · [SkyCAT 端口与 CAT 归属](docs/zh-cn/fork-guide/embedded-skycat.md)

### 功能状态与安全说明

- **正式功能与实验功能：** 上述模块已在 Fork 源码及发行版中整合，但不同硬件上的表现可能不同。IC-9700 独立 Direct LAN 频谱仍属于实验功能；AFC 标定精度、音频设备实际时延、CW 解码性能、转台机械运动等仍须实机验证。
- **发射及机械安全：** 使用前应确认实际频率、PTT 与 CAT 控制权、天线方位反馈及机械限位。CI 自动测试通过不等于射频设备和旋转器已完成实机验收。
- **外部依赖：** 部分功能需要 RS-BA1、SkyCAT、WinDivert、VB-CABLE、ONVIF 摄像头或具备 GPU 解码能力的 mpv。应用安装程序可能未签名，实际请核对各次 [Release](https://github.com/Iu-yang1/SkyRoof-Plus/releases) 说明。
- **文档范围：** Fork 文档集中说明上游之外的扩展；基础卫星预测、SDR 与遥测等可参考[上游用户手册](https://ve3nea.github.io/SkyRoof/)。现有在线 Fork 文档仍部署在 `/SkyRoof/` GitHub Pages 路径。

## 编译项目

在 Windows 上使用兼容的 **Visual Studio 2026** 打开 `SkyRoof.sln`，根据仓库要求安装对应 .NET / Windows Forms 工具链（当前为 **.NET 10**）。具体构建配置以 [Compile Check](https://github.com/Iu-yang1/SkyRoof-Plus/actions/workflows/compile-check.yml) 为准。另见 [Fork 安装说明](docs/zh-cn/fork-guide/overview.md)。

## 致谢与交流

- 原始项目：[VE3NEA/SkyRoof](https://github.com/VE3NEA/SkyRoof) · [上游网站](https://ve3nea.github.io/SkyRoof/)
- 本 Fork：[更新历史](FORK-CHANGELOG.md) · [第三方依赖与致谢](docs/credits.md)
- 交流社区：[SkyRoof Discussion Group](https://groups.google.com/g/skyroof)
