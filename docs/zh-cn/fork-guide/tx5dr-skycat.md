# TX-5DR 与 SkyRoof 内嵌 SkyCAT：安全并行工作

本文说明各 TCP 端口的真实能力，防止将返回成功误认为电台已调频。

| 端口 | 服务 | TX-5DR 兼容性 |
| --- | --- | --- |
| 4532 | SkyCAT 主 CAT 命令服务 | 不是完整 Hamlib NET rigctl，不应直接假定兼容 |
| 4534 | WSJT-X 受限 Hamlib NET rigctl 代理 | 频率、模式、PTT 可读，CAT PTT 采用共享租约；频率/模式/VFO/Split/亚音写入返回成功，但**不会发送到电台** |
| 4535 | IC-9700 二进制频谱流 | 非 rigctl |
| 4537 | Remote Control Switch 辅助设置 | 非 rigctl / PTT |
| 4538 | IC-9700 CW Keyer | 非 rigctl |

SkyRoof 使用内嵌 SkyCAT 时，CI-V 串口只应由 SkyCAT 打开；卫星多普勒、频率、模式由 SkyRoof 单独管理。TX-5DR 的 Hamlib 独立进程和 #97 TONE/CSQL 修复，不会让 4534 的写入屏蔽失效。4534 对 F/M/C/U 等命令返回 RPRT 0 **不代表硬件写入成功**。

在仓库根目录执行以下 Windows PowerShell 只读诊断：

    .\scripts\Probe-Tx5drSkycat.ps1

它只访问 127.0.0.1:4534，发送 get_info、f、m 查询，不改变频率、模式或 PTT。

如果要由 TX-5DR 独立控制 CAT，须先停止 SkyRoof 对同一电台的 CAT 控制，并选用已经验证的完整连接；不能把 4534 或 Switch 端口当成完整 rigctl。进行任何发射实机测试前，应核对硬件 PTT 已确实解除。

详见 [内嵌 SkyCAT](embedded-skycat.md) 与 [SkyCAT 命令说明](https://github.com/Iu-yang1/SkyCAT/blob/master/docs/zh-cn/skycatd.md)。
