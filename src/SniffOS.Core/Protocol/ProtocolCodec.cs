using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SniffOS.Core.Protocol;

public static class ProtocolCodec
{
    public static byte[] Encode(ProtocolPacket packet, int maxPayloadBytes = ProtocolConstants.DefaultMaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        var payload = packet.Payload ?? throw new ArgumentException("Payload cannot be null.", nameof(packet));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, maxPayloadBytes);

        var frame = new byte[ProtocolConstants.HeaderSize + payload.Length];
        frame[0] = ProtocolConstants.Magic0;
        frame[1] = ProtocolConstants.Magic1;
        frame[2] = ProtocolConstants.Version;
        frame[3] = ProtocolConstants.HeaderSize;
        frame[4] = (byte)packet.MessageType;
        frame[5] = (byte)packet.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6, 4), packet.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10, 4), packet.ConfigurationId);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(14, 8), packet.AcquisitionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(22, 4), checked((uint)payload.Length));
        payload.AsSpan().CopyTo(frame.AsSpan(ProtocolConstants.HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(26, 4),
            Crc32.Compute(frame.AsSpan(2, 24), payload));
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, int maxPayloadBytes, out ProtocolPacket? packet)
    {
        packet = null;
        if (maxPayloadBytes <= 0) return false;
        if (frame.Length < ProtocolConstants.HeaderSize || frame[0] != ProtocolConstants.Magic0 || frame[1] != ProtocolConstants.Magic1)
            return false;
        if (frame[2] != ProtocolConstants.Version || frame[3] != ProtocolConstants.HeaderSize)
            return false;
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(22, 4));
        if (payloadLength > (uint)maxPayloadBytes || frame.Length != ProtocolConstants.HeaderSize + (long)payloadLength)
            return false;
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(26, 4));
        var actual = Crc32.Compute(frame.Slice(2, 24), frame.Slice(ProtocolConstants.HeaderSize));
        if (expected != actual)
            return false;

        packet = new ProtocolPacket(
            (MessageType)frame[4],
            (PacketOptions)frame[5],
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(6, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(10, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(14, 8)),
            frame.Slice(ProtocolConstants.HeaderSize).ToArray());
        return true;
    }
}

public sealed class PacketStreamParser
{
    private readonly int _maxPayloadBytes;
    private readonly int _maxBufferedBytes;
    private readonly List<byte> _buffer = new();

    public PacketStreamParser(int maxPayloadBytes = ProtocolConstants.DefaultMaxPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        _maxPayloadBytes = maxPayloadBytes;
        _maxBufferedBytes = checked(maxPayloadBytes + ProtocolConstants.HeaderSize + 2);
    }

    public PacketParseResult Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0)
            _buffer.AddRange(bytes.ToArray());

        var packets = new List<ProtocolPacket>();
        var issues = new List<PacketParseIssue>();
        while (true)
        {
            if (!AlignToMagic(issues))
                break;
            if (_buffer.Count < ProtocolConstants.HeaderSize)
                break;

            if (_buffer[2] != ProtocolConstants.Version || _buffer[3] != ProtocolConstants.HeaderSize)
            {
                issues.Add(new(PacketParseIssueKind.InvalidHeader, "The protocol version or header length is invalid; resynchronizing.", 1));
                _buffer.RemoveAt(0);
                continue;
            }

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(CollectionsMarshal.AsSpan(_buffer).Slice(22, 4));
            if (payloadLength > (uint)_maxPayloadBytes)
            {
                issues.Add(new(PacketParseIssueKind.OversizedPayload, $"Payload length {payloadLength} exceeds the configured limit.", 1));
                _buffer.RemoveAt(0);
                continue;
            }

            var frameLength = checked(ProtocolConstants.HeaderSize + (int)payloadLength);
            if (_buffer.Count < frameLength)
                break;

            var frame = CollectionsMarshal.AsSpan(_buffer).Slice(0, frameLength);
            if (!ProtocolCodec.TryDecode(frame, _maxPayloadBytes, out var packet))
            {
                issues.Add(new(PacketParseIssueKind.IntegrityFailure, "Frame CRC or structure failed; one byte was discarded for resynchronization.", 1));
                _buffer.RemoveAt(0);
                continue;
            }

            packets.Add(packet!);
            _buffer.RemoveRange(0, frameLength);
        }

        if (_buffer.Count > _maxBufferedBytes)
        {
            var remove = _buffer.Count - _maxBufferedBytes;
            _buffer.RemoveRange(0, remove);
            issues.Add(new(PacketParseIssueKind.BufferLimitReached, "Parser buffer reached its bound; leading bytes were discarded.", remove));
        }
        return new PacketParseResult(packets, issues);
    }

    private bool AlignToMagic(List<PacketParseIssue> issues)
    {
        if (_buffer.Count < 2)
            return false;
        var index = -1;
        for (var i = 0; i < _buffer.Count - 1; i++)
        {
            if (_buffer[i] == ProtocolConstants.Magic0 && _buffer[i + 1] == ProtocolConstants.Magic1)
            {
                index = i;
                break;
            }
        }
        if (index == 0)
            return true;
        if (index > 0)
        {
            _buffer.RemoveRange(0, index);
            issues.Add(new(PacketParseIssueKind.DiscardedBytes, "Bytes before the next frame marker were discarded.", index));
            return true;
        }

        // Preserve a possible first marker byte across USB read boundaries.
        var keep = _buffer[^1] == ProtocolConstants.Magic0 ? 1 : 0;
        var discarded = _buffer.Count - keep;
        if (discarded > 0)
        {
            _buffer.RemoveRange(0, discarded);
            issues.Add(new(PacketParseIssueKind.DiscardedBytes, "Noise without a frame marker was discarded.", discarded));
        }
        return false;
    }
}

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
            crc = Update(crc, value);
        foreach (var value in second)
            crc = Update(crc, value);
        return ~crc;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) == 1 ? (crc >> 1) ^ Polynomial : crc >> 1;
        return crc;
    }
}
