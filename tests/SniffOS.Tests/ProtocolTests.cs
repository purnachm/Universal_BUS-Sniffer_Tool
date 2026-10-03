using SniffOS.Core.Protocol;

namespace SniffOS.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void CodecRoundTripsExplicitLittleEndianFrame()
    {
        var original = new ProtocolPacket(MessageType.CaptureData, PacketOptions.First, 0x12345678, 0xAABBCCDD, 0x0102030405060708, new byte[] { 1, 2, 3, 0xFF });
        var encoded = ProtocolCodec.Encode(original);
        Assert.True(ProtocolCodec.TryDecode(encoded, ProtocolConstants.DefaultMaxPayloadBytes, out var decoded));
        Assert.Equal(original.MessageType, decoded!.MessageType);
        Assert.Equal(original.Flags, decoded.Flags);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.ConfigurationId, decoded.ConfigurationId);
        Assert.Equal(original.AcquisitionIndex, decoded.AcquisitionIndex);
        Assert.Equal(original.Payload, decoded.Payload);
    }

    [Fact]
    public void ParserHandlesFragmentationAndResynchronizesAfterCorruptFrame()
    {
        var good = ProtocolCodec.Encode(new ProtocolPacket(MessageType.Status, PacketOptions.None, 2, 9, 10, new byte[] { 7, 8 }));
        var bad = ProtocolCodec.Encode(new ProtocolPacket(MessageType.Status, PacketOptions.None, 1, 9, 8, new byte[] { 4, 5 }));
        bad[^1] ^= 0x80;
        var parser = new PacketStreamParser();
        var noise = parser.Feed(new byte[] { 0x00, 0x55 });
        var stream = bad.Concat(good).ToArray();
        var first = parser.Feed(stream.Take(13).ToArray());
        var second = parser.Feed(stream.Skip(13).ToArray());
        Assert.Empty(noise.Packets);
        Assert.Empty(first.Packets);
        Assert.Single(second.Packets);
        Assert.Equal((uint)2, second.Packets[0].Sequence);
        Assert.Contains(second.Issues, issue => issue.Kind == PacketParseIssueKind.IntegrityFailure);
        Assert.Contains(noise.Issues, issue => issue.Kind == PacketParseIssueKind.DiscardedBytes);
    }

    [Fact]
    public void ParserReportsOversizedPayloadWithoutAllocatingIt()
    {
        var parser = new PacketStreamParser(maxPayloadBytes: 8);
        var bytes = new byte[] { ProtocolConstants.Magic0, ProtocolConstants.Magic1, ProtocolConstants.Version, ProtocolConstants.HeaderSize, (byte)MessageType.Status, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0 };
        var result = parser.Feed(bytes);
        Assert.Empty(result.Packets);
        Assert.Contains(result.Issues, issue => issue.Kind == PacketParseIssueKind.OversizedPayload);
    }
}
