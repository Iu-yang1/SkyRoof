# ONVIF network camera (GPU-only video)

**Language:** English | [简体中文](../../zh-cn/fork-guide/onvif-camera.md)

Open **View → ONVIF Camera (GPU)**. The dockable viewer implements ONVIF control-plane services, not just a hardcoded RTSP link.

## ONVIF protocol implementation

- **Discovery:** SOAP 1.2 WS-Discovery Probe to 239.255.255.250:3702/UDP with NetworkVideoTransmitter type, parsing ProbeMatch XAddrs. For blocked multicast, enter the Device Service URL manually.
- **Management:** Device Service GetServices with GetCapabilities fallback finds the Media service endpoints.
- **Profiles:** Media2 (ver20) and Media1 (ver10) GetProfiles, preferring Media2.
- **Media2:** GetStreamUri with Protocol=RTSP and ProfileToken (RTP over RTSP/TCP).
- **Media1:** GetStreamUri with StreamSetup containing RTP-Unicast and Transport/Protocol=RTSP, plus ProfileToken. The Media1 and Media2 XML schemas differ.
- **Authentication:** HTTP Digest/Basic and WS-Security UsernameToken PasswordDigest with fresh nonce and UTC Created. Passwords are not saved in Settings.json.
- **Media transport:** RTSP/TCP for the URI supplied by ONVIF. No ONVIF PTZ, events, recordings, multicast transport, or device-vendor extensions in this first version.

## Hardware-only decode

Install Windows **mpv** from [mpv.io](https://mpv.io/installation/), place mpv.exe in PATH, or select it via **Browse**; SkyRoof does not bundle mpv. The player uses Direct3D 11 and D3D11VA hardware decoding, disables WARP and decoder software fallback, and verifies live mpv hwdec-current via a private randomized Windows IPC pipe. Unsupported GPU/codec combinations stop playback instead of deliberately switching to a CPU video decoder. Video pixels are not decoded or copied into a managed SkyRoof Bitmap.

RTSP networking, demultiplexing, SOAP and UI necessarily use some CPU. This is GPU-only **video decoding**, not zero total CPU usage.

## Usage

Choose **Settings** from the Live video toolbar. In the settings dialog, Discover (or enter the ONVIF Device Service URL), enter credentials, Get profiles and select a video profile, then Save. Return to Live video and choose Play (or enable automatic playback in Settings). Stop ends playback. Camera passwords and RTSP userinfo are not placed in mpv process command-line arguments.

References: [ONVIF specifications](https://www.onvif.org/profiles/specifications/) · [mpv manual](https://mpv.io/manual/stable/).

## V1 video-first layout

The **Live video** window now shows only a small header (Play, Stop, Reconnect, Settings), the live camera image, and a concise status strip with stream profile and confirmed decoder. The device address, credentials, discovery, Profile selection and mpv.exe location are no longer editable on the video page.

To configure a new camera, open the camera pane and choose **Open Settings**. In the new **ONVIF Camera Settings** dialog: choose **Discover**, or enter an HTTP(S) ONVIF Device Service endpoint; fill camera name, account and password; use **Get profiles** to select the default Media2 or Media1 stream; and select **mpv.exe** when it cannot be found on PATH. Click **Save** to persist preferences. The saved password is protected for the current Windows user with Windows DPAPI in Settings.json; **Cancel** leaves existing settings unchanged. The same settings can be inspected in **Tools → Settings → ONVIF Camera**.

**Connect when window opens** contacts ONVIF and selects the profile, without video playback. **Start video automatically** also starts RTSP playback; turning it on implies connection. **Reconnect after playback interruption** retries failed video sessions but not explicit Stop. Incomplete or unsupported hardware decoding displays a failure rather than silently using CPU decoding.

The status bar intentionally avoids showing credential-bearing RTSP URLs. For diagnostics, inspect the configured endpoint in Settings. GPU hardware validation requires a supported local Windows device; CI only tests the configuration and control flow, not physical camera compatibility.
