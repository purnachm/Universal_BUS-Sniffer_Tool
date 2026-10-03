using System.Buffers.Binary;
using System.Globalization;
using SniffOS.Core.Protocol;

namespace SniffOS.Core.Acquisition;

public sealed record SampleChunk(ulong FirstSampleIndex, ushort[] Samples)
{
    public ulong EndExclusive => checked(FirstSampleIndex + (ulong)Samples.Length);
}

public sealed record GapRecord(ulong StartSampleIndex, ulong? MissingSamples, string Reason)
{
    public string DisplayLength => MissingSamples is ulong count ? count.ToString(CultureInfo.InvariantCulture) : "unknown length";
}

public sealed class RawCapture
{
    private readonly List<SampleChunk> _chunks = new();
    private readonly List<GapRecord> _gaps = new();

    public RawCapture(uint sampleRateHz, int channelCount, ulong captureId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (channelCount > 16) throw new ArgumentOutOfRangeException(nameof(channelCount));
        SampleRateHz = sampleRateHz;
        ChannelCount = channelCount;
        CaptureId = captureId;
    }

    public uint SampleRateHz { get; }
    public int ChannelCount { get; }
    public ulong CaptureId { get; }
    public IReadOnlyList<SampleChunk> Chunks => _chunks;
    public IReadOnlyList<GapRecord> Gaps => _gaps;
    public bool IsComplete { get; private set; }
    public bool HasLoss => _gaps.Count != 0;
    public ulong CapturedSampleCount => _chunks.Aggregate<SampleChunk, ulong>(0, (total, chunk) => checked(total + (ulong)chunk.Samples.Length));

    public void AddChunk(SampleChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Samples.Length == 0) throw new ArgumentException("A sample chunk cannot be empty.", nameof(chunk));
        if (_chunks.Count > 0 && chunk.FirstSampleIndex < _chunks[^1].EndExclusive)
            throw new InvalidOperationException("Sample chunks must be monotonic and non-overlapping.");
        _chunks.Add(chunk);
    }

    public void AddGap(GapRecord gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        _gaps.Add(gap);
    }

    public void MarkComplete() => IsComplete = true;
}

public static class SampleChunkCodec
{
    // CaptureData payload: uint32 sample count followed by little-endian uint16 channel words.
    public const int PayloadPrefixBytes = 4;

    public static byte[] Encode(IReadOnlyList<ushort> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var payload = new byte[checked(PayloadPrefixBytes + samples.Count * sizeof(ushort))];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)samples.Count));
        for (var i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(PayloadPrefixBytes + i * 2, 2), samples[i]);
        return payload;
    }

    public static ushort[] Decode(ReadOnlySpan<byte> payload, int maxSamples = 524_288)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSamples);
        if (payload.Length < PayloadPrefixBytes)
            throw new InvalidDataException("Capture data payload is truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ArgumentOutOfRangeException.ThrowIfZero(count);
        if (count > (uint)maxSamples)
            throw new InvalidDataException("Capture data sample count is outside the configured bound.");
        var expectedBytes = checked(PayloadPrefixBytes + (int)count * sizeof(ushort));
        if (payload.Length != expectedBytes)
            throw new InvalidDataException("Capture data payload length does not match its sample count.");
        var samples = new ushort[checked((int)count)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(PayloadPrefixBytes + i * 2, 2));
        return samples;
    }
}

public sealed class CaptureAssembler
{
    private readonly RawCapture _capture;
    private readonly uint _configurationId;
    private uint? _lastPacketSequence;
    private ulong _nextExpectedSample;

    public CaptureAssembler(RawCapture capture, uint configurationId)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _configurationId = configurationId;
    }

    public void Add(ProtocolPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.ConfigurationId != _configurationId)
        {
            _capture.AddGap(new GapRecord(packet.AcquisitionIndex, null, "Configuration identifier changed during capture."));
            return;
        }
        if (_lastPacketSequence is uint previous && packet.Sequence != previous + 1)
            _capture.AddGap(new GapRecord(packet.AcquisitionIndex, null, "Transport packet sequence gap; missing length is unknown."));
        _lastPacketSequence = packet.Sequence;

        if (packet.MessageType == MessageType.Gap)
        {
            _capture.AddGap(new GapRecord(packet.AcquisitionIndex, null, "Device reported an acquisition gap."));
            return;
        }
        if (packet.MessageType != MessageType.CaptureData)
            return;

        var samples = SampleChunkCodec.Decode(packet.Payload);
        if (packet.AcquisitionIndex > _nextExpectedSample)
            _capture.AddGap(new GapRecord(_nextExpectedSample, packet.AcquisitionIndex - _nextExpectedSample, "Device sample-index gap."));
        else if (packet.AcquisitionIndex < _nextExpectedSample)
        {
            _capture.AddGap(new GapRecord(packet.AcquisitionIndex, null, "Out-of-order acquisition sample index; packet was not appended."));
            return;
        }
        _capture.AddChunk(new SampleChunk(packet.AcquisitionIndex, samples));
        _nextExpectedSample = checked(packet.AcquisitionIndex + (ulong)samples.Length);
    }

    public void Complete() => _capture.MarkComplete();
}
