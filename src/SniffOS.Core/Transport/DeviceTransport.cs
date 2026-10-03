using System.Threading.Channels;
using SniffOS.Core.Acquisition;
using SniffOS.Core.Protocol;

namespace SniffOS.Core.Transport;

public interface IDeviceTransport : IAsyncDisposable
{
    string DisplayName { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default);
}

/// <summary>
/// The hardware transport is intentionally only an interface until a specific firmware USB mode,
/// protocol endpoint, and measured Windows transport have been selected. This prevents a COM port
/// from being mistaken for a compatible SniffOS device.
/// </summary>
public sealed class DeviceClient : IAsyncDisposable
{
    private readonly IDeviceTransport _transport;
    private readonly PacketStreamParser _parser;
    private readonly Channel<ProtocolPacket> _packets = Channel.CreateBounded<ProtocolPacket>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true,
    });
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private uint _sequence;

    public DeviceClient(IDeviceTransport transport, int maxPayloadBytes = ProtocolConstants.DefaultMaxPayloadBytes)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _parser = new PacketStreamParser(maxPayloadBytes);
    }

    public event Action<PacketParseIssue>? ParseIssue;
    public bool IsConnected => _transport.IsConnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _transport.ConnectAsync(cancellationToken);
        _readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = ReadLoopAsync(_readerCancellation.Token);
        await SendAsync(MessageType.Hello, Array.Empty<byte>(), cancellationToken: cancellationToken);
    }

    public async Task SendAsync(MessageType type, byte[] payload, uint configurationId = 0, ulong acquisitionIndex = 0, PacketOptions flags = PacketOptions.None, CancellationToken cancellationToken = default)
    {
        var packet = new ProtocolPacket(type, flags, checked(++_sequence), configurationId, acquisitionIndex, payload);
        await _transport.WriteAsync(ProtocolCodec.Encode(packet), cancellationToken);
    }

    public IAsyncEnumerable<ProtocolPacket> ReadPacketsAsync(CancellationToken cancellationToken = default)
        => _packets.Reader.ReadAllAsync(cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_readerCancellation is not null)
        {
            await _readerCancellation.CancelAsync();
            if (_readerTask is not null)
            {
                try { await _readerTask; } catch (OperationCanceledException) { }
            }
            _readerCancellation.Dispose();
            _readerCancellation = null;
            _readerTask = null;
        }
        await _transport.DisconnectAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _transport.DisposeAsync();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await _transport.ReadAsync(buffer, cancellationToken);
                if (count == 0) throw new IOException("The device transport ended unexpectedly.");
                var result = _parser.Feed(buffer.AsSpan(0, count));
                foreach (var issue in result.Issues) ParseIssue?.Invoke(issue);
                foreach (var packet in result.Packets)
                    await _packets.Writer.WriteAsync(packet, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _packets.Writer.TryComplete(ex);
        }
    }
}

public sealed record CaptureProgress(ulong ReceivedSamples, ulong ExpectedSamples, DeviceState State, string Message);

public interface ICaptureDevice : IAsyncDisposable
{
    string DisplayName { get; }
    DeviceState State { get; }
    DeviceCapabilities Capabilities { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<RawCapture> CaptureAsync(CaptureConfiguration configuration, IProgress<CaptureProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic, bounded simulator used by the GUI and CI. It exercises the same packet model,
/// sample payload codec, and capture assembler as a transport-backed implementation, but is not a hardware benchmark.
/// </summary>
public sealed class SimulatedCaptureDevice : ICaptureDevice
{
    private readonly TimeSpan _interChunkDelay;
    private DeviceState _state = DeviceState.Disconnected;
    private uint _configurationId;

    public SimulatedCaptureDevice(TimeSpan? interChunkDelay = null)
    {
        _interChunkDelay = interChunkDelay ?? TimeSpan.FromMilliseconds(2);
        Capabilities = new DeviceCapabilities(
            "SIM-TEENSY-4.1", "simulator-1.0", 16,
            new uint[] { 1_000, 10_000, 100_000, 1_000_000 },
            2_000_000, false, true, "simulated 48 MHz acquisition clock; not a hardware result");
    }

    public string DisplayName => "Simulated Teensy 4.1 (deterministic)";
    public DeviceState State => _state;
    public DeviceCapabilities Capabilities { get; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = DeviceState.Idle;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _state = DeviceState.Disconnected;
        return Task.CompletedTask;
    }

    public async Task<RawCapture> CaptureAsync(CaptureConfiguration configuration, IProgress<CaptureProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_state == DeviceState.Disconnected) throw new InvalidOperationException("Connect the device before capture.");
        configuration.Validate(Capabilities);
        _state = DeviceState.Configured;
        var capture = new RawCapture(configuration.SampleRateHz, configuration.ChannelCount, 1);
        _configurationId = unchecked(_configurationId + 1);
        var assembler = new CaptureAssembler(capture, _configurationId);
        _state = DeviceState.Armed;
        const int samplesPerPacket = 256;
        var channelMask = (ushort)configuration.EnabledChannelMask;
        var produced = 0u;
        uint sequence = 0;
        _state = DeviceState.Capturing;

        // A bounded channel models backpressure: producer waits rather than dropping a packet.
        var queue = Channel.CreateBounded<ProtocolPacket>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                while (produced < configuration.SampleCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min((uint)samplesPerPacket, configuration.SampleCount - produced);
                    var samples = new ushort[count];
                    for (var i = 0; i < count; i++)
                    {
                        var index = produced + (uint)i;
                        ushort value = 0;
                        for (var channel = 0; channel < configuration.ChannelCount; channel++)
                            if ((channelMask & (1u << channel)) != 0 && (((index + (uint)channel * 3) / (uint)(channel + 2)) & 1) != 0)
                                value |= (ushort)(1u << channel);
                        samples[i] = value;
                    }
                    var packet = new ProtocolPacket(MessageType.CaptureData, PacketOptions.None, sequence++, _configurationId, produced, SampleChunkCodec.Encode(samples));
                    await queue.Writer.WriteAsync(packet, cancellationToken);
                    produced += (uint)count;
                    if (_interChunkDelay > TimeSpan.Zero) await Task.Delay(_interChunkDelay, cancellationToken);
                }
                queue.Writer.TryComplete();
            }
            catch (Exception ex) { queue.Writer.TryComplete(ex); }
        }, CancellationToken.None);

        try
        {
            await foreach (var packet in queue.Reader.ReadAllAsync(cancellationToken))
            {
                assembler.Add(packet);
                progress?.Report(new CaptureProgress(capture.CapturedSampleCount, configuration.SampleCount, _state, "Receiving deterministic sample chunks"));
            }
            await producer;
            assembler.Complete();
            _state = DeviceState.Complete;
            progress?.Report(new CaptureProgress(capture.CapturedSampleCount, configuration.SampleCount, _state, "Capture complete"));
            return capture;
        }
        catch
        {
            try { await producer; } catch { /* Preserve the original cancellation/transport error. */ }
            _state = DeviceState.Faulted;
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
