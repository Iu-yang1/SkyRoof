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


### 顶栏滚动条与 UTC 时间修复

- 主顶栏明确禁用滚动，只在固定设计画布上等比例缩放全部控件。缩放引擎在初次 100% 显示以及 1 像素尺寸变化时也会刷新绘制区域。
- Icom LAN Spectrum 工具栏只在基准宽度下测量并固定排布一次，冻结按钮尺寸和坐标后再整体缩放；停靠窗口变化时绝不创建水平滚动条或自动换行。
- 时钟使用固定的紧凑尺寸标签，并以 `yyyy-MM-dd UTC` 或 `yyyy-MM-dd LT` 明确标记时区；点击 **UTC** 固定选择 UTC，点击 **Local** 固定选择计算机本地时间。保留原有可切换及持久化设置，不再出现混合语言的长日期裁切。
- **注意：**合并 PR 和通过 CI 不会自动替换本机原有 SkyRoof 1.57 可执行文件。请从对应 **Windows Compile Check** 下载最新未签名测试安装包，关闭旧程序后安装验证。自动化覆盖 100%、125%、150% 比例，但 Windows 实际 DPI 截图、mpv 和转台安全硬件仍需实测。

## 整体等比例缩放（不重新排版或添加滚动条）

已撤销 PR #84 的自适应换行及滚动布局实验。主工具栏和 Frequency Control 现在采用**整体等比例缩放**：控件尺寸、点击区域、行列间距与字体均按同一个缩放系数变更，且每次都从不可变的原始设计尺寸计算，避免连续拖动窗口后累积像素误差。

- **主工具栏：** 卫星选择、照片、上下行频率、增益、旋转器、PTT 与时钟均保留原有顺序，主窗口缩窄时整组一起缩小，不再隐藏照片或让 PTT 遮住时钟。
- **Frequency Control：** Downlink/Uplink Base 与 Tuning 保持原来的两列排列及位置关系，缩小时连同字体一起按比例缩小，不通过滚动或重排内容来规避裁切。
- 在极窄窗口下文字也会相应变小，这是此模式的预期效果；扩大窗口即可恢复可读性。
- 后续改动把固定设计基线推广至 **CW Console、Icom LAN Spectrum、ONVIF Camera、QSO Entry**，并对旋转器方向盘弹出卡片按显示器 DPI 缩放。频谱、瀑布图和 mpv 原生视频嵌入区通过缩放后的 WinForms ClientRectangle 处理绘制与鼠标坐标；本次不修改 FFT、CW 解码、CAT 或转台指令算法。
- Icom 频谱工具栏仅在初始设计尺寸下排布一次，之后冻结坐标；CW 工具栏取消滚动，QSO 卡片采用固定原始位置，不再动态换行。极小窗口可能导致字体过小，应主动扩大停靠区域。

### Windows 实机与硬件验收清单（CI 无法替代）
- 在 **100%、125%、150% DPI** 下分别以停靠及浮动方式反复改变五个窗口大小，检查状态文字、TX 宏、频率、QSO 卡片、视频顶部控制和鼠标点击区域；不得出现新增滚动条、自动换行或累计坐标漂移。
- CW 接收期间检查瀑布图点击槽位、AF 缩放/滚动及频率选择；Icom 频谱接收期间检查鼠标滚轮、频率调谐、瀑布图历史与 475-bin 映射，并对照 RS-BA1。
- ONVIF 播放时检查 **mpv 原生子窗口**与黑色视频承载控件同步缩放，确认 D3D11VA 硬解正常，验证重连/停止。
- **转台保护：**先使用模拟器，再连接带独立硬件急停的 GS-232B；注入不可能的反馈角度跳变，核对 STOP 锁存、确认状态以及 Track/PARK 不得擅自恢复；实测 Track 开启前后 rotctld CPU 占用与 1° 指令合并效果。
- **ONVIF 发现：**使用真实相机测试各网络接口的多播发现、ONVIF HTTP Device Service 与 RTSP 554 端口区分、Media1/2、视频重连。
- 自动化测试只覆盖布局结构、缩放、CW/QSO 固定位置和方向轮命中测试，**不代表**已经完成 Windows DPI 交互、GPU 视频、实机射频和转台安全验收。
