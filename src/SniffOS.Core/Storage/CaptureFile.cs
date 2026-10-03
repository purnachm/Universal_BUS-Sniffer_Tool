using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SniffOS.Core.Acquisition;
using SniffOS.Core.Protocol;

namespace SniffOS.Core.Storage;

public sealed record CaptureMetadata(
    string DeviceId,
    string FirmwareVersion,
    uint SampleRateHz,
    int ChannelCount,
    uint EnabledChannelMask,
    string[] ChannelNames,
    string TriggerDescription,
    ulong CaptureId,
    string CreatedUtc,
    string TimebaseDescription,
    int FormatVersion = CaptureFileFormat.Version);

public sealed record CaptureFile(CaptureMetadata Metadata, RawCapture Capture);

public static class CaptureFileFormat
{
    public const ushort Version = 1;
    public const int MaxMetadataBytes = 65_536;
    public const int MaxRecordBytes = 8 * 1024 * 1024;
    private static readonly byte[] Magic = "SNFC"u8.ToArray();

    public static async Task WriteAsync(string path, CaptureMetadata metadata, RawCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(capture);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteBytesAsync(stream, Magic, cancellationToken);
        await WriteUInt16Async(stream, Version, cancellationToken);
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata);
        if (json.Length > MaxMetadataBytes) throw new InvalidDataException("Capture metadata exceeds the format limit.");
        await WriteUInt32Async(stream, checked((uint)json.Length), cancellationToken);
        await WriteBytesAsync(stream, json, cancellationToken);
        await WriteUInt32Async(stream, Crc32.Compute(json, ReadOnlySpan<byte>.Empty), cancellationToken);

        foreach (var chunk in capture.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new byte[checked(12 + chunk.Samples.Length * 2)];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), chunk.FirstSampleIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), checked((uint)chunk.Samples.Length));
            for (var i = 0; i < chunk.Samples.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12 + 2 * i, 2), chunk.Samples[i]);
            await WriteRecordAsync(stream, 1, payload, cancellationToken);
        }

        foreach (var gap in capture.Gaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = Encoding.UTF8.GetBytes(gap.Reason);
            if (reason.Length > 4096) throw new InvalidDataException("Gap reason exceeds the format limit.");
            var payload = new byte[checked(17 + reason.Length)];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), gap.StartSampleIndex);
            payload[8] = gap.MissingSamples.HasValue ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(9, 8), gap.MissingSamples.GetValueOrDefault());
            reason.AsSpan().CopyTo(payload.AsSpan(17));
            await WriteRecordAsync(stream, 2, payload, cancellationToken);
        }
        await WriteRecordAsync(stream, 255, Array.Empty<byte>(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<CaptureFile> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var magic = await ReadExactlyAsync(stream, 4, cancellationToken);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not a SniffOS capture file.");
        var version = await ReadUInt16Async(stream, cancellationToken);
        if (version != Version) throw new InvalidDataException($"Unsupported capture format version {version}.");
        var metadataLength = await ReadUInt32Async(stream, cancellationToken);
        if (metadataLength > (uint)MaxMetadataBytes) throw new InvalidDataException("Capture metadata exceeds the format limit.");
        var metadataBytes = await ReadExactlyAsync(stream, checked((int)metadataLength), cancellationToken);
        var expectedMetadataCrc = await ReadUInt32Async(stream, cancellationToken);
        var actualMetadataCrc = Crc32.Compute(metadataBytes, ReadOnlySpan<byte>.Empty);
        if (expectedMetadataCrc != actualMetadataCrc) throw new InvalidDataException("Capture metadata integrity check failed.");
        var metadata = JsonSerializer.Deserialize<CaptureMetadata>(metadataBytes) ?? throw new InvalidDataException("Capture metadata is empty.");
        var capture = new RawCapture(metadata.SampleRateHz, metadata.ChannelCount, metadata.CaptureId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = (await ReadExactlyAsync(stream, 1, cancellationToken))[0];
            var length = await ReadUInt32Async(stream, cancellationToken);
            if (length > (uint)MaxRecordBytes) throw new InvalidDataException("Capture record exceeds the format limit.");
            var payload = await ReadExactlyAsync(stream, checked((int)length), cancellationToken);
            var expectedCrc = await ReadUInt32Async(stream, cancellationToken);
            if (expectedCrc != Crc32.Compute(payload, ReadOnlySpan<byte>.Empty))
                throw new InvalidDataException("Capture record integrity check failed.");
            if (type == 255)
            {
                if (payload.Length != 0) throw new InvalidDataException("End record has a payload.");
                capture.MarkComplete();
                break;
            }
            switch (type)
            {
                case 1:
                    if (payload.Length < 12) throw new InvalidDataException("Sample record is truncated.");
                    var first = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8));
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                    if (count == 0 || count > (uint)((MaxRecordBytes - 12) / 2) || payload.Length != 12 + (long)count * 2)
                        throw new InvalidDataException("Sample record length is invalid.");
                    var samples = new ushort[checked((int)count)];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12 + 2 * i, 2));
                    capture.AddChunk(new SampleChunk(first, samples));
                    break;
                case 2:
                    if (payload.Length < 17) throw new InvalidDataException("Gap record is truncated.");
                    var start = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8));
                    var known = payload[8] == 1;
                    if (payload[8] > 1) throw new InvalidDataException("Gap record known-length flag is invalid.");
                    var missing = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(9, 8));
                    var reason = Encoding.UTF8.GetString(payload, 17, payload.Length - 17);
                    capture.AddGap(new GapRecord(start, known ? missing : null, reason));
                    break;
                default:
                    throw new InvalidDataException($"Unknown capture record type {type}.");
            }
        }
        return new CaptureFile(metadata, capture);
    }

    private static async Task WriteRecordAsync(Stream stream, byte type, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxRecordBytes) throw new InvalidDataException("Capture record exceeds the format limit.");
        await WriteBytesAsync(stream, new[] { type }, cancellationToken);
        await WriteUInt32Async(stream, checked((uint)payload.Length), cancellationToken);
        await WriteBytesAsync(stream, payload, cancellationToken);
        await WriteUInt32Async(stream, Crc32.Compute(payload, ReadOnlySpan<byte>.Empty), cancellationToken);
    }

    private static async Task WriteUInt16Async(Stream stream, ushort value, CancellationToken token)
    {
        var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); await WriteBytesAsync(stream, bytes, token);
    }

    private static async Task WriteUInt32Async(Stream stream, uint value, CancellationToken token)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); await WriteBytesAsync(stream, bytes, token);
    }

    private static async Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken token) => await stream.WriteAsync(bytes.AsMemory(), token);

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
            if (n == 0) throw new EndOfStreamException("Capture file ended before a complete record was read.");
            read += n;
        }
        return buffer;
    }

    private static async Task<ushort> ReadUInt16Async(Stream stream, CancellationToken token)
        => BinaryPrimitives.ReadUInt16LittleEndian(await ReadExactlyAsync(stream, 2, token));

    private static async Task<uint> ReadUInt32Async(Stream stream, CancellationToken token)
        => BinaryPrimitives.ReadUInt32LittleEndian(await ReadExactlyAsync(stream, 4, token));
}
