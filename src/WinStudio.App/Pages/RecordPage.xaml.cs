using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using WinStudio.App.Helpers;
using WinStudio.App.Models;
using WinStudio.Common;

namespace WinStudio.App.Pages;

public sealed partial class RecordPage : Page
{
    private nint _selectedWindowHandle;
    private string? _selectedWindowTitle;
    private string _selectedBackgroundColorHex = BackgroundSettings.SlateBlue.ColorHex;
    private string? _customBackgroundImagePath;
    private readonly List<RadioButton> _swatchButtons = [];

    public event EventHandler<RecordRequestedEventArgs>? StartRequested;

    public RecordPage()
    {
        InitializeComponent();
        WindowCaptureToggle.IsChecked = true;
        MonitorCaptureToggle.IsChecked = false;
        ZoomIntensitySlider.Value = 1.4;
        ZoomSensitivitySlider.Value = 1.2;
        FollowSpeedSlider.Value = 1.15;
        BuildBackgroundSwatches();
        LoadWindowTargets();
        UpdateSegmentVisuals();
        UpdateCaptureModeUi();
        UpdateSettingsPreview();
        UpdateOverviewSummary();
        SetStatus("Pick a source and start when you are ready.");
    }

    public RecordingOptions GetCurrentOptions()
    {
        var captureTarget = WindowCaptureToggle.IsChecked == true ? "Window" : "Monitor";

        BackgroundSettings background;
        if (BackgroundToggle.IsOn)
        {
            background = !string.IsNullOrWhiteSpace(_customBackgroundImagePath)
                ? new BackgroundSettings(BackgroundMode.Image, ImagePath: _customBackgroundImagePath)
                : new BackgroundSettings(BackgroundMode.Solid, _selectedBackgroundColorHex);
        }
        else
        {
            background = BackgroundSettings.None;
        }

        return new RecordingOptions(
            captureTarget,
            60,
            SystemAudioToggle.IsOn,
            captureTarget == "Window" ? _selectedWindowHandle : nint.Zero,
            captureTarget == "Window" ? _selectedWindowTitle : null,
            (float)ZoomIntensitySlider.Value,
            (float)ZoomSensitivitySlider.Value,
            (float)FollowSpeedSlider.Value,
            background);
    }

    public void SetBusy(bool busy)
    {
        StartRecordingButton.IsEnabled = !busy;
        RefreshWindowsButton.IsEnabled = !busy;
        WindowPickerComboBox.IsEnabled = !busy;
        ReadinessTextBlock.Text = busy ? "Starting recording..." : GetReadinessMessage();
        StartRecordingSecondaryTextBlock.Text = busy
            ? "Setting up the recorder and positioning the live controller."
            : GetStartButtonHelperText();
    }

    public void SetStatus(string message)
    {
        StatusTextBlock.Text = string.IsNullOrWhiteSpace(message)
            ? "Pick a source and start when you are ready."
            : message;
        ReadinessTextBlock.Text = GetReadinessMessage();
    }

    private void WindowCaptureToggle_Checked(object sender, RoutedEventArgs e)
    {
        MonitorCaptureToggle.IsChecked = false;
        UpdateSegmentVisuals();
        UpdateCaptureModeUi();
        UpdateOverviewSummary();
    }

    private void MonitorCaptureToggle_Checked(object sender, RoutedEventArgs e)
    {
        WindowCaptureToggle.IsChecked = false;
        UpdateSegmentVisuals();
        UpdateCaptureModeUi();
        UpdateOverviewSummary();
    }

