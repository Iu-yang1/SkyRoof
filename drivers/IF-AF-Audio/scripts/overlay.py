#!/usr/bin/env python3
"""Apply SkyRoof-specific, fail-closed modifications to the pinned LoopIt7 SysVAD overlay.

Provenance: Microsoft Windows-driver-samples SysVAD + SeventhSG/LoopIt7
(MIT). Their base patch is kept unchanged in patches/ for reproducibility.
This script runs ONLY after git apply of the pinned LoopIt7 patch.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

if len(sys.argv) != 2:
    raise SystemExit("usage: overlay.py <pinned-windows-driver-samples-checkout>")

base = Path(sys.argv[1]).resolve() / "audio" / "sysvad"
if not (base / "EndpointsCommon" / "CableRing.cpp").is_file():
    raise SystemExit("Missing patched CCableRing: run fetch-sample.ps1 first")


def _codec(path: Path) -> str:
    # Microsoft source occasionally uses UTF-16LE for .rc / VCXPROJ.
    # Preserve its original BOM when writing patched files.
    header = path.read_bytes()[:3]
    if header[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return "utf-16"
    if header == b"\xef\xbb\xbf":
        return "utf-8-sig"
    return "utf-8"


def load(relative: str) -> str:
    path = base / relative
    return path.read_text(encoding=_codec(path))


def put(relative: str, content: str) -> None:
    path = base / relative
    path.write_text(content, encoding=_codec(path), newline="\r\n")


def one(content: str, old: str, new: str, place: str) -> str:
    count = content.count(old)
    if count != 1:
        raise ValueError(f"{place}: expected exactly one {old!r}, found {count}")
    return content.replace(old, new)


def pattern_one(content: str, old: str, new: str, place: str) -> str:
    content, n = re.subn(old, new, content, flags=re.MULTILINE)
    if n != 1:
        raise ValueError(f"{place}: expected exactly one pattern {old!r}, got {n}")
    return content


# Rename only artifacts that the pinned cable patch introduced.
# Do NOT rename framework classes or original Microsoft components.
patched = [
    "EndpointsCommon/CableRing.cpp",
    "EndpointsCommon/CableRing.h",
    "EndpointsCommon/minwavertstream.cpp",
    "EndpointsCommon/minwavertstream.h",
    "EndpointsCommon/EndpointsCommon.vcxproj",
    "EndpointsCommon/speakertoptable.h",
    "TabletAudioSample/ComponentizedAudioSample.inx",
    "TabletAudioSample/TabletAudioSample.rc",
    "TabletAudioSample/TabletAudioSample.vcxproj",
    "TabletAudioSample/cablespeakerwavtable.h",
    "TabletAudioSample/micintoptable.h",
    "TabletAudioSample/micinwavtable.h",
    "TabletAudioSample/minipairs.h",
    "adapter.cpp", "common.cpp", "common.h",
]
for file in patched:
    data = load(file)
    data = data.replace("LoopIt7Cable", "IfAfAudio")
    data = data.replace("loopit7cable", "ifafaudio")
    data = data.replace("LoopIt7 Cable", "IF-AF-Audio")
    data = data.replace("Cable In", "IF-AF-Audio Playback")
    data = data.replace("Cable Out", "IF-AF-Audio")
    put(file, data)

# Handle the base cable patch's EOF-only MSBuild hunk separately: some
# Git-for-Windows autocrlf configurations cannot match it, although every
# functional ring/endpoint hunk is byte-for-byte the upstream MIT patch.
project_path = "TabletAudioSample/TabletAudioSample.vcxproj"
project = load(project_path)
target = "<TargetName>TabletAudioSample</TargetName>"
if project.count(target) != 4:
    raise ValueError("Pinned SysVAD project TargetName baseline changed")
project = project.replace(target, "<TargetName>IfAfAudio</TargetName>")
project = one(project, "</Project>",
    '  <Target Name="InfVerif" AfterTargets="StampInf" BeforeTargets="PreBuildEvent" />\n</Project>',
    project_path)
put(project_path, project)

# A separate PnP ROOT hardware ID, service, SYS and CAT prevent collision
# with a previously installed LoopIt7 Cable or Microsoft's sample.
inf_path = "TabletAudioSample/ComponentizedAudioSample.inx"
inf = load(inf_path)
inf = one(inf, 'ProviderName              = "SeventhSG"',
          'ProviderName              = "SkyRoof Contributors"', inf_path)
inf = one(inf, 'MfgName                   = "SeventhSG"',
          'MfgName                   = "SkyRoof Contributors"', inf_path)
# Unique endpoint category GUIDs. Both C++ and INF must agree.
render_old = "9f8da4bf-c4c1-4b12-938c-54e5de7031f4"
render_new = "cf71ae12-f042-4b58-bb5b-5f1168a69733"
capture_old = "d48deb08-fd1c-4d1e-b821-9064d49ae96e"
capture_new = "a6c3c9c3-0dd2-4a5a-8804-95f7088e9d6b"
inf = one(inf, render_old, render_new, inf_path)
inf = one(inf, capture_old, capture_new, inf_path)
put(inf_path, inf)
speaker_top = "EndpointsCommon/speakertoptable.h"
top = load(speaker_top)
top = one(top, render_old, render_new, speaker_top)
top = one(top,
          "0x9f8da4bf, 0xc4c1, 0x4b12, 0x93, 0x8c, 0x54, 0xe5, 0xde, 0x70, 0x31, 0xf4",
          "0xcf71ae12, 0xf042, 0x4b58, 0xbb, 0x5b, 0x5f, 0x11, 0x68, 0xa6, 0x97, 0x33",
          speaker_top)
put(speaker_top, top)
mic_top = "TabletAudioSample/micintoptable.h"
top = load(mic_top)
top = one(top, capture_old, capture_new, mic_top)
top = one(top,
          "0xd48deb08, 0xfd1c, 0x4d1e, 0xb8, 0x21, 0x90, 0x64, 0xd4, 0x9a, 0xe9, 0x6e",
          "0xa6c3c9c3, 0x0dd2, 0x4a5a, 0x88, 0x04, 0x95, 0xf7, 0x08, 0x8e, 0x9d, 0x6b",
          mic_top)
top = one(top, "KSAUDIO_SPEAKER_STEREO", "KSAUDIO_SPEAKER_MONO", mic_top)
put(mic_top, top)

# Native format must be identical at both ends: 48 kHz, mono, float32,
# nBlockAlign=4 and nAvgBytesPerSec=192000. Raw bytes can then flow through
# CCableRing without a kernel resampler, matching SkyRoof's WASAPI float AF.
render = "TabletAudioSample/cablespeakerwavtable.h"
data = load(render)
data = one(data, "#define CABLE_CHANNELS                  2",
           "#define CABLE_CHANNELS                  1", render)
data = one(data, "#define CABLE_BITS_PER_SAMPLE           16",
           "#define CABLE_BITS_PER_SAMPLE           32", render)
data = data.replace("KSDATAFORMAT_SUBTYPE_PCM", "KSDATAFORMAT_SUBTYPE_IEEE_FLOAT")
data = one(data, "KSAUDIO_SPEAKER_STEREO", "KSAUDIO_SPEAKER_MONO", render)
put(render, data)

capture = "TabletAudioSample/micinwavtable.h"
data = load(capture)
data = one(data, "#define MICIN_DEVICE_MAX_CHANNELS           2",
           "#define MICIN_DEVICE_MAX_CHANNELS           1", capture)
data = one(data, "#define MICIN_MIN_BITS_PER_SAMPLE_PCM       16",
           "#define MICIN_MIN_BITS_PER_SAMPLE_PCM       32", capture)
data = one(data, "#define MICIN_MAX_BITS_PER_SAMPLE_PCM       16",
           "#define MICIN_MAX_BITS_PER_SAMPLE_PCM       32", capture)
data = pattern_one(
    data,
    r"(WAVE_FORMAT_EXTENSIBLE,\s+)2,(\s+48000,\s+192000,\s+4,\s+)16,",
    r"\g<1>1,\g<2>32,",
    capture,
)
data = data.replace("KSDATAFORMAT_SUBTYPE_PCM", "KSDATAFORMAT_SUBTYPE_IEEE_FLOAT")
data = one(data, "KSAUDIO_SPEAKER_STEREO", "KSAUDIO_SPEAKER_MONO", capture)
put(capture, data)

common = "common.cpp"
data = load(common)
data = one(data, "const ULONG CableBlockAlign     = 2 * (16 / 8);",
           "const ULONG CableBlockAlign     = 1 * (32 / 8);", common)
put(common, data)

# Reject missing/wrong generated endpoints and any accidental stub driver.
inf = load(inf_path)
render_data = load(render)
capture_data = load(capture)
assert "Root\\IfAfAudio" in inf
assert "ifafaudio.sys" in inf and "ifafaudio.cat" in inf
assert 'CableInName               = "IF-AF-Audio Playback"' in inf
assert 'CableOutName              = "IF-AF-Audio"' in inf
assert "CABLE_CHANNELS                  1" in render_data
assert "CABLE_BITS_PER_SAMPLE           32" in render_data
assert "KSDATAFORMAT_SUBTYPE_IEEE_FLOAT" in render_data
assert "MICIN_DEVICE_MAX_CHANNELS           1" in capture_data
assert "KSDATAFORMAT_SUBTYPE_IEEE_FLOAT" in capture_data
assert "const ULONG CableBlockAlign     = 1 * (32 / 8);" in load(common)
assert render_new in inf and capture_new in inf
print("IF-AF-Audio overlay ready: ROOT\\IfAfAudio, render + capture, 48-kHz mono IEEE float32.")
