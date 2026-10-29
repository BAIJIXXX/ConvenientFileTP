using System;
using System.Windows;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI.Widget
{
    internal enum WidgetEdge
    {
        None = 0,
        Left,
        Right,
        Top,
        Bottom
    }

    /// <summary>Monitor work area expressed in WPF device independent pixels.</summary>
    internal sealed class WidgetScreenInfo
    {
        public double Left;
        public double Top;
        public double Right;
        public double Bottom;
        public double Scale = 1.0;

        public double Width { get { return Right - Left; } }
        public double Height { get { return Bottom - Top; } }
    }

    /// <summary>
    /// Screen / edge geometry helpers. Multi-monitor support comes from
    /// <see cref="System.Windows.Forms.Screen.FromPoint"/>, DPI conversion from the window's
    /// presentation source (the process is PerMonitorV2 aware, see app.manifest).
    /// </summary>
    internal static class WidgetScreen
    {
        public static bool IsVertical(WidgetEdge edge)
        {
            return edge == WidgetEdge.Left || edge == WidgetEdge.Right;
        }

        public static string EdgeName(WidgetEdge edge)
        {
            switch (edge)
            {
                case WidgetEdge.Left: return "left";
                case WidgetEdge.Right: return "right";
                case WidgetEdge.Top: return "top";
                case WidgetEdge.Bottom: return "bottom";
                default: return "right";
            }
        }

        public static WidgetEdge ParseEdge(string value)
        {
            if (string.IsNullOrEmpty(value)) return WidgetEdge.Right;
            switch (value.Trim().ToLowerInvariant())
            {
                case "left": return WidgetEdge.Left;
                case "right": return WidgetEdge.Right;
                case "top": return WidgetEdge.Top;
                case "bottom": return WidgetEdge.Bottom;
                default: return WidgetEdge.None;
            }
        }

        /// <summary>Work area of the monitor the window currently lives on (DIP).</summary>
        public static WidgetScreenInfo ForVisual(FrameworkElement visual)
        {
            if (visual == null) return ForPrimary();

            try
            {
                var source = PresentationSource.FromVisual(visual);
                if (source == null || source.CompositionTarget == null) return ForPrimary();

                double scale = source.CompositionTarget.TransformToDevice.M11;
                if (scale < 0.5 || scale > 4.0) scale = 1.0;

                double w = visual.ActualWidth > 0 ? visual.ActualWidth : 120.0;
                double h = visual.ActualHeight > 0 ? visual.ActualHeight : 120.0;
                Point center = visual.PointToScreen(new Point(w / 2.0, h / 2.0));

                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y)));
                if (screen == null) return ForPrimary();
                return FromScreen(screen, scale);
            }
            catch (Exception ex)
            {
                Log.Debug("浮窗获取屏幕信息失败，回退主屏: " + ex.Message);
                return ForPrimary();
            }
        }

        public static WidgetScreenInfo ForPrimary()
        {
            try
            {
                double scale = 1.0;
                try
                {
                    using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    {
                        if (g != null && g.DpiX > 0) scale = g.DpiX / 96.0;
                    }
                }
                catch
                {
                    scale = 1.0;
                }
                if (scale < 0.5 || scale > 4.0) scale = 1.0;

                var screen = System.Windows.Forms.Screen.PrimaryScreen;
                if (screen != null) return FromScreen(screen, scale);
            }
            catch
            {
                // ignore and return a nominal 1920x1080 work area
            }

            return new WidgetScreenInfo { Left = 0, Top = 0, Right = 1920, Bottom = 1080, Scale = 1.0 };
        }

        private static WidgetScreenInfo FromScreen(System.Windows.Forms.Screen screen, double scale)
        {
            var wa = screen.WorkingArea;
            return new WidgetScreenInfo
            {
                Left = wa.Left / scale,
                Top = wa.Top / scale,
                Right = wa.Right / scale,
                Bottom = wa.Bottom / scale,
                Scale = scale
            };
        }

        /// <summary>
        /// Ball window rectangle (<paramref name="windowSize"/> includes the transparent shadow
        /// padding) anchored flush to the requested edge at the given 0..1 offset ratio.
        /// </summary>
        public static Rect BallRect(WidgetScreenInfo info, WidgetEdge edge, double ratio, double windowSize)
        {
            ratio = Clamp01(ratio);
            double left;
            double top;

            switch (edge)
            {
                case WidgetEdge.Left:
                    left = info.Left;
                    top = info.Top + ratio * info.Height - windowSize / 2.0;
                    break;
                case WidgetEdge.Top:
                    left = info.Left + ratio * info.Width - windowSize / 2.0;
                    top = info.Top;
                    break;
                case WidgetEdge.Bottom:
                    left = info.Left + ratio * info.Width - windowSize / 2.0;
                    top = info.Bottom - windowSize;
                    break;
                default: // right
                    left = info.Right - windowSize;
                    top = info.Top + ratio * info.Height - windowSize / 2.0;
                    break;
            }

            return new Rect(
                Clamp(left, info.Left, Math.Max(info.Left, info.Right - windowSize)),
                Clamp(top, info.Top, Math.Max(info.Top, info.Bottom - windowSize)),
                windowSize,
                windowSize);
        }

        /// <summary>Collapsed bar window rectangle for the requested edge.</summary>
        public static Rect BarRect(WidgetScreenInfo info, WidgetEdge edge, double ratio,
            double thickness, double length)
        {
            ratio = Clamp01(ratio);
            double left;
            double top;
            double width;
            double height;

            if (IsVertical(edge))
            {
                width = thickness;
                height = length;
                left = edge == WidgetEdge.Left ? info.Left : info.Right - thickness;
                top = info.Top + ratio * info.Height - length / 2.0;
                top = Clamp(top, info.Top, Math.Max(info.Top, info.Bottom - length));
            }
            else
            {
                width = length;
                height = thickness;
                top = edge == WidgetEdge.Top ? info.Top : info.Bottom - thickness;
                left = info.Left + ratio * info.Width - length / 2.0;
                left = Clamp(left, info.Left, Math.Max(info.Left, info.Right - length));
            }

            return new Rect(left, top, width, height);
        }

        /// <summary>
        /// Returns the edge within <paramref name="threshold"/> DIP of the ball's visual bounds,
        /// or <see cref="WidgetEdge.None"/> when the ball is not close enough to any edge.
        /// </summary>
        public static WidgetEdge NearestEdge(WidgetScreenInfo info, Rect ballRect, double threshold)
        {
            double left = ballRect.Left - info.Left;
            double right = info.Right - ballRect.Right;
            double top = ballRect.Top - info.Top;
            double bottom = info.Bottom - ballRect.Bottom;

            WidgetEdge edge = WidgetEdge.None;
            double best = double.MaxValue;

            if (left < best) { best = left; edge = WidgetEdge.Left; }
            if (right < best) { best = right; edge = WidgetEdge.Right; }
            if (top < best) { best = top; edge = WidgetEdge.Top; }
            if (bottom < best) { best = bottom; edge = WidgetEdge.Bottom; }

            if (best > threshold) return WidgetEdge.None;
            return edge;
        }

        /// <summary>0..1 position of the ball centre along the requested edge.</summary>
        public static double RatioAlongEdge(WidgetScreenInfo info, WidgetEdge edge, Rect ballRect)
        {
            if (IsVertical(edge))
            {
                double centerY = ballRect.Top + ballRect.Height / 2.0;
                if (info.Height <= 0) return 0.5;
                return Clamp01((centerY - info.Top) / info.Height);
            }

            double centerX = ballRect.Left + ballRect.Width / 2.0;
            if (info.Width <= 0) return 0.5;
            return Clamp01((centerX - info.Left) / info.Width);
        }

        public static Rect ClampInside(WidgetScreenInfo info, Rect rect)
        {
            double left = Clamp(rect.Left, info.Left, Math.Max(info.Left, info.Right - rect.Width));
            double top = Clamp(rect.Top, info.Top, Math.Max(info.Top, info.Bottom - rect.Height));
            return new Rect(left, top, rect.Width, rect.Height);
        }

        public static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0.5;
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
