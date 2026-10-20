using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GameWake.App.ViewModels;

namespace GameWake.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWcpDoNotRound = 1;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    private readonly ShellViewModel _shell;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_shell.RequestClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        WindowState = _restoreState == WindowState.Minimized ? WindowState.Normal : _restoreState;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _restoreState = WindowState;
        }

        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        EnterFullscreenGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        ExitFullscreenGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximise";
        System.Windows.Automation.AutomationProperties.SetName(
            MaximizeButton,
            maximized ? "Restore" : "Maximise");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var useDark = 1;
        var square = DwmWcpDoNotRound;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref square, sizeof(int));

        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = NativeMethods.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var work = monitorInfo.Work;
        var display = monitorInfo.Monitor;
        info.MaxPosition.X = Math.Abs(work.Left - display.Left);
        info.MaxPosition.Y = Math.Abs(work.Top - display.Top);
        info.MaxSize.X = Math.Abs(work.Right - work.Left);
        info.MaxSize.Y = Math.Abs(work.Bottom - work.Top);

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } transform)
        {
            info.MinTrackSize.X = (int)Math.Ceiling(MinWidth * transform.TransformToDevice.M11);
            info.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * transform.TransformToDevice.M22);
        }

        Marshal.StructureToPtr(info, lParam, fDeleteOld: true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
    }
}
