using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ConvenientFileTP.Core;
using ConvenientFileTP.UI.Widget;
using WinForms = System.Windows.Forms;

namespace ConvenientFileTP.UI
{
    /// <summary>
    /// Desktop floating widget: a round colour-coded ball that tucks itself into a slim bar when
    /// it sits on a screen edge, expands again on hover, and accepts dropped files to send them
    /// to the currently selected peer.
    /// </summary>
    public partial class FloatingWidget : Window
    {
        // ---- geometry (DIP) -------------------------------------------------------------
        private const double BallSizeDip = 96.0;
        private const double BarThicknessDip = 10.0;
        private const double BarLengthDip = 140.0;
        private const double ShadowPadDip = 12.0;
        private const double DockThresholdDip = 24.0;
        private const double DragStartThresholdDip = 4.0;

        // ---- animation / timers ---------------------------------------------------------
        private const int AnimDurationMs = 240;
        private const int CollapseDelayMs = 900;
        private const int StartupCollapseDelayMs = 1600;
        private const int PersistDelayMs = 900;

        private PeerInfo _target;

        private double _scale = 1.0;
        private double _ballSize = BallSizeDip;
        private double _pad = ShadowPadDip;
        private double _ballWindow = BallSizeDip + ShadowPadDip * 2;
        private double _barThickness = BarThicknessDip;
        private double _barLength = BarLengthDip;

        private WidgetEdge _edge = WidgetEdge.Right;
        private double _ratio = 0.5;
        private bool _docked;
        private bool _isBar;
        private bool _noAutoCollapse;

        private WidgetState _state = WidgetState.Idle;
        private double _progress;
        private readonly List<string> _unviewed = new List<string>();

        // drag state
        private bool _pressing;
        private bool _dragging;
        private bool _suppressClick;
        private bool _nativeCaptured;
        private Point _pressScreen;
        private double _pressLeft;
        private double _pressTop;
        private IntPtr _hwnd;
        private DispatcherTimer _pressWatcher;

        // layout / animation state
        private bool _loaded;
        private bool _envDockImmediate;
        private bool _dropHighlight;
        private bool _animActive;
        private bool _animToBall;
        private Rect _animFrom;
        private Rect _animTo;
        private double _animFromBarOpacity;
        private Stopwatch _animWatch;
        private bool _animHooked;
        private double _animLastElapsedMs;
        private double _animMaxFrameMs;
        private int _animFrameCount;
        private int _animDurationMs = AnimDurationMs;

        private DispatcherTimer _clickTimer;
        private DispatcherTimer _collapseTimer;
        private DispatcherTimer _persistTimer;
        private DispatcherTimer _idleTimer;

        // screenshot / QA demo mode
        private string _demoMode;
        private DateTime _demoStart;
        private TimeSpan _demoDuration = TimeSpan.FromSeconds(20);
        private DispatcherTimer _demoTimer;
        private readonly bool _trace = GetEnv("CFTP_WIDGET_TRACE") == "1";
        private static int _instanceCount;
        private readonly string _wid;
        private readonly int _instanceNo;

        public FloatingWidget()
        {
            InitializeComponent();
            _instanceNo = Interlocked.Increment(ref _instanceCount);
            _wid = "w" + _instanceNo + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            Log.Info("浮窗实例创建: " + _wid);

            _target = new PeerInfo
            {
                Id = "local",
                Name = "本机",
                Address = "127.0.0.1",
                IsSelf = true,
                Mode = NetworkMode.Loopback
            };

            Loaded += FloatingWidget_Loaded;
            BarPanel.SizeChanged += (s, e) => ApplyBarFill();
        }

        /// <summary>Peer this widget sends to / receives from.</summary>
        public PeerInfo Target
        {
            get { return _target; }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                WidgetNative.ApplyToolWindow(_hwnd);
                WidgetNative.PreventActivation(this);
                LostMouseCapture += FloatingWidget_LostMouseCapture;
                Log.Debug("浮窗输入初始化完成, hwnd=" + _hwnd);
            }
            catch
            {
                // cosmetic only
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!AppServices.IsExiting)
            {
                e.Cancel = true;
                HideWidget();
                return;
            }
            StopTimers();
            base.OnClosing(e);
        }

        // =================================================================================
        // Public contract surface
        // =================================================================================

        public void BindPeer(PeerInfo peer)
        {
            if (peer == null) return;
            _target = peer.Clone();
            _progress = 0;
            ClearUnviewed();
            SetState(WidgetState.Idle);
            UpdateNameText();
            Log.Info("浮窗已绑定设备: " + DisplayName());
        }

        public void UpdatePeer(PeerInfo peer)
        {
            if (peer == null) return;
            _target = peer.Clone();
            UpdateNameText();
        }

