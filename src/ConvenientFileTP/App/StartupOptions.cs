using System;
using System.Collections.Generic;
using System.IO;

namespace ConvenientFileTP
{
    /// <summary>Command line / launcher options.</summary>
    public sealed class StartupOptions
    {
        public static StartupOptions Current { get; internal set; } = new StartupOptions();

        public string Profile { get; private set; } = "";
        public string ProjectRoot { get; private set; } = "";
        public string DeviceNameOverride { get; private set; }
        public string DownloadDirectoryOverride { get; private set; }
        public string AutoSelectPeer { get; private set; }
        public string AutoSendFile { get; private set; }
        public int AutoExitSeconds { get; private set; }
        public bool StartMinimized { get; private set; }
        public bool NoTray { get; private set; }
        public bool RichDebug { get; private set; }

        public bool IsTesting
        {
            get { return !string.IsNullOrEmpty(AutoSelectPeer) || !string.IsNullOrEmpty(AutoSendFile) || AutoExitSeconds > 0; }
        }

        public static StartupOptions Parse(string[] args)
        {
            var o = new StartupOptions();
            if (args == null) return o;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                switch (a.ToLowerInvariant())
                {
                    case "--profile":
                        o.Profile = Next(args, ref i) ?? "";
                        break;
                    case "--root":
                        o.ProjectRoot = Next(args, ref i) ?? "";
                        break;
                    case "--name":
                        o.DeviceNameOverride = Next(args, ref i);
                        break;
                    case "--download-dir":
                        o.DownloadDirectoryOverride = Next(args, ref i);
                        break;
                    case "--auto-select":
                        o.AutoSelectPeer = Next(args, ref i);
                        break;
                    case "--auto-send":
                        o.AutoSendFile = Next(args, ref i);
                        break;
                    case "--auto-exit":
                        int secs;
                        if (int.TryParse(Next(args, ref i), out secs)) o.AutoExitSeconds = secs;
                        break;
                    case "--minimized":
                        o.StartMinimized = true;
                        break;
                    case "--no-tray":
                        o.NoTray = true;
                        break;
                    case "--debug":
                        o.RichDebug = true;
                        break;
                }
            }

            if (string.IsNullOrEmpty(o.ProjectRoot))
            {
                // Fall back to the directory containing the app payload (…\app\..).
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                o.ProjectRoot = Path.GetFullPath(Path.Combine(baseDir, ".."));
            }
            else
            {
                o.ProjectRoot = Path.GetFullPath(o.ProjectRoot);
            }
            return o;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return null;
            i++;
            return args[i];
        }
    }
}
