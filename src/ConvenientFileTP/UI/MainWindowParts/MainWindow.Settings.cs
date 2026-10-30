using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI
{
    /// <summary>主窗口：设置页面。</summary>
    public partial class MainWindow
    {
        private static readonly string[] AccentPresets =
        {
            "#8B5CF6", "#6366F1", "#3B82F6", "#06B6D4", "#10B981", "#F59E0B", "#EF4444", "#EC4899"
        };

        private bool _appearanceSyncing;

        private void LoadSettingsIntoUi()
        {
            try
            {
                var config = AppServices.Config;
                if (config == null) return;

                if (DeviceNameBox != null) DeviceNameBox.Text = config.DeviceName ?? "";
                if (DownloadDirBox != null) DownloadDirBox.Text = config.DownloadDirectory ?? "";
                if (AutoAcceptToggle != null) AutoAcceptToggle.IsChecked = config.AutoAcceptFiles;
                if (NotifyToggle != null) NotifyToggle.IsChecked = config.ShowNotifications;
                if (CloseToTrayToggle != null) CloseToTrayToggle.IsChecked = config.CloseToTray;

                RefreshAppearanceUi();
                UpdateSettingsInfo();
            }
            catch (Exception ex)
            {
                Log.Error("读取设置失败", ex);
            }
        }

        /// <summary>Re-selects the appearance controls after the theme was changed anywhere.</summary>
        public void RefreshAppearanceUi()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshAppearanceUi));
                return;
            }

            _appearanceSyncing = true;
            try
            {
                var mode = ThemeManager.Mode;
                if (ThemeDarkRadio != null) ThemeDarkRadio.IsChecked = mode == ThemeMode.Dark;
                if (ThemeLightRadio != null) ThemeLightRadio.IsChecked = mode == ThemeMode.Light;
                if (ThemeSystemRadio != null) ThemeSystemRadio.IsChecked = mode == ThemeMode.System;

                RebuildAccentSwatches();

                if (AccentValueText != null)
                {
                    AccentValueText.Text = ThemeManager.ToHex(ThemeManager.AccentColor) + " · " +
                                           (ThemeManager.IsDark ? "夜晚配色" : "白天配色");
                }
            }
            finally
            {
                _appearanceSyncing = false;
            }
        }

        private void RebuildAccentSwatches()
        {
            if (AccentSwatchPanel == null) return;
            AccentSwatchPanel.Children.Clear();

            string current = ThemeManager.ToHex(ThemeManager.AccentColor);
            var style = TryFindResource("AccentSwatchButton") as Style;

            foreach (string hex in AccentPresets)
            {
                Color color;
                if (!ThemeManager.TryParseColor(hex, out color)) continue;

                var swatch = new System.Windows.Controls.Button
                {
                    Background = new SolidColorBrush(color),
                    Tag = hex,
                    ToolTip = hex,
                    BorderThickness = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)
                        ? new Thickness(2)
                        : new Thickness(1),
                    BorderBrush = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)
                        ? (Brush)(TryFindResource("TextPrimary") ?? Brushes.White)
                        : (Brush)(TryFindResource("Border") ?? Brushes.Gray)
                };
                System.Windows.Automation.AutomationProperties.SetName(swatch, hex);
                if (style != null) swatch.Style = style;
                swatch.Click += AccentSwatch_Click;
                AccentSwatchPanel.Children.Add(swatch);
            }
        }

        private void ThemeMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_appearanceSyncing) return;
            var radio = sender as System.Windows.Controls.RadioButton;
            string tag = radio != null ? radio.Tag as string : null;
            AppServices.SetThemeMode(ThemeManager.ParseMode(tag));
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            string hex = button != null ? button.Tag as string : null;
            Color color;
            if (hex != null && ThemeManager.TryParseColor(hex, out color))
            {
                AppServices.SetAccentColor(color);
            }
        }

        private bool _pickerSyncing;
        private double _pickerHue = 265;
        private double _pickerSat = 0.62;
        private double _pickerVal = 0.96;

        private void CustomAccent_Click(object sender, RoutedEventArgs e)
        {
            OpenAccentPicker();
        }

        private void OpenAccentPicker()
        {
            if (AccentPickerOverlay == null) return;
            AccentPickerOverlay.Visibility = Visibility.Visible;
            SyncPickerFromColor(ThemeManager.AccentColor);
            Dispatcher.BeginInvoke(new Action(() => SyncPickerFromColor(ThemeManager.AccentColor)));
        }

        private void CloseAccentPicker_Click(object sender, RoutedEventArgs e)
        {
            if (AccentPickerOverlay != null) AccentPickerOverlay.Visibility = Visibility.Collapsed;
        }

        private void AccentPickerOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (AccentPickerOverlay != null) AccentPickerOverlay.Visibility = Visibility.Collapsed;
        }

        private void AccentPickerCard_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void SvArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SvArea.CaptureMouse();
            UpdateSvFromPointer(e.GetPosition(SvArea));
            e.Handled = true;
        }

        private void SvArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SvArea.IsMouseCaptured && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                UpdateSvFromPointer(e.GetPosition(SvArea));
            }
        }

        private void SvArea_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SvArea.IsMouseCaptured) SvArea.ReleaseMouseCapture();
            PersistCurrentPickerColor();
            e.Handled = true;
        }

        private void SvArea_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // While dragging, keep updating through mouse capture.
        }

        private void HueArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            HueArea.CaptureMouse();
            UpdateHueFromPointer(e.GetPosition(HueArea));
            e.Handled = true;
        }

        private void HueArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (HueArea.IsMouseCaptured && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                UpdateHueFromPointer(e.GetPosition(HueArea));
            }
        }

        private void HueArea_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (HueArea.IsMouseCaptured) HueArea.ReleaseMouseCapture();
            PersistCurrentPickerColor();
            e.Handled = true;
        }

        private void UpdateSvFromPointer(Point point)
        {
            double width = SvArea.ActualWidth > 1 ? SvArea.ActualWidth : 236;
            double height = SvArea.ActualHeight > 1 ? SvArea.ActualHeight : 150;
            _pickerSat = Clamp01(point.X / width);
            _pickerVal = Clamp01(1.0 - point.Y / height);
            ApplyPickerColor(HsvToColor(_pickerHue, _pickerSat, _pickerVal), false, true);
        }

        private void UpdateHueFromPointer(Point point)
        {
            double width = HueArea.ActualWidth > 1 ? HueArea.ActualWidth : 236;
            _pickerHue = Clamp01(point.X / width) * 360.0;
            RebuildSaturationGradient();
            ApplyPickerColor(HsvToColor(_pickerHue, _pickerSat, _pickerVal), false, true);
        }

        private void ApplyPickerColor(Color color, bool persist, bool syncHexText)
        {
            bool previous = _pickerSyncing;
            _pickerSyncing = true;
            try
            {
                if (AccentPreviewSwatch != null) AccentPreviewSwatch.Background = new SolidColorBrush(color);
                if (AccentPreviewText != null)
                {
                    AccentPreviewText.Text = "当前色号 " + ThemeManager.ToHex(color);
                }
                if (syncHexText && AccentHexInput != null)
                {
                    AccentHexInput.Text = ThemeManager.ToHex(color);
                }
                AppServices.SetAccentColor(color, persist);
                UpdatePickerPositions(color);
            }
            finally
            {
                _pickerSyncing = previous;
            }
        }

        private void PersistCurrentPickerColor()
        {
            AppServices.SetAccentColor(HsvToColor(_pickerHue, _pickerSat, _pickerVal), true);
        }

        private void SyncPickerFromColor(Color color)
        {
            double h, s, v;
            ColorToHsv(color, out h, out s, out v);
            _pickerHue = h;
            _pickerSat = s;
            _pickerVal = v;
            RebuildSaturationGradient();
            ApplyPickerColor(color, false, true);
            UpdatePickerPositions(color);
        }

        private void RebuildSaturationGradient()
        {
            if (SvSaturationRect == null) return;
            var gradient = new LinearGradientBrush(Colors.White, HsvToColor(_pickerHue, 1, 1), 0);
            SvSaturationRect.Fill = gradient;
        }

        private void UpdatePickerPositions(Color color)
        {
            if (SvThumb != null && SvArea != null)
            {
                double width = SvArea.ActualWidth > 1 ? SvArea.ActualWidth : 236;
                double height = SvArea.ActualHeight > 1 ? SvArea.ActualHeight : 150;
                System.Windows.Controls.Canvas.SetLeft(SvThumb, _pickerSat * width - 7);
                System.Windows.Controls.Canvas.SetTop(SvThumb, (1.0 - _pickerVal) * height - 7);
                SvThumb.Fill = new SolidColorBrush(color);
            }
            if (HueThumb != null && HueArea != null)
            {
                double width = HueArea.ActualWidth > 1 ? HueArea.ActualWidth : 236;
                System.Windows.Controls.Canvas.SetLeft(HueThumb, Clamp01(_pickerHue / 360.0) * (width - 4));
            }
        }

        private void AccentHexInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_pickerSyncing) return;
            Color color;
            if (!TryParseHexText(AccentHexInput != null ? AccentHexInput.Text : null, out color)) return;
            double h, s, v;
            ColorToHsv(color, out h, out s, out v);
            _pickerHue = h;
            _pickerSat = s;
            _pickerVal = v;
            RebuildSaturationGradient();
            ApplyPickerColor(color, false, false);
        }

        private void AccentHexInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyHexAccent_Click(sender, e);
                e.Handled = true;
            }
        }

        private void ApplyHexAccent_Click(object sender, RoutedEventArgs e)
        {
            Color color;
            if (AccentHexInput == null || !TryParseHexText(AccentHexInput.Text, out color))
            {
                ShowToast("色号格式不正确", "请输入 #RRGGBB，例如 #8B5CF6。");
                return;
            }

            SyncPickerFromColor(color);
            AppServices.SetAccentColor(color, true);
        }

        private void ResetAccentPicker_Click(object sender, RoutedEventArgs e)
        {
            Color color = ThemeManager.DefaultAccent;
            SyncPickerFromColor(color);
            AppServices.SetAccentColor(color, true);
        }

        private static bool TryParseHexText(string text, out Color color)
        {
            color = Colors.Transparent;
            string value = (text ?? "").Trim();
            if (value.StartsWith("#", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.Length != 6) return false;
            int r, g, b;
            if (!int.TryParse(value.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out r)) return false;
            if (!int.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out g)) return false;
            if (!int.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out b)) return false;
            color = Color.FromRgb((byte)r, (byte)g, (byte)b);
            return true;
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static Color HsvToColor(double hue, double saturation, double value)
        {
            double h = ((hue % 360.0) + 360.0) % 360.0;
            double s = Clamp01(saturation);
            double v = Clamp01(value);
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private static void ColorToHsv(Color color, out double hue, out double saturation, out double value)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            value = max;
            saturation = max <= 0 ? 0 : delta / max;

            if (delta <= 0)
            {
                hue = 0;
                return;
            }

            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
            if (hue < 0) hue += 360;
        }

        private void UpdateSettingsInfo()
        {
            try
            {
                var config = AppServices.Config;
                var discovery = AppServices.Discovery;
                var transfers = AppServices.Transfers;

                if (InfoDeviceIdText != null)
                {
                    InfoDeviceIdText.Text = config != null && !string.IsNullOrEmpty(config.DeviceId)
                        ? config.DeviceId
                        : "未知";
                }
                if (InfoAddressText != null)
                {
                    var self = discovery != null ? discovery.Self : null;
                    if (self != null && !string.IsNullOrEmpty(self.AddressText))
                    {
                        InfoAddressText.Text = self.AddressText + "（" + self.ModeText + "）";
                    }
                    else
                    {
                        InfoAddressText.Text = "未获取到本机网络地址";
                    }
                }
                if (InfoPortText != null)
                {
                    int port = transfers != null ? transfers.ActualListenPort : 0;
                    InfoPortText.Text = port > 0 ? port.ToString() : "尚未启动";
                }
                if (InfoVersionText != null)
                {
                    InfoVersionText.Text = "ConvenientFileTP " + AppVersion;
                }
            }
            catch (Exception ex)
            {
                Log.Error("更新本机信息失败", ex);
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择接收文件的保存目录";
                    dialog.ShowNewFolderButton = true;

                    string current = DownloadDirBox != null ? (DownloadDirBox.Text ?? "").Trim() : "";
                    if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;

                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
                        !string.IsNullOrEmpty(dialog.SelectedPath))
                    {
                        if (DownloadDirBox != null) DownloadDirBox.Text = dialog.SelectedPath;
                        ShowToast("已选择目录", "点击“保存设置”后生效");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("选择下载目录失败", ex);
                ShowToast("无法选择目录", ex.Message);
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var config = AppServices.Config;
            if (config == null)
            {
                ShowToast("无法保存", "应用配置尚未初始化");
                return;
            }

            string name = DeviceNameBox != null ? (DeviceNameBox.Text ?? "").Trim() : "";
            if (name.Length == 0)
            {
                ShowToast("无法保存", "设备名称不能为空");
                if (DeviceNameBox != null) DeviceNameBox.Focus();
                return;
            }

            string directory = DownloadDirBox != null ? (DownloadDirBox.Text ?? "").Trim() : "";
            if (directory.Length == 0)
            {
                ShowToast("无法保存", "请选择下载目录");
                return;
            }
            try
            {
                directory = Path.GetFullPath(directory);
            }
            catch (Exception)
            {
                ShowToast("无法保存", "下载目录路径无效");
                return;
            }

            bool nameChanged = !string.Equals(name, config.DeviceName ?? "", StringComparison.Ordinal);
            bool dirChanged = !string.Equals(directory, config.DownloadDirectory ?? "", StringComparison.OrdinalIgnoreCase);

            try
            {
                // 设备名称：调用 AppServices，同步更新 Identity、发现公告与配置文件。
                if (nameChanged) AppServices.RenameDevice(name);

                // 下载目录：调用 AppServices，内部会保存配置并确保目录存在。
                if (dirChanged) AppServices.SetDownloadDirectory(directory);

                config.AutoAcceptFiles = AutoAcceptToggle != null && AutoAcceptToggle.IsChecked == true;
                config.ShowNotifications = NotifyToggle != null && NotifyToggle.IsChecked == true;
                config.CloseToTray = CloseToTrayToggle != null && CloseToTrayToggle.IsChecked == true;
                AppServices.SaveConfig();
            }
            catch (Exception ex)
            {
                Log.Error("保存设置失败", ex);
                ShowToast("保存失败", ex.Message);
                return;
            }

            LoadSettingsIntoUi();
            UpdateHeader();

            Log.Info("界面保存设置: name=" + name + ", dir=" + directory +
                     ", autoAccept=" + config.AutoAcceptFiles +
                     ", notify=" + config.ShowNotifications +
                     ", closeToTray=" + config.CloseToTray);

            if (nameChanged && dirChanged) ShowToast("设置已保存", "设备名称与下载目录已立即生效");
            else if (nameChanged) ShowToast("设置已保存", "设备名称已更新，其他设备将看到新名称");
            else if (dirChanged) ShowToast("设置已保存", "新的下载目录已生效");
            else ShowToast("设置已保存", "配置已保存到本机");
        }
    }
}
