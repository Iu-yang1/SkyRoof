# Satellite Data

## Data Sources

SkyRoof obtains satellite date from several sources:

- [SatNOGS DB](https://db.satnogs.org/) is the main source of satellite data.
    It is a frequently updated, crowd-sourced dataset that contains detailed information
    about all satellites transmitting in the Ham bands;
- [JE9PEL Satellite List](https://www.ne.jp/asahi/hamradio/je9pel/satslist.htm) is another
    dataset with information about the satellites, maintained by Mineo Wakita JE9PEL, that,
    in particular,
    includes the callsigns of the satellites. SkyRoof also mines the JE9PEL mode descriptions to
    fill in the [signal parameters](#signal-parameters) that SatNOGS leaves blank.

- [LoTW](https://www.arrl.org/quick-start) - The ARRL LoTW service accepts satellite QSO
    only if the satellite abbreviation is one of those published on their
    [web site](https://lotw.arrl.org/lotw-help/frequently-asked-questions).
    These abbreviations are stored in a file in the
    [Data folder](data_folder.md), you can view them in the
    [Satellite Details window](satellite_details_window.md).

- [AMSAT Live OSCAR Satellite Status Page](https://www.amsat.org/status/) accepts satellite
    observations with their own satellite abbreviations, these abbreviations are stored in a file in the
    [Data folder](data_folder.md).

## Signal Parameters

To decode a satellite's telemetry, SkyRoof needs the **modulation**, **baud rate**, and **framing**
of its downlink. These are resolved for each transmitter from several sources, in order of priority:

1. your manual overrides (see below);
2. the [gr-satellites](https://github.com/daniestevez/gr-satellites) database;
3. the **JE9PEL** satellite list;
4. the **SatNOGS DB** transmitter description.

The first source that specifies a given parameter wins, so a higher-priority source fills in only what
the lower-priority ones leave unknown. The resolved values appear in the mouse tooltip of the
transmitter on the [Frequency Scale](frequency_scale.md).

### Overriding Signal Parameters

When the automatic sources are wrong or incomplete, you can correct them in the
**transmitters-override.json** file in the [Data folder](data_folder.md). Each entry is keyed by the
transmitter UUID and lists only the fields to change, for example:

```json
{
  "FdxJrwmqFrJnP3sd96Bip8": {
    "satellite": "SITRO-AIS-56", "norad": 59778,
    "modulation": "GMSK", "baudrate": 2400, "framing": "USP"
  }
}
```

SkyRoof ships a default copy of this file and refreshes it as new corrections are published. To keep
your own edits from being overwritten, add `"read_only": true` to the entry — SkyRoof then never
replaces it. Entries you add that are not in the shipped file are always kept.

The telemetry definition files in the **TelemetryRegistry** folder work the same way: add
`"readOnly": true` (camelCase, to match those files' key style) to a definition to keep your edits when
SkyRoof updates its bundled definitions.

## TLE

The satellite orbit elements ([TLE](https://celestrak.org/columns/v04n03/) data)
are downloaded from **SatNOGS DB** by default.

The built-in satellite-list, transmitter-list and primary TLE URLs are exposed in
**Tools / Orbit / TLE Sources / Edit Source URLs** under **Orbit / Ephemeris Sources**.
They are normal settings, so you can replace or clear them instead of editing source code.

Additional TLE feeds can be entered in **Custom TLE Sources**. Separate multiple URLs or local
file paths with semicolons or new lines. Plain 2-line/3-line TLE text and SatNOGS-style JSON are
supported. For example:

```text
http://autotle.bi4pym.cn/AutoTLE.txt
```

Custom TLE feeds are applied after the primary source, so they can override an existing NORAD
object and can also add tracking-only objects that are not present in the SatNOGS database.

SatNOGS obtains these data from different sources and makes the latest and most reliable data
available on their web site. The source of TLE and its creation time are shown in the
[Satellite Details window](satellite_details_window.md)
or [panel](satellite_details_panel.md):

![TLE Date](../images/tle_date_details.png)

and in the mouse tooltip of the satellite:

![TLE Date](../images/tle_date_tooltip.png)

## JPL Ephemeris Sources

Moon, Sun and Venus tracking uses a JPL/NAIF SPK kernel. **DE440s** is the default. SkyRoof ships
no large BSP file; it downloads the selected kernel into the user data cache when needed.

The DE440s and DE421 download source lists are editable in the same **Orbit / Ephemeris Sources**
settings section. Each default list contains two official NASA/JPL endpoints (NAIF first, SSD
second), and SkyRoof tries them in order. Large-kernel downloads use a 15-minute HTTP timeout.
You can remove, reorder or replace either URL, or use **Load SPK/BSP From File** for a local copy.

## Automatic Updates

SkyRoof automatically downloads the satellite list every 7 days, and TLE data every 24 hours.

The mouse tooltip of the Satellite Data label on the status bar shows the last download time:

![Satellite Data Age](../images/satellite_data_age.png)

The light next to the label turns yellow if the satellite data are not up to date.

## Manual Updates

In addition to automatic downloads, the data may be manually downloaded at any time using
the **Tools / Download All Satellite Data** and **Tools / Download Only TLE** menu commands.

## Loading TLE from File

If your system is not connected to the Internet, you can load TLE data from a local file
using the **Tools / Load TLE from File** menu command. Two TLE formats are supported:

- **.json** - TLE data from the SatNOGS web site, recommended ([download](https://db.satnogs.org/api/tle/?format=json));
- **.txt** - 3-line TLE data in a text file, available from many sources, e.g. CelesTrak
    ([download](https://celestrak.org/NORAD/elements/gp.php?GROUP=amateur&FORMAT=tle)).

Note that TLE import cannot add new satellites, it only loads orbital elements for the satellites already in the database.

## AMSAT Satellite Status

[AMSAT Live OSCAR Satellite Status Page](https://www.amsat.org/status/) is a crowd-sourced, real-time Ham satellite status page.

### Posting Status Data

You can post your satellite status observations the the AMSAT web site either by filling the submission form on their
site, or using the right-click menu of the satellite   labels on the [Frequency Scale](frequency_scale.md).
A valid Ham callsign must be entered in the [Settings window](settings_window.md) for this function to work.

### Downloading Status Data

Set the **Amsat Satellite Status / Enable** option in the Settings window to `true` to enable automatic downloads of the
satellite status information from the AMSAT web site. The statuses are shown on the [Current Group](current_group_panel.md)
panel, the green and red icons represent the active and inactive status respectively.

Satellite status data are downloaded once an hour. You can manually download it at any time using the
**Tools / Download AMSAT Statuses** menu command.
