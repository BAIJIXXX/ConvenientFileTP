using System;
using System.Windows;
using ConvenientFileTP.Core;

namespace ConvenientFileTP
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (s, args) =>
            {
                Log.Error("DispatcherUnhandledException", args.Exception);
                args.Handled = true;
                MessageBox.Show(
                    "程序遇到异常：\r\n" + args.Exception.Message,
                    "ConvenientFileTP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log.Error("UnhandledException", args.ExceptionObject as Exception);
            };

            try
            {
                AppServices.Initialize();
            }
            catch (Exception ex)
            {
                Log.Error("AppServices.Initialize failed", ex);
                MessageBox.Show(
                    "启动失败：\r\n" + ex.Message,
                    "ConvenientFileTP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppServices.Shutdown();
            base.OnExit(e);
        }
    }
}
