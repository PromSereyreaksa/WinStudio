using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinStudio.App.Helpers;
using WinStudio.App.Models;

namespace WinStudio.App.Pages;

public sealed partial class RecordPage : Page
{
    public event EventHandler<RecordRequestedEventArgs>? StartRequested;

    public RecordPage()
    {
        InitializeComponent();
        WindowCaptureToggle.IsChecked = true;
        MonitorCaptureToggle.IsChecked = false;
        ZoomIntensitySlider.Value = 1.4;
        ZoomSensitivitySlider.Value = 1.2;
        FollowSpeedSlider.Value = 1.15;
        LoadWindowTargets();
        UpdateSegmentVisuals();
        UpdateCaptureModeUi();
        UpdateSettingsPreview();
    }

    public RecordingOptions GetCurrentOptions()
    {
        var captureTarget = WindowCaptureToggle.IsChecked == true ? "Window" : "Monitor";
        var selectedWindow = WindowPickerComboBox.SelectedItem as WindowTargetOption;
        return new RecordingOptions(
            captureTarget,
            60,
            SystemAudioToggle.IsOn,
            selectedWindow?.Handle ?? nint.Zero,
            selectedWindow?.Title,
            (float)ZoomIntensitySlider.Value,
            (float)ZoomSensitivitySlider.Value,
            (float)FollowSpeedSlider.Value);
    }

    public void SetBusy(bool busy)
    {
        StartRecordingButton.IsEnabled = !busy;
        RefreshWindowsButton.IsEnabled = !busy;
        WindowPickerComboBox.IsEnabled = !busy;
    }

    public void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void WindowCaptureToggle_Checked(object sender, RoutedEventArgs e)
    {
        MonitorCaptureToggle.IsChecked = false;
        UpdateSegmentVisuals();
        UpdateCaptureModeUi();
    }

    private void MonitorCaptureToggle_Checked(object sender, RoutedEventArgs e)
    {
        WindowCaptureToggle.IsChecked = false;
        UpdateSegmentVisuals();
        UpdateCaptureModeUi();
    }

    private void WindowPickerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWindowSelectionHint();
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadWindowTargets();
    }

    private void SettingsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateSettingsPreview();
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCaptureToggle.IsChecked == true && WindowPickerComboBox.SelectedItem is null)
        {
            SetStatus("Select a window before starting a window capture.");
            return;
        }

        StartRequested?.Invoke(this, new RecordRequestedEventArgs(GetCurrentOptions()));
    }

    private void LoadWindowTargets()
    {
        var previousHandle = (WindowPickerComboBox.SelectedItem as WindowTargetOption)?.Handle ?? nint.Zero;
        var windows = WindowEnumerationHelper.GetRecordableWindows();

        WindowPickerComboBox.ItemsSource = windows;
        WindowPickerComboBox.SelectedItem = windows.FirstOrDefault(option => option.Handle == previousHandle)
            ?? windows.FirstOrDefault();

        if (windows.Count == 0)
        {
            WindowSelectionHintTextBlock.Text = "No app windows were found. Open the app you want to record, then press Refresh.";
            return;
        }

        UpdateWindowSelectionHint();
    }

    private void UpdateWindowSelectionHint()
    {
        if (WindowPickerComboBox.SelectedItem is WindowTargetOption selectedWindow)
        {
            WindowSelectionHintTextBlock.Text = $"Selected: {selectedWindow.Title}";
        }
        else
        {
            WindowSelectionHintTextBlock.Text = "No window selected.";
        }
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

        return "Dramatic";
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

        return "Hair Trigger";
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
        var selectedBackground = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        var selectedBorder = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        var unselectedBackground = new SolidColorBrush(Color.FromArgb(255, 31, 31, 31));
        var unselectedBorder = new SolidColorBrush(Color.FromArgb(255, 42, 42, 42));
        var selectedForeground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var unselectedForeground = new SolidColorBrush(Color.FromArgb(255, 136, 136, 136));

        var windowSelected = WindowCaptureToggle.IsChecked == true;

        WindowCaptureToggle.Background = windowSelected ? selectedBackground : unselectedBackground;
        WindowCaptureToggle.BorderBrush = windowSelected ? selectedBorder : unselectedBorder;
        WindowCaptureToggle.Foreground = windowSelected ? selectedForeground : unselectedForeground;

        MonitorCaptureToggle.Background = windowSelected ? unselectedBackground : selectedBackground;
        MonitorCaptureToggle.BorderBrush = windowSelected ? unselectedBorder : selectedBorder;
        MonitorCaptureToggle.Foreground = windowSelected ? unselectedForeground : selectedForeground;
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
