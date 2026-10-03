# Hardware front-end validation checklist

**No item below is marked passed in this checkout.** Complete it before producing final wiring or calling a hardware capture supported.

## Identity and documentation

- [ ] Photograph each board, both sides, with markings legible.
- [ ] Record manufacturer and exact 74LVC245 ordering code/suffix/package.
- [ ] Attach the manufacturer datasheet and board schematic or trace the board.
- [ ] Identify VCC/GND, A/B sides, DIR, `/OE`, pull resistors, LEDs, connector pinout, and protection components.
- [ ] Verify the board is an LVC245, not HC245, an auto-direction translator, or an isolated device.

## Electrical compatibility

- [ ] Record target high/low voltage and tolerance at the monitored connector.
- [ ] Check VIH/VIL at the selected buffer VCC from the exact datasheet. Specifically test the proposed 1.8 V case; do not infer it from the LVC family name.
- [ ] Check input/output overvoltage, absolute maximum, Ioff, powered-off input tolerance, partial-power-down behavior, and back-power paths.
- [ ] Decide whether target and Teensy share ground. If not, stop: the proposed non-isolated front end has no valid signal reference.
- [ ] Check target-side loading, buffer input leakage/capacitance, probe capacitance, and cable length.
- [ ] Select connector ESD protection and series damping only after its voltage, capacitance, and pulse ratings are documented.

## Fixed direction and startup

For each 8-bit buffer, the proposed receive-only topology is **target -> A inputs, B outputs -> Teensy inputs**, subject to the exact board schematic. `DIR` must be held at the fixed direction required by the exact datasheet; `/OE` must be held inactive during power-up and connection. The MCU capture pins must be configured as inputs before `/OE` is enabled. Add documented resistor defaults so a disconnected MCU cannot leave DIR/OE floating.

- [ ] Validate the actual DIR polarity from the datasheet and board.
- [ ] Validate the actual `/OE` polarity; it is normally active-low, but the board wiring must be checked.
- [ ] Ensure Teensy pins are inputs/reset-safe before buffer outputs are enabled.
- [ ] Ensure buffer outputs cannot contend with target-side drivers. The B/Teensy side must not be connected to another output.
- [ ] Ensure target-side pins cannot drive the buffer output side through an accidental board reversal.
- [ ] Define safe connect/disconnect sequencing for target power, buffer VCC, Teensy USB power, and ground.
- [ ] Test powered-off target and powered-off Teensy cases for back-powering with current limited and instrumented.
- [ ] Add current limiting/series resistance or stop the test if an unvalidated state can create contention.

## I2C/open-drain boundary

Do not add pull-ups to the target by default. An LVC245 configured in one fixed direction does not reproduce the bidirectional wired-AND behavior of I2C and is not a transparent repeater. Passive monitoring must observe both SCL and SDA in a way that does not drive them; if the selected front end cannot guarantee that behavior, do not connect it to I2C.

## Bench procedure after review

1. Power the buffer and Teensy with no target connected. Confirm VCC/GND and `/OE` inactive with a current-limited supply.
2. With a scope or logic analyzer, verify buffer outputs remain high impedance while `/OE` is inactive and during MCU reset.
3. Connect a low-voltage, current-limited deterministic source through the reviewed connector and common ground. Start at a slow rate.
4. Verify input thresholds, output levels, overshoot/ringing, delay, and no unexpected current into target or Teensy pins.
5. Increase rate only while observing edge integrity. Record exact VCC, temperature, cable/probe, source, and measured delay/skew.
6. Test power sequencing and disconnect sequencing one case at a time.
7. Use an independent reference instrument/source to compare sample count, edge timing, and lost pulses. Same-device loopback is not independent validation.

Report measured traces and stop on any unexplained current, heating, contention, or level violation. A passed bench test is required before pin-level wiring is published.
