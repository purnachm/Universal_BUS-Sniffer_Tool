# SniffOS requirements and feasibility baseline

**Status: Milestone 1 documentation and simulator implementation only.** This document deliberately separates facts, assumptions, proposals, and measurements. No physical hardware result is claimed.

## Verified facts

- The available MCU is a Teensy 4.1, based on an NXP i.MX RT1062 family MCU. The exact board revision, installed firmware, Teensyduino/Arduino core version, and exposed pin selection have not been supplied.
- Two boards are marked `74LVC245`. A marking alone does not identify the manufacturer, exact ordering code, package, schematic, supply wiring, or whether the board includes protection, pull resistors, or level shifting.
- The PC target is Windows. The first application is a WPF desktop application using C# and a supported .NET LTS target (`net8.0-windows` in this repository).
- The intended electrical role is a receive-only bridge. SniffOS is not an inline electrical bridge, bus repeater, injector, or protocol transmitter.
- The repository currently has no physical wiring, firmware, bench measurements, or validated instrument traces. Therefore hardware rates, voltage compatibility, pin muxes, USB throughput, timing error, and loss performance are **unverified**.

## Scope and safety boundary

| Interface | Initial treatment | Boundary |
|---|---|---|
| UART logic level | Passive sampled capture and decoder | Level, polarity, framing, and timing must be configured and validated. |
| SPI | Passive sampled capture and decoder | CPOL/CPHA, mapping, word size, and CS are explicit settings. |
| I2C | Passive sampled capture and decoder | No target pull-up is added. The LVC245 is not treated as a transparent bidirectional open-drain repeater. |
| Generic parallel | Raw waveform inspection | Atomicity and skew depend on the selected capture architecture. |
| 1-Wire | Deferred | Timing validation and a dedicated decoder are required. |
| CAN/CAN FD | Deferred | Requires a suitable external differential transceiver; controller and decoder support are separate questions. |
| RS-232, RS-485, LIN | Deferred | Each requires the correct receiver/transceiver. |
| USB physical bus | Not supported by this GPIO front end | A USB analyzer/front end is a separate design. |
| Analog | Not supported | The digital buffer front end is not an ADC or analog instrument. |

Use only on equipment owned or authorized for testing. The initial front end must be treated as **unvalidated and potentially unsafe** until the hardware checklist is completed.

## Proposed architecture

```text
Target bus (authorized equipment)
  -> documented protection / connector / common ground
  -> two fixed-direction, OE-controlled buffer channels (if electrically compatible)
  -> Teensy 4.1 input-only capture pins
  -> acquisition engine (sampled or timestamped edge, firmware)
  -> bounded packet queue + versioned framed protocol
  -> USB CDC transport (only after firmware and measured transport validation)
  -> Windows transport reader
  -> protocol parser / loss detector / chunk writer
  -> decoders (UART, SPI, I2C)
  -> multiresolution waveform surface + virtualized event table
```

The current repository implements the Windows simulator path, the protocol framing/parser, raw capture model with explicit gap records, chunked `.sniffcap` storage, independent decoder modules, and a WPF/MVVM surface. The hardware transport is an interface only; no COM-port implementation is included until a specific firmware USB mode and measured Windows transport have been selected.

## Front-end facts still needed before wiring

The two LVC245 boards must not be wired from the marking alone. For each board, obtain:

1. Manufacturer and complete part number printed on the IC and board, including suffix/package.
2. Board schematic or clear front/back photographs showing VCC/GND, A/B labels, DIR, /OE, pull resistors, indicator circuits, connectors, and any protection parts.
3. Supply voltage actually intended for the board and supply wiring.
4. Which side is target and which side is Teensy, including fixed DIR and /OE default states.
5. Target logic-high/low voltage, idle behavior, common-ground availability, and maximum frequency/edge rate.
6. Whether the target can be powered off while the Teensy/front end remains powered, and the reverse.
7. Any available series resistance, ESD protection, connector keying, and probe/cable details.

Before final wiring, the exact LVC245 datasheet must be checked for VIH/VIL at the selected VCC, input/output overvoltage, powered-off input tolerance, partial-power-down behavior, Ioff/back-powering, absolute maximum ratings, propagation delay, and output current. A 3.3 V supply does not by itself prove that 1.8 V targets are recognized. An LVC245 must not be confused with HC245, an automatic translator, or an isolated part.

**Not yet approved:** a pin-level wiring diagram, 5 V target connection, 1.8 V target connection, powered-off target connection, or I2C connection. Common ground, target-side loading, connector sequencing, and contention analysis remain open.

## Acquisition feasibility comparison

