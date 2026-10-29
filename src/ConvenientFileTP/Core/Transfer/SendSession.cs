using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ConvenientFileTP.Core.Transfer
{
    /// <summary>
    /// One outbound transfer (connect -&gt; offer -&gt; files -&gt; done). Never throws: every failure is
    /// logged and reported through the manager's Completed event before SendFilesAsync returns.
    /// </summary>
    internal sealed class SendSession
    {
        private readonly TransferManager _manager;
        private readonly PeerInfo _peer;
        private readonly IReadOnlyList<string> _paths;
        private readonly CancellationToken _cancellation;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly SpeedMeter _speed = new SpeedMeter();
        private readonly ProgressThrottle _throttle = new ProgressThrottle();
        private readonly byte[] _buffer = new byte[TransferLimits.BufferSize];
        private TransferConnection _connection;
        private string _sessionId = "";
        private long _totalBytes;
        private long _transferredBytes;
        private string _currentFileName = "";
        private int _currentIndex = -1;
        private int _fileCount;
        private bool _completedRaised;

        public SendSession(TransferManager manager, PeerInfo peer, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            _manager = manager;
            _peer = peer != null ? peer.Clone() : new PeerInfo();
            _paths = paths;
            _cancellation = cancellationToken;
        }

        public Task<string[]> ExecuteAsync()
        {
            return Task.Run(new Func<string[]>(Run));
        }

        private string[] Run()
        {
            try
            {
                return RunCore();
            }
            catch (Exception ex)
            {
                bool canceled = ex is OperationCanceledException || _cancellation.IsCancellationRequested || _manager.IsStopping;
                string error = canceled
                    ? (_manager.IsStopping && !_cancellation.IsCancellationRequested ? "传输已停止" : "传输已取消")
                    : TransferErrors.Describe(ex);
                TrySendError(error);
                if (canceled)
                {
                    Log.Info("SESSION CANCELED session=" + _sessionId + " dir=Send");
                    Report(TransferStatus.Canceled, error, true);
                }
                else
                {
                    Log.Warn("SESSION FAILED session=" + _sessionId + " dir=Send error=" + error);
                    if (!(ex is TransferException)) Log.Error("发送文件失败 session=" + _sessionId, ex);
                    Report(TransferStatus.Failed, error, true);
                }
                Complete(false, error, new string[0]);
                return new string[0];
            }
            finally
            {
                if (_connection != null)
                {
                    _manager.UnregisterConnection(_connection);
                    _connection.Close();
                }
            }
        }

        private string[] RunCore()
        {
            List<FileInfo> files = CollectFiles();
            _fileCount = files.Count;
            _totalBytes = 0;
            foreach (FileInfo info in files) _totalBytes += info.Length;

            string host = _peer != null ? (_peer.Address ?? "").Trim() : "";
            int port = _peer != null && _peer.Port > 0
                ? _peer.Port
                : (_manager.Config != null && _manager.Config.TcpPort > 0 ? _manager.Config.TcpPort : Protocol.DefaultTcpPort);

            _sessionId = Guid.NewGuid().ToString("N");
            Report(TransferStatus.Preparing, null, true);
            if (files.Count == 0) throw new TransferException("没有可发送的文件");
            if (string.IsNullOrEmpty(host)) throw new TransferException("目标设备没有可用的网络地址");

            Log.Info("SESSION START session=" + _sessionId + " dir=Send peer=" + DescribePeer() +
                     " host=" + host + ":" + port + " files=" + files.Count + " total=" + _totalBytes);

            _connection = Connect(host, port);
            _manager.RegisterConnection(_connection);

            var offerFiles = new List<WireFileEntry>();
            foreach (FileInfo info in files)
            {
                offerFiles.Add(new WireFileEntry { Name = info.Name, Size = info.Length });
            }
            _connection.WriteLine(Wire.Offer(_sessionId,
                _manager.Identity != null ? _manager.Identity.Id : "",
                _manager.Identity != null ? _manager.Identity.Name : "",
                offerFiles));
            Report(TransferStatus.Waiting, null, true);

            WireMessage accept = ReadControl("accept");
            if (accept == null) throw new TransferException("对方没有响应接收请求");
            if (accept.Type == "error")
                throw new TransferException(string.IsNullOrEmpty(accept.Message) ? "对方报告错误" : accept.Message);
            if (!string.Equals(accept.Type, "accept", StringComparison.Ordinal))
                throw new TransferProtocolException("期望 accept 消息，收到 " + (accept.Type ?? "?"));
            if (!accept.Accepted)
                throw new TransferException("对方拒绝接收：" + (string.IsNullOrEmpty(accept.Reason) ? "未说明原因" : accept.Reason));

            _connection.SetReceiveTimeout(TransferLimits.IdleTimeoutMs);
            Log.Info("SESSION ACCEPTED session=" + _sessionId + " dir=Send");
            Report(TransferStatus.InProgress, null, true);

            var remoteSaved = new string[files.Count];
            for (int index = 0; index < files.Count; index++)
            {
                ThrowIfCanceled();
                FileInfo info = files[index];
                _currentIndex = index;
                _currentFileName = info.Name;

                using (var source = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    TransferLimits.BufferSize, FileOptions.SequentialScan))
                {
                    long length = source.Length;
                    if (length != info.Length)
                        throw new TransferException("文件在传输过程中被修改：" + info.Name);
                    _connection.WriteLine(Wire.FileHeader(_sessionId, index, info.Name, length));
                    CopyFile(source, info.Name, length);
                }

                WireMessage result = ReadControl("file-result");
                if (result == null) throw new TransferException("对方在接收文件后断开连接");
                if (result.Type == "error")
                    throw new TransferException(string.IsNullOrEmpty(result.Message) ? "对方报告错误" : result.Message);
                if (!string.Equals(result.Type, "file-result", StringComparison.Ordinal))
                    throw new TransferProtocolException("期望 file-result 消息，收到 " + (result.Type ?? "?"));
                if (result.Index != index)
                    throw new TransferProtocolException("文件结果序号不匹配：" + result.Index + "，期望 " + index);
                if (!result.Ok)
                    throw new TransferException("对方保存文件失败：" + (string.IsNullOrEmpty(result.Error) ? "未知原因" : result.Error));

                // Contract: SavedFiles carries the remote saved paths; if the receiver did not
                // report one, fall back to the local source file name so the UI still has a label.
                remoteSaved[index] = !string.IsNullOrEmpty(result.Saved) ? result.Saved : info.Name;
                Log.Info("FILE SENT session=" + _sessionId + " index=" + index + " name=" + info.Name +
                         " size=" + info.Length + " saved=" + (remoteSaved[index].Length > 0 ? remoteSaved[index] : "?"));
                Report(TransferStatus.InProgress, null, true);
            }

            ThrowIfCanceled();
            _connection.WriteLine(Wire.Done(_sessionId, true));
            WireMessage done = ReadControl("done");
            if (done == null) throw new TransferException("对方未确认传输完成就断开连接");
            if (done.Type == "error")
                throw new TransferException(string.IsNullOrEmpty(done.Message) ? "对方报告错误" : done.Message);
            if (!string.Equals(done.Type, "done", StringComparison.Ordinal))
                throw new TransferProtocolException("期望 done 消息，收到 " + (done.Type ?? "?"));
            if (!done.Ok) throw new TransferException("对方报告传输失败");

            Report(TransferStatus.Completed, null, true);
            Log.Info("SENDER DONE session=" + _sessionId + " dir=Send files=" + files.Count +
                     " bytes=" + _transferredBytes + " elapsed=" + (DateTime.UtcNow - _startedUtc).TotalSeconds.ToString("0.0") + "s");
            Complete(true, null, remoteSaved);
            return remoteSaved;
        }

        private List<FileInfo> CollectFiles()
        {
            var files = new List<FileInfo>();
            if (_paths == null) return files;
            foreach (string path in _paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    var info = new FileInfo(Path.GetFullPath(path));
                    if (info.Exists && info.Length >= 0) files.Add(info);
                    else Log.Warn("跳过不存在的文件：" + path);
                }
                catch (Exception ex)
                {
                    Log.Warn("跳过无法读取的文件：" + path + " - " + ex.Message);
                }
            }
            return files;
        }

        private TransferConnection Connect(string host, int port)
        {
            var client = new TcpClient();
            try
            {
                client.NoDelay = true;
                IAsyncResult async = client.BeginConnect(host, port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(TransferLimits.ConnectTimeoutMs))
                {
                    try { client.Close(); } catch { }
                    throw new TransferException("连接超时：对方未在 " + (TransferLimits.ConnectTimeoutMs / 1000) + " 秒内响应");
                }
                client.EndConnect(async);
            }
            catch (TransferException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { client.Close(); } catch { }
                throw new TransferException("无法连接到 " + host + ":" + port + " - " + TransferErrors.Describe(ex), ex);
            }

            var connection = new TransferConnection(client);
            connection.SetSendTimeout(TransferLimits.IdleTimeoutMs);
            connection.SetReceiveTimeout(TransferLimits.HandshakeTimeoutMs);
            return connection;
        }

        private void CopyFile(FileStream source, string name, long length)
        {
            long remaining = length;
            while (remaining > 0)
            {
                ThrowIfCanceled();
                // Protocol v1: write the payload in 128 KiB chunks using a 64 KiB buffer.
                int chunkRemaining = (int)Math.Min(TransferLimits.ChunkSize, remaining);
                while (chunkRemaining > 0)
                {
                    int want = Math.Min(_buffer.Length, chunkRemaining);
                    int read = source.Read(_buffer, 0, want);
                    if (read <= 0) throw new TransferException("读取文件失败：" + name);
                    _connection.Write(_buffer, 0, read);
                    chunkRemaining -= read;
                    remaining -= read;
                    _transferredBytes += read;
                    Report(TransferStatus.InProgress, null, false);
                }
            }
        }

        private WireMessage ReadControl(string expectedType)
        {
            string line = _connection.ReadLine();
            if (line == null) return null;
            WireMessage message = WireMessage.Parse(line);
            if (message == null)
                throw new TransferProtocolException("无法解析对端控制消息（期望 " + expectedType + "）");
            if (!string.IsNullOrEmpty(message.Session) && !string.Equals(message.Session, _sessionId, StringComparison.Ordinal))
                throw new TransferProtocolException("会话标识不匹配");
            return message;
        }

        private void ThrowIfCanceled()
        {
            if (_cancellation.IsCancellationRequested) throw new OperationCanceledException(_cancellation);
            if (_manager.IsStopping) throw new TransferException("传输已停止");
        }

        private void TrySendError(string message)
        {
            if (_connection == null || string.IsNullOrEmpty(_sessionId)) return;
            try { _connection.WriteLine(Wire.Error(_sessionId, message)); } catch { }
        }

        private void Report(TransferStatus status, string error, bool force)
        {
            if (!force && !_throttle.ShouldReport()) return;
            _throttle.Mark();
            var progress = new TransferProgress
            {
                SessionId = _sessionId,
                Direction = TransferDirection.Send,
                Status = status,
                Peer = _peer,
                CurrentFileName = _currentFileName,
                FileIndex = _currentFileName.Length > 0 ? _currentIndex + 1 : 0,
                FileCount = _fileCount,
                TransferredBytes = _transferredBytes,
                TotalBytes = _totalBytes,
                BytesPerSecond = _speed.Update(_transferredBytes),
                Error = error,
                StartedUtc = _startedUtc
            };
            _manager.RaiseProgress(progress);
        }

        private void Complete(bool success, string error, string[] savedFiles)
        {
            if (_completedRaised) return;
            _completedRaised = true;
            _manager.RaiseCompleted(new TransferCompletedEventArgs
            {
                SessionId = _sessionId,
                Direction = TransferDirection.Send,
                Peer = _peer,
                Success = success,
                Error = error,
                SavedFiles = savedFiles ?? new string[0],
                TotalBytes = _totalBytes,
                StartedUtc = _startedUtc,
                FinishedUtc = DateTime.UtcNow
            });
        }

        private string DescribePeer()
        {
            if (_peer == null) return "?";
            return _peer.DisplayName + "@" + (string.IsNullOrEmpty(_peer.Address) ? "?" : _peer.AddressText);
        }
    }
}
