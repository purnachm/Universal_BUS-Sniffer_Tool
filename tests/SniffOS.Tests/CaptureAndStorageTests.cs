using System.Globalization;
using SniffOS.Core.Acquisition;
using SniffOS.Core.Protocol;
using SniffOS.Core.Storage;

namespace SniffOS.Tests;

public sealed class CaptureAndStorageTests
{
    private static readonly string[] ChannelNames = ["SCL", "SDA"];

    [Fact]
    public void AssemblerRecordsSequenceAndSampleIndexGaps()
    {
        var capture = new RawCapture(1_000_000, 16, 42);
        var assembler = new CaptureAssembler(capture, 7);
        assembler.Add(new ProtocolPacket(MessageType.CaptureData, PacketOptions.None, 0, 7, 0, SampleChunkCodec.Encode(new ushort[] { 1, 2 })));
        assembler.Add(new ProtocolPacket(MessageType.CaptureData, PacketOptions.None, 2, 7, 5, SampleChunkCodec.Encode(new ushort[] { 3, 4 })));
        Assert.Equal(2, capture.Chunks.Count);
        Assert.Equal(2, capture.Gaps.Count);
        Assert.Contains(capture.Gaps, gap => gap.MissingSamples == 3);
        Assert.Contains(capture.Gaps, gap => gap.MissingSamples is null);
    }

    [Fact]
    public async Task CaptureFileRoundTripsChunksAndExplicitGaps()
    {
        var capture = new RawCapture(10_000, 2, 99);
        capture.AddChunk(new SampleChunk(0, new ushort[] { 0, 1, 3, 2 }));
        capture.AddGap(new GapRecord(4, null, "USB disconnect; missing length unknown"));
        capture.AddChunk(new SampleChunk(20, new ushort[] { 0xFFFF }));
        capture.MarkComplete();
        var path = Path.Combine(Path.GetTempPath(), $"sniffos-{Guid.NewGuid():N}.sniffcap");
        try
        {
            var metadata = new CaptureMetadata("SIM", "test", 10_000, 2, 3, ChannelNames, "Immediate", 99, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), "test clock");
            await CaptureFileFormat.WriteAsync(path, metadata, capture);
            var loaded = await CaptureFileFormat.ReadAsync(path);
            Assert.Equal(metadata.DeviceId, loaded.Metadata.DeviceId);
            Assert.True(loaded.Capture.IsComplete);
            Assert.Equal(capture.Chunks.Count, loaded.Capture.Chunks.Count);
            Assert.Equal(capture.Gaps.Single().Reason, loaded.Capture.Gaps.Single().Reason);
            Assert.Null(loaded.Capture.Gaps.Single().MissingSamples);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CaptureFileRejectsTruncatedRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sniffos-truncated-{Guid.NewGuid():N}.sniffcap");
        await File.WriteAllBytesAsync(path, "SNFC"u8.ToArray());
        try { await Assert.ThrowsAnyAsync<Exception>(() => CaptureFileFormat.ReadAsync(path)); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
