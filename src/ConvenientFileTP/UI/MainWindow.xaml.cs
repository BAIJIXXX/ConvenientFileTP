using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI
{
    /// <summary>
    /// 主窗口：左侧导航 + 设备 / 传输记录 / 设置三个页面。
    /// 只负责界面；配置、设备发现与文件传输都由 <see cref="AppServices"/> 提供。
    /// 设备发现与传输事件都可能在后台线程触发，界面更新统一调度到 Dispatcher。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxTransferRows = 100;

        private readonly ObservableCollection<PeerRow> _peerRows = new ObservableCollection<PeerRow>();
        private readonly ObservableCollection<TransferRow> _transferRows = new ObservableCollection<TransferRow>();
        private readonly Dictionary<string, TransferRow> _transferIndex =
            new Dictionary<string, TransferRow>(StringComparer.OrdinalIgnoreCase);

        private DispatcherTimer _statusTimer;
        private DispatcherTimer _toastTimer;
        private bool _eventsWired;
        private bool _suppressPeerSelection;
        private string _currentPage = "devices";

        public MainWindow()
        {
            InitializeComponent();

            PeerList.ItemsSource = _peerRows;
            TransferList.ItemsSource = _transferRows;

            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;
        }

        // ==================== 生命周期 ====================

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                WireEvents();
                ShowPage("devices");
                LoadSettingsIntoUi();
                RefreshPeers();
                UpdateHeader();
                UpdateTransferEmptyState();
                UpdateTransferCount();
                StartStatusTimer();
                Log.Info("主窗口界面已就绪");
            }
            catch (Exception ex)
            {
                Log.Error("主窗口加载失败", ex);
            }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            try
            {
                if (_statusTimer != null)
                {
                    _statusTimer.Stop();
                    _statusTimer = null;
                }
                if (_toastTimer != null)
                {
                    _toastTimer.Stop();
                    _toastTimer = null;
                }
                if (!_eventsWired) return;
                _eventsWired = false;

                var discovery = AppServices.Discovery;
                if (discovery != null) discovery.PeersChanged -= OnPeersChanged;

                var transfers = AppServices.Transfers;
                if (transfers != null)
                {
                    transfers.Progress -= OnTransferProgress;
                    transfers.Completed -= OnTransferCompleted;
                }
            }
            catch (Exception ex)
            {
                Log.Error("清理主窗口事件失败", ex);
            }
        }

        private void WireEvents()
        {
            if (_eventsWired) return;
            _eventsWired = true;

            var discovery = AppServices.Discovery;
            if (discovery != null) discovery.PeersChanged += OnPeersChanged;

            var transfers = AppServices.Transfers;
            if (transfers != null)
            {
                transfers.Progress += OnTransferProgress;
                transfers.Completed += OnTransferCompleted;
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try
            {
                UpdateHeader();
                RefreshPeerStatuses();
            }
            catch (Exception ex)
            {
                Log.Error("激活主窗口时刷新失败", ex);
            }
        }

        // ==================== 后台事件 -> UI 线程 ====================

        private void OnPeersChanged(object sender, EventArgs e)
        {
            PostToUi(RefreshPeers);
        }

        private void OnTransferProgress(object sender, TransferProgress progress)
        {
            if (progress == null) return;
            PostToUi(() => ApplyTransferProgress(progress));
        }

        private void OnTransferCompleted(object sender, TransferCompletedEventArgs args)
        {
            if (args == null) return;
            PostToUi(() => ApplyTransferCompleted(args));
        }

        private void PostToUi(Action action)
        {
            if (action == null) return;
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    SafeInvoke(action);
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() => SafeInvoke(action)));
            }
            catch (Exception ex)
            {
                Log.Error("调度到 UI 线程失败", ex);
            }
        }

        private void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("主窗口界面更新失败", ex);
            }
        }

        // ==================== 页面切换 ====================

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string page = button != null ? (button.Tag as string ?? "") : "";
            if (string.IsNullOrEmpty(page)) return;
            ShowPage(page);
        }

        private void ShowPage(string page)
        {
            if (string.IsNullOrEmpty(page)) return;
            _currentPage = page;

            if (DevicesPage != null)
                DevicesPage.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
            if (TransfersPage != null)
                TransfersPage.Visibility = page == "transfers" ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsPage != null)
                SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

            ApplyNavStyle(NavDevicesButton, page == "devices");
            ApplyNavStyle(NavTransfersButton, page == "transfers");
            ApplyNavStyle(NavSettingsButton, page == "settings");

            if (page == "transfers")
            {
                UpdateTransferEmptyState();
                UpdateTransferCount();
            }
            else if (page == "settings")
            {
                UpdateSettingsInfo();
            }
        }

        private void ApplyNavStyle(Button button, bool active)
        {
            if (button == null) return;
            var style = FindResource(active ? "NavButtonActive" : "NavButton") as Style;
            if (style != null) button.Style = style;
        }

        // ==================== 顶部状态 ====================

        private void UpdateHeader()
        {
            try
            {
                var config = AppServices.Config;
                var discovery = AppServices.Discovery;
                var self = discovery != null ? discovery.Self : null;

                string name;
                if (self != null && !string.IsNullOrEmpty(self.DisplayName)) name = self.DisplayName;
                else if (config != null && !string.IsNullOrWhiteSpace(config.DeviceName)) name = config.DeviceName;
                else name = Environment.MachineName;
                if (string.IsNullOrWhiteSpace(name)) name = "本机";
                name = name.Trim();

                if (HeaderSelfName != null) HeaderSelfName.Text = name;
                if (NavSelfName != null) NavSelfName.Text = name;
                if (SelfNameText != null) SelfNameText.Text = name;

                string initial = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "本";
                if (SelfAvatarText != null) SelfAvatarText.Text = initial;
                if (NavAvatarText != null) NavAvatarText.Text = initial;

                string mode = self != null ? self.ModeText : "未知网络";
                if (HeaderModeText != null) HeaderModeText.Text = mode;
                if (SelfModeText != null) SelfModeText.Text = mode;
                if (NavSelfMode != null) NavSelfMode.Text = "本机 · " + PrimaryMode(mode);

                if (SelfAddressText != null)
                {
                    SelfAddressText.Text = self != null && !string.IsNullOrEmpty(self.AddressText)
                        ? self.AddressText
                        : "未获取到本机网络地址";
                }
                if (SelfIdText != null)
                {
                    SelfIdText.Text = config != null && !string.IsNullOrEmpty(config.DeviceId)
                        ? "设备 ID " + ShortId(config.DeviceId)
                        : "";
                }

                int online = 0;
                int total = 0;
                if (discovery != null)
                {
                    foreach (var peer in discovery.Peers)
                    {
                        if (peer == null || peer.IsSelf) continue;
                        total++;
                        if (peer.IsOnline) online++;
                    }
                }
                if (HeaderOnlineText != null) HeaderOnlineText.Text = online + " 台设备在线";
                if (OtherCountText != null)
                {
                    OtherCountText.Text = total == 0 ? "暂无设备" : total + " 台设备 · " + online + " 台在线";
                }
                if (VersionText != null) VersionText.Text = "v" + AppVersion;
            }
            catch (Exception ex)
            {
                Log.Error("更新状态栏失败", ex);
            }
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.Length <= 10 ? id : id.Substring(0, 10);
        }

        /// <summary>网络模式可能包含多个接口（例如“局域网 + 本机热点”），侧栏只显示主要模式。</summary>
        private static string PrimaryMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "未知网络";
            int index = mode.IndexOf(" + ", StringComparison.Ordinal);
            return index > 0 ? mode.Substring(0, index) : mode;
        }

        private static string AppVersion
        {
            get { return AppInfo.Version; }
        }

        private void StartStatusTimer()
        {
            if (_statusTimer != null) return;
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (sender, e) =>
            {
                try
                {
                    UpdateHeader();
                    RefreshPeerStatuses();
                }
                catch (Exception ex)
                {
                    Log.Error("定时刷新状态失败", ex);
                }
            };
            _statusTimer.Start();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppServices.Discovery != null) AppServices.Discovery.Refresh();
            }
            catch (Exception ex)
            {
                Log.Error("刷新设备发现失败", ex);
            }
            RefreshPeers();
        }

        // ==================== 窗口契约 ====================

        /// <summary>显示并置前主窗口。</summary>
        public void ShowAndActivate()
        {
            try
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
                // The widget intentionally never activates our process, so Activate() can
                // be refused while another app owns the foreground; force it explicitly.
                Native.Win32.BringToFront(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                UpdateHeader();
                RefreshPeers();
            }
            catch (Exception ex)
            {
                Log.Error("显示主窗口失败", ex);
            }
        }

        /// <summary>窗口内浮层提示，淡入后约 2.5 秒自动淡出。</summary>
        public void ShowToast(string title, string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                PostToUi(() => ShowToast(title, message));
                return;
            }
            try
            {
                Log.Info("提示: " + title + " - " + message);
                if (ToastHost == null) return;

                if (ToastTitle != null)
                {
                    ToastTitle.Text = title ?? "";
                    ToastTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
                }
                if (ToastMessage != null)
                {
                    ToastMessage.Text = message ?? "";
                    ToastMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
                }

                if (_toastTimer == null)
                {
                    _toastTimer = new DispatcherTimer();
                    _toastTimer.Tick += OnToastTimerTick;
                }
                _toastTimer.Stop();

                ToastHost.BeginAnimation(OpacityProperty, null);
                var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ToastHost.BeginAnimation(OpacityProperty, fadeIn);

                _toastTimer.Interval = TimeSpan.FromMilliseconds(2500);
                _toastTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Error("显示浮层提示失败", ex);
            }
        }

        private void OnToastTimerTick(object sender, EventArgs e)
        {
            if (_toastTimer != null) _toastTimer.Stop();
            try
            {
                if (ToastHost == null) return;
                var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                ToastHost.BeginAnimation(OpacityProperty, fadeOut);
            }
            catch (Exception ex)
            {
                Log.Error("隐藏浮层提示失败", ex);
            }
        }

        // ==================== 自绘标题栏 ====================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try { DragMove(); } catch { }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!AppServices.IsExiting && AppServices.Config != null && AppServices.Config.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                Log.Info("关闭主窗口：按设置最小化到托盘");
                return;
            }
            base.OnClosing(e);
        }
    }
}