    private void WindowPickerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StoreSelectedWindow(WindowPickerComboBox.SelectedItem as WindowTargetOption);
        UpdateWindowSelectionHint();
        UpdateOverviewSummary();
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadWindowTargets();
        UpdateOverviewSummary();
    }

    private void SettingsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateSettingsPreview();
    }

    private void SystemAudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateOverviewSummary();
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCaptureToggle.IsChecked == true && _selectedWindowHandle == nint.Zero)
        {
            SetStatus("Select a window before starting a window capture.");
            return;
        }

        StartRequested?.Invoke(this, new RecordRequestedEventArgs(GetCurrentOptions()));
    }

    private void LoadWindowTargets()
    {
        var previousHandle = _selectedWindowHandle;
        var windows = WindowEnumerationHelper.GetRecordableWindows();

        WindowPickerComboBox.ItemsSource = windows;
        WindowPickerComboBox.SelectedItem = previousHandle != nint.Zero
            ? windows.FirstOrDefault(option => option.Handle == previousHandle)
            : null;

        if (WindowPickerComboBox.SelectedItem is not WindowTargetOption)
        {
            StoreSelectedWindow(null);
        }

        if (windows.Count == 0)
        {
            WindowSelectionHintTextBlock.Text = "No app windows were found. Open the app you want to record, then press Refresh.";
            UpdateOverviewSummary();
            return;
        }

        UpdateWindowSelectionHint();
        UpdateOverviewSummary();
    }

    private void UpdateWindowSelectionHint()
    {
        if (WindowPickerComboBox.SelectedItem is WindowTargetOption selectedWindow)
        {
            WindowSelectionHintTextBlock.Text = $"Selected: {selectedWindow.Title}";
        }
        else
        {
            WindowSelectionHintTextBlock.Text = "Select a window to record.";
        }
    }

    private void StoreSelectedWindow(WindowTargetOption? selectedWindow)
    {
        _selectedWindowHandle = selectedWindow?.Handle ?? nint.Zero;
        _selectedWindowTitle = selectedWindow?.Title;
    }

    private void UpdateCaptureModeUi()
    {
        var isWindowMode = WindowCaptureToggle.IsChecked == true;
        WindowPickerCard.Visibility = isWindowMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSettingsPreview()
    {
        ZoomIntensityPreviewTextBlock.Text = DescribeIntensity(ZoomIntensitySlider.Value);
        ZoomSensitivityPreviewTextBlock.Text = DescribeSensitivity(ZoomSensitivitySlider.Value);
        FollowSpeedPreviewTextBlock.Text = DescribeSpeed(FollowSpeedSlider.Value);
        UpdateOverviewSummary();
    }

    private static string DescribeIntensity(double value)
    {
        if (value < 1.0)
        {
            return "Subtle";
        }

        if (value < 1.7)
        {
            return "Balanced";
        }

        return "Punchy";
    }

    private static string DescribeSensitivity(double value)
    {
        if (value < 1.0)
        {
            return "Calm";
        }

        if (value < 1.5)
        {
            return "Responsive";
        }

        return "Tight";
    }

    private static string DescribeSpeed(double value)
    {
        if (value < 1.0)
        {
            return "Cinematic";
        }

        if (value < 1.5)
        {
            return "Snappy";
        }

        return "Fast";
    }

    private void UpdateSegmentVisuals()
    {
        var selectedBackground = GetBrush("AppAccentBrush");
        var selectedBorder = GetBrush("AppAccentStrongBrush");
        var unselectedBackground = GetBrush("AppSurfaceRaisedBrush");
        var unselectedBorder = GetBrush("AppStrokeBrush");
        var selectedForeground = GetBrush("AppOnAccentBrush");
        var unselectedForeground = GetBrush("AppSubtleTextBrush");

        var windowSelected = WindowCaptureToggle.IsChecked == true;

        WindowCaptureToggle.Background = windowSelected ? selectedBackground : unselectedBackground;
        WindowCaptureToggle.BorderBrush = windowSelected ? selectedBorder : unselectedBorder;
        WindowCaptureToggle.Foreground = windowSelected ? selectedForeground : unselectedForeground;

        MonitorCaptureToggle.Background = windowSelected ? unselectedBackground : selectedBackground;
        MonitorCaptureToggle.BorderBrush = windowSelected ? unselectedBorder : selectedBorder;
        MonitorCaptureToggle.Foreground = windowSelected ? unselectedForeground : selectedForeground;
    }

    private void BackgroundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        BackgroundOptionsPanel.Visibility = BackgroundToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateOverviewSummary();
    }

    private void BgSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string hex)
        {
            _selectedBackgroundColorHex = hex;
            _customBackgroundImagePath = null;
            UpdateBackgroundImageHint();
        }
    }

    private async void BackgroundBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        var hwnd = App.ActiveWindow is { } w
            ? WindowNative.GetWindowHandle(w)
            : IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            _customBackgroundImagePath = file.Path;
            // Uncheck all swatches since a custom image is now active.
            foreach (var btn in _swatchButtons)
            {
                btn.IsChecked = false;
            }

            UpdateBackgroundImageHint();
        }
    }

    private void UpdateBackgroundImageHint()
    {
        if (!IsLoaded)
        {
            return;
        }

        BackgroundImagePathTextBlock.Text = string.IsNullOrWhiteSpace(_customBackgroundImagePath)
            ? "Or pick a custom image file."
            : System.IO.Path.GetFileName(_customBackgroundImagePath);
    }

    private void BuildBackgroundSwatches()
    {
        var defaultHex = BackgroundSettings.SlateBlue.ColorHex;
        foreach (var preset in BackgroundSettings.BuiltInPresets)
        {
            var btn = new RadioButton
            {
                Style = (Style)Resources["ColorSwatchStyle"],
                Background = new SolidColorBrush(ParseHexColor(preset.ColorHex)),
                GroupName = "BgColor",
                Tag = preset.ColorHex,
                IsChecked = preset.ColorHex == defaultHex,
            };
            btn.Checked += BgSwatch_Checked;
            _swatchButtons.Add(btn);
            BackgroundSwatchContainer.Children.Add(btn);
        }
    }

    private static Color ParseHexColor(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6)
        {
            return Color.FromArgb(
                0xFF,
                Convert.ToByte(h[..2], 16),
                Convert.ToByte(h[2..4], 16),
                Convert.ToByte(h[4..6], 16));
        }

        return Color.FromArgb(0xFF, 0, 0, 0);
    }

    private void UpdateOverviewSummary()
    {
        var windowMode = WindowCaptureToggle.IsChecked == true;
        CaptureSummaryTextBlock.Text = windowMode
            ? (_selectedWindowHandle == nint.Zero ? "Window capture pending" : "Window capture ready")
            : "Monitor capture";
        AudioSummaryTextBlock.Text = SystemAudioToggle.IsOn ? "System audio on" : "Silent video";
        AutoZoomSummaryTextBlock.Text =
            $"{DescribeIntensity(ZoomIntensitySlider.Value)} zoom | {DescribeSpeed(FollowSpeedSlider.Value)} follow";
        ReadinessTextBlock.Text = GetReadinessMessage();
        StartRecordingSecondaryTextBlock.Text = GetStartButtonHelperText();
    }

    private string GetReadinessMessage()
    {
        if (WindowCaptureToggle.IsChecked == true && _selectedWindowHandle == nint.Zero)
        {
            return "Choose a window first";
        }

        return "Ready to record";
    }

    private string GetStartButtonHelperText()
    {
        return WindowCaptureToggle.IsChecked == true
            ? "Window recordings show a short countdown before capture begins."
            : "Monitor recordings start immediately and hide the controller from the capture.";
    }

    private static Brush GetBrush(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }
}

public sealed class RecordRequestedEventArgs : EventArgs
{
    public RecordRequestedEventArgs(RecordingOptions options)
    {
        Options = options;
    }

    public RecordingOptions Options { get; }
}
