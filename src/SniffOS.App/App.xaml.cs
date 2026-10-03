using System.Windows;
using SniffOS.App.ViewModels;
using SniffOS.Core.Transport;

namespace SniffOS.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Simulator is the only default device. USB CDC selection belongs behind a capability
        // handshake and measured transport test; it is not inferred from a COM port existing.
        var device = new SimulatedCaptureDevice();
        _viewModel = new MainViewModel(device, new WindowsFileDialogService());
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.DisposeAsync();
        base.OnExit(e);
    }
}

internal sealed class WindowsFileDialogService : IFileDialogService
{
    public string? PickCaptureToSave()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "SniffOS capture (*.sniffcap)|*.sniffcap|All files (*.*)|*.*",
            DefaultExt = ".sniffcap",
            AddExtension = true,
            Title = "Save SniffOS capture"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickCaptureToOpen()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "SniffOS capture (*.sniffcap)|*.sniffcap|All files (*.*)|*.*",
            Title = "Open SniffOS capture",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
