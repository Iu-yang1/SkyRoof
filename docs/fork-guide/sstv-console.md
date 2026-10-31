# SkyRoof SSTV Console

Open **View → SSTV RX / TX** to access four dockable pages: Receive, Transmit, Gallery and QSO Log.

## Receive

Select an AF capture device, RS-BA1/Windows loopback, 12-kHz IF capture or IF loopback. The RX selector includes **all 43 TX profiles** plus automatic SSTV detection. Native Robot/PD are handled by SkySignals auto VIS decoding; other RGB/extended modes support manual start. RX tuning includes audio frequency correction, auto VIS correction, WAV replay, image slant and image archive.

## TX picture editing

Open or paste an image and select a mode. The editor provides separate **My callsign**, **Their callsign**, and **custom caption** fields. Choose a layer in **Edit object**, adjust its font size (percentage of image width) and color, or **drag its text in the preview** to position it. Use the mouse wheel above the preview to resize the selected text layer.

Saved user templates preserve all three captions, positions, sizes, colors and the chosen built-in background style. Use **Save template** to create or replace a personal layout, and **Delete template** to remove the selected user template; built-in presets cannot be deleted. Existing `SstvImages/templates.json` files remain readable.

Caption macros: `{CALL}`, `{MYGRID}`, `{HISCALL}`, `{SAT}`, `{MODE}`, `{UTC}`, `{DATE}`, `{TXHZ}`, `{RXHZ}`. **Plain image** intentionally renders no captions.

All 43 SSTV TX profiles can export 48-kHz mono WAV without keying a transceiver. On-air transmission uses a deliberately chosen soundcard endpoint and manual CAT/PTT authorization; **Stop** or **Esc** terminates an SSTV operation. Optional CW/FSK ID uses the text in the My callsign field.

## Gallery and QSO Log

Browse or reuse recorded pictures, associate received images with a manually entered QSO, and export UTC ADIF records.

## Implementation

The WinForms tests cover all-mode selector parity, drag and text editing, backward-compatible template persistence and deletion, the image editor and virtual IC-9700 CAT/PTT tests. The native and extended RX implementations have distinct synchronization paths, and some extended-mode reception remains sensitive to clock drift and fading.
