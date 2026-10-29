using System.Windows;
using System.Windows.Media;

namespace ConvenientFileTP.UI.Widget
{
    /// <summary>Visual state of the floating widget, mapped to the palette in Theme.xaml.</summary>
    internal enum WidgetState
    {
        /// <summary>Ready, no transfer running: accent purple.</summary>
        Idle,

        /// <summary>Sending files: danger red.</summary>
        Sending,

        /// <summary>Receiving files: success green.</summary>
        Receiving,

        /// <summary>Received files not looked at yet: warning yellow.</summary>
        Unviewed
    }

    internal static class WidgetPalette
    {
        public static Color ColorFor(WidgetState state)
        {
            switch (state)
            {
                case WidgetState.Sending:
                    return ResourceColor("Danger", Color.FromRgb(0xF8, 0x71, 0x71));
                case WidgetState.Receiving:
                    return ResourceColor("Success", Color.FromRgb(0x34, 0xD3, 0x99));
                case WidgetState.Unviewed:
                    return ResourceColor("Warning", Color.FromRgb(0xFB, 0xBF, 0x24));
                default:
                    return ResourceColor("Accent", Color.FromRgb(0x8B, 0x5C, 0xF6));
            }
        }

        public static Color AccentLightColor()
        {
            return ResourceColor("AccentLight", Color.FromRgb(0xA7, 0x8B, 0xFA));
        }

        private static Color ResourceColor(string key, Color fallback)
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    var brush = app.TryFindResource(key) as SolidColorBrush;
                    if (brush != null) return brush.Color;
                }
            }
            catch
            {
                // Theme not ready (unit-style startup); fall back to the hard-coded palette.
            }
            return fallback;
        }
    }
}
