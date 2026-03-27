using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace WinStudio.App.Helpers;

public static class WindowHelper
{
    private const int DefaultDpi = 96;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint WdaNone = 0x0;
    private const uint WdaExcludeFromCapture = 0x11;

    public static IntPtr GetWindowHandle(Window window)
    {
        return WindowNative.GetWindowHandle(window);
    }

    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    public static void ConfigureRecordWindow(Window window)
    {
        const int effectiveWidth = 860;
        const int effectiveHeight = 760;

        try
        {
            var hwnd = GetWindowHandle(window);
            var width = ScaleForDpi(effectiveWidth, hwnd);
            var height = ScaleForDpi(effectiveHeight, hwnd);
            var appWindow = GetAppWindow(window);
            var size = ConstrainToWorkArea(appWindow, width, height, 48);
            appWindow.Resize(size);
            CenterOnPrimary(appWindow, size.Width, size.Height);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;
            }

            TrySetCaptureExclusion(hwnd, excludeFromCapture: false);
        }
        catch
        {
            // Keep startup resilient even if AppWindow APIs fail on some environments.
        }
    }

    public static void ConfigureFloatingToolbar(
        Window window,
        nint avoidWindowHandle = 0,
        int effectiveWidth = 360,
        int effectiveHeight = 54,
        int effectiveMargin = 20)
    {
        try
        {
            var hwnd = GetWindowHandle(window);
            var width = ScaleForDpi(effectiveWidth, hwnd);
            var height = ScaleForDpi(effectiveHeight, hwnd);
            var margin = ScaleForDpi(effectiveMargin, hwnd);
            var appWindow = GetAppWindow(window);
            var size = ConstrainToWorkArea(appWindow, width, height, margin * 2);
            appWindow.Resize(size);
            appWindow.IsShownInSwitchers = false;

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }

            PositionFloatingToolbar(appWindow, size.Width, size.Height, margin, avoidWindowHandle);
            TrySetCaptureExclusion(hwnd, excludeFromCapture: true);
        }
        catch
        {
            // Keep recording flow alive even if toolbar window configuration is partially unsupported.
        }
    }

    private static void CenterOnPrimary(AppWindow appWindow, int width, int height)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return;
        }

        var x = displayArea.WorkArea.X + ((displayArea.WorkArea.Width - width) / 2);
        var y = displayArea.WorkArea.Y + ((displayArea.WorkArea.Height - height) / 2);
        appWindow.Move(new PointInt32(x, y));
    }

    private static SizeInt32 ConstrainToWorkArea(AppWindow appWindow, int width, int height, int margin)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return new SizeInt32(width, height);
        }

        var maxWidth = Math.Max(320, displayArea.WorkArea.Width - margin);
        var maxHeight = Math.Max(240, displayArea.WorkArea.Height - margin);
        return new SizeInt32(Math.Min(width, maxWidth), Math.Min(height, maxHeight));
    }

    private static int ScaleForDpi(int value, IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / (double)DefaultDpi : 1d;
        return Math.Max(1, (int)Math.Round(value * scale));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static void PositionFloatingToolbar(AppWindow appWindow, int width, int height, int margin, nint avoidWindowHandle)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return;
        }

        if (avoidWindowHandle != 0 && TryGetWindowBounds(avoidWindowHandle, out var avoidBounds))
        {
            var x = Math.Clamp(
                avoidBounds.X + ((avoidBounds.Width - width) / 2),
                displayArea.WorkArea.X,
                (displayArea.WorkArea.X + displayArea.WorkArea.Width) - width);

            var aboveY = avoidBounds.Y - height - margin;
            if (aboveY >= displayArea.WorkArea.Y)
            {
                appWindow.Move(new PointInt32(x, aboveY));
                return;
            }

            var belowY = avoidBounds.Y + avoidBounds.Height + margin;
            if (belowY + height <= displayArea.WorkArea.Y + displayArea.WorkArea.Height)
            {
                appWindow.Move(new PointInt32(x, belowY));
                return;
            }
        }

        MoveTopCenter(appWindow, width, height, margin);
    }

    private static void MoveTopCenter(AppWindow appWindow, int width, int height, int topMargin)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return;
        }

        var x = displayArea.WorkArea.X + ((displayArea.WorkArea.Width - width) / 2);
        var y = displayArea.WorkArea.Y + topMargin;
        appWindow.Move(new PointInt32(x, y));
    }

    private static bool TryGetWindowBounds(nint hwnd, out RectInt32 bounds)
    {
        bounds = default;

        if (DwmGetWindowAttribute((IntPtr)hwnd, DwmwaExtendedFrameBounds, out RECT rect, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect((IntPtr)hwnd, out rect))
        {
            return false;
        }

        bounds = new RectInt32(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private static void TrySetCaptureExclusion(IntPtr hwnd, bool excludeFromCapture)
    {
        try
        {
            _ = SetWindowDisplayAffinity(
                hwnd,
                excludeFromCapture ? WdaExcludeFromCapture : WdaNone
            );
        }
        catch
        {
            // Exclusion is best-effort. Keep recording functional on unsupported systems.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
