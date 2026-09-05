using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetScheduler
{
    public enum Mode { Auto, ForceOn, ForceOff }
    public enum WiredState { On, Off }

    public static class ModeText
    {
        public static string Get(Mode m)
        {
            switch (m)
            {
                case Mode.ForceOn: return "强制有线开";
                case Mode.ForceOff: return "强制有线关";
                default: return "跟随计划";
            }
        }
    }

    public class WhitelistRange
    {
        public DateTime Start;
        public DateTime End;

        public bool Contains(DateTime d)
        {
            return d.Date >= Start && d.Date <= End;
        }
    }

    public class AppConfig
    {
        public List<int> OffDays = new List<int>(new int[] { 0, 1, 2, 3, 4 });
        public string OffTime = "22:00";
        public string OnTime = "05:00";
        public string EthernetName = "";
        public string WifiProfile = "";
        public int ConnectRetries = 5;
        public int RetryIntervalSec = 30;
        public List<WhitelistRange> Whitelist = new List<WhitelistRange>();
        public bool ManualExpiresNextEvent = true;
        public bool KeepWifiConnected = true;
        public int CheckIntervalSec = 60;
        public int LogMaxSizeKB = 1024;

        public TimeSpan GetOffTime()
        {
            TimeSpan t;
            return TimeSpan.TryParse(OffTime, out t) ? t : new TimeSpan(22, 0, 0);
        }

        public TimeSpan GetOnTime()
        {
            TimeSpan t;
            return TimeSpan.TryParse(OnTime, out t) ? t : new TimeSpan(5, 0, 0);
        }

        public bool IsOffDay(DayOfWeek d)
        {
            return OffDays.Contains((int)d);
        }

        public bool IsWhitelisted(DateTime date)
        {
            if (Whitelist == null) return false;
            foreach (WhitelistRange r in Whitelist)
                if (r.Contains(date)) return true;
            return false;
        }

        /// <summary>不考虑手动模式时的期望状态：白名单日 → 有线开；断网时段 → 有线关。</summary>
        public WiredState DesiredAuto(DateTime now)
        {
            if (IsWhitelisted(now.Date)) return WiredState.On;
            if (InOffWindow(now))
            {
                // 凌晨部分属于昨天开始的断网时段：昨天是白名单日则整段免除
                if (now.TimeOfDay < GetOnTime() && IsWhitelisted(now.Date.AddDays(-1)))
                    return WiredState.On;
                return WiredState.Off;
            }
            return WiredState.On;
        }

        /// <summary>某时刻是否处于断网时段（OffTime 当晚 → 次日 OnTime）。</summary>
        public bool InOffWindow(DateTime now)
        {
            TimeSpan off = GetOffTime(), on = GetOnTime();
            if (off == on) return false;
            // 晚间侧：当天是断网日且已过 OffTime
            if (now.TimeOfDay >= off && IsOffDay(now.DayOfWeek)) return true;
            // 凌晨侧：还没到 OnTime，且昨天是断网日
            if (now.TimeOfDay < on && IsOffDay((DayOfWeek)(((int)now.DayOfWeek + 6) % 7))) return true;
            return false;
        }

        /// <summary>最终期望状态（含手动模式）。</summary>
        public WiredState Desired(DateTime now, Mode mode)
        {
            if (mode == Mode.ForceOn) return WiredState.On;
            if (mode == Mode.ForceOff) return WiredState.Off;
            return DesiredAuto(now);
        }

        /// <summary>
        /// 下一次"期望状态真正会发生改变"的时间点（自动模式下）。
        /// 白名单/非断网日上的边界是无操作，会被跳过；向后查找 9 天。
        /// </summary>
        public DateTime? NextEffectiveBoundary(DateTime from, out WiredState stateAfter)
        {
            stateAfter = WiredState.On;
            TimeSpan off = GetOffTime(), on = GetOnTime();
            for (int d = 0; d <= 8; d++)
            {
                DateTime date = from.Date.AddDays(d);
                DateTime bOn = date + on;    // 恢复边界
                DateTime bOff = date + off;  // 断网边界
                DateTime first = bOn <= bOff ? bOn : bOff;
                DateTime second = bOn <= bOff ? bOff : bOn;
                WiredState sa;
                if (TryBoundary(first, from, out sa)) { stateAfter = sa; return first; }
                if (TryBoundary(second, from, out sa)) { stateAfter = sa; return second; }
            }
            return null;
        }

        private bool TryBoundary(DateTime b, DateTime from, out WiredState after)
        {
            after = WiredState.On;
            if (b <= from) return false;
            WiredState before = DesiredAuto(b.AddMinutes(-1));
            after = DesiredAuto(b.AddMinutes(1));
            return before != after;
        }

        // ---------- 配置文件解析 ----------

        public static AppConfig Load(string path)
        {
            AppConfig cfg = new AppConfig();
            List<WhitelistRange> wl = new List<WhitelistRange>();
            string text = ReadTextSmart(path);
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                int ci = val.IndexOf(';');
                if (ci >= 0) val = val.Substring(0, ci).Trim();
                switch (key)
                {
                    case "OffDays": cfg.OffDays = ParseDays(val); break;
                    case "OffTime": cfg.OffTime = val; break;
                    case "OnTime": cfg.OnTime = val; break;
                    case "EthernetName":
                        cfg.EthernetName = (val.Length == 0 || val == "auto" || val == "自动检测") ? "" : val;
                        break;
                    case "WifiProfile": cfg.WifiProfile = val; break;
                    case "ConnectRetries": int.TryParse(val, out cfg.ConnectRetries); break;
                    case "RetryIntervalSec": int.TryParse(val, out cfg.RetryIntervalSec); break;
                    case "Whitelist": wl = ParseWhitelist(val); break;
                    case "ManualExpiresNextEvent": cfg.ManualExpiresNextEvent = IsTrue(val); break;
                    case "KeepWifiConnected": cfg.KeepWifiConnected = IsTrue(val); break;
                    case "CheckIntervalSec": int.TryParse(val, out cfg.CheckIntervalSec); break;
                    case "LogMaxSizeKB": int.TryParse(val, out cfg.LogMaxSizeKB); break;
                }
            }
            cfg.Whitelist = wl;
            if (cfg.ConnectRetries < 1) cfg.ConnectRetries = 1;
            if (cfg.RetryIntervalSec < 5) cfg.RetryIntervalSec = 5;
            if (cfg.CheckIntervalSec < 15) cfg.CheckIntervalSec = 15;
            if (cfg.LogMaxSizeKB < 64) cfg.LogMaxSizeKB = 64;
            return cfg;
        }

        private static string ReadTextSmart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes); // ANSI/GBK 兜底
            }
        }

        private static bool IsTrue(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes" || v == "on";
        }

        private static List<int> ParseDays(string val)
        {
            List<int> days = new List<int>();
            foreach (string p in (val ?? "").Split(','))
            {
                int d;
                if (int.TryParse(p.Trim(), out d) && d >= 0 && d <= 6 && !days.Contains(d))
                    days.Add(d);
            }
            return days;
        }

        private static List<WhitelistRange> ParseWhitelist(string val)
        {
            List<WhitelistRange> list = new List<WhitelistRange>();
            foreach (string p in (val ?? "").Split(','))
            {
                string token = p.Trim();
                if (token.Length == 0) continue;
                int sep = token.IndexOf("..", StringComparison.Ordinal);
                WhitelistRange r = new WhitelistRange();
                if (sep >= 0)
                {
                    DateTime a, b;
                    if (TryParseDate(token.Substring(0, sep), out a) && TryParseDate(token.Substring(sep + 2), out b))
                    {
                        r.Start = a; r.End = b;
                        if (r.End < r.Start) { var t = r.Start; r.Start = r.End; r.End = t; }
                        list.Add(r);
                    }
                }
                else
                {
                    DateTime a;
                    if (TryParseDate(token, out a))
                    {
                        r.Start = a; r.End = a;
                        list.Add(r);
                    }
                }
            }
            return list;
        }

        private static bool TryParseDate(string s, out DateTime d)
        {
            return DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }
    }

    public static class DefaultConfig
    {
        public const string Text =
@"# ================= NetScheduler 配置文件 =================
# 修改保存后自动生效，无需重启程序。
# 以 '#' 或 ';' 开头的行为注释。
# =========================================================

[Schedule]
# 断网生效的星期，0=周日 1=周一 ... 6=周六，逗号分隔
OffDays = 0,1,2,3,4
# 断网时刻（此时刻起禁用有线网卡）
OffTime = 22:00
# 恢复时刻（此时刻起重新启用有线网卡）
OnTime = 05:00

[Network]
# 有线网卡的连接名（控制面板 - 网络连接 里显示的名字，如：以太网）
# 留空 = 自动检测（检测到后会自动写回这里）
EthernetName =
# 手机热点的 Wi-Fi 配置文件名（一般就是热点名/SSID，需先手动连接保存过一次）
WifiProfile =
# 断网时主动连接热点的重试次数与间隔（秒）；重试用尽后每分钟对账仍会继续尝试
ConnectRetries = 5
RetryIntervalSec = 30

[Whitelist]
# 白名单日期，这些日期不执行断网（全天保持有线开启）。逗号分隔，支持单日和区间：
#   单日: 2026-01-01
#   区间: 2026-01-20..2026-02-24
Whitelist =

[Behavior]
# true = 手动强制开/关 到下一个定时点后自动恢复为跟随计划（防止忘了改回来）
ManualExpiresNextEvent = true
# 断网时段内若 Wi-Fi 掉线，每分钟自动重连热点
KeepWifiConnected = true
# 状态对账间隔（秒），越小反应越快，资源占用也几乎不变
CheckIntervalSec = 60
# 日志文件大小上限（KB），超过后轮转为 .old
LogMaxSizeKB = 1024
";
    }
}
