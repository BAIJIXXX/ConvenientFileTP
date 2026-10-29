using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace ConvenientFileTP.Core.Transfer
{
    /// <summary>One file entry inside an offer message.</summary>
    internal sealed class WireFileEntry
    {
        public string Name { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    /// Parsed control message. The v1 protocol uses a single flat shape: every message carries
    /// "t" plus the fields that are meaningful for that type. Parsing is done through
    /// Dictionary&lt;string, object&gt; so unknown / extra fields are simply ignored.
    /// </summary>
    internal sealed class WireMessage
    {
        public string Type { get; set; }
        public string App { get; set; }
        public int Version { get; set; }
        public string Session { get; set; }
        public string FromId { get; set; }
        public string FromName { get; set; }
        public List<WireFileEntry> Files { get; set; }
        public bool Accepted { get; set; }
        public string Reason { get; set; }
        public int Index { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public bool Ok { get; set; }
        public string Saved { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }

        public static WireMessage Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            Dictionary<string, object> map;
            try
            {
                map = Json.Deserialize<Dictionary<string, object>>(line);
            }
            catch
            {
                return null;
            }
            if (map == null) return null;

            var message = new WireMessage
            {
                Type = GetString(map, "t"),
                App = GetString(map, "app"),
                Version = GetInt(map, "v"),
                Session = GetString(map, "session"),
                FromId = GetString(map, "fromId"),
                FromName = GetString(map, "fromName"),
                Accepted = GetBool(map, "accepted"),
                Reason = GetString(map, "reason"),
                Index = GetInt(map, "index"),
                Name = GetString(map, "name"),
                Size = GetLong(map, "size"),
                Ok = GetBool(map, "ok"),
                Saved = GetString(map, "saved"),
                Error = GetString(map, "error"),
                Message = GetString(map, "message")
            };

            object filesValue;
            if (map.TryGetValue("files", out filesValue) && filesValue != null)
            {
                var entries = new List<WireFileEntry>();
                var sequence = filesValue as IEnumerable;
                if (sequence != null)
                {
                    foreach (object item in sequence)
                    {
                        var fileMap = item as Dictionary<string, object>;
                        if (fileMap == null) continue;
                        entries.Add(new WireFileEntry
                        {
                            Name = GetString(fileMap, "name"),
                            Size = GetLong(fileMap, "size")
                        });
                    }
                }
                message.Files = entries;
            }
            return message;
        }

        private static object GetValue(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value = GetValue(map, key);
            if (value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int GetInt(Dictionary<string, object> map, string key)
        {
            object value = GetValue(map, key);
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static long GetLong(Dictionary<string, object> map, string key)
        {
            object value = GetValue(map, key);
            if (value == null) return 0;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static bool GetBool(Dictionary<string, object> map, string key)
        {
            object value = GetValue(map, key);
            if (value == null) return false;
            if (value is bool) return (bool)value;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }
    }

    /// <summary>Builds the JSON control lines defined by docs/protocol.md (UTF-8 + LF).</summary>
    internal static class Wire
    {
        public static string Offer(string session, string fromId, string fromName, IList<WireFileEntry> files)
        {
            var list = new List<object>();
            if (files != null)
            {
                foreach (WireFileEntry entry in files)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "name", entry.Name ?? "" },
                        { "size", entry.Size }
                    });
                }
            }
            return Json.Serialize(new Dictionary<string, object>
            {
                { "t", "offer" },
                { "app", Protocol.AppId },
                { "v", Protocol.Version },
                { "session", session ?? "" },
                { "fromId", fromId ?? "" },
                { "fromName", fromName ?? "" },
                { "files", list }
            });
        }

        public static string Accept(string session, bool accepted, string reason)
        {
            var map = new Dictionary<string, object>
            {
                { "t", "accept" },
                { "session", session ?? "" },
                { "accepted", accepted }
            };
            if (!accepted) map["reason"] = reason ?? "";
            return Json.Serialize(map);
        }

        public static string FileHeader(string session, int index, string name, long size)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "t", "file" },
                { "session", session ?? "" },
                { "index", index },
                { "name", name ?? "" },
                { "size", size }
            });
        }

        public static string FileResult(string session, int index, bool ok, string saved, string error)
        {
            var map = new Dictionary<string, object>
            {
                { "t", "file-result" },
                { "session", session ?? "" },
                { "index", index },
                { "ok", ok }
            };
            if (ok) map["saved"] = saved ?? "";
            else map["error"] = error ?? "";
            return Json.Serialize(map);
        }

        public static string Done(string session, bool ok)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "t", "done" },
                { "session", session ?? "" },
                { "ok", ok }
            });
        }

        public static string Error(string session, string message)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "t", "error" },
                { "session", session ?? "" },
                { "message", message ?? "" }
            });
        }
    }
}
