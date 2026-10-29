using System;
using System.IO;
using System.Windows;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI
{
    /// <summary>主窗口：设置页面。</summary>
    public partial class MainWindow
    {
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

                UpdateSettingsInfo();
            }
            catch (Exception ex)
            {
                Log.Error("读取设置失败", ex);
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
