using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using ConvenientFileTP.Core;

namespace ConvenientFileTP.UI
{
    /// <summary>设备页“其他设备”列表的一行。</summary>
    public sealed class PeerRow : INotifyPropertyChanged
    {
        public PeerRow(PeerInfo peer)
        {
            Peer = peer;
        }

        public PeerInfo Peer { get; private set; }

        public string DisplayName
        {
            get { return Peer != null ? Peer.DisplayName : ""; }
        }

        public string Initial
        {
            get
            {
                string name = DisplayName;
                if (string.IsNullOrEmpty(name)) return "?";
                name = name.Trim();
                return name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
            }
        }

        public string AddressText
        {
            get { return Peer != null ? Peer.AddressText : ""; }
        }

        public string ModeText
        {
            get { return Peer != null ? Peer.ModeText : "未知网络"; }
        }

        public bool IsSelf
        {
            get { return Peer != null && Peer.IsSelf; }
        }

        public bool IsOnline
        {
            get { return Peer != null && Peer.IsOnline; }
        }

        public Visibility SelectVisibility
        {
            get { return IsSelf ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string StatusText
        {
            get
            {
                if (IsSelf) return "本机";
                return IsOnline ? "在线" : "离线";
            }
        }

        public string LastSeenText
        {
            get
            {
                if (Peer == null || IsSelf) return "";
                double seconds = (DateTime.UtcNow - Peer.LastSeenUtc).TotalSeconds;
                if (seconds < 5) return "最后在线 刚刚";
                if (seconds < 60) return "最后在线 " + (int)seconds + " 秒前";
                if (seconds < 3600) return "最后在线 " + (int)(seconds / 60) + " 分钟前";
                return "最后在线 " + Peer.LastSeenUtc.ToLocalTime().ToString("MM-dd HH:mm");
            }
        }

        /// <summary>在线状态 / 最后在线时间等相对信息变化后调用。</summary>
        public void Refresh()
        {
            Raise("DisplayName");
            Raise("Initial");
            Raise("AddressText");
            Raise("ModeText");
            Raise("IsSelf");
            Raise("IsOnline");
            Raise("StatusText");
            Raise("LastSeenText");
            Raise("SelectVisibility");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>传输记录页的一行，对应一次传输会话。</summary>
    public sealed class TransferRow : INotifyPropertyChanged
    {
        private TransferRow()
        {
            StatusKind = "muted";
            StatusText = "等待中";
            PercentText = "0%";
            SpeedText = "";
            FileName = "";
            PeerText = "";
            TimeText = "";
        }

        public TransferRow(TransferProgress progress) : this()
        {
            Update(progress);
        }

        public TransferRow(TransferCompletedEventArgs args) : this()
        {
            Complete(args);
        }

        public string Key { get; set; }
        public string SessionId { get; private set; }
        public TransferDirection Direction { get; private set; }
        public bool IsSend { get { return Direction == TransferDirection.Send; } }
        public string DirectionText { get { return IsSend ? "发送" : "接收"; } }
        public string PeerText { get; private set; }
        public string FileName { get; private set; }
        public double Percent { get; private set; }
        public string PercentText { get; private set; }
        public string SpeedText { get; private set; }
        public string StatusText { get; private set; }
        public string StatusKind { get; private set; }
        public string TimeText { get; private set; }
        public string[] SavedFiles { get; private set; }
        public bool Success { get; private set; }

        public bool CanOpen
        {
            get
            {
                return Direction == TransferDirection.Receive && Success &&
                       SavedFiles != null && SavedFiles.Length > 0;
            }
        }

        public void Update(TransferProgress progress)
        {
            if (progress == null) return;
            SessionId = progress.SessionId;
            Direction = progress.Direction;
            PeerText = DescribePeer(progress.Peer);

            string fileName = progress.CurrentFileName;
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = progress.Status == TransferStatus.Preparing ? "（准备中…）" : "（等待文件名）";
            }
            else
            {
                fileName = SafeFileName(fileName);
                if (progress.FileCount > 1)
                {
                    int index = progress.FileIndex + 1;
                    if (index < 1) index = 1;
                    if (index > progress.FileCount) index = progress.FileCount;
                    fileName = fileName + "（" + index + "/" + progress.FileCount + "）";
                }
            }
            FileName = fileName;

            Percent = Math.Max(0, Math.Min(100, progress.Percent));
            PercentText = ((int)Math.Round(Percent)) + "%";
            SpeedText = progress.Status == TransferStatus.InProgress ? progress.SpeedText : "";
            StatusText = DescribeStatus(progress.Status);
            StatusKind = KindOf(progress.Status);
            TimeText = progress.StartedUtc == DateTime.MinValue
                ? ""
                : progress.StartedUtc.ToLocalTime().ToString("HH:mm:ss");
            RaiseAll();
        }

        public void Complete(TransferCompletedEventArgs args)
        {
            if (args == null) return;
            SessionId = args.SessionId;
            Direction = args.Direction;
            PeerText = DescribePeer(args.Peer);
            SavedFiles = args.SavedFiles;
            Success = args.Success;

            if (args.Success)
            {
                Percent = 100;
                StatusText = "已完成";
                StatusKind = "success";
            }
            else
            {
                StatusText = "失败";
                StatusKind = "fail";
            }
            PercentText = ((int)Math.Round(Percent)) + "%";
            SpeedText = args.TotalBytes > 0 ? FormatUtil.Bytes(args.TotalBytes) : "";

            string first = null;
            if (args.SavedFiles != null && args.SavedFiles.Length > 0) first = SafeFileName(args.SavedFiles[0]);
            if (!string.IsNullOrEmpty(first))
            {
                FileName = args.SavedFiles.Length > 1
                    ? first + " 等 " + args.SavedFiles.Length + " 个文件"
                    : first;
            }
            if (string.IsNullOrEmpty(FileName)) FileName = "（未知文件）";

            TimeText = args.FinishedUtc == DateTime.MinValue
                ? ""
                : args.FinishedUtc.ToLocalTime().ToString("HH:mm:ss");
            RaiseAll();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private static string DescribePeer(PeerInfo peer)
        {
            if (peer == null) return "未知设备";
            string name = peer.DisplayName;
            return string.IsNullOrEmpty(name) ? "未知设备" : name;
        }

        private static string DescribeStatus(TransferStatus status)
        {
            switch (status)
            {
                case TransferStatus.Preparing: return "准备中";
                case TransferStatus.Waiting: return "等待对方";
                case TransferStatus.InProgress: return "传输中";
                case TransferStatus.Completed: return "已完成";
                case TransferStatus.Failed: return "失败";
                case TransferStatus.Canceled: return "已取消";
                default: return "进行中";
            }
        }

        private static string KindOf(TransferStatus status)
        {
            switch (status)
            {
                case TransferStatus.InProgress: return "progress";
                case TransferStatus.Completed: return "success";
                case TransferStatus.Failed: return "fail";
                default: return "muted";
            }
        }

        private static string SafeFileName(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "";
                string name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch
            {
                return path ?? "";
            }
        }

        private void RaiseAll()
        {
            Raise("SessionId");
            Raise("Direction");
            Raise("IsSend");
            Raise("DirectionText");
            Raise("PeerText");
            Raise("FileName");
            Raise("Percent");
            Raise("PercentText");
            Raise("SpeedText");
            Raise("StatusText");
            Raise("StatusKind");
            Raise("TimeText");
            Raise("SavedFiles");
            Raise("Success");
            Raise("CanOpen");
        }

        private void Raise(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