        public void ShowWidget()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowWidget));
                return;
            }

            if (!_loaded) EnsureScale();
            if (!IsVisible) Show();

            try
            {
                Topmost = false;
                Topmost = true;
            }
            catch
            {
                // cosmetic only
            }
        }

        public void HideWidget()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(HideWidget));
                return;
            }

            CancelCollapse();
            _pressing = false;
            _dragging = false;
            StopPressWatcher();
            try { ReleaseMouseCapture(); } catch { }
            if (_nativeCaptured)
            {
                _nativeCaptured = false;
                WidgetNative.ReleaseCaptureFrom(_hwnd);
            }
            if (IsVisible) Hide();
        }

        public void OnTransferProgress(TransferProgress progress)
        {
            if (progress == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnTransferProgress(progress)));
                return;
            }

            _progress = progress.Percent;
            SetState(progress.Direction == TransferDirection.Send ? WidgetState.Sending : WidgetState.Receiving);
            Log.Debug("浮窗进度 " + (int)Math.Round(_progress) + "% (" +
                      (progress.Direction == TransferDirection.Send ? "发送" : "接收") + ")");
        }

        public void OnTransferCompleted(TransferCompletedEventArgs completed)
        {
            if (completed == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnTransferCompleted(completed)));
                return;
            }

            CancelDemo();

            if (completed.Direction == TransferDirection.Receive)
            {
                if (completed.Success && completed.SavedFiles != null)
                {
                    foreach (string file in completed.SavedFiles)
                    {
                        if (string.IsNullOrEmpty(file)) continue;
                        if (!_unviewed.Contains(file)) _unviewed.Add(file);
                    }
                }

                if (!completed.Success)
                {
                    Log.Warn("浮窗接收失败: " + (completed.Error ?? "未知错误"));
                }

                if (_unviewed.Count > 0)
                {
                    _progress = 100;
                    SetState(WidgetState.Unviewed);
                    Log.Info("浮窗收到文件，未查看 " + _unviewed.Count + " 个");
                }
                else
                {
                    _progress = 0;
                    SetState(WidgetState.Idle);
                }
            }
            else
            {
                _progress = 0;
                SetState(_unviewed.Count > 0 ? WidgetState.Unviewed : WidgetState.Idle);
                if (completed.Success) Log.Info("浮窗发送完成");
                else Log.Warn("浮窗发送失败: " + (completed.Error ?? "未知错误"));
            }
        }

        // =================================================================================
        // Sizing / placement
        // =================================================================================

        private void EnsureScale()
        {
            double scale = 1.0;
            try
            {
                if (AppServices.Config != null) scale = AppServices.Config.WidgetScale;
            }
            catch
            {
                scale = 1.0;
            }
            if (double.IsNaN(scale) || scale < 0.6 || scale > 2.0) scale = 1.0;

            _scale = scale;
            _ballSize = Math.Round(BallSizeDip * scale);
            _pad = Math.Round(ShadowPadDip * scale);
            _ballWindow = _ballSize + _pad * 2;
            _barThickness = Math.Max(6, Math.Round(BarThicknessDip * scale));
            _barLength = Math.Max(72, Math.Round(BarLengthDip * scale));

            BallPanel.Margin = new Thickness(_pad);
            RingCanvas.Width = _ballSize;
            RingCanvas.Height = _ballSize;
            ProgressRing.StrokeThickness = Math.Max(2.5, Math.Round(_ballSize * 0.038, 1));
            NameText.MaxWidth = Math.Max(40, _ballSize - 12);
            BallShadow.BlurRadius = Math.Round(20 * Math.Max(0.7, scale * 0.9));

            // QA hook: stretch the transition so individual frames can be inspected.
            _animDurationMs = ReadIntEnv("CFTP_WIDGET_ANIM_MS", AnimDurationMs, 80, 3000);
        }

        private void FloatingWidget_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            EnsureScale();
            RestorePlacement();
            SetState(WidgetState.Idle);
            StartDemoIfRequested();
            StartIdleWatcher();
            StartTraceTimer();

            if (_envDockImmediate)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => CollapseToBar(false)));
            }
            else if (!_noAutoCollapse && _docked)
            {
                ScheduleCollapse(StartupCollapseDelayMs);
            }
        }

        private void RestorePlacement()
        {
            WidgetEdge edge = WidgetEdge.Right;
            double ratio = 0.5;

            try
            {
                if (AppServices.Config != null)
                {
                    edge = WidgetScreen.ParseEdge(AppServices.Config.WidgetEdge);
                    ratio = AppServices.Config.WidgetOffsetRatio;
                }
            }
            catch
            {
                edge = WidgetEdge.Right;
                ratio = 0.5;
            }
            if (edge == WidgetEdge.None) edge = WidgetEdge.Right;
            ratio = WidgetScreen.Clamp01(ratio);

            string envDock = GetEnv("CFTP_WIDGET_AUTODOCK");
            if (!string.IsNullOrEmpty(envDock))
            {
                string value = envDock.Trim().ToLowerInvariant();
                if (value == "ball" || value == "center")
                {
                    _noAutoCollapse = true;
                    if (value == "center")
                    {
                        var info = WidgetScreen.ForPrimary();
                        var centered = new Rect(
                            info.Left + (info.Width - _ballWindow) / 2.0,
                            info.Top + (info.Height - _ballWindow) / 2.0,
                            _ballWindow, _ballWindow);
                        _docked = false;
                        _isBar = false;
                        SetWindowRect(centered);
                        ShowBallPanel();
                        Log.Info("浮窗测试钩子: 球体居中显示");
                        return;
                    }
                }
                else
                {
                    WidgetEdge requested = WidgetScreen.ParseEdge(value);
                    if (requested != WidgetEdge.None)
                    {
                        edge = requested;
                        _envDockImmediate = true;
                        string ratioText = GetEnv("CFTP_WIDGET_AUTODOCK_RATIO");
                        double parsed;
                        if (!string.IsNullOrEmpty(ratioText) &&
                            double.TryParse(ratioText, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        {
                            ratio = WidgetScreen.Clamp01(parsed);
                        }
                    }
                }
            }

            _edge = edge;
            _ratio = ratio;
            _docked = true;
            _isBar = false;

            var screen = WidgetScreen.ForVisual(this);
            SetWindowRect(WidgetScreen.BallRect(screen, _edge, _ratio, _ballWindow));
            ShowBallPanel();
            Log.Info("浮窗位置恢复: " + WidgetScreen.EdgeName(_edge) + " " + _ratio.ToString("0.###"));
        }

        private void SetWindowRect(Rect rect)
        {
            if (double.IsNaN(rect.Left) || double.IsNaN(rect.Top) ||
                double.IsNaN(rect.Width) || double.IsNaN(rect.Height)) return;

            Left = rect.Left;
            Top = rect.Top;
            Width = Math.Max(1, rect.Width);
            Height = Math.Max(1, rect.Height);
            Trace("rect " + Left.ToString("0.0") + "," + Top.ToString("0.0") + " " +
                  Width.ToString("0.0") + "x" + Height.ToString("0.0"));
        }

        private void Trace(string message)
        {
            if (!_trace) return;
            try { Log.Info("浮窗TRACE [" + _wid + "] " + message); } catch { }
        }

        private void StartTraceTimer()
        {
            if (!_trace) return;
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += delegate
            {
                try
                {
                    Trace("state visible=" + IsVisible + " active=" + IsActive +
                          " mouseOver=" + IsMouseOver +
                          " rect=" + Left.ToString("0.0") + "," + Top.ToString("0.0") + " " +
                          Width.ToString("0.0") + "x" + Height.ToString("0.0") +
                          " root=" + RootGrid.ActualWidth.ToString("0.0") + "x" + RootGrid.ActualHeight.ToString("0.0") +
                          " ball=" + BallPanel.Visibility + "/" + BallPanel.Opacity.ToString("0.00") +
                          " bar=" + BarPanel.Visibility + "/" + BarPanel.Opacity.ToString("0.00") +
                          " isBar=" + _isBar + " docked=" + _docked +
                          " cursor=" + WidgetNative.GetCursorPoint());
                }
                catch { }
            };
            timer.Start();
        }

        private void ShowBallPanel()
        {
            BallPanel.Visibility = Visibility.Visible;
            BallPanel.Opacity = 1;
            BallMorphScale.ScaleX = 1;
            BallMorphScale.ScaleY = 1;
            CenterStack.Opacity = 1;
            NameBadge.Opacity = 1;
            RingCanvas.Opacity = 1;
            BarPanel.Visibility = Visibility.Collapsed;
            BarPanel.Opacity = 0;
            Trace("showball");
        }

        private void ShowBarPanel()
        {
            BallPanel.Visibility = Visibility.Collapsed;
            BallPanel.Opacity = 0;
            BallMorphScale.ScaleX = 1;
            BallMorphScale.ScaleY = 1;
            CenterStack.Opacity = 0;
            NameBadge.Opacity = 0;
            RingCanvas.Opacity = 0;
            BarPanel.Visibility = Visibility.Visible;
            BarPanel.Opacity = 1;
            ApplyBarFill();
            Trace("showbar");
        }

        // =================================================================================
        // State colouring / progress
        // =================================================================================

        private void SetState(WidgetState state)
        {
            _state = state;
            ApplyStateVisuals();
        }

        private void ApplyStateVisuals()
        {
            Color color = WidgetPalette.ColorFor(_state);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            Ball.Fill = brush;
            BarPanel.BorderBrush = brush;
            BarFill.Background = brush;
            BarIdlePill.Background = brush;

            bool transferring = _state == WidgetState.Sending || _state == WidgetState.Receiving;

            IconText.Visibility = transferring ? Visibility.Collapsed : Visibility.Visible;
            ProgressRing.Visibility = transferring ? Visibility.Visible : Visibility.Collapsed;
            PercentText.Visibility = transferring ? Visibility.Visible : Visibility.Collapsed;
            HintText.Visibility = transferring ? Visibility.Visible : Visibility.Collapsed;
            BarIdlePill.Visibility = _state == WidgetState.Idle ? Visibility.Visible : Visibility.Collapsed;

            if (WidgetScreen.IsVertical(_edge))
            {
                BarIdlePill.Width = Math.Max(2, Math.Round(3 * _scale));
                BarIdlePill.Height = Math.Max(16, Math.Round(44 * _scale));
            }
            else
            {
                BarIdlePill.Width = Math.Max(16, Math.Round(44 * _scale));
                BarIdlePill.Height = Math.Max(2, Math.Round(3 * _scale));
            }

            ApplyProgressToVisuals();
            UpdateNameText();
        }

        private void ApplyProgressToVisuals()
        {
            bool transferring = _state == WidgetState.Sending || _state == WidgetState.Receiving;

            if (transferring)
            {
                double percent = Math.Max(0, Math.Min(100, _progress));
                PercentText.Text = ((int)Math.Round(percent)) + "%";
                HintText.Text = _state == WidgetState.Sending ? "发送中" : "接收中";
                UpdateProgressRing(percent);
            }
            else if (_state == WidgetState.Unviewed)
            {
                UpdateProgressRing(100);
            }

            ApplyBarFill();
        }

        private void UpdateProgressRing(double percent)
        {
            double size = _ballSize;
            double thickness = ProgressRing.StrokeThickness;
            double radius = Math.Max(2, size / 2.0 - thickness / 2.0 - 1.0);
            var center = new Point(size / 2.0, size / 2.0);

            double fraction = Math.Max(0.0005, Math.Min(1.0, percent / 100.0));
            double sweep = 360.0 * fraction;
            if (sweep > 359.9) sweep = 359.9;

            Point start = new Point(center.X, center.Y - radius);
            Point end = PointOnCircle(center, radius, -90.0 + sweep);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(start, false, false);
                context.ArcTo(end, new Size(radius, radius), 0, sweep > 180.0,
                    SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            ProgressRing.Data = geometry;
        }

        private static Point PointOnCircle(Point center, double radius, double angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        private void ApplyBarFill()
        {
            if (BarPanel == null || BarFill == null) return;

            bool transferring = _state == WidgetState.Sending || _state == WidgetState.Receiving;
            if (!transferring && _state != WidgetState.Unviewed)
            {
                BarFill.Visibility = Visibility.Collapsed;
                return;
            }

            double innerWidth = Math.Max(1, BarPanel.ActualWidth - 6);
            double innerHeight = Math.Max(1, BarPanel.ActualHeight - 6);
            double fraction = _state == WidgetState.Unviewed ? 1.0 : Math.Max(0.01, Math.Min(1.0, _progress / 100.0));

            BarFill.Visibility = Visibility.Visible;
            if (WidgetScreen.IsVertical(_edge))
            {
                BarFill.Width = double.NaN;
                BarFill.Height = Math.Max(1, innerHeight * fraction);
                BarFill.HorizontalAlignment = HorizontalAlignment.Stretch;
                BarFill.VerticalAlignment = VerticalAlignment.Bottom;
                BarFill.CornerRadius = new CornerRadius(3);
            }
            else
            {
                BarFill.Width = Math.Max(1, innerWidth * fraction);
                BarFill.Height = double.NaN;
                BarFill.HorizontalAlignment = HorizontalAlignment.Left;
                BarFill.VerticalAlignment = VerticalAlignment.Stretch;
                BarFill.CornerRadius = new CornerRadius(3);
            }
        }

        private void UpdateNameText()
        {
            if (NameText == null) return;
            NameText.Text = _dropHighlight ? "松开发送" : DisplayName();
        }

        private string DisplayName()
        {
            try
            {
                if (_target == null) return "未选择设备";
                string name = _target.DisplayName;
                if (string.IsNullOrEmpty(name)) name = _target.Name;
                return string.IsNullOrEmpty(name) ? "未选择设备" : name;
            }
            catch
            {
                return "未选择设备";
            }
        }

        private void ClearUnviewed()
        {
            if (_unviewed.Count == 0) return;
            _unviewed.Clear();
            if (_state == WidgetState.Unviewed) SetState(WidgetState.Idle);
        }

        // =================================================================================
        // Hover / expand / collapse / animations
        // =================================================================================

        private void RootGrid_MouseEnter(object sender, MouseEventArgs e)
        {
            Trace("mouseenter");
            CancelCollapse();
            if (_isBar) ExpandFromBar(true);
        }

        private void RootGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_pressing && !WidgetNative.IsLeftButtonDown()) EndPress(false);
            if (_pressing || _dragging || _animActive) return;
            if (_docked && !_isBar) ScheduleCollapse(CollapseDelayMs);
        }

        private void ExpandFromBar(bool animate)
        {
            Trace("expand animate=" + animate + " isBar=" + _isBar + " animActive=" + _animActive + " animToBall=" + _animToBall);
            CancelCollapse();
            var screen = WidgetScreen.ForVisual(this);
            var target = WidgetScreen.BallRect(screen, _edge, _ratio, _ballWindow);

            if (!animate)
            {
                StopAnimation();
                _isBar = false;
                SetWindowRect(target);
                ShowBallPanel();
                return;
            }

            if (_animActive && _animToBall) return;   // already expanding
            if (!_animActive && !_isBar) return;      // already a ball

            _isBar = false;
            BeginPanelAnimation(new Rect(Left, Top, Width, Height), target, true);
        }

        private void CollapseToBar(bool animate)
        {
            Trace("collapse animate=" + animate + " docked=" + _docked + " isBar=" + _isBar + " animActive=" + _animActive + " animToBall=" + _animToBall);
            if (!_docked) return;

            var screen = WidgetScreen.ForVisual(this);
            var target = WidgetScreen.BarRect(screen, _edge, _ratio, _barThickness, _barLength);

            if (!animate)
            {
                StopAnimation();
                _isBar = true;
                SetWindowRect(target);
                ShowBarPanel();
                return;
            }

            if (_animActive && !_animToBall) return;  // already collapsing
            if (!_animActive && _isBar) return;       // already a bar

            _isBar = true;
            BeginPanelAnimation(new Rect(Left, Top, Width, Height), target, false);
        }

        /// <summary>
        /// Starts (or smoothly reverses) the ball/bar morph. The loop is driven by
        /// <see cref="CompositionTarget.Rendering"/> so it stays locked to the compositor
        /// frame cadence instead of a 16 ms DispatcherTimer, and the window rectangle,
        /// cross-fade and shape scale are all interpolated from one monotonic clock.
        /// </summary>
        private void BeginPanelAnimation(Rect from, Rect to, bool toBall)
        {
            bool resuming = _animActive;
            Trace("begin toBall=" + toBall + " resuming=" + resuming + " barOp=" + BarPanel.Opacity.ToString("0.00") + " ballVis=" + BallPanel.Visibility + " barVis=" + BarPanel.Visibility);

            _animFrom = from;
            _animTo = to;
            _animToBall = toBall;

            BallPanel.Visibility = Visibility.Visible;
            BarPanel.Visibility = Visibility.Visible;
            BallPanel.Opacity = 1.0;
            BallMorphScale.ScaleX = 1;
            BallMorphScale.ScaleY = 1;

            // Ball content (icon / progress / device name) would look smeared while the
            // window is squashed to bar size, so it only fades in once the shape is
            // roughly round again (and fades out before the collapse starts).
            double contentOpacity = toBall ? 0.0 : 1.0;
            CenterStack.Opacity = contentOpacity;
            NameBadge.Opacity = contentOpacity;
            RingCanvas.Opacity = contentOpacity;

            // The morph is carried by the ball panel: because the window itself is being
            // resized, the round ball is automatically squashed into a vertical pill at
            // bar size. We therefore only fade the crisp bar shell in/out at the ends
            // instead of cross-fading two differently-stretched shapes (which produced a
            // visible dark rectangle mid-transition).
            if (toBall)
            {
                _animFromBarOpacity = resuming ? BarPanel.Opacity : 1.0;
                BarPanel.Opacity = _animFromBarOpacity;
            }
            else
            {
                _animFromBarOpacity = 0.0;
                BarPanel.Opacity = 0.0;
            }

            _animActive = true;
            _animWatch = Stopwatch.StartNew();
            _animLastElapsedMs = 0;
            _animMaxFrameMs = 0;
            _animFrameCount = 0;

            if (!_animHooked)
            {
                CompositionTarget.Rendering += AnimFrame;
                _animHooked = true;
            }
        }

        private void AnimFrame(object sender, EventArgs e)
        {
            if (!_animActive || _animWatch == null) return;

            double elapsed = _animWatch.Elapsed.TotalMilliseconds;
            if (_animLastElapsedMs > 0)
            {
                double delta = elapsed - _animLastElapsedMs;
                if (delta > _animMaxFrameMs) _animMaxFrameMs = delta;
            }
            _animLastElapsedMs = elapsed;
            _animFrameCount++;

            double linear = Math.Max(0.0, Math.Min(1.0, elapsed / Math.Max(1.0, _animDurationMs)));
            double posEase = EaseInOutCubic(linear);

            SetWindowRect(new Rect(
                _animFrom.Left + (_animTo.Left - _animFrom.Left) * posEase,
                _animFrom.Top + (_animTo.Top - _animFrom.Top) * posEase,
                _animFrom.Width + (_animTo.Width - _animFrom.Width) * posEase,
                _animFrom.Height + (_animTo.Height - _animFrom.Height) * posEase));

            if (_animToBall)
            {
                // Expanding: the squashed ball is already visible inside the growing
                // window; fade the bar shell out during the first fifth of the move.
                double barFade = SmoothStep(Math.Min(1.0, linear / 0.20));
                BarPanel.Opacity = _animFromBarOpacity * (1.0 - barFade);
                BallPanel.Opacity = 1.0;
            }
            else
            {
                // Collapsing: keep the squashed ball visible for most of the move, then
                // cross-fade to the crisp bar shell over the final part.
                double swapT = Math.Max(0.0, Math.Min(1.0, (linear - 0.55) / 0.45));
                double swap = SmoothStep(swapT);
                BallPanel.Opacity = 1.0 - swap;
                BarPanel.Opacity = swap;
            }

            double contentOpacity;
            if (_animToBall)
            {
                contentOpacity = SmoothStep(Math.Max(0.0, Math.Min(1.0, (linear - 0.30) / 0.45)));
            }
            else
            {
                contentOpacity = 1.0 - SmoothStep(Math.Min(1.0, linear / 0.35));
            }
            CenterStack.Opacity = contentOpacity;
            NameBadge.Opacity = contentOpacity;
            RingCanvas.Opacity = contentOpacity;

            if (linear >= 1.0) FinishAnimation();
        }

        private void FinishAnimation()
        {
            if (!_animActive) return;

            bool toBall = _animToBall;
            int frames = _animFrameCount;
            double maxFrame = _animMaxFrameMs;

            Trace("finish toBall=" + toBall + " docked=" + _docked + " isBar=" + _isBar +
                  " frames=" + frames +
                  " ballOp=" + BallPanel.Opacity.ToString("0.00") + " barOp=" + BarPanel.Opacity.ToString("0.00"));

            StopAnimation();
            _isBar = !toBall;

            SetWindowRect(_animTo);
            BallMorphScale.ScaleX = 1;
            BallMorphScale.ScaleY = 1;

            if (toBall)
            {
                BallPanel.Visibility = Visibility.Visible;
                BallPanel.Opacity = 1;
                BarPanel.Visibility = Visibility.Collapsed;
                BarPanel.Opacity = 0;
                CenterStack.Opacity = 1;
                NameBadge.Opacity = 1;
                RingCanvas.Opacity = 1;
            }
            else
            {
                BallPanel.Visibility = Visibility.Collapsed;
                BallPanel.Opacity = 0;
                BarPanel.Visibility = Visibility.Visible;
                BarPanel.Opacity = 1;
                CenterStack.Opacity = 0;
                NameBadge.Opacity = 0;
                RingCanvas.Opacity = 0;
                ApplyBarFill();
            }

            Trace("finish-end ballOp=" + BallPanel.Opacity.ToString("0.00") + " barOp=" + BarPanel.Opacity.ToString("0.00") +
                  " ballVis=" + BallPanel.Visibility + " barVis=" + BarPanel.Visibility + " isBar=" + _isBar);

            if (GetEnv("CFTP_WIDGET_ANIM_DEBUG") == "1")
            {
                Log.Info("浮窗动画: " + (toBall ? "展开" : "收起") + " " + _animDurationMs +
                         "ms, 帧数 " + frames + ", 最大帧间隔 " + maxFrame.ToString("0.0") + "ms");
            }

            if (toBall)
            {
                if (_docked && !IsMouseOver) ScheduleCollapse(CollapseDelayMs);
            }
            else if (IsMouseOver)
            {
                // The pointer followed the widget while it collapsed; open it again smoothly.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!_animActive && _isBar) ExpandFromBar(true);
                }));
            }
        }

        private void StopAnimation()
        {
            _animActive = false;
            if (_animHooked)
            {
                CompositionTarget.Rendering -= AnimFrame;
                _animHooked = false;
            }
            _animWatch = null;
        }

        private static double EaseInOutCubic(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t < 0.5 ? 4.0 * t * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;
        }

        private static double SmoothStep(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * (3.0 - 2.0 * t);
        }

        private void ScheduleCollapse(int milliseconds)
        {
            if (_noAutoCollapse) return;
            if (_collapseTimer == null)
            {
                _collapseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
                _collapseTimer.Tick += CollapseTimer_Tick;
            }
            _collapseTimer.Stop();
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _collapseTimer.Start();
        }

        private void CancelCollapse()
        {
            if (_collapseTimer != null) _collapseTimer.Stop();
        }

        private void CollapseTimer_Tick(object sender, EventArgs e)
        {
            if (_collapseTimer != null) _collapseTimer.Stop();
            if (_pressing && !WidgetNative.IsLeftButtonDown()) EndPress(false);
            if (_animActive || _pressing || _dragging || !_docked || _isBar) return;
            if (IsMouseOver)
            {
                ScheduleCollapse(700);
                return;
            }
            CollapseToBar(true);
        }

        // =================================================================================
        // Dragging / clicking
        // =================================================================================

        private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                _suppressClick = true;
                CancelCollapse();
                ClearUnviewed();
                AppServices.OpenDownloadDirectory();
                e.Handled = true;
                return;
            }

            if (_isBar)
            {
                ExpandFromBar(true);
                e.Handled = true;
                return;
            }

            CancelCollapse();
            _pressing = true;
            _dragging = false;
            _suppressClick = false;
            _pressScreen = WidgetNative.GetCursorPoint();
            _pressLeft = Left;
            _pressTop = Top;

            bool wpfCaptured = false;
            try { wpfCaptured = CaptureMouse(); } catch { }
            if (!wpfCaptured)
            {
                // WPF capture can fail while the widget is not the active window; the
                // Win32 capture plus the button poll below keep the drag alive anyway.
                WidgetNative.CaptureTo(_hwnd);
                _nativeCaptured = true;
            }

            StartPressWatcher();
            Log.Debug("浮窗按下 capture=" + wpfCaptured);
            e.Handled = true;
        }

        private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_pressing) return;
            if (!WidgetNative.IsLeftButtonDown())
            {
                // The mouse-up never reached us (focus/capture race). Recover instead of
                // staying in a phantom "pressed" state that makes the widget follow the cursor.
                EndPress(false);
                return;
            }

            UpdateDrag();
        }

        private void RootGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pressing) return;
            EndPress(false);
            e.Handled = true;
        }

        /// <summary>Moves the widget with the pointer while a drag is in progress.</summary>
        private void UpdateDrag()
        {
            Point current = WidgetNative.GetCursorPoint();
            double dx = (current.X - _pressScreen.X) / GetDpiScaleX();
            double dy = (current.Y - _pressScreen.Y) / GetDpiScaleY();

            if (!_dragging && (Math.Abs(dx) > DragStartThresholdDip || Math.Abs(dy) > DragStartThresholdDip))
            {
                _dragging = true;
                _docked = false;
                CancelCollapse();
                Log.Debug("浮窗开始拖动");
            }

            if (_dragging)
            {
                Left = _pressLeft + dx;
                Top = _pressTop + dy;
            }
        }

        /// <summary>
        /// Completes a press exactly once. <paramref name="fromPoll"/> means the Win32 button
        /// poll noticed the release before WPF delivered a mouse-up.
        /// </summary>
        private void EndPress(bool fromPoll)
        {
            if (!_pressing && !_dragging) return;

            bool wasDragging = _dragging;
            _pressing = false;
            _dragging = false;
            StopPressWatcher();
            try { ReleaseMouseCapture(); } catch { }
            if (_nativeCaptured)
            {
                _nativeCaptured = false;
                WidgetNative.ReleaseCaptureFrom(_hwnd);
            }

            if (wasDragging) FinishDrag();
            else if (!_suppressClick) StartClickTimer();

            Log.Debug("浮窗松开" + (fromPoll ? "(轮询)" : ""));
        }

        private void FloatingWidget_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_pressing) return;
            if (!WidgetNative.IsLeftButtonDown())
            {
                EndPress(false);
                return;
            }
            Log.Debug("浮窗丢失鼠标捕获(按键仍按下)");
        }

        private void StartPressWatcher()
        {
            if (_pressWatcher == null)
            {
                _pressWatcher = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _pressWatcher.Tick += PressWatcher_Tick;
            }
            _pressWatcher.Stop();
            _pressWatcher.Start();
        }

        private void StopPressWatcher()
        {
            try { if (_pressWatcher != null) _pressWatcher.Stop(); } catch { }
        }

        private void PressWatcher_Tick(object sender, EventArgs e)
        {
            if (!_pressing)
            {
                StopPressWatcher();
                return;
            }
            if (!WidgetNative.IsLeftButtonDown())
            {
                // The physical button was released but no mouse-up arrived: finish the
                // gesture from the poll so the widget can never stay captured/ghost-dragged.
                EndPress(true);
                return;
            }
            UpdateDrag();
        }

        /// <summary>
        /// Keeps the collapsed/expanded state in sync with the physical cursor. WPF's
        /// IsMouseOver / MouseLeave can go stale when the tiny window is resized underneath a
        /// still pointer (which previously left the ball expanded forever); this independent
        /// 250 ms check makes hover and auto-collapse deterministic.
        /// </summary>
        private void StartIdleWatcher()
        {
            if (_idleTimer == null)
            {
                _idleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _idleTimer.Tick += IdleTimer_Tick;
            }
            _idleTimer.Start();
        }

        private void IdleTimer_Tick(object sender, EventArgs e)
        {
            if (!IsVisible) return;

            if (_pressing && !WidgetNative.IsLeftButtonDown()) EndPress(false);
            if (_animActive || _pressing || _dragging) return;

            bool over = IsCursorOverWidget();

            if (_isBar)
            {
                // Self-heal: an interrupted/retargeted animation can leave the window
                // smaller than the bar (or the bar faded out). The window is still
                // topmost and hit-testable, so the user sees an invisible strip that
                // also blocks clicks; re-assert the canonical bar geometry here.
                if (!_animActive && _docked)
                {
                    Rect expected = WidgetScreen.BarRect(
                        WidgetScreen.ForVisual(this), _edge, _ratio, _barThickness, _barLength);
                    bool sizeOff = Math.Abs(Width - expected.Width) > 0.8
                                   || Math.Abs(Height - expected.Height) > 0.8
                                   || Math.Abs(Left - expected.Left) > 0.8
                                   || Math.Abs(Top - expected.Top) > 0.8;
                    if (sizeOff || BarPanel.Visibility != Visibility.Visible || BarPanel.Opacity < 0.99)
                    {
                        Trace("self-heal bar: rect=" + Width.ToString("0.0") + "x" + Height.ToString("0.0")
                              + " barOp=" + BarPanel.Opacity.ToString("0.00")
                              + " barVis=" + BarPanel.Visibility + " expected="
                              + expected.Width.ToString("0.0") + "x" + expected.Height.ToString("0.0"));
                        SetWindowRect(expected);
                        ShowBarPanel();
                    }
                }
                if (over) ExpandFromBar(true);
                return;
            }

            if (!_docked) return;

            if (over)
            {
                CancelCollapse();
            }
            else if (_collapseTimer == null || !_collapseTimer.IsEnabled)
            {
                ScheduleCollapse(CollapseDelayMs);
            }
        }

        private bool IsCursorOverWidget()
        {
            try
            {
                if (_hwnd == IntPtr.Zero) return IsMouseOver;
                return WidgetNative.WindowFromPoint(WidgetNative.GetCursorPoint()) == _hwnd;
            }
            catch
            {
                return IsMouseOver;
            }
        }

        private void FinishDrag()
        {
            var screen = WidgetScreen.ForVisual(this);
            var ballRect = new Rect(Left + _pad, Top + _pad, _ballSize, _ballSize);
            WidgetEdge edge = WidgetScreen.NearestEdge(screen, ballRect, DockThresholdDip);

            if (edge != WidgetEdge.None)
            {
                _edge = edge;
                _ratio = WidgetScreen.RatioAlongEdge(screen, edge, ballRect);
                _docked = true;
                _isBar = false;
                SetWindowRect(WidgetScreen.BallRect(screen, _edge, _ratio, _ballWindow));
                PersistPositionLater();
                ScheduleCollapse(320);
                Log.Info("浮窗吸附到 " + WidgetScreen.EdgeName(_edge) + " 边，位置比例 " + _ratio.ToString("0.###"));
            }
            else
            {
                _docked = false;
                var clamped = WidgetScreen.ClampInside(screen, new Rect(Left, Top, _ballWindow, _ballWindow));
                SetWindowRect(clamped);
                var clampedBall = new Rect(clamped.Left + _pad, clamped.Top + _pad, _ballSize, _ballSize);
                WidgetEdge nearest = WidgetScreen.NearestEdge(screen, clampedBall, double.MaxValue);
                if (nearest != WidgetEdge.None)
                {
                    _edge = nearest;
                    _ratio = WidgetScreen.RatioAlongEdge(screen, nearest, clampedBall);
                }
                PersistPositionLater();
                Log.Info("浮窗自由放置，记录边 " + WidgetScreen.EdgeName(_edge));
            }
        }

        private void StartClickTimer()
        {
            if (_clickTimer == null)
            {
                _clickTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
                _clickTimer.Tick += ClickTimer_Tick;
            }
            _clickTimer.Stop();
            int doubleClickMs = 500;
            try { doubleClickMs = WinForms.SystemInformation.DoubleClickTime; } catch { }
            _clickTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(220, doubleClickMs + 40));
            _clickTimer.Start();
        }

        private void ClickTimer_Tick(object sender, EventArgs e)
        {
            if (_clickTimer != null) _clickTimer.Stop();
            HandleBallClick();
        }

        private void HandleBallClick()
        {
            if (_isBar)
            {
                ExpandFromBar(true);
                return;
            }

            var files = _unviewed.Where(File.Exists).ToList();
            if (files.Count == 1)
            {
                OpenFile(files[0]);
                ClearUnviewed();
            }
            else if (files.Count > 1)
            {
                ClearUnviewed();
                AppServices.OpenDownloadDirectory();
            }
            else
            {
                AppServices.ShowMainWindow();
            }
        }

        private static void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开文件失败: " + ex.Message);
            }
        }

        // =================================================================================
        // File drop
        // =================================================================================

        private void RootGrid_DragEnter(object sender, DragEventArgs e)
        {
            HandleDragOver(e);
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            HandleDragOver(e);
        }

        private void HandleDragOver(DragEventArgs e)
        {
            if (!HasFileDrop(e))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            CancelCollapse();
            if (_isBar) ExpandFromBar(true);
            SetDropHighlight(true);
        }

        private void RootGrid_DragLeave(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
        }

        private void RootGrid_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropHighlight(false);

            string[] dropped = null;
            try
            {
                dropped = e.Data != null ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            }
            catch
            {
                dropped = null;
            }

            var paths = dropped == null
                ? new List<string>()
                : dropped.Where(File.Exists).ToList();

            if (paths.Count == 0)
            {
                Log.Warn("浮窗拖入的内容中没有文件");
                return;
            }

            var target = _target;
            if (target == null || target.IsSelf)
            {
                try
                {
                    if (AppServices.Main != null)
                    {
                        AppServices.Main.ShowToast("浮窗", "请先在主窗口选择要发送到的设备");
                    }
                }
                catch
                {
                    // toast is best-effort
                }
                CancelCollapse();
                AppServices.ShowMainWindow();
                return;
            }

            StartSend(paths, target);
        }

        private static bool HasFileDrop(DragEventArgs e)
        {
            try
            {
                return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            }
            catch
            {
                return false;
            }
        }

        private void StartSend(List<string> paths, PeerInfo target)
        {
            _progress = 0;
            SetState(WidgetState.Sending);

            PeerInfo peer = target.Clone();
            var files = new List<string>(paths);
            Log.Info("浮窗拖入 " + files.Count + " 个文件，发送到 " + target.Name);

            Task.Run(async () =>
            {
                try
                {
                    await AppServices.Transfers.SendFilesAsync(peer, files, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("浮窗发送失败", ex);
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _progress = 0;
                        SetState(_unviewed.Count > 0 ? WidgetState.Unviewed : WidgetState.Idle);
                    }));
                }
            });
        }

        private void SetDropHighlight(bool highlight)
        {
            if (_dropHighlight == highlight) return;
            _dropHighlight = highlight;

            Ball.Stroke = highlight
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
            Ball.StrokeThickness = highlight ? 3.0 : 1.5;
            BarPanel.BorderThickness = new Thickness(highlight ? 2 : 1);

            double scale = highlight ? 1.07 : 1.0;
            var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(130));
            try
            {
                BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
            catch
            {
                BallScale.ScaleX = scale;
                BallScale.ScaleY = scale;
            }

            UpdateNameText();
        }

        // =================================================================================
        // Context menu
        // =================================================================================

        private void RootGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            CancelCollapse();
            _pressing = false;
            _dragging = false;
            _suppressClick = true;
        }

        private void MenuOpenDownload_Click(object sender, RoutedEventArgs e)
        {
            ClearUnviewed();
            AppServices.OpenDownloadDirectory();
        }

        private void MenuShowMain_Click(object sender, RoutedEventArgs e)
        {
            AppServices.ShowMainWindow();
        }

        private void MenuHide_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Exit();
        }

        // =================================================================================
        // Position persistence
        // =================================================================================

        private void PersistPositionLater()
        {
            if (_persistTimer == null)
            {
                _persistTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
                _persistTimer.Tick += PersistTimer_Tick;
            }
            _persistTimer.Stop();
            _persistTimer.Interval = TimeSpan.FromMilliseconds(PersistDelayMs);
            _persistTimer.Start();
        }

        private void PersistTimer_Tick(object sender, EventArgs e)
        {
            if (_persistTimer != null) _persistTimer.Stop();
            PersistPositionNow();
        }

        private void PersistPositionNow()
        {
            try
            {
                var appConfig = AppServices.Config;
                if (appConfig == null) return;
                if (AppServices.IsExiting) return;

                appConfig.WidgetEdge = WidgetScreen.EdgeName(_edge);
                appConfig.WidgetOffsetRatio = Math.Round(WidgetScreen.Clamp01(_ratio), 4);
                AppServices.SaveConfig();
                Log.Info("浮窗位置已保存: " + appConfig.WidgetEdge + " " + appConfig.WidgetOffsetRatio.ToString("0.###"));
            }
            catch (Exception ex)
            {
                Log.Warn("保存浮窗位置失败: " + ex.Message);
            }
        }

        // =================================================================================
        // Helpers
        // =================================================================================

        private Point GetScreenPoint(MouseEventArgs e)
        {
            try
            {
                return PointToScreen(e.GetPosition(this));
            }
            catch
            {
                return new Point(0, 0);
            }
        }

        private double GetDpiScaleX()
        {
            try
            {
                double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return scale > 0.1 ? scale : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private double GetDpiScaleY()
        {
            try
            {
                double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
                return scale > 0.1 ? scale : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private static string GetEnv(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static int ReadIntEnv(string name, int fallback, int min, int max)
        {
            string text = GetEnv(name);
            int parsed;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= min && parsed <= max)
            {
                return parsed;
            }
            return fallback;
        }

        private void StopTimers()
        {
            StopAnimation();
            StopPressWatcher();
            try { if (_collapseTimer != null) _collapseTimer.Stop(); } catch { }
            try { if (_clickTimer != null) _clickTimer.Stop(); } catch { }
            try { if (_persistTimer != null) _persistTimer.Stop(); } catch { }
            try { if (_demoTimer != null) _demoTimer.Stop(); } catch { }
        }

        // =================================================================================
        // QA / screenshot demo mode (enabled only via CFTP_WIDGET_DEMO environment variable)
        // =================================================================================

        private void StartDemoIfRequested()
        {
            string mode = GetEnv("CFTP_WIDGET_DEMO");
            if (string.IsNullOrEmpty(mode)) return;

            _demoMode = mode.Trim().ToLowerInvariant();

            double seconds = 20;
            double parsed;
            if (double.TryParse(GetEnv("CFTP_WIDGET_DEMO_SECONDS"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out parsed) && parsed >= 1 && parsed <= 600)
            {
                seconds = parsed;
            }
            _demoDuration = TimeSpan.FromSeconds(seconds);

            if (_demoMode == "unviewed")
            {
                var files = CreateDemoFiles(2);
                _unviewed.Clear();
                _unviewed.AddRange(files);
                SetState(WidgetState.Unviewed);
                Log.Info("浮窗演示模式: 未查看文件(黄)");
                return;
            }

            if (_demoMode != "send" && _demoMode != "receive") return;

            _progress = 0;
            SetState(_demoMode == "send" ? WidgetState.Sending : WidgetState.Receiving);
            _demoStart = DateTime.UtcNow;
            _demoTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _demoTimer.Tick += DemoTimer_Tick;
            _demoTimer.Start();
            Log.Info("浮窗演示模式开始: " + _demoMode + " " + seconds + " 秒");
        }

        private void DemoTimer_Tick(object sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _demoStart).TotalMilliseconds / Math.Max(1, _demoDuration.TotalMilliseconds);
            if (t > 1) t = 1;
            _progress = t * 100;
            ApplyProgressToVisuals();

            if (t >= 1)
            {
                if (_demoTimer != null) _demoTimer.Stop();
                CompleteDemo();
            }
        }

        private void CompleteDemo()
        {
            var completed = new TransferCompletedEventArgs
            {
                Direction = _demoMode == "send" ? TransferDirection.Send : TransferDirection.Receive,
                Success = true,
                SavedFiles = new string[0],
                FinishedUtc = DateTime.UtcNow
            };

            if (_demoMode == "receive")
            {
                var files = CreateDemoFiles(1);
                completed.SavedFiles = files.ToArray();
            }

            Log.Info("浮窗演示模式结束: " + _demoMode);
            OnTransferCompleted(completed);
        }

        private List<string> CreateDemoFiles(int count)
        {
            var files = new List<string>();
            try
            {
                string directory = AppServices.DownloadDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    directory = Path.Combine(AppPaths.DataDirectory, "downloads");
                }
                Directory.CreateDirectory(directory);

                for (int i = 0; i < count; i++)
                {
                    string name = "浮窗演示文件-" + DateTime.Now.ToString("HHmmss") + "-" + (i + 1) + ".txt";
                    string path = Path.Combine(directory, name);
                    File.WriteAllText(path, "ConvenientFileTP 浮窗演示文件\r\n生成时间: " +
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n");
                    files.Add(path);
                }
                Log.Info("演示文件已生成 " + files.Count + " 个");
            }
            catch (Exception ex)
            {
                Log.Warn("创建演示文件失败: " + ex.Message);
            }
            return files;
        }

        private void CancelDemo()
        {
            if (_demoTimer != null)
            {
                _demoTimer.Stop();
                _demoTimer = null;
            }
        }
    }
}
