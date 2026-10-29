# 内部接口契约（并行开发冻结版）

本文件是并行开发期间各工作流的**冻结接口**。任何签名变更必须先通知 Lead，不要自行改动。

## 运行环境约束

- 目标框架：.NET Framework 4.8，WPF + WinForms，`net48`，C# latest。
- **Windows Smart App Control 已开启**：任何本地编译的 exe/dll 直接从磁盘执行都会被阻止；`.vbs` 脚本入口也会被拦截（“Dangerous file extension from the web”）。
  程序必须编译为 **库**（`app\ConvenientFileTP.dll`），由已签名的 `powershell.exe` 通过 `Assembly.Load(bytes)` 从内存加载。
  启动命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File launcher.ps1`；用户入口是 `启动 ConvenientFileTP.cmd` 或桌面快捷方式。
- 机器离线，不允许 NuGet 包。只用 .NET Framework 自带程序集（已引用 System.Web.Extensions / System.Drawing / System.Windows.Forms）。
- PowerShell 5.1 把无 BOM 的脚本文本当作 ANSI：**所有 .ps1 文件只用 ASCII 字符**，中文只出现在 C# / XAML / Markdown / JSON 中。
- 构建：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1`（内部串行化，可并发调用）。
- 临时运行数据放 `.scratch\<工作流名>\`，截图放 `artifacts\<工作流名>-*.png`。

## 组合根 `ConvenientFileTP.Core.AppServices`（Lead 维护）

```csharp
public static class AppServices
{
    public static AppConfig Config { get; }
    public static DeviceIdentity Identity { get; }
    public static DiscoveryService Discovery { get; }
    public static TransferManager Transfers { get; }
    public static MainWindow Main { get; internal set; }
    public static FloatingWidget Floating { get; internal set; }
    public static TrayIcon Tray { get; internal set; }
    public static bool IsExiting { get; }
    public static string DownloadDirectory { get; }

    public static void Initialize();
    public static void Shutdown();
    public static void ShowMainWindow();
    public static void SelectPeer(PeerInfo peer);       // 选中设备：显示浮窗 + 隐藏主窗
    public static void CreateFloatingWidget(PeerInfo peer);
    public static void ToggleFloatingWindow();
    public static void OpenDownloadDirectory();
    public static void SaveConfig();
    public static void RenameDevice(string name);          // 设置页改设备名（即时生效）
    public static void SetDownloadDirectory(string dir);   // 设置页改下载目录（即时生效）
    public static void Exit();
}
```

`SelectPeer(null)` / `peer.IsSelf` 会被忽略。所有 UI 方法都必须在 WPF Dispatcher 线程调用。

## 主窗口 `ConvenientFileTP.UI.MainWindow`（UI-Main 工作流维护）

```csharp
public partial class MainWindow : Window
{
    public MainWindow();                    // 无参构造，InitializeComponent 后可用
    public void RefreshPeers();             // 根据 AppServices.Discovery.Peers 重建设备列表（UI 线程）
    public void ShowAndActivate();          // 显示/还原/置前
    public void ShowToast(string title, string message); // 窗口内浮层提示，2.5s 自动消失
}
```

- 现有 `OnClosing`：`!AppServices.IsExiting && Config.CloseToTray` 时取消关闭并 `Hide()`；必须保留该行为。
- 设备卡片点击调用 `AppServices.SelectPeer(peer)`；本机卡片不可选择。
- 可自行订阅 `AppServices.Transfers.Progress/Completed` 填充"传输记录"页（UI 线程调度）。
- 主题资源在 `UI\Theme.xaml`，XAML 中直接 `{StaticResource BgDeep}` 等使用。
- 不得修改 `App.xaml` / `AppEntry.cs` / `AppServices.cs` / 其它工作流文件。

## 悬浮窗 `ConvenientFileTP.UI.FloatingWidget`（UI-Float 工作流维护）

```csharp
public partial class FloatingWidget : Window
{
    public FloatingWidget();
    public PeerInfo Target { get; }                        // 当前发送目标
    public void BindPeer(PeerInfo peer);                   // 绑定/切换目标
    public void UpdatePeer(PeerInfo peer);                 // 发现服务刷新目标信息
    public void OnTransferProgress(TransferProgress p);    // UI 线程
    public void OnTransferCompleted(TransferCompletedEventArgs e); // UI 线程
    public void ShowWidget();
    public void HideWidget();
}
```

交互要求（用户需求）：

- 初始为圆形浮窗（默认 96 DIP，可随 `Config.WidgetScale` 缩放），无边框、透明、置顶、不占任务栏、不抢焦点。
- 拖动可移动；移动后靠近屏幕任意边缘（阈值约 24 DIP）自动吸附到该边缘，并收起为**细长小条**（厚度约 10 DIP，长度约 140 DIP），小条有 1px 描边、常态中心为紫色。
- 鼠标悬停在小条上时展开为圆形球（可显示目标名 / 进度百分比 / 状态）。
- 文件拖入浮窗（`AllowDrop`，`DataFormats.FileDrop`）→ 调用 `AppServices.Transfers.SendFilesAsync(Target, paths, CancellationToken.None)`；无目标/目标是自己时提示并打开主窗口。拖动悬停时高亮。
- 状态颜色：空闲紫 `Accent`；发送中红 `Danger`；接收中绿 `Success`；接收完成且有未查看文件时黄 `Warning`。
- 点击球：恰好 1 个未查看文件→用默认程序打开该文件；多个未查看→打开下载目录；无未查看→`AppServices.ShowMainWindow()`。打开后清除"未查看"黄色状态。
- 接收开始/进度由 `AppServices` 调用 `OnTransferProgress`（`Direction==Receive` 时绿色，`Send` 时红色），实时百分比进度（环形或纵向填充均可）。
- 接收成功完成：记录 `e.SavedFiles` 为未查看；状态转黄。发送完成：恢复紫色。
- 右键菜单：打开下载目录 / 显示主窗口 / 隐藏浮窗 / 退出程序。
- 位置/边缘/比例建议持久化到 `AppServices.Config.WidgetEdge`、`WidgetOffsetRatio`（调用 `AppServices.SaveConfig()`）。
- 不得修改 `AppServices.cs`、`MainWindow.*`、`TransferManager.cs`、`App.xaml`。

## 传输引擎 `ConvenientFileTP.Core.TransferManager`（NET 工作流维护）

```csharp
public sealed class TransferManager : IDisposable
{
    public TransferManager(DeviceIdentity identity, AppConfig config);
    public event EventHandler<TransferProgress> Progress;            // 任意线程
    public event EventHandler<TransferCompletedEventArgs> Completed; // 任意线程
    public int ActualListenPort { get; }   // Start 成功后为实际监听端口
    public bool IsRunning { get; }

