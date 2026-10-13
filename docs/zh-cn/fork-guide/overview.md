# SkyRoof Fork：概览与安装

**语言：** [English](../../fork-guide/overview.md) | 简体中文

本文档对应 **[Iu-yang1/SkyRoof](https://github.com/Iu-yang1/SkyRoof)** 的 `master`，以 **VE3NEA/SkyRoof v1.55** 上游代码为基线，介绍 **fork 内已经合并的功能**，不代表上游 [VE3NEA/SkyRoof](https://github.com/VE3NEA/SkyRoof) 也提供这些扩展。原来的[英文用户手册](../../users_guide/overview.md)仍适用于基础面板、SDR 接收、遥测、SSTV/SSDV、FT4、ADIF 和卫星分组等通用功能。

## 安装与运行环境

- **平台：** Windows 桌面，基于 .NET 10 / Windows Forms。构建 `SkyRoof.sln` 时应使用仓库相应的 Windows/.NET 构建工具链；建议先核对 [Compile Check](https://github.com/Iu-yang1/SkyRoof/actions/workflows/compile-check.yml)。
- **获取程序：** 参阅 fork [Releases](https://github.com/Iu-yang1/SkyRoof/releases)、[Actions](https://github.com/Iu-yang1/SkyRoof/actions) 及原有[下载说明](../../download.md)。**通过编译测试的构建产物或未签名测试安装包不等于正式签名发行版**；上游 v1.55 安装包不包含 fork 的全部扩展。
- **可选依赖：** [SkyCAT fork](https://github.com/Iu-yang1/SkyCAT) 提供 CAT/CI-V；RS-BA1 提供 IC-9700 LAN 远控；Hamlib `rotctld` 提供转台接口；**WinDivert** 用于被动 LAN 频谱。
- **首次使用：** 配置台站位置与正确的 CAT、音频、转台设备。发射及转动天线之前，先确保 PTT 未按下且转台处于安全方位。

## 自 fork 以来的主要扩展

| 领域 | 已经合并的功能 |
|---|---|
| 卫星与轨道 | 自定义 SatNOGS、CelesTrak OMM CSV、手动 TLE/OMM 来源、AutoTLE 后备；JPL DE440s/DE421 或本地 BSP 对月球、太阳、金星的计算 |
| 频率/多普勒 | 分上下行保存的 Base 频率校正、返回基准、重置数据库、参考频率滑动调谐、已校正和未应用多普勒的频率显示 |
| 转发器 | 本地创建、编辑、删除转发器；允许只有上行、只有下行或两者都有 |
| CAT/远控 | SkyCAT 连接、可靠 CAT/PTT、物理/HID 快捷 PTT、CTCSS、FT4 安全改进、NAudio/WASAPI |
| IC-9700 频谱 | 独立停靠频谱页、SkyCAT 原生源、RS-BA1 被动 LAN 源、**实验性**独立 Direct LAN；示波与瀑布设置 |
| 转台 | rotctld 反馈及重试、方向轮快捷卡、跟踪期间手动操作锁定、按真实反馈逐点执行的多航点 PARK |
| 主题与增益 | 粉蓝白浅色配色（`#5BCEFA`、`#F5A9B8`、`#FFFFFF`）、GitHub Light/Dark、SkyCAT RF GAIN 和指定 RS-BA1 播放会话的 AF GAIN |
| 安全与可靠性 | 设置备份恢复、敏感凭据保护、下载数据校验和 Windows 构建测试 |

## IC-9700 三软件并行的推荐架构

| 组件 | 接口 | 控制职责 |
|---|---|---|
| RS-BA1 Remote Utility | IC-9700 LAN、虚拟 CI-V COM | LAN 会话、电台音频 |
| SkyCAT `skycatd` | 独占一个 RS-BA1 虚拟 COM | CAT 指令串行仲裁 |
| SkyRoof CAT | `127.0.0.1:4532` | 卫星频率、模式、多普勒和电台控制 |
| WSJT-X（可选） | `127.0.0.1:4534` | 受限 rigctl 代理 |
| SkyRoof 频谱（可选） | SkyCAT `127.0.0.1:4535` 或 RS-BA1 被动 LAN | 频谱帧 |
| Remote Control Switch（可选） | `127.0.0.1:4537` | 输入源、压缩器、功率等辅助设置，不操作 VFO/PTT |
| Hamlib rotctld | 默认 `127.0.0.1:4533` | 天线指向 |

**特别注意：** 不同 TCP 端口仍然共用同一条电台 CI-V 串口链路，不能让多个程序直接争抢虚拟 COM。参阅 [CAT 与 FT4](radio-and-ft4.md)、[频谱](spectrum.md)和 [SkyCAT 中文指南](https://iu-yang1.github.io/SkyCAT/zh-cn/skycatd.html)。

## 文档目录

[卫星数据与频率](satellite-data-and-tuning.md) · [CAT、FT4 与远控](radio-and-ft4.md) · [IC-9700 频谱](spectrum.md) · [转台与主题](rotator-and-ui.md) · [排障与 PR 历史](troubleshooting-and-history.md)。

**功能状态说明：** “实验性”指相关代码已经存在，但尚不应视为稳定生产方案；具体 CI-V 能力还取决于电台机型及固件。
