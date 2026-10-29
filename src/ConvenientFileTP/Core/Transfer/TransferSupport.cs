using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace ConvenientFileTP.Core.Transfer
{
    /// <summary>Protocol v1 tuning constants (see docs/protocol.md).</summary>
    internal static class TransferLimits
    {
        /// <summary>Handshake (offer/accept exchange) must finish within this many ms.</summary>
        public const int HandshakeTimeoutMs = 20000;

        /// <summary>No progress at all for this long means the session is dead.</summary>
        public const int IdleTimeoutMs = 45000;

        /// <summary>Payload read/write buffer size.</summary>
        public const int BufferSize = 65536;

        /// <summary>File transfer chunk size (protocol v1: 128 KiB chunks over 64 KiB buffers).</summary>
        public const int ChunkSize = 131072;

        /// <summary>Progress events are throttled to roughly this interval.</summary>
        public const int ProgressIntervalMs = 100;

        /// <summary>TCP connect attempt must succeed within this many ms.</summary>
        public const int ConnectTimeoutMs = 10000;

        /// <summary>How many receive sessions may be active at the same time.</summary>
        public const int MaxConcurrentReceives = 4;

        /// <summary>Backlog for the listener; small is fine for a LAN tool.</summary>
        public const int ListenBacklog = 16;
    }

    /// <summary>Turns exceptions into short user-facing (Chinese) descriptions.</summary>
    internal static class TransferErrors
    {
        public static string Describe(Exception exception)
        {
            if (exception == null) return "未知错误";
            if (exception is TransferException) return exception.Message;

            var socket = exception as SocketException;
            if (socket != null) return DescribeSocket(socket);

            var io = exception as IOException;
            if (io != null && io.InnerException is SocketException)
                return DescribeSocket((SocketException)io.InnerException);

            var unauthorized = exception as UnauthorizedAccessException;
            if (unauthorized != null) return "没有权限访问文件或目录：" + unauthorized.Message;

            var notFound = exception as FileNotFoundException;
            if (notFound != null) return "文件不存在：" + notFound.Message;

            var directory = exception as DirectoryNotFoundException;
            if (directory != null) return "目录不存在：" + directory.Message;

            var pathTooLong = exception as PathTooLongException;
            if (pathTooLong != null) return "路径过长：" + pathTooLong.Message;

            var argument = exception as ArgumentException;
            if (argument != null) return "参数错误：" + argument.Message;

            var operation = exception as OperationCanceledException;
            if (operation != null) return "传输已取消";

            return exception.Message;
        }

        private static string DescribeSocket(SocketException exception)
        {
            switch (exception.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return "对方拒绝连接（接收端未启动或端口不通）";
                case SocketError.TimedOut:
                    return "连接超时，对方无响应";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                    return "网络不可达，请检查局域网连接";
                case SocketError.ConnectionReset:
                case SocketError.ConnectionAborted:
                case SocketError.Shutdown:
                    return "连接已被对方断开";
                case SocketError.AddressAlreadyInUse:
                    return "端口已被占用";
                case SocketError.AccessDenied:
                    return "网络访问被拒绝（防火墙可能拦截）";
                default:
                    return "网络错误：" + exception.Message;
            }
        }
    }

    /// <summary>
    /// Sliding-window byte rate used for the SpeedText / BytesPerSecond progress fields.
    /// Samples come from polled counters, so no per-chunk accounting is needed.
    /// </summary>
    internal sealed class SpeedMeter
    {
        private readonly object _sync = new object();
        private readonly Queue<KeyValuePair<long, long>> _samples = new Queue<KeyValuePair<long, long>>();
        private readonly long _windowTicks;
        private long _originTicks;
        private long _originBytes;
        private long _lastBytes;
        private long _lastSampleTicks;

        public SpeedMeter()
        {
            _windowTicks = Stopwatch.Frequency * 2; // 2 second window
            _originTicks = Stopwatch.GetTimestamp();
            _originBytes = 0;
            _lastBytes = 0;
            _lastSampleTicks = _originTicks;
        }

        public void Reset(long bytes)
        {
            lock (_sync)
            {
                _samples.Clear();
                long now = Stopwatch.GetTimestamp();
                _originTicks = now;
                _originBytes = bytes;
                _lastBytes = bytes;
                _lastSampleTicks = now;
                _samples.Enqueue(new KeyValuePair<long, long>(now, bytes));
            }
        }

        /// <summary>Feeds the current total byte count and returns the smoothed bytes/second.</summary>
        public double Update(long bytes)
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                _lastBytes = bytes;
                if (now - _lastSampleTicks >= Stopwatch.Frequency / 8)
                {
                    _lastSampleTicks = now;
                    _samples.Enqueue(new KeyValuePair<long, long>(now, bytes));
                    while (_samples.Count > 2 && now - _samples.Peek().Key > _windowTicks)
                        _samples.Dequeue();
                }
                return CurrentLocked(now);
            }
        }

        private double CurrentLocked(long now)
        {
            if (_samples.Count >= 2)
            {
                KeyValuePair<long, long> first = _samples.Peek();
                long elapsed = _lastSampleTicks - first.Key;
                if (elapsed <= 0) return 0;
                long delta = _lastBytes - first.Value;
                if (delta <= 0) return 0;
                return delta * (double)Stopwatch.Frequency / elapsed;
            }

            // Short transfers end before two samples can be collected; fall back to the
            // average rate since the start so a quick LAN copy still shows a real speed.
            long span = now - _originTicks;
            if (span <= 0) return 0;
            long average = _lastBytes - _originBytes;
            if (average <= 0) return 0;
            return average * (double)Stopwatch.Frequency / span;
        }
    }

    /// <summary>Rate limiter for Progress events (first event always allowed immediately).</summary>
    internal sealed class ProgressThrottle
    {
        private readonly int _intervalMs;
        private long _lastTicks;

        public ProgressThrottle() : this(TransferLimits.ProgressIntervalMs) { }

        public ProgressThrottle(int intervalMs)
        {
            _intervalMs = intervalMs > 0 ? intervalMs : TransferLimits.ProgressIntervalMs;
            _lastTicks = 0;
        }

        /// <summary>Polls whether a progress event may be raised now.</summary>
        public bool ShouldReport()
        {
            long now = Stopwatch.GetTimestamp();
            long last = _lastTicks;
            if (last != 0 && (now - last) * 1000 < _intervalMs * Stopwatch.Frequency) return false;
            _lastTicks = now;
            return true;
        }

        /// <summary>Marks the throttle as just fired (used before forced reports).</summary>
        public void Mark()
        {
            _lastTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Misc helper conversions shared by the sessions.</summary>
    internal static class TransferText
    {
        public static string FileNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path) ?? path; }
            catch { return path; }
        }

        public static string Fmt(long bytes)
        {
            return bytes.ToString();
        }
    }
}
