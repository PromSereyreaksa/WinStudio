using Microsoft.UI.Xaml;
namespace WinStudio.App.Controls;

public sealed partial class FloatingToolbar : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _pulseTimer;
    private TimeSpan _elapsed;
    private DateTimeOffset? _lastResumeUtc;
    private bool _isPaused;

    public event EventHandler? CancelRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? PauseToggleRequested;

    public FloatingToolbar()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick += (_, _) => UpdateElapsedDisplay();
        _pulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _pulseTimer.Tick += (_, _) =>
        {
            RecordingDot.Opacity = RecordingDot.Opacity < 0.7 ? 1 : 0.35;
        };

        Closed += FloatingToolbar_Closed;
    }

    public bool IsPaused => _isPaused;

    public void StartClock()
    {
        _elapsed = TimeSpan.Zero;
        _lastResumeUtc = DateTimeOffset.UtcNow;
        _isPaused = false;
        PauseGlyphTextBlock.Text = "||";
        _timer.Start();
        StartPulse();
        UpdateElapsedDisplay();
    }

    public void SetPaused(bool paused)
    {
        if (paused == _isPaused)
        {
            return;
        }

        _isPaused = paused;
        if (_isPaused)
        {
            if (_lastResumeUtc is DateTimeOffset resumedAt)
            {
                _elapsed += DateTimeOffset.UtcNow - resumedAt;
                _lastResumeUtc = null;
            }

            PauseGlyphTextBlock.Text = "▶";
            StopPulse();
        }
        else
        {
            _lastResumeUtc = DateTimeOffset.UtcNow;
            PauseGlyphTextBlock.Text = "||";
            StartPulse();
        }

        UpdateElapsedDisplay();
    }

    public void SetBusyStopping()
    {
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
    }

    public async Task RunCountdownAsync(int seconds, CancellationToken cancellationToken)
    {
        CountdownOverlay.Visibility = Visibility.Visible;
        try
        {
            for (var remaining = Math.Max(1, seconds); remaining >= 1; remaining--)
            {
                CountdownTextBlock.Text = remaining.ToString();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            CountdownOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void FloatingToolbar_Closed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _pulseTimer.Stop();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PauseToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StartPulse()
    {
        _pulseTimer.Start();
        RecordingDot.Opacity = 1;
    }

    private void StopPulse()
    {
        _pulseTimer.Stop();
        RecordingDot.Opacity = 1;
    }

    private void UpdateElapsedDisplay()
    {
        var current = _elapsed;
        if (!_isPaused && _lastResumeUtc is DateTimeOffset resumedAt)
        {
            current += DateTimeOffset.UtcNow - resumedAt;
        }

        ElapsedTextBlock.Text = current.ToString(@"hh\:mm\:ss");
    }
}
