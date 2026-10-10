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
