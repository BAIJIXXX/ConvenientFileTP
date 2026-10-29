using System;
using System.Windows;
using ConvenientFileTP.Core;
using ConvenientFileTP.Native;

namespace ConvenientFileTP
{
    /// <summary>
    /// Entry point invoked from the PowerShell launcher. The application is compiled as a
    /// library and loaded from memory, so WPF never maps an unsigned executable into the
    /// process (required on machines with Smart App Control enabled).
    /// </summary>
    public static class AppEntry
    {
        [STAThread]
        public static int Run(string[] args)
        {
            try
            {
                Win32.EnablePerMonitorDpi();
                Win32.SetAppUserModelId("ConvenientFileTP.Desktop.1");
                StartupOptions.Current = StartupOptions.Parse(args ?? new string[0]);

                if (!SingleInstance.TryAcquire(StartupOptions.Current.Profile))
                {
                    MessageBox.Show(
                        "ConvenientFileTP 已经在运行了。\r\n请在任务栏通知区域查看它。",
                        "ConvenientFileTP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return 0;
                }

                var app = new App();
                Application.ResourceAssembly = typeof(AppEntry).Assembly;
                app.InitializeComponent();
                app.Run();
                return 0;
            }
            catch (Exception ex)
            {
                try { Log.Error("Fatal startup error", ex); } catch { }
                MessageBox.Show(
                    "ConvenientFileTP 启动失败：\r\n\r\n" + ex,
                    "ConvenientFileTP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
