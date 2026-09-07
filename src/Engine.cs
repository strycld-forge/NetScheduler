using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace NetScheduler
{
    /// <summary>全局控制模式：自动=按计划调度（可临时强制，到点恢复）；手动=完全手动，不自动切换。</summary>
    public enum ControlMode { Auto, Manual }

    /// <summary>
    /// 核心对账引擎：每分钟根据 控制模式/强制状态/时间/星期/白名单 计算"期望状态"，
    /// 与网卡实际状态不一致时自动纠正。统一覆盖定时切换、开机抉择、睡眠唤醒补判、外部篡改自愈。
    /// 控制模式与强制状态持久化到 state.ini，重启后保持。
    /// </summary>
    public class Engine : IDisposable
    {
        private readonly string _configPath;
        private int _busy = 0;
        private DateTime? _manualExpiresAt;
        private DateTime _lastWifiAttempt = DateTime.MinValue;
        private DateTime _lastHeartbeat = DateTime.MinValue;

        public AppConfig Config;
        public ControlMode Control = ControlMode.Auto;
        public Mode CurrentMode = Mode.Auto; // Auto=跟随计划；ForceOn/ForceOff=强制（是否带到期取决于控制模式）
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
            LoadState(cfg.EnableWiredOnStart);
        }

        private string StatePath
        {
            get { return Path.Combine(Path.GetDirectoryName(_configPath), "state.ini"); }
        }

        // ---------- 期望状态 ----------

        /// <summary>最终期望状态：手动控制=完全由强制状态决定；自动控制=强制优先（带到期），否则按计划。</summary>
        public WiredState Desired(DateTime now)
        {
            if (Control == ControlMode.Manual)
                return CurrentMode == Mode.ForceOff ? WiredState.Off : WiredState.On;
            if (CurrentMode == Mode.ForceOn) return WiredState.On;
            if (CurrentMode == Mode.ForceOff) return WiredState.Off;
            return Config.DesiredAuto(now);
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

            // 自动控制下：临时强制到定时点自动恢复为跟随计划
            if (Control == ControlMode.Auto && CurrentMode != Mode.Auto && Config.ManualExpiresNextEvent &&
                _manualExpiresAt.HasValue && now >= _manualExpiresAt.Value)
            {
                CurrentMode = Mode.Auto;
                _manualExpiresAt = null;
                SaveState();
                Logger.Info("已到定时点，临时强制自动恢复为跟随计划");
                Notify("NetScheduler", "已到定时点，自动恢复为跟随计划");
            }

            WiredState desired = Desired(now);
            bool? enabled = NetHelper.GetAdapterEnabled(ResolvedEthernet);
            if (!enabled.HasValue)
            {
                // 配置的网卡不存在（设备名变了/USB共享网卡已拔出等）：自动回退到检测到的网卡
                string fallback = NetHelper.DetectEthernetName();
                if (!string.IsNullOrEmpty(fallback) && fallback != ResolvedEthernet)
                {
                    Logger.Warn("网卡 \"" + ResolvedEthernet + "\" 不存在，自动切换为检测到的 \"" + fallback + "\"");
                    Notify("NetScheduler 网卡变更",
                        "有线网卡 \"" + ResolvedEthernet + "\" 不存在，已自动切换为 \"" + fallback + "\"");
                    ResolvedEthernet = fallback;
                    enabled = NetHelper.GetAdapterEnabled(ResolvedEthernet);
                }
                if (!enabled.HasValue)
                {
                    Logger.Warn("找不到有线网卡 \"" + ResolvedEthernet + "\"，跳过本次对账");
                    LastActual = null;
                    return;
                }
            }
            WiredState? actual = enabled.Value ? WiredState.On : WiredState.Off;
            LastActual = actual;

            // 半小时一次心跳日志，便于判断程序是否一直在正常工作
            if ((now - _lastHeartbeat).TotalMinutes >= 30)
            {
                _lastHeartbeat = now;
                Logger.Info(string.Format("心跳: 有线网卡=\"{0}\" 状态={1} 控制={2}",
                    ResolvedEthernet, actual == WiredState.On ? "开" : "关", ControlStatusText()));
            }

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
                        string.Format("{0:HH:mm} 已{1}有线网络（{2}）", DateTime.Now,
                            desired == WiredState.On ? "恢复" : "断开", ControlStatusText()));
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

        // ---------- 控制模式与强制 ----------

        /// <summary>切换全局控制模式。</summary>
        public void SetControl(ControlMode m)
        {
            Control = m;
            _manualExpiresAt = null;
            if (m == ControlMode.Manual)
            {
                if (CurrentMode == Mode.Auto) CurrentMode = Mode.ForceOn; // 进入手动默认有线开
                Logger.Info("控制模式: 手动（有线" + (CurrentMode == Mode.ForceOn ? "开" : "关") + "，不再自动切换）");
                Notify("NetScheduler", "已切换为手动模式：有线" + (CurrentMode == Mode.ForceOn ? "开" : "关") +
                                       "，不再自动切换");
            }
            else
            {
                CurrentMode = Mode.Auto;
                Logger.Info("控制模式: 自动（跟随计划调度）");
                Notify("NetScheduler", "已切换为自动模式：跟随计划调度");
            }
            SaveState();
            TickAsync();
        }

        /// <summary>强制有线开/关。自动控制下带到期（到下个定时点恢复跟随）；手动控制下一直保持。</summary>
        public void SetForce(Mode m)
        {
            CurrentMode = m;
            _manualExpiresAt = null;
            if (Control == ControlMode.Auto && Config.ManualExpiresNextEvent)
            {
                WiredState after;
                DateTime? b = Config.NextEffectiveBoundary(DateTime.Now, out after);
                _manualExpiresAt = b;
            }
            string expiry = _manualExpiresAt.HasValue ? "，至 " + _manualExpiresAt.Value.ToString("MM-dd HH:mm") : "";
            Logger.Info((m == Mode.ForceOn ? "强制有线开" : "强制有线关") +
                        "（控制=" + (Control == ControlMode.Manual
                            ? "手动，保持不变）"
                            : "自动" + expiry + "）"));
            SaveState();
            TickAsync();
        }

        /// <summary>回到跟随计划；处于手动控制时等价于切回自动模式。</summary>
        public void SetFollow()
        {
            if (Control == ControlMode.Manual)
            {
                SetControl(ControlMode.Auto);
                return;
            }
            CurrentMode = Mode.Auto;
            _manualExpiresAt = null;
            Logger.Info("已恢复跟随计划");
            SaveState();
            TickAsync();
        }

        /// <summary>切换"手动模式下每次开机启用有线"开关（写回配置文件，热重载）。</summary>
        public void ToggleBootWired()
        {
            Config.EnableWiredOnStart = !Config.EnableWiredOnStart;
            WriteConfigValue("EnableWiredOnStart", Config.EnableWiredOnStart ? "true" : "false");
            Logger.Info("手动模式每次开机启用有线: " + (Config.EnableWiredOnStart ? "开" : "关"));
            Action cb = UiChanged;
            if (cb != null) { try { cb(); } catch { } }
        }

        /// <summary>托盘菜单状态行的控制文本（与真实状态同步）。</summary>
        public string ControlStatusText()
        {
            if (Control == ControlMode.Manual)
                return "手动（" + (CurrentMode == Mode.ForceOff ? "有线关" : "有线开") + "，不自动切换）";
            if (CurrentMode == Mode.ForceOn) return "自动（临时强制开" + ManualExpiresText() + "）";
            if (CurrentMode == Mode.ForceOff) return "自动（临时强制关" + ManualExpiresText() + "）";
            return "自动（跟随计划）";
        }

        public string ManualExpiresText()
        {
            if (Control == ControlMode.Auto && _manualExpiresAt.HasValue)
                return "，至 " + _manualExpiresAt.Value.ToString("HH:mm");
            return "";
        }

        // ---------- 状态持久化 ----------

        private void LoadState(bool enableWiredOnStart)
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    foreach (string raw in File.ReadAllLines(StatePath))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "Control" && v == "Manual") Control = ControlMode.Manual;
                        else if (k == "Mode" && v == "ForceOn") CurrentMode = Mode.ForceOn;
                        else if (k == "Mode" && v == "ForceOff") CurrentMode = Mode.ForceOff;
                        else if (k == "Mode" && v == "Auto") CurrentMode = Mode.Auto;
                        else if (k == "Expires")
                        {
                            DateTime exp;
                            if (DateTime.TryParse(v, out exp)) _manualExpiresAt = exp;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取状态失败，使用默认: " + ex.Message);
            }

            // 手动模式 + 每次开机启用有线 → 开机强制有线开（该选项关闭时维持上次状态）
            if (Control == ControlMode.Manual && enableWiredOnStart) CurrentMode = Mode.ForceOn;
            // 自动控制下已过期的临时强制 → 回跟随计划
            if (Control == ControlMode.Auto && CurrentMode != Mode.Auto &&
                Config.ManualExpiresNextEvent && _manualExpiresAt.HasValue &&
                DateTime.Now >= _manualExpiresAt.Value)
            {
                CurrentMode = Mode.Auto;
                _manualExpiresAt = null;
            }
        }

        private void SaveState()
        {
            try
            {
                File.WriteAllLines(StatePath, new[]
                {
                    "Control = " + (Control == ControlMode.Manual ? "Manual" : "Auto"),
                    "Mode = " + CurrentMode,
                    "Expires = " + (_manualExpiresAt.HasValue
                        ? _manualExpiresAt.Value.ToString("yyyy-MM-dd HH:mm") : "")
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("保存状态失败: " + ex.Message);
            }
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

        /// <summary>把某个键写回配置文件（保持其余行与注释不变），保存后由监听器自动热重载。</summary>
        public void WriteConfigValue(string key, string value)
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

        /// <summary>下次有效切换的显示文本（手动模式下不自动切换）。</summary>
        public string NextBoundaryText()
        {
            if (Control == ControlMode.Manual) return "手动模式，不自动切换";
            WiredState after;
            DateTime? b = Config.NextEffectiveBoundary(DateTime.Now, out after);
            if (!b.HasValue) return "无自动切换（白名单期间）";
            return b.Value.ToString("ddd HH:mm") + " " + (after == WiredState.Off ? "断有线" : "恢复有线");
        }

        private void TickAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate { Tick(); });
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
