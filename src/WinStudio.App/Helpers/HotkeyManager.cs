using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WinStudio.App.Helpers;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

public sealed class HotkeyManager : IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmHotkey = 0x0312;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly WndProc _wndProcDelegate;
    private readonly IntPtr _oldWndProc;
    private bool _disposed;

    public HotkeyManager(IntPtr hwnd, DispatcherQueue dispatcherQueue)
    {
        _hwnd = hwnd;
        _dispatcherQueue = dispatcherQueue;
        _wndProcDelegate = WindowProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    public void Register(int id, HotKeyModifiers modifiers, uint virtualKey, Action callback)
    {
        ThrowIfDisposed();
        if (!RegisterHotKey(_hwnd, id, (uint)modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterHotKey failed for id {id}. Win32Error={error}.");
        }

        _handlers[id] = callback;
    }

    public void Unregister(int id)
    {
        if (_disposed)
        {
            return;
        }

        UnregisterHotKey(_hwnd, id);
        _handlers.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var id in _handlers.Keys.ToArray())
        {
            UnregisterHotKey(_hwnd, id);
        }

        _handlers.Clear();
        _ = SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);
        _disposed = true;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotkey)
        {
            var hotkeyId = wParam.ToInt32();
            if (_handlers.TryGetValue(hotkeyId, out var callback))
            {
                _dispatcherQueue.TryEnqueue(() => callback());
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HotkeyManager));
        }
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);
}
