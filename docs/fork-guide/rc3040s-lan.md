# RC-3040S LAN rotator driver (TCP 60020 + WebSocket 60030)

This is SkyRoof's **only dedicated RC-3040S integration**. The experimental DTrac NRL2 UDP relay driver, its RotorID/SID settings, discovery/codec/transport source, old operations dialog, and protocol-specific tests were removed from SkyRoof. Research source and historical settings schema are preserved separately in [DTracNRL2.NET](https://github.com/Iu-yang1/DTracNRL2.NET/tree/main/docs/archive/skyroof-nrl2).

To use the direct LAN controller, choose **Rotator > Controller Driver: Native**, **Native Protocol: Rc3040sLan**, **Rotator Network Host** (your controller's current IPv4), and **Rotator Network Port: 60020**. The protocol automatically uses TCP; GS-232B `Wxxx yyy\r` moves, `C2\r` queries actual position, and `S\r` stops. No external NRL2 relay, client SID, Rotor ID, or internet service is used.

Right-click the rotator status icon for manual wheel. The **RC-3040S** button launches the LAN management dialog. The software DEMO waypoint sequence and PARK functions continue to use actual position feedback and the existing single motor I/O thread, not a separate motor socket.

The on-demand **management WebSocket** at TCP/60030 can read the configuration (including `AZ_SPD`, `EL_SPD` when actually emitted), offer proposed raw speeds in UI sliders, and send one explicitly confirmed setting at a time with independent configuration readback. **Dragging a slider does not send AT commands.** Revert resets the local sliders to last read values, not factory settings. Restart is a separate doubly confirmed operation. Firmware 260223_C has hardware-verified `AT+TEST` echo, but speed writes/reboot/EEPROM persistence do **not** have real-hardware acceptance evidence.

### Legacy settings safety

NRL2 UDP's former numeric protocol ID was **10**. Rc3040sLan has ID **11**, and old ID 10 is deliberately not reassigned. Loading an older numeric-ID NRL2 configuration forces rotator **Enabled=false**; it does **not** reinterpret server IP / 60050 as the controller's LAN endpoint. Operator must deliberately select `Rc3040sLan`, enter the real controller IP, confirm 60020 and re-enable. Historical NRL2-only Settings.json fields disappear from SkyRoof's active schema and are documented in DTracNRL2.NET; do not put real SID/RotorID in any repository.

### Motion planning status

The settings UI exposes Direct / Precision Stop / Trapezoidal / S-Curve preferences, but the engine currently keeps direct GS-232 W execution until a properly validated feedback-driven planner is integrated and hardware-tested. Merely showing the profile options **does not** implement physical jerk-limited motor motion. No automatic EEPROM writes during satellite tracking. Continue to use the physical emergency stop and soft limits.
