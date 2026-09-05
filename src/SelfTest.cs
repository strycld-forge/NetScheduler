using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NetScheduler
{
    /// <summary>--selftest：对状态计算逻辑（不含 WMI/网络操作）做场景自测，结果写入 selftest-result.txt。</summary>
    public static class SelfTest
    {
        private static int _pass, _fail;
        private static readonly StringBuilder Out = new StringBuilder();

        public static void Run()
        {
            AppConfig cfg = new AppConfig(); // 默认: 周日~周四, 22:00 断, 05:00 恢复
            Check("周日21:59 有线开", WiredState.On, cfg.DesiredAuto(new DateTime(2026, 9, 6, 21, 59, 0)));
            Check("周日22:00 有线关", WiredState.Off, cfg.DesiredAuto(new DateTime(2026, 9, 6, 22, 0, 0)));
            Check("周日23:00 有线关", WiredState.Off, cfg.DesiredAuto(new DateTime(2026, 9, 6, 23, 0, 0)));
            Check("周一04:59 有线关", WiredState.Off, cfg.DesiredAuto(new DateTime(2026, 9, 7, 4, 59, 0)));
            Check("周一05:00 有线开", WiredState.On, cfg.DesiredAuto(new DateTime(2026, 9, 7, 5, 0, 0)));
            Check("周一01:00 有线关", WiredState.Off, cfg.DesiredAuto(new DateTime(2026, 9, 7, 1, 0, 0)));
            Check("周五23:00 有线开", WiredState.On, cfg.DesiredAuto(new DateTime(2026, 9, 11, 23, 0, 0)));
            Check("周六01:00 有线开", WiredState.On, cfg.DesiredAuto(new DateTime(2026, 9, 12, 1, 0, 0)));

            AppConfig wl = new AppConfig();
            wl.Whitelist.Add(new WhitelistRange { Start = new DateTime(2026, 9, 6), End = new DateTime(2026, 9, 8) });
            Check("白名单周日23:00 有线开", WiredState.On, wl.DesiredAuto(new DateTime(2026, 9, 6, 23, 0, 0)));
            Check("白名单周一02:00 有线开", WiredState.On, wl.DesiredAuto(new DateTime(2026, 9, 7, 2, 0, 0)));
            Check("白名单结束后周二22:00 有线关", WiredState.Off, wl.DesiredAuto(new DateTime(2026, 9, 9, 22, 0, 0)));

            Check("强制开 → 有线开", WiredState.On, cfg.Desired(new DateTime(2026, 9, 6, 23, 0, 0), Mode.ForceOn));
            Check("强制关 → 有线关", WiredState.Off, cfg.Desired(new DateTime(2026, 9, 7, 12, 0, 0), Mode.ForceOff));

            WiredState sa;
            DateTime? b = cfg.NextEffectiveBoundary(new DateTime(2026, 9, 7, 12, 0, 0), out sa);
            Check2("下次切换: 周一12点起 → 22:00", new DateTime(2026, 9, 7, 22, 0, 0), b);
            Check("其状态变化 → 关", WiredState.Off, sa);
            b = cfg.NextEffectiveBoundary(new DateTime(2026, 9, 7, 23, 0, 0), out sa);
            Check2("下次切换: 周一23点起 → 次日05:00", new DateTime(2026, 9, 8, 5, 0, 0), b);
            Check("其状态变化 → 开", WiredState.On, sa);

            // 周五23:00 强制开，周末两天白名单 → 自动语义下下次有效边界应为周一22:00
            AppConfig wl2 = new AppConfig();
            wl2.Whitelist.Add(new WhitelistRange { Start = new DateTime(2026, 9, 12), End = new DateTime(2026, 9, 13) });
            b = wl2.NextEffectiveBoundary(new DateTime(2026, 9, 11, 23, 0, 0), out sa);
            Check2("周五23点起(周末白名单) → 周一22:00", new DateTime(2026, 9, 14, 22, 0, 0), b);

            // 全程白名单 → 无边界
            AppConfig wl3 = new AppConfig();
            wl3.Whitelist.Add(new WhitelistRange { Start = new DateTime(2026, 9, 1), End = new DateTime(2026, 9, 30) });
            b = wl3.NextEffectiveBoundary(new DateTime(2026, 9, 7, 0, 0, 0), out sa);
            Check2("整个月白名单 → 无自动切换", null, b);

            // 配置文件解析回归测试
            string exeDir0 = AppDomain.CurrentDomain.BaseDirectory;
            string tmpCfg = Path.Combine(exeDir0, "selftest-config.ini");
            File.WriteAllText(tmpCfg,
                "# 注释\n[Schedule]\nOffDays = 5,6\nOffTime = 23:30 ; 行内注释\nOnTime = 07:30\n" +
                "[Network]\nEthernetName = 以太网\nWifiProfile = 我的热点\n" +
                "[Whitelist]\nWhitelist = 2026-01-01, 2026-01-20..2026-02-24\n" +
                "[Behavior]\nManualExpiresNextEvent = false\nCheckIntervalSec = 30\n", new UTF8Encoding(true));
            AppConfig p = AppConfig.Load(tmpCfg);
            CheckBool("解析: OffDays 含周六", true, p.OffDays.Contains(6));
            CheckBool("解析: OffDays 不含周日", true, !p.OffDays.Contains(0));
            CheckBool("解析: OffTime 23:30", true, p.GetOffTime() == new TimeSpan(23, 30, 0));
            CheckBool("解析: OnTime 07:30", true, p.GetOnTime() == new TimeSpan(7, 30, 0));
            CheckBool("解析: WifiProfile 中文", true, p.WifiProfile == "我的热点");
            CheckBool("解析: 白名单单日", true, p.IsWhitelisted(new DateTime(2026, 1, 1)));
            CheckBool("解析: 白名单区间内", true, p.IsWhitelisted(new DateTime(2026, 2, 10)));
            CheckBool("解析: 白名单区间外", false, p.IsWhitelisted(new DateTime(2026, 3, 1)));
            CheckBool("解析: ManualExpires=false", true, !p.ManualExpiresNextEvent);
            CheckBool("解析: CheckInterval=30", true, p.CheckIntervalSec == 30);
            File.Delete(tmpCfg);
            Out.AppendLine();

            Out.AppendLine();
            Out.AppendLine("通过 " + _pass + " / 失败 " + _fail);            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string file = Path.Combine(exeDir, "selftest-result.txt");
            File.WriteAllText(file, Out.ToString(), new UTF8Encoding(true));
            MessageBox.Show("通过 " + _pass + " 项, 失败 " + _fail + " 项\r\n详细结果: " + file,
                "NetScheduler 自测", MessageBoxButtons.OK,
                _fail == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static void Check(string name, WiredState expect, WiredState actual)
        {
            bool ok = expect == actual;
            if (ok) _pass++; else _fail++;
            Out.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name +
                (ok ? "" : "  期望=" + expect + " 实际=" + actual));
        }

        private static void CheckBool(string name, bool expect, bool actual)
        {
            bool ok = expect == actual;
            if (ok) _pass++; else _fail++;
            Out.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name +
                (ok ? "" : "  期望=" + expect + " 实际=" + actual));
        }

        private static void Check2(string name, DateTime? expect, DateTime? actual)
        {
            bool ok = expect.HasValue == actual.HasValue &&
                      (!expect.HasValue || expect.Value == actual.Value);
            if (ok) _pass++; else _fail++;
            Out.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name +
                (ok ? "" : "  期望=" + expect + " 实际=" + actual));
        }
    }
}
