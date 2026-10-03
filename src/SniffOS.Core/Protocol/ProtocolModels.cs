namespace SniffOS.Core.Protocol;

public static class ProtocolConstants
{
    public const byte Magic0 = 0x53; // S
    public const byte Magic1 = 0x4E; // N
    public const byte Version = 1;
    public const int HeaderSize = 30;
    public const int DefaultMaxPayloadBytes = 1_048_576;
}

public enum MessageType : byte
{
    Hello = 1,
    Capabilities = 2,
    Configure = 3,
    ConfigureAck = 4,
    Arm = 5,
    ArmAck = 6,
    Start = 7,
    Stop = 8,
    CaptureData = 9,
    Gap = 10,
    Status = 11,
    Error = 12,
    Heartbeat = 13,
}

[Flags]
public enum PacketOptions : byte
{
    None = 0,
    First = 1,
    Last = 2,
    Acknowledgement = 4,
    Error = 8,
}

public sealed record ProtocolPacket(
    MessageType MessageType,
    PacketOptions Flags,
    uint Sequence,
    uint ConfigurationId,
    ulong AcquisitionIndex,
    byte[] Payload);

public enum PacketParseIssueKind
{
    DiscardedBytes,
    InvalidHeader,
    UnsupportedVersion,
    OversizedPayload,
    IntegrityFailure,
    BufferLimitReached,
}

public sealed record PacketParseIssue(PacketParseIssueKind Kind, string Description, int AffectedBytes);

public sealed record PacketParseResult(
    IReadOnlyList<ProtocolPacket> Packets,
    IReadOnlyList<PacketParseIssue> Issues);

public sealed record DeviceCapabilities(
    string DeviceId,
    string FirmwareVersion,
    int ChannelCount,
    IReadOnlyList<uint> SupportedSampleRates,
    uint MaxCaptureSamples,
    bool SupportsEdgeTimestamps,
    bool SupportsUsbCdc,
    string TimebaseDescription);

public sealed record CaptureConfiguration(
    int ChannelCount,
    uint SampleRateHz,
    uint SampleCount,
    uint EnabledChannelMask,
    TriggerMode Trigger,
    int TriggerChannel,
    bool TriggerRising,
    string ConfigurationName = "Default")
{
    public void Validate(DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChannelCount);
        if (ChannelCount > 16 || ChannelCount > capabilities.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(capabilities), $"ChannelCount {ChannelCount} is not supported by the device.");

        ArgumentOutOfRangeException.ThrowIfZero(SampleRateHz);
        if (!capabilities.SupportedSampleRates.Contains(SampleRateHz))
            throw new ArgumentException($"Sample rate {SampleRateHz} Hz is not advertised by the device.", nameof(capabilities));

        ArgumentOutOfRangeException.ThrowIfZero(SampleCount);
        if (SampleCount > capabilities.MaxCaptureSamples)
            throw new ArgumentOutOfRangeException(nameof(capabilities), $"SampleCount {SampleCount} exceeds the device limit.");

        ArgumentOutOfRangeException.ThrowIfZero(EnabledChannelMask);
        if ((EnabledChannelMask & ~((1u << ChannelCount) - 1u)) != 0)
            throw new ArgumentException("The enabled channel mask is inconsistent with the channel count.", nameof(capabilities));

        if (Trigger != TriggerMode.Immediate && (TriggerChannel < 0 || TriggerChannel >= ChannelCount))
            throw new ArgumentOutOfRangeException(nameof(capabilities), $"TriggerChannel {TriggerChannel} is outside the configured channel range.");
    }
}

public enum TriggerMode
{
    Immediate,
    RisingEdge,
    FallingEdge,
}

public enum DeviceState
{
    Disconnected,
    Connecting,
    Idle,
    Configured,
    Armed,
    Capturing,
    Complete,
    Faulted,
}
