namespace SniffOS.Core.Acquisition;

/// <summary>Bit extraction shared by decoders and waveform rendering.</summary>
public static class DigitalSample
{
    public static bool IsHigh(ushort value, int channel) => (value & (1u << channel)) != 0;
}
