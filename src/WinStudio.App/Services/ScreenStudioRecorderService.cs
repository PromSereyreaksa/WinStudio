using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinStudio.App.Models;
using WinStudio.Common;
using WinStudio.Processing;

namespace WinStudio.App.Services;

public sealed class ScreenStudioRecorderService : IScreenStudioRecorderService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int CaptureMapTolerancePixels = 24;

    private readonly object _sync = new();
    private readonly List<CursorEvent> _cursorEvents = [];
    private readonly ConcurrentQueue<PendingInputEvent> _pendingInputEvents = new();
    private readonly ZoomRegionGenerator _zoomGenerator = new();
    private readonly CursorSmoother _cursorSmoother = new();
    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly Stopwatch _recordingClock = new();

    private Process? _ffmpegProcess;
    private CancellationTokenSource? _cursorCaptureCts;
    private Task? _cursorCaptureTask;
    private Task? _inputProcessingTask;
    private Task? _ffmpegOutputTask;
    private Task? _ffmpegErrorTask;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private string? _rawVideoPath;
    private string? _processedVideoPath;
    private string? _recordingLogPath;
    private string? _processingLogPath;
    private float _zoomIntensity = 1.4f;
    private float _zoomSensitivity = 1.2f;
    private float _followSpeed = 1.15f;
    private BackgroundSettings _background = BackgroundSettings.None;
    private long _startTicks;
    private long _pauseStartedTicks;
    private long _accumulatedPausedTicks;
    private bool _recording;
    private bool _paused;
    private float _lastRelativeX;
    private float _lastRelativeY;
    private bool _hasLastPoint;
    private POINT _lastHookPoint;
    private bool _hasLastHookPoint;
    private long _lastHookKeyPressTicks;
    private CaptureBounds _captureBounds;
    private CaptureCoordinateTransform _captureTransform;

    // Cached module handle — required for hooks to work reliably on 64-bit .NET.
    // Passing IntPtr.Zero causes silent hook installation failure on many systems.
    private static readonly IntPtr HookModuleHandle = GetModuleHandle(null);

    public bool IsRecording => _recording;

    public bool IsPaused => _paused;

    public ScreenStudioRecorderService()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public Task StartAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        if (_recording)
        {
            throw new InvalidOperationException("A recording session is already running.");
        }

        EnsureFfmpegIsAvailable();

        var outputDirectory = BuildOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var sessionPrefix = $"recording-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        (_captureBounds, var physicalBounds) = ResolveCaptureBounds(options);
        
        // For gdigrab with -offset_x/Y, the captured video starts at (0,0) but represents
        // the region at offset (X, Y) on screen. We need to subtract the window origin
        // from screen coordinates to get position within the captured frame.
        // Content dimensions match capture dimensions (no additional scaling needed).
        System.Diagnostics.Debug.WriteLine($"[ZOOM] Capture bounds: logical=({_captureBounds.X},{_captureBounds.Y}) {_captureBounds.Width}x{_captureBounds.Height}, physical=({physicalBounds.X},{physicalBounds.Y}) {physicalBounds.Width}x{physicalBounds.Height}");
        
        _captureTransform = new CaptureCoordinateTransform(
            physicalBounds.X,
            physicalBounds.Y,
            physicalBounds.Width,
            physicalBounds.Height,
            _captureBounds.Width,
            _captureBounds.Height
        );
        _rawVideoPath = Path.Combine(outputDirectory, $"{sessionPrefix}.raw.mp4");
        _processedVideoPath = Path.Combine(outputDirectory, $"{sessionPrefix}.processed.mp4");
        _recordingLogPath = Path.Combine(outputDirectory, $"{sessionPrefix}.recording.ffmpeg.log");
        _processingLogPath = Path.Combine(
            outputDirectory,
            $"{sessionPrefix}.processing.ffmpeg.log"
        );
        _zoomIntensity = options.ZoomIntensity;
        _zoomSensitivity = options.ZoomSensitivity;
        _followSpeed = options.FollowSpeed;
        _background = options.Background ?? BackgroundSettings.None;
        _pauseStartedTicks = 0;
        _accumulatedPausedTicks = 0;
        _recording = true;
        _paused = false;
        _hasLastPoint = false;
        _hasLastHookPoint = false;
        _lastHookKeyPressTicks = 0;

        lock (_sync)
        {
            _cursorEvents.Clear();
        }

        while (_pendingInputEvents.TryDequeue(out _)) { }

        // Start FFmpeg FIRST, then start the recording clock and hooks.
        // This aligns cursor event timestamps with the FFmpeg video timeline,
        // preventing temporal offset where zoom/pan actions appear too early or
        // too late in the processed output.
        (_ffmpegProcess, _ffmpegOutputTask, _ffmpegErrorTask) = StartFfmpegProcess(
            _rawVideoPath,
            _recordingLogPath,
            options.FramesPerSecond,
            options,
            _captureBounds
        );

        _startTicks = DateTime.UtcNow.Ticks;
        _recordingClock.Restart();

        InstallInputHooks();
        _cursorCaptureCts = new CancellationTokenSource();
        _inputProcessingTask = ProcessPendingInputEventsLoopAsync(_cursorCaptureCts.Token);
        _cursorCaptureTask = CaptureKeyboardFallbackLoopAsync(_cursorCaptureCts.Token);

        return Task.CompletedTask;
    }

    public async Task<bool> TogglePauseAsync(CancellationToken cancellationToken)
    {
        if (!_recording || _ffmpegProcess is null || _ffmpegProcess.HasExited)
        {
            throw new InvalidOperationException("No active recording session.");
        }

        await _ffmpegProcess.StandardInput.WriteLineAsync("p").ConfigureAwait(false);
        await _ffmpegProcess.StandardInput.FlushAsync().ConfigureAwait(false);

        if (_paused)
        {
            _paused = false;
            if (_pauseStartedTicks > 0)
            {
                _accumulatedPausedTicks += Math.Max(0, DateTime.UtcNow.Ticks - _pauseStartedTicks);
            }

            _pauseStartedTicks = 0;
        }
        else
        {
            _paused = true;
            _pauseStartedTicks = DateTime.UtcNow.Ticks;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _paused;
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (!_recording)
        {
            return;
        }

        await StopCursorCaptureAsync().ConfigureAwait(false);

        if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
        {
            _ffmpegProcess.Kill(true);
            await _ffmpegProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        await AwaitFfmpegLogTasksAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_rawVideoPath) && File.Exists(_rawVideoPath))
        {
            File.Delete(_rawVideoPath);
        }

        if (!string.IsNullOrWhiteSpace(_processedVideoPath) && File.Exists(_processedVideoPath))
        {
            File.Delete(_processedVideoPath);
        }

        ResetSessionState();
    }

    public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken)
    {
        if (
            !_recording
            || _ffmpegProcess is null
            || _rawVideoPath is null
            || _processedVideoPath is null
        )
        {
            throw new InvalidOperationException("No active recording session.");
        }

        _recording = false;

        await StopCursorCaptureAsync().ConfigureAwait(false);
        await StopFfmpegProcessAsync(_ffmpegProcess, cancellationToken).ConfigureAwait(false);
        await AwaitFfmpegLogTasksAsync().ConfigureAwait(false);

        var endTicks = DateTime.UtcNow.Ticks;
        if (_paused && _pauseStartedTicks > 0)
        {
            _accumulatedPausedTicks += Math.Max(0, endTicks - _pauseStartedTicks);
            _paused = false;
            _pauseStartedTicks = 0;
        }

        var cursorEvents = SnapshotCursorEvents();
        var probedVideoSize = await ProbeVideoFrameSizeAsync(_rawVideoPath, cancellationToken)
            .ConfigureAwait(false);
        var processingWidth = _captureBounds.Width;
        var processingHeight = _captureBounds.Height;
        if (probedVideoSize is { } frameSize && frameSize.Width > 1 && frameSize.Height > 1)
        {
            processingWidth = frameSize.Width;
            processingHeight = frameSize.Height;
        }

        var mappedCursorEvents = MapCursorEventsToVideoSpace(
            cursorEvents,
            _captureBounds.Width,
            _captureBounds.Height,
            processingWidth,
            processingHeight
        );

        System.Diagnostics.Debug.WriteLine(
            $"[ZOOM] {cursorEvents.Count} cursor events, capture={_captureBounds.Width}x{_captureBounds.Height}, video={processingWidth}x{processingHeight}"
        );
        foreach (var e in cursorEvents.Take(20))
        {
            System.Diagnostics.Debug.WriteLine($"  {e.EventType} @ ({e.X:F1}, {e.Y:F1})");
        }

        var smoothedCursorEvents = _cursorSmoother.Smooth(mappedCursorEvents);
        var zoomKeyframes = _zoomGenerator.Generate(
            smoothedCursorEvents,
            processingWidth,
            processingHeight,
            _zoomIntensity,
            _zoomSensitivity,
            _followSpeed
        );
        
        System.Diagnostics.Debug.WriteLine($"[ZOOM] Generated {zoomKeyframes.Count} keyframes");
        foreach (var kf in zoomKeyframes.Take(10))
        {
            System.Diagnostics.Debug.WriteLine($"  [{TimeSpan.FromTicks(kf.StartTicks).TotalSeconds:F2}-{TimeSpan.FromTicks(kf.EndTicks).TotalSeconds:F2}] rect=({kf.TargetRect.X:F0},{kf.TargetRect.Y:F0} {kf.TargetRect.Width:F0}x{kf.TargetRect.Height:F0})");
        }

        var cursorLogPath = Path.ChangeExtension(_rawVideoPath, ".cursor.json");
        var zoomPath = Path.ChangeExtension(_rawVideoPath, ".zoom.json");

        await WriteJsonAsync(cursorLogPath, cursorEvents, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(zoomPath, zoomKeyframes, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(_rawVideoPath))
        {
            throw new InvalidOperationException(BuildMissingRawVideoMessage());
        }

        string? processingError = null;
        try
        {
            await ApplyAutoZoomAsync(
                    _rawVideoPath,
                    _processedVideoPath,
                    _processingLogPath,
                    zoomKeyframes,
                    new CaptureBounds(_captureBounds.X, _captureBounds.Y, processingWidth, processingHeight),
                    _startTicks,
                    processingWidth,
                    processingHeight,
                    _background,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (File.Exists(_rawVideoPath))
        {
            processingError = ex.Message;
            File.Copy(_rawVideoPath, _processedVideoPath, overwrite: true);
        }

        var elapsedTicks = Math.Max(0, endTicks - _startTicks - _accumulatedPausedTicks);
        var result = new RecordingResult(
            _rawVideoPath,
            _processedVideoPath,
            cursorLogPath,
            zoomPath,
            cursorEvents.Count,
            zoomKeyframes.Count,
            TimeSpan.FromTicks(elapsedTicks),
            processingError
        );

        ResetSessionState();
        return result;
    }

    private async Task StopCursorCaptureAsync()
    {
        UninstallInputHooks();
        DrainPendingInputEvents();

        if (_cursorCaptureCts is not null)
        {
            await _cursorCaptureCts.CancelAsync().ConfigureAwait(false);
        }

        if (_inputProcessingTask is not null)
        {
            try
            {
                await _inputProcessingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        if (_cursorCaptureTask is not null)
        {
            try
            {
                await _cursorCaptureTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        DrainPendingInputEvents();
    }

    private async Task AwaitFfmpegLogTasksAsync()
    {
        if (_ffmpegOutputTask is not null)
        {
            await _ffmpegOutputTask.ConfigureAwait(false);
        }

        if (_ffmpegErrorTask is not null)
        {
            await _ffmpegErrorTask.ConfigureAwait(false);
        }
    }

    private string BuildMissingRawVideoMessage()
    {
        var builder = new StringBuilder("Recording did not produce a raw MP4 file.");
        if (!string.IsNullOrWhiteSpace(_recordingLogPath))
        {
            builder.Append(" Check FFmpeg log: ").Append(_recordingLogPath);
        }

        return builder.ToString();
    }

    private void ResetSessionState()
    {
        _recording = false;
        _paused = false;
        UninstallInputHooks();
        _cursorCaptureCts?.Dispose();
        _cursorCaptureCts = null;
        _cursorCaptureTask = null;
        _inputProcessingTask = null;
        _ffmpegOutputTask = null;
        _ffmpegErrorTask = null;
        _ffmpegProcess = null;
        _rawVideoPath = null;
        _processedVideoPath = null;
        _recordingLogPath = null;
        _processingLogPath = null;
        _startTicks = 0;
        _recordingClock.Reset();
        _pauseStartedTicks = 0;
        _accumulatedPausedTicks = 0;
        _lastHookKeyPressTicks = 0;
        _background = BackgroundSettings.None;
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T data,
        CancellationToken cancellationToken
    )
    {
        await using var stream = File.Create(path);
        await JsonSerializer
            .SerializeAsync(
                stream,
                data,
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static string BuildOutputDirectory()
    {
        var videosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(videosPath))
        {
            return Path.Combine(videosPath, "WinStudio");
        }

        return Path.Combine(AppContext.BaseDirectory, "output");
    }

    private static void EnsureFfmpegIsAvailable()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Could not start ffmpeg.");
            }

            process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "FFmpeg is required for recording. Install FFmpeg and ensure `ffmpeg` is available on PATH.",
                ex
            );
        }
    }

    // Returns (logical bounds for gdigrab/zoom, physical bounds for mouse-coordinate mapping).
    // DwmGetWindowAttribute returns physical pixels, so on scaled displays we must convert to
    // logical GDI pixels that gdigrab and GetSystemMetrics operate in.
    private static (CaptureBounds Logical, CaptureBounds Physical) ResolveCaptureBounds(RecordingOptions options)
    {
        var isWindow = string.Equals(
            options.CaptureTarget,
            "Window",
            StringComparison.OrdinalIgnoreCase
        );
        if (!isWindow)
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = MakeEven(Math.Max(2, GetSystemMetrics(SmCxVirtualScreen)));
            var height = MakeEven(Math.Max(2, GetSystemMetrics(SmCyVirtualScreen)));
            var fullscreen = new CaptureBounds(x, y, width, height);
            // Fullscreen always uses logical metrics — physical == logical here.
            return (fullscreen, fullscreen);
        }

        var hwnd = options.SelectedWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "No stored target window. Select a window to record and try again."
            );
        }

        if (!IsWindow(hwnd))
        {
            throw new InvalidOperationException(
                "The selected window is no longer available. Refresh the list and select it again."
            );
        }

        if (IsIconic(hwnd))
        {
            throw new InvalidOperationException(
                "The selected window is minimized. Restore it before recording."
            );
        }

        if (!TryGetWindowCaptureRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("Could not read the selected window bounds.");
        }

        // Preserve true window X/width; only fill the vertical taskbar gap for a
        // maximized work-area window when horizontal edges already match monitor.
        if (IsZoomed(hwnd) && TryGetMonitorBoundsForWindow(hwnd, out var monitorRect))
        {
            var leftAligned = Math.Abs(rect.Left - monitorRect.Left) <= 2;
            var rightAligned = Math.Abs(rect.Right - monitorRect.Right) <= 2;
            var hasBottomGap = monitorRect.Bottom - rect.Bottom is > 0 and <= 160;
            if (leftAligned && rightAligned && hasBottomGap)
            {
                rect.Top = monitorRect.Top;
                rect.Bottom = monitorRect.Bottom;
            }
        }

        rect = ClampToVirtualScreen(rect);

        var physW = rect.Right - rect.Left;
        var physH = rect.Bottom - rect.Top;
        if (physW < 16 || physH < 16)
        {
            throw new InvalidOperationException("Selected window is too small to capture.");
        }

        // Physical bounds (for mouse-hook coordinate mapping — MSLLHOOKSTRUCT uses physical pixels).
        var physical = new CaptureBounds(rect.Left, rect.Top, MakeEven(physW), MakeEven(physH));

        // Convert physical → logical for gdigrab, which uses GDI (logical) pixel coordinates.
        // GetDpiForWindow returns 96 at 100 % scale, 120 at 125 %, 144 at 150 %, etc.
        var dpi = (float)Math.Max(1, GetDpiForWindow(hwnd));
        var scale = 96f / dpi;
        // Keep the full physical window in view after DPI conversion:
        // floor the origin and ceil the far edge to avoid trimming the right/bottom edge.
        var logLeft = (int)MathF.Floor(rect.Left * scale);
        var logTop = (int)MathF.Floor(rect.Top * scale);
        var logRight = (int)MathF.Ceiling(rect.Right * scale);
        var logBottom = (int)MathF.Ceiling(rect.Bottom * scale);
        var logW = MakeEven(Math.Max(2, logRight - logLeft));
        var logH = MakeEven(Math.Max(2, logBottom - logTop));
        var logical = new CaptureBounds(
            logLeft,
            logTop,
            logW,
            logH);

        return (logical, physical);
    }

    private static bool TryGetWindowCaptureRect(IntPtr hwnd, out RECT rect)
    {
        if (
            DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>())
            == 0
        )
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool TryGetMonitorBoundsForWindow(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf<MONITORINFO>();
        if (!GetMonitorInfo(hMonitor, ref info))
        {
            return false;
        }

        rect = info.rcMonitor;
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static RECT ClampToVirtualScreen(RECT rect)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        var right = left + Math.Max(1, width);
        var bottom = top + Math.Max(1, height);

        rect.Left = Math.Clamp(rect.Left, left, right - 1);
        rect.Top = Math.Clamp(rect.Top, top, bottom - 1);
        rect.Right = Math.Clamp(rect.Right, rect.Left + 1, right);
        rect.Bottom = Math.Clamp(rect.Bottom, rect.Top + 1, bottom);

        return rect;
    }

    private static int MakeEven(int value)
    {
        var evenValue = value % 2 == 0 ? value : value - 1;
        return Math.Max(2, evenValue);
    }

    private static (Process Process, Task OutputTask, Task ErrorTask) StartFfmpegProcess(
        string outputPath,
        string? logPath,
        int framesPerSecond,
        RecordingOptions options,
        CaptureBounds bounds
    )
    {
        var fps = Math.Clamp(framesPerSecond, 15, 60);
        var arguments = BuildFfmpegArguments(outputPath, fps, options, bounds);
        WriteRecordingLogPreamble(logPath, options, bounds, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg recording process.");

        var outputTask = PumpReaderToLogAsync(process.StandardOutput, logPath, "stdout");
        var errorTask = PumpReaderToLogAsync(process.StandardError, logPath, "stderr");

        return (process, outputTask, errorTask);
    }

    private static string BuildFfmpegArguments(
        string outputPath,
        int framesPerSecond,
        RecordingOptions options,
        CaptureBounds bounds
    )
    {
        _ = options;
        var captureInputArguments = BuildDesktopCaptureArguments(bounds);
        return $"-y -f gdigrab -framerate {framesPerSecond} {captureInputArguments} -an -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{outputPath}\"";
    }

    private static string BuildDesktopCaptureArguments(CaptureBounds bounds)
    {
        return $"-offset_x {bounds.X} -offset_y {bounds.Y} -video_size {bounds.Width}x{bounds.Height} -draw_mouse 1 -i desktop";
    }

    private static void WriteRecordingLogPreamble(
        string? logPath,
        RecordingOptions options,
        CaptureBounds bounds,
        string arguments
    )
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"[app] captureTarget={options.CaptureTarget}");
        builder.AppendLine($"[app] selectedWindowHandle={options.SelectedWindowHandle}");

        if (!string.IsNullOrWhiteSpace(options.SelectedWindowTitle))
        {
            builder.AppendLine($"[app] selectedWindowTitle={options.SelectedWindowTitle}");
        }

        builder.AppendLine(
            $"[app] resolvedBounds={bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}"
        );
        builder.AppendLine($"[app] ffmpegArgs={arguments}");
        builder.AppendLine();

        File.WriteAllText(logPath, builder.ToString());
    }

    private static async Task StopFfmpegProcessAsync(
        Process process,
        CancellationToken cancellationToken
    )
    {
        if (process.HasExited)
        {
            return;
        }

        await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
    }

    private static async Task ApplyAutoZoomAsync(
        string inputPath,
        string outputPath,
        string? logPath,
        IReadOnlyList<ZoomKeyframe> zoomKeyframes,
        CaptureBounds bounds,
        long sessionStartTicks,
        int outputWidth,
        int outputHeight,
        BackgroundSettings background,
        CancellationToken cancellationToken
    )
    {
        var inputDurationSeconds = await ProbeVideoDurationSecondsAsync(
                inputPath,
                cancellationToken
            )
            .ConfigureAwait(false);
        var segments = BuildSegments(
            zoomKeyframes,
            bounds,
            sessionStartTicks,
            inputDurationSeconds
        );
        System.Diagnostics.Debug.WriteLine($"[ZOOM] Built {segments.Count} segments:");
        foreach (var seg in segments.Take(10))
        {
            System.Diagnostics.Debug.WriteLine($"  [{seg.StartSeconds:F2}-{seg.EndSeconds:F2}] x={seg.X}, y={seg.Y}, size={seg.Width}x{seg.Height}");
        }
        if (segments.Count == 0)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var filterScriptPath = Path.ChangeExtension(outputPath, ".filter.txt");

        var safeOutWidth = MakeEven(Math.Max(2, outputWidth));
        var safeOutHeight = MakeEven(Math.Max(2, outputHeight));
        var renderSegments = BuildRenderSegments(
            segments,
            bounds.Width,
            bounds.Height,
            inputDurationSeconds
        );
        if (renderSegments.Count == 0)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var filterPlan = BuildFilterExpressionPlan(renderSegments);
        var xExpr = filterPlan.XExpression;
        var yExpr = filterPlan.YExpression;
        var zoomExpr = filterPlan.ZoomExpression;

        var scaledWidthExpr = $"max({safeOutWidth},trunc(iw*({zoomExpr})/2)*2)";
        var scaledHeightExpr = $"max({safeOutHeight},trunc(ih*({zoomExpr})/2)*2)";
        var overlayXExpr =
            $"-min(max(0,({xExpr})*({zoomExpr})),overlay_w-{safeOutWidth})";
        var overlayYExpr =
            $"-min(max(0,({yExpr})*({zoomExpr})),overlay_h-{safeOutHeight})";

        string filterGraph;
        var useBackground = background.Mode != BackgroundMode.None;
        if (useBackground)
        {
            // Compute inset dimensions — the captured content occupies (outW - 2*pad) × (outH - 2*pad)
            // and the chosen background fills the border around it.
            var pad = MakeEven((int)Math.Round(
                background.PaddingFraction * Math.Min(safeOutWidth, safeOutHeight)));
            var innerW = Math.Max(2, MakeEven(safeOutWidth - 2 * pad));
            var innerH = Math.Max(2, MakeEven(safeOutHeight - 2 * pad));

            // Scale the zoom factor down so that the same source region is visible
            // but occupies the smaller inner area instead of the full output frame.
            var innerScale = (double)innerW / safeOutWidth;
            var bgZoomExpr = $"({zoomExpr})*{innerScale.ToString("F6", CultureInfo.InvariantCulture)}";
            var scaledWidthBgExpr = $"max({innerW},trunc(iw*({bgZoomExpr})/2)*2)";
            var scaledHeightBgExpr = $"max({innerH},trunc(ih*({bgZoomExpr})/2)*2)";
            var overlayXBgExpr =
                $"-min(max(0,({xExpr})*({bgZoomExpr})),overlay_w-{innerW})";
            var overlayYBgExpr =
                $"-min(max(0,({yExpr})*({bgZoomExpr})),overlay_h-{innerH})";

            string bgSourceFilter;
            if (background.Mode == BackgroundMode.Image
                && !string.IsNullOrWhiteSpace(background.ImagePath)
                && File.Exists(background.ImagePath))
            {
                // [1:v] is the image input — added to the ffmpeg ArgumentList below.
                bgSourceFilter =
                    $"[1:v]scale={safeOutWidth}:{safeOutHeight}:force_original_aspect_ratio=increase" +
                    $",crop={safeOutWidth}:{safeOutHeight},setsar=1[bg]";
            }
            else
            {
                // Solid colour background via lavfi color source.
                var colorHex = background.ColorHex.TrimStart('#');
                bgSourceFilter =
                    $"color=c=0x{colorHex}:size={safeOutWidth}x{safeOutHeight},setsar=1[bg]";
            }

            filterGraph =
                $"{bgSourceFilter};" +
                $"color=c=black:size={innerW}x{innerH},setsar=1[winbase];" +
                $"[0:v]scale=w='{scaledWidthBgExpr}':h='{scaledHeightBgExpr}':eval=frame:flags=bicubic[scaledwin];" +
                $"[winbase][scaledwin]overlay=x='{overlayXBgExpr}':y='{overlayYBgExpr}':eval=frame:shortest=1[win];" +
                // shortest=1: stop when the finite video input ([win]) ends,
                // preventing the infinite color= or looped-image stream from
                // causing ffmpeg to run forever.
                $"[bg][win]overlay={pad}:{pad}:eval=init:shortest=1[v]";
        }
        else
        {
            filterGraph =
                $"color=c=black:size={safeOutWidth}x{safeOutHeight},setsar=1[base];" +
                $"[0:v]scale=w='{scaledWidthExpr}':h='{scaledHeightExpr}':eval=frame:flags=bicubic[scaled];" +
                $"[base][scaled]overlay=x='{overlayXExpr}':y='{overlayYExpr}':eval=frame:shortest=1[v]";
        }
        
        await File.WriteAllTextAsync(filterScriptPath, filterGraph, cancellationToken).ConfigureAwait(false);

        // Use ArgumentList so Windows does not re-parse or escape the arguments.
        // -/filter_complex <file> is the FFmpeg 8.x way to pass a filter via file.
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);

        // When using an image background, add it as the second video input ([1:v]).
        // -loop 1 makes the still image an infinite stream; -shortest stops encoding
        // as soon as the video input ([0:v]) finishes — without it ffmpeg runs forever.
        var useImageBackground = background.Mode == BackgroundMode.Image
            && !string.IsNullOrWhiteSpace(background.ImagePath)
            && File.Exists(background.ImagePath);
        if (useImageBackground)
        {
            startInfo.ArgumentList.Add("-loop");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(background.ImagePath!);
        }

        startInfo.ArgumentList.Add("-/filter_complex");
        startInfo.ArgumentList.Add(filterScriptPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("[v]");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("ultrafast");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        if (useBackground)
        {
            // Stop encoding when the shortest stream ends. Both the looped image and
            // the color= lavfi source generate infinite streams; without this flag
            // ffmpeg runs forever waiting for them to end.
            startInfo.ArgumentList.Add("-shortest");
        }
        startInfo.ArgumentList.Add(outputPath);

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg post-processing.");

        var outputTask = PumpReaderToLogAsync(process.StandardOutput, logPath, "stdout");
        var errorTask = PumpReaderToLogAsync(process.StandardError, logPath, "stderr");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var logHint = string.IsNullOrWhiteSpace(logPath)
                ? string.Empty
                : $" Check FFmpeg log: {logPath}";
            throw new InvalidOperationException($"FFmpeg post-processing failed.{logHint}");
        }
    }

    private static async Task PumpReaderToLogAsync(
        StreamReader reader,
        string? logPath,
        string streamName
    )
    {
        // Read all lines into memory first — calling File.AppendAllTextAsync per line
        // opens and flushes the file hundreds of times per second, which is the primary
        // cause of the "stuck after recording" slowdown.
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            sb.Append('[').Append(streamName).Append("] ").AppendLine(line);
        }

        if (string.IsNullOrWhiteSpace(logPath) || sb.Length == 0)
        {
            return;
        }

        await File.AppendAllTextAsync(logPath, sb.ToString()).ConfigureAwait(false);
    }

    private static List<ZoomSegment> BuildSegments(
        IReadOnlyList<ZoomKeyframe> zoomKeyframes,
        CaptureBounds bounds,
        long sessionStartTicks,
        double? inputDurationSeconds
    )
    {
        const int maxSegmentsForFilter = 768;
        var segments = new List<ZoomSegment>();
        var durationLimit =
            inputDurationSeconds.HasValue && inputDurationSeconds.Value > 0
                ? inputDurationSeconds.Value
                : double.PositiveInfinity;

        foreach (var keyframe in zoomKeyframes)
        {
            if (keyframe.EndTicks <= keyframe.StartTicks)
            {
                continue;
            }

            var startSeconds = TimeSpan
                .FromTicks(Math.Max(0, keyframe.StartTicks - sessionStartTicks))
                .TotalSeconds;
            var endSeconds = TimeSpan
                .FromTicks(Math.Max(0, keyframe.EndTicks - sessionStartTicks))
                .TotalSeconds;
            if (startSeconds >= durationLimit)
            {
                continue;
            }

            endSeconds = Math.Min(endSeconds, durationLimit);
            if (endSeconds <= startSeconds)
            {
                continue;
            }

            var normalized = NormalizeRectToAspect(
                keyframe.TargetRect,
                bounds.Width,
                bounds.Height
            );
            var width = MakeEven((int)Math.Round(normalized.Width));
            var height = MakeEven((int)Math.Round(normalized.Height));
            var x = (int)Math.Round(normalized.X);
            var y = (int)Math.Round(normalized.Y);

            if (width >= bounds.Width && height >= bounds.Height)
            {
                continue;
            }

            segments.Add(
                new ZoomSegment(
                    Math.Max(0, startSeconds),
                    Math.Max(0, endSeconds),
                    x,
                    y,
                    width,
                    height,
                    bounds.Width,
                    bounds.Height
                )
            );
        }

        segments = MergeAdjacentSegments(segments);
        if (segments.Count <= maxSegmentsForFilter)
        {
            return segments;
        }

        return ReduceSegmentsPreservingCoverage(segments, maxSegmentsForFilter);
    }

    private static List<ZoomSegment> BuildRenderSegments(
        IReadOnlyList<ZoomSegment> zoomSegments,
        int fullWidth,
        int fullHeight,
        double? inputDurationSeconds
    )
    {
        var durationLimit =
            inputDurationSeconds.HasValue && inputDurationSeconds.Value > 0
                ? inputDurationSeconds.Value
            : zoomSegments.Count > 0 ? zoomSegments.Max(static s => s.EndSeconds)
            : 0d;

        if (durationLimit <= 0d)
        {
            return [];
        }

        var fullFrame = new ZoomSegment(0d, 0d, 0, 0, fullWidth, fullHeight, fullWidth, fullHeight);
        var renderSegments = new List<ZoomSegment>();
        var cursor = 0d;

        foreach (var zoomSegment in zoomSegments.OrderBy(static s => s.StartSeconds))
        {
            var start = Math.Clamp(zoomSegment.StartSeconds, 0d, durationLimit);
            var end = Math.Clamp(zoomSegment.EndSeconds, start, durationLimit);
            if (end <= start)
            {
                continue;
            }

            if (start > cursor + 0.0005d)
            {
                renderSegments.Add(fullFrame with { StartSeconds = cursor, EndSeconds = start });
            }

            var maxX = Math.Max(0, fullWidth - zoomSegment.Width);
            var maxY = Math.Max(0, fullHeight - zoomSegment.Height);
            renderSegments.Add(
                new ZoomSegment(
                    start,
                    end,
                    Math.Clamp(zoomSegment.X, 0, maxX),
                    Math.Clamp(zoomSegment.Y, 0, maxY),
                    zoomSegment.Width,
                    zoomSegment.Height,
                    fullWidth,
                    fullHeight
                )
            );

            cursor = end;
        }

        if (cursor < durationLimit - 0.0005d)
        {
            renderSegments.Add(
                fullFrame with
                {
                    StartSeconds = cursor,
                    EndSeconds = durationLimit,
                }
            );
        }

        return MergeAdjacentSegments(renderSegments);
    }

    private static FilterExpressionPlan BuildFilterExpressionPlan(
        IReadOnlyList<ZoomSegment> renderSegments
    )
    {
        // The flat-sum expression builder is much cheaper for FFmpeg to evaluate than
        // the older nested-if tree, so bias toward preserving motion detail and only
        // reduce aggressively on very long recordings.
        const int targetExpressionBudget = 20000;
        const int maxPerExpressionLength = 8000;
        const int maxTimedSegmentsPerExpression = 96;
        int[] reductionTargets = [int.MaxValue, 192, 160, 128, 96, 72, 56, 40, 28, 20, 16, 12, 8, 6];
        FilterExpressionPlan? bestPlan = null;

        foreach (var reductionTarget in reductionTargets)
        {
            var candidateSegments =
                reductionTarget == int.MaxValue || renderSegments.Count <= reductionTarget
                    ? MergeAdjacentSegments(renderSegments)
                    : ReduceSegmentsPreservingCoverage(renderSegments, reductionTarget);
            var xValueSegments = BuildValueSegments(candidateSegments, static s => s.X);
            var yValueSegments = BuildValueSegments(candidateSegments, static s => s.Y);
            var zoomValueSegments = BuildValueSegments(
                candidateSegments,
                static s => (double)s.FullWidth / Math.Max(1, s.Width)
            );
            var xExpression = BuildTimedExpression(xValueSegments, fallbackValue: 0d);
            var yExpression = BuildTimedExpression(yValueSegments, fallbackValue: 0d);
            var zoomExpression = BuildTimedExpression(zoomValueSegments, fallbackValue: 1d);
            var plan = new FilterExpressionPlan(
                candidateSegments,
                xExpression,
                yExpression,
                zoomExpression
            );
            bestPlan = plan;

            var longestExpressionLength = Math.Max(
                xExpression.Length,
                Math.Max(yExpression.Length, zoomExpression.Length)
            );
            var maxTimedSegmentCount = Math.Max(
                xValueSegments.Count,
                Math.Max(yValueSegments.Count, zoomValueSegments.Count)
            );

            if (
                plan.TotalExpressionLength <= targetExpressionBudget
                && longestExpressionLength <= maxPerExpressionLength
                && maxTimedSegmentCount <= maxTimedSegmentsPerExpression
            )
            {
                return plan;
            }
        }

        return bestPlan ?? new FilterExpressionPlan([], "0", "0", "1");
    }

    private static List<ZoomSegment> MergeAdjacentSegments(IReadOnlyList<ZoomSegment> segments)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        var merged = new List<ZoomSegment>(segments.Count) { segments[0] };
        for (var i = 1; i < segments.Count; i++)
        {
            var current = segments[i];
            var previous = merged[^1];
            var contiguous = current.StartSeconds <= previous.EndSeconds + 0.03;
            var similar =
                Math.Abs(current.X - previous.X) <= 2
                && Math.Abs(current.Y - previous.Y) <= 2
                && Math.Abs(current.Width - previous.Width) <= 2
                && Math.Abs(current.Height - previous.Height) <= 2;
            if (contiguous && similar)
            {
                merged[^1] = previous with
                {
                    EndSeconds = Math.Max(previous.EndSeconds, current.EndSeconds),
                };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static List<ZoomSegment> ReduceSegmentsPreservingCoverage(
        IReadOnlyList<ZoomSegment> segments,
        int maxSegments
    )
    {
        if (segments.Count <= maxSegments)
        {
            return segments.ToList();
        }

        var reduced = new List<ZoomSegment>(maxSegments);
        var remainingBudget = Math.Max(1, maxSegments);
        var remainingZoomSegments = segments.Count(static s => !IsFullFrameSegment(s));

        for (var i = 0; i < segments.Count;)
        {
            var runIsFullFrame = IsFullFrameSegment(segments[i]);
            var runStart = i;
            while (i < segments.Count && IsFullFrameSegment(segments[i]) == runIsFullFrame)
            {
                i++;
            }

            var runLength = i - runStart;
            if (runLength <= 0)
            {
                continue;
            }

            if (runIsFullFrame)
            {
                // Preserve full-frame runs so the filter can always return to the uncropped view.
                reduced.AddRange(segments.Skip(runStart).Take(runLength));
                remainingBudget -= runLength;
                continue;
            }

            var targetForRun = remainingZoomSegments <= 0
                ? 1
                : Math.Max(1, (int)Math.Round(remainingBudget * (runLength / (double)remainingZoomSegments)));
            targetForRun = Math.Min(targetForRun, runLength);
            var bucketSize = (int)Math.Ceiling(runLength / (double)targetForRun);

            for (var j = runStart; j < i; j += bucketSize)
            {
                var endExclusive = Math.Min(i, j + bucketSize);
                reduced.Add(AverageSegments(segments, j, endExclusive));
            }

            remainingBudget -= targetForRun;
            remainingZoomSegments -= runLength;
        }

        return MergeAdjacentSegments(reduced)
            .Take(Math.Max(1, maxSegments + 8))
            .ToList();
    }

    private static async Task<double?> ProbeVideoDurationSecondsAsync(
        string inputPath,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments =
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            if (
                double.TryParse(
                    stdout.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds
                )
                && seconds > 0
            )
            {
                return seconds;
            }
        }
        catch { }

        return null;
    }

    private static async Task<(int Width, int Height)?> ProbeVideoFrameSizeAsync(
        string inputPath,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments =
                $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x \"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var tokens = stdout.Trim().Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2)
            {
                return null;
            }

            if (
                int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                && int.TryParse(
                    tokens[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var height
                )
                && width > 1
                && height > 1
            )
            {
                return (width, height);
            }
        }
        catch { }

        return null;
    }

    private static IReadOnlyList<CursorEvent> MapCursorEventsToVideoSpace(
        IReadOnlyList<CursorEvent> events,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight
    )
    {
        if (
            sourceWidth <= 1
            || sourceHeight <= 1
            || targetWidth <= 1
            || targetHeight <= 1
            || (sourceWidth == targetWidth && sourceHeight == targetHeight)
        )
        {
            return events;
        }

        var scaleX = targetWidth / (float)sourceWidth;
        var scaleY = targetHeight / (float)sourceHeight;
        var mapped = new CursorEvent[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            mapped[i] = evt with
            {
                X = Math.Clamp(evt.X * scaleX, 0f, targetWidth - 1f),
                Y = Math.Clamp(evt.Y * scaleY, 0f, targetHeight - 1f)
            };
        }

        return mapped;
    }

    private static RectF NormalizeRectToAspect(RectF source, int fullWidth, int fullHeight)
    {
        var centerX = source.X + (source.Width / 2f);
        var centerY = source.Y + (source.Height / 2f);
        var targetAspect = (float)fullWidth / fullHeight;

        var width = source.Width;
        var height = source.Height;
        var currentAspect = width / Math.Max(1f, height);

        if (currentAspect < targetAspect)
        {
            width = height * targetAspect;
        }
        else
        {
            height = width / targetAspect;
        }

        var x = centerX - (width / 2f);
        var y = centerY - (height / 2f);

        x = Math.Clamp(x, 0f, Math.Max(0f, fullWidth - width));
        y = Math.Clamp(y, 0f, Math.Max(0f, fullHeight - height));
        width = Math.Clamp(width, 2f, fullWidth);
        height = Math.Clamp(height, 2f, fullHeight);

        return new RectF(x, y, width, height);
    }

    private static string BuildTimedExpression(
        IReadOnlyList<ZoomSegment> segments,
        Func<ZoomSegment, double> valueSelector
    )
    {
        var valueSegments = BuildValueSegments(segments, valueSelector);
        var fallbackValue = valueSegments.Count > 0 ? valueSegments[^1].EndValue : 0d;
        return BuildTimedExpression(valueSegments, fallbackValue);
    }

    private static string BuildTimedExpression(
        IReadOnlyList<TimedValueSegment> valueSegments,
        double fallbackValue
    )
    {
        if (valueSegments.Count == 0)
        {
            return FormatFilterSeconds(fallbackValue);
        }

        var fallback = FormatFilterSeconds(fallbackValue);

        // Build a FLAT SUM expression: fallback + sum(between(t,s,e)*(interp(t)-fallback))
        // This has zero recursion depth in FFmpeg's evaluator, unlike the old nested if() approach
        // which hit FFmpeg's ~100-level recursion limit with more than ~50 keyframes.
        //
        // Resolve overlaps first: earlier valueSegments have higher priority (same as the
        // old nested-if, where earlier entries were the outermost/first-evaluated conditions).
        // We clip each segment's start to max(its original start, previous segment's end),
        // discarding any portion that is dominated by an earlier segment.
        var sb = new StringBuilder();
        sb.Append(fallback);

        double prevEnd = double.MinValue;
        foreach (var seg in valueSegments)
        {
            var clippedStart = Math.Max(seg.StartSeconds, prevEnd);
            if (clippedStart >= seg.EndSeconds - 0.0001d)
            {
                // Entirely dominated by an earlier segment; skip.
                continue;
            }

            prevEnd = seg.EndSeconds;

            // If the segment was clipped, recompute the starting value at the new start.
            var startValue = seg.StartValue;
            if (clippedStart > seg.StartSeconds + 0.0001d && !NearlyEqual(seg.StartValue, seg.EndValue))
            {
                var frac = (clippedStart - seg.StartSeconds) / Math.Max(seg.EndSeconds - seg.StartSeconds, 0.001d);
                startValue = seg.StartValue + (seg.EndValue - seg.StartValue) * frac;
            }

            // Skip terms that would contribute zero (segment value equals fallback throughout).
            if (NearlyEqual(startValue, fallbackValue) && NearlyEqual(seg.EndValue, fallbackValue))
            {
                continue;
            }

            var fStart = FormatFilterSeconds(clippedStart);
            var fEnd = FormatFilterSeconds(seg.EndSeconds);
            var segExpr = BuildSegmentInterpolationExpression(fStart, fEnd, startValue, seg.EndValue);
            sb.Append($"+between(t,{fStart},{fEnd})*({segExpr}-({fallback}))");
        }

        return sb.ToString();
    }

    private static ZoomSegment AverageSegments(
        IReadOnlyList<ZoomSegment> segments,
        int startInclusive,
        int endExclusive
    )
    {
        var first = segments[startInclusive];
        var last = segments[endExclusive - 1];

        double totalDuration = 0d;
        double weightedX = 0d;
        double weightedY = 0d;
        double weightedWidth = 0d;
        double weightedHeight = 0d;

        for (var i = startInclusive; i < endExclusive; i++)
        {
            var segment = segments[i];
            var duration = Math.Max(0.001d, segment.EndSeconds - segment.StartSeconds);
            totalDuration += duration;
            weightedX += segment.X * duration;
            weightedY += segment.Y * duration;
            weightedWidth += segment.Width * duration;
            weightedHeight += segment.Height * duration;
        }

        if (totalDuration <= 0d)
        {
            totalDuration = 1d;
        }

        var width = MakeEven((int)Math.Round(weightedWidth / totalDuration));
        var height = MakeEven((int)Math.Round(weightedHeight / totalDuration));
        var maxX = Math.Max(0, first.FullWidth - width);
        var maxY = Math.Max(0, first.FullHeight - height);

        return new ZoomSegment(
            first.StartSeconds,
            last.EndSeconds,
            Math.Clamp((int)Math.Round(weightedX / totalDuration), 0, maxX),
            Math.Clamp((int)Math.Round(weightedY / totalDuration), 0, maxY),
            width,
            height,
            first.FullWidth,
            first.FullHeight
        );
    }

    private static bool IsFullFrameSegment(ZoomSegment segment)
    {
        return segment.X == 0
            && segment.Y == 0
            && Math.Abs(segment.Width - segment.FullWidth) <= 2
            && Math.Abs(segment.Height - segment.FullHeight) <= 2;
    }

    private static List<TimedValueSegment> BuildValueSegments(
        IReadOnlyList<ZoomSegment> segments,
        Func<ZoomSegment, double> valueSelector
    )
    {
        // FIX: Reduced from 0.24s to 0.12s AND capped to half the segment duration.
        // The old value of 0.24s was longer than most pan keyframes (5-80ms), which
        // caused every tiny pan step to get a 240ms transition, completely destroying
        // the filter interpolation and making zoom-in appear to take ~0.77 seconds.
        const double maxTransitionDurationSeconds = 0.12d;

        if (segments.Count == 0)
        {
            return [];
        }

        var valueSegments = new List<TimedValueSegment>(segments.Count * 2);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var startValue = valueSelector(segment);
            var segmentAdded = false;

            if (i < segments.Count - 1)
            {
                var nextSegment = segments[i + 1];
                if (nextSegment.StartSeconds <= segment.EndSeconds + 0.035d)
                {
                    var nextValue = valueSelector(nextSegment);
                    if (!NearlyEqual(startValue, nextValue))
                    {
                        // FIX: Cap the transition to half the segment duration so that
                        // short pan keyframes (e.g. 8ms) never get a transition longer
                        // than themselves. Old code used a fixed 0.24s which was always
                        // longer than pan segments, causing the camera to interpolate
                        // across the wrong time range entirely.
                        var segmentDuration = segment.EndSeconds - segment.StartSeconds;
                        var transitionDuration = Math.Min(
                            maxTransitionDurationSeconds,
                            segmentDuration * 0.5d
                        );

                        var transitionStart = Math.Max(
                            segment.StartSeconds,
                            segment.EndSeconds - transitionDuration
                        );
                        if (transitionStart > segment.StartSeconds + 0.0005d)
                        {
                            AppendValueSegment(
                                valueSegments,
                                segment.StartSeconds,
                                transitionStart,
                                startValue,
                                startValue
                            );
                        }

                        // FIX: Transition ends at nextSegment.StartSeconds (straddling the
                        // boundary) rather than segment.EndSeconds. This means the camera
                        // starts moving before the next click lands, matching Screen Studio
                        // behavior where the pan anticipates the next position.
                        var transitionEnd = Math.Min(
                            nextSegment.StartSeconds + (transitionDuration * 0.5d),
                            nextSegment.EndSeconds
                        );
                        AppendValueSegment(
                            valueSegments,
                            transitionStart,
                            transitionEnd,
                            startValue,
                            nextValue
                        );
                        segmentAdded = true;
                    }
                }
            }

            if (!segmentAdded)
            {
                AppendValueSegment(
                    valueSegments,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    startValue,
                    startValue
                );
            }
        }

        return valueSegments;
    }

    private static void AppendValueSegment(
        List<TimedValueSegment> valueSegments,
        double startSeconds,
        double endSeconds,
        double startValue,
        double endValue
    )
    {
        if (endSeconds <= startSeconds)
        {
            return;
        }

        if (valueSegments.Count > 0)
        {
            var previous = valueSegments[^1];
            var contiguous = startSeconds <= previous.EndSeconds + 0.035d;
            var sameValues =
                NearlyEqual(previous.StartValue, startValue)
                && NearlyEqual(previous.EndValue, endValue);
            if (contiguous && sameValues)
            {
                valueSegments[^1] = previous with { EndSeconds = endSeconds };
                return;
            }
        }

        valueSegments.Add(new TimedValueSegment(startSeconds, endSeconds, startValue, endValue));
    }

    private static string BuildSegmentInterpolationExpression(
        string start,
        string end,
        double startValue,
        double endValue
    )
    {
        if (Math.Abs(endValue - startValue) <= 0.001d)
        {
            return FormatFilterSeconds(startValue);
        }

        var formattedStart = FormatFilterSeconds(startValue);
        var delta = FormatFilterSeconds(endValue - startValue);
        return $"({formattedStart}+(({delta})*((t-{start})/max({end}-{start},0.001))))";
    }

    private static string FormatFilterSeconds(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool NearlyEqual(double left, double right, double epsilon = 0.001d)
    {
        return Math.Abs(left - right) <= epsilon;
    }

    private IReadOnlyList<CursorEvent> SnapshotCursorEvents()
    {
        lock (_sync)
        {
            return _cursorEvents.OrderBy(static e => e.TimestampTicks).ToArray();
        }
    }

    private void InstallInputHooks()
    {
        InstallMouseHook();
        InstallKeyboardHook();
    }

    private void UninstallInputHooks()
    {
        UninstallMouseHook();
        UninstallKeyboardHook();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        // Must pass a valid module handle — IntPtr.Zero causes silent failure on 64-bit .NET
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, HookModuleHandle, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not install the mouse activity hook.");
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        // Must pass a valid module handle — IntPtr.Zero causes silent failure on 64-bit .NET
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, HookModuleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not install the keyboard activity hook.");
        }
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_recording || _paused)
        {
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var mouseData = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var eventPoint = mouseData.pt;
        var eventTicks = GetRecordingTimestampTicks();

        switch (message)
        {
            case WmMouseMove:
                if (
                    !_hasLastHookPoint
                    || eventPoint.X != _lastHookPoint.X
                    || eventPoint.Y != _lastHookPoint.Y
                )
                {
                    if (
                        TryMapToCapture(
                            eventPoint,
                            clampToBounds: true,
                            out var relativeX,
                            out var relativeY
                        )
                    )
                    {
                        System.Diagnostics.Debug.WriteLine($"[MOUSE] raw=({eventPoint.X},{eventPoint.Y}) -> transform=({relativeX:F1},{relativeY:F1})");
                        EnqueueInputEvent(
                            new PendingInputEvent(
                                eventTicks,
                                relativeX,
                                relativeY,
                                CursorEventType.Move
                            )
                        );
                    }

                    _lastHookPoint = eventPoint;
                    _hasLastHookPoint = true;
                }

                break;

            case WmLButtonDown:
                if (TryMapToCapture(eventPoint, clampToBounds: true, out var downX, out var downY))
                {
                    EnqueueInputEvent(
                        new PendingInputEvent(eventTicks, downX, downY, CursorEventType.LeftDown)
                    );
                }

                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmLButtonUp:
                if (TryMapToCapture(eventPoint, clampToBounds: true, out var upX, out var upY))
                {
                    EnqueueInputEvent(
                        new PendingInputEvent(eventTicks, upX, upY, CursorEventType.LeftUp)
                    );
                }

                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmRButtonDown:
                if (
                    TryMapToCapture(
                        eventPoint,
                        clampToBounds: false,
                        out var rightDownX,
                        out var rightDownY
                    )
                )
                {
                    EnqueueInputEvent(
                        new PendingInputEvent(
                            eventTicks,
                            rightDownX,
                            rightDownY,
                            CursorEventType.RightDown
                        )
                    );
                }

                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmRButtonUp:
                if (
                    TryMapToCapture(
                        eventPoint,
                        clampToBounds: false,
                        out var rightUpX,
                        out var rightUpY
                    )
                )
                {
                    EnqueueInputEvent(
                        new PendingInputEvent(
                            eventTicks,
                            rightUpX,
                            rightUpY,
                            CursorEventType.RightUp
                        )
                    );
                }

                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmMouseWheel:
                if (
                    TryMapToCapture(
                        eventPoint,
                        clampToBounds: false,
                        out var scrollX,
                        out var scrollY
                    )
                )
                {
                    EnqueueInputEvent(
                        new PendingInputEvent(eventTicks, scrollX, scrollY, CursorEventType.Scroll)
                    );
                }

                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_recording || _paused)
        {
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message is not WmKeyDown and not WmSysKeyDown)
        {
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        var keyboardData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (IsIgnoredKeyboardKey(keyboardData.vkCode))
        {
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (TryGetBestCursorPos(out var point))
        {
            var eventTicks = GetRecordingTimestampTicks();
            Interlocked.Exchange(ref _lastHookKeyPressTicks, eventTicks);
            if (TryMapToCapture(point, clampToBounds: false, out var relativeX, out var relativeY))
            {
                EnqueueInputEvent(
                    new PendingInputEvent(
                        eventTicks,
                        relativeX,
                        relativeY,
                        CursorEventType.KeyPress
                    )
                );
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void EnqueueInputEvent(PendingInputEvent pendingEvent)
    {
        System.Diagnostics.Debug.WriteLine($"[CURSOR] {pendingEvent.EventType} @ ({pendingEvent.RelativeX:F1}, {pendingEvent.RelativeY:F1})");
        _pendingInputEvents.Enqueue(pendingEvent);
    }

    private bool TryMapToCapture(
        POINT point,
        bool clampToBounds,
        out float relativeX,
        out float relativeY
    )
    {
        return _captureTransform.TryMapDesktopPoint(
            point.X,
            point.Y,
            clampToBounds,
            CaptureMapTolerancePixels,
            out relativeX,
            out relativeY
        );
    }

    private static bool TryGetBestCursorPos(out POINT point)
    {
        // Try physical first — MSLLHOOKSTRUCT.pt (mouse hook) always returns
        // per-monitor-aware physical coordinates. GetPhysicalCursorPos returns
        // the same coordinate space, keeping keyboard events consistent.
        if (GetPhysicalCursorPos(out point))
        {
            return true;
        }

        if (GetCursorPos(out point))
        {
            return true;
        }

        return false;
    }

    private static bool IsIgnoredKeyboardKey(uint virtualKey)
    {
        return virtualKey is >= 0x01 and <= 0x06 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
    }

    private async Task CaptureKeyboardFallbackLoopAsync(CancellationToken cancellationToken)
    {
        var keyState = new bool[256];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_recording || _paused)
            {
                continue;
            }

            var nowTicks = GetRecordingTimestampTicks();
            var sinceHookTicks = nowTicks - Interlocked.Read(ref _lastHookKeyPressTicks);
            if (sinceHookTicks <= TimeSpan.FromMilliseconds(500).Ticks)
            {
                continue;
            }

            for (var vk = 0x08; vk <= 0xFE; vk++)
            {
                if (IsIgnoredKeyboardKey((uint)vk))
                {
                    continue;
                }

                var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down && !keyState[vk])
                {
                    keyState[vk] = true;
                    if (
                        TryGetBestCursorPos(out var point)
                        && TryMapToCapture(
                            point,
                            clampToBounds: false,
                            out var relativeX,
                            out var relativeY
                        )
                    )
                    {
                        AddCursorEvent(
                            new CursorEvent(
                                nowTicks,
                                relativeX,
                                relativeY,
                                CursorEventType.KeyPress
                            )
                        );
                    }
                }
                else if (!down && keyState[vk])
                {
                    keyState[vk] = false;
                }
            }
        }
    }

    private async Task ProcessPendingInputEventsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            DrainPendingInputEvents();
        }
    }

    private void DrainPendingInputEvents()
    {
        while (_pendingInputEvents.TryDequeue(out var pendingEvent))
        {
            if (
                pendingEvent.EventType == CursorEventType.Move
                && _hasLastPoint
                && Math.Abs(pendingEvent.RelativeX - _lastRelativeX) <= 0.01f
                && Math.Abs(pendingEvent.RelativeY - _lastRelativeY) <= 0.01f
            )
            {
                continue;
            }

            AddCursorEvent(
                new CursorEvent(
                    pendingEvent.TimestampTicks,
                    pendingEvent.RelativeX,
                    pendingEvent.RelativeY,
                    pendingEvent.EventType
                )
            );
            _lastRelativeX = pendingEvent.RelativeX;
            _lastRelativeY = pendingEvent.RelativeY;
            _hasLastPoint = true;
        }
    }

    private long GetRecordingTimestampTicks()
    {
        if (_startTicks <= 0 || !_recordingClock.IsRunning)
        {
            return DateTime.UtcNow.Ticks;
        }

        return _startTicks + _recordingClock.Elapsed.Ticks;
    }

    private void AddCursorEvent(CursorEvent cursorEvent)
    {
        lock (_sync)
        {
            _cursorEvents.Add(cursorEvent);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out RECT pvAttribute,
        int cbAttribute
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private readonly record struct CaptureBounds(int X, int Y, int Width, int Height);

    private readonly record struct ZoomSegment(
        double StartSeconds,
        double EndSeconds,
        int X,
        int Y,
        int Width,
        int Height,
        int FullWidth,
        int FullHeight
    );

    private readonly record struct FilterExpressionPlan(
        IReadOnlyList<ZoomSegment> Segments,
        string XExpression,
        string YExpression,
        string ZoomExpression
    )
    {
        public int TotalExpressionLength =>
            XExpression.Length + YExpression.Length + ZoomExpression.Length;
    }

    private readonly record struct TimedValueSegment(
        double StartSeconds,
        double EndSeconds,
        double StartValue,
        double EndValue
    );

    private readonly record struct PendingInputEvent(
        long TimestampTicks,
        float RelativeX,
        float RelativeY,
        CursorEventType EventType
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint MonitorDefaultToNearest = 0x00000002;
}
