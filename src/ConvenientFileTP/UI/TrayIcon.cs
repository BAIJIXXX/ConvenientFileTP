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

        private ThemeColors _colors;

        public TrayIcon()
        {
            _colors = ThemeColors.FromResources();

            var menu = new ContextMenuStrip
            {
                Renderer = new ThemedMenuRenderer(_colors),
                BackColor = _colors.Panel,
                ForeColor = _colors.Text,
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

        private ToolStripMenuItem CreateItem(string text)
        {
            return new ToolStripMenuItem(text)
            {
                ForeColor = _colors.Text,
                BackColor = Color.Transparent,
                Padding = new Padding(8, 5, 8, 5)
            };
        }

        /// <summary>Rebuilds menu colours after the app theme changed.</summary>
        public void RefreshTheme()
        {
            if (_disposed) return;
            try
            {
                _colors = ThemeColors.FromResources();
                _menu.Renderer = new ThemedMenuRenderer(_colors);
                _menu.BackColor = _colors.Panel;
                _menu.ForeColor = _colors.Text;
                foreach (ToolStripItem item in _menu.Items)
                {
                    if (item is ToolStripMenuItem) item.ForeColor = _colors.Text;
                }
            }
            catch { }
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

        internal sealed class ThemeColors
        {
            public Color Panel;
            public Color Text;
            public Color Muted;
            public Color Hover;
            public Color Border;

            public static ThemeColors FromResources()
            {
                return new ThemeColors
                {
                    Panel = Read("BgPanel", 27, 23, 41),
                    Text = Read("TextPrimary", 243, 240, 255),
                    Muted = Read("TextMuted", 124, 114, 149),
                    Hover = Read("BgCardHover", 45, 39, 70),
                    Border = Read("Border", 58, 50, 86)
                };
            }

            private static Color Read(string key, int r, int g, int b)
            {
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app != null)
                    {
                        var brush = app.TryFindResource(key) as System.Windows.Media.SolidColorBrush;
                        if (brush != null) return Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                    }
                }
                catch { }
                return Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
            }
        }

        private sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly ThemeColors _colors;

            public ThemedMenuRenderer(ThemeColors colors) : base(new ThemedColorTable(colors))
            {
                _colors = colors;
                RoundedEdges = true;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? _colors.Text : _colors.Muted;
                base.OnRenderItemText(e);
            }
        }

        private sealed class ThemedColorTable : ProfessionalColorTable
        {
            private readonly ThemeColors _colors;

            public ThemedColorTable(ThemeColors colors)
            {
                _colors = colors;
            }

            public override Color ToolStripDropDownBackground { get { return _colors.Panel; } }
            public override Color ImageMarginGradientBegin { get { return _colors.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return _colors.Panel; } }
            public override Color ImageMarginGradientEnd { get { return _colors.Panel; } }
            public override Color MenuBorder { get { return _colors.Border; } }
            public override Color MenuItemBorder { get { return _colors.Hover; } }
            public override Color MenuItemSelected { get { return _colors.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return _colors.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return _colors.Hover; } }
            public override Color SeparatorDark { get { return _colors.Border; } }
            public override Color SeparatorLight { get { return _colors.Border; } }
        }
    }
}
