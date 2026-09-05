using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Management;

namespace NetScheduler
{
    /// <summary>网卡状态查询/切换与 Wi-Fi 连接操作（均通过 WMI / netsh，与系统语言无关）。</summary>
    public static class NetHelper
    {
        /// <summary>查询网卡管理状态：true=已启用 false=已禁用 null=未找到。</summary>
        public static bool? GetAdapterEnabled(string connId)
        {
            if (string.IsNullOrEmpty(connId)) return null;
            try
            {
                string q = "SELECT NetEnabled FROM Win32_NetworkAdapter WHERE NetConnectionID = '" +
                           connId.Replace("'", "") + "'";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(q))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            object v = o["NetEnabled"];
                            if (v == null) return null;
                            return Convert.ToBoolean(v);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("查询网卡状态失败: " + ex.Message);
            }
            return null;
        }

        /// <summary>启用/禁用网卡，返回是否成功。</summary>
        public static bool SetAdapter(string connId, bool enable)
        {
            if (string.IsNullOrEmpty(connId)) return false;
            try
            {
                string q = "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID = '" +
                           connId.Replace("'", "") + "'";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(q))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            object rc = o.InvokeMethod(enable ? "Enable" : "Disable", null);
                            return rc != null && Convert.ToUInt32(rc) == 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("切换网卡失败: " + ex.Message);
            }
            return false;
        }

        /// <summary>自动检测有线网卡连接名：物理网卡中排除无线/蓝牙后的第一个。</summary>
        public static string DetectEthernetName()
        {
            try
            {
                string q = "SELECT NetConnectionID, Name, Description FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(q))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            string id = Convert.ToString(o["NetConnectionID"]);
                            if (string.IsNullOrEmpty(id)) continue;
                            string desc = (Convert.ToString(o["Description"]) ?? "") + " " +
                                          (Convert.ToString(o["Name"]) ?? "");
                            string dl = desc.ToLowerInvariant();
                            if (dl.Contains("wi-fi") || dl.Contains("wifi") || dl.Contains("wireless") ||
                                dl.Contains("802.11") || dl.Contains("wlan") || dl.Contains("bluetooth") ||
                                desc.Contains("无线") || desc.Contains("蓝牙"))
                                continue;
                            return id;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("检测有线网卡失败: " + ex.Message);
            }
            return null;
        }

        /// <summary>通过 netsh 发起 Wi-Fi 连接（异步命令，立即返回）。</summary>
        public static void ConnectWifi(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan connect name=\"" + profile + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex)
            {
                Logger.Error("发起 Wi-Fi 连接失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 探测当前已连接 Wi-Fi 的配置文件名（用于自动填充 WifiProfile）。
        /// 未连接任何 Wi-Fi 或解析失败时返回 null；autoConnectOut 表示该配置是否为"自动连接"。
        /// </summary>
        public static string GetCurrentWifiProfile(out bool autoConnectOut)
        {
            autoConnectOut = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show interfaces");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    bool connected = false;
                    string profile = null;
                    string[] lines = output.Replace("\r\n", "\n").Split('\n');
                    foreach (string raw in lines)
                    {
                        string line = raw.Trim();
                        int ci = line.IndexOf(':');
                        if (ci <= 0) continue;
                        string key = line.Substring(0, ci).Trim();
                        string val = line.Substring(ci + 1).Trim();
                        if (key == "状态" || key.Equals("State", StringComparison.OrdinalIgnoreCase))
                            connected = val.Contains("已连接") ||
                                val.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0;
                        else if (key == "配置文件" || key.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(val)) profile = val;
                        }
                        else if (key == "连接模式" || key.Equals("Connection mode", StringComparison.OrdinalIgnoreCase))
                            autoConnectOut = val.Contains("自动连接") ||
                                val.IndexOf("auto", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    return connected ? profile : null;
                }
            }
            catch { return null; }
        }

        /// <summary>是否有任一无线网卡处于连接状态。</summary>
        public static bool IsWifiConnected()
        {
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        nic.OperationalStatus == OperationalStatus.Up)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
