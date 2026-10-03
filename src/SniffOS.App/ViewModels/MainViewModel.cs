using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SniffOS.Core.Acquisition;
using SniffOS.Core.Decoding;
using SniffOS.Core.Protocol;
using SniffOS.Core.Storage;
using SniffOS.Core.Transport;

namespace SniffOS.App.ViewModels;

public interface IFileDialogService
{
    string? PickCaptureToSave();
    string? PickCaptureToOpen();
}

public sealed class ChannelViewModel : ObservableObject
{
    private bool _isEnabled = true;
    private string _name;

    public ChannelViewModel(int number)
    {
        Number = number;
        _name = $"CH{number:00}";
    }

    public int Number { get; }
    public string DefaultName => $"CH{Number:00}";
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string Name { get => _name; set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? DefaultName : value.Trim()); }
}

public sealed record DecoderEventRow(string Time, string Kind, string Value, string Status);

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AcquisitionManager _acquisition;
    private readonly IFileDialogService _dialogs;
    private CancellationTokenSource? _captureCancellation;
    private RawCapture? _capture;
    private bool _isBusy;
    private string _deviceStatus = "Disconnected";
    private string _statusMessage = "Simulator selected. Hardware transport is intentionally not selected.";
    private string _sampleCountText = "10000";
    private uint _selectedSampleRate = 100_000;
    private TriggerMode _selectedTrigger = TriggerMode.Immediate;
    private ulong _viewStartSample;
    private double _samplesPerPixel = 32;
    private double _cursorA;
    private double _cursorB = 1000;
    private string _captureSummary = "No capture loaded";

    public MainViewModel(ICaptureDevice device, IFileDialogService dialogs)
    {
        _acquisition = new AcquisitionManager(device ?? throw new ArgumentNullException(nameof(device)));
        _acquisition.StateChanged += state =>
        {
            DeviceStatus = state.ToString();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            RefreshCommandStates();
        };
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Channels = new ObservableCollection<ChannelViewModel>(Enumerable.Range(0, 16).Select(i => new ChannelViewModel(i)));
        SampleRates = _acquisition.Capabilities.SupportedSampleRates;
        TriggerModes = Enum.GetValues<TriggerMode>();
        _selectedSampleRate = SampleRates.Contains(_selectedSampleRate) ? _selectedSampleRate : SampleRates[0];
    }

    public ObservableCollection<ChannelViewModel> Channels { get; }
    public IReadOnlyList<uint> SampleRates { get; }
    public IReadOnlyList<TriggerMode> TriggerModes { get; }
    public ObservableCollection<DecoderEventRow> DecoderEvents { get; } = new();
    public DeviceCapabilities Capabilities => _acquisition.Capabilities;
    public string DeviceName => _acquisition.DeviceName;
    public RawCapture? Capture { get => _capture; private set => SetProperty(ref _capture, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanDisconnect)); } } }
    public bool CanStart => !IsBusy && (_acquisition.State is DeviceState.Idle or DeviceState.Complete);
    public bool CanConnect => !IsBusy && _acquisition.State == DeviceState.Disconnected;
    public bool CanDisconnect => !IsBusy && _acquisition.State != DeviceState.Disconnected;
    public string DeviceStatus { get => _deviceStatus; private set => SetProperty(ref _deviceStatus, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string SampleCountText { get => _sampleCountText; set => SetProperty(ref _sampleCountText, value); }
    public uint SelectedSampleRate { get => _selectedSampleRate; set => SetProperty(ref _selectedSampleRate, value); }
    public TriggerMode SelectedTrigger { get => _selectedTrigger; set => SetProperty(ref _selectedTrigger, value); }
    public ulong ViewStartSample { get => _viewStartSample; set => SetProperty(ref _viewStartSample, value); }
    public double SamplesPerPixel { get => _samplesPerPixel; set => SetProperty(ref _samplesPerPixel, value); }
    public double CursorA { get => _cursorA; set => SetProperty(ref _cursorA, Math.Max(0d, value)); }
    public double CursorB { get => _cursorB; set => SetProperty(ref _cursorB, Math.Max(0d, value)); }
    public string CaptureSummary { get => _captureSummary; private set => SetProperty(ref _captureSummary, value); }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        try
        {
            await _acquisition.ConnectAsync();
            DeviceStatus = _acquisition.State.ToString();
            StatusMessage = $"Connected to {_acquisition.DeviceName}. Capabilities are device-advertised.";
        }
        catch (Exception ex)
        {
            DeviceStatus = "Faulted";
            StatusMessage = $"Connect failed: {ex.Message}";
        }
        finally { RefreshCommandStates(); }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartCaptureAsync()
    {
        if (!uint.TryParse(SampleCountText, out var sampleCount) || sampleCount == 0 || sampleCount > _acquisition.Capabilities.MaxCaptureSamples)
        {
            StatusMessage = $"Sample count must be between 1 and {_acquisition.Capabilities.MaxCaptureSamples:N0}.";
            return;
        }
        var enabledMask = Channels.Where(c => c.IsEnabled).Aggregate(0u, (mask, channel) => mask | (1u << channel.Number));
        if (enabledMask == 0) { StatusMessage = "Enable at least one channel."; return; }
        var config = new CaptureConfiguration(16, SelectedSampleRate, sampleCount, enabledMask, SelectedTrigger, 0, true, "GUI simulator capture");
        try { config.Validate(_acquisition.Capabilities); }
        catch (Exception ex) { StatusMessage = $"Configuration rejected: {ex.Message}"; return; }

        IsBusy = true;
        _captureCancellation = new CancellationTokenSource();
        DeviceStatus = "Capturing";
        StatusMessage = "Acquisition is running off the UI thread; no samples are represented by UI elements.";
        var progress = new Progress<CaptureProgress>(p =>
        {
            DeviceStatus = p.State.ToString();
            StatusMessage = $"{p.Message}: {p.ReceivedSamples:N0} / {p.ExpectedSamples:N0} samples";
        });
        try
        {
            var completedCapture = await _acquisition.CaptureAsync(config, progress, _captureCancellation.Token);
            Capture = completedCapture;
            ViewStartSample = 0;
            SamplesPerPixel = Math.Max(1d, sampleCount / 1200d);
            CursorA = 0;
            CursorB = Math.Min(sampleCount, 1000u);
            CaptureSummary = $"{completedCapture.CapturedSampleCount:N0} samples, {completedCapture.Chunks.Count:N0} chunks, {completedCapture.Gaps.Count:N0} reported gaps";
            StatusMessage = completedCapture.HasLoss ? "Capture complete with loss/gap records. Decoding will not bridge gaps." : "Capture complete; no gaps were reported by the simulator.";
        }
        catch (OperationCanceledException)
        {
            DeviceStatus = "Idle";
            StatusMessage = "Capture cancelled. Partial data was not promoted to a complete capture.";
        }
        catch (Exception ex)
        {
            DeviceStatus = "Faulted";
            StatusMessage = $"Capture failed: {ex.Message}";
        }
        finally
        {
            _captureCancellation.Dispose();
            _captureCancellation = null;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand]
    private void StopCapture()
    {
        if (!IsBusy) return;
        _captureCancellation?.Cancel();
        StatusMessage = "Cancellation requested; waiting for the bounded acquisition pipeline to unwind.";
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        _captureCancellation?.Cancel();
        try { await _acquisition.DisconnectAsync(); DeviceStatus = _acquisition.State.ToString(); StatusMessage = "Device disconnected; no capture data was discarded."; }
        catch (Exception ex) { DeviceStatus = "Faulted"; StatusMessage = $"Disconnect failed: {ex.Message}"; }
        finally { RefreshCommandStates(); }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var capture = Capture;
        if (capture is null) { StatusMessage = "There is no capture to save."; return; }
        var path = _dialogs.PickCaptureToSave();
        if (path is null) return;
        try
        {
            var metadata = new CaptureMetadata(_acquisition.Capabilities.DeviceId, _acquisition.Capabilities.FirmwareVersion, capture.SampleRateHz,
                capture.ChannelCount, Channels.Where(c => c.IsEnabled).Aggregate(0u, (mask, c) => mask | (1u << c.Number)),
                Channels.Select(c => c.Name).ToArray(), SelectedTrigger.ToString(), capture.CaptureId, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), _acquisition.Capabilities.TimebaseDescription);
            await CaptureFileFormat.WriteAsync(path, metadata, capture);
            StatusMessage = $"Saved capture to {Path.GetFileName(path)}.";
        }
        catch (OperationCanceledException) { StatusMessage = "Save cancelled."; }
        catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var path = _dialogs.PickCaptureToOpen();
        if (path is null) return;
        try
        {
            var file = await CaptureFileFormat.ReadAsync(path);
            var loadedCapture = file.Capture;
            Capture = loadedCapture;
            SelectedSampleRate = file.Metadata.SampleRateHz;
            ViewStartSample = 0;
            SamplesPerPixel = Math.Max(1d, loadedCapture.Chunks.Select(c => c.EndExclusive).DefaultIfEmpty(1200).Max() / 1200d);
            CaptureSummary = $"{loadedCapture.CapturedSampleCount:N0} samples, {loadedCapture.Chunks.Count:N0} chunks, {loadedCapture.Gaps.Count:N0} reported gaps";
            StatusMessage = loadedCapture.HasLoss ? "Loaded capture contains explicit gaps." : $"Loaded {Path.GetFileName(path)}.";
        }
        catch (Exception ex) { StatusMessage = $"Load failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DecodeUartAsync()
    {
        var capture = Capture;
        if (capture is null) { StatusMessage = "Load or acquire a capture first."; return; }
        IsBusy = true;
        try
        {
            var events = await Task.Run(() => UartDecoder.Decode(capture, 0, new UartConfiguration(9_600)));
            DecoderEvents.Clear();
            foreach (var item in events)
                DecoderEvents.Add(new DecoderEventRow(FormatTime(item.SampleIndex), "UART", $"0x{item.Value:X2} ({(char)item.Value})", item.Diagnostic ?? "OK"));
            StatusMessage = $"UART decode produced {events.Count:N0} events using channel 0 / 9600 8N1.";
        }
        catch (Exception ex) { StatusMessage = $"UART decode failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private string FormatTime(ulong sample) => $"{sample / (double)Math.Max(1u, Capture?.SampleRateHz ?? 1u):0.000000} s";
    private void RefreshCommandStates()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        StartCaptureCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _captureCancellation?.Cancel();
        await _acquisition.DisposeAsync();
    }
}
