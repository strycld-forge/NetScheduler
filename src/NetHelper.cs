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

        /// <summary>
        /// 启用/禁用网卡，返回是否成功（以切换后实际查询验证为准）。
        /// 主路径用 netsh：部分网卡上 WMI 的 Enable/Disable 会返回 0 但实际不生效（假成功）。
        /// </summary>
        public static bool SetAdapter(string connId, bool enable)
        {
            if (string.IsNullOrEmpty(connId)) return false;

            // 1) netsh 切换
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh",
                    "interface set interface name=\"" + connId + "\" " +
                    (enable ? "admin=enable" : "admin=disable"));
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    if (!p.HasExited || p.ExitCode != 0)
                    {
                        Logger.Error("netsh " + (enable ? "启用" : "禁用") + "网卡失败: " +
                                     (string.IsNullOrEmpty(err) ? "exit=" + p.ExitCode : err.Trim()));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("netsh 切换网卡异常: " + ex.Message);
            }

            // 2) 实际验证（最长约 4 秒，驱动启动可能稍有延迟）
            for (int i = 0; i < 4; i++)
            {
                System.Threading.Thread.Sleep(1000);
                bool? s = GetAdapterEnabled(connId);
                if (s.HasValue && s.Value == enable) return true;
            }

            // 3) 兜底：WMI 再试一次并验证（个别系统 netsh 被策略限制）
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
                            if (rc == null || Convert.ToUInt32(rc) != 0) break;
                            for (int i = 0; i < 3; i++)
                            {
                                System.Threading.Thread.Sleep(1000);
                                bool? st = GetAdapterEnabled(connId);
                                if (st.HasValue && st.Value == enable) return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("WMI 切换网卡异常: " + ex.Message);
            }
            return false;
        }

        /// <summary>无线/手机共享等应当排除的网卡关键词（匹配连接名与描述的小写形式）。</summary>
        private static readonly string[] ExcludedKeywords =
        {
            "wi-fi", "wifi", "wireless", "802.11", "wlan", "bluetooth", "蓝牙",
            "rndis", "remote ndis", "tether", "共享", "手机",
            "oppo", "xiaomi", "huawei", "honor", "samsung", "android",
            "iphone", "apple mobile", "oneplus", "realme", "vivo", "redmi"
        };

        /// <summary>自动检测有线网卡连接名：物理网卡中排除无线/蓝牙/手机共享后的第一个，
        /// 多个候选时优先选名字里带"以太网/Ethernet"的。</summary>
        public static string DetectEthernetName()
        {
            try
            {
                string q = "SELECT NetConnectionID, Name, Description FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(q))
                {
                    string first = null, preferred = null;
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            string id = Convert.ToString(o["NetConnectionID"]);
                            if (string.IsNullOrEmpty(id)) continue;
                            string desc = (Convert.ToString(o["Description"]) ?? "") + " " +
                                          (Convert.ToString(o["Name"]) ?? "");
                            string all = (id + " " + desc).ToLowerInvariant();
                            bool excluded = false;
                            foreach (string k in ExcludedKeywords)
                            {
                                if (all.Contains(k)) { excluded = true; break; }
                            }
                            if (excluded) continue;
                            if (first == null) first = id;
                            if (preferred == null &&
                                (id.Contains("以太网") ||
                                 id.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 id.StartsWith("本地连接")))
                                preferred = id;
                        }
                    }
                    return preferred ?? first;
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
