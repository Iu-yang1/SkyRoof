# 卫星数据、星历、转发器与频率调谐

**语言：** [English](../../fork-guide/satellite-data-and-tuning.md) | 简体中文

## 多来源轨道数据（PR #8、#17–18、#24）

进入 **Tools → Orbit / TLE Sources → Edit Source URLs** 或设置中的 **Orbit / Ephemeris Sources**。当前来源优先级从高到低：

1. **CelesTrak amateur OMM CSV**（默认最高）：直接传播 OMM 数据，不再反向转换为旧 TLE。
2. **手动指定的网址或本地轨道文件**：按填写顺序排序，支持 TLE 文本、SatNOGS JSON、CelesTrak OMM JSON/CSV；手动来源的覆盖有效期可按实现维持约 **72 小时**。
3. **AutoTLE** 后备来源，可以单独修改或禁用。
4. **SatNOGS** 原始 TLE 来源，作为自动数据的最后后备。

卫星列表和转发器列表 URL 也可修改，但必须使用兼容的 SatNOGS JSON 数据格式。下载窗口提供 JPL 下载进度；数据读取加入了合法性检查和容错，避免错误轨道覆盖已缓存的有效信息。通用数据库说明见[卫星数据](../../users_guide/satellite_data.md)。

### 月球与太阳系天体

**JPL Ephemeris Kernel** 支持 **DE440s**（推荐默认）、**DE421** 以及兼容的 **Custom BSP File**。星历可用时，可将月球、太阳和金星纳入跟踪目标。自动下载按用户配置的 JPL/NAIF、SSD 地址顺序尝试；自定义本地 BSP 不会被自动下载替代。

**注意：** 星历与跟踪不等于自动完成 EME 链路预算、QSO 预测或极化旋转补偿。

## 持久化 Base 频率与滑动调谐（PR #2、#4–6、#33）

**Frequency Control** 可停靠面板将三个概念分开：

- **Database reference**：卫星转发器数据库中保存的上行/下行参考频率。
- **Saved Base**：数据库频率 + 为当前转发器分别保存的上下行基准校正；**不包含即时多普勒**。
- **实际电台调谐**：Base + 临时调谐位移 + 已启用的实时多普勒修正。

用 **Edit Base...** 修改持久基准；**Reset to Database** 会在确认后清除相应基准校正；**Reset to Base** 只撤销临时调谐量，不删除保存的 Base。频率滑条和“无多普勒”读数表示参考频率，而发给 CAT 的实际频率可能包含多普勒。滑条允许跨出转发器数据库标称带宽，但实际使用必须遵守设备频段和通信规定。

可分别启用/关闭上下行的多普勒及手动修正。对反向线性转发器应核对正确的上/下行与边带。使用变频器时，若频率不在已配置的 IF 映射内，SkyRoof 会跳过不安全的 CAT 写入。

## 本地自定义转发器（PR #29、#31、#34）

在 **Satellite Transmitters** 面板右键新建 **Local** 转发器；本地创建的记录可再次编辑或删除。可填写：

- 名称/描述；
- 下行 MHz、上行 MHz（两者至少填写一项）；
- 模式，包括 `FM_D`、`USB_D`、`LSB_D` 等支持的数据模式别名。

本地 UUID 保持稳定，编辑后不会丢失相关的 Base 修正、CTCSS 与模式记录。删除前有确认提示。自定义转发器记录位于 SkyRoof 数据目录中的 **`custom-transmitters.json`**，不会改写从 SatNOGS 下载的原始数据库。通过 **Help → Data Folder** 打开真实路径，常见目录是 `%APPDATA%\Afreet\Products\SkyRoof`；手动修改前请备份。

**限制：** 编辑/删除仅适用于本地创建的转发器，不能直接修改在线数据库下载记录。

进一步阅读：[Frequency Control](../../users_guide/frequency_control.md) · [Doppler Tracking](../../users_guide/doppler_tracking.md) · [Satellite Transmitters](../../users_guide/satellite_transmitters_panel.md) · [Data Folder](../../users_guide/data_folder.md)。
