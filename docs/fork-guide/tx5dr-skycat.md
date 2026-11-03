# TX-5DR + SkyRoof embedded SkyCAT: safe coexistence

This guide documents actual protocol capabilities and avoids a false-success CAT configuration.

| Port | Actual service | TX-5DR compatibility |
| --- | --- | --- |
| 4532 | SkyCAT main line-based CAT server | Not a complete Hamlib NET rigctl endpoint; do not assume native compatibility |
| 4534 | Restricted WSJT-X Hamlib NET rigctl proxy | Frequency/mode/PTT reads and leased CAT PTT; frequency/mode/VFO/split/tone writes are acknowledged but NOT sent to the radio |
| 4535 | IC-9700 binary scope stream | Not rigctl |
| 4537 | Remote Control Switch auxiliary settings | Not rigctl or PTT |
| 4538 | IC-9700 CW Keyer | Not rigctl |

SkyRoof must remain sole Doppler/CAT state owner while embedded SkyCAT has the CI-V COM port open. Multiple software clients must not independently retune the same radio. TX-5DR's own Hamlib subprocess isolation and CTCSS/DCS TONE/CSQL support do not alter the restrictions of SkyCAT's 4534 proxy. Successful RPRT 0 replies from that proxy to frequency/mode/tone writes are NOT confirmed hardware changes.

For receive-only identification, run Windows PowerShell from the repository root:

    .\scripts\Probe-Tx5drSkycat.ps1

This probe connects only to 127.0.0.1:4534 and sends three READ queries: get_info, f, and m. It never sends PTT, frequency/mode or tone writes.

For independent TX-5DR CAT control, first disable SkyRoof's CAT ownership and use a separately validated full radio connection; neither 4534 nor the special-purpose auxiliary ports can substitute for this. Verify physical PTT-off before any real station test; a disconnected TCP socket is not evidence that RF transmission stopped.

See [Embedded SkyCAT](embedded-skycat.md) and [SkyCAT command details](https://github.com/Iu-yang1/SkyCAT/blob/master/docs/skycatd.md).
