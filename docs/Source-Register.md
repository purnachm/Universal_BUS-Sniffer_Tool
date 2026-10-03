# Source register

These are the authoritative sources to use at the relevant design gate. The current project uses only the general facts below; it does not infer the unknown 74LVC245 board part from a family label.

- PJRC, **Teensy 4.1 Development Board**: https://www.pjrc.com/store/teensy41.html
  - Board-level pin exposure, 3.3 V GPIO warning, USB modes, DMA availability, and the official schematic links are the starting point for board verification.
- NXP, **i.MX RT1060 product documentation**: https://www.nxp.com/products/i.MX-RT1060
  - Obtain the exact current `IMXRT1060RM` reference manual and the matching MIMXRT1062 data sheet. Use the manual for IOMUXC, GPIO, FlexIO, eDMA request routing, memory, cache, timers, and input timing. No pin mux or acquisition rate has been selected yet.
- Microsoft, **.NET releases and support**: https://learn.microsoft.com/dotnet/core/releases-and-support
  - This project targets .NET 8 LTS (`net8.0` / `net8.0-windows`) in the current support window. Re-check the support page before release and move to a newer LTS only as a documented migration.
- Microsoft, **WPF overview**: https://learn.microsoft.com/dotnet/desktop/wpf/overview/
  - Windows-only WPF target and rendering/application model.
- CommunityToolkit.Mvvm documentation: https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/
  - MVVM source generators and command/property patterns used by the application.
- Microsoft, **System.IO.Ports.SerialPort**: https://learn.microsoft.com/dotnet/api/system.io.ports.serialport
  - A future candidate only if SniffOS firmware exposes the defined protocol over USB CDC/COM. It is intentionally not referenced by the current simulator build.

## Missing authoritative component source

The exact manufacturer and ordering code for each user board marked `74LVC245` is not in the repository. Request the datasheet and board schematic/photographs before selecting VCC, DIR, `/OE`, target side, protection, or any final pin mapping. A generic family datasheet is insufficient to validate a board with unknown additions or wiring.
