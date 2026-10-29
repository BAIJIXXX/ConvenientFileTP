using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ConvenientFileTP.UI.Widget
{
    /// <summary>
    /// Window chrome / input helpers for the desktop widget.
    ///
    /// The widget must not take keyboard focus from the user's active application, but it
    /// still needs reliable mouse input (press / drag / release, including when the pointer
    /// leaves the tiny window). WS_EX_NOACTIVATE silently breaks WPF mouse capture in that
    /// situation, so instead we keep a normal activatable HWND and answer WM_MOUSEACTIVATE
    /// with MA_NOACTIVATE. Capture is additionally backed by a Win32 capture and an explicit
    /// button-state poll so a lost mouse-up can never leave the widget stuck in "pressed".
    /// </summary>
    internal static class WidgetNative
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        private const int VK_LBUTTON = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
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

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        /// <summary>Marks the window as a tool window (no taskbar entry, no Alt+Tab).</summary>
        public static void ApplyToolWindow(IntPtr handle)
        {
            try
            {
                if (handle == IntPtr.Zero) return;
                int style = GetWindowLong32(handle, GWL_EXSTYLE);
                SetWindowLong32(handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
            }
            catch
            {
                // Cosmetic only.
            }
        }

        /// <summary>
        /// Keeps the widget from stealing activation while still behaving like a normal
        /// window for the WPF input stack (capture, mouse-up delivery, hit testing).
        /// </summary>
        public static void PreventActivation(Visual visual)
        {
            try
            {
                var source = PresentationSource.FromVisual(visual) as HwndSource;
                if (source == null) return;
                source.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                {
                    if (msg == WM_MOUSEACTIVATE)
                    {
                        handled = true;
                        return new IntPtr(MA_NOACTIVATE);
                    }
                    return IntPtr.Zero;
                });
            }
            catch
            {
                // Cosmetic only.
            }
        }

        public static void CaptureTo(IntPtr handle)
        {
            try { if (handle != IntPtr.Zero) SetCapture(handle); } catch { }
        }

        public static void ReleaseCaptureFrom(IntPtr handle)
        {
            try { ReleaseCapture(); } catch { }
        }

        public static bool IsLeftButtonDown()
        {
            try { return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
            catch { return false; }
        }

        public static Point GetCursorPoint()
        {
            POINT point;
            if (GetCursorPos(out point)) return new Point(point.X, point.Y);
            return new Point(0, 0);
        }

        public static IntPtr WindowFromPoint(Point screenPoint)
        {
            POINT point;
            point.X = (int)screenPoint.X;
            point.Y = (int)screenPoint.Y;
            return WindowFromPoint(point);
        }

        /// <summary>
        /// True when the physical cursor position is inside the HWND's screen rectangle.
        /// WindowFromPoint ignores fully transparent layered pixels (the widget window is
        /// mostly padding), so the rectangle test is what keeps hover state stable while the
        /// window morphs between the thin bar and the round ball.
        /// </summary>
        public static bool IsPointInWindow(IntPtr handle, Point screenPoint)
        {
            try
            {
                if (handle == IntPtr.Zero) return false;
                RECT rect;
                if (!GetWindowRect(handle, out rect)) return false;
                int x = (int)screenPoint.X;
                int y = (int)screenPoint.Y;
                return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
            }
            catch
            {
                return false;
            }
        }
    }
}
