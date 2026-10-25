# SkyRoof 内嵌 SkyCAT

SkyRoof 可直接托管审查后的 [Iu-yang1/SkyCAT](https://github.com/Iu-yang1/SkyCAT) 1.8 版本，将 CI-V 串口、CAT、多普勒、CW Keyer 和 IC-9700 频谱端口统一纳入 SkyRoof 生命周期。**默认关闭**，升级安装后不会抢占你原来的电台串口。

## 配置

进入 **Settings → CAT → Embedded SkyCAT**，填写 Windows CI-V 串口（例如 `COM9`），IC-9700 型号为 `3081`，波特率按电台实际设置。开启 **Enable Embedded SkyCAT**。在 **RX CAT**、**TX CAT** 的属性中勾选 **Use Embedded SkyCAT** 和 **Enabled**，SkyRoof 即经由内嵌 TCP CAT 服务完成频率、模式、PTT 等指令操作。

服务默认仅监听 `127.0.0.1`，各端口如下：

| 用途 | 默认端口 |
| --- | ---: |
| CAT / rigctld 协议 | 4532 |
| WSJT-X 兼容接口 | 4534 |
| IC-9700 频谱数据 | 4535 |
| Remote Control Switch | 4537 |
| CW Keyer | 4538 |

通过 **Tools → Embedded SkyCAT Status** 查看服务状态，通过 **Tools → Open Log Archive Folder** 查看运行日志。实际串口握手、CW 发射和多客户端行为需实机确认，任务线程运行中不等于设备已成功连接。

## 安全约束

不要让独立 skycatd、RS-BA1 串口桥接与内嵌 SkyCAT 同时打开同一 COM 口。内嵌模式会检查 TCP 端口是否已被占用，冲突时拒绝启动。多客户端操作统一经过 SkyCAT 的串口命令锁；PTT 和 CW Keyer 按会话所有权清理，避免关闭 SkyRoof 时误操作其他程序控制的发射状态。正常退出时先释放 SkyRoof 的发射及 CW 会话，最后恢复 IC-9700 的 CI-V Transceive 并释放串口。

源码集成使用锁定提交的 `Vendor/SkyCAT` Git 子模块；本机构建需执行 `git submodule update --init --recursive`。适配和设计说明参见 [英文版完整文档](../../fork-guide/embedded-skycat.md)。
