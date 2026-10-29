using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ConvenientFileTP.Core.Transfer;

namespace ConvenientFileTP.Core
{
    /// <summary>
    /// TCP file transfer engine implementing protocol v1 (docs/protocol.md).
    /// The public surface is frozen for the UI layer: Start/Stop, SendFilesAsync, Progress/Completed.
    /// </summary>
    public sealed class TransferManager : IDisposable
    {
        private readonly DeviceIdentity _identity;
        private readonly AppConfig _config;
        private readonly object _sync = new object();
        private readonly HashSet<TransferConnection> _connections = new HashSet<TransferConnection>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private volatile bool _stopping;
        private volatile bool _disposed;
        private int _activeReceives;
        private int _actualPort;

        public TransferManager(DeviceIdentity identity, AppConfig config)
        {
            _identity = identity;
            _config = config;
        }

        /// <summary>Raised on an arbitrary thread whenever transfer state changes.</summary>
        public event EventHandler<TransferProgress> Progress;

        /// <summary>Raised once per session on an arbitrary thread when it finishes (success or failure).</summary>
        public event EventHandler<TransferCompletedEventArgs> Completed;

        /// <summary>Port the listener actually bound to (0 before Start / after a failed Start).</summary>
        public int ActualListenPort
        {
            get { return _actualPort; }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        internal DeviceIdentity Identity
        {
            get { return _identity; }
        }

        internal AppConfig Config
        {
            get { return _config; }
        }

        internal bool IsStopping
        {
            get { return _stopping; }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _running) return;

                int requestedPort = _config != null && _config.TcpPort > 0 && _config.TcpPort <= 65535
                    ? _config.TcpPort
                    : Protocol.DefaultTcpPort;
                TcpListener listener = null;
                int port = requestedPort;
                Exception lastError = null;

                for (int attempt = 0; attempt < 20 && port <= 65535; attempt++)
                {
                    var candidate = new TcpListener(IPAddress.Any, port);
                    try
                    {
                        candidate.Start(TransferLimits.ListenBacklog);
                        listener = candidate;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        try { candidate.Stop(); } catch { }
                        var socket = ex as SocketException;
                        if (socket == null || socket.SocketErrorCode != SocketError.AddressAlreadyInUse)
                        {
                            listener = null;
                            break;
                        }
                        port++;
                    }
                }

                if (listener == null)
                {
                    _running = false;
                    _actualPort = 0;
                    Log.Error("无法启动传输服务：从端口 " + requestedPort + " 起尝试 20 次均失败", lastError);
                    return;
                }

                _listener = listener;
                _running = true;
                _stopping = false;
                try
                {
                    var endpoint = listener.LocalEndpoint as IPEndPoint;
                    _actualPort = endpoint != null ? endpoint.Port : port;
                }
                catch
                {
                    _actualPort = port;
                }
                Log.Info("Transfer server listening on 0.0.0.0:" + _actualPort + " (requested " + requestedPort + ")");

                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "ConvenientFileTP-TransferAccept"
                };
                _acceptThread.Start();
            }
        }

        public void Stop()
        {
            TcpListener listener;
            Thread acceptThread;
            TransferConnection[] connections;

            lock (_sync)
            {
                bool wasStarted = _running || _listener != null;
                _running = false;
                _stopping = true;
                listener = _listener;
                _listener = null;
                acceptThread = _acceptThread;
                _acceptThread = null;
                connections = new TransferConnection[_connections.Count];
                _connections.CopyTo(connections);
                _connections.Clear();
                if (!wasStarted) return;
            }

            if (listener != null)
            {
                try { listener.Stop(); }
                catch (Exception ex) { Log.Warn("关闭监听端口失败：" + ex.Message); }
            }
            if (acceptThread != null && acceptThread != Thread.CurrentThread)
            {
                try
                {
                    if (!acceptThread.Join(2000)) Log.Warn("等待接收线程退出超时");
                }
                catch { }
            }
            foreach (TransferConnection connection in connections)
            {
                try { connection.Close(); } catch { }
            }
            Log.Info("Transfer server stopped");
        }

        public Task<string[]> SendFilesAsync(PeerInfo peer, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            var session = new SendSession(this, peer, paths, cancellationToken);
            return session.ExecuteAsync();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpListener listener = _listener;
                if (listener == null) break;

                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Log.Warn("接收连接失败：" + ex.Message);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Warn("接收连接失败：" + TransferErrors.Describe(ex));
                    continue;
                }

                if (client == null) continue;
                TcpClient accepted = client;
                ThreadPool.QueueUserWorkItem(state => HandleClient(accepted));
            }
        }

        private void HandleClient(TcpClient client)
        {
            TransferConnection connection = null;
            bool receiving = false;
            try
            {
                connection = new TransferConnection(client);
                connection.SetReceiveTimeout(TransferLimits.HandshakeTimeoutMs);
                connection.SetSendTimeout(TransferLimits.IdleTimeoutMs);
                RegisterConnection(connection);

                string line = connection.ReadLine();
                if (line == null) return;

                WireMessage offer = WireMessage.Parse(line);
                if (offer == null)
                {
                    TryReject(connection, "", "无法解析握手消息");
                    return;
                }
                if (!string.Equals(offer.App, Protocol.AppId, StringComparison.Ordinal))
                {
                    TryReject(connection, "", "应用标识不匹配");
                    return;
                }
                if (offer.Version != Protocol.Version)
                {
                    TryReject(connection, "", "不支持的协议版本：" + offer.Version);
                    return;
                }
                if (!string.Equals(offer.Type, "offer", StringComparison.Ordinal))
                {
                    TryReject(connection, "", "期望 offer 握手消息，收到 " + (offer.Type ?? "?"));
                    return;
                }
                if (string.IsNullOrEmpty(offer.Session))
                {
                    TryReject(connection, "", "握手缺少会话标识");
                    return;
                }

                if (!TryBeginReceive())
                {
                    TryReject(connection, offer.Session, "接收端繁忙：并发接收已达上限（" + TransferLimits.MaxConcurrentReceives + "）");
                    Log.Warn("SESSION REJECTED session=" + offer.Session + " reason=busy peer=" + connection.RemoteText);
                    return;
                }
                receiving = true;

                PeerInfo peer = CreatePeer(offer, connection.RemoteAddress);
                Log.Debug("RECV OFFER session=" + offer.Session + " peer=" + (peer != null ? peer.DisplayName : "?") +
                          " remote=" + connection.RemoteText + " files=" + (offer.Files != null ? offer.Files.Count : 0));
                var session = new ReceiveSession(this, connection, offer, peer);
                session.Run();
            }
            catch (Exception ex)
            {
                if (!_stopping)
                {
                    Log.Error("处理接收连接失败", ex);
                    if (connection != null) TryReject(connection, "", TransferErrors.Describe(ex));
                }
            }
            finally
            {
                if (receiving) EndReceive();
                if (connection != null)
                {
                    UnregisterConnection(connection);
                    try { connection.Close(); } catch { }
                }
            }
        }

        private void TryReject(TransferConnection connection, string session, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(session)) connection.WriteLine(Wire.Error("", reason));
                else connection.WriteLine(Wire.Error(session, reason));
            }
            catch { }
        }

        internal void RegisterConnection(TransferConnection connection)
        {
            if (connection == null) return;
            lock (_sync) { _connections.Add(connection); }
        }

        internal void UnregisterConnection(TransferConnection connection)
        {
            if (connection == null) return;
            lock (_sync) { _connections.Remove(connection); }
        }

        internal bool TryBeginReceive()
        {
            lock (_sync)
            {
                if (_activeReceives >= TransferLimits.MaxConcurrentReceives) return false;
                _activeReceives++;
                return true;
            }
        }

        internal void EndReceive()
        {
            lock (_sync)
            {
                if (_activeReceives > 0) _activeReceives--;
            }
        }

        internal string ResolveDownloadDirectory()
        {
            string directory = null;
            try { directory = AppServices.DownloadDirectory; } catch { }
            if (string.IsNullOrWhiteSpace(directory))
            {
                try { directory = AppPaths.DownloadDirectory; } catch { }
            }
            if (string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(userProfile)) directory = Path.Combine(userProfile, "Downloads");
                }
                catch { }
            }
            return directory ?? "";
        }

        internal PeerInfo CreatePeer(WireMessage offer, string remoteAddress)
        {
            string address = remoteAddress ?? "";
            var peer = new PeerInfo
            {
                Id = offer != null ? (offer.FromId ?? "") : "",
                Name = offer != null ? (offer.FromName ?? "") : "",
                Address = address,
                Port = 0,
                Mode = IsLoopbackAddress(address) ? NetworkMode.Loopback : NetworkMode.Lan,
                Platform = "windows",
                Version = "",
                LastSeenUtc = DateTime.UtcNow
            };
            if (string.IsNullOrWhiteSpace(peer.Name))
                peer.Name = string.IsNullOrEmpty(address) ? "未知设备" : address;
            if (_identity != null && !string.IsNullOrEmpty(_identity.Id) &&
                string.Equals(_identity.Id, peer.Id, StringComparison.OrdinalIgnoreCase))
                peer.IsSelf = true;
            return peer;
        }

        internal void RaiseProgress(TransferProgress progress)
        {
            if (progress == null) return;
            Log.Debug("PROGRESS session=" + (progress.SessionId ?? "") + " dir=" + progress.Direction +
                      " status=" + progress.Status + " bytes=" + progress.TransferredBytes + "/" + progress.TotalBytes +
                      " file=" + (progress.CurrentFileName ?? "") + " speed=" + ((long)progress.BytesPerSecond) + "B/s");
            EventHandler<TransferProgress> handler = Progress;
            if (handler == null) return;
            try
            {
                handler(this, progress);
            }
            catch (Exception ex)
            {
                Log.Error("进度事件处理失败", ex);
            }
        }

        internal void RaiseCompleted(TransferCompletedEventArgs completed)
        {
            if (completed == null) return;
            Log.Debug("COMPLETED session=" + (completed.SessionId ?? "") + " dir=" + completed.Direction +
                      " success=" + completed.Success + " files=" + (completed.SavedFiles != null ? completed.SavedFiles.Length : 0) +
                      " bytes=" + completed.TotalBytes + (string.IsNullOrEmpty(completed.Error) ? "" : " error=" + completed.Error));
            EventHandler<TransferCompletedEventArgs> handler = Completed;
            if (handler == null) return;
            try
            {
                handler(this, completed);
            }
            catch (Exception ex)
            {
                Log.Error("完成事件处理失败", ex);
            }
        }

        private static bool IsLoopbackAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            return address.StartsWith("127.", StringComparison.Ordinal) || address == "::1";
        }
    }
}
