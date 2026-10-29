using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ConvenientFileTP.Core.Transfer
{
    /// <summary>
    /// Thin wrapper around one TCP control connection: UTF-8 JSON lines for control messages and
    /// raw bytes for file payloads. Timeouts use the underlying socket so a stalled peer never
    /// blocks a worker forever.
    /// </summary>
    internal sealed class TransferConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private volatile bool _closed;

        public TransferConnection(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException("client");
            _client = client;
            try { _client.NoDelay = true; } catch { }
            _stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        }

        public IPEndPoint RemoteEndPoint { get; private set; }

        public string RemoteAddress
        {
            get
            {
                IPEndPoint endpoint = RemoteEndPoint;
                return endpoint != null && endpoint.Address != null ? endpoint.Address.ToString() : "";
            }
        }

        public string RemoteText
        {
            get
            {
                IPEndPoint endpoint = RemoteEndPoint;
                return endpoint != null ? endpoint.ToString() : "";
            }
        }

        public bool IsClosed
        {
            get { return _closed; }
        }

        public void SetReceiveTimeout(int milliseconds)
        {
            try { _client.Client.ReceiveTimeout = milliseconds; } catch { }
        }

        public void SetSendTimeout(int milliseconds)
        {
            try { _client.Client.SendTimeout = milliseconds; } catch { }
        }

        /// <summary>Reads one LF-terminated UTF-8 line; returns null on a clean EOF.</summary>
        public string ReadLine()
        {
            return Protocol.ReadLine(_stream);
        }

        /// <summary>Reads raw file payload bytes; returns 0 on EOF.</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            return _stream.Read(buffer, offset, count);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
        }

        public void WriteLine(string line)
        {
            Protocol.WriteLine(_stream, line);
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _client.Close(); } catch { }
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>Raised when a transfer fails for a reason we already describe to the user.</summary>
    internal class TransferException : Exception
    {
        public TransferException(string message) : base(message) { }

        public TransferException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>Raised when the peer violates the wire protocol.</summary>
    internal sealed class TransferProtocolException : TransferException
    {
        public TransferProtocolException(string message) : base(message) { }
    }
}
