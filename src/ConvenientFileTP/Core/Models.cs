using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ConvenientFileTP.Core
{
    public enum NetworkMode
    {
        Unknown = 0,
        Lan = 1,
        HotspotHost = 2,
        HotspotClient = 3,
        Loopback = 4
    }

    public enum TransferDirection
    {
        Send = 0,
        Receive = 1
    }

    public enum TransferStatus
    {
        Preparing = 0,
        Waiting = 1,
        InProgress = 2,
        Completed = 3,
        Failed = 4,
        Canceled = 5
    }

    public sealed class DeviceIdentity
    {
        public DeviceIdentity(string id, string name)
        {
            Id = id ?? "";
            Name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        }

        public string Id { get; private set; }
        public string Name { get; set; }
    }

    /// <summary>A device visible on the local network (or this machine).</summary>
    public sealed class PeerInfo : INotifyPropertyChanged
    {
        private string _id = "";
        private string _name = "";
        private string _address = "";
        private int _port;
        private bool _isSelf;
        private DateTime _lastSeenUtc = DateTime.UtcNow;
        private NetworkMode _mode = NetworkMode.Unknown;
        private string _modeDetail = "";
        private string _interfaceName = "";
        private string _platform = "windows";
        private string _version = "";

        public string Id { get { return _id; } set { Set(ref _id, value); } }
        public string Name { get { return _name; } set { Set(ref _name, value); } }
        public string Address { get { return _address; } set { Set(ref _address, value); } }
        public int Port { get { return _port; } set { Set(ref _port, value); } }
        public bool IsSelf { get { return _isSelf; } set { Set(ref _isSelf, value); } }
        public DateTime LastSeenUtc { get { return _lastSeenUtc; } set { Set(ref _lastSeenUtc, value); } }
        public NetworkMode Mode { get { return _mode; } set { Set(ref _mode, value); } }
        /// <summary>Optional richer description (e.g. "局域网 + 本机热点") for the local machine.</summary>
        public string ModeDetail { get { return _modeDetail; } set { Set(ref _modeDetail, value); } }
        public string InterfaceName { get { return _interfaceName; } set { Set(ref _interfaceName, value); } }
        public string Platform { get { return _platform; } set { Set(ref _platform, value); } }
        public string Version { get { return _version; } set { Set(ref _version, value); } }

        public bool IsOnline
        {
            get
            {
                if (IsSelf) return true;
                return (DateTime.UtcNow - LastSeenUtc).TotalSeconds <= 15;
            }
        }

        public string AddressText
        {
            get
            {
                if (string.IsNullOrEmpty(Address)) return "";
                if (Port <= 0) return Address;
                return Address + ":" + Port;
            }
        }

        public string ModeText
        {
            get
            {
                if (!string.IsNullOrEmpty(ModeDetail)) return ModeDetail;
                switch (Mode)
                {
                    case NetworkMode.Lan: return "局域网";
                    case NetworkMode.HotspotHost: return "本机热点";
                    case NetworkMode.HotspotClient: return "热点";
                    case NetworkMode.Loopback: return "本机";
                    default: return "未知网络";
                }
            }
        }

        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return Address;
                return Name;
            }
        }

        public PeerInfo Clone()
        {
            return new PeerInfo
            {
                Id = Id,
                Name = Name,
                Address = Address,
                Port = Port,
                IsSelf = IsSelf,
                LastSeenUtc = LastSeenUtc,
                Mode = Mode,
                ModeDetail = ModeDetail,
                InterfaceName = InterfaceName,
                Platform = Platform,
                Version = Version
            };
        }

        public void CopyFrom(PeerInfo other)
        {
            if (other == null) return;
            Id = other.Id;
            Name = other.Name;
            Address = other.Address;
            Port = other.Port;
            IsSelf = other.IsSelf;
            LastSeenUtc = other.LastSeenUtc;
            Mode = other.Mode;
            ModeDetail = other.ModeDetail;
            InterfaceName = other.InterfaceName;
            Platform = other.Platform;
            Version = other.Version;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName == "LastSeenUtc" || propertyName == "Address" || propertyName == "Port")
            {
                handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs("IsOnline"));
                if (handler != null) handler(this, new PropertyChangedEventArgs("AddressText"));
            }
        }
    }

    /// <summary>A local IPv4 interface that participates in discovery.</summary>
    public sealed class LocalInterfaceInfo
    {
        public string InterfaceName { get; set; }
        public string Address { get; set; }
        public string Mask { get; set; }
        public string Gateway { get; set; }
        public NetworkMode Mode { get; set; }
        public string Ssid { get; set; }

        public override string ToString()
        {
            return InterfaceName + " " + Address + " (" + Mode + ")";
        }
    }

    public sealed class TransferProgress : EventArgs
    {
        public string SessionId { get; set; }
        public TransferDirection Direction { get; set; }
        public TransferStatus Status { get; set; }
        public PeerInfo Peer { get; set; }
        public string CurrentFileName { get; set; }
        public int FileIndex { get; set; }
        public int FileCount { get; set; }
        public long TransferredBytes { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public string Error { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        public double Percent
        {
            get
            {
                if (TotalBytes <= 0) return 0;
                return Math.Max(0, Math.Min(100, 100.0 * TransferredBytes / TotalBytes));
            }
        }

        public string SpeedText
        {
            get { return FormatUtil.Bytes((long)Math.Max(0, BytesPerSecond)) + "/s"; }
        }

        public string SizeText
        {
            get { return FormatUtil.Bytes(TransferredBytes) + " / " + FormatUtil.Bytes(TotalBytes); }
        }

        public TransferProgress Clone()
        {
            return (TransferProgress)MemberwiseClone();
        }
    }

    public sealed class TransferCompletedEventArgs : EventArgs
    {
        public string SessionId { get; set; }
        public TransferDirection Direction { get; set; }
        public PeerInfo Peer { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public string[] SavedFiles { get; set; } = new string[0];
        public long TotalBytes { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime FinishedUtc { get; set; } = DateTime.UtcNow;
    }

    public static class FormatUtil
    {
        public static string Bytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            if (unit == 0) return bytes + " B";
            return value.ToString(value >= 100 ? "0" : "0.0") + " " + units[unit];
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return "不到 1 秒";
            if (span.TotalMinutes < 1) return span.Seconds + " 秒";
            if (span.TotalHours < 1) return span.Minutes + " 分 " + span.Seconds + " 秒";
            return span.Hours + " 时 " + span.Minutes + " 分";
        }

        public static string FileName(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "";
                return Path.GetFileName(path);
            }
            catch
            {
                return path ?? "";
            }
        }
    }
}
