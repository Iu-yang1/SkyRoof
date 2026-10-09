# 转台、多航点 PARK、主题和可靠性

**语言：** [English](../../fork-guide/rotator-and-ui.md) | 简体中文

## Hamlib rotctld 转台（PR #22–23、#25–28、#34）

在 **Settings → Rotator Control** 填写 `rotctld` 的主机和 TCP 端口，通常是 **127.0.0.1:4533**；根据实际转台机械可转范围、线缆与天线安装方式设定方位角、俯仰角限制、偏移和步进。GS-232B 兼容的转台可由 Hamlib 服务。实际 COM、波特率和转台协议应配置在 `rotctld` 一侧，而不是 SkyRoof 的 TCP 连接设置里。

fork 修复了 rotctld 应答、运动重试、重复 Track 命令与 STOP/断线切换问题。转台状态处右键可弹出方向轮手动控制卡，包含四向点动和 PARK；适用于**没有正在执行卫星自动跟踪**的状态。

**安全规则：** 正在跟踪过境卫星时，手动点动会被锁定。需要手动操作请先停止跟踪。务必观察天线运动；机械范围/角度偏移配置不正确时立即停止。

### 有序多航点 PARK

右键 **PARK** 编辑路径/预设，支持航点 **Add / Update / Delete / Move** 和上下排序；原来的单点 PARK 也继续可用。运行时按保存顺序**依次**到达各点，而不是一次性对所有航点发命令。

前进下一航点必须获得 **两次不同且新鲜的 rotctld 实际位置回报**，与目标的 AZ/EL 偏差均在约 **1.5°** 内。断开连接、无效反馈、反馈超过 **12 秒**未更新或当前航段超过 **5 分钟**会终止路径。对于允许转动 **450°** 的转台，方位角按机械原始角度比较，不会强制对 360 取模。STOP、手动控制或恢复自动跟踪会按状态取消旧路径。**命令 ACK 不等于天线已经转到目标**。

## 主题、设置保存与恢复（PR #12–13、#30–35）

在 **Tools → Theme** 可选择 **System**、**Light**、**Dark**、**GitHub Light**、**GitHub Dark**。当前 fork 的浅色主题使用 **`#5BCEFA` 粉蓝系蓝色**、**`#F5A9B8` 粉色**、**`#FFFFFF` 白色**；GitHub 两套主题采用对应的亮色/暗色设计语义。

主题通过 `Settings.json` 持久化，且在 DockPanel 创建前初始化，因此某些切换可能需要重启或重新创建窗口才能完整生效。IC-9700 示波曲线可以保留独立的高对比度仪表风格，不能假定它一定随全局主题变色。

Dock 布局、列宽等用户设置会保存。`Settings.json` 采用临时文件替换并保留 `Settings.json.bak`；启动发现文件损坏时，可保存经过脱敏的 `.damaged-*.json`，从备份或默认值恢复。敏感凭据尽可能使用 Windows DPAPI 保护，旧明文配置也具有迁移处理。

顶部 **RF GAIN**（由 SkyCAT 控制电台）与 **AF GAIN**（选定的 RS-BA1 Windows 播放会话音量）是两个不同功能，参见 [CAT / FT4](radio-and-ft4.md)。

## 维护与兼容

- Windows [Compile Check](https://github.com/Iu-yang1/SkyRoof/actions/workflows/compile-check.yml) 包含功能和回归测试。
- WinDivert 打包链校验固定版本压缩包的哈希，并在安装时处理兼容的已加载驱动场景。
- 未特别说明的原有 SDR、遥测、SSTV/SSDV、ADIF、录音与过境规划等功能仍保留，参见[上游用户手册](../../users_guide/overview.md)。

进一步参考：[Rotator Control](../../users_guide/rotator_control.md) · [转台设置](../../users_guide/setting_up_rotator_control.md) · [数据目录](../../users_guide/data_folder.md)。
