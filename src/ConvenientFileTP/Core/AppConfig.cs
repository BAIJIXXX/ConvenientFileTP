using System;
using System.IO;

namespace ConvenientFileTP.Core
{
    /// <summary>Persisted, per-profile application settings.</summary>
    public sealed class AppConfig
    {
        public int Version { get; set; } = 1;
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DownloadDirectory { get; set; } = "";
        public int DiscoveryPort { get; set; } = 47731;
        public int TcpPort { get; set; } = 47732;
        public bool AutoAcceptFiles { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool AutoStart { get; set; }
        public bool CloseToTray { get; set; } = true;
        public double WidgetScale { get; set; } = 1.0;
        public string WidgetEdge { get; set; } = "right";
        public double WidgetOffsetRatio { get; set; } = 0.5;

        [NonSerialized]
        private string _path;

        public static AppConfig Load(string path, StartupOptions options)
        {
            AppConfig config = null;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    config = Json.Deserialize<AppConfig>(json);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，将使用默认配置", ex);
                try
                {
                    if (File.Exists(path)) File.Copy(path, path + ".broken." + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch { }
            }

            if (config == null) config = new AppConfig();
            config._path = path;

            if (string.IsNullOrWhiteSpace(config.DeviceId))
            {
                config.DeviceId = Guid.NewGuid().ToString("N");
            }
            if (!string.IsNullOrWhiteSpace(options.DeviceNameOverride))
            {
                config.DeviceName = options.DeviceNameOverride.Trim();
            }
            else if (string.IsNullOrWhiteSpace(config.DeviceName))
            {
                config.DeviceName = Environment.MachineName;
            }

            if (!string.IsNullOrWhiteSpace(options.DownloadDirectoryOverride))
            {
                config.DownloadDirectory = Path.GetFullPath(options.DownloadDirectoryOverride);
            }
            else if (string.IsNullOrWhiteSpace(config.DownloadDirectory))
            {
                config.DownloadDirectory = AppPaths.DefaultDownloadDirectory;
            }

            if (config.DiscoveryPort <= 0 || config.DiscoveryPort > 65535) config.DiscoveryPort = 47731;
            if (config.TcpPort <= 0 || config.TcpPort > 65535) config.TcpPort = 47732;
            if (config.WidgetScale < 0.6 || config.WidgetScale > 2.0) config.WidgetScale = 1.0;
            if (string.IsNullOrEmpty(config.WidgetEdge)) config.WidgetEdge = "right";
            if (config.WidgetOffsetRatio < 0 || config.WidgetOffsetRatio > 1) config.WidgetOffsetRatio = 0.5;

            config.Save();
            return config;
        }

        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) _path = AppPaths.ConfigFile;
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(this));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
            }
        }
    }
}
