# Satellite data, ephemerides, transmitters and frequency tuning

**Language:** English | [简体中文](../zh-cn/fork-guide/satellite-data-and-tuning.md)

## Multiple satellite/orbit sources (PRs #8, #17–18 and #24)

Open **Tools → Orbit / TLE Sources → Edit Source URLs** or the **Orbit / Ephemeris Sources** settings section. The current precedence, from highest to lowest, is:

1. **CelesTrak amateur OMM CSV** (default). Propagated as OMM rather than being converted back to a legacy TLE.
2. **Manually configured orbit URLs or local files** in listed order (TLE text, SatNOGS JSON, CelesTrak OMM JSON/CSV). Manual overrides can remain effective for **72 hours** as described by the source-selection implementation.
3. **AutoTLE** fallback, configurable and independently disableable.
4. **SatNOGS** orbit endpoint, the last automatic fallback.

Satellite-list and transmitter-list URLs can also be customized, but their data must be compatible with the supported SatNOGS JSON formats. The download UI reports JPL progress; validation prevents malformed or stale data from silently replacing working orbit data. Check [upstream satellite data](../users_guide/satellite_data.md) for general fields.

### Lunar and solar-system tracking

**JPL Ephemeris Kernel** supports **DE440s** (recommended default), **DE421**, or a compatible **Custom BSP File**. When enabled and the kernel is available, SkyRoof adds the Moon, Sun and Venus to its target database. Automatic download uses configurable JPL/NAIF or JPL/SSD URLs, in order; a local custom BSP is never downloaded automatically.

A JPL kernel makes target tracking available; it does **not** by itself calculate EME link budgets, predict a completed QSO, or perform celestial polarization correction.

## Persistent base frequency and the tuning bar (PRs #2, #4–6 and #33)

The **Frequency Control** dockable panel distinguishes three independent concepts:

- **Database reference:** downlink/uplink frequency supplied by the selected transponder record.
- **Saved Base:** database frequency plus your persisted per-transmitter uplink/downlink Base correction. This is your chosen station reference, **without** current Doppler.
- **Actual radio tuning:** Base plus interactive tuning displacement and, when enabled, real-time Doppler correction.

Use **Edit Base...** to enter the persistent reference. **Reset to Database** discards your correction **after confirmation**. **Reset to Base** clears the temporary tuning displacement without wiping the saved Base. The no-Doppler readouts and tuning bar are distinct from the CAT-applied frequencies. Manual tuning can extend beyond a database passband's nominal end; always verify that the chosen RF signal is legal and within the selected equipment's range.

Separate per-satellite settings enable/disable downlink/uplink Doppler and manual corrections. Inverting linear transponders need the correct uplink/downlink direction and mode. When a transverter is configured, SkyRoof validates the usable IF-band mapping and suppresses unsupported CAT writes rather than sending out-of-band frequencies.

## User-created transmitter records (PRs #29, #31, #34)

From the **Satellite Transmitters** panel, open the context menu to create a **Local** transmitter; locally created entries can also be edited or deleted. Supply:

- Name/description;
- Optional **downlink MHz** and/or **uplink MHz** — at least one is required;
- Radio mode (including supported data-mode aliases such as `FM_D`, `USB_D` and `LSB_D`).

Records use stable local UUIDs, keeping their individual corrections, chosen modes and CTCSS settings associated during edits. Deletion is confirmed and intentionally removes the local definition. Local definitions are stored in **`custom-transmitters.json`** in the SkyRoof data folder, not in the upstream-downloaded SatNOGS transmitter list. Open **Help → Data Folder** to find the exact user folder (normally `%APPDATA%\Afreet\Products\SkyRoof`). Back up the file before manual editing.

**Limit:** the editor is for **locally created** transmitters. It does not silently overwrite externally downloaded records.

## Related original documentation

[Frequency Control](../users_guide/frequency_control.md) · [Doppler Tracking](../users_guide/doppler_tracking.md) · [Satellite Transmitters](../users_guide/satellite_transmitters_panel.md) · [Data Folder](../users_guide/data_folder.md).
