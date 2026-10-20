using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace GameWake.App.ViewModels;

public sealed partial class AdvancedViewModel : ObservableObject
{
    private readonly AppHost _host;

    public AdvancedViewModel(AppHost host)
    {
        _host = host;
        DataFolder = host.Paths.Root;
        LogsFolder = host.Paths.LogsDirectory;
        Version = host.Version;
    }

    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private string _dataFolder = string.Empty;
    [ObservableProperty] private string _logsFolder = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public void RefreshLogs()
    {
        var lines = _host.FileLogger.Recent.Snapshot();
        LogText = lines.Count == 0 ? "No log lines yet." : string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenPath(_host.Paths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenPath(_host.Paths.Root);

    [RelayCommand]
    private void OpenCurrentLog()
    {
        var file = _host.FileLogger.CurrentFile;
        if (file is null || !File.Exists(file))
        {
            OpenLogsFolder();
            return;
        }

        OpenPath(file);
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export GameWake diagnostics",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"GameWake-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, _host.BuildDiagnostics());
        StatusMessage = "Diagnostics saved.";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear the saved activity history? This cannot be undone.",
            "GameWake",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _host.History.Clear();
        _host.PersistHistory();
        StatusMessage = "Activity history cleared.";
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var result = System.Windows.MessageBox.Show(
            "Reset every setting to GameWake's defaults?",
            "GameWake",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var defaults = _host.Settings.Reset();
        _host.Startup.Apply(defaults.LaunchWithWindows);
        _host.Engine.ApplySettings(defaults);
        StatusMessage = "Settings reset to defaults.";
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "GameWake", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
