using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI
{
    /// <summary>主窗口：设备页面（本机卡片 + 局域网内其他设备列表）。</summary>
    public partial class MainWindow
    {
        /// <summary>按 AppServices.Discovery.Peers 重建“其他设备”列表。可从任意线程调用。</summary>
        public void RefreshPeers()
        {
            if (!Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(RefreshPeers));
                }
                catch (Exception ex)
                {
                    Log.Error("调度设备列表刷新失败", ex);
                }
                return;
            }

            try
            {
                if (PeerList == null) return;

                var discovery = AppServices.Discovery;
                var peers = new List<PeerInfo>();
                if (discovery != null)
                {
                    foreach (var peer in discovery.Peers)
                    {
                        if (peer == null || peer.IsSelf) continue;
                        peers.Add(peer);
                    }
                }
                peers.Sort(ComparePeers);

                bool previous = _suppressPeerSelection;
                _suppressPeerSelection = true;
                try
                {
                    PeerList.SelectedItem = null;
                    _peerRows.Clear();
                    foreach (var peer in peers)
                    {
                        _peerRows.Add(new PeerRow(peer));
                    }
                }
                finally
                {
                    _suppressPeerSelection = previous;
                }

                UpdatePeerEmptyState();
                UpdateHeader();
            }
            catch (Exception ex)
            {
                Log.Error("刷新设备列表失败", ex);
            }
        }

        private static int ComparePeers(PeerInfo a, PeerInfo b)
        {
            int byOnline = b.IsOnline.CompareTo(a.IsOnline);
            if (byOnline != 0) return byOnline;
            return string.Compare(a.DisplayName ?? "", b.DisplayName ?? "", StringComparison.CurrentCulture);
        }

        private void UpdatePeerEmptyState()
        {
            if (PeerEmptyPanel == null || PeerList == null) return;
            bool empty = _peerRows.Count == 0;
            PeerEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            PeerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>在线状态与“最后在线”是相对时间，需要定时刷新。</summary>
        private void RefreshPeerStatuses()
        {
            for (int i = 0; i < _peerRows.Count; i++)
            {
                _peerRows[i].Refresh();
            }
        }

        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPeerSelection) return;
            var row = PeerList != null ? PeerList.SelectedItem as PeerRow : null;
            if (row == null) return;

            // 列表项选中即触发选择；随后立刻清除选中，避免“已选中”状态残留。
            _suppressPeerSelection = true;
            try { PeerList.SelectedItem = null; }
            finally { _suppressPeerSelection = false; }

            SelectPeerRow(row);
        }

        private void PeerSelect_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as PeerRow : null;
            if (row == null) return;
            SelectPeerRow(row);
        }

        private void SelectPeerRow(PeerRow row)
        {
            if (row == null || row.Peer == null || row.IsSelf) return;
            try
            {
                Log.Info("界面选择设备: " + row.DisplayName + " (" + row.AddressText + ")");
                AppServices.SelectPeer(row.Peer);
            }
            catch (Exception ex)
            {
                Log.Error("选择设备失败", ex);
                ShowToast("无法选择设备", ex.Message);
            }
        }
    }
}
