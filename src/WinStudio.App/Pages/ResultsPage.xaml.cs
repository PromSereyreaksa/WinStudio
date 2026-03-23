using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Media.Core;
using Windows.UI;
using WinStudio.App.Models;

namespace WinStudio.App.Pages;

public sealed partial class ResultsPage : Page
{
    private RecordingResult? _result;

    public event EventHandler? NewRecordingRequested;

    public ResultsPage()
    {
        InitializeComponent();
        Unloaded += ResultsPage_Unloaded;
    }

    public void LoadResult(RecordingResult result)
    {
        _result = result;
        SummaryTextBlock.Text =
            $"Duration {result.Duration:mm\\:ss}  •  Cursor events {result.CursorEventCount}  •  Zoom keyframes {result.ZoomKeyframeCount}";
        EditedPathTextBlock.Text = result.VideoPath;
        RawPathTextBlock.Text = result.RawVideoPath;
        DataPathTextBlock.Text = $"{result.CursorLogPath}\n{result.ZoomKeyframesPath}";

        var hasEditedVideo = File.Exists(result.VideoPath);
        var hasRawVideo = File.Exists(result.RawVideoPath);
        OpenEditedButton.IsEnabled = hasEditedVideo;
        OpenRawButton.IsEnabled = hasRawVideo;

        if (string.IsNullOrWhiteSpace(result.ProcessingError))
        {
            ProcessingWarningBorder.Visibility = Visibility.Collapsed;
            ProcessingWarningTextBlock.Text = string.Empty;
        }
        else
        {
            ProcessingWarningBorder.Visibility = Visibility.Visible;
            ProcessingWarningTextBlock.Text =
                $"Edited video failed, but the raw recording was saved.\n{result.ProcessingError}";
        }

        EditedPreviewToggle.IsChecked = hasEditedVideo;
        RawPreviewToggle.IsChecked = !hasEditedVideo && hasRawVideo;
        UpdatePreviewToggleVisuals();
        LoadPreview(EditedPreviewToggle.IsChecked == true ? result.VideoPath : result.RawVideoPath);
    }

    public void UnloadPreview()
    {
        PreviewPlayerElement.Source = null;
    }

    private void ResultsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        UnloadPreview();
    }

    private void EditedPreviewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        RawPreviewToggle.IsChecked = false;
        UpdatePreviewToggleVisuals();
        LoadPreview(_result.VideoPath);
    }

    private void RawPreviewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        EditedPreviewToggle.IsChecked = false;
        UpdatePreviewToggleVisuals();
        LoadPreview(_result.RawVideoPath);
    }

    private void OpenEditedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        OpenPath(_result.VideoPath);
    }

    private void OpenRawButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        OpenPath(_result.RawVideoPath);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        var target = File.Exists(_result.VideoPath) ? _result.VideoPath : _result.RawVideoPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch failures.
        }
    }

    private void NewRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        UnloadPreview();
        NewRecordingRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LoadPreview(string path)
    {
        if (!File.Exists(path))
        {
            PreviewPlayerElement.Source = null;
            PreviewUnavailableOverlay.Visibility = Visibility.Visible;
            PreviewUnavailableTextBlock.Text = "That video file was not found on disk.";
            return;
        }

        try
        {
            PreviewUnavailableOverlay.Visibility = Visibility.Collapsed;
            PreviewPlayerElement.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch (Exception ex)
        {
            PreviewPlayerElement.Source = null;
            PreviewUnavailableOverlay.Visibility = Visibility.Visible;
            PreviewUnavailableTextBlock.Text = ex.Message;
        }
    }

    private void UpdatePreviewToggleVisuals()
    {
        ApplyToggleStyle(EditedPreviewToggle, EditedPreviewToggle.IsChecked == true);
        ApplyToggleStyle(RawPreviewToggle, RawPreviewToggle.IsChecked == true);
    }

    private static void ApplyToggleStyle(ToggleButton toggle, bool selected)
    {
        toggle.Background = new SolidColorBrush(selected ? Color.FromArgb(255, 0, 120, 212) : Color.FromArgb(255, 31, 31, 31));
        toggle.BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(255, 0, 120, 212) : Color.FromArgb(255, 42, 42, 42));
        toggle.Foreground = new SolidColorBrush(selected ? Microsoft.UI.Colors.White : Color.FromArgb(255, 154, 154, 154));
    }

    private static void OpenPath(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch failures.
        }
    }
}
