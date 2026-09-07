using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace NetScheduler
{
    /// <summary>
    /// 隐藏窗口 + 托盘图标。菜单只创建一次，之后仅原地更新文字/勾选/图标——
    /// 重建销毁菜单会导致勾选状态不同步，也会在菜单打开时销毁句柄。
    /// 配置文件保存后自动热更新。
    /// </summary>
    public class TrayContext : Form
    {
        private readonly string _configPath;
        private readonly NotifyIcon _icon;
        private readonly Engine _engine;
        private readonly Timer _tickTimer;
        private readonly Timer _uiTimer;
        private readonly Dictionary<string, Icon> _iconCache = new Dictionary<string, Icon>();
        private long _lastFsEvent = 0; // 配置文件最近一次变更的时钟（Ticks），用于去抖
        private FileSystemWatcher _watcher;
        private ContextMenuStrip _menu;

        // 状态行（不可点，实时同步真实状态）
        private ToolStripMenuItem _stWired, _stControl, _stNext;
        // 控制模式
        private ToolStripMenuItem _miCtrlAuto, _miCtrlManual, _miBootWired;
        // 强制
        private ToolStripMenuItem _miFollow, _miForceOn, _miForceOff;

        public TrayContext()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;

            // 关键：永不显示的窗口默认没有原生句柄，BeginInvoke 会静默失败，
            // 导致菜单/气泡永远不刷新。访问 Handle 强制创建句柄。
            IntPtr forceHandle = Handle;

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(exeDir, "config.ini");
            if (!File.Exists(_configPath))
            {
                File.WriteAllText(_configPath, DefaultConfig.Text, new UTF8Encoding(true));
                Logger.Init(Path.Combine(exeDir, "netscheduler.log"), 1024);
                Logger.Info("未找到配置文件，已生成默认 config.ini");
            }

            AppConfig cfg = LoadConfigSafe();
            Logger.Init(Path.Combine(exeDir, "netscheduler.log"), cfg.LogMaxSizeKB);
            _engine = new Engine(_configPath, cfg);
            _engine.UiChanged = RefreshUi;
            _engine.NotifyUser = ShowBalloon;
            Logger.Info(string.Format("启动完成 管理员={0} 有线网卡=\"{1}\" 热点=\"{2}\" 控制={3}",
                _engine.IsAdmin, _engine.ResolvedEthernet, cfg.WifiProfile, _engine.ControlStatusText()));

            _icon = new NotifyIcon
            {
                Text = "NetScheduler",
                Visible = true
            };
            BuildMenuOnce();
            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowStatusBalloon();
            };
            _icon.MouseDoubleClick += delegate { OpenConfig(); };

            _tickTimer = new Timer { Interval = Math.Max(15, cfg.CheckIntervalSec) * 1000 };
            _tickTimer.Tick += delegate { ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); }); };
            _tickTimer.Start();

            // UI 线程定时器兜底刷新：Tick 事件在 UI 线程触发，直接读取引擎状态同步菜单，
            // 不依赖跨线程投递，保证状态行/勾选/图标永远与真实状态一致
            _uiTimer = new Timer { Interval = 1000 };
            _uiTimer.Tick += delegate { DoUpdateMenu(); };
            _uiTimer.Start();

            SetupWatcher();

            DoUpdateMenu();
            ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); });
            if (!_engine.IsAdmin)
                ShowBalloon("NetScheduler", "未以管理员身份运行，无法自动切换网卡（仅监控）");
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false); // 永不显示窗口
        }

        // ---------- 菜单（仅创建一次，动态内容在 DoUpdateMenu 中更新） ----------

        private void BuildMenuOnce()
        {
            _menu = new ContextMenuStrip();

            _stWired = new ToolStripMenuItem("有线网络") { Enabled = false };
            _stControl = new ToolStripMenuItem("控制") { Enabled = false };
            _stNext = new ToolStripMenuItem("下次切换") { Enabled = false };
            _menu.Items.AddRange(new ToolStripItem[] { _stWired, _stControl, _stNext, new ToolStripSeparator() });

            ToolStripMenuItem ctrlMenu = new ToolStripMenuItem("控制模式");
            _miCtrlAuto = new ToolStripMenuItem("自动（按计划调度，可临时强制）");
            _miCtrlAuto.Click += delegate { _engine.SetControl(ControlMode.Auto); };
            _miCtrlManual = new ToolStripMenuItem("手动（完全手动，不自动切换）");
            _miCtrlManual.Click += delegate { _engine.SetControl(ControlMode.Manual); };
            _miBootWired = new ToolStripMenuItem("手动模式下每次开机启用有线");
            _miBootWired.Click += delegate { _engine.ToggleBootWired(); };
            ctrlMenu.DropDownItems.AddRange(new ToolStripItem[]
            { _miCtrlAuto, _miCtrlManual, new ToolStripSeparator(), _miBootWired });
            _menu.Items.Add(ctrlMenu);

            ToolStripMenuItem forceMenu = new ToolStripMenuItem("强制");
            _miFollow = new ToolStripMenuItem("跟随计划");
            _miFollow.Click += delegate { _engine.SetFollow(); };
            _miForceOn = new ToolStripMenuItem("强制有线开");
            _miForceOn.Click += delegate { _engine.SetForce(Mode.ForceOn); };
            _miForceOff = new ToolStripMenuItem("强制有线关");
            _miForceOff.Click += delegate { _engine.SetForce(Mode.ForceOff); };
            forceMenu.DropDownItems.AddRange(new ToolStripItem[] { _miFollow, _miForceOn, _miForceOff });
            _menu.Items.Add(forceMenu);

            ToolStripMenuItem apply = new ToolStripMenuItem("立即对账");
            apply.Click += delegate { ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); }); };
            _menu.Items.Add(apply);
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openCfg = new ToolStripMenuItem("打开配置文件");
            openCfg.Font = new Font(openCfg.Font, FontStyle.Bold);
            openCfg.Click += delegate { OpenConfig(); };
            _menu.Items.Add(openCfg);

            ToolStripMenuItem reload = new ToolStripMenuItem("重载配置");
            reload.Click += delegate { ReloadConfig(); };
            _menu.Items.Add(reload);

            ToolStripMenuItem openLog = new ToolStripMenuItem("打开日志");
            openLog.Click += delegate { OpenFile(Logger.LogPath); };
            _menu.Items.Add(openLog);
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate
            {
                Logger.Info("用户退出");
                _icon.Visible = false;
                Close();
            };
            _menu.Items.Add(exit);

            _icon.ContextMenuStrip = _menu;
        }

        /// <summary>后台线程回调：切回 UI 线程更新菜单。</summary>
        private void RefreshUi()
        {
            try { BeginInvoke((Action)DoUpdateMenu); } catch { }
        }

        /// <summary>把引擎的真实状态同步到菜单文字/勾选与托盘图标颜色。</summary>
        private void DoUpdateMenu()
        {
            try
            {
                string wired = _engine.LastActual == WiredState.Off ? "已断开"
                    : (_engine.LastActual == WiredState.On ? "开启" : "未知");
                _stWired.Text = "有线网络: " + wired + (_engine.IsAdmin ? "" : "  (无管理员权限，仅监控)");
                _stControl.Text = "控制: " + _engine.ControlStatusText();
                _stNext.Text = "下次切换: " + _engine.NextBoundaryText();

                _miCtrlAuto.Checked = _engine.Control == ControlMode.Auto;
                _miCtrlManual.Checked = _engine.Control == ControlMode.Manual;
                _miBootWired.Checked = _engine.Config.EnableWiredOnStart;

                _miFollow.Checked = _engine.Control == ControlMode.Auto && _engine.CurrentMode == Mode.Auto;
                _miForceOn.Checked = _engine.CurrentMode == Mode.ForceOn;
                _miForceOff.Checked = _engine.CurrentMode == Mode.ForceOff;

                // 图标按颜色缓存、只创建一次，从不 Dispose（避免把已释放图标赋给 NotifyIcon 导致崩溃）
                Icon cur = _engine.LastActual == WiredState.Off
                    ? MakeIcon(Color.FromArgb(224, 64, 64))
                    : (_engine.LastActual.HasValue ? MakeIcon(Color.FromArgb(64, 176, 80)) : MakeIcon(Color.FromArgb(220, 160, 32)));
                if (!ReferenceEquals(_icon.Icon, cur)) _icon.Icon = cur;
            }
            catch { }
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                BeginInvoke((Action)delegate
                {
                    _icon.BalloonTipTitle = title;
                    _icon.BalloonTipText = text;
                    _icon.ShowBalloonTip(3000);
                });
            }
            catch { }
        }

        private void ShowStatusBalloon()
        {
            string wired = _engine.LastActual == WiredState.Off ? "已断开"
                : (_engine.LastActual == WiredState.On ? "开启" : "未知");
            ShowBalloon("NetScheduler",
                "有线网络: " + wired + "\r\n控制: " + _engine.ControlStatusText() +
                "\r\n下次切换: " + _engine.NextBoundaryText());
        }

        // ---------- 配置 ----------

        private AppConfig LoadConfigSafe()
        {
            try
            {
                return AppConfig.Load(_configPath);
            }
            catch (Exception ex)
            {
                Logger.Error("配置解析失败，沿用默认配置: " + ex.Message);
                return new AppConfig();
            }
        }

        private void ReloadConfig()
        {
            AppConfig cfg = LoadConfigSafe();
            _engine.Config = cfg;
            _engine.ResolveEthernet();
            _engine.ResolveWifiProfile();
            _tickTimer.Interval = Math.Max(15, cfg.CheckIntervalSec) * 1000;
            Logger.Info("配置已重载: 有线网卡=\"" + _engine.ResolvedEthernet + "\" 热点=\"" + cfg.WifiProfile + "\"");
            ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); });
        }

        /// <summary>配置文件变更去抖：FileSystemWatcher 回调在后台线程，去抖放在线程池做，
        /// 重载本身通过 BeginInvoke 切回 UI 线程（WinForms Timer / 控件只能在 UI 线程操作）。</summary>
        private void OnConfigChanged()
        {
            try
            {
                long now = DateTime.Now.Ticks;
                Interlocked.Exchange(ref _lastFsEvent, now);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(800);
                    if (Interlocked.CompareExchange(ref _lastFsEvent, 0, 0) == now)
                    {
                        try { BeginInvoke((Action)ReloadConfig); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("监听配置变更异常(可用菜单手动重载): " + ex.Message);
            }
        }

        private void SetupWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath), "config.ini");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += delegate { OnConfigChanged(); };
                _watcher.Renamed += delegate { OnConfigChanged(); };
                _watcher.Created += delegate { OnConfigChanged(); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Logger.Warn("配置监听启动失败(可用菜单手动重载): " + ex.Message);
            }
        }

        // ---------- 打开文件 ----------

        private void OpenConfig()
        {
            if (!File.Exists(_configPath))
                File.WriteAllText(_configPath, DefaultConfig.Text, new UTF8Encoding(true));
            OpenFile(_configPath);
        }

        private void OpenFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                ProcessStartHelper.Open(path);
            }
            catch (Exception ex)
            {
                Logger.Warn("打开文件失败: " + ex.Message);
            }
        }

        // ---------- 图标 ----------

        private Icon MakeIcon(Color color)
        {
            string key = color.ToArgb().ToString();
            Icon cached;
            if (_iconCache.TryGetValue(key, out cached)) return cached;
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, 2, 2, 12, 12);
                    using (Pen p = new Pen(Color.FromArgb(70, 70, 70))) g.DrawEllipse(p, 2, 2, 12, 12);
                }
                _iconCache[key] = Icon.FromHandle(bmp.GetHicon()).Clone() as Icon;
            }
            return _iconCache[key];
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tickTimer.Stop();
                if (_uiTimer != null) _uiTimer.Stop();
                if (_watcher != null) _watcher.Dispose();
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }

    public static class ProcessStartHelper
    {
        public static void Open(string path)
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(path);
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }
    }
}
