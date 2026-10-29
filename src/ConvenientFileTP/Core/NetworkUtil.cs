using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ConvenientFileTP.Core
{
    /// <summary>Enumerates local IPv4 interfaces and makes a best effort to classify the network.</summary>
    public static class NetworkUtil
    {
        public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.77.77");

        public static List<LocalInterfaceInfo> GetLocalInterfaces()
        {
            var result = new List<LocalInterfaceInfo>();
            string ssid = null;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { continue; }
                    if (props == null) continue;

                    string gateway = "";
                    try
                    {
                        var gw = props.GatewayAddresses.FirstOrDefault(g =>
                            g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !g.Address.Equals(IPAddress.Any));
                        if (gw != null) gateway = gw.Address.ToString();
                    }
                    catch { }

                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;

                        string mask = "";
                        try { if (ua.IPv4Mask != null) mask = ua.IPv4Mask.ToString(); } catch { }

                        bool wireless = IsWireless(ni);
                        if (wireless && ssid == null) ssid = TryGetSsid();

                        NetworkMode mode = DetectMode(ni, ip, gateway, wireless, ssid);
                        result.Add(new LocalInterfaceInfo
                        {
                            InterfaceName = ni.Name,
                            Address = ip,
                            Mask = mask,
                            Gateway = gateway,
                            Mode = mode,
                            Ssid = wireless ? ssid : null
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("枚举本机网卡失败", ex);
            }

            if (result.Count == 0)
            {
                result.Add(new LocalInterfaceInfo
                {
                    InterfaceName = "Loopback",
                    Address = "127.0.0.1",
                    Mask = "255.0.0.0",
                    Gateway = "",
                    Mode = NetworkMode.Loopback
                });
            }
            return result;
        }

        private static bool IsWireless(NetworkInterface ni)
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return true;
                string text = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
                return text.Contains("wi-fi") || text.Contains("wifi") || text.Contains("wireless") ||
                       text.Contains("wlan") || text.Contains("802.11");
            }
            catch { return false; }
        }

        private static NetworkMode DetectMode(NetworkInterface ni, string ip, string gateway, bool wireless, string ssid)
        {
            string text = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
            if (text.Contains("wi-fi direct virtual") || text.Contains("hosted network") ||
                text.Contains("virtual wifi") || ip.StartsWith("192.168.137."))
            {
                return NetworkMode.HotspotHost;
            }

            if (wireless && !string.IsNullOrEmpty(gateway))
            {
                // Common phone-hotspot gateways.
                if (gateway == "192.168.43.1" || gateway.StartsWith("172.20.10.") ||
                    gateway == "192.168.137.1")
                {
                    return NetworkMode.HotspotClient;
                }
            }

            if (wireless && !string.IsNullOrEmpty(ssid))
            {
                return NetworkMode.HotspotClient;
            }

            return NetworkMode.Lan;
        }

        public static string TryGetSsid()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2500);
                    var m = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    if (m.Success)
                    {
                        string value = m.Groups[1].Value.Trim();
                        if (value.Length > 0) return value;
                    }
                }
            }
            catch { }
            return null;
        }

        public static string GetSelfModeText(IList<LocalInterfaceInfo> interfaces)
        {
            if (interfaces == null || interfaces.Count == 0) return "局域网";
            bool lan = interfaces.Any(i => i.Mode == NetworkMode.Lan);
            bool host = interfaces.Any(i => i.Mode == NetworkMode.HotspotHost);
            bool client = interfaces.Any(i => i.Mode == NetworkMode.HotspotClient);
            if (!lan && !host && !client)
            {
                return interfaces.All(i => i.Mode == NetworkMode.Loopback) ? "仅本机" : "未知网络";
            }
            var parts = new List<string>();
            if (lan) parts.Add("局域网");
            if (host) parts.Add("本机热点");
            if (client) parts.Add("Wi-Fi 热点");
            return string.Join(" + ", parts);
        }

        public static NetworkMode GetSelfMode(IList<LocalInterfaceInfo> interfaces)
        {
            if (interfaces == null || interfaces.Count == 0) return NetworkMode.Unknown;
            if (interfaces.Any(i => i.Mode == NetworkMode.HotspotHost)) return NetworkMode.HotspotHost;
            if (interfaces.Any(i => i.Mode == NetworkMode.HotspotClient)) return NetworkMode.HotspotClient;
            if (interfaces.Any(i => i.Mode == NetworkMode.Lan)) return NetworkMode.Lan;
            return interfaces[0].Mode;
        }

        public static string GetPrimaryAddress(IList<LocalInterfaceInfo> interfaces)
        {
            if (interfaces == null || interfaces.Count == 0) return "127.0.0.1";
            LocalInterfaceInfo best = interfaces.FirstOrDefault(i => i.Mode == NetworkMode.Lan)
                                     ?? interfaces.FirstOrDefault(i => i.Mode == NetworkMode.HotspotHost)
                                     ?? interfaces.FirstOrDefault(i => i.Mode == NetworkMode.HotspotClient)
                                     ?? interfaces[0];
            return best.Address;
        }

        public static List<IPAddress> GetBroadcastAddresses(IList<LocalInterfaceInfo> interfaces)
        {
            var list = new List<IPAddress>();
            if (interfaces != null)
            {
                foreach (var iface in interfaces)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(iface.Address) || string.IsNullOrEmpty(iface.Mask)) continue;
                        byte[] ip = IPAddress.Parse(iface.Address).GetAddressBytes();
                        byte[] mask = IPAddress.Parse(iface.Mask).GetAddressBytes();
                        if (ip.Length != 4 || mask.Length != 4) continue;
                        byte[] bcast = new byte[4];
                        for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                        var addr = new IPAddress(bcast);
                        if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.Broadcast)) continue;
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                    catch { }
                }
            }
            if (!list.Contains(IPAddress.Broadcast)) list.Add(IPAddress.Broadcast);
            return list;
        }
    }
}
