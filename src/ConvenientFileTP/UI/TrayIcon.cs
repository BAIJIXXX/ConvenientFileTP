using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using ConvenientFileTP.Core;
using ConvenientFileTP.Native;

namespace ConvenientFileTP.UI
{
    /// <summary>Tray icon and its dark context menu.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private bool _disposed;

        public TrayIcon()
        {
            var menu = new ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                BackColor = Color.FromArgb(27, 23, 41),
                ForeColor = Color.FromArgb(243, 240, 255),
                Font = new Font("Microsoft YaHei UI", 9f),
                ShowImageMargin = false,
                Padding = new Padding(4)
            };

            var showItem = CreateItem("显示主窗口");
            showItem.Click += (s, e) => AppServices.ShowMainWindow();

            var floatingItem = CreateItem("显示 / 隐藏悬浮窗");
            floatingItem.Click += (s, e) => AppServices.ToggleFloatingWindow();

            var folderItem = CreateItem("打开下载文件夹");
            folderItem.Click += (s, e) => AppServices.OpenDownloadDirectory();

            var exitItem = CreateItem("退出 ConvenientFileTP");
            exitItem.Click += (s, e) => AppServices.Exit();

            menu.Items.Add(showItem);
            menu.Items.Add(floatingItem);
            menu.Items.Add(folderItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _menu = menu;
            _icon = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "ConvenientFileTP 局域网传输",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (s, e) => AppServices.ShowMainWindow();
        }

        public void ShowBalloon(string title, string message)
        {
            if (_disposed || _icon == null) return;
            try
            {
                _icon.BalloonTipTitle = title ?? "ConvenientFileTP";
                _icon.BalloonTipText = message ?? "";
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(3500);
            }
            catch { }
        }

        private static ToolStripMenuItem CreateItem(string text)
        {
            return new ToolStripMenuItem(text)
            {
                ForeColor = Color.FromArgb(243, 240, 255),
                BackColor = Color.Transparent,
                Padding = new Padding(8, 5, 8, 5)
            };
        }

        private static Icon CreateTrayIcon()
        {
            // Prefer the generated multi-size .ico that ships next to the project.
            try
            {
                string icoPath = Path.Combine(AppPaths.ProjectRoot ?? "", "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    using (var fileIcon = new Icon(icoPath, new Size(32, 32)))
                    {
                        return (Icon)fileIcon.Clone();
                    }
                }
            }
            catch { }

            const int size = 32;
            using (var bitmap = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(255, 139, 92, 246)))
                    {
                        g.FillEllipse(brush, 1, 1, size - 2, size - 2);
                    }
                    using (var pen = new Pen(Color.White, 2.4f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        // Up arrow
                        g.DrawLine(pen, 11, 14, 11, 7);
                        g.DrawLine(pen, 11, 7, 8, 10);
                        g.DrawLine(pen, 11, 7, 14, 10);
                        // Down arrow (accent light)
                        using (var pen2 = new Pen(Color.FromArgb(255, 240, 235, 255), 2.4f))
                        {
                            pen2.StartCap = LineCap.Round;
                            pen2.EndCap = LineCap.Round;
                            g.DrawLine(pen2, 21, 18, 21, 25);
                            g.DrawLine(pen2, 21, 25, 18, 22);
                            g.DrawLine(pen2, 21, 25, 24, 22);
                        }
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    Win32.DestroyIcon(handle);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                }
            }
            catch { }
            try { if (_menu != null) _menu.Dispose(); } catch { }
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColorTable())
            {
                RoundedEdges = true;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled) e.TextColor = Color.FromArgb(120, 112, 149);
                else e.TextColor = Color.FromArgb(243, 240, 255);
                base.OnRenderItemText(e);
            }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            private static readonly Color Panel = Color.FromArgb(27, 23, 41);
            private static readonly Color Hover = Color.FromArgb(45, 39, 70);
            private static readonly Color BorderColor = Color.FromArgb(58, 50, 86);

            public override Color ToolStripDropDownBackground { get { return Panel; } }
            public override Color ImageMarginGradientBegin { get { return Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Panel; } }
            public override Color ImageMarginGradientEnd { get { return Panel; } }
            public override Color MenuBorder { get { return BorderColor; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return BorderColor; } }
            public override Color SeparatorLight { get { return BorderColor; } }
        }
    }
}
