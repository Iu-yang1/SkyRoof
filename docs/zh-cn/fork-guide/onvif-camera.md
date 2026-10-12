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

Discover（或输入 ONVIF 地址）→ 账号密码 → Profiles → 选择视频 Profile → Play (GPU)；Stop 停止播放。启动 mpv 时不会把视频 URI 中的账号密码放在进程命令行上。

标准：[ONVIF 规范](https://www.onvif.org/profiles/specifications/) · [mpv 手册](https://mpv.io/manual/stable/)。
