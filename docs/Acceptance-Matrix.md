# Milestone acceptance matrix

| Milestone | Exit condition | Current status |
|---|---|---|
| Requirements and feasibility | Scope, unresolved facts, safety limits, and bandwidth estimates documented | **Documentation baseline present; user hardware answers still required.** |
| Hardware front end | Exact boards/datasheets, verified wiring/startup, voltage and bench checks | **Not started; blocked on board identity/photos/schematics and target rates.** |
| Transport prototype | Protocol v1 and measured transport tests pass | **Protocol/parser simulator tests present; no USB transport selected or measured.** |
| Acquisition MVP | Raw capture, indices/timestamps, explicit losses validated | **Simulator finite capture present; Teensy acquisition not implemented or validated.** |
| Windows MVP | Connect/configure/capture/display/save-load against simulator and hardware | **Simulator WPF path implemented; Windows build and acceptance run require a Windows/.NET environment. Hardware path deferred; disk-backed indexed long-capture store is also deferred.** |
| Protocol decoders | UART/SPI/I2C golden tests and hardware comparisons pass | **Deterministic golden tests present; hardware comparison not done.** |
| Reliability/performance | Stress, disconnect, overflow, recovery, benchmarks pass | **Tests are scaffolding for simulator behavior; no hardware benchmark.** |
| Release preparation | Reproducible builds, installer, license, limitations | **Deferred.** |

## Current acceptance checks

On a Windows machine with the .NET 8 SDK:

```powershell
dotnet restore SniffOS.sln
dotnet test SniffOS.sln --configuration Release
dotnet build src\SniffOS.App\SniffOS.App.csproj --configuration Release
```

Run the GUI, click **Connect**, choose a simulator-advertised rate, and run a finite capture. The waveform must show 16 labeled channels without one WPF element per sample; stop must cancel; save/load must preserve explicit gap records; decoder errors must be visible in the event table/status line. These are simulator acceptance checks, not hardware support.

The repository snapshot was created in an environment without `dotnet` installed, so those commands have not been executed here. This is intentionally reported rather than called passing.
