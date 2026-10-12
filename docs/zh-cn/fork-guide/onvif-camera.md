# ONVIF 网络摄像机（仅 GPU 视频解码）

**语言：** [English](../../fork-guide/onvif-camera.md) | 简体中文

通过 **View → ONVIF Camera (GPU)** 打开独立可停靠窗口。这是实现 ONVIF 设备与媒体服务的客户端，而不是简单 RTSP 地址输入框。

## ONVIF 协议核对

- **发现：** 发送 SOAP 1.2 WS-Discovery Probe 到 239.255.255.250:3702/UDP，设备类型 NetworkVideoTransmitter，读取 ProbeMatch / XAddrs。若 VLAN/防火墙阻止组播，可手工输入 ONVIF Device Service 地址。
- **设备服务：** 调用 GetServices，必要时回退 GetCapabilities 获取媒体服务地址。
- **媒体服务：** 优先 Media2(ver20)，可回退 Media1(ver10) 的 GetProfiles。Media2 GetStreamUri 使用 Protocol=RTSP 和 ProfileToken；Media1 必须使用 StreamSetup（RTP-Unicast、RTSP）与 ProfileToken，不能混用两者 XML 请求格式。
- **认证：** HTTP Digest/Basic，及 WS-Security UsernameToken PasswordDigest（随机 Nonce、UTC Created）；密码不会写入 Settings.json。
- **实际视频：** ONVIF 返回的 URI 使用 RTSP/TCP 建立 RTP 视频会话。首版未包含 ONVIF PTZ、事件、录像、多播和厂商私有扩展。

## 严格 GPU 解码

需要从 [mpv 官网](https://mpv.io/installation/) 安装 Windows 版 mpv，将 mpv.exe 加入 PATH，或通过 Browse 指定，安装包目前未捆绑 mpv。播放时指定 Direct3D11 + D3D11VA，禁用 WARP 与软件解码回退，并从 Windows 私有随机 IPC 管道查询 mpv 实际 hwdec-current。无法确认 D3D11VA 时停止播放，不主动降级为 CPU 视频解码。SkyRoof 不使用 CPU Bitmap 解码视频帧。

显卡须支持实际视频编码 Profile、分辨率等；不支持时改选摄像机 H.264/H.265 兼容子码流。网络处理、RTSP 解复用、SOAP 和 UI 仍会消耗少量 CPU。

## 操作

在 Live video 顶部点击 **Settings**，于弹出的设置窗口执行 Discover（或手工填写 ONVIF 设备服务地址）→ 填写账号密码 → Get profiles 查询并选择视频 Profile → Save 保存。回到视频页点击 Play 播放（也可在设置中开启自动播放），Stop 停止。密码不会出现在 mpv 进程启动参数中。

标准：[ONVIF 规范](https://www.onvif.org/profiles/specifications/) · [mpv 手册](https://mpv.io/manual/stable/)。

## V1 以视频为中心的界面

**Live video** 首页仅保留 Play、Stop、Reconnect、Settings，中央大面积视频区域，以及只读的码流 Profile / 实际解码器状态栏。Device、Discover、Endpoint、User/Password、Profiles、mpv.exe/Browse 均已移入独立的 **ONVIF Camera Settings** 窗口，避免将工程参数堆在视频首页。

首次打开显示 **No camera configured / Open Settings**。在设置窗口中选择 **Discover** 或手工填写 HTTP(S) ONVIF 设备服务地址，填写摄像机名称、账户及密码；点 **Get profiles** 查询 Media2 / Media1 配置并选择默认视频码流；如自动找不到 mpv.exe，可使用 **Browse** 指定。点击 Save 保存；Cancel 不修改已有配置。设置保存在 Settings.json 中，密码由当前 Windows 用户的 **DPAPI** 加密保护。也可以从 **Tools → Settings → ONVIF Camera** 查看配置。

**Connect when window opens** 仅连接 ONVIF、查询 Profile，不自动播放；**Start video automatically** 会在窗口打开时自动开始视频（隐含先连接）；**Reconnect after playback interruption** 允许断流后重新连接，但用户明确 Stop 后不会自动启动。硬件解码失败时提示错误，不回退软件解码。

底部不显示可能包含账号信息的完整 RTSP URL，仅展示码流及硬件解码状态。实际摄像机互通和显卡解码能力仍需在 Windows 设备上实测；自动化 CI 不能替代硬件验收。

## Windows 多网卡 ONVIF 发现排障

**Discover** 现对所有处于启用状态的本地 IPv4 网卡分别发送符合规范的 WS-Discovery 组播 Probe，并通过对应网卡绑定的 UDP 套接字接收单播 ProbeMatch。先尝试限定 `dn:NetworkVideoTransmitter` 类型的探测，再尝试不限定类型的兼容探测。设置页将显示探测网卡数及收到的 UDP 数据包数；发现阶段不会发送摄像机账号密码。

如果 ONVIF Device Manager（ODM）可以发现摄像机，而 SkyRoof 仍返回 0，请检查 Windows 防火墙对 UDP 3702 的访问，以及 VLAN、虚拟网卡与组播路由是否正确。也允许手工填写摄像机的 ONVIF Device Service URL。

**不要将 `http://摄像机IP:554` 直接填为设备服务地址**，554 通常属于 RTSP 视频端口。海康威视摄像机常见 HTTP 服务路径为 `http://摄像机IP/onvif/device_service`（常见使用 TCP 80）；请优先使用 ODM 发现响应中的准确 `XAddr` 或设备实际配置。**发现摄像机与使用账户认证其 ONVIF 服务是两个独立步骤**。

这次改动修复的是 Windows 多网卡探测设计缺陷，尚不代表完成针对某个实体摄像机型号的实时兼容性验收。
