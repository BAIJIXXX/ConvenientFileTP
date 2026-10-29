using System;
using System.IO;

namespace ConvenientFileTP.Core
{
    /// <summary>All runtime paths, derived from the project root passed by the launcher.</summary>
    public static class AppPaths
    {
        public static string ProjectRoot { get; private set; }
        public static string DataDirectory { get; private set; }
        public static string LogDirectory { get; private set; }
        public static string LogFile { get; private set; }
        public static string ConfigFile { get; private set; }
        public static string DefaultDownloadDirectory { get; private set; }
        public static string DownloadDirectory { get; set; }
        public static string LauncherPath { get; private set; }

        public static void Initialize(StartupOptions options)
        {
            ProjectRoot = Path.GetFullPath(options.ProjectRoot);
            DefaultDownloadDirectory = Path.Combine(ProjectRoot, "downloads");

            string profile = options.Profile ?? "";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                profile = profile.Replace(c, '_');
            }
            string baseData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ConvenientFileTP");
            DataDirectory = string.IsNullOrEmpty(profile) ? baseData : Path.Combine(baseData, "profiles", profile);
            LogDirectory = Path.Combine(DataDirectory, "logs");
            LogFile = Path.Combine(LogDirectory, "app.log");
            ConfigFile = Path.Combine(DataDirectory, "config.json");
            LauncherPath = Path.Combine(ProjectRoot, "launcher.vbs");

            try { Directory.CreateDirectory(DataDirectory); } catch { }
            try { Directory.CreateDirectory(LogDirectory); } catch { }
            try { Directory.CreateDirectory(DefaultDownloadDirectory); } catch { }
        }

        public static void EnsureDownloadDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path);
            }
            catch { }
        }
    }
}
