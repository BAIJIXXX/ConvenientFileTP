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

        private void CustomAccent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dialog = new System.Windows.Forms.ColorDialog())
                {
                    dialog.FullOpen = true;
                    dialog.AnyColor = true;
                    var current = ThemeManager.AccentColor;
                    dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);

                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var picked = dialog.Color;
                        AppServices.SetAccentColor(Color.FromRgb(picked.R, picked.G, picked.B));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("选择自定义主题色失败", ex);
                ShowToast("无法选择颜色", ex.Message);
            }
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
