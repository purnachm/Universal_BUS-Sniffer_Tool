# SniffOS host protocol v1 (proposed, simulator-tested)

This is a host-side protocol definition for the simulator and future firmware. It is not evidence that current Teensy firmware implements it.

## Frame

All integer fields are little-endian. A frame is:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 2 | Magic bytes `0x53 0x4E` (`SN`) |
| 2 | 1 | Protocol version, currently `1` |
| 3 | 1 | Header length, currently `30` |
| 4 | 1 | Message type |
| 5 | 1 | Flags |
| 6 | 4 | Transport sequence number, modulo 2^32 |
| 10 | 4 | Configuration identifier |
| 14 | 8 | Acquisition sample index or edge timestamp, modulo 2^64 |
| 22 | 4 | Payload length |
| 26 | 4 | CRC-32 over bytes 2..25 followed by payload |
| 30 | N | Payload, bounded by the negotiated host limit |

CRC uses the reflected CRC-32 polynomial `0xEDB88320`, initial `0xFFFFFFFF`, final inversion. A future firmware revision must retain this algorithm or increment the protocol version.

## Message types

`Hello`, `Capabilities`, `Configure`, `ConfigureAck`, `Arm`, `ArmAck`, `Start`, `Stop`, `CaptureData`, `Gap`, `Status`, `Error`, and `Heartbeat` are allocated. Payload schemas for capability/configuration/status are still under firmware design. `CaptureData` is currently simulator-defined as a `uint32` sample count followed by little-endian `uint16` sample words.

A host must reject payloads over its configured bound before allocating. A device must reject invalid channel masks, unsupported rates, zero captures, and configuration IDs it does not own.

## Loss and resynchronization

- Host transport sequence numbers are checked for continuity. A missing sequence creates a gap with unknown length unless a device `Gap` message gives a trusted count.
- Acquisition sample indices are checked separately. A forward index jump creates a known sample-count gap; a backwards index is an out-of-order gap.
- CRC failures discard only enough bytes to search for the next marker and emit an integrity issue. They are not silently ignored.
- Host arrival time is never used as acquisition time.
- Sequence and acquisition indexes wrap modulo their defined widths. Firmware and host acceptance tests must define rollover arithmetic before using rollover captures. The current assembler treats a raw `uint` increment literally and therefore rollover handling is **not yet accepted**.

## State and recovery

The intended command sequence is `Hello -> Capabilities -> Configure -> Arm -> Start`, followed by `CaptureData`/`Gap`/`Status`, then `Stop` or a finite-capture completion status. Disconnects must complete the reader with an error, preserve packets already assembled, and leave the UI in a visible fault/recovery state. The current simulator tests cancellation/disconnect behavior; hardware recovery remains deferred.

## Transport decision gate

USB CDC remains a candidate because Windows generally presents it as a COM endpoint without a custom kernel driver. No serial implementation is enabled or included until firmware exposes this exact protocol over USB CDC. Before making a sustained-rate claim, benchmark:

- exact Teensy firmware/toolchain and USB mode;
- Windows version and COM port configuration;
- packet size, framing, sample rate, channel count and capture duration;
- host CPU/storage and process priority;
- received packet count, sequence gaps, parser errors, device overruns, and cancellation/disconnect recovery.

USB signaling speed is not application throughput.
