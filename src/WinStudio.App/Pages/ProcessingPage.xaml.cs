using Microsoft.UI.Xaml.Controls;

namespace WinStudio.App.Pages;

public sealed partial class ProcessingPage : Page
{
    public ProcessingPage()
    {
        InitializeComponent();
    }

    public void SetIndeterminate(string status, string details)
    {
        StatusTextBlock.Text = status;
        ProgressTextBlock.Text = details;
        ProcessingProgressBar.IsIndeterminate = true;
    }

    public void SetProgress(double value, string details)
    {
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressBar.Value = Math.Clamp(value, 0, 100);
        ProgressTextBlock.Text = details;
    }
}
