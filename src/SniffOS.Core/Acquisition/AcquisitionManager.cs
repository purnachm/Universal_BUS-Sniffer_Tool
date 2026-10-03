using SniffOS.Core.Protocol;
using SniffOS.Core.Transport;

namespace SniffOS.Core.Acquisition;

/// <summary>
/// Owns the device lifecycle and acquisition state machine. UI code does not manipulate a
/// transport or decide whether a partial capture is complete.
/// </summary>
public sealed class AcquisitionManager : IAsyncDisposable
{
    private readonly ICaptureDevice _device;
    private DeviceState _state = DeviceState.Disconnected;

    public AcquisitionManager(ICaptureDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public event Action<DeviceState>? StateChanged;
    public string DeviceName => _device.DisplayName;
    public DeviceCapabilities Capabilities => _device.Capabilities;
    public DeviceState State => _state;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(DeviceState.Disconnected);
        SetState(DeviceState.Connecting);
        try
        {
            await _device.ConnectAsync(cancellationToken);
            SetState(DeviceState.Idle);
        }
        catch
        {
            SetState(DeviceState.Faulted);
            throw;
        }
    }

    public async Task<RawCapture> CaptureAsync(CaptureConfiguration configuration, IProgress<CaptureProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureState(DeviceState.Idle, DeviceState.Complete);
        configuration.Validate(Capabilities);
        SetState(DeviceState.Configured);
        try
        {
            var capture = await _device.CaptureAsync(configuration, progress, cancellationToken);
            SetState(DeviceState.Complete);
            return capture;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancellation never returns a partial RawCapture. The device can be re-armed only
            // after the manager returns to Idle, and the caller receives the cancellation.
            SetState(DeviceState.Idle);
            throw;
        }
        catch
        {
            SetState(DeviceState.Faulted);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _device.DisconnectAsync(cancellationToken);
        SetState(DeviceState.Disconnected);
    }

    public async ValueTask DisposeAsync() => await _device.DisposeAsync();

    private void EnsureState(params DeviceState[] allowed)
    {
        if (!allowed.Contains(_state))
            throw new InvalidOperationException($"Device state {_state} does not permit this operation.");
    }

    private void SetState(DeviceState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }
}
