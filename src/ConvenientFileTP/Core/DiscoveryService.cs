using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ConvenientFileTP.Core
{
    /// <summary>
    /// UDP multicast + broadcast presence announcements. Peers that run this tool are listed
    /// with their transfer port; the local machine is always present as the first entry.
    /// </summary>
    public sealed class DiscoveryService : IDisposable
    {
        private readonly DeviceIdentity _identity;
        private readonly int _discoveryPort;
        private readonly object _gate = new object();
        private readonly Dictionary<string, PeerInfo> _peers = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);

        private Socket _socket;
        private Thread _receiveThread;
        private Thread _announceThread;
        private volatile bool _running;
        private List<LocalInterfaceInfo> _interfaces = new List<LocalInterfaceInfo>();
        private int _tcpPort;
        private DateTime _lastPeerRemoval = DateTime.UtcNow;

        public DiscoveryService(DeviceIdentity identity, int discoveryPort)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            _identity = identity;
            _discoveryPort = discoveryPort;
        }

        public event EventHandler PeersChanged;

        public bool IsRunning { get { return _running; } }
        public string LastError { get; private set; }
        public PeerInfo Self { get; private set; }
        public IReadOnlyList<LocalInterfaceInfo> LocalInterfaces { get { return _interfaces; } }

        public IReadOnlyList<PeerInfo> Peers
        {
            get
            {
                lock (_gate)
                {
                    return _peers.Values.ToList();
                }
            }
        }

        public PeerInfo FindPeer(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_gate)
            {
                PeerInfo peer;
                return _peers.TryGetValue(id, out peer) ? peer : null;
            }
        }

        public void Start(int tcpPort)
        {
            _tcpPort = tcpPort;
            _interfaces = NetworkUtil.GetLocalInterfaces();
            NetworkMode selfMode = NetworkUtil.GetSelfMode(_interfaces);
            string selfAddress = NetworkUtil.GetPrimaryAddress(_interfaces);
            Self = new PeerInfo
            {
                Id = _identity.Id,
                Name = _identity.Name,
                Address = selfAddress,
                Port = tcpPort,
                IsSelf = true,
                Mode = selfMode,
                ModeDetail = NetworkUtil.GetSelfModeText(_interfaces),
                InterfaceName = _interfaces.Count > 0 ? _interfaces[0].InterfaceName : "",
                Platform = "windows",
                Version = GetVersion(),
                LastSeenUtc = DateTime.UtcNow
            };
            lock (_gate)
            {
                _peers.Clear();
                _peers[Self.Id] = Self;
            }

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.EnableBroadcast = true;
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                _socket.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

                foreach (var iface in _interfaces)
                {
                    try
                    {
                        var local = IPAddress.Parse(iface.Address);
                        if (local.Equals(IPAddress.Loopback) || local.Equals(IPAddress.Any)) continue;
                        _socket.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(NetworkUtil.MulticastAddress, local));
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("加入多播组失败 " + iface.Address + ": " + ex.Message);
                    }
                }

                _running = true;
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Discovery.Receive" };
                _receiveThread.Start();
                _announceThread = new Thread(AnnounceLoop) { IsBackground = true, Name = "Discovery.Announce" };
                _announceThread.Start();
                SendQuery();
                Log.Info("设备发现已启动，端口 " + _discoveryPort + "，本机地址 " + selfAddress + "，传输端口 " + tcpPort);
                RaisePeersChanged();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _running = false;
                Log.Error("设备发现启动失败（将仅显示本机）", ex);
            }
        }

        public void Refresh()
        {
            SendQuery();
            RaisePeersChanged();
        }

        /// <summary>Applies a changed device name to the self entry and announces it.</summary>
        public void UpdateIdentity()
        {
            var self = Self;
            if (self != null)
            {
                self.Name = _identity.Name;
            }
            SendAnnounce();
            RaisePeersChanged();
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[8192];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running && _socket != null)
            {
                try
                {
                    int count = _socket.ReceiveFrom(buffer, ref remote);
                    if (count <= 0) continue;
                    string json = Protocol.Utf8NoBom.GetString(buffer, 0, count);
                    DiscoveryMessage message;
                    if (!Json.TryDeserialize(json, out message) || message == null) continue;
                    if (!string.Equals(message.app, Protocol.AppId, StringComparison.Ordinal)) continue;
                    if (message.v != Protocol.Version) continue;

                    var endpoint = remote as IPEndPoint;
                    string address = endpoint != null ? endpoint.Address.ToString() : "";
                    HandleMessage(message, address);
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Log.Debug("发现套接字异常: " + ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Error("处理发现消息失败", ex);
                }
            }
        }

        private void HandleMessage(DiscoveryMessage message, string address)
        {
            if (message == null || string.IsNullOrEmpty(message.t)) return;
            switch (message.t)
            {
                case "query":
                    if (!string.Equals(message.id, _identity.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        SendAnnounce();
                    }
                    break;
                case "bye":
                    if (string.Equals(message.id, _identity.Id, StringComparison.OrdinalIgnoreCase)) return;
                    RemovePeer(message.id);
                    break;
                case "announce":
                    if (string.Equals(message.id, _identity.Id, StringComparison.OrdinalIgnoreCase)) return;
                    if (string.IsNullOrEmpty(message.id)) return;
                    UpdatePeer(message, address);
                    break;
            }
        }

        private void UpdatePeer(DiscoveryMessage message, string address)
        {
            NetworkMode mode;
            if (!Enum.TryParse(message.mode, true, out mode)) mode = NetworkMode.Unknown;
            if (string.IsNullOrEmpty(address) || address == "0.0.0.0")
            {
                return;
            }
            if (address.StartsWith("127."))
            {
                // A loopback announce is another instance on this machine; keep its address as
                // loopback so connecting works regardless of the actual interface.
                address = "127.0.0.1";
            }

            bool changed = false;
            PeerInfo peer;
            lock (_gate)
            {
                if (!_peers.TryGetValue(message.id, out peer) || peer.IsSelf)
                {
                    peer = new PeerInfo
                    {
                        Id = message.id,
                        IsSelf = false
                    };
                    _peers[message.id] = peer;
                    changed = true;
                }
                DateTime now = DateTime.UtcNow;
                if (peer.LastSeenUtc == default(DateTime) ||
                    (now - peer.LastSeenUtc).TotalSeconds > 15)
                {
                    changed = true;
                }
                peer.Name = string.IsNullOrWhiteSpace(message.name) ? address : message.name.Trim();
                peer.Address = address;
                peer.Port = message.port;
                peer.Mode = mode;
                peer.Platform = message.os ?? "windows";
                peer.Version = message.ver ?? "";
                peer.LastSeenUtc = now;
            }
            if (changed) RaisePeersChanged();
        }

        private void RemovePeer(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            bool removed = false;
            lock (_gate)
            {
                PeerInfo peer;
                if (_peers.TryGetValue(id, out peer) && !peer.IsSelf)
                {
                    removed = _peers.Remove(id);
                }
            }
            if (removed)
            {
                Log.Info("设备离线: " + id);
                RaisePeersChanged();
            }
        }

        private void AnnounceLoop()
        {
            DateTime lastCleanup = DateTime.UtcNow;
            while (_running)
            {
                try
                {
                    SendAnnounce();
                    if ((DateTime.UtcNow - lastCleanup).TotalSeconds >= 5)
                    {
                        lastCleanup = DateTime.UtcNow;
                        CleanupStalePeers();
                    }
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Debug("发送设备公告失败: " + ex.Message);
                }
                for (int i = 0; i < 30 && _running; i++) Thread.Sleep(100);
            }
        }

        private void CleanupStalePeers()
        {
            List<string> stale = new List<string>();
            lock (_gate)
            {
                foreach (var kv in _peers)
                {
                    if (kv.Value.IsSelf) continue;
                    if ((DateTime.UtcNow - kv.Value.LastSeenUtc).TotalSeconds > 20) stale.Add(kv.Key);
                }
                foreach (string id in stale) _peers.Remove(id);
            }
            if (stale.Count > 0)
            {
                Log.Info("清理离线设备 " + stale.Count + " 台");
                RaisePeersChanged();
            }
        }

        private DiscoveryMessage BuildMessage(string type)
        {
            return new DiscoveryMessage
            {
                app = Protocol.AppId,
                v = Protocol.Version,
                t = type,
                id = _identity.Id,
                name = _identity.Name,
                port = _tcpPort,
                mode = Self != null ? Self.Mode.ToString() : NetworkMode.Unknown.ToString(),
                os = "windows",
                ver = GetVersion(),
                ts = TimeUtil.ToMilliseconds(DateTime.UtcNow)
            };
        }

        private void SendQuery()
        {
            SendToAllTargets(BuildMessage("query"));
        }

        private void SendAnnounce()
        {
            SendToAllTargets(BuildMessage("announce"));
        }

        private void SendToAllTargets(DiscoveryMessage message)
        {
            var socket = _socket;
            if (socket == null || !_running) return;
            byte[] data = Protocol.Utf8NoBom.GetBytes(Json.Serialize(message));
            var targets = new List<IPAddress>();
            targets.Add(NetworkUtil.MulticastAddress);
            foreach (var b in NetworkUtil.GetBroadcastAddresses(_interfaces))
            {
                if (!targets.Contains(b)) targets.Add(b);
            }
            foreach (var target in targets)
            {
                try
                {
                    socket.SendTo(data, new IPEndPoint(target, _discoveryPort));
                }
                catch (Exception ex)
                {
                    Log.Debug("发送到 " + target + " 失败: " + ex.Message);
                }
            }
        }

        private void RaisePeersChanged()
        {
            var handler = PeersChanged;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("PeersChanged 处理失败", ex); }
        }

        private static string GetVersion()
        {
            return AppInfo.Version;
        }

        public void Dispose()
        {
            if (!_running && _socket == null) return;
            try
            {
                if (_socket != null && _running)
                {
                    try { SendToAllTargets(BuildMessage("bye")); } catch { }
                }
                _running = false;
                if (_socket != null)
                {
                    try { _socket.Close(); } catch { }
                    _socket = null;
                }
            }
            catch { }
            try { if (_receiveThread != null) _receiveThread.Join(500); } catch { }
            try { if (_announceThread != null) _announceThread.Join(500); } catch { }
            Log.Info("设备发现已停止");
        }
    }
}
