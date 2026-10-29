using System;
using System.Collections.Generic;
using System.IO;

namespace ConvenientFileTP.Core.Transfer
{
    /// <summary>
    /// One inbound transfer (offer -&gt; accept -&gt; files -&gt; done). Runs on a thread-pool thread and
    /// never lets an exception escape: every failure is logged and reported through Completed.
    /// </summary>
    internal sealed class ReceiveSession
    {
        private readonly TransferManager _manager;
        private readonly TransferConnection _connection;
        private readonly string _sessionId;
        private readonly PeerInfo _peer;
        private readonly List<WireFileEntry> _files;
        private readonly DateTime _startedUtc;
        private readonly SpeedMeter _speed = new SpeedMeter();
        private readonly ProgressThrottle _throttle = new ProgressThrottle();
        private readonly List<string> _savedFiles = new List<string>();
        private readonly byte[] _buffer = new byte[TransferLimits.BufferSize];
        private long _totalBytes;
        private long _transferredBytes;
        private string _currentFileName = "";
        private int _currentIndex = -1;
        private string _partialPath;
        private bool _completedRaised;

        public ReceiveSession(TransferManager manager, TransferConnection connection, WireMessage offer, PeerInfo peer)
        {
            _manager = manager;
            _connection = connection;
            _sessionId = offer.Session ?? "";
            _peer = peer;
            _startedUtc = DateTime.UtcNow;
            _files = offer.Files ?? new List<WireFileEntry>();
            foreach (WireFileEntry entry in _files)
            {
                if (entry != null && entry.Size > 0) _totalBytes += entry.Size;
            }
        }

        public string SessionId
        {
            get { return _sessionId; }
        }

        public void Run()
        {
            try
            {
                RunCore();
            }
            catch (Exception ex)
            {
                bool stopping = _manager.IsStopping;
                string error = stopping ? "接收已停止" : TransferErrors.Describe(ex);
                DeletePartialFile();
                TrySendError(error);
                if (stopping)
                {
                    Log.Info("SESSION CANCELED session=" + _sessionId + " dir=Receive");
                    Report(TransferStatus.Canceled, error, true);
                }
                else
                {
                    Log.Warn("SESSION FAILED session=" + _sessionId + " dir=Receive error=" + error);
                    if (!(ex is TransferException)) Log.Error("接收文件失败 session=" + _sessionId, ex);
                    Report(TransferStatus.Failed, error, true);
                }
                Complete(false, error);
            }
        }

        private void RunCore()
        {
            Log.Info("SESSION START session=" + _sessionId + " dir=Receive peer=" +
                     DescribePeer(_peer) + " files=" + _files.Count + " total=" + _totalBytes);

            Report(TransferStatus.Preparing, null, true);

            AppConfig config = _manager.Config;
            if (config != null && !config.AutoAcceptFiles)
            {
                _connection.WriteLine(Wire.Accept(_sessionId, false, "接收端已关闭自动接收"));
                Log.Warn("SESSION REJECTED session=" + _sessionId + " reason=auto-accept-off");
                Complete(false, "接收端已关闭自动接收");
                return;
            }

            string downloadRoot;
            try
            {
                downloadRoot = _manager.ResolveDownloadDirectory();
                if (string.IsNullOrEmpty(downloadRoot)) throw new InvalidOperationException("下载目录未配置");
                Directory.CreateDirectory(downloadRoot);
            }
            catch (Exception ex)
            {
                string reason = "无法准备下载目录：" + TransferErrors.Describe(ex);
                _connection.WriteLine(Wire.Accept(_sessionId, false, reason));
                Log.Warn("SESSION REJECTED session=" + _sessionId + " reason=" + reason);
                Complete(false, reason);
                return;
            }

            _connection.WriteLine(Wire.Accept(_sessionId, true, null));
            _connection.SetReceiveTimeout(TransferLimits.IdleTimeoutMs);
            Log.Info("SESSION ACCEPTED session=" + _sessionId + " dir=Receive downloadDir=" + downloadRoot);
            Report(TransferStatus.InProgress, null, true);

            for (int index = 0; index < _files.Count; index++)
            {
                if (_manager.IsStopping) throw new TransferException("接收已停止");

                WireFileEntry entry = _files[index];
                string offeredName = entry != null ? entry.Name : "";
                long offeredSize = entry != null ? entry.Size : 0;
                if (offeredSize < 0)
                    throw new TransferProtocolException("文件大小无效：" + DescribeFile(offeredName, index));

                string line = _connection.ReadLine();
                if (line == null) throw new TransferException("发送端已断开连接");
                WireMessage message = WireMessage.Parse(line);
                if (message == null) throw new TransferProtocolException("无法解析控制消息");
                if (!string.Equals(message.Session, _sessionId, StringComparison.Ordinal))
                    throw new TransferProtocolException("会话标识不匹配");
                if (!string.Equals(message.Type, "file", StringComparison.Ordinal))
                    throw new TransferProtocolException("期望 file 消息，收到 " + (message.Type ?? "?"));
                if (message.Index != index)
                    throw new TransferProtocolException("文件序号不匹配：" + message.Index + "，期望 " + index);
                if (message.Size != offeredSize)
                    throw new TransferProtocolException("文件大小与 offer 不一致：" + DescribeFile(message.Name, index));

                string safeName = Protocol.SafeFileName(message.Name);
                if (string.IsNullOrEmpty(safeName)) safeName = Protocol.SafeFileName(offeredName);
                if (string.IsNullOrEmpty(safeName)) safeName = "file" + (index + 1) + ".bin";

                _currentIndex = index;
                string path = null;
                try
                {
                    using (FileStream target = CreateTargetFile(downloadRoot, safeName, out path))
                    {
                        _currentFileName = Path.GetFileName(path);
                        _partialPath = path;
                        CopyExact(target, offeredSize);
                        try { target.Flush(true); } catch { target.Flush(); }
                    }
                }
                catch (Exception ex)
                {
                    TrySendFileResult(index, false, TransferErrors.Describe(ex));
                    throw;
                }
                _partialPath = null;
                _savedFiles.Add(path);
                Log.Info("FILE SAVED session=" + _sessionId + " index=" + index + " name=" + _currentFileName +
                         " size=" + offeredSize + " path=" + path);
                Report(TransferStatus.InProgress, null, true);

                // Protocol v1: acknowledge every file so the sender can advance to the next one.
                _connection.WriteLine(Wire.FileResult(_sessionId, index, true, path, null));
            }

            if (_manager.IsStopping) throw new TransferException("接收已停止");
            string doneLine = _connection.ReadLine();
            if (doneLine == null) throw new TransferException("发送端未发送完成消息就断开连接");
            WireMessage doneMessage = WireMessage.Parse(doneLine);
            if (doneMessage == null) throw new TransferProtocolException("无法解析 done 消息");
            if (doneMessage.Type == "error")
                throw new TransferException(string.IsNullOrEmpty(doneMessage.Message) ? "发送端报告错误" : doneMessage.Message);
            if (!string.Equals(doneMessage.Type, "done", StringComparison.Ordinal))
                throw new TransferProtocolException("期望 done 消息，收到 " + (doneMessage.Type ?? "?"));
            if (!doneMessage.Ok) throw new TransferException("发送端报告传输失败");

            // Protocol v1: the receiver acknowledges done before both sides close.
            _connection.WriteLine(Wire.Done(_sessionId, true));

            Report(TransferStatus.Completed, null, true);
            Log.Info("SESSION DONE session=" + _sessionId + " dir=Receive files=" + _savedFiles.Count +
                     " bytes=" + _transferredBytes + " elapsed=" + Elapsed());
            Complete(true, null);
        }

