using System.Runtime.InteropServices;
using System.Threading.Channels;
using WinStudio.Common;

namespace WinStudio.Capture;

public sealed class CaptureCoordinator
{
    private readonly ScreenCaptureSession _screenCaptureSession = new();
    private readonly AudioCaptureSession  _audioCaptureSession  = new();
    private readonly CursorTracker        _cursorTracker        = new();
    private readonly List<RawVideoFrame>  _frames               = [];
    private readonly List<AudioChunk>     _chunks               = [];

    // The HWND or monitor handle must be set before StartAsync
    // Set CapturedHwnd for window capture, CapturedHmonitor for monitor capture
    public IntPtr CapturedHwnd     { get; set; } = IntPtr.Zero;
    public IntPtr CapturedHmonitor { get; set; } = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _screenCaptureSession.StartAsync(cancellationToken).ConfigureAwait(false);

        // Resolve capture origin BEFORE starting the cursor tracker
        // so all recorded coordinates are already in capture-relative space
        ResolveAndSetCaptureOrigin();

        await _audioCaptureSession.StartAsync(cancellationToken).ConfigureAwait(false);
        await _cursorTracker.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _screenCaptureSession.StopAsync().ConfigureAwait(false);
        await _audioCaptureSession.StopAsync().ConfigureAwait(false);
        await _cursorTracker.StopAsync().ConfigureAwait(false);
        await DrainChannelsAsync(cancellationToken).ConfigureAwait(false);
    }

    public CaptureSession GetSession()
    {
        return new CaptureSession(
            _screenCaptureSession.StartTimestampTicks,
            _frames.ToArray(),
            _chunks.ToArray(),
            _cursorTracker.GetEvents());
    }

    private void ResolveAndSetCaptureOrigin()
    {
        if (CapturedHwnd != IntPtr.Zero)
        {
            // Use DwmGetWindowAttribute with DWMWA_EXTENDED_FRAME_BOUNDS
            // NOT GetWindowRect — GetWindowRect includes invisible resize borders (~8px each side)
            // which shifts the origin and causes zoom to land in the wrong position
            var hr = DwmGetWindowAttribute(
                CapturedHwnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT bounds,
                Marshal.SizeOf<RECT>());

            if (hr == 0) // S_OK
            {
                _cursorTracker.CaptureOriginX = bounds.Left;
                _cursorTracker.CaptureOriginY = bounds.Top;
                return;
            }
        }

        if (CapturedHmonitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(CapturedHmonitor, ref info))
            {
                _cursorTracker.CaptureOriginX = info.rcMonitor.Left;
                _cursorTracker.CaptureOriginY = info.rcMonitor.Top;
                return;
            }
        }

        // Fallback: no origin — coordinates will be screen-absolute
        // This means zoom may be off but the app will not crash
        _cursorTracker.CaptureOriginX = 0;
        _cursorTracker.CaptureOriginY = 0;
    }

    private async Task DrainChannelsAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _screenCaptureSession.Frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _frames.Add(frame);
        }
        await foreach (var chunk in _audioCaptureSession.Chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _chunks.Add(chunk);
        }
    }
}