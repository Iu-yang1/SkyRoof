# Embedded SkyCAT / 内嵌 SkyCAT

## English

SkyRoof can host the pinned, upstream-synchronized SkyCAT 1.8 CAT server **inside the SkyRoof process**. The existing external rigctld and separately launched skycatd options remain supported.

1. Open **Settings → CAT → Embedded SkyCAT**. Set **CI-V Serial Port** to the Windows port used by your radio, choose the radio model (for IC-9700 use **3081**), and configure the baud rate. Set **Enable Embedded SkyCAT** to True.
2. Under **RX CAT** and/or **TX CAT**, turn on **Use Embedded SkyCAT** and set **Enabled** as needed. This routes SkyRoof's existing Doppler and PTT engines through the in-process loopback server; the external Host/Port fields are ignored for those selected radios.
3. The standard loopback endpoints are **4532 main CAT**, **4534 WSJT-X**, **4535 IC-9700 scope stream**, **4537 Switch**, and **4538 CW Keyer**. The main CAT server is loopback-only, not open to the LAN.
4. Open **Tools → Embedded SkyCAT Status** for service state, or **Tools → Open Log Archive Folder** for connection/protocol logs.

Do **not** open the same COM port with another skycatd, RS-BA1 serial bridge, or third-party rig controller simultaneously. SkyCAT owns that port while enabled.

On SkyRoof exit, its CW and PTT clients are stopped before the hosted SkyCAT service releases its radio mode configuration and closes the serial port. Only PTT asserted by SkyCAT/SkyRoof may be released; observed external PTT is not owned. IC-9700 transceive restoration is delayed until full service shutdown or explicit mode switch, not the departure of one of several clients.

This mode reuses the reviewed **Iu-yang1/SkyCAT** Git submodule at a fixed commit. Local source builds require `git submodule update --init --recursive`. SkyRoof CI and the packaged installer include the matching radio commandset JSON files.

## 中文

SkyRoof 支持将经上游同步的 SkyCAT 1.8 **直接运行在 SkyRoof 进程内**，无需手动启动独立 skycatd。默认关闭，不影响原有外部 rigctld/SkyCAT 使用方式。

1. 进入 **设置 → CAT → Embedded SkyCAT**，指定 IC-9700 对应的 Windows CI-V 串口、型号 **3081** 与波特率，然后打开 **Enable Embedded SkyCAT**。
2. 在 **RX CAT**、**TX CAT** 中按需勾选 **Use Embedded SkyCAT** 和 **Enabled**，多普勒、PTT 等继续走 SkyRoof 原有 CAT 控制引擎。
3. 默认回环端口为 **4532 主 CAT、4534 WSJT-X、4535 频谱、4537 Switch、4538 CW Keyer**，不会绑定公网接口。
4. 使用 **Tools → Embedded SkyCAT Status** 查看服务状态，**Tools → Open Log Archive Folder** 检查日志。

不要让其他软件同时占用同一 COM 口。正常退出时先释放 SkyRoof 的 CW、PTT 控制权，再停止内嵌 SkyCAT，避免意外解除其他应用持有的发射状态。多个 TCP 客户端分别断开时，不会提前执行整个电台的 teardown。

本地从 Git 克隆源码后需要运行 `git submodule update --init --recursive`。实际串口连接及发射安全仍需实机验证。
