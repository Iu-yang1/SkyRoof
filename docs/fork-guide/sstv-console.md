# SkyRoof SSTV Console

Open **View → SSTV RX / TX** to access four dockable pages: Receive, Transmit, Gallery and QSO Log.

## Receive

Select an AF capture device, RS-BA1/Windows loopback, 12-kHz IF capture or IF loopback. The RX selector includes **all 43 TX profiles** plus automatic SSTV detection. Native Robot/PD are handled by SkySignals auto VIS decoding; other RGB/extended modes support manual start. RX tuning includes audio frequency correction, auto VIS correction, WAV replay, image slant and image archive.

## Gallery management and shared tone scale

The Gallery / History tab supports **Import image**, **Export selected image**, and **Delete selected image** (confirmation required). Imported PNG/JPEG/BMP/GIF images are normalized to PNG; deleting a gallery entry also removes its optional matching receive-metadata file. Selecting an existing TX thumbnail updates the editor **without appending another recent image**, while right-clicking opens a context menu.

RX and TX now share the **same orange/black MMSSTV-style AF/IF tone ruler**, labelled only 1200, 1500, 1900, and 2300 Hz. On RX, the black lower strip draws live audio spectrum data; on TX, it remains blank.

## MMSSTV-style TX workspace

The TX page now has a picture-edit toolbar across the top, a large image preview at left, a fast 43-mode selector with station/QSO/caption controls at right, and clickable recent/transmitted-image thumbnails below. Quick modes and the standard mode dropdown are synchronized. A small orange tone scale indicates **AF frequency reference marks** (1200/1500/1900/2300 Hz); it is not a live TX spectrum. Station details include MY/HIS calls, TX/RX RSV reports, name, QTH, and notes. RST/name/QTH are preserved in JSONL and exported to ADIF. The side editor scrolls at small dock-window dimensions.

## Multiple captions, RSV and picture performance

The editor supports **up to eight independent text layers** (three original layers plus five new captions) with add/duplicate/remove, editable content, per-layer color/size and dragging. Select a variable and insert it into the active layer: `MYCALL`, `HISCALL`, `MYGRID`, `SAT`, `MODE`, `RSV_TX`, `RSV_RX`, `NAME`, `QTH`, `NOTE`, `UTC`, `DATE`, `HHMMSS`, `TXMHZ`, `RXMHZ` and existing aliases. Three-layer template files remain supported; extra layers are persisted automatically.

SSTV signal-report UI labels now use **RSV**. Exported ADIF retains the standardized `RST_SENT`/`RST_RCVD` field names. Large photos are downscaled at load time, archival thumbnails are cached, undo/redo transfers bitmap ownership and RX PNGs can be saved by direct file copy.

## TX picture editing

Open or paste an image and select a mode. The editor provides separate **My callsign**, **Their callsign**, and **custom caption** fields. Choose a layer in **Edit object**, adjust its font size (percentage of image width) and color, or **drag its text in the preview** to position it. Use the mouse wheel above the preview to resize the selected text layer.

Saved user templates preserve all three captions, positions, sizes, colors and the chosen built-in background style. Use **Save template** to create or replace a personal layout, and **Delete template** to remove the selected user template; built-in presets cannot be deleted. Existing `SstvImages/templates.json` files remain readable.

Caption macros: `{CALL}`, `{MYGRID}`, `{HISCALL}`, `{SAT}`, `{MODE}`, `{UTC}`, `{DATE}`, `{TXHZ}`, `{RXHZ}`. **Plain image** intentionally renders no captions.

All 43 SSTV TX profiles can export 48-kHz mono WAV without keying a transceiver. On-air transmission uses a deliberately chosen soundcard endpoint and manual CAT/PTT authorization; **Stop** or **Esc** terminates an SSTV operation. Optional CW/FSK ID uses the text in the My callsign field.

## Gallery and QSO Log

Browse or reuse recorded pictures, associate received images with a manually entered QSO, and export UTC ADIF records.

## Implementation

The WinForms tests cover all-mode selector parity, drag and text editing, backward-compatible template persistence and deletion, the image editor and virtual IC-9700 CAT/PTT tests. The native and extended RX implementations have distinct synchronization paths, and some extended-mode reception remains sensitive to clock drift and fading.
