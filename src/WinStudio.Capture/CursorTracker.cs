using System.Diagnostics;
using System.Runtime.InteropServices;
using WinStudio.Common;

namespace WinStudio.Capture;

public sealed class CursorTracker : IDisposable
{
    private readonly List<CursorEvent> _events = [];
    private readonly object _sync = new();
    private bool _isRunning;

    // Set by CaptureCoordinator before StartAsync
    public int CaptureOriginX { get; set; }
    public int CaptureOriginY { get; set; }

    // Keep delegate references as fields — GC will crash the hook if these are locals
    private LowLevelMouseProc?    _mouseProc;
    private LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _mouseHook    = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL    = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_MOUSEWHEEL  = 0x020A;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT  pt;
        public uint   mouseData;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // GetPhysicalCursorPos always returns physical-pixel coordinates regardless of DPI awareness,
    // matching the MSLLHOOKSTRUCT.pt values used by the mouse hook.
    [DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out POINT lpPoint);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isRunning = true;
        _events.Clear();

        using var process = Process.GetCurrentProcess();
        using var module  = process.MainModule!;
        var hMod = GetModuleHandle(module.ModuleName);

        _mouseProc    = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        _mouseHook    = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc,    hMod, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _isRunning = false;

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        return Task.CompletedTask;
    }

    public void RecordEvent(float x, float y, CursorEventType eventType, long? timestampTicks = null)
    {
        if (!_isRunning) return;
        lock (_sync)
        {
            _events.Add(new CursorEvent(timestampTicks ?? DateTime.UtcNow.Ticks, x, y, eventType));
        }
    }

    public IReadOnlyList<CursorEvent> GetEvents()
    {
        lock (_sync)
        {
            return _events.OrderBy(static e => e.TimestampTicks).ToArray();
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isRunning)
        {
            var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Convert screen-absolute coordinates to capture-relative
            var x = (float)(s.pt.X - CaptureOriginX);
            var y = (float)(s.pt.Y - CaptureOriginY);

            CursorEventType? eventType = (int)wParam switch
            {
                WM_MOUSEMOVE   => CursorEventType.Move,
                WM_LBUTTONDOWN => CursorEventType.LeftDown,
                WM_LBUTTONUP   => CursorEventType.LeftUp,
                WM_RBUTTONDOWN => CursorEventType.RightDown,
                WM_RBUTTONUP   => CursorEventType.RightUp,
                WM_MOUSEWHEEL  => CursorEventType.Scroll,
                _              => null
            };

            if (eventType.HasValue)
            {
                lock (_sync)
                {
                    _events.Add(new CursorEvent(DateTime.UtcNow.Ticks, x, y, eventType.Value));
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isRunning)
        {
            var msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                // Use GetPhysicalCursorPos so the coordinates are in physical pixels,
                // matching MSLLHOOKSTRUCT.pt from the mouse hook and the capture origin
                // resolved by CaptureCoordinator (DWMWA_EXTENDED_FRAME_BOUNDS is also physical).
                GetPhysicalCursorPos(out POINT pt);
                var x = (float)(pt.X - CaptureOriginX);
                var y = (float)(pt.Y - CaptureOriginY);
                lock (_sync)
                {
                    _events.Add(new CursorEvent(DateTime.UtcNow.Ticks, x, y, CursorEventType.KeyPress));
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose() => StopAsync();
}