using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConvenientFileTP.UI;

namespace ConvenientFileTP.Core
{
    /// <summary>Composition root: owns configuration, discovery, transfers and the windows.</summary>
    public static class AppServices
    {
        private static bool _autoSendScheduled;
        private static bool _autoSendStarted;

        public static AppConfig Config { get; private set; }
        public static DeviceIdentity Identity { get; private set; }
        public static DiscoveryService Discovery { get; private set; }
        public static TransferManager Transfers { get; private set; }
        public static MainWindow Main { get; internal set; }
        public static FloatingWidget Floating { get; internal set; }
        public static TrayIcon Tray { get; internal set; }
        public static bool IsExiting { get; private set; }

        public static string DownloadDirectory
        {
            get { return Config != null ? Config.DownloadDirectory : AppPaths.DownloadDirectory; }
        }

        public static void Initialize()
        {
            AppPaths.Initialize(StartupOptions.Current);
            Log.Initialize(AppPaths.LogFile);

            Config = AppConfig.Load(AppPaths.ConfigFile, StartupOptions.Current);
            AppPaths.DownloadDirectory = Config.DownloadDirectory;
            AppPaths.EnsureDownloadDirectory(Config.DownloadDirectory);
            ThemeManager.Initialize(Config);
            ThemeManager.ThemeChanged += OnThemeChanged;

            Identity = new DeviceIdentity(Config.DeviceId, Config.DeviceName);
            Log.Info("启动: " + Identity.Name + " id=" + Identity.Id + " 配置=" + AppPaths.ConfigFile);

            Transfers = new TransferManager(Identity, Config);
            Transfers.Progress += OnTransferProgress;
            Transfers.Completed += OnTransferCompleted;
            Transfers.Start();
            Log.Info("文件传输服务监听端口 " + Transfers.ActualListenPort);

            Discovery = new DiscoveryService(Identity, Config.DiscoveryPort);
            Discovery.PeersChanged += OnPeersChanged;
            Discovery.Start(Transfers.ActualListenPort);

            Main = new MainWindow();
            ApplyWindowIcon(Main);
            Application.Current.MainWindow = Main;

            if (!StartupOptions.Current.NoTray)
            {
                try
                {
                    Tray = new TrayIcon();
                }
                catch (Exception ex)
                {
                    Log.Error("创建托盘图标失败", ex);
                }
            }

            bool showMain = !StartupOptions.Current.StartMinimized || Tray == null;
            if (showMain)
            {
                Main.ShowAndActivate();
            }

            if (StartupOptions.Current.AutoExitSeconds > 0)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StartupOptions.Current.AutoExitSeconds) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    Log.Info("测试模式：自动退出");
                    Exit();
                };
                timer.Start();
            }

            TryAutoSelectPeer();
            Log.Info("初始化完成");
        }

        private static void ApplyWindowIcon(Window window)
        {
            if (window == null) return;
            try
            {
                string ico = Path.Combine(AppPaths.ProjectRoot ?? "", "assets", "app.ico");
                if (File.Exists(ico))
                {
                    window.Icon = BitmapFrame.Create(new Uri(ico, UriKind.Absolute));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("设置窗口图标失败：" + ex.Message);
            }
        }

        public static void Shutdown()
        {
            IsExiting = true;
            try { if (Tray != null) Tray.Dispose(); } catch { }
            try { if (Discovery != null) Discovery.Dispose(); } catch { }
            try { if (Transfers != null) Transfers.Dispose(); } catch { }
            Tray = null;
            SingleInstance.Release();
            Log.Info("已退出");
        }

        public static void ShowMainWindow()
        {
            if (Main == null) return;
            Main.ShowAndActivate();
        }

        public static void SelectPeer(PeerInfo peer)
        {
            if (peer == null || peer.IsSelf) return;
            Log.Info("选择设备: " + peer.Name + " " + peer.AddressText);
            if (Floating == null)
            {
                CreateFloatingWidget(peer);
            }
            else
            {
                Floating.BindPeer(peer);
                Floating.ShowWidget();
            }
            if (Main != null && Main.IsVisible)
            {
                Main.Hide();
            }
        }

        public static void CreateFloatingWidget(PeerInfo peer)
        {
            var widget = new FloatingWidget();
            widget.Closed += (s, e) =>
            {
                if (ReferenceEquals(Floating, widget)) Floating = null;
            };
            widget.BindPeer(peer ?? new PeerInfo
            {
                Id = "local",
                Name = "本机",
                Address = "127.0.0.1",
                IsSelf = true,
                Mode = NetworkMode.Loopback
            });
            Floating = widget;
            widget.ShowWidget();
        }

        public static void ToggleFloatingWindow()
        {
            if (Floating != null)
            {
                if (Floating.IsVisible) Floating.HideWidget();
                else Floating.ShowWidget();
            }
            else
            {
                ShowMainWindow();
            }
        }

        public static void OpenDownloadDirectory()
        {
            try
            {
                string dir = DownloadDirectory;
                AppPaths.EnsureDownloadDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开下载目录失败", ex);
            }
        }

        public static void SaveConfig()
        {
            if (Config == null) return;
            Config.Save();
            AppPaths.DownloadDirectory = Config.DownloadDirectory;
            AppPaths.EnsureDownloadDirectory(Config.DownloadDirectory);
            if (Discovery != null) Discovery.Refresh();
        }

        /// <summary>Applies a new device name from the settings page at runtime.</summary>
        public static void RenameDevice(string name)
        {
            if (Config == null) return;
            name = (name ?? "").Trim();
            if (name.Length == 0) name = Environment.MachineName;
            Config.DeviceName = name;
            if (Identity != null) Identity.Name = name;
            if (Discovery != null) Discovery.UpdateIdentity();
            SaveConfig();
            if (Main != null) Main.RefreshPeers();
            Log.Info("设备名称已更新为 " + name);
        }

        /// <summary>Applies a new download directory from the settings page at runtime.</summary>
        public static void SetDownloadDirectory(string directory)
        {
            if (Config == null || string.IsNullOrWhiteSpace(directory)) return;
            Config.DownloadDirectory = directory.Trim();
            SaveConfig();
        }

        /// <summary>Applies a new day/night palette mode and persists it.</summary>
        public static void SetThemeMode(ThemeMode mode)
        {
            if (Config == null) return;
            Config.ThemeMode = ThemeManager.ModeKey(mode);
            ThemeManager.SetMode(mode);
            SaveConfig();
            Log.Info("主题模式已切换为 " + ThemeManager.ModeText);
        }

        /// <summary>Applies a new accent colour and persists it.</summary>
        public static void SetAccentColor(System.Windows.Media.Color color)
        {
            SetAccentColor(color, true);
        }

        /// <summary>Applies a new accent colour; persist=false avoids disk writes while dragging a colour picker.</summary>
        public static void SetAccentColor(System.Windows.Media.Color color, bool persist)
        {
            if (Config == null) return;
            Config.AccentColor = ThemeManager.ToHex(color);
            ThemeManager.SetAccent(color);
            if (!persist) return;
            SaveConfig();
            Log.Info("主题色已切换为 " + Config.AccentColor);
        }

        public static void ResetTheme()
        {
            if (Config == null) return;
            Config.ThemeMode = "dark";
            Config.AccentColor = ThemeManager.ToHex(ThemeManager.DefaultAccent);
            ThemeManager.SetMode(ThemeMode.Dark);
            ThemeManager.SetAccent(ThemeManager.DefaultAccent);
            SaveConfig();
        }

        private static void OnThemeChanged(object sender, EventArgs e)
        {
            try
            {
                if (Tray != null) Tray.RefreshTheme();
                if (Floating != null) Floating.RefreshTheme();
                if (Main != null) Main.RefreshAppearanceUi();
            }
            catch (Exception ex)
            {
                Log.Error("刷新主题界面失败", ex);
            }
        }

        public static void Exit()
        {
            if (IsExiting) return;
            IsExiting = true;
            Log.Info("用户退出程序");
            try { if (Tray != null) Tray.Dispose(); } catch { }
            try { if (Floating != null) Floating.Close(); } catch { }
            try { if (Main != null) Main.Close(); } catch { }
            try { if (Discovery != null) Discovery.Dispose(); } catch { }
            try { if (Transfers != null) Transfers.Dispose(); } catch { }
            Application.Current.Shutdown();
        }

        private static void OnPeersChanged(object sender, EventArgs e)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (Main != null) Main.RefreshPeers();
                    if (Floating != null && Floating.Target != null && Discovery != null)
                    {
                        var current = Discovery.FindPeer(Floating.Target.Id);
                        if (current != null) Floating.UpdatePeer(current);
                    }
                    TryAutoSelectPeer();
                }
                catch (Exception ex)
                {
                    Log.Error("刷新设备列表失败", ex);
                }
            }));
        }

        private static void OnTransferProgress(object sender, TransferProgress e)
        {
            if (e == null) return;
            var app = Application.Current;
            if (app == null || app.Dispatcher == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (e.Direction == TransferDirection.Receive && Floating == null)
                    {
                        CreateFloatingWidget(e.Peer);
                    }
                    if (Floating != null) Floating.OnTransferProgress(e);
                }
                catch (Exception ex)
                {
                    Log.Error("处理传输进度失败", ex);
                }
            }));
        }

        private static void OnTransferCompleted(object sender, TransferCompletedEventArgs e)
        {
            if (e == null) return;
            var app = Application.Current;
            if (app == null || app.Dispatcher == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (e.Direction == TransferDirection.Receive && Floating == null)
                    {
                        CreateFloatingWidget(e.Peer);
                    }
                    if (Floating != null) Floating.OnTransferCompleted(e);

                    bool mainHidden = Main == null || !Main.IsVisible;
                    if (e.Direction == TransferDirection.Receive && e.Success && mainHidden &&
                        Tray != null && Config != null && Config.ShowNotifications)
                    {
                        string name = e.SavedFiles != null && e.SavedFiles.Length > 0
                            ? Path.GetFileName(e.SavedFiles[0])
                            : "文件";
                        string text = e.SavedFiles != null && e.SavedFiles.Length > 1
                            ? name + " 等 " + e.SavedFiles.Length + " 个文件"
                            : name;
                        Tray.ShowBalloon("收到文件", text);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("处理传输完成事件失败", ex);
                }
            }));
        }

        private static void TryAutoSelectPeer()
        {
            if (IsExiting) return;
            string pattern = StartupOptions.Current.AutoSelectPeer;
            if (string.IsNullOrEmpty(pattern) || Discovery == null) return;
            if (Floating != null && Floating.Target != null && !Floating.Target.IsSelf) return;
            var peer = Discovery.Peers.FirstOrDefault(p =>
                p != null && !p.IsSelf && p.Name != null &&
                p.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
            if (peer == null) return;
            SelectPeer(peer);
            ScheduleAutoSend();
        }

        private static void ScheduleAutoSend()
        {
            if (_autoSendScheduled || _autoSendStarted) return;
            if (string.IsNullOrEmpty(StartupOptions.Current.AutoSendFile)) return;
            _autoSendScheduled = true;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (_autoSendStarted) return;
                _autoSendStarted = true;
                RunAutoSend();
            };
            timer.Start();
        }

        private static void RunAutoSend()
        {
            var paths = (StartupOptions.Current.AutoSendFile ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(File.Exists)
                .ToList();
            var target = Floating != null ? Floating.Target : null;
            if (target == null || target.IsSelf || paths.Count == 0)
            {
                Log.Warn("测试模式自动发送跳过：目标或文件不存在");
                return;
            }
            Log.Info("测试模式自动发送 " + paths.Count + " 个文件到 " + target.Name);
            var peer = target.Clone();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var task = Transfers.SendFilesAsync(peer, paths, CancellationToken.None);
                    task.Wait(TimeSpan.FromMinutes(5));
                    Log.Info("测试模式发送任务结束: " + task.Status);
                }
                catch (Exception ex)
                {
                    Log.Error("测试模式自动发送失败", ex);
                }
            });
        }
    }
}
