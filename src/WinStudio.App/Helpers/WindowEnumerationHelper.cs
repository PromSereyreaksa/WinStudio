using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinStudio.App.Models;

namespace WinStudio.App.Helpers;

public static class WindowEnumerationHelper
{
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;

    public static IReadOnlyList<WindowTargetOption> GetRecordableWindows()
    {
        var currentProcessId = Environment.ProcessId;
        var results = new List<WindowTargetOption>();
        var handle = GCHandle.Alloc(new EnumerationContext(currentProcessId, results));

        try
        {
            EnumWindows(
                static (hwnd, lParam) =>
                {
                    var state = GCHandle.FromIntPtr(lParam);
                    var context = (EnumerationContext)state.Target!;
                    context.TryAddWindow(hwnd);
                    return true;
                },
                GCHandle.ToIntPtr(handle));
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        return results
            .OrderBy(static option => option.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private sealed class EnumerationContext(int currentProcessId, List<WindowTargetOption> results)
    {
        public void TryAddWindow(IntPtr hwnd)
        {
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                {
                    return;
                }

                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                {
                    return;
                }

                var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                if ((exStyle & WsExToolWindow) != 0)
                {
                    return;
                }

                if (IsCloaked(hwnd))
                {
                    return;
                }

                _ = GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == currentProcessId)
                {
                    return;
                }

                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return;
                }

                var buffer = new StringBuilder(length + 1);
                _ = GetWindowText(hwnd, buffer, buffer.Capacity);
                var title = buffer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    return;
                }

                var processName = TryGetProcessName(processId);
                var label = string.IsNullOrWhiteSpace(processName)
                    ? title
                    : $"{title} ({processName})";

                results.Add(new WindowTargetOption(hwnd, label));
            }
            catch
            {
                // Skip windows that cannot be queried.
            }
        }

        private static string? TryGetProcessName(uint processId)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) != 0)
            {
                return false;
            }

            return cloaked != 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}
