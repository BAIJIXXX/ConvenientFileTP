using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI
{
    /// <summary>主窗口：传输记录页面。</summary>
    public partial class MainWindow
    {
        private void ApplyTransferProgress(TransferProgress progress)
        {
            if (progress == null) return;
            try
            {
                string key = BuildTransferKey(progress.SessionId, progress.Direction);
                TransferRow row;
                if (!_transferIndex.TryGetValue(key, out row))
                {
                    row = new TransferRow(progress) { Key = key };
                    _transferIndex[key] = row;
                    _transferRows.Insert(0, row);
                    TrimTransferRows();
                }
                else
                {
                    row.Update(progress);
                }
                UpdateTransferEmptyState();
                UpdateTransferCount();
            }
            catch (Exception ex)
            {
                Log.Error("更新传输进度失败", ex);
            }
        }

        private void ApplyTransferCompleted(TransferCompletedEventArgs args)
        {
            if (args == null) return;
            try
            {
                string key = BuildTransferKey(args.SessionId, args.Direction);
                TransferRow row;
                if (!_transferIndex.TryGetValue(key, out row))
                {
                    row = new TransferRow(args) { Key = key };
                    _transferIndex[key] = row;
                    _transferRows.Insert(0, row);
                    TrimTransferRows();
                }
                else
                {
                    row.Complete(args);
                }
                UpdateTransferEmptyState();
                UpdateTransferCount();

                if (args.Direction == TransferDirection.Receive && args.Success && IsVisible)
                {
                    string fileName = "文件";
                    if (args.SavedFiles != null && args.SavedFiles.Length > 0)
                    {
                        try { fileName = Path.GetFileName(args.SavedFiles[0]); }
                        catch { }
                        if (string.IsNullOrEmpty(fileName)) fileName = "文件";
                    }
                    string message = args.SavedFiles != null && args.SavedFiles.Length > 1
                        ? "已接收 " + args.SavedFiles.Length + " 个文件，保存在下载目录"
                        : fileName + " 已保存在下载目录";
                    ShowToast("接收完成", message);
                }
            }
            catch (Exception ex)
            {
                Log.Error("更新传输完成状态失败", ex);
            }
        }

        private static string BuildTransferKey(string sessionId, TransferDirection direction)
        {
            string session = string.IsNullOrEmpty(sessionId) ? "unknown" : sessionId;
            return session + "|" + (int)direction;
        }

        private void TrimTransferRows()
        {
            while (_transferRows.Count > MaxTransferRows)
            {
                int lastIndex = _transferRows.Count - 1;
                TransferRow last = _transferRows[lastIndex];
                _transferRows.RemoveAt(lastIndex);
                if (last != null && !string.IsNullOrEmpty(last.Key))
                {
                    _transferIndex.Remove(last.Key);
                }
            }
        }

        private void UpdateTransferEmptyState()
        {
            if (TransferEmptyPanel == null || TransferList == null) return;
            bool empty = _transferRows.Count == 0;
            TransferEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            TransferList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateTransferCount()
        {
            if (TransferCountText == null) return;
            TransferCountText.Text = _transferRows.Count == 0
                ? "暂无记录 · 最多保留 " + MaxTransferRows + " 条"
                : _transferRows.Count + " 条记录 · 最多保留 " + MaxTransferRows + " 条";
        }

        private void ClearTransfers_Click(object sender, RoutedEventArgs e)
        {
            _transferRows.Clear();
            _transferIndex.Clear();
            UpdateTransferEmptyState();
            UpdateTransferCount();
            ShowToast("已清空", "传输记录已清空");
        }

        private void OpenDownloads_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppServices.OpenDownloadDirectory();
            }
            catch (Exception ex)
            {
                Log.Error("打开下载目录失败", ex);
                ShowToast("无法打开下载目录", ex.Message);
            }
        }

        private void TransferList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = TransferList != null ? TransferList.SelectedItem as TransferRow : null;
            if (row == null || !row.CanOpen) return;

            try
            {
                string[] files = row.SavedFiles;
                if (files != null && files.Length == 1 && !string.IsNullOrEmpty(files[0]) && File.Exists(files[0]))
                {
                    Process.Start(new ProcessStartInfo(files[0]) { UseShellExecute = true });
                }
                else
                {
                    AppServices.OpenDownloadDirectory();
                }
            }
            catch (Exception ex)
            {
                Log.Error("打开已接收文件失败", ex);
                ShowToast("无法打开文件", ex.Message);
            }
        }
    }
}
