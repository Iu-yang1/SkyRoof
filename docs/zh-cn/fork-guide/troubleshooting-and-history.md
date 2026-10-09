# 故障排查、安全与 Fork 更新记录

**语言：** [English](../../fork-guide/troubleshooting-and-history.md) | 简体中文

## 故障快速定位

| 症状 | 重点核查 | 建议操作 |
|---|---|---|
| SkyRoof CAT 断开、无法调频 | SkyCAT **4532** 是否监听；唯一 CI-V COM 是否被其他程序打开 | 先启动 SkyCAT，再使用 `127.0.0.1:4532` 连接；不要让多个进程抢占同一虚拟 COM |
| FT4 或 PTT 不工作 | 电台收发条件、PTT 所有权、音频设备及发射输入源 | 确认 RS-BA1 输入正确、PTT 租约安全，并保留独立解开发射的手段 |
| RS-BA1 被动频谱停住 | RS-BA1 频谱窗口是否关闭、WinDivert 权限、有无 UDP 扫描帧 | 保持 RS-BA1 频谱输出，检查兼容驱动和实际 CI-V 数据 |
| 频谱能显示但设置只读 | **Source** 与 **Control path** 是两个独立选项 | 要写电台时选 `Control path = SkyCat`，并核对对应 `27 xx` 指令 |
| Direct LAN 干扰 RS-BA1 | 两个认证会话争用电台资源 | 停止实验性 Direct LAN，优先改用 RS-BA1 被动捕获 |
| Switch 无法连接 | 它使用独立端口 **4537**，不是普通 CAT **4532** | 更新 SkyCAT 与 Switch，查询端口占用，`PING/PONG` 只能验证 TCP 服务 |
| 转台方向按钮无法使用 | 自动跟踪仍在运行、rotctld 端口或反馈无效 | 停止自动跟踪后手动操作，检查 **4533** 与位置反馈 |
| PARK 中途终止 | 新鲜回报不足、超出目标容差、位置超范围 | 核对机械转动范围和 rotctld 反馈；仅有 ACK 不代表到达 |
| 频率显示偏差 | Base、临时调谐、多普勒三者混淆 | 查看 Database、Saved Base 与是否应用多普勒，只重置需要的部分 |
| 重启丢失配置 | `Settings.json` 损坏并执行恢复 | 进入 Help → Data Folder，比较 `Settings.json` 与 `Settings.json.bak` |
| 频谱捕获权限不足 | WinDivert、管理员权限或驱动冲突 | 使用匹配版本和必要权限，不直接删除已加载驱动 |
| 自建转发器消失 | 自建文件、卫星绑定或手动删除 | 检查 `custom-transmitters.json` 与备份；重新下载 SatNOGS 无法恢复已删除的本地条目 |

### 查看 Windows TCP 端口占用

```powershell
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $_.LocalPort -in 4532,4533,4534,4535,4537 } |
  Select-Object LocalAddress,LocalPort,OwningProcess,
    @{Name='Process';Expression={
      (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    }}
```

如果 **4537** 被其他程序占用，可给 SkyCAT 增加 `--switch-port <空闲端口>`，并同时修改 **Remote Control Switch** 保存的地址；**SkyRoof CAT 4532 不需要改变**。不要未经确认直接结束未知程序。

## 自 Fork 以来的合并记录

下面列出的是 **已合并 PR** 的功能类别，不包括只停留在计划或未合并分支的功能。编号对应 [Iu-yang1/SkyRoof PR](https://github.com/Iu-yang1/SkyRoof/pulls?q=is%3Apr+is%3Amerged)。原版功能可参考[上游发行说明](../../download.md)。

| PR | 已合并内容 |
|---|---|
| #2、#4–6 | 上下行 Base 频率持久化、读数和频率显示修正 |
| #7 | IC-9700 LAN 转换探针中的鉴权信息脱敏 |
| #8 | 自定义轨道源、JPL 天体星历、实体快捷 PTT |
| #9 | WinDivert 安装/打包 |
| #10–11 | FT4 PTT 安全、CAT 和原生线程生命周期 |
| #12–14 | 保存凭据保护、下载/打包校验、Node 24 Actions |
| #16 | NAudio/WASAPI 音频后端 |
| #17–19 | TLE/JPL 来源修正、下载进度、简化 PTT、启动防崩溃 |
| #21 | 同步上游 v1.55 |
| #22–23 | rotctld 应答、运动重试、Track/STOP 状态 |
| #24 | CelesTrak OMM CSV 优先级、手动轨道覆盖 |
| #25–28 | 手动转台方向轮、过境锁定、PARK 预设编辑 |
| #29、#31 | 本地创建与删除卫星转发器 |
| #30 | 粉蓝白浅色主题 |
| #32–33 | IC-9700 频谱重构、对比度和无多普勒显示修复 |
| #34 | 频谱控制、本地转发器编辑、反馈判定的多航点 PARK |
| #35 | SkyCAT RF GAIN 与 RS-BA1 播放会话 AF GAIN |
| #36 | IC-9700 频谱增量非阻塞设置读回 |

**没有合并的 PR 和实验分支不会被当作已经发布的功能。** GitHub Light/Dark 主题实现及其测试已存在于当前 `master`；不意味着所有未来设想的 UI 均已完成。

## 安全与发布说明

- 不要公开 RS-BA1/Icom 登录令牌、Direct LAN 密码、网络拓扑及未脱敏的抓包数据。DPAPI 可以保护受支持的已保存凭据，但配置备份仍属于敏感文件。
- 被动捕获需要相关权限与驱动；应使用经过校验的指定版本。
- 对于转台超限或持续发射，必须有实际硬件应急措施，不能仅依赖软件的安全重试。
- [SkyRoof fork](https://github.com/Iu-yang1/SkyRoof) · [构建任务](https://github.com/Iu-yang1/SkyRoof/actions) · [GitHub Pages](https://iu-yang1.github.io/SkyRoof/)。
