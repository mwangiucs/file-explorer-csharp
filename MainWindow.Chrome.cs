// ── MainWindow.Chrome.cs ────────────────────────────────────────────────────
// Drop this file into your project alongside MainWindow.xaml.cs.
// It adds: custom title-bar drag, window-chrome buttons, inspector tab switching,
// the StringToVisibilityConverter used in the tag-pill binding, window state persistence,
// and double-click title bar to maximize/restore.

using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FileExplorerCS;

// ── Converter ────────────────────────────────────────────────────────────────
/// <summary>Returns Visible when the string is non-empty, Collapsed otherwise.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NonNullToVisibilityConverter : IValueConverter
{
    public static readonly NonNullToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

// ── Window state persistence ───────────────────────────────────────────────────
public class WindowState
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public static class WindowStateManager
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileExplorerCS",
        "windowstate.json");

    public static void Save(WindowState state)
    {
        try
        {
            string directory = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(state);
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
            // Silently fail if we can't save state
        }
    }

    public static WindowState? Load()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return null;

            string json = File.ReadAllText(StateFilePath);
            return JsonSerializer.Deserialize<WindowState>(json);
        }
        catch
        {
            return null;
        }
    }
}

// ── Window chrome ─────────────────────────────────────────────────────────────
public partial class MainWindow
{
    private WindowState? _savedWindowState;

    // ── Window state persistence ───────────────────────────────────────────────
    private void LoadWindowState()
    {
        _appSettings = AppSettingsStore.Load();
        Left = _appSettings.WindowLeft;
        Top = _appSettings.WindowTop;
        Width = _appSettings.WindowWidth;
        Height = _appSettings.WindowHeight;
        if (_appSettings.IsWindowMaximized)
        {
            WindowState = System.Windows.WindowState.Maximized;
        }
        ArchivePathTextBox.Text = _appSettings.ArchiveRoot;
    }

    private void SaveWindowState()
    {
        if (WindowState == System.Windows.WindowState.Maximized)
        {
            _appSettings.IsWindowMaximized = true;
        }
        else
        {
            _appSettings.IsWindowMaximized = false;
            _appSettings.WindowLeft = Left;
            _appSettings.WindowTop = Top;
            _appSettings.WindowWidth = Width;
            _appSettings.WindowHeight = Height;
        }

        if (!string.IsNullOrWhiteSpace(_currentPath))
        {
            _appSettings.LastPath = _currentPath;
        }

        _appSettings.ArchiveRoot = ArchivePathTextBox.Text;

        SaveColumnWidths();

        AppSettingsStore.Save(_appSettings);
    }

