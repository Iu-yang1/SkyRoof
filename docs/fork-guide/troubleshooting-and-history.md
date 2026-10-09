# Troubleshooting, security and fork change history

**Language:** English | [简体中文](../zh-cn/fork-guide/troubleshooting-and-history.md)

## Quick fault isolation

| Symptom | Check | Corrective action |
|---|---|---|
| SkyRoof CAT disconnected or cannot tune | Is SkyCAT listening on **4532**? Is its single CI-V COM available? | Start SkyCAT first, then enable SkyRoof CAT at `127.0.0.1:4532`; avoid opening the same virtual COM in another process |
| No FT4 or PTT control | Radio readiness, CAT PTT ownership, audio device and transmit path | Confirm CAT/PTT configuration, the correct RS-BA1 input, and an independent means to unkey; do not bypass PTT arbitration |
| Passive spectrum freezes | RS-BA1 spectrum/window closed, WinDivert permission, relevant UDP packets | Reopen RS-BA1 scope, confirm WinDivert and that CI-V scope data is actually sent |
| SkyCAT scope visible but settings read-only | Spectrum **Source** differs from **Control path** | Set Scope control path to `SkyCat` explicitly when wanted; verify SkyCAT supports the selected `27 xx` controls |
| Direct LAN interferes with RS-BA1 | Two clients competing for remote LAN resources | Stop experimental Direct LAN and use passive RS-BA1 capture; Direct LAN never auto-starts |
| Remote Control Switch cannot connect | Distinct SkyCAT auxiliary **4537**, not CAT 4532 | Update SkyCAT + Switch; check port owner; connection uses `PING/PONG` (not a radio test) |
| Rotator will not jog | Active pass tracking, rotctld host/port or stale feedback | Stop tracking before manual jog, inspect correct 4533 endpoint and actual position replies |
| PARK aborted | No two fresh in-tolerance position readings, stale feedback, limit violation | Inspect actual az/el range, motion and rotctld feedback; ACK alone is not arrival |
| Frequency appears offset | Base correction vs Doppler vs temporary tuning; wrong transmitter | Inspect database reference, saved Base, and active Doppler; reset only the intended component |
| User settings missing after restart | `Settings.json` was damaged/recovered, or file changed | Open Help → Data Folder; compare `Settings.json` and `Settings.json.bak` before editing |
| LAN scope permission error | WinDivert/admin privilege or driver version | Use the supplied matching driver in an elevated session; avoid overwriting an active driver |
| Custom transmitter missing | Local file, satellite association or deletion | Check `custom-transmitters.json` and a backup; reinstalling external SatNOGS data will not recreate a deleted local entry |

### Check whether a TCP port is already in use

```powershell
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $_.LocalPort -in 4532,4533,4534,4535,4537 } |
  Select-Object LocalAddress,LocalPort,OwningProcess,
    @{Name='Process';Expression={
      (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    }}
```

If **4537** is used by another process, configure SkyCAT with `--switch-port <unused-port>` and change the address **inside Remote Control Switch** accordingly. Do not change SkyRoof's 4532 CAT connection or indiscriminately kill processes.

## Fork implementation history

Changes below reflect **merged pull requests**, not every experimental branch. They complement [upstream release notes](../download.md) rather than replacing them. PR numbers refer to [Iu-yang1/SkyRoof](https://github.com/Iu-yang1/SkyRoof/pulls?q=is%3Apr+is%3Amerged).

| PR | Implemented area |
|---|---|
| #2, #4–6 | Persistent per-transmitter base frequency corrections and consistent display |
| #7 | Redacted IC-9700 authenticated LAN transition reports |
| #8 | Custom orbit sources, JPL solar-system ephemerides, physical PTT |
| #9 | WinDivert release packaging |
| #10–11 | FT4 PTT fail-safe and CAT/native worker lifecycle |
| #12–14 | Protected credentials, input/release hardening, Node 24 CI |
| #16 | NAudio/WASAPI audio backend |
| #17–19 | Orbit source/TLE and JPL progress fixes, compact PTT UI, startup recovery |
| #21 | Synchronize upstream v1.55 |
| #22–23 | Rotctld reply handling, movement retry and tracking/STOP lifecycle |
| #24 | Priority CelesTrak OMM CSV and manual orbit-source overrides |
| #25–28 | Manual direction wheel, live-pass safety lock, PARK editor |
| #29, #31 | Create/delete locally persisted satellite transmitters |
| #30 | Pink/blue/white light-theme styling |
| #32–33 | IC-9700 spectrum refactor, visual contrast and no-Doppler readout fixes |
| #34 | Explicit scope control, local transmitter editing, sequential feedback-based PARK route |
| #35 | SkyCAT RF GAIN and RS-BA1 playback AF GAIN |
| #36 | Incremental nonblocking IC-9700 scope settings readback |

Non-merged PRs and unmerged experimental feature branches are **not** documented as shipped. GitHub Light/Dark theme code is present in the current `master` and covered by theme regression tests; no claim is made that all future UI ideas are complete.

## Responsible operation, security and deployment

- Avoid releasing logs containing RS-BA1/Icom authentication tokens, direct LAN passwords, IP topology or unredacted packets. Windows DPAPI protects supported persisted credentials; the settings backup should still be handled privately.
- RS-BA1 passive capture uses network capture privileges; only install a driver from the pinned verified source.
- An unsafe rotator movement or stuck TX requires **physical intervention**; software retries are not a replacement for safe antenna installation.
- [Current fork source](https://github.com/Iu-yang1/SkyRoof) · [Windows build checks](https://github.com/Iu-yang1/SkyRoof/actions) · [GitHub Pages](https://iu-yang1.github.io/SkyRoof/).
