# RC-3040S LAN motion / AT control P0–P6 implementation and acceptance

## Scope and evidence

- **Controller:** SkyRoof Native / `Rc3040sLan`, a dedicated GS-232B TCP/60020 transport. No NRL2 UDP or remote relay in SkyRoof.
- **Management:** Independent on-demand `ws://<controller-ip>:60030/`. Real 260223_C hardware confirmed C2 read and `AT+TEST` exact echo, as well as `AT+AZ_SPD=05000 / AT+EL_SPD=05000` strings after echo. It has **not** validated speed writes, actual EEPROM retention, restart, hardware acceleration or automatic jerk limiting.
- **One motor command writer:** NativeRotatorControlEngine. Manual wheel, satellite target updates, STOP, PARK and DEMO share its motion arbitration and safety latches. The speed control panel NEVER writes `W` or `S` through a second motor TCP connection.

## P0 – protocol separation / safety

Direct LAN motion uses GS-232B W/C2/S on 60020. Restart and AT raw-speed writes use only management 60030. Previously experimental NRL2 UDP was removed, with legacy sources archived in DTracNRL2.NET. Unsupported numeric legacy enum value 10 fails closed and is not automatically interpreted as the new LAN enum value 11. STOP always wins over queued target updates. Existing feedback jump, TCP reconnect, lost-position and PARK safety interlocks stay active.

## P1 – management + manual wheel UI

Right-click the rotator status to open the existing wheel popup. On `Rc3040sLan` only, an **inline** lower section appears with AZ and EL raw speed sliders, Read, Save AZ, Save EL, Revert, Restart. Other protocols keep the original popup dimensions. The dialog `RC-3040S` still offers separate DEMO, detailed status and Motion Settings.

- Read obtains the on-device configuration; firmware and the device ID suffix are used to pin subsequent write/restart sessions.
- Drag changes **UI only**. Nothing is transmitted until one explicit Save AZ or Save EL; both actions require a warning and a precise typed confirmation, never auto-repeat.
- Revert restores the last **read** values in local sliders. It is not factory reset, not a mechanical re-home and not an EEPROM write.
- Restart has a separate double confirmation and requires the motor TCP thread to have sent STOP before issuing one `AT+RESET`; this does **not** prove the motor physically stopped or the device rebooted. User must verify the physical state.
- Write/Restart may persist or change motor configuration on 260223_C. Do not test unattended or with moving hardware. If uncertain, do not retry, re-read first after operator inspection.

## P2 – closed-loop position and feedback-rate estimator

Only fresh GS-232 `C2` readings can authorize a new target. `Rc3040sFeedbackEstimator` compares timestamped mechanical AZ (0..450) and EL (0..90/180 according to configuration), rejects stale/duplicate readings and implausible jumps; never reduces AZ modulo 360 when beyond mechanical 360. The estimator's rate is for diagnostics, **not a physical brake or motion safety guarantee**. Faults still trigger existing stop and latching supervisor.

## P3 – profiles

`Rc3040sMotionPlanner` is a pure C# reference planner supporting Direct, PrecisionStop, Trapezoidal and jerk-bounded S-Curve **setpoint generation**. For each axis the braking envelope is `v_ref<=sqrt(2*a_max*remaining_distance)`; acceleration is limited by configured `a_max`, and S-Curve mode also constrains `|Δa|/Δt<=j_max` during normal trajectory integration. The moving target is bounded by mechanical min/max and a capped look-ahead from actual feedback.

**IMPORTANT**: GS-232 `Wxxx yyy` has no velocity/acceleration field; the RC-3040S may complete any intermediate W at its fixed, firmware-configured speed. SkyRoof therefore **cannot honestly guarantee** that these reference trajectories become physical jerk-limited motor motion. Near-target quantization may cause stepwise movement or repeated stops. Original software profiles default to Direct/off.

## P4 – feedback-aware execution, simulator & rate limit

The new planner is integrated into the same native TCP I/O worker and is active ONLY when both **Enable experimental setpoint planner** and **Physical hardware validated by operator** are selected and the profile is not Direct. A stale feedback sample blocks the new planned endpoint. Planner reference resets after STOP, manual takeover and reconnect, and new motion frames are limited to an interval of at least 450ms. The safety and polling logic remain the same. Simulation tests use synthetic perfect and lagging plants with target swaps, latency, bounds, 450° tracking and STOP-like resets; synthetic behavior is explicitly not physical device acceptance.

## P5 – physical acceptance is NOT complete

An operator with access to the specific 260223_C unit must complete the following before enabling hardware validation:

1. Ensure physical hard limits and emergency STOP, isolate motor load where possible; backup exact device configuration (both raw speeds). Verify real-time feedback (C2) and motor STOP/reconnect procedures.
2. Measure AZ and EL `05000` motor speed over multiple **controlled, limited-angle** moves with time-stamped C2 feedback and compare to legacy 251217 nominal formula (AZ ~4.98 °/s; EL ~5.51 °/s). Do not assume formulas are correct for 260223_C.
3. Verify manual/Track/PARK/DEMO takeover, unexpected link loss, feedback jumps, a STOP while moving, and 360°/450° cable-limit transitions in a safe fixture.
4. Only in an electrically safe setup, and with operator consent, attempt a **single** AT speed setting update and re-read, then after a controlled power cycle determine persistence, and restore the original configuration explicitly. Do not use a realtime speed-write feedback loop. Do not auto-retry ambiguous EEPROM writes.
5. Collect actual position-vs-time traces for direct and experimental profiles, calculate overshoot, settling, maximum speed/acceleration and jerk, and disable profile execution if quantified limits are not met. Reference trajectory pass is **not** sufficient to declare real S-curve.

If the WebSocket management listener intermittently refuses TCP connections, it must not affect STOP/position telemetry on TCP/60020; never attempt to bypass those safety controls with a management command.

## P6 – integrated SkyRoof behaviors

All manual wheel, satellite tracking, PARK and software DEMO remain on the single native worker. The quick speed panel does not block the wheel; manual operations are disabled while live satellite tracking owns the motor. Motion-profiles settings are scoped to RC-3040S LAN and persisted in existing Settings.json. Simulator CI covers default-off behavior and setpoint limits; **physical P5 results are required before signing off the complete P0–P6 hardware rollout**.

## Source, tests and repeatability

- `SkyRoof/Rotator/Native/Rc3040sMotionPlanner.cs`: pure reference profile with validation.
- `SkyRoof/Rotator/Native/Rc3040sFeedbackEstimator.cs`: timestamped C2 rate estimates.
- `SkyRoof/Rotator/Native/NativeRotatorControlEngine.cs`: single TCP motor output worker, STOP, polling and optional setpoint planning.
- `SkyRoof/Rotator/Native/Rc3040sManagementService.cs`: bounded WebSocket read, one-shot explicitly authorized AT writes, firmware/device matching.
- `SkyRoof/Widgets/Rc3040sQuickControls.cs`: inline popup quick controls; no automatic AT on slider changes.
- `VE3NEA.Dsp.Tests/Rc3040sMotionPlannerTests.cs`, `Rc3040sMotionSimulationTests.cs`, `Rc3040sLanMigrationTests.cs`: offline regression.

**No hardware movement or AT writes have been sent by these GitHub CI jobs**.