| Approach | Strength | Limitation / decision status |
|---|---|---|
| Direct GPIO polling | Simple baseline and useful self-test | CPU jitter, poor sustained rate, not atomic across ports. Proposed only as a diagnostic baseline. |
| GPIO + timer/DMA | Regular samples with lower CPU load | Requires exact RT1062 DMA request routing, pin mux, memory placement, alignment, and cache maintenance checks. Candidate for sampled MVP. |
| FlexIO + DMA | Hardware-assisted capture and potentially coherent shifter words | Exact Teensy-exposed pins, FlexIO instance/shifter/timer/DMA routes must be checked against the RT1062 reference manual and board pin mapping. Not chosen until verified. |
| Edge timestamping | Compact event stream and good sparse-edge visibility | Misses pulses/edges beyond timer/input bandwidth; not equivalent to a fixed-rate sample. Candidate later mode. |
| Peripheral-assisted UART/SPI/I2C | Protocol-aware and efficient for known buses | Can miss malformed traffic and does not preserve arbitrary raw waveforms. Not the raw capture MVP. |

The MVP will distinguish **fixed-rate samples** from **edge timestamps** in the protocol and capture metadata. A fixed-rate capture can miss a pulse narrower than its sample interval and has inter-channel skew if channels span ports/peripherals. An edge capture can miss edges while its queue/timestamp hardware is saturated and does not show level state between edges. No supported rate is claimed until a deterministic source and an independent reference instrument produce measurements.

### Candidate bandwidth calculations (not measurements)

For a fixed-rate sample where each sample is one 16-bit word:

```text
raw payload = sample_rate × 2 bytes × enabled channels packed as a 16-bit word
```

If all 16 channels are represented in a `uint16` word:

| Sample rate | Raw payload | Approximate decimal bit rate |
|---:|---:|---:|
| 1 MSa/s | 2 MB/s | 16 Mb/s |
| 5 MSa/s | 10 MB/s | 80 Mb/s |
| 10 MSa/s | 20 MB/s | 160 Mb/s |

These are payload rates only. Add packet header/CRC, framing/escaping if used, USB and host scheduling overhead, and storage writes. If only 4 channels are packed into a byte, the format still needs a defined atomic sample word or packing rule; it cannot be assumed to have zero skew. A finite buffer can preserve a bounded capture during a host stall; a sustained stream cannot preserve timing by waiting for USB. The firmware must detect and report acquisition overrun instead of silently slowing or dropping data.

## Protocol and GUI choices

The repository protocol v1 uses a `SN` marker, version, fixed 30-byte little-endian header, message type, flags, transport sequence, configuration ID, acquisition sample index, bounded payload length, and CRC-32. The parser bounds allocations and resynchronizes after noise/corruption while emitting parse issues. Sequence and sample-index gaps become explicit capture gaps; unknown loss length remains unknown.

The GUI uses WPF/MVVM and CommunityToolkit.Mvvm. USB reads, capture processing, file I/O, and decoding are asynchronous. The waveform is a custom `FrameworkElement` that requests a min/max envelope per visible pixel; it does not create a visual for every sample. A bounded `Channel<T>` is used in the simulator and device client with `FullMode=Wait`, not silent drop mode.

The implemented interchange format is versioned chunked `.sniffcap`. CSV export is planned for decoded event tables and a documented column/timebase convention; VCD export is planned for raw digital channel changes with explicit gap markers or a refusal to represent unknown gaps as continuous time. sigrok/PulseView compatibility is an evaluation item, not a compatibility claim; it will require fixture exports and import tests against a pinned PulseView/sigrok version.

USB CDC is the initial transport proposal because it is driver-light on Windows and straightforward to prototype. The optional `UsbCdcSerialTransport` is not a proof of firmware support or throughput. A Teensy firmware implementation must expose this exact protocol, and transport benchmarks must publish conditions, rate, duration, channel count, observed loss, and OS/toolchain versions before sustained capture claims.

## Milestone 1 exit criteria

Requirements and feasibility exits only when:

- Scope and the receive-only safety boundary are written.
- Facts, assumptions, proposals, unresolved hardware facts, and non-claims are visible.
- Raw bandwidth examples include payload and protocol-overhead caveats.
- A hardware information request identifies the exact buffer boards, supply/DIR/OE wiring, target levels, and maximum signal rates needed before final wiring.
- The acquisition decision is explicitly held until RT1062 reference-manual, pin-mux, DMA/FlexIO, memory/cache, and independent timing validation are complete.
- The repository has a buildable design baseline and deterministic simulator tests; those tests do **not** satisfy physical hardware acceptance.

## Next validation step

Provide the exact two board part numbers and board schematics or clear photographs, target bus voltage(s), common-ground/power-off conditions, and highest expected signal rate. I will then produce a reviewed front-end proposal and a bench-safe wiring procedure. Do not connect an unknown LVC245 board to a live target or Teensy based on this document.
