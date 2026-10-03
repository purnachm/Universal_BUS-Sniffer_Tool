using SniffOS.Core.Acquisition;
using SniffOS.Core.Decoding;

namespace SniffOS.Tests;

public sealed class DecoderTests
{
    [Fact]
    public void Uart_DecodesGoldenByteAndFlagsNoError()
    {
        const int samplesPerBit = 10;
        var values = new List<ushort>(20);
        values.AddRange(Enumerable.Repeat((ushort)1, 10));
        values.AddRange(Enumerable.Repeat((ushort)0, samplesPerBit));
        const byte expected = 0xA5;
        for (var bit = 0; bit < 8; bit++) values.AddRange(Enumerable.Repeat((ushort)(((expected >> bit) & 1) == 1 ? 1 : 0), samplesPerBit));
        values.AddRange(Enumerable.Repeat((ushort)1, samplesPerBit * 3));
        var capture = new RawCapture(1_000, 1, 1);
        capture.AddChunk(new SampleChunk(0, values.ToArray()));
        var result = UartDecoder.Decode(capture, 0, new UartConfiguration(100));
        var item = Assert.Single(result);
        Assert.Equal(expected, item.Value);
        Assert.False(item.FramingError);
        Assert.False(item.ParityError);
    }

    [Fact]
    public void Spi_DecodesMsbFirstWordOnLeadingEdges()
    {
        var values = new List<ushort> { 0 }; // CS active low, clock idle low
        const byte expected = 0xA5;
        for (var bit = 7; bit >= 0; bit--)
        {
            var data = ((expected >> bit) & 1) != 0;
            values.Add((ushort)(data ? 2 : 0)); // clock low, MOSI on bit 1
            values.Add((ushort)((data ? 2 : 0) | 1)); // leading clock rising
            values.Add((ushort)(data ? 2 : 0)); // return to idle
        }
        var capture = new RawCapture(1_000, 2, 1);
        capture.AddChunk(new SampleChunk(0, values.ToArray()));
        var result = SpiDecoder.Decode(capture, new SpiConfiguration(0, 1, null, 2, SpiClockPolarity.IdleLow, SpiClockPhase.SampleLeading, BitOrder.MsbFirst, 8, true));
        var word = Assert.Single(result.Words);
        Assert.Equal((uint)expected, word.Mosi);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void I2c_RecognizesStartByteAckAndStop()
    {
        var values = new List<ushort> { 3, 1, 0 }; // idle, START, SCL low
        const byte expected = 0x52;
        for (var bit = 7; bit >= 0; bit--)
        {
            var sda = ((expected >> bit) & 1) != 0;
            values.Add((ushort)(sda ? 2 : 0));
            values.Add((ushort)((sda ? 2 : 0) | 1));
            values.Add((ushort)(sda ? 2 : 0));
        }
        values.Add(0); // ACK low while SCL low
        values.Add(1); // ACK rising: accepted
        values.Add(0); // clock low
        values.Add(0); // prepare STOP with SDA low
        values.Add(1); // SCL high
        values.Add(3); // SDA rises while SCL high: STOP
        var capture = new RawCapture(1_000, 2, 1);
        capture.AddChunk(new SampleChunk(0, values.ToArray()));
        var result = I2cDecoder.Decode(capture, 0, 1);
        Assert.Contains(result.Events, e => e.Kind == I2cEventKind.Start);
        var item = Assert.Single(result.Events.Where(e => e.Kind == I2cEventKind.Byte));
        Assert.Equal(expected, item.Data!.Value);
        Assert.True(item.Acknowledged!.Value);
        Assert.Contains(result.Events, e => e.Kind == I2cEventKind.Stop);
        Assert.Empty(result.Diagnostics);
    }
}
