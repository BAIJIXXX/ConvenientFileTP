using System;
using System.Runtime.InteropServices;

namespace ConvenientFileTP.Native
{
    public static class Win32
    {
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public const int SW_RESTORE = 9;

        /// <summary>Enable per-monitor v2 DPI awareness before WPF initializes.</summary>
        public static void EnablePerMonitorDpi()
        {
            try
            {
                if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }
        }

        public static void BringToFront(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                if (GetForegroundWindow() == hwnd) return;

                // SetForegroundWindow is ignored unless the calling process owns the
                // foreground lock. Temporarily attaching to the foreground thread's input
                // queue is the standard way to make the request reliable.
                IntPtr foreground = GetForegroundWindow();
                uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
                uint currentThread = GetCurrentThreadId();
                bool attached = false;
                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(foregroundThread, currentThread, true);
                }
                SetForegroundWindow(hwnd);
                if (attached) AttachThreadInput(foregroundThread, currentThread, false);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// Rounds a window's corners on Windows 11. Silently ignored on older builds.
        /// </summary>
        public static void TryEnableRoundedCorners(IntPtr hwnd)
        {
            try
            {
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        /// <summary>
        /// Gives the app its own taskbar group so Windows uses the window icon
        /// instead of the signed powershell.exe host icon.
        /// </summary>
        public static void SetAppUserModelId(string appId)
        {
            try { SetCurrentProcessExplicitAppUserModelID(appId); } catch { }
        }

        /// <summary>Prevents Windows from shrinking the window when the taskbar auto-hides.</summary>
        public static void TrySetBorderless(IntPtr hwnd)
        {
            try
            {
                int attr = 1; // DWMWA_NCRENDERING_ENABLED concept replacement not needed
                DwmSetWindowAttribute(hwnd, 2, ref attr, sizeof(int));
            }
            catch { }
        }
    }
}
