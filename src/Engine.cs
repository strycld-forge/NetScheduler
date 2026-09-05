using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace NetScheduler
{
    /// <summary>
    /// 核心对账引擎：每分钟根据 时间/星期/白名单/手动模式 计算"期望状态"，
    /// 与网卡实际状态不一致时自动纠正。统一覆盖定时切换、开机抉择、睡眠唤醒补判、外部篡改自愈。
    /// </summary>
    public class Engine : IDisposable
    {
        private readonly string _configPath;
        private int _busy = 0;
        private DateTime? _manualExpiresAt;
        private DateTime _lastWifiAttempt = DateTime.MinValue;

        public AppConfig Config;
        public Mode CurrentMode = Mode.Auto;
        public string ResolvedEthernet = "";
        public bool IsAdmin { get; private set; }
        public WiredState? LastActual { get; private set; }

        /// <summary>UI 刷新回调（在后台线程触发，UI 端自行 Invoke）。</summary>
        public Action UiChanged;
        /// <summary>气泡通知回调 (标题, 内容)。</summary>
        public Action<string, string> NotifyUser;

        public Engine(string configPath, AppConfig cfg)
        {
            _configPath = configPath;
            Config = cfg;
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    IsAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { IsAdmin = false; }
            ResolveEthernet();
            ResolveWifiProfile();
        }

        // ---------- 主入口 ----------

        /// <summary>执行一次状态对账（线程安全，重入时直接跳过）。</summary>
        public void Tick()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            try
            {
                DoTick();
            }
            catch (Exception ex)
            {
                Logger.Error("对账异常: " + ex);
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
            Action cb = UiChanged;
            if (cb != null) { try { cb(); } catch { } }
        }

        private void DoTick()
        {
            DateTime now = DateTime.Now;

            // 手动模式到期自动恢复
            if (CurrentMode != Mode.Auto && Config.ManualExpiresNextEvent &&
                _manualExpiresAt.HasValue && now >= _manualExpiresAt.Value)
            {
                CurrentMode = Mode.Auto;
                _manualExpiresAt = null;
                Logger.Info("已到定时点，手动模式自动恢复为跟随计划");
                Notify("NetScheduler", "已到定时点，自动恢复为跟随计划模式");
            }

            WiredState desired = Config.Desired(now, CurrentMode);
            bool? enabled = NetHelper.GetAdapterEnabled(ResolvedEthernet);
            if (!enabled.HasValue)
            {
                ResolveEthernet(); // 网卡名可能变了，重试检测
                enabled = NetHelper.GetAdapterEnabled(ResolvedEthernet);
                if (!enabled.HasValue)
                {
                    Logger.Warn("找不到有线网卡 \"" + ResolvedEthernet + "\"，跳过本次对账");
                    LastActual = null;
                    return;
                }
            }
            WiredState? actual = enabled.Value ? WiredState.On : WiredState.Off;
            LastActual = actual;

            if (desired != actual.Value)
            {
                bool ok = NetHelper.SetAdapter(ResolvedEthernet, desired == WiredState.On);
                string act = desired == WiredState.On ? "启用" : "禁用";
                Logger.Info(act + "有线网卡 \"" + ResolvedEthernet + "\" " + (ok ? "成功" : "失败"));
                if (ok)
                {
                    LastActual = desired;
                    if (desired == WiredState.Off)
                        StartWifiConnect(Config.ConnectRetries);
                    Notify("NetScheduler 状态切换",
                        string.Format("{0:HH:mm} 已{1}有线网络", DateTime.Now,
                            desired == WiredState.On ? "恢复" : "断开"));
                }
                else
                {
                    Notify("NetScheduler", act + "有线网卡失败，下个对账周期将重试");
                }
            }
            else if (desired == WiredState.Off && Config.KeepWifiConnected &&
                     !string.IsNullOrEmpty(Config.WifiProfile))
            {
                EnsureWifi();
            }
        }

        // ---------- 手动模式 ----------

        public void SetMode(Mode m)
        {
            CurrentMode = m;
            _manualExpiresAt = null;
            if (m != Mode.Auto && Config.ManualExpiresNextEvent)
            {
                WiredState after;
                DateTime? b = Config.NextEffectiveBoundary(DateTime.Now, out after);
                _manualExpiresAt = b;
            }
            Logger.Info("手动设置模式: " + ModeText.Get(m) +
                        (_manualExpiresAt.HasValue ? "（至 " + _manualExpiresAt.Value.ToString("MM-dd HH:mm") + "）" : ""));
            ThreadPool.QueueUserWorkItem(delegate { Tick(); });
        }

        public string ManualExpiresText()
        {
            if (CurrentMode == Mode.Auto || !_manualExpiresAt.HasValue) return "";
            return "（至 " + _manualExpiresAt.Value.ToString("HH:mm") + "）";
        }

        // ---------- Wi-Fi ----------

        private void EnsureWifi()
        {
            if (NetHelper.IsWifiConnected())
            {
                // 连着 Wi-Fi 但还没记录配置文件名：顺手自动补上，供掉线重连使用
                if (string.IsNullOrEmpty(Config.WifiProfile)) ResolveWifiProfile();
                return;
            }
            if (string.IsNullOrEmpty(Config.WifiProfile) || (DateTime.Now - _lastWifiAttempt).TotalSeconds < 55)
                return;
            _lastWifiAttempt = DateTime.Now;
            string profile = Config.WifiProfile;
            Logger.Info("Wi-Fi 未连接，尝试连接热点 " + profile);
            ThreadPool.QueueUserWorkItem(delegate { NetHelper.ConnectWifi(profile); });
        }

        private void StartWifiConnect(int attempts)
        {
            if (string.IsNullOrEmpty(Config.WifiProfile))
            {
                Logger.Warn("未配置 WifiProfile，跳过 Wi-Fi 连接");
                return;
            }
            _lastWifiAttempt = DateTime.Now;
            string profile = Config.WifiProfile;
            int interval = Config.RetryIntervalSec;
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 1; i <= attempts; i++)
                {
                    NetHelper.ConnectWifi(profile);
                    Thread.Sleep(TimeSpan.FromSeconds(interval));
                    if (NetHelper.IsWifiConnected())
                    {
                        Logger.Info("Wi-Fi 已连接 " + profile);
                        return;
                    }
                    Logger.Info(string.Format("第 {0}/{1} 次连接热点未成功", i, attempts));
                }
                Logger.Warn("热点连接重试用尽，将随每分钟对账继续尝试");
            });
        }

        // ---------- 辅助 ----------

        /// <summary>解析有线网卡名；为空时自动检测并写回配置文件。</summary>
        public void ResolveEthernet()
        {
            if (!string.IsNullOrEmpty(Config.EthernetName))
            {
                ResolvedEthernet = Config.EthernetName;
                return;
            }
            string detected = NetHelper.DetectEthernetName();
            if (!string.IsNullOrEmpty(detected))
            {
                ResolvedEthernet = detected;
                WriteConfigValue("EthernetName", detected);
                Logger.Info("已自动检测有线网卡并写回配置: " + detected);
            }
        }

        /// <summary>WifiProfile 为空时，探测当前已连接 Wi-Fi 的配置文件名并写回配置。</summary>
        public void ResolveWifiProfile()
        {
            if (!string.IsNullOrEmpty(Config.WifiProfile)) return;
            bool auto;
            string detected = NetHelper.GetCurrentWifiProfile(out auto);
            if (string.IsNullOrEmpty(detected)) return;
            Config.WifiProfile = detected;
            WriteConfigValue("WifiProfile", detected);
            Logger.Info("已自动检测 Wi-Fi 配置文件并写回配置: " + detected + (auto ? "（自动连接）" : "（注意：该配置未开启自动连接）"));
        }

        private void WriteConfigValue(string key, string value)
        {
            try
            {
                string[] lines = File.ReadAllLines(_configPath);
                bool done = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].TrimStart();
                    if (t.StartsWith(key, StringComparison.OrdinalIgnoreCase) && t.IndexOf('=') >= 0)
                    {
                        lines[i] = key + " = " + value;
                        done = true;
                    }
                }
                if (!done) return;
                File.WriteAllLines(_configPath, lines, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Logger.Warn("写回配置 " + key + " 失败: " + ex.Message);
            }
        }

        /// <summary>下次有效切换的显示文本。</summary>
        public string NextBoundaryText()
        {
            WiredState after;
            DateTime? b = Config.NextEffectiveBoundary(DateTime.Now, out after);
            if (!b.HasValue) return "无自动切换（白名单期间）";
            return b.Value.ToString("ddd HH:mm") + " " + (after == WiredState.Off ? "断有线" : "恢复有线");
        }

        private void Notify(string title, string text)
        {
            Action<string, string> cb = NotifyUser;
            if (cb != null) { try { cb(title, text); } catch { } }
        }

        public void Dispose()
        {
        }
    }
}