        private FileStream CreateTargetFile(string directory, string safeName, out string path)
        {
            string candidate = safeName;
            for (int attempt = 0; ; attempt++)
            {
                path = Protocol.MakeUniquePath(directory, candidate);
                try
                {
                    return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        TransferLimits.BufferSize, FileOptions.SequentialScan);
                }
                catch (IOException)
                {
                    if (attempt >= 100) throw;
                    candidate = Path.GetFileName(path);
                }
            }
        }

        private void CopyExact(FileStream target, long expected)
        {
            long remaining = expected;
            while (remaining > 0)
            {
                if (_manager.IsStopping) throw new TransferException("接收已停止");
                // Protocol v1: 128 KiB chunks, 64 KiB read/write buffer.
                int chunkRemaining = (int)Math.Min(TransferLimits.ChunkSize, remaining);
                while (chunkRemaining > 0)
                {
                    int want = Math.Min(_buffer.Length, chunkRemaining);
                    int read = _connection.Read(_buffer, 0, want);
                    if (read <= 0) throw new TransferException("数据不完整，还差 " + remaining + " 字节");
                    target.Write(_buffer, 0, read);
                    chunkRemaining -= read;
                    remaining -= read;
                    _transferredBytes += read;
                    Report(TransferStatus.InProgress, null, false);
                }
            }
        }

        private void DeletePartialFile()
        {
            string path = _partialPath;
            _partialPath = null;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Info("已删除未完成的文件：" + path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("删除未完成文件失败：" + path + " - " + ex.Message);
            }
        }

        private void TrySendError(string message)
        {
            try { _connection.WriteLine(Wire.Error(_sessionId, message)); } catch { }
        }

        private void TrySendFileResult(int index, bool ok, string error)
        {
            try { _connection.WriteLine(Wire.FileResult(_sessionId, index, ok, null, error)); } catch { }
        }

        private void Report(TransferStatus status, string error, bool force)
        {
            if (!force && !_throttle.ShouldReport()) return;
            _throttle.Mark();
            var progress = new TransferProgress
            {
                SessionId = _sessionId,
                Direction = TransferDirection.Receive,
                Status = status,
                Peer = _peer,
                CurrentFileName = _currentFileName,
                FileIndex = _currentFileName.Length > 0 ? _currentIndex + 1 : 0,
                FileCount = _files.Count,
                TransferredBytes = _transferredBytes,
                TotalBytes = _totalBytes,
                BytesPerSecond = _speed.Update(_transferredBytes),
                Error = error,
                StartedUtc = _startedUtc
            };
            _manager.RaiseProgress(progress);
        }

        private void Complete(bool success, string error)
        {
            if (_completedRaised) return;
            _completedRaised = true;
            _manager.RaiseCompleted(new TransferCompletedEventArgs
            {
                SessionId = _sessionId,
                Direction = TransferDirection.Receive,
                Peer = _peer,
                Success = success,
                Error = error,
                SavedFiles = _savedFiles.ToArray(),
                TotalBytes = _totalBytes,
                StartedUtc = _startedUtc,
                FinishedUtc = DateTime.UtcNow
            });
        }

        private string Elapsed()
        {
            return (DateTime.UtcNow - _startedUtc).TotalSeconds.ToString("0.0") + "s";
        }

        private static string DescribePeer(PeerInfo peer)
        {
            if (peer == null) return "?";
            return peer.DisplayName + "@" + (string.IsNullOrEmpty(peer.Address) ? "?" : peer.AddressText);
        }

        private static string DescribeFile(string name, int index)
        {
            return string.IsNullOrEmpty(name) ? "index=" + index : name;
        }
    }
}
