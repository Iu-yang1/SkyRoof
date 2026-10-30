# SkyRoof Fork v.1.59 / SkyRoof Fork 1.59 版本说明

Release date / 发布日期: 2026-10-30. Source: `Iu-yang1/SkyRoof`, based on VE3NEA/SkyRoof v.1.56.

## English — changes since v.1.58

- The IC-9700 LAN spectrum toolbar now uses compact English operator buttons: START/STOP, CLEAR, SYNC and SETS, while retaining REF, VBW, AVG, SMOOTH and ZOOM.
- Initial bilingual Settings PropertyGrid interface, with an in-dialog 简体中文 / English selector. Language is saved to `UiSettings.Language` and does not change radio parameters or JSON field names.
- AFC Live Tracking, control states, diagnosis messages and Advanced Settings labels support Chinese/English presentation; technical details and numerical settings keep their original semantics.
- Includes recent responsive spectrum-toolbar and DPI improvements, and safe reusable MORE context-menu lifecycle handling.
- Localized names and core controls are covered first; less common technical setting descriptions retain their original text until reviewed.
- No change to CAT protocol, Doppler logic, IF demodulation or AFC correction algorithms.

## 中文 — 相对 v.1.58 的变化

- IC-9700 LAN 频谱工具栏采用紧凑英文操作按钮：START/STOP、CLEAR、SYNC、SETS，保留 REF、VBW、AVG、SMOOTH、ZOOM。
- 设置窗口增加简体中文/English 语言选择，首批汉化设置分类、常用设置项和参数标签；语言设置可保存，不更改 JSON 字段或电台控制配置。
- AFC 实时追踪页、控制状态、诊断说明和高级设置属性支持中英双语呈现。
- 包含近期 DPI 响应式两行工具栏及 MORE 菜单生命周期异常修复。
- 较少使用的技术设置说明文字仍逐步完善，本版不宣称全部设置说明均已完成翻译。
- 不修改 CAT 协议、多普勒算法、IF 解调或 AFC 频率控制算法。

## Release process / 发布流程

唯一版本来源为 `Directory.Build.props` → `1.59`。合并至 master 后，Build and Release 自动完成 Windows x64 编译、测试、Inno Setup 安装包和 `v.1.59` Release。
如未配置 SignPath，依照项目现有策略发布未签名的 **pre-release**，不得标为已签名正式版。

---

# SkyRoof Fork v.1.58 / SkyRoof Fork 1.58 版本说明

Release date / 发布日期: 2026-10-24. Source: `Iu-yang1/SkyRoof`, based on VE3NEA/SkyRoof v.1.56.

## English — changes since v.1.57

- RC-3040S native LAN control: streamlined quick AZ/EL speed controls, device readback, and motion settings with Direct, Precision stop, Trapezoidal, and S-curve software reference planners.
- Speed configuration is sent only after explicit user approval. Removed the cumbersome typed confirmation dialog, retaining a single Yes/No safety confirmation.
- Reduced management TCP/60030 connection churn: one bounded WebSocket session per speed save (C2 preflight → a single AT command → fresh C2 readback), device identity pinning, and no implicit write retries.
- Fixed the motor-control state handling after a successful speed readback: recovery is conditional on fresh independent TCP/60020 position telemetry. Explicitly distinguish physical feedback loss, management safety lock, and ready status.
- Cleaned up obsolete RC-3040S DEMO UI/settings, reorganized motion settings and button layout, and simplified hardware-control wording.
- Preserve CW Console, IC-9700 LAN spectrum, Doppler/CAT/transponder workflow and previous v.1.57 improvements.
- The reported raw speed setting may not represent calibrated mechanical angular speed; firmware EEPROM retention and physical S-curve compliance have not been independently proven. The application remains safety-conservative.

## 中文 — 相对 v.1.57 的变化

- 完善 RC-3040S 原生 LAN 控制：AZ/EL 速度快捷控制、配置读回，以及 Direct、Precision stop、Trapezoidal、S-curve 四种软件参考轨迹规划模式。
- 去除输入指定文字的二次确认框，保留一次 Yes/No 安全确认，绝不后台自动写入 AT 速度参数。
- 优化 TCP/60030 WebSocket 管理流程：每次保存使用单个会话完成读取、一次性写入及写后重新查询，核对设备身份，不自动重发配置命令。
- 修复速度保存后无条件锁定云台导致状态栏误显示为断连的问题；需取得 TCP/60020 独立的新位置反馈后方可恢复，安全锁与真实通信断开明确区分。
- 删除无用 DEMO 入口及设置，整理 Motion Settings 布局及界面提示。
- 保留 CW Pileup、IC-9700 LAN 频谱、卫星频率/多普勒及上版功能。
- 速度显示及配置回读不等于实测机械速度，也不代表 EEPROM 断电保持或固件实际执行了 S 曲线；真实运动安全仍应实机确认。

## Release process / 发布流程

Version source of truth: `Directory.Build.props` → `1.58`.
The release workflow triggers on this file's change on `master`, validates release invariants,
builds and tests Windows x64, packages the installer, and publishes `v.1.58`.
Unsigned builds are published as **pre-releases**; a signed release requires valid SignPath configuration.

---

# SkyRoof Fork v.1.57 / SkyRoof Fork 1.57 版本说明

Release source: `Iu-yang1/SkyRoof`; upstream baseline: `VE3NEA/SkyRoof` v.1.56 (`bc7befff64fb0dc8e83d207259313e27b834b92f`).

## English

- Integrated the upstream v.1.56 changes: AO-123 SSDV decoding, SSTV auto-save filter selection (None / Wiener / NLM), and refreshed vendored telemetry/DSP libraries.
- Retained the fork's satellite operations workflow: SkyCAT/RS-BA1 CAT integration, IC-9700 LAN spectrum and passband UI, manual transponder records, Doppler-aware frequency control, and rotator control.
- Includes the recent CW Console / Pileup receive and transmit workflow, eight-lane UI/decoder continuity improvements, and removal of obsolete FPS diagnostics (PR #77).
- Preserved the fork's GitHub/light/pink-blue themes and bilingual extension guides.
- Unsigned builds are published as **pre-releases**. Signed releases require the SignPath repository secret and a passing Windows release workflow.

## 中文

- 合并上游 v.1.56：AO-123 SSDV 图像解码、SSTV 自动保存滤波器选择（None / Wiener / NLM）及遥测/DSP 依赖更新。
- 保留 Fork 的 SkyCAT/RS-BA1 CAT 控制、IC-9700 LAN 频谱、手动转发器、多普勒频率控制和旋转器控制。
- 包含最近的 CW Console / Pileup、八路槽位显示与解码连续性修复，并删除旧 FPS 诊断功能（PR #77）。
- 保留 GitHub 明暗主题、粉蓝白主题与中英文扩展功能文档。
- 未签名安装包仅以 **预发布版** 发布；稳定签名发布需要配置 SignPath 凭据并通过 Windows Release 工作流。

## Release process / 发布流程

Version source of truth: `Directory.Build.props` -> `1.57`.
Merge this release branch after CI validation. The `Build and Release` workflow is automatically triggered only when `Directory.Build.props` changes on `master`, or via `workflow_dispatch`.
It builds and tests the application, packages the installer, then publishes the `v.1.57` tag and GitHub Release. Do not create the tag manually before the workflow.

版本唯一来源：`Directory.Build.props`。合并前检查 CI；合并后自动触发安装包构建、测试和 Release。不要提前手动创建同名标签。
