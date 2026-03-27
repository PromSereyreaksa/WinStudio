using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStudio.App.Controls;
using WinStudio.App.Helpers;
using WinStudio.App.Models;
using WinStudio.App.Pages;
using WinStudio.App.Services;

namespace WinStudio.App;

public sealed partial class MainWindow : Window
{
    private const int StartStopHotkeyId = 1001;
    private const int PauseHotkeyId = 1002;
    private const int CancelHotkeyId = 1003;
    private const uint VkR = 0x52;
    private const uint VkP = 0x50;
    private const uint VkEscape = 0x1B;

    private readonly IScreenStudioRecorderService _recorderService;
    private readonly RecordPage _recordPage;
    private readonly ProcessingPage _processingPage;
    private readonly ResultsPage _resultsPage;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private HotkeyManager? _hotkeyManager;
    private FloatingToolbar? _floatingToolbar;
    private CancellationTokenSource? _sessionCts;
    private RecordingState _state;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        _recorderService = new ScreenStudioRecorderService();
        _recordPage = new RecordPage();
        _processingPage = new ProcessingPage();
        _resultsPage = new ResultsPage();

        _recordPage.StartRequested += RecordPage_StartRequested;
        _resultsPage.NewRecordingRequested += ResultsPage_NewRecordingRequested;
        RootFrame.Content = _recordPage;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        WindowHelper.ConfigureRecordWindow(this);

        try
        {
            var hwnd = WindowHelper.GetWindowHandle(this);
            _hotkeyManager = new HotkeyManager(hwnd, DispatcherQueue);
            _hotkeyManager.Register(StartStopHotkeyId, HotKeyModifiers.Control | HotKeyModifiers.Shift, VkR, () => _ = RunSafeAsync(HandleStartStopHotkeyAsync));
            _hotkeyManager.Register(PauseHotkeyId, HotKeyModifiers.Control | HotKeyModifiers.Shift, VkP, () => _ = RunSafeAsync(HandlePauseHotkeyAsync));
            _hotkeyManager.Register(CancelHotkeyId, HotKeyModifiers.None, VkEscape, () => _ = RunSafeAsync(HandleCancelHotkeyAsync));
        }
        catch (Exception ex)
        {
            _recordPage.SetStatus($"Hotkeys unavailable: {ex.Message}");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _resultsPage.UnloadPreview();
        _floatingToolbar?.Close();
        _hotkeyManager?.Dispose();
        _sessionCts?.Dispose();
    }

    private void RecordPage_StartRequested(object? sender, RecordRequestedEventArgs e)
    {
        _ = RunSafeAsync(() => StartRecordingAsync(e.Options));
    }

    private async Task HandleStartStopHotkeyAsync()
    {
        if (_state == RecordingState.Idle)
        {
            await StartRecordingAsync(_recordPage.GetCurrentOptions()).ConfigureAwait(true);
            return;
        }

        if (_state == RecordingState.Recording)
        {
            await StopAndProcessAsync().ConfigureAwait(true);
        }
    }

    private async Task HandlePauseHotkeyAsync()
    {
        if (_state != RecordingState.Recording)
        {
            return;
        }

        var paused = await _recorderService.TogglePauseAsync(CancellationToken.None).ConfigureAwait(true);
        _floatingToolbar?.SetPaused(paused);
    }

    private async Task HandleCancelHotkeyAsync()
    {
        if (_state == RecordingState.Starting || _state == RecordingState.Recording)
        {
            await CancelRecordingAsync().ConfigureAwait(true);
        }
    }

    private async Task StartRecordingAsync(RecordingOptions options)
    {
        await _stateLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_state != RecordingState.Idle)
            {
                return;
            }

            if (string.Equals(options.CaptureTarget, "Window", StringComparison.OrdinalIgnoreCase)
                && options.SelectedWindowHandle == nint.Zero)
            {
                throw new InvalidOperationException("Select a window before starting a window capture.");
            }

            _state = RecordingState.Starting;
            _sessionCts = new CancellationTokenSource();
            _recordPage.SetBusy(true);
            _recordPage.SetStatus("Preparing recorder...");
            EnsureFloatingToolbar();

            _floatingToolbar!.Activate();
            var avoidHandle = string.Equals(options.CaptureTarget, "Window", StringComparison.OrdinalIgnoreCase)
                ? options.SelectedWindowHandle
                : nint.Zero;
            WindowHelper.ConfigureFloatingToolbar(_floatingToolbar, avoidHandle);
            WindowHelper.GetAppWindow(this).Hide();

