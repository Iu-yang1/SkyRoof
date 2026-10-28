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


### 2026-10-28：发射期间旋转器反馈暂时中断的诊断与恢复

SkyRoof 原有 Native Rotator 默认响应超时为 600 ms；短时间连续 3 次 TCP 查询超时后，应用会关闭仍可能有效的 TCP 会话并重连。电台发射时如对 USB 串口、电源、TCP/串口桥或控制线造成共模射频干扰，可能表现为“刚发射就掉线”，但**软件日志无法单独证明 RFI 是根因**。

现有新配置默认响应超时为 1500 ms。旧配置中已经保存的超时值不会被强制覆盖；可在 Rotator Settings 手动设置 **Native Response Timeout (ms)** 为 1500–2000，通常不建议无限调大。已经成功同步过的 Native TCP/Serial 会话，遇到位置回复缺失先保留传输连接，待连续至少 6 次且 7 秒无有效回复后才尝试重新连接；首次没有收到有效回复的会话采用 4 次/4 秒的门限。一次查询失败立即暂停下发新的运动指令，而非继续依赖不可靠的位置数据。

**运动中失去反馈是另一种安全等级。** 如果存在未确认已经停止的目标移动或连续点动，且反馈中断达到至少 2 次和 max(2.5 秒，3 × 轮询间隔)，系统请求 STOP 并锁住自动运动。网络发送 STOP 不等于电机物理停转确认：请人工检查方位角、俯仰角、机械余量后再解除故障，切勿在天线运动时仅为了提高可用性而放宽安全联锁。

排查建议：同时记录电台 PTT 开关时间（UTC）、发射功率、发射频段、USB 设备插拔或 Windows 网络事件，并与日志中 `Native rotator position reply timeout`、`feedback recovered`、`transport established` 的时间关联。依次以 0 W/仅接收、低功率和正常功率测试；若掉线只在特定功率或频段出现，应优先检查 RC-3040S 控制器和串口桥供电、网线/USB 线的共模电流、屏蔽与接地、铁氧体扼流及天馈系统的驻波/泄漏。**不建议在实际运动控制期间通过关闭 STOP 保护来进行发射测试。**

这次只修改 Native TCP/Serial 反馈的超时与安全门限；Legacy rotctld、PstRotator UDP 和独立控制子进程 5 秒看门狗保持原行为。测试以模拟 TCP 丢包为依据，不能替代实机 EMC 验证。