    // ── Title-bar drag ────────────────────────────────────────────────────────
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                // Double-click to maximize/restore
                WindowState = WindowState == System.Windows.WindowState.Maximized
                    ? System.Windows.WindowState.Normal
                    : System.Windows.WindowState.Maximized;
            }
            else
            {
                // Single-click to drag
                DragMove();
            }
        }
    }

    // ── Window control buttons ────────────────────────────────────────────────
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowState();
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = System.Windows.WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;

    private void Window_StateChanged(object sender, System.EventArgs e)
    {
        if (MaximizeButton == null || OuterBorder == null || TitleBarBorder == null || CloseButton == null)
            return;

        if (WindowState == System.Windows.WindowState.Maximized)
        {
            MaximizeButton.Content = "\uE923";
            MaximizeButton.ToolTip = "Restore Down";
            OuterBorder.CornerRadius = new CornerRadius(0);
            TitleBarBorder.CornerRadius = new CornerRadius(0);
            CloseButton.Tag = new CornerRadius(0);
        }
        else
        {
            MaximizeButton.Content = "\uE922";
            MaximizeButton.ToolTip = "Maximize";
            OuterBorder.CornerRadius = new CornerRadius(10);
            TitleBarBorder.CornerRadius = new CornerRadius(10, 10, 0, 0);
            CloseButton.Tag = new CornerRadius(0, 10, 0, 0);
        }
    }

    // ── Inspector tab toggle ──────────────────────────────────────────────────
    private void InspectorTab_Click(object sender, RoutedEventArgs e)
    {
        bool showPreview = InspectorPreviewTab.IsChecked == true;
        PreviewPane.Visibility  = showPreview ? Visibility.Visible : Visibility.Collapsed;
        DetailsPane.Visibility  = showPreview ? Visibility.Collapsed : Visibility.Visible;
    }

    /*
    // ── Sort-panel card clicks (clicking the card border selects its radio) ───
    private void SortCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border card) return;

        // Map each card Border to its RadioButton by name convention
        if      (card == SortCardNone)  SmartSortNoneRadio.IsChecked  = true;
        else if (card == SortCardYear)  SmartSortYearRadio.IsChecked  = true;
        else if (card == SortCardMonth) SmartSortMonthRadio.IsChecked = true;
        else if (card == SortCardDay)   SmartSortDayRadio.IsChecked   = true;

        RefreshSortCards();
    }

    private void RefreshSortCards()
    {
        var blueBg     = FindResource("Blue050Brush") as System.Windows.Media.SolidColorBrush;
        var whiteBg    = System.Windows.Media.Brushes.White;
        var blueBorder = FindResource("Blue600Brush") as System.Windows.Media.SolidColorBrush;
        var grayBorder = FindResource("Gray300Brush") as System.Windows.Media.SolidColorBrush;

        void Style(System.Windows.Controls.Border b, bool active)
        {
            if (b == null) return;
            b.Background  = active ? blueBg  : whiteBg;
            b.BorderBrush = active ? blueBorder : grayBorder;
        }

        Style(SortCardNone,  SmartSortNoneRadio?.IsChecked  == true);
        Style(SortCardYear,  SmartSortYearRadio?.IsChecked  == true);
        Style(SortCardMonth, SmartSortMonthRadio?.IsChecked == true);
        Style(SortCardDay,   SmartSortDayRadio?.IsChecked   == true);
    }
    */
    // ── WM_GETMINMAXINFO hook to prevent maximized window from covering taskbar ──
    public void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Enable rounded corners on Windows 11
        try
        {
            int attribute = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
            int preference = 2; // DWMWCP_ROUND (Round corners)
            DwmSetWindowAttribute(handle, attribute, ref preference, sizeof(int));
        }
        catch { /* Fallback for Windows 10 or older */ }

        // Enable native shadow for borderless window
        try
        {
            var margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        catch { }

        var hwndSource = HwndSource.FromHwnd(handle);
        hwndSource?.AddHook(WindowProc);
    }

    private bool _isMaximizePressed = false;

    private bool IsMouseOverElement(UIElement? element, IntPtr lParam)
    {
        if (element == null || !element.IsVisible) return false;

        long val = lParam.ToInt64();
        int x = (int)(short)(val & 0xFFFF);
        int y = (int)(short)((val >> 16) & 0xFFFF);

        try
        {
            System.Windows.Point elementPos = element.PointToScreen(new System.Windows.Point(0, 0));
            double width = element.RenderSize.Width;
            double height = element.RenderSize.Height;

            var source = PresentationSource.FromVisual(element);
            if (source?.CompositionTarget != null)
            {
                double mX = source.CompositionTarget.TransformToDevice.M11;
                double mY = source.CompositionTarget.TransformToDevice.M22;

                // PointToScreen already returns coordinates in physical device pixels
                double physicalLeft = elementPos.X;
                double physicalTop = elementPos.Y;
                double physicalWidth = width * mX;
                double physicalHeight = height * mY;

                return (x >= physicalLeft && x < physicalLeft + physicalWidth &&
                        y >= physicalTop && y < physicalTop + physicalHeight);
            }
        }
        catch
        {
            // point to screen or composition target might fail
        }

        return false;
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        else if (msg == 0x0084) // WM_NCHITTEST
        {
            if (IsMouseOverElement(MaximizeButton, lParam))
            {
                handled = true;
                return new IntPtr(9); // HTMAXBUTTON
            }
        }
        else if (msg == 0x00A1) // WM_NCLBUTTONDOWN
        {
            if (wParam.ToInt32() == 9) // HTMAXBUTTON
            {
                _isMaximizePressed = true;
                handled = true;
                return IntPtr.Zero;
            }
        }
        else if (msg == 0x00A2) // WM_NCLBUTTONUP
        {
            if (_isMaximizePressed)
            {
                _isMaximizePressed = false;
                if (IsMouseOverElement(MaximizeButton, lParam))
                {
                    MaximizeButton_Click(this, new RoutedEventArgs());
                }
                handled = true;
                return IntPtr.Zero;
            }
        }
        else if (msg == 0x02A2 || msg == 0x02A3) // WM_NCMOUSELEAVE, WM_MOUSELEAVE
        {
            _isMaximizePressed = false;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;

        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO();
            monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(monitor, ref monitorInfo);

            RECT rcWorkArea = monitorInfo.rcWork;
            RECT rcMonitorArea = monitorInfo.rcMonitor;

            mmi.ptMaxPosition.x = Math.Abs(rcWorkArea.Left - rcMonitorArea.Left);
            mmi.ptMaxPosition.y = Math.Abs(rcWorkArea.Top - rcMonitorArea.Top);
            mmi.ptMaxSize.x = Math.Abs(rcWorkArea.Right - rcWorkArea.Left);
            mmi.ptMaxSize.y = Math.Abs(rcWorkArea.Bottom - rcWorkArea.Top);
            
            // Min track size
            mmi.ptMinTrackSize.x = (int)MinWidth;
            mmi.ptMinTrackSize.y = (int)MinHeight;
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }
}