    public void Start();                   // 启动 TCP 接收服务，端口冲突自动 +1 重试
    public void Stop();
    public Task<string[]> SendFilesAsync(PeerInfo peer, IReadOnlyList<string> paths, CancellationToken ct);
    public void Dispose();
}
```

- 必须实现 `docs\protocol.md` 定义的 v1 协议；接收端自动接受（`Config.AutoAcceptFiles=true` 默认）。
- 接收文件保存到 `AppServices.DownloadDirectory`，用 `Protocol.SafeFileName` + `Protocol.MakeUniquePath` 防覆盖、防路径穿越。
- `Progress` 至少每 100ms 一次，字段填全：`SessionId, Direction, Status, Peer, CurrentFileName, FileIndex, FileCount, TransferredBytes, TotalBytes, BytesPerSecond, StartedUtc`。
- `Completed` 的 `SavedFiles`：接收方为本地保存路径；发送方为远端保存路径（拿不到时用源文件名）。失败填 `Error`。
- 发送端不得因为 UI 未订阅而丢异常；所有失败以 `Completed(Success=false)` 上报并写日志。
- 测试钩子：`--auto-send <file1;file2>` 触发发送、`--auto-exit <secs>` 退出，已由 AppServices 实现。

## 数据模型（Lead 维护，只读）

`PeerInfo`：`Id, Name, Address, Port, IsSelf, Mode, InterfaceName, Platform, Version, LastSeenUtc`，计算属性 `IsOnline, AddressText, ModeText, DisplayName`，方法 `Clone()`、`CopyFrom()`。

`TransferProgress : EventArgs`：上列字段 + `Error`、`StartedUtc`，计算属性 `Percent`（0-100）、`SpeedText`、`SizeText`。

`TransferCompletedEventArgs`：`SessionId, Direction, Peer, Success, Error, string[] SavedFiles, TotalBytes, StartedUtc, FinishedUtc`。

`AppConfig`：`DeviceId, DeviceName, DownloadDirectory, DiscoveryPort(47731), TcpPort(47732), AutoAcceptFiles, ShowNotifications, AutoStart, CloseToTray, WidgetScale, WidgetEdge, WidgetOffsetRatio`；`Save()`。

`DiscoveryService.Peers` 已包含本机（`IsSelf=true`，排第一），`PeersChanged` 事件可能在后台线程触发。

## 主题资源键（UI\Theme.xaml 已存在）

画刷：`BgDeep, BgPanel, BgCard, BgCardHover, BgInput, Border, BorderSoft, TextPrimary, TextSecondary, TextMuted, Accent, AccentLight, AccentDark, AccentSoft, Success, Danger, Warning`。

样式：`AppTitleText, SectionTitleText, BodyText, HintText, TinyText, BaseButton, PrimaryButton, SecondaryButton, GhostButton, DangerButton, IconButton, InputBox, SwitchToggle, CardBorder, PeerListBox, PeerListBoxItem, ProgressBar, ScrollBar`。

## 并行协作规则

- 严格只改自己任务写作用域内的文件；需要改契约/其他文件时先给 Lead 发消息。
- 构建用 `tools\build.ps1`（带全局互斥锁）；跑实例用唯一 `--profile <工作流名>`，避免单实例互斥与配置冲突。
- 完成后：`team_task_update` 完成共享任务，并给 Lead 发一条包含"改了什么/怎么验证/遗留问题"的消息。
