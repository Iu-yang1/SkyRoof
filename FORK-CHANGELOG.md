# SkyRoof Fork v.1.66 / SkyRoof Fork 1.66 版本说明

Release date / 发布日期: 2026-11-05. P0–P3 risk-assessed code-quality and lifecycle maintenance release.

## English
- Isolated rotator child: preserve IPC, startup rollback, transport teardown and **physical STOP unverified** failure evidence without changing motion commands, STOP priority, feedback interlock or hardware retry timing.
- SkyCAT/CAT lifecycle: distinguish expected process exit from an unconfirmed child termination; preserve CAT/PTT ownership and serial protocol behavior.
- FT4/SSTV: keep mandatory PTT release when audio-buffer cleanup fails; report temporary-WAV deletion and malformed QSO log rows while retaining valid records.
- Improve WASAPI device-loss handling, ONVIF camera lifecycle diagnostics, voice playback disposal, SatNOGS upload shutdown, ephemeris cache cleanup, and installer log fallbacks.
- Run the CW R2C FFT benchmark on every master push, with Windows x64 build, full xUnit and pinned aislop v0.18.1; independent CI, not a cross-CPU throughput claim.
- P1: make AMSAT status submissions cancellable and non-blocking on the WinForms UI thread (#164); reject incomplete satellite/transmitter customization state explicitly before tuning (#165), while retaining the existing stored values and frequency formulas.
- P2: refactor AFC observation/polarity/servo responsibilities (#154) with regression coverage for independent FIR/DSP references, AFC CAT-timeline/polarity and CW R2C/complex FFT phase and amplitude.
- P3: require pinned aislop and six-size CW R2C FFT microbenchmark inside Build and Release itself, refuse an existing release tag, serialize releases, verify Authenticode before publishing, and mark a build without SignPath as a prerelease. Never claim signing from workflow configuration alone.
- Full-source pinned aislop after #165: 64/100, 781 findings (1 intentional native callback exception error, 779 warnings and 1 info). Historical debt is documented, not declared fixed or silently rebaselined.
- The SoapySDR native log callback retains one deliberately reviewed non-propagating catch to avoid exceptions crossing the unmanaged callback boundary; see `docs/quality/aislop-native-callback-exception-20261105.md`.
- **Hardware acceptance remains separate:** physical rotator STOP/C2 feedback, IC-9700 CAT/PTT, WASAPI unplug and ONVIF devices require field verification; Windows CI does not certify them.

## 中文
- 旋转器独立子进程：保留 IPC、启动失败回滚、传输关闭和**物理 STOP 未确认**的诊断证据，不改变运动命令、STOP 优先级、反馈互锁或重试时间。
- SkyCAT/CAT 生命周期：区分正常退出与子进程终止未确认，保留 CAT/PTT 控制权和串口协议。
- FT4/SSTV：音频缓冲清理失败仍保证既有 PTT 释放；记录临时 WAV 删除和损坏 QSO 日志行，保留有效记录。
- 修复与加强 WASAPI 断开、ONVIF 生命周期、语音播放资源清理、SatNOGS 后台任务、星历缓存和安装程序日志的诊断。
- master 每次推送都运行 CW R2C FFT 基准，并保留 Windows x64、全量 xUnit 与 aislop v0.18.1 门禁；基准仅作为同环境回归检查。
- P1：AMSAT 上报改为可取消的异步操作，避免阻塞界面（#164）；RadioLink 在读取或修改调谐配置前明确验证配置对象已初始化（#165），不改变已有频率公式和持久化值。
- P2：按 #154 拆分 AFC 观测、极性和伺服职责；由独立 FIR/DSP、AFC CAT 回读时序及 CW R2C/复数 FFT 相位幅度回归测试覆盖核心数学约束。
- P3：Build and Release 自身强制通过固定版本 aislop 与六组 CW R2C FFT 性能基准，拒绝已存在的 Release Tag、串行化发布、上传前核查 Authenticode；没有 SignPath 时作为预发布，签名情况以实际安装包验证为准。
- #165 之后固定版本 aislop 报告：64/100，781 项发现（1 项原生回调例外 error、779 warnings、1 info）。历史技术债持续保留，不声称全部清零或自动重新生成基线。
- SoapySDR 原生回调保留一项有依据的安全例外，避免异常跨越非托管边界；详见 `docs/quality/aislop-native-callback-exception-20261105.md`。
- **实机验收仍独立进行：** 旋转器物理 STOP/C2、IC-9700 CAT/PTT、WASAPI 热拔插及 ONVIF 设备均不能以 CI 成功替代。

---

# SkyRoof Fork v.1.64 / SkyRoof Fork 1.64 版本说明

Release date / 发布日期: 2026-11-02. Settings dialog layout correction.

## English
- Fixed top VB-CABLE and language toolbars covering the first four Settings sections: Orbit / Ephemeris Sources, User, Audio, OutputStream.
- Replaced overlapping Dock layouts with independent TableLayoutPanel rows; kept all existing settings values and serialized JSON names.
- Added Windows bilingual PropertyGrid, nonoverlap and settings persistence regression tests.
- No changes to IC-9700 IF→AF, AFC, PTT or satellite Doppler.

## 中文
- 修复 VB-CABLE 工具栏及语言选择栏遮挡设置列表前四项（轨道与星历来源、用户信息、音频、音频输出流）的问题。
- 改为独立三行布局，保留原设置内容、序列化键名和中英标签。
- 增加 Windows 真实 PropertyGrid 显示、工具栏不重叠及配置保存测试。
- 未修改电台 IF→AF 解调、AFC、PTT 或卫星多普勒。

---

# SkyRoof Fork v.1.63 / SkyRoof Fork 1.63 版本说明

Release date / 发布日期: 2026-11-02.

## English

- Production IC-9700 48-kS/s real 12-kHz USB IF → SSB/CW/FM AF → Windows virtual cable for WSJT-X; raw IF is shared with AFC/SSTV where possible.
- FM 6.25-kHz (900-Hz deviation) startup fixed; decoder failures surfaced to the link status.
- Removed experimental dual USB IF/LAN AF comparison recorder and verbose per-block diagnostics; normal recording and four-stage IF/DDC/AF/V-MIC health display remain.
- Preserved bounded queues, peak protection, slow SSB/CW AGC, FM gain, weak-FM evidence gate, HoldLast and RX-only CAT/PTT safety.
- Optional original basic VB-CABLE Donationware package, verified for publisher and integrity. No experimental SysVAD kernel driver is shipped.

## 中文

- 正式提供 IC-9700 12 kHz 实数 IF → SSB/CW/FM → 虚拟声卡 → WSJT-X 接收功能，AFC/SSTV 可共享原始 IF。
- 修复 6.25 kHz FM 900 Hz 频偏导致的静默无声与解调异常诊断。
- 移除实验性双路 IF/LAN AF 对照录音和冗长 DSP 诊断，保留普通录音及 IF/DDC/AF/V-MIC 链路状态。
- 保留受限音频增益、削峰、缓冲、SO-50 弱 FM 置信度验证、AFC HoldLast 与 RX/TX 安全互锁。
- 正式安装包可选安装原厂基础版 VB-CABLE Donationware；不分发未正式签名的内核驱动。

---

# SkyRoof Fork v.1.62 / SkyRoof Fork 1.62 版本说明

Release date / 发布日期: 2026-10-31. SSTV Console integration release.

## English — changes since v.1.61

- AFC FM fix: repeated WASAPI IF audio timestamps no longer clear the longer frequency-measurement buffer while the independent spectrum remains active.
- AFC distinguishes no measurement from a true zero residual, and displays tentative FM IF offset estimates plus quality without unsafe CAT correction.
- Added +820 Hz FM offset and repeated-timestamp regression tests and measurement pipeline diagnostics.

- Official v1.62 update: up to eight draggable SSTV caption layers with add/duplicate/remove, editable text, size, color and template persistence.
- Added picture variable insertion for MY/DX calls, RSV sent/received, QTH/name/notes, UTC time and TX/RX MHz, preserving older RST variable aliases.
- Optimized large photo loading, bitmap undo/redo, gallery thumbnail caching, fast picture drag, and background saving of received images.
- SSTV QSO reports display RSV; ADIF exports standard RST_SENT and RST_RCVD field names.

- v1.62 gallery hotfix: selecting a thumbnail no longer duplicates its entry; right-click now opens a context menu.
- Gallery/History adds image import, export and delete (including a confirmation prompt).
- RX and TX use the same MMSSTV-style orange/black AF/IF reference with numeric 1200/1500/1900/2300 Hz labels only; RX retains a live trace within the lower strip.

- v1.62 replacement build: RX mode dropdown now includes all 43 TX profile names (Robot/PD remain native auto-decoded).
- TX editor has separate MY/DX callsign and custom caption layers, independent font colors/sizes and mouse dragging.
- Existing user templates can be deleted; saved layouts persist per-layer positions, color, size and callsigns without breaking older template JSON.

- New dockable SSTV Console: receive, transmit, image gallery, UTC QSO log and ADIF export.
- 43 transmit modes with standard and extended VIS/N-VIS, selectable WASAPI audio output, station ID, PNG/template editing and 48-kHz mono WAV export.
- AF capture, RS-BA1/Windows loopback and 12-kHz IF SSTV reception; VIS AFC, manual fine tuning and slant correction.
- Satellite uplink CAT/PTT uses current Doppler frequency during a picture, preserving STOP across an in-flight CAT request. Esc stops the SSTV operation.
- Virtual IC-9700 CAT TCP simulator exercises key/stop, interrupted PTT ACK and reconnect; a five-minute virtual satellite pass exercises Doppler updates.
- Corrected MC110-N scan timing and optimized cached CAT-state sampling and SSTV STOP feedback.

## 中文 — 相对 v.1.61 的变化

- 修复 AFC FM 频偏测量：连续 IF 音频块使用重复时间戳时不再清空测量缓冲，避免出现频谱正常但无频率数据。
- 未测量不再显示为真实零频偏，低置信度 FM IF 估计以“候选”形式显示，同时保持 CAT 自动修正限制。
- 添加 +820 Hz FM 偏移、重复时间戳回归测试和测量链路状态诊断。

- 正式版 v1.62 更新：最多八个独立 SSTV 图层，支持添加、复制、删除、拖动、文字颜色/大小及模板持久化。
- 增加呼号、RSV、QTH、姓名、备注、UTC 时间和上下行 MHz 变量的选择与插入，兼容旧 RST 宏。
- 优化大图加载、撤销重做内存、图库缩略图缓存、拖动渲染与接收图片异步保存。
- SSTV 通联报告界面改为 RSV，ADIF 继续使用标准 RST_SENT/RST_RCVD 字段。

- v1.62 图库修复：选中已有缩略图不再重复添加，右键弹出操作菜单。
- 图库／历史增加导入、导出与删除图片功能（删除需确认）。
- RX/TX 采用同一橙黑色 MMSSTV 音调刻度，只有 1200、1500、1900、2300 Hz 数字，RX 下方黑色区显示 AF/IF 活动。

- v1.62 替换构建：RX 模式下拉框补全全部 43 种 TX 制式，Robot/PD 仍使用原生自动 VIS 解码。
- TX 独立己方呼号、对方呼号及自由文字三图层，支持分别设置字体大小、颜色和鼠标拖动定位。
- 增加自定义模板删除，保存呼号和每层位置、字号、颜色，兼容已有模板文件。

- 新增可停靠 SSTV 控制台，包含接收、发射、图库、UTC 通联日志和 ADIF 导出。
- 支持 43 种 SSTV 图片发射模式、标准与扩展 VIS/N-VIS、指定 WASAPI 发射声卡、台站识别、图片模板编辑及 48 kHz 单声道 WAV 导出。
- 提供 AF 声卡采集、RS-BA1/Windows 回环、12 kHz IF 接收，支持 VIS 自动校频、手动微调与倾斜校正。
- 卫星 CAT/PTT 发射期间跟随当前多普勒频率，修复 CAT 指令尚未返回时 STOP 丢失的问题；Esc 可停止 SSTV。
- 增加虚拟 IC-9700 TCP CAT 测试，覆盖按键/停止、PTT 应答丢失与重新连接，并进行五分钟多普勒模拟。
- 修正 MC110-N 行扫描时间，降低冗余缓存状态检查频率，优化停止操作反馈。

## Release process / 发布流程

源代码版本为 `Directory.Build.props` 中的 `1.62`；使用 Windows x64 自动构建、测试及安装包发布流程。

---

# SkyRoof Fork v.1.61 / SkyRoof Fork 1.61 版本说明

Release date / 发布日期: 2026-10-31. Corrective AFC calibration release following v.1.60.

## English — changes since v.1.60

- Corrected CAT request/readback attribution: a command is never treated as acknowledged by a readback captured before it, and each calibration phase is independently fenced.
- Made the IF timing explicit: the FM estimator averages a 2-second window, so calibration now evaluates settled measurements after the real CAT request time instead of treating the window center as a new command timestamp.
- Updated the conservative RX-only probe to 150 Hz for FM (40 Hz for CW/SSB pilot), requiring two stable IF samples on each of the probe and restore movements; the same measured direction must be verified on return.
- Added more precise calibration failure details, bounded deadlines and a brief hold tolerance for momentary FM reference instability. PTT and lost tracking continue to abort and restore the temporary RX probe.
- The model's predicted RX Doppler rate is subtracted from observed CAT movements, so the probe is not falsely rejected while a satellite is moving. No TX, persistent transponder correction or orbit/ephemeris changes. IC-9700 real-radio validation is still required.

## 中文 — 相对 v.1.60 的变化

- 修正 CAT 写频请求与真实读回的时间归因，杜绝用下发之前的读回确认写频；每一阶段只接受该阶段下发后的硬件反馈。
- 考虑 FM 频偏算法约 2 秒 IF 测量窗口，仅在命令生效后获得稳定 IF 观测，才判断调谐方向。
- FM 采用 150 Hz 临时 RX 探针，CW/SSB 导频为 40 Hz；一次调谐和返回须各有两次稳定 IF 观测及真实 CAT 读回，完成返回验证后才启动闭环修正。
- 增加失败原因、阶段超时诊断和短暂失号保持，不放宽 PTT 安全保护。所有校准只作用于 RX 临时修正，不保存频偏、不改 TX。
- 针对卫星运行时 CAT 正常跟踪多普勒的情况，在方向校准判断中扣除模型给出的预期频率变化，不再把正常多普勒位移作为探针误差。
- IC-9700 实机时序及 AFC 收敛仍需现场复测。

## Release process / 发布流程

Release version is `1.61` in `Directory.Build.props`. A successful Windows build, xUnit regression suite and installer payload verification are required before merging. Unsigned artifacts remain pre-releases.

---

# SkyRoof Fork v.1.60 / SkyRoof Fork 1.60 版本说明

Release date / 发布日期: 2026-10-30. Based on VE3NEA/SkyRoof v.1.56; AFC stabilization release.

## English — changes since v.1.59

- Fixed AFC's zero-correction lock by decoupling live RX CAT readback from Ignore Dial Knob; operator dial feedback remains disabled when requested.
- Added bounded, self-restoring RX-only CAT/IF polarity calibration (two probe/restore cycles) before automatic frequency writes when polarity is unknown.
- Prevented unverified Doppler-derived polarity from opening the AFC servo. Added PTT, stale IF, lost-lock and CAT timeout fail-safe handling and a deferred RX restoration path.
- Added CAT readback freshness and polarity-probe states to the bilingual AFC console, along with unit tests and updated operator documentation.
- No changes to TX frequency, persistent transponder/manual offsets, RIT/XIT, or the underlying satellite ephemeris model.
- Automated synthetic/CAT-state tests do not replace IC-9700 real-radio IF/CAT timing verification.

## 中文 — 相对 v.1.59 的变化

- 修复 AFC 信号已锁定但修正仍为 0 Hz 的控制前置条件：将 RX CAT 实际频率读回从 Ignore Dial Knob 功能中独立出来。
- 新增限时、可恢复的 RX-only RF/IF 方向自动标定，两组小幅调谐/回退均得到 CAT 读回和 IF 观测确认后，才开放自动 AFC 写频。
- 卫星多普勒引起的普通频率变化不再单独作为开放 AFC 写频的方向证明；保留 PTT、失号、超时和 CAT 异常保护，并补充延迟回退路径。
- AFC 中英界面新增 CAT 读回新鲜度和方向标定状态；加入回归测试与使用说明。
- 不修改 TX、永久转发器修正、手动频偏、RIT/XIT 和卫星轨道模型。
- 合成数据及 CAT 状态机通过测试不代表 IC-9700 实机完成验收，仍需 IF 与 CAT 联合测试。

## Release process / 发布流程

唯一版本来源：`Directory.Build.props` → `1.60`。合并至 master 后自动完成 Windows x64 构建、xUnit 测试与安装包发布；有签名能力时按原签名流程处理，否则依现有工作流发布未签名 pre-release。保留 v.1.59 以便回退。

---

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
