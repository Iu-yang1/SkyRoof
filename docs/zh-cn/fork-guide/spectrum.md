# IC-9700 原生频谱与瀑布

**语言：** [English](../../fork-guide/spectrum.md) | 简体中文

fork 新增可停靠的 **Icom LAN Spectrum** 面板，与原来的 SDR 宽带瀑布独立。它接收 IC-9700 的 CI-V **`27 00`** 频谱帧，完整扫描通常为 **475 点**，显示频谱曲线、瀑布、频率标记、峰值保持、平均和显示调节。它不是 SDR 基带采样，也不能代替接收机解调。

通过程序的面板/窗口菜单打开 **Icom LAN Spectrum**，先在 **Tools → Settings → Icom LAN Spectrum** 配置数据源。

## 频谱来源与写入控制链分开选择

| Scope source | 依赖 | 实际行为 |
|---|---|---|
| **SkyCat** | SkyCAT 的 `127.0.0.1:4535` 原生二进制频谱流；有效 CI-V 链路 | 直接复用 SkyCAT 当前串口流，不另建 IC-9700 LAN 会话 |
| **RsBa1** | RS-BA1 已开启、LAN 上有 CI-V 数据、WinDivert、相应权限 | **被动抓取** RS-BA1 现有 UDP 会话，不登录电台；关闭 RS-BA1 或它的频谱窗口后，数据可能停止 |
| **DirectLan** | 电台 IP、LAN 账号密码及空闲会话资源 | **实验性独立鉴权客户端**，只能手动启动，可能与现有 RS-BA1 会话冲突 |

**Scope control path** 是另外一个选项：`Auto`、`SkyCat`、`DirectLan`、`ReadOnly`。Auto 会按来源选择控制路径，但 **Auto + 被动 RS-BA1 通常是只读**。希望被动接收 RS-BA1 频谱，同时用 SkyCAT 改变频谱模式时，需要将 control path 显式设成 **SkyCat** 并保持 CAT 连接。`ReadOnly` 完全不写入频谱控制指令。

**RS-BA1 与 SkyRoof 并行时推荐：** `Source = RsBa1`，`Control path = SkyCat`（前提是 SkyCAT 已连接且支持对应 CI-V scope 指令）。没有可用控制通道就维持只读。**不要仅为了提高帧率启动实验性 Direct LAN 争用会话**。

## 频谱控制和显示

新版面板提供本地瀑布与曲线分割比例、缩放、hold/peak hold 与清除，及：

- MAIN/SUB/Auto 波形选取；
- CENTER、FIXED、SCROLL 范围模式、span、边界组和参考电平；
- 扫描速度、VBW、TX 时频谱、CENTER 标记参考等（以电台实际支持为准）；
- 本地平均/平滑、瀑布亮度/对比度/色表/历史行数；
- 与 CAT 状态相关的频率标记和鼠标调谐。

频谱状态采用**增量读回**：每个 CAT 周期只查询一项并验证答复，允许个别不支持的寄存器缺失而保留其他已确认字段，避免长时间占用与 PTT 共用的命令锁。某次读回失败不能表示所有选项都已成功同步；界面可能显示正在同步或只读。

**界面 FPS 不等于电台完整扫描 FPS。** RS-BA1 LAN 可能把 475 点 `27 00` 一次性发送，而虚拟 COM 可能将一个扫描拆成多条消息；程序集兼容不同形式，但不保证恒定帧率。

## WinDivert 与管理员权限

RS-BA1 被动捕获需要兼容的 **WinDivert DLL/驱动**及管理员权限。构建/安装链采用 **WinDivert 2.2.2 x64**。重装时 `WinDivert64.sys` 可能已由某程序加载；不要随意删除正在使用的驱动或盲目结束 RS-BA1。请使用受支持的同版本复用方案，或先正常关闭依赖程序再替换。

常见三种不同故障：

1. 关闭 RS-BA1 频谱或会话后，不再有被动扫描帧；
2. SkyCAT 普通 CAT 可用，但原生 `4535` 频谱没有有效数据；
3. 已经显示频谱，但 scope control path 处于只读或者电台拒绝写入。

**Direct LAN 目前仅限实验验证**；即使凭据在设置文件中受到保护，也不要在公开 Issue 上传未经脱敏的登录信息或原始鉴权网络报文。

更多：[SkyCAT](https://iu-yang1.github.io/SkyCAT/zh-cn/skycatd.html) · [原 SDR 瀑布](../../users_guide/waterfall_display.md)。
