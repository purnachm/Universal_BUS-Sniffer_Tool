# SniffOS

SniffOS is a proposed open-source Windows digital bus instrumentation system: a passive, receive-only 16-channel capture front end, a Teensy acquisition bridge, and a WPF desktop application. It is for equipment owned or authorized for test. It is not an electrical bus bridge, transparent repeater, injector, or protocol transmitter.

## Current implementation status

This repository now contains a **simulator-first Windows GUI baseline**, not validated hardware support:

- C# WPF/MVVM application targeting `net8.0-windows` with CommunityToolkit.Mvvm.
- Separate Core modules for transport, protocol framing/parser, acquisition/gap accounting, storage, decoders, and waveform rendering.
- Deterministic simulated Teensy device with bounded queues and no silent queue-drop mode.
- Custom multiresolution waveform surface with viewport culling; no UI element per sample.
- Channel labels, sample settings, immediate/edge trigger settings, connect/start/stop, zoom/pan, cursors, decoded-event table, `.sniffcap` save/load, and visible gap status.
- Tests for malformed/fragmented packets, CRC recovery, gap records, truncated files, UART/SPI/I2C fixtures, cancellation, and simulator state transitions.
- A transport interface ready for a future USB CDC implementation. No COM-port transport is included until a specific firmware endpoint and measured Windows transport have been selected.

The checkout was created in an environment without the `dotnet` executable, so build/test results are **not claimed**. The first Windows validation step is to run the commands below and report any compiler/test result.

## Build and test on Windows

Install the supported .NET 8 SDK and a Windows WPF-capable build environment:

```powershell
dotnet restore SniffOS.sln
dotnet test SniffOS.sln --configuration Release
dotnet build src\SniffOS.App\SniffOS.App.csproj --configuration Release
```

Run `src\SniffOS.App\bin\Release\net8.0-windows\SniffOS.exe`. The default device is explicitly labeled a deterministic simulator. Hardware transport must not be inferred from a COM port.

## Documents and engineering gates

- [Requirements and feasibility](docs/Requirements-and-Feasibility.md) — facts, assumptions, proposals, non-claims, bandwidth examples, and milestone 1 exit conditions.
- [Architecture](docs/Architecture.md) — modules, data flow, acquisition decision gate, and threat model.
- [Protocol v1](docs/Protocol-v1.md) — framing, bounds, CRC, sequence/index gap semantics, and transport measurement gate.
- [Hardware validation checklist](docs/Hardware-Validation-Checklist.md) — exact board identity, electrical safety, DIR/OE startup, I2C boundary, and bench procedure.
- [Acceptance matrix](docs/Acceptance-Matrix.md) — what is present, what is blocked, and what has not been measured.

## Hardware is intentionally blocked

Do not wire the two boards marked `74LVC245` from that marking alone. Before a final pin map, provide each exact manufacturer/part number, board schematic or clear photographs, supply/DIR/`/OE` wiring, protection parts, target voltage, power-off conditions, and maximum signal rate. Verify the exact datasheet's VIH/VIL, powered-off tolerance, Ioff/back-powering, delay, loading, and common-ground requirements. No supported sampling rate, pin mux, target compatibility, USB throughput, or hardware decoder result is claimed yet.
