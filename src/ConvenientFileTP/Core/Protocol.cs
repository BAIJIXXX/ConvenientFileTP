using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ConvenientFileTP.Core
{
    /// <summary>Shared wire protocol constants and small helpers.</summary>
    public static class Protocol
    {
        public const string AppId = "ConvenientFileTP";
        public const int Version = 1;
        public const int DefaultDiscoveryPort = 47731;
        public const int DefaultTcpPort = 47732;
        public const int MaxControlLineBytes = 256 * 1024;

        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Serialize(object value)
        {
            return Json.Serialize(value);
        }

        public static T Deserialize<T>(string json)
        {
            return Json.Deserialize<T>(json);
        }

        /// <summary>Reads one UTF-8 line (LF terminated) without buffering beyond the line.</summary>
        public static string ReadLine(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            var buffer = new MemoryStream(256);
            int b = -1;
            while (true)
            {
                b = stream.ReadByte();
                if (b < 0) break;
                if (b == '\n') break;
                if (b == '\r') continue;
                buffer.WriteByte((byte)b);
                if (buffer.Length > MaxControlLineBytes)
                {
                    throw new InvalidDataException("控制消息过长");
                }
            }
            if (b < 0 && buffer.Length == 0) return null;
            return Utf8NoBom.GetString(buffer.ToArray());
        }

        public static void WriteLine(Stream stream, string line)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            byte[] data = Utf8NoBom.GetBytes(line ?? "");
            stream.Write(data, 0, data.Length);
            stream.WriteByte((byte)'\n');
            stream.Flush();
        }

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>Sanitizes an incoming file name (strips any directory component).</summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "文件";
            name = name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            name = name.Replace(":", "_");
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            name = name.Trim();
            name = name.TrimEnd('.');
            if (name.Length == 0 || name == "." || name == "..") name = "文件";

            string baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (Array.IndexOf(ReservedNames, baseName) >= 0)
            {
                name = "_" + name;
            }

            const int maxLength = 120;
            if (name.Length > maxLength)
            {
                string ext = Path.GetExtension(name);
                if (ext.Length > 16) ext = "";
                string stem = Path.GetFileNameWithoutExtension(name);
                int keep = Math.Max(1, maxLength - ext.Length);
                if (stem.Length > keep) stem = stem.Substring(0, keep);
                name = stem + ext;
            }
            return name;
        }

        /// <summary>Returns a path inside <paramref name="directory"/> that does not exist yet.</summary>
        public static string MakeUniquePath(string directory, string fileName)
        {
            string safe = SafeFileName(fileName);
            string candidate = Path.Combine(directory, safe);
            if (!File.Exists(candidate)) return candidate;

            string stem = Path.GetFileNameWithoutExtension(safe);
            string ext = Path.GetExtension(safe);
            for (int i = 1; i < 10000; i++)
            {
                candidate = Path.Combine(directory, stem + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(directory, stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
        }
    }

    /// <summary>UDP discovery datagram payload.</summary>
    public sealed class DiscoveryMessage
    {
        public string app { get; set; }
        public int v { get; set; }
        public string t { get; set; }          // "announce" | "query" | "bye"
        public string id { get; set; }
        public string name { get; set; }
        public int port { get; set; }
        public string mode { get; set; }
        public string os { get; set; }
        public string ver { get; set; }
        public long ts { get; set; }
    }

    /// <summary>Millisecond Unix timestamps used on the wire.</summary>
    public static class TimeUtil
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long ToMilliseconds(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc) utc = utc.ToUniversalTime();
            return (long)(utc - Epoch).TotalMilliseconds;
        }

        public static DateTime FromMilliseconds(long ms)
        {
            try { return Epoch.AddMilliseconds(ms); }
            catch { return DateTime.UtcNow; }
        }
    }
}
