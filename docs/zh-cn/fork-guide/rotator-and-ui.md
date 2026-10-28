# 转台、多航点 PARK、主题和可靠性

**语言：** [English](../../fork-guide/rotator-and-ui.md) | 简体中文

## Hamlib rotctld 转台（PR #22–23、#25–28、#34）

在 **Settings → Rotator Control** 填写 `rotctld` 的主机和 TCP 端口，通常是 **127.0.0.1:4533**；根据实际转台机械可转范围、线缆与天线安装方式设定方位角、俯仰角限制、偏移和步进。GS-232B 兼容的转台可由 Hamlib 服务。实际 COM、波特率和转台协议应配置在 `rotctld` 一侧，而不是 SkyRoof 的 TCP 连接设置里。

fork 修复了 rotctld 应答、运动重试、重复 Track 命令与 STOP/断线切换问题。转台状态处右键可弹出方向轮手动控制卡。四个方向现在改成真正的**持续运动**：鼠标按下发送 Hamlib `M direction -1`（UP=2、DOWN=4、LEFT=8、RIGHT=16），松开鼠标或丢失鼠标捕获立即发送 `S` 停止；不再每 130 ms 递增目标角度并反复发送绝对 `P` 命令。这与 GS-232B / 云端卫星类方向键的“按下开始转、松开停止”语义一致。

方向盘旁的 **Track step** 现在绑定到真正的 `RotatorSettings.StepSize`：修改后立即保存并重建当前过境的优化路径，因此会同时影响自动跟踪重新下发位置命令的误差门限和 PathOptimizer，不再只是旧版手动 jog 的临时步长。

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


## 实际角度突变安全锁定

当 rotctld 连续两次间隔不超过 2 秒的新鲜反馈，其方位角或俯仰角变化超过 **Position Jump Limit**（默认 30°），程序即在通信线程发送 Hamlib **S STOP**，停止后续自动/手动命令并提示核对**实际天线方向**及反馈传感器接线。弹窗明确区分 STOP 已被确认与 STOP 未确认；若没有确认必须自行确认电机已停转。359°→1° 的正常角度环绕不会误触发，但 450° 等扩展机械角度保留原始变化。检查后重新应用 Rotator Control 设置才会解除锁定；自动重连不会解锁。本功能并不替代堵转或机械限位保护。

## Track 模式 CPU 占用及 GS-232B 指令精度

SkyRoof 默认无论 Track 是否开启，都会以约 **300 ms** 周期发送位置查询 `p`。Track 开启时，在指向误差超过阈值后，约每秒计算并可能下发一次 `P az el` 目标。Hamlib 的 **GS-232B** 驱动最终将目标角度取整，串口发送 `Waaa eee` 指令。

现在 Rotator Control 设置中提供 **Position Command Resolution (deg)**，按后台控制器实际定位分辨率去除重复的 `P` 指令。通用默认仍为 **0.1°**，不影响高精度旋转器；使用 **GS-232B** 时建议配置为 **1.0°**，然后重新应用旋转器设置。同一整数目标不会因为追踪插值的微小变化反复写入串口；不同目标、手动控制、PARK、安全 STOP 与重新连接仍正常工作。停止命令会使缓存失效，允许再次下发相同目标。

此处修复的是**确认存在的冗余指令问题**，并不构成 `rotctld.exe` CPU 占用 20% 的根因已经确定的证明。如仍占用较高，应分别测量静止、手动旋转、Track、断开 SkyRoof TCP 客户端后的 rotctld CPU，并检查 Hamlib 版本、串口驱动及重复/失败重试。不要为降低 CPU 盲目关闭安全 STOP 或位置反馈。

## RC-3040S LAN：取代实验性 NRL2 UDP 驱动

SkyRoof 已**完全删除 NRL2 UDP 旋转器驱动**及其 RotorID、APP SID、公网中继服务器设置与专属操作窗口。协议编解码、发现、历史设置字段定义及旧版测试统一归档到 [DTracNRL2.NET](https://github.com/Iu-yang1/DTracNRL2.NET/tree/main/docs/archive/skyroof-nrl2)。

请在 **Settings → Rotator Control** 选择 **Controller Driver: Native / Native Protocol: Rc3040sLan**，设置控制板的局域网 IPv4 和端口 **60020**；它使用 GS-232B 的 W/C2/S 命令，不依赖互联网。管理接口通过独立的 **WebSocket TCP/60030** 按需读取配置，原始 AZ/EL 速度条在管理窗口内拖动不会自动写 EEPROM，保存与设备重启均需明确确认。

原 NRL2 数字协议值 10 已废弃，不会自动迁移为 Rc3040sLan（值 11），旧配置必须由用户重新确认连接目标并启用云台。、PARK 则继续在局域网模式下工作。


### 2026-10-28：RC-3040S 非 Direct 跟踪可能失步的保护修复

**仅凭 RC-3040S 的 C2=30°，不能保证机械轴真实只转了 30°**。该固件支持 GS-232B 的 Waaa eee 位置目标；PrecisionStop / Trapezoidal / SCurve 是 SkyRoof 软件端的参考轨迹，不是控制电机脉冲的固件命令，速度/加速度/jerk 参数也不是 RC-3040S 可执行的原生速度命令。

源码审查发现，旧版在没有新 C2 反馈时也可能按照调度循环重复推进软件规划器状态，并以约 450 ms 最短间隔发送更新后的 W 目标。此次修复：

- 非 Direct 路径只在**新的、有效且未过期的 C2 反馈**到达后推进一次轨迹，每帧反馈最多触发一个新的规划端点。
- 对下发的整数角度 W 目标再次限幅，每个轴最多领先最新 C2 位置 **1.5°**。这个约束限制的是命令，而非实际物理转角。
- 已经发出运动端点后，连续 **4 秒**没有取得至少 **0.5°** 的反馈进展，发出 STOP 并锁住后续自动目标，待人工检查后重新应用设置。没有电机物理 STOP 确认时，不可称其已停稳。
- 模式、硬件授权和规划限值在 IO worker 启动时固化，防止更改设置时意外退回 Direct 并下发大角度 W。
- 旧的已保存“硬件验证”勾选不会自动授权本次新版本的非 Direct 控制；首次使用前，须以独立角度尺/水平仪/罗盘测量至少两个位置的真实 AZ、EL，并确认 C2 读数和 STOP 行为，再勾选 **Actual shaft AZ/EL independently match C2; STOP tested**。

如果出现“反馈 30°、机械约 60°”，请先停用自动跟踪、PARK 与非 Direct 路径。机械转角与 C2 不一致可能是步进电机失步、控制板电机步数/减速比参数、初始化回零误差或实际视觉判断偏差。仅靠 C2 本身无法自动校正其与真实角度之间的比例关系，必须进行实机多点校准；软件限幅、超时及回归测试不能替代机械验证。尽量先卸下天线负载或在安全的机械余量内测试。
