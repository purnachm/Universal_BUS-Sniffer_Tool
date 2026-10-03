using SniffOS.Core.Acquisition;

namespace SniffOS.Core.Decoding;

public enum UartParity { None, Even, Odd }
public enum UartStopBits { One, Two }
public enum BitOrder { LsbFirst, MsbFirst }
public enum SpiClockPolarity { IdleLow, IdleHigh }
public enum SpiClockPhase { SampleLeading, SampleTrailing }

public sealed record UartConfiguration(uint BaudRate, int DataBits = 8, UartParity Parity = UartParity.None, UartStopBits StopBits = UartStopBits.One, bool Inverted = false)
{
    public void Validate(uint sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfZero(BaudRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(DataBits, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DataBits, 8);
        ArgumentOutOfRangeException.ThrowIfZero(sampleRateHz);
        if (sampleRateHz < BaudRate * 2) throw new ArgumentException("At least two samples per UART bit are required for this decoder.", nameof(sampleRateHz));
    }
}

public sealed record UartEvent(ulong SampleIndex, byte Value, bool ParityError, bool FramingError, string? Diagnostic);

public static class UartDecoder
{
    public static IReadOnlyList<UartEvent> Decode(RawCapture capture, int channel, UartConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 15);
        configuration.Validate(capture.SampleRateHz);
        var events = new List<UartEvent>();
        var samplesPerBit = (double)capture.SampleRateHz / configuration.BaudRate;
        foreach (var chunk in capture.Chunks)
        {
            // A chunk is a decode boundary. This deliberately prevents a decoder from treating a
            // missing chunk as a valid UART transition.
            var values = chunk.Samples;
            for (var offset = 1; offset < values.Length; offset++)
            {
                var previous = Logical(values[offset - 1], channel, configuration.Inverted);
                var current = Logical(values[offset], channel, configuration.Inverted);
                if (previous || !current) continue; // logical falling edge: start bit
                var frameSamples = (int)Math.Ceiling((1.0 + configuration.DataBits + (configuration.Parity == UartParity.None ? 0 : 1) + (configuration.StopBits == UartStopBits.Two ? 2 : 1)) * samplesPerBit);
                if (offset + frameSamples >= values.Length) break;

                var start = chunk.FirstSampleIndex + (ulong)offset;
                var value = 0;
                var parityOnes = 0;
                for (var bit = 0; bit < configuration.DataBits; bit++)
                {
                    var sampleOffset = SampleAt(offset, samplesPerBit, 1.5 + bit);
                    var bitValue = Logical(values[sampleOffset], channel, configuration.Inverted);
                    if (bitValue) parityOnes++;
                    if (bitValue) value |= 1 << bit;
                }
                var parityError = false;
                var parityOffset = 1.5 + configuration.DataBits;
                if (configuration.Parity != UartParity.None)
                {
                    var parityBit = Logical(values[SampleAt(offset, samplesPerBit, parityOffset)], channel, configuration.Inverted);
                    var expectedOne = configuration.Parity == UartParity.Even ? (parityOnes % 2 == 1) : (parityOnes % 2 == 0);
                    parityError = parityBit != expectedOne;
                    parityOffset += 1;
                }
                var stop1 = Logical(values[SampleAt(offset, samplesPerBit, parityOffset)], channel, configuration.Inverted);
                var framingError = !stop1;
                if (configuration.StopBits == UartStopBits.Two)
                    framingError |= !Logical(values[SampleAt(offset, samplesPerBit, parityOffset + 1)], channel, configuration.Inverted);
                events.Add(new UartEvent(start, (byte)value, parityError, framingError, parityError ? "UART parity error" : framingError ? "UART stop-bit error" : null));
                offset += Math.Max(1, SampleAt(offset, samplesPerBit, parityOffset + (configuration.StopBits == UartStopBits.Two ? 1 : 0))) - 1;
            }
        }
        return events;
    }

    private static int SampleAt(int startOffset, double samplesPerBit, double bitCenter)
        => checked(startOffset + (int)Math.Round(bitCenter * samplesPerBit, MidpointRounding.AwayFromZero));

    private static bool Logical(ushort sample, int channel, bool inverted)
        => DigitalSample.IsHigh(sample, channel) ^ inverted;
}

public sealed record SpiConfiguration(
    int ClockChannel,
    int MosiChannel,
    int? MisoChannel,
    int? ChipSelectChannel,
    SpiClockPolarity ClockPolarity = SpiClockPolarity.IdleLow,
    SpiClockPhase ClockPhase = SpiClockPhase.SampleLeading,
    BitOrder BitOrder = BitOrder.MsbFirst,
    int WordSize = 8,
    bool ChipSelectActiveLow = true);

public sealed record SpiWordEvent(ulong SampleIndex, uint? Mosi, uint? Miso, int BitsCaptured, bool ChipSelectActive);
public sealed record SpiDecodeResult(IReadOnlyList<SpiWordEvent> Words, IReadOnlyList<string> Diagnostics);

