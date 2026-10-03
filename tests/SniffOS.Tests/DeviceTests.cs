using SniffOS.Core.Protocol;
using SniffOS.Core.Transport;

namespace SniffOS.Tests;

public sealed class DeviceTests
{
    [Fact]
    public async Task AcquisitionManager_EnforcesLifecycleTransitions()
    {
        await using var manager = new SniffOS.Core.Acquisition.AcquisitionManager(new SimulatedCaptureDevice(TimeSpan.Zero));
        Assert.Equal(DeviceState.Disconnected, manager.State);
        await manager.ConnectAsync();
        Assert.Equal(DeviceState.Idle, manager.State);
        var config = new CaptureConfiguration(4, 1_000, 32, 0xF, TriggerMode.Immediate, 0, true);
        await manager.CaptureAsync(config);
        Assert.Equal(DeviceState.Complete, manager.State);
        await manager.DisconnectAsync();
        Assert.Equal(DeviceState.Disconnected, manager.State);
    }

    [Fact]
    public async Task Simulator_ConnectsCapturesAndReportsCompleteState()
    {
        await using var device = new SimulatedCaptureDevice(TimeSpan.Zero);
        await device.ConnectAsync();
        var configuration = new CaptureConfiguration(4, 1_000, 1_000, 0xF, TriggerMode.Immediate, 0, true, "test");
        var capture = await device.CaptureAsync(configuration);
        Assert.Equal(DeviceState.Complete, device.State);
        Assert.True(capture.IsComplete);
        Assert.Equal((ulong)1_000, capture.CapturedSampleCount);
        Assert.Empty(capture.Gaps);
    }

    [Fact]
    public async Task Simulator_CancellationDoesNotReturnPartialCaptureAsComplete()
    {
        await using var device = new SimulatedCaptureDevice(TimeSpan.FromMilliseconds(2));
        await device.ConnectAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(8));
        var configuration = new CaptureConfiguration(4, 1_000, 100_000, 0xF, TriggerMode.Immediate, 0, true, "cancel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.CaptureAsync(configuration, cancellationToken: cancellation.Token));
        Assert.Equal(DeviceState.Faulted, device.State);
        await device.DisconnectAsync();
        Assert.Equal(DeviceState.Disconnected, device.State);
    }

    [Fact]
    public void CaptureConfiguration_RejectsUnadvertisedRateAndMask()
    {
        var device = new SimulatedCaptureDevice();
        var badRate = new CaptureConfiguration(4, 123, 10, 0xF, TriggerMode.Immediate, 0, true);
        Assert.ThrowsAny<Exception>(() => badRate.Validate(device.Capabilities));
        var badMask = new CaptureConfiguration(4, 1_000, 10, 0x10, TriggerMode.Immediate, 0, true);
        Assert.ThrowsAny<Exception>(() => badMask.Validate(device.Capabilities));
    }
}