            // Run countdown for all capture types — user needs visual feedback before recording starts
            await _floatingToolbar.RunCountdownAsync(3, _sessionCts.Token).ConfigureAwait(true);

            await _recorderService.StartAsync(options, _sessionCts.Token).ConfigureAwait(true);
            _floatingToolbar.StartClock();

            _state = RecordingState.Recording;
        }
        catch (OperationCanceledException)
        {
            await RecoverToIdleAsync("Recording cancelled.").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await RecoverToIdleAsync($"Failed to start recording: {ex.Message}").ConfigureAwait(true);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task StopAndProcessAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_state != RecordingState.Recording)
            {
                return;
            }

            _state = RecordingState.Processing;
            CloseFloatingToolbar();

            WindowHelper.GetAppWindow(this).Show();
            Activate();
            RootFrame.Content = _processingPage;
            _processingPage.SetIndeterminate("Processing recording...", "Processing zoom and cursor metadata...");

            var result = await _recorderService.StopAsync(CancellationToken.None).ConfigureAwait(true);
            _processingPage.SetProgress(100, "Processing complete.");
            await Task.Delay(350).ConfigureAwait(true);

            WindowHelper.ConfigureRecordWindow(this);
            _resultsPage.LoadResult(result);
            RootFrame.Content = _resultsPage;
            _recordPage.SetBusy(false);
            _recordPage.SetStatus("Recording ready.");
            _sessionCts?.Dispose();
            _sessionCts = null;
            _state = RecordingState.Idle;
        }
        catch (Exception ex)
        {
            await RecoverToIdleAsync($"Failed to stop recording: {ex.Message}").ConfigureAwait(true);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task CancelRecordingAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_state != RecordingState.Starting && _state != RecordingState.Recording)
            {
                return;
            }

            _sessionCts?.Cancel();
            if (_state == RecordingState.Recording)
            {
                await _recorderService.CancelAsync(CancellationToken.None).ConfigureAwait(true);
            }

            await RecoverToIdleAsync("Recording discarded.").ConfigureAwait(true);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void EnsureFloatingToolbar()
    {
        if (_floatingToolbar is not null)
        {
            return;
        }

        _floatingToolbar = new FloatingToolbar();
        _floatingToolbar.CancelRequested += FloatingToolbar_CancelRequested;
        _floatingToolbar.StopRequested += FloatingToolbar_StopRequested;
        _floatingToolbar.PauseToggleRequested += FloatingToolbar_PauseToggleRequested;
    }

    private async void FloatingToolbar_CancelRequested(object? sender, EventArgs e)
    {
        await RunSafeAsync(CancelRecordingAsync).ConfigureAwait(true);
    }

    private async void FloatingToolbar_StopRequested(object? sender, EventArgs e)
    {
        await RunSafeAsync(StopAndProcessAsync).ConfigureAwait(true);
    }

    private async void FloatingToolbar_PauseToggleRequested(object? sender, EventArgs e)
    {
        await RunSafeAsync(HandlePauseHotkeyAsync).ConfigureAwait(true);
    }

    private void CloseFloatingToolbar()
    {
        if (_floatingToolbar is null)
        {
            return;
        }

        _floatingToolbar.CancelRequested -= FloatingToolbar_CancelRequested;
        _floatingToolbar.StopRequested -= FloatingToolbar_StopRequested;
        _floatingToolbar.PauseToggleRequested -= FloatingToolbar_PauseToggleRequested;
        _floatingToolbar.Close();
        _floatingToolbar = null;
    }

    private async Task RecoverToIdleAsync(string status)
    {
        _resultsPage.UnloadPreview();
        CloseFloatingToolbar();
        WindowHelper.GetAppWindow(this).Show();
        Activate();
        WindowHelper.ConfigureRecordWindow(this);
        RootFrame.Content = _recordPage;
        _recordPage.SetBusy(false);
        _recordPage.SetStatus(status);
        _sessionCts?.Dispose();
        _sessionCts = null;
        _state = RecordingState.Idle;
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await RecoverToIdleAsync($"Unexpected error: {ex.Message}").ConfigureAwait(true);
        }
    }

    private void ResultsPage_NewRecordingRequested(object? sender, EventArgs e)
    {
        WindowHelper.ConfigureRecordWindow(this);
        RootFrame.Content = _recordPage;
        _recordPage.SetBusy(false);
        _recordPage.SetStatus("Ready for a new recording.");
    }

    private enum RecordingState
    {
        Idle,
        Starting,
        Recording,
        Processing
    }
}
