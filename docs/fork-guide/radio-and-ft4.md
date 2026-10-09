# CAT, IC-9700 remote control, PTT and FT4

**Language:** English | [简体中文](../zh-cn/fork-guide/radio-and-ft4.md)

## CAT architecture

The fork retains SkyRoof's **Hamlib rigctld-compatible CAT interface**, with the enhanced [SkyCAT fork](https://github.com/Iu-yang1/SkyCAT) recommended for IC-9700 satellite control. In **Tools → Settings → CAT Control**, enable RX CAT and/or TX CAT, using host `127.0.0.1` and port **4532** when running SkyCAT locally. A single rig may be configured in both RX/TX sections. The CAT connection may be disabled when using SkyRoof only for tracking.

**Example** (change `COM9` to the RS-BA1 virtual CI-V COM you already use):

```powershell
skycatd.exe -m IC-9700 -r COM9 -s 115200
```

SkyRoof uses the main CAT **4532**; WSJT-X uses SkyCAT's restricted **4534**; native scope frames use **4535**; the separate auxiliary [Remote Control Switch](https://github.com/Iu-yang1/IC-9700-Remote-Control-Switch) uses **4537**. Do not connect SkyRoof CAT to 4534/4535/4537. SkyCAT owns the COM and serializes requests, so multiple programs must not open it separately. Detailed [SkyCAT English](https://iu-yang1.github.io/SkyCAT/skycatd.html) / [Chinese](https://iu-yang1.github.io/SkyCAT/zh-cn/skycatd.html) setup is maintained alongside SkyCAT.

## CAT and safe PTT changes (PRs #5, #10–11, #17, #34–36)

The fork hardens command acknowledgement, timeout/reconnection and TX/RX transitions. SkyCAT's WSJT-X proxy uses an exclusive **PTT lease**, preventing a second client from keying the radio while another client holds the lease. Radio mode/frequency/VFO/SAT/CTCSS writes requested through the WSJT-X compatibility proxy are deliberately **acknowledged as no-ops**, keeping SkyRoof responsible for Doppler and frequency control.

A configurable **Physical PTT Key** (e.g., a USB HID foot switch mapped to F13–F24) is a **momentary** TX switch: key down transmits, key up releases. **Suppress PTT Key** optionally prevents the key from reaching the foreground app. Recheck hotkey mappings and radio TX readiness before putting the system on air; a keyboard shortcut is not a substitute for a hardware TX interlock. CAT fail-safe handling also covers FT4 worker failures and interrupted sessions, but hardware should always have an independent way to unkey.

CTCSS is available through the transponder/radio settings. Set the **uplink** repeater tone on the radio's transmit path; do not assume receive tone squelch is required. Keep an eye on the actual radio state if another client changes tone controls.

## FT4, audio and gains (PRs #16 and #35)

The existing [FT4 Console](../users_guide/ft4_console_panel.md) remains part of SkyRoof. The fork migrated the active Windows audio/WASAPI backend to **NAudio** and added safety for interrupted FT4 PTT. Select the actual Windows capture/playback devices used by RS-BA1 before trying on-air operation; this is distinct from the optional WSJT-X proxy.

The top bar has **two different gain backends**, which must not be confused:

- **RF GAIN** for IC-9700: SkyCAT CAT read/write with real radio readback; it is not the Windows speaker-volume slider.
- **AF GAIN** with RS-BA1: volume of an explicitly selected **Windows playback audio session** (scoped to the selected RS-BA1 process and playback endpoint). If multiple sessions exist, select the intended one using the AF GAIN heading. This changes RS-BA1 playback volume, not RF gain or physical transmitter power.

When using a local SDR, native SDR gain/audio handling still applies. Do not equate the RF/AF controls with `RF POWER` in Remote Control Switch: RF POWER sets transmit power and belongs to a different auxiliary CI-V command.

## Remote Control Switch and RS-BA1

Run RS-BA1 normally, keep SkyCAT as **the sole CI-V COM owner**, then choose **SkyCAT TCP** in the Remote Control Switch connection dialog and save `127.0.0.1:4537`. The restricted Switch port controls DATA OFF/DATA MOD, USB AF/IF output, compressor, COMP level, CW speed and TX power; it does **not** permit satellite mode writes, PTT, VFO changes or frequency/mode writes. SkyRoof retains these responsibilities.

For basic operation, see [Setting up CAT](../users_guide/setting_up_cat_control.md), [FT4 Console](../users_guide/ft4_console_panel.md), and [Transverters](../users_guide/setting_up_transverter.md).
