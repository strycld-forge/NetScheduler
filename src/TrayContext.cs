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
    /// 隐藏窗口 + 托盘图标：右键菜单（状态显示 / 手动模式 / 立即应用 / 配置 / 日志 / 退出），
    /// 配置文件保存后自动热更新。
    /// </summary>
    public class TrayContext : Form
    {
        private readonly string _configPath;
        private readonly NotifyIcon _icon;
        private readonly Engine _engine;
        private readonly Timer _tickTimer;
        private readonly Timer _debounceTimer;
        private readonly Dictionary<string, Icon> _iconCache = new Dictionary<string, Icon>();
        private FileSystemWatcher _watcher;
        private ContextMenuStrip _menu;

        public TrayContext()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;

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
            Logger.Info(string.Format("启动完成 管理员={0} 有线网卡=\"{1}\" 热点=\"{2}\"",
                _engine.IsAdmin, _engine.ResolvedEthernet, cfg.WifiProfile));

            _icon = new NotifyIcon
            {
                Text = "NetScheduler",
                Visible = true
            };
            _icon.ContextMenuStrip = BuildMenu();
            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowStatusBalloon();
            };
            _icon.MouseDoubleClick += delegate { OpenConfig(); };

            _tickTimer = new Timer { Interval = Math.Max(15, cfg.CheckIntervalSec) * 1000 };
            _tickTimer.Tick += delegate { ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); }); };
            _tickTimer.Start();

            _debounceTimer = new Timer { Interval = 800 };
            _debounceTimer.Tick += delegate
            {
                _debounceTimer.Stop();
                ReloadConfig();
            };

            SetupWatcher();

            ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); });
            if (!_engine.IsAdmin)
                ShowBalloon("NetScheduler", "未以管理员身份运行，无法自动切换网卡（仅监控）");
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false); // 永不显示窗口
        }

        // ---------- 菜单 ----------

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            string wired;
            switch (_engine.LastActual)
            {
                case WiredState.On: wired = "开启"; break;
                case WiredState.Off: wired = "已断开"; break;
                default: wired = "未知"; break;
            }

            ToolStripMenuItem st1 = new ToolStripMenuItem("有线网络: " + wired +
                (_engine.IsAdmin ? "" : "  (无管理员权限，仅监控)")) { Enabled = false };
            ToolStripMenuItem st2 = new ToolStripMenuItem("模式: " + ModeText.Get(_engine.CurrentMode) +
                _engine.ManualExpiresText()) { Enabled = false };
            ToolStripMenuItem st3 = new ToolStripMenuItem("下次切换: " + _engine.NextBoundaryText()) { Enabled = false };
            menu.Items.Add(st1);
            menu.Items.Add(st2);
            menu.Items.Add(st3);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mode = new ToolStripMenuItem("模式");
            ToolStripMenuItem miAuto = new ToolStripMenuItem("跟随计划");
            ToolStripMenuItem miOn = new ToolStripMenuItem("强制有线开");
            ToolStripMenuItem miOff = new ToolStripMenuItem("强制有线关");
            miAuto.Checked = _engine.CurrentMode == Mode.Auto;
            miOn.Checked = _engine.CurrentMode == Mode.ForceOn;
            miOff.Checked = _engine.CurrentMode == Mode.ForceOff;
            miAuto.Click += delegate { _engine.SetMode(Mode.Auto); };
            miOn.Click += delegate { _engine.SetMode(Mode.ForceOn); };
            miOff.Click += delegate { _engine.SetMode(Mode.ForceOff); };
            mode.DropDownItems.Add(miAuto);
            mode.DropDownItems.Add(miOn);
            mode.DropDownItems.Add(miOff);
            menu.Items.Add(mode);

            ToolStripMenuItem apply = new ToolStripMenuItem("立即对账");
            apply.Click += delegate { ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); }); };
            menu.Items.Add(apply);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openCfg = new ToolStripMenuItem("打开配置文件");
            openCfg.Font = new Font(openCfg.Font, FontStyle.Bold);
            openCfg.Click += delegate { OpenConfig(); };
            menu.Items.Add(openCfg);

            ToolStripMenuItem reload = new ToolStripMenuItem("重载配置");
            reload.Click += delegate { ReloadConfig(); };
            menu.Items.Add(reload);

            ToolStripMenuItem openLog = new ToolStripMenuItem("打开日志");
            openLog.Click += delegate { OpenFile(Logger.LogPath); };
            menu.Items.Add(openLog);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate
            {
                Logger.Info("用户退出");
                _icon.Visible = false;
                Close();
            };
            menu.Items.Add(exit);

            Icon cur = _engine.LastActual == WiredState.Off
                ? MakeIcon(Color.FromArgb(224, 64, 64))
                : (_engine.LastActual.HasValue ? MakeIcon(Color.FromArgb(64, 176, 80)) : MakeIcon(Color.FromArgb(220, 160, 32)));
            if (_icon.Icon != null) { try { _icon.Icon.Dispose(); } catch { } }
            _icon.Icon = cur;

            ContextMenuStrip old = _menu;
            _menu = menu;
            if (old != null)
            {
                try { old.Dispose(); } catch { }
            }
            return menu;
        }

        private void RefreshUi()
        {
            try
            {
                BeginInvoke((Action)delegate
                {
                    try { BuildMenu(); } catch { }
                });
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
            string wired = _engine.LastActual == WiredState.Off ? "已断开" :
                (_engine.LastActual == WiredState.On ? "开启" : "未知");
            ShowBalloon("NetScheduler",
                "有线网络: " + wired + "\r\n模式: " + ModeText.Get(_engine.CurrentMode) +
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
            _engine.CurrentMode = Mode.Auto; // 配置变更后回到跟随计划，避免旧强制状态残留
            _tickTimer.Interval = Math.Max(15, cfg.CheckIntervalSec) * 1000;
            Logger.Info("配置已重载: 有线网卡=\"" + _engine.ResolvedEthernet + "\" 热点=\"" + cfg.WifiProfile + "\"");
            ThreadPool.QueueUserWorkItem(delegate { _engine.Tick(); });
        }

        private void SetupWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath), "config.ini");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += delegate { _debounceTimer.Stop(); _debounceTimer.Start(); };
                _watcher.Renamed += delegate { _debounceTimer.Stop(); _debounceTimer.Start(); };
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
                _debounceTimer.Stop();
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
