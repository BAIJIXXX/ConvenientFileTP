using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ConvenientFileTP.Core;
using Microsoft.Win32;

namespace ConvenientFileTP.UI
{
    /// <summary>How the window/panel palette follows day/night style.</summary>
    public enum ThemeMode
    {
        System,
        Light,
        Dark
    }

    /// <summary>
    /// Applies the day/night palette and the user accent colour to the WPF resource brushes.
    /// XAML uses DynamicResource for every palette key, so swapping brush instances updates
    /// already-created windows, the main window and the floating widget instantly.
    /// </summary>
    public static class ThemeManager
    {
        public static event EventHandler ThemeChanged;

        public static readonly Color DefaultAccent = Color.FromRgb(0x8B, 0x5C, 0xF6);

        private static ThemeMode _mode = ThemeMode.Dark;
        private static bool _isDark = true;
        private static Color _accent = DefaultAccent;
        private static bool _initialized;

        public static ThemeMode Mode { get { return _mode; } }
        public static bool IsDark { get { return _isDark; } }
        public static Color AccentColor { get { return _accent; } }

        public static string ModeText
        {
            get
            {
                if (_mode == ThemeMode.Light) return "浅色";
                if (_mode == ThemeMode.Dark) return "深色";
                return "跟随系统";
            }
        }

        public static void Initialize(AppConfig config)
        {
            if (_initialized) return;
            _initialized = true;

            _mode = ParseMode(config != null ? config.ThemeMode : null);
            Color parsed;
            _accent = TryParseColor(config != null ? config.AccentColor : null, out parsed) ? parsed : DefaultAccent;

            ApplyBrushes(false);

            try
            {
                SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            }
            catch (Exception ex)
            {
                Log.Warn("无法监听系统主题变化: " + ex.Message);
            }

            Log.Info("主题初始化: mode=" + _mode + ", dark=" + _isDark + ", accent=" + ToHex(_accent));
        }

        public static void SetMode(ThemeMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            ApplyBrushes(true);
        }

        public static void SetAccent(Color color)
        {
            // Keep the alpha channel predictable for all derived shades.
            color = Color.FromRgb(color.R, color.G, color.B);
            if (EqualColor(_accent, color)) return;
            _accent = color;
            ApplyBrushes(true);
        }

        /// <summary>Re-applies the current mode/accent (also refreshes when the OS theme changes).</summary>
        public static void Refresh()
        {
            ApplyBrushes(true);
        }

        public static string ToHex(Color color)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        }

