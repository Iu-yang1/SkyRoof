# RC-3040S LAN rotator driver (TCP 60020 + WebSocket 60030)

This is SkyRoof's **only dedicated RC-3040S integration**. The experimental DTrac NRL2 UDP relay driver, its RotorID/SID settings, discovery/codec/transport source, old operations dialog, and protocol-specific tests were removed from SkyRoof. Research source and historical settings schema are preserved separately in [DTracNRL2.NET](https://github.com/Iu-yang1/DTracNRL2.NET/tree/main/docs/archive/skyroof-nrl2).

To use the direct LAN controller, choose **Rotator > Controller Driver: Native**, **Native Protocol: Rc3040sLan**, **Rotator Network Host** (your controller's current IPv4), and **Rotator Network Port: 60020**. The protocol automatically uses TCP; GS-232B `Wxxx yyy\r` moves, `C2\r` queries actual position, and `S\r` stops. No external NRL2 relay, client SID, Rotor ID, or internet service is used.

Right-click the rotator status icon for manual wheel. The **RC-3040S** button launches the LAN management dialog. PARK and satellite tracking continue to use the existing single TCP/60020 motor I/O thread.

The on-demand **management WebSocket** at TCP/60030 can read the configuration (including `AZ_SPD`, `EL_SPD` when actually emitted), offer AZ/EL speed controls in °/s (converting to raw AT values), and send one explicitly confirmed setting at a time with independent configuration readback. **Dragging a slider does not send AT commands.** Revert resets the local sliders to last read values, not factory settings. Restart is a separate doubly confirmed operation. Firmware 260223_C has hardware-verified `AT+TEST` echo, but speed writes/reboot/EEPROM persistence do **not** have real-hardware acceptance evidence.

### Legacy settings safety

NRL2 UDP's former numeric protocol ID was **10**. Rc3040sLan has ID **11**, and old ID 10 is deliberately not reassigned. Loading an older numeric-ID NRL2 configuration forces rotator **Enabled=false**; it does **not** reinterpret server IP / 60050 as the controller's LAN endpoint. Operator must deliberately select `Rc3040sLan`, enter the real controller IP, confirm 60020 and re-enable. Historical NRL2-only Settings.json fields disappear from SkyRoof's active schema and are documented in DTracNRL2.NET; do not put real SID/RotorID in any repository.

### Motion planning status

The Motion Settings window offers Direct, Precision Stop, Trapezoidal and S-curve modes. A profile can only control the rotator after explicitly enabling it and completing hardware checks; Direct remains the default. No AT speed writes occur during tracking.


### RC-3040S PARK / Home verification safety (P0–P2)

- PARK is an absolute GS-232 target route, **not** a firmware encoder-reset,
  end-stop seek, or mechanical home calibration. Do not issue AT+INIT/RESET or
  undocumented firmware home commands as part of PARK.
- Each waypoint must have a freshly transmitted W frame and new C2 samples;
  RC-3040S waits for >=3 distinct position reads within 1 degree with a
  2.5-second settling dwell and <=0.5 degree sample-to-sample change.
- The last waypoint then requests S (STOP) through the existing single TCP
  motor I/O worker. PARK is confirmed only after the S frame is actually
  transmitted and >=2 subsequent distinct C2 readings over >=1 second remain
  stable and in tolerance. Loss of feedback, STOP timeout or unexpected
  post-STOP position changes aborts the route and requests STOP again.
- The UI deliberately says **PARK C2 OK** rather than **Home calibrated**.
  Zero has *not* been checked by any independent physical sensor: the
  operator must compare a mechanical reference before relying on it.
- PARK diagnostics record the UI and wire target coordinates, configured
  azimuth/elevation offsets, actual W/S transmission timestamps and the
  raw TCP/60020 C2 replies. The log archive is accessible via
  **Tools > Open Log Archive Folder**; logs use the existing Serilog
  rotating files under the product's AppData/Logs directory.
- This improves early-stop safety but cannot correct step loss, backlash,
  a controller-side internal-counter zero error, or an invalid physical
  limit/home calibration. Test at a safe middle-of-travel target first.