public static class SpiDecoder
{
    public static SpiDecodeResult Decode(RawCapture capture, SpiConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Validate(configuration);
        var words = new List<SpiWordEvent>();
        var diagnostics = new List<string>();
        foreach (var chunk in capture.Chunks)
        {
            uint mosi = 0, miso = 0;
            var bits = 0;
            var previousClock = DigitalSample.IsHigh(chunk.Samples[0], configuration.ClockChannel);
            var selected = configuration.ChipSelectChannel is null || IsChipSelected(chunk.Samples[0], configuration);
            for (var i = 1; i < chunk.Samples.Length; i++)
            {
                var sample = chunk.Samples[i];
                var clock = DigitalSample.IsHigh(sample, configuration.ClockChannel);
                var wasSelected = selected;
                selected = configuration.ChipSelectChannel is null || IsChipSelected(sample, configuration);
                if (!selected)
                {
                    if (bits != 0) diagnostics.Add($"SPI transaction ended at sample {chunk.FirstSampleIndex + (ulong)i} with an incomplete word ({bits} bits).");
                    bits = 0; mosi = 0; miso = 0;
                }
                var leading = configuration.ClockPolarity == SpiClockPolarity.IdleLow ? !previousClock && clock : previousClock && !clock;
                var trailing = configuration.ClockPolarity == SpiClockPolarity.IdleLow ? previousClock && !clock : !previousClock && clock;
                var sampleEdge = configuration.ClockPhase == SpiClockPhase.SampleLeading ? leading : trailing;
                if (wasSelected && selected && sampleEdge)
                {
                    mosi = Shift(mosi, DigitalSample.IsHigh(sample, configuration.MosiChannel), bits, configuration);
                    if (configuration.MisoChannel is int misoChannel)
                        miso = Shift(miso, DigitalSample.IsHigh(sample, misoChannel), bits, configuration);
                    bits++;
                    if (bits == configuration.WordSize)
                    {
                        words.Add(new SpiWordEvent(chunk.FirstSampleIndex + (ulong)i, mosi, configuration.MisoChannel is null ? null : miso, bits, selected));
                        bits = 0; mosi = 0; miso = 0;
                    }
                }
                previousClock = clock;
            }
            if (bits != 0) diagnostics.Add($"Capture chunk ended at sample {chunk.EndExclusive} with an incomplete SPI word ({bits} bits).");
        }
        return new SpiDecodeResult(words, diagnostics);
    }

    private static uint Shift(uint value, bool bit, int position, SpiConfiguration configuration)
    {
        if (configuration.BitOrder == BitOrder.MsbFirst)
            return (value << 1) | (bit ? 1u : 0u);
        return value | ((bit ? 1u : 0u) << position);
    }

    private static bool IsChipSelected(ushort sample, SpiConfiguration configuration)
    {
        var high = DigitalSample.IsHigh(sample, configuration.ChipSelectChannel!.Value);
        return configuration.ChipSelectActiveLow ? !high : high;
    }

    private static void Validate(SpiConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var channel in new[] { configuration.ClockChannel, configuration.MosiChannel, configuration.MisoChannel ?? -1, configuration.ChipSelectChannel ?? -1 })
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(channel, -1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 15);
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.WordSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.WordSize, 32);
    }
}

public enum I2cEventKind { Start, RepeatedStart, Stop, Byte }
public sealed record I2cEvent(ulong SampleIndex, I2cEventKind Kind, byte? Data = null, bool? Acknowledged = null);
public sealed record I2cDecodeResult(IReadOnlyList<I2cEvent> Events, IReadOnlyList<string> Diagnostics);

public static class I2cDecoder
{
    public static I2cDecodeResult Decode(RawCapture capture, int sclChannel, int sdaChannel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sclChannel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sclChannel, 15);
        ArgumentOutOfRangeException.ThrowIfNegative(sdaChannel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sdaChannel, 15);
        var events = new List<I2cEvent>();
        var diagnostics = new List<string>();
        foreach (var chunk in capture.Chunks)
        {
            if (chunk.Samples.Length < 2) continue;
            var active = false;
            var previousScl = DigitalSample.IsHigh(chunk.Samples[0], sclChannel);
            var previousSda = DigitalSample.IsHigh(chunk.Samples[0], sdaChannel);
            var bitCount = 0;
            var currentByte = 0;
            var awaitingAck = false;
            for (var i = 1; i < chunk.Samples.Length; i++)
            {
                var sample = chunk.Samples[i];
                var scl = DigitalSample.IsHigh(sample, sclChannel);
                var sda = DigitalSample.IsHigh(sample, sdaChannel);
                var index = chunk.FirstSampleIndex + (ulong)i;
                if (previousScl && previousSda && !sda)
                {
                    events.Add(new I2cEvent(index, active ? I2cEventKind.RepeatedStart : I2cEventKind.Start));
                    active = true; bitCount = 0; currentByte = 0; awaitingAck = false;
                }
                else if (previousScl && !previousSda && sda)
                {
                    events.Add(new I2cEvent(index, I2cEventKind.Stop));
                    active = false; bitCount = 0; currentByte = 0; awaitingAck = false;
                }
                if (active && !previousScl && scl)
                {
                    if (awaitingAck)
                    {
                        events.Add(new I2cEvent(index, I2cEventKind.Byte, (byte)currentByte, !sda));
                        awaitingAck = false; bitCount = 0; currentByte = 0;
                    }
                    else
                    {
                        currentByte = (currentByte << 1) | (sda ? 1 : 0);
                        bitCount++;
                        if (bitCount == 8) awaitingAck = true;
                    }
                }
                previousScl = scl;
                previousSda = sda;
            }
            if (active && (bitCount != 0 || awaitingAck))
                diagnostics.Add($"I2C capture chunk ended at sample {chunk.EndExclusive} in an incomplete byte.");
        }
        return new I2cDecodeResult(events, diagnostics);
    }
}