        public static bool TryParseColor(string text, out Color color)
        {
            color = DefaultAccent;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                object parsed = ColorConverter.ConvertFromString(text);
                if (parsed is Color)
                {
                    color = (Color)parsed;
                    return true;
                }
            }
            catch
            {
                // Ignore invalid strings from a hand-edited config file.
            }
            return false;
        }

        public static ThemeMode ParseMode(string text)
        {
            if (string.Equals(text, "light", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Light;
            if (string.Equals(text, "system", StringComparison.OrdinalIgnoreCase)) return ThemeMode.System;
            return ThemeMode.Dark;
        }

        public static string ModeKey(ThemeMode mode)
        {
            if (mode == ThemeMode.Light) return "light";
            if (mode == ThemeMode.System) return "system";
            return "dark";
        }

        private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_mode != ThemeMode.System) return;
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    ApplyBrushes(true);
                }));
            }
            catch
            {
                // Ignore shutdown races.
            }
        }

        private static void ApplyBrushes(bool notify)
        {
            _isDark = _mode == ThemeMode.Dark || (_mode == ThemeMode.System && !IsSystemLightMode());

            Color accent = _accent;
            Color accentLight = _isDark ? Lighten(accent, 0.20) : Lighten(accent, 0.10);
            Color accentDark = _isDark ? Darken(accent, 0.22) : Darken(accent, 0.14);
            Color accentSoft = _isDark
                ? Blend(accent, ColorFromHex("#241F36"), 0.28)
                : Blend(accent, Colors.White, 0.86);
            Color onAccentDark = ColorFromHex("#1F1A2E");
            Color onAccent = ContrastRatio(accent, Colors.White) >= ContrastRatio(accent, onAccentDark)
                ? Colors.White
                : onAccentDark;
            Color onAccentSoft = _isDark ? Lighten(accent, 0.45) : Darken(accent, 0.35);

            SetBrush("Accent", accent);
            SetBrush("AccentLight", accentLight);
            SetBrush("AccentDark", accentDark);
            SetBrush("AccentSoft", accentSoft);
            SetBrush("OnAccent", onAccent);
            SetBrush("OnAccentSoft", onAccentSoft);

            if (_isDark)
            {
                SetBrush("WidgetShell", ColorFromArgb(0xE6, 0x14, 0x11, 0x1F));
                SetBrush("WidgetBorder", ColorFromHex("#14111F"));
            }
            else
            {
                SetBrush("WidgetShell", ColorFromArgb(0xF2, 0xFF, 0xFF, 0xFF));
                SetBrush("WidgetBorder", ColorFromHex("#14111F"));
            }

            foreach (KeyValuePair<string, Color> pair in _isDark ? DarkPalette() : LightPalette())
            {
                SetBrush(pair.Key, pair.Value);
            }

            if (notify)
            {
                try
                {
                    EventHandler handler = ThemeChanged;
                    if (handler != null) handler(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Log.Error("主题切换通知失败", ex);
                }
                Log.Info("主题已应用: mode=" + _mode + " (" + (_isDark ? "dark" : "light") + "), accent=" + ToHex(_accent));
            }
        }

        private static void SetBrush(string key, Color color)
        {
            Application app = Application.Current;
            if (app == null) return;
            try
            {
                app.Resources[key] = new SolidColorBrush(color);
            }
            catch (Exception ex)
            {
                Log.Error("设置主题画刷失败: " + key, ex);
            }
        }

        private static bool IsSystemLightMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value is int) return (int)value == 1;
                    }
                }
            }
            catch
            {
                // Fall back to the dark default if the registry cannot be read.
            }
            return false;
        }

        private static Dictionary<string, Color> DarkPalette()
        {
            return new Dictionary<string, Color>
            {
                { "BgDeep", ColorFromHex("#14111F") },
                { "BgPanel", ColorFromHex("#1B1729") },
                { "BgCard", ColorFromHex("#241F36") },
                { "BgCardHover", ColorFromHex("#2D2746") },
                { "BgInput", ColorFromHex("#1E1A2E") },
                { "Border", ColorFromHex("#3A3256") },
                { "BorderSoft", ColorFromHex("#2C2642") },
                { "TextPrimary", ColorFromHex("#F3F0FF") },
                { "TextSecondary", ColorFromHex("#B6ADCE") },
                { "TextMuted", ColorFromHex("#7C7295") },
                { "Success", ColorFromHex("#34D399") },
                { "SuccessSoft", ColorFromHex("#123A2E") },
                { "Danger", ColorFromHex("#F87171") },
                { "DangerSoft", ColorFromHex("#3A1A1F") },
                { "DangerHover", ColorFromHex("#4A2027") },
                { "DangerPressed", ColorFromHex("#2E1218") },
                { "Warning", ColorFromHex("#FBBF24") },
                { "WarningSoft", ColorFromHex("#3A2E12") }
            };
        }

        private static Dictionary<string, Color> LightPalette()
        {
            return new Dictionary<string, Color>
            {
                { "BgDeep", ColorFromHex("#F2F0F7") },
                { "BgPanel", ColorFromHex("#FAF9FD") },
                { "BgCard", ColorFromHex("#FFFFFF") },
                { "BgCardHover", ColorFromHex("#F2EEFA") },
                { "BgInput", ColorFromHex("#F5F3FA") },
                { "Border", ColorFromHex("#D8D2E6") },
                { "BorderSoft", ColorFromHex("#E7E2F0") },
                { "TextPrimary", ColorFromHex("#1F1A2E") },
                { "TextSecondary", ColorFromHex("#56506B") },
                { "TextMuted", ColorFromHex("#8B839D") },
                { "Success", ColorFromHex("#0E9F6E") },
                { "SuccessSoft", ColorFromHex("#D8F5EA") },
                { "Danger", ColorFromHex("#DC2626") },
                { "DangerSoft", ColorFromHex("#FCE4E4") },
                { "DangerHover", ColorFromHex("#F7DCDB") },
                { "DangerPressed", ColorFromHex("#F2C9C8") },
                { "Warning", ColorFromHex("#D97706") },
                { "WarningSoft", ColorFromHex("#FDF0D5") }
            };
        }

        private static Color ColorFromHex(string hex)
        {
            Color color;
            return TryParseColor(hex, out color) ? color : DefaultAccent;
        }

        private static Color ColorFromArgb(byte a, byte r, byte g, byte b)
        {
            return Color.FromArgb(a, r, g, b);
        }

        private static bool EqualColor(Color a, Color b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
        }

        private static Color Lighten(Color color, double amount)
        {
            return Blend(color, Colors.White, amount);
        }

        private static Color Darken(Color color, double amount)
        {
            return Blend(color, Colors.Black, amount);
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private static double Luminance(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        }

        private static double ContrastRatio(Color a, Color b)
        {
            double la = RelativeLuminance(a);
            double lb = RelativeLuminance(b);
            double high = Math.Max(la, lb);
            double low = Math.Min(la, lb);
            return (high + 0.05) / (low + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126 * LinearChannel(color.R)
                 + 0.7152 * LinearChannel(color.G)
                 + 0.0722 * LinearChannel(color.B);
        }

        private static double LinearChannel(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
