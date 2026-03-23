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
    private const int VkLButton = 0x01;
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
    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyboardProc;

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
    private long _startTicks;
    private long _pauseStartedTicks;
    private long _accumulatedPausedTicks;
    private bool _recording;
    private bool _paused;
    private POINT _lastPoint;
    private bool _hasLastPoint;
    private POINT _lastHookPoint;
    private bool _hasLastHookPoint;
    private long _lastHookKeyPressTicks;
    private CaptureBounds _captureBounds;

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
        _captureBounds = ResolveCaptureBounds(options);
        _rawVideoPath = Path.Combine(outputDirectory, $"{sessionPrefix}.raw.mp4");
        _processedVideoPath = Path.Combine(outputDirectory, $"{sessionPrefix}.processed.mp4");
        _recordingLogPath = Path.Combine(outputDirectory, $"{sessionPrefix}.recording.ffmpeg.log");
        _processingLogPath = Path.Combine(outputDirectory, $"{sessionPrefix}.processing.ffmpeg.log");
        _zoomIntensity = options.ZoomIntensity;
        _zoomSensitivity = options.ZoomSensitivity;
        _followSpeed = options.FollowSpeed;
        _startTicks = DateTime.UtcNow.Ticks;
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

        while (_pendingInputEvents.TryDequeue(out _))
        {
        }

        InstallInputHooks();
        _cursorCaptureCts = new CancellationTokenSource();
        _inputProcessingTask = ProcessPendingInputEventsLoopAsync(_cursorCaptureCts.Token);
        _cursorCaptureTask = CaptureKeyboardFallbackLoopAsync(_cursorCaptureCts.Token);
        (_ffmpegProcess, _ffmpegOutputTask, _ffmpegErrorTask) =
            StartFfmpegProcess(_rawVideoPath, _recordingLogPath, options.FramesPerSecond, _captureBounds);

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
        if (!_recording
            || _ffmpegProcess is null
            || _rawVideoPath is null
            || _processedVideoPath is null)
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
        var zoomKeyframes = _zoomGenerator.Generate(
            cursorEvents,
            _captureBounds.Width,
            _captureBounds.Height,
            _zoomIntensity,
            _zoomSensitivity,
            _followSpeed);

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
                    _captureBounds,
                    _startTicks,
                    _captureBounds.Width,
                    _captureBounds.Height,
                    cancellationToken)
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
            processingError);

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
            catch (OperationCanceledException)
            {
            }
        }

        if (_cursorCaptureTask is not null)
        {
            try
            {
                await _cursorCaptureTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
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
        _pauseStartedTicks = 0;
        _accumulatedPausedTicks = 0;
        _lastHookKeyPressTicks = 0;
    }

    private static async Task WriteJsonAsync<T>(string path, T data, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
                stream,
                data,
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken)
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
            CreateNoWindow = true
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
                ex);
        }
    }

    private static CaptureBounds ResolveCaptureBounds(RecordingOptions options)
    {
        var isWindow = string.Equals(options.CaptureTarget, "Window", StringComparison.OrdinalIgnoreCase);
        if (!isWindow)
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = MakeEven(Math.Max(2, GetSystemMetrics(SmCxVirtualScreen)));
            var height = MakeEven(Math.Max(2, GetSystemMetrics(SmCyVirtualScreen)));
            return new CaptureBounds(x, y, width, height);
        }

        var hwnd = options.SelectedWindowHandle != nint.Zero
            ? options.SelectedWindowHandle
            : GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("No target window selected.");
        }

        if (!IsWindow(hwnd))
        {
            throw new InvalidOperationException("The selected window is no longer available. Refresh the list and select it again.");
        }

        if (IsIconic(hwnd))
        {
            throw new InvalidOperationException("The selected window is minimized. Restore it before recording.");
        }

        if (!TryGetWindowCaptureRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("Could not read the selected window bounds.");
        }

        rect = ClampToVirtualScreen(rect);

        var widthValue = rect.Right - rect.Left;
        var heightValue = rect.Bottom - rect.Top;
        if (widthValue < 16 || heightValue < 16)
        {
            throw new InvalidOperationException("Selected window is too small to capture.");
        }

        return new CaptureBounds(
            rect.Left,
            rect.Top,
            MakeEven(widthValue),
            MakeEven(heightValue));
    }

    private static bool TryGetWindowCaptureRect(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
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
        CaptureBounds bounds)
    {
        var fps = Math.Clamp(framesPerSecond, 15, 60);
        var arguments =
            $"-y -f gdigrab -framerate {fps} -offset_x {bounds.X} -offset_y {bounds.Y} -video_size {bounds.Width}x{bounds.Height} -draw_mouse 1 -i desktop -an -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{outputPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg recording process.");

        var outputTask = PumpReaderToLogAsync(process.StandardOutput, logPath, "stdout");
        var errorTask = PumpReaderToLogAsync(process.StandardError, logPath, "stderr");

        return (process, outputTask, errorTask);
    }

    private static async Task StopFfmpegProcessAsync(Process process, CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var inputDurationSeconds = await ProbeVideoDurationSecondsAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var segments = BuildSegments(zoomKeyframes, bounds, sessionStartTicks, inputDurationSeconds);
        if (segments.Count == 0)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var xExpr = BuildTimedExpression(0d, segments, static s => s.X);
        var yExpr = BuildTimedExpression(0d, segments, static s => s.Y);
        var zoomExpr = BuildTimedExpression(
            1d,
            segments,
            static s => (double)s.FullWidth / Math.Max(1, s.Width));

        var filterScriptPath = Path.ChangeExtension(outputPath, ".filter.txt");
        var safeOutWidth = MakeEven(Math.Max(2, outputWidth));
        var safeOutHeight = MakeEven(Math.Max(2, outputHeight));
        var scaledWidthExpr = $"trunc(iw*({zoomExpr})/2)*2";
        var scaledHeightExpr = $"trunc(ih*({zoomExpr})/2)*2";
        var cropXExpr = $"min(max(0,({xExpr})*({zoomExpr})),iw-{safeOutWidth})";
        var cropYExpr = $"min(max(0,({yExpr})*({zoomExpr})),ih-{safeOutHeight})";
        var filterGraph =
            $"[0:v]scale=w='{scaledWidthExpr}':h='{scaledHeightExpr}':eval=frame:flags=lanczos," +
            $"crop={safeOutWidth}:{safeOutHeight}:x='{cropXExpr}':y='{cropYExpr}'[v]";
        await File.WriteAllTextAsync(filterScriptPath, filterGraph, cancellationToken).ConfigureAwait(false);

        var arguments =
            $"-y -i \"{inputPath}\" -filter_complex_script \"{filterScriptPath}\" -map \"[v]\" -an -c:v libx264 -preset medium -pix_fmt yuv420p -movflags +faststart \"{outputPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg post-processing.");

        var outputTask = PumpReaderToLogAsync(process.StandardOutput, logPath, "stdout");
        var errorTask = PumpReaderToLogAsync(process.StandardError, logPath, "stderr");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var logHint = string.IsNullOrWhiteSpace(logPath) ? string.Empty : $" Check FFmpeg log: {logPath}";
            throw new InvalidOperationException($"FFmpeg post-processing failed.{logHint}");
        }
    }

    private static async Task PumpReaderToLogAsync(StreamReader reader, string? logPath, string streamName)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                continue;
            }

            await File.AppendAllTextAsync(logPath, $"[{streamName}] {line}{Environment.NewLine}").ConfigureAwait(false);
        }
    }

    private static List<ZoomSegment> BuildSegments(
        IReadOnlyList<ZoomKeyframe> zoomKeyframes,
        CaptureBounds bounds,
        long sessionStartTicks,
        double? inputDurationSeconds)
    {
        const int maxSegmentsForFilter = 128;
        var segments = new List<ZoomSegment>();
        var durationLimit = inputDurationSeconds.HasValue && inputDurationSeconds.Value > 0
            ? inputDurationSeconds.Value
            : double.PositiveInfinity;

        foreach (var keyframe in zoomKeyframes)
        {
            if (keyframe.EndTicks <= keyframe.StartTicks)
            {
                continue;
            }

            var startSeconds = TimeSpan.FromTicks(Math.Max(0, keyframe.StartTicks - sessionStartTicks)).TotalSeconds;
            var endSeconds = TimeSpan.FromTicks(Math.Max(0, keyframe.EndTicks - sessionStartTicks)).TotalSeconds;
            if (startSeconds >= durationLimit)
            {
                continue;
            }

            endSeconds = Math.Min(endSeconds, durationLimit);
            if (endSeconds <= startSeconds)
            {
                continue;
            }

            var normalized = NormalizeRectToAspect(keyframe.TargetRect, bounds.Width, bounds.Height);
            var width = MakeEven((int)Math.Round(normalized.Width));
            var height = MakeEven((int)Math.Round(normalized.Height));
            var x = (int)Math.Round(normalized.X);
            var y = (int)Math.Round(normalized.Y);

            if (width >= bounds.Width && height >= bounds.Height)
            {
                continue;
            }

            segments.Add(new ZoomSegment(
                Math.Max(0, startSeconds),
                Math.Max(0, endSeconds),
                x,
                y,
                width,
                height,
                bounds.Width));
        }

        segments = MergeAdjacentSegments(segments);
        if (segments.Count <= maxSegmentsForFilter)
        {
            return segments;
        }

        return ReduceSegmentsPreservingCoverage(segments, maxSegmentsForFilter);
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
            var similar = Math.Abs(current.X - previous.X) <= 2
                && Math.Abs(current.Y - previous.Y) <= 2
                && Math.Abs(current.Width - previous.Width) <= 2
                && Math.Abs(current.Height - previous.Height) <= 2;
            if (contiguous && similar)
            {
                merged[^1] = previous with { EndSeconds = Math.Max(previous.EndSeconds, current.EndSeconds) };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static List<ZoomSegment> ReduceSegmentsPreservingCoverage(IReadOnlyList<ZoomSegment> segments, int maxSegments)
    {
        if (segments.Count <= maxSegments)
        {
            return segments.ToList();
        }

        var bucketSize = (int)Math.Ceiling(segments.Count / (double)maxSegments);
        var reduced = new List<ZoomSegment>(maxSegments);

        for (var i = 0; i < segments.Count; i += bucketSize)
        {
            var endExclusive = Math.Min(segments.Count, i + bucketSize);
            var first = segments[i];
            var last = segments[endExclusive - 1];

            double totalDuration = 0d;
            double weightedX = 0d;
            double weightedY = 0d;
            double weightedWidth = 0d;
            double weightedHeight = 0d;

            for (var j = i; j < endExclusive; j++)
            {
                var segment = segments[j];
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

            reduced.Add(new ZoomSegment(
                first.StartSeconds,
                last.EndSeconds,
                Math.Clamp((int)Math.Round(weightedX / totalDuration), 0, maxX),
                Math.Max(0, (int)Math.Round(weightedY / totalDuration)),
                width,
                height,
                first.FullWidth));
        }

        return MergeAdjacentSegments(reduced);
    }

    private static async Task<double?> ProbeVideoDurationSecondsAsync(string inputPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
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

            if (double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }
        catch
        {
        }

        return null;
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
        double defaultValue,
        IReadOnlyList<ZoomSegment> segments,
        Func<ZoomSegment, double> valueSelector)
    {
        var expression = FormatExpressionNumber(defaultValue);
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];
            var start = segment.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var end = segment.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var startValue = valueSelector(segment);
            var endValue = startValue;

            if (i < segments.Count - 1)
            {
                var nextSegment = segments[i + 1];
                var contiguous = nextSegment.StartSeconds <= segment.EndSeconds + 0.035;
                if (contiguous)
                {
                    endValue = valueSelector(nextSegment);
                }
            }

            var segmentExpression = BuildSegmentInterpolationExpression(start, end, startValue, endValue);
            expression = $"if(between(t,{start},{end}),{segmentExpression},{expression})";
        }

        return expression;
    }

    private static string BuildSegmentInterpolationExpression(string start, string end, double startValue, double endValue)
    {
        if (Math.Abs(endValue - startValue) <= 0.001d)
        {
            return FormatExpressionNumber(startValue);
        }

        var formattedStart = FormatExpressionNumber(startValue);
        var delta = FormatExpressionNumber(endValue - startValue);
        return $"({formattedStart}+(({delta})*((t-{start})/max({end}-{start},0.001))))";
    }

    private static string FormatExpressionNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
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
        var eventTicks = DateTime.UtcNow.Ticks;

        switch (message)
        {
            case WmMouseMove:
                if (!_hasLastHookPoint || eventPoint.X != _lastHookPoint.X || eventPoint.Y != _lastHookPoint.Y)
                {
                    EnqueueInputEvent(new PendingInputEvent(eventTicks, eventPoint, CursorEventType.Move));
                    _lastHookPoint = eventPoint;
                    _hasLastHookPoint = true;
                }

                break;

            case WmLButtonDown:
                EnqueueInputEvent(new PendingInputEvent(eventTicks, eventPoint, CursorEventType.LeftDown));
                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmLButtonUp:
                EnqueueInputEvent(new PendingInputEvent(eventTicks, eventPoint, CursorEventType.LeftUp));
                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmRButtonDown:
                EnqueueInputEvent(new PendingInputEvent(eventTicks, eventPoint, CursorEventType.RightDown));
                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmRButtonUp:
                EnqueueInputEvent(new PendingInputEvent(eventTicks, eventPoint, CursorEventType.RightUp));
                _lastHookPoint = eventPoint;
                _hasLastHookPoint = true;
                break;

            case WmMouseWheel:
                EnqueueInputEvent(new PendingInputEvent(eventTicks, eventPoint, CursorEventType.Scroll));
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
            var eventTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _lastHookKeyPressTicks, eventTicks);
            EnqueueInputEvent(new PendingInputEvent(eventTicks, point, CursorEventType.KeyPress));
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void EnqueueInputEvent(PendingInputEvent pendingEvent)
    {
        _pendingInputEvents.Enqueue(pendingEvent);
    }

    private bool TryMapToCapture(POINT point, out float relativeX, out float relativeY)
    {
        relativeX = point.X - _captureBounds.X;
        relativeY = point.Y - _captureBounds.Y;

        if (relativeX < -CaptureMapTolerancePixels
            || relativeY < -CaptureMapTolerancePixels
            || relativeX > _captureBounds.Width + CaptureMapTolerancePixels
            || relativeY > _captureBounds.Height + CaptureMapTolerancePixels)
        {
            return false;
        }

        relativeX = Math.Clamp(relativeX, 0f, _captureBounds.Width - 1f);
        relativeY = Math.Clamp(relativeY, 0f, _captureBounds.Height - 1f);
        return true;
    }

    private static bool TryGetBestCursorPos(out POINT point)
    {
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
        return virtualKey is >= 0x01 and <= 0x06
            or 0x10
            or 0x11
            or 0x12
            or 0x5B
            or 0x5C;
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

            var nowTicks = DateTime.UtcNow.Ticks;
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
                    if (TryGetBestCursorPos(out var point)
                        && TryMapToCapture(point, out var relativeX, out var relativeY))
                    {
                        AddCursorEvent(new CursorEvent(nowTicks, relativeX, relativeY, CursorEventType.KeyPress));
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
            if (!TryMapToCapture(pendingEvent.Point, out var relativeX, out var relativeY))
            {
                continue;
            }

            if (pendingEvent.EventType == CursorEventType.Move
                && _hasLastPoint
                && pendingEvent.Point.X == _lastPoint.X
                && pendingEvent.Point.Y == _lastPoint.Y)
            {
                continue;
            }

            AddCursorEvent(new CursorEvent(pendingEvent.TimestampTicks, relativeX, relativeY, pendingEvent.EventType));
            _lastPoint = pendingEvent.Point;
            _hasLastPoint = true;
        }
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
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

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

    private readonly record struct CaptureBounds(int X, int Y, int Width, int Height);

    private readonly record struct ZoomSegment(
        double StartSeconds,
        double EndSeconds,
        int X,
        int Y,
        int Width,
        int Height,
        int FullWidth);

    private readonly record struct PendingInputEvent(long TimestampTicks, POINT Point, CursorEventType EventType);

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
}
