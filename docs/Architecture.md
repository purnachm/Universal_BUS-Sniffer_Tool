# SniffOS architecture

## Status vocabulary

- **Verified** means supported by repository tests or an identified authoritative source.
- **Assumption** means supplied by the project request but not bench-verified.
- **Proposed** means a design under review.
- **Measured** is reserved for a recorded test condition and result. There are no hardware measurements in this checkout.
- **Deferred** is not implemented and must not be described as working.

## Repository layout

```text
src/SniffOS.Core/
  Acquisition/  raw sample chunks, explicit gap records, packet-to-capture assembly
  Decoding/     independently testable UART, SPI, and I2C decoders
  Protocol/     versioned packet model, CRC framing, bounded stream parser
  Storage/      versioned chunked .sniffcap reader/writer
  Transport/    transport interface, bounded device client, deterministic simulator
  Waveform/     viewport-culling min/max waveform renderer
src/SniffOS.App/
  App.xaml      WPF resources and simulator composition root
  MainWindow    XAML shell
  ViewModels/   CommunityToolkit.Mvvm state/commands, dialogs, capture lifecycle
  Views/        custom waveform FrameworkElement
  SniffOS.App.csproj (net8.0-windows)
tests/SniffOS.Tests/
  protocol, gap/storage, decoder, simulator cancellation/state tests
```

## Capture data flow

1. The simulator (or future firmware-backed device) produces protocol frames.
2. `PacketStreamParser` consumes arbitrary USB read fragments. It bounds payloads and records noise, malformed headers, CRC failures, and buffer limits.
3. `CaptureAssembler` checks transport sequence, configuration identifier, and acquisition sample index. It records a `GapRecord` on any discontinuity. It never fabricates a loss count for a transport gap.
4. `RawCapture` retains chunks and gap records. Decoders operate on chunks independently and do not bridge a chunk boundary.
5. `CaptureFileFormat` writes metadata, sample chunks, gaps, CRCs, and an end record. Reader bounds every metadata/record allocation and rejects truncation or integrity failure. The current reader materializes a `RawCapture`; a disk-backed indexed store for truly multi-gigabyte captures is explicitly deferred.
6. `WaveformRenderer` culls to the viewport and emits at most one envelope column per pixel. WPF retains one custom surface, not one element per sample.

The simulator's bounded queue uses wait-on-full semantics. This models a finite buffered capture and makes test behavior deterministic; it is not evidence of a real Teensy's sustained USB rate. Firmware must sample independently of host transport and report acquisition overruns.

## UI state transitions

The GUI starts with a simulator object in `Disconnected`. Connect -> `Idle`; start validates advertised capabilities -> `Configured` -> `Armed` -> `Capturing`; successful finite capture -> `Complete`; cancellation does not promote a partial capture to complete; failures -> `Faulted`; disconnect is explicit. Hardware connection/recovery is not yet accepted because no firmware endpoint exists.

## Acquisition decision gate

No firmware pin map or sample rate is selected in this repository. The gate requires:

- i.MX RT1062 reference-manual references for GPIO/FlexIO/timer/DMA request routes.
- Exact Teensy 4.1 pin exposure and alternate-function mapping.
- DMA-visible memory region, alignment, cache maintenance, and buffer ownership rules.
- A coherent 16-bit sample-word layout or an explicit inter-channel skew model for split ports.
- Separate finite-capture and sustained-stream behavior.
- Independent source/reference-instrument timing and loss measurements.

## Threat model boundary

USB bytes and `.sniffcap` files are untrusted input. Parser/file code bounds lengths, validates version/magic, verifies CRCs, rejects unknown record types, and avoids allocations from unchecked counts. The UI must treat decoder text and metadata as data; it does not execute files or load plugins. Production distribution additionally requires signed installer and authenticated firmware update design, which are **deferred**.
