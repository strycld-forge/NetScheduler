using System;
using System.IO;

namespace NetScheduler
{
    /// <summary>带大小上限的滚动日志（超过上限时轮转为 .old，防止无限膨胀）。</summary>
    public static class Logger
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static long _maxSize = 1024 * 1024;

        public static string LogPath { get { return _path; } }

        public static void Init(string file, int maxKb)
        {
            lock (Gate)
            {
                _path = file;
                if (maxKb > 0) _maxSize = maxKb * 1024L;
            }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            if (_path == null) return;
            try
            {
                lock (Gate)
                {
                    try
                    {
                        if (File.Exists(_path) && new FileInfo(_path).Length > _maxSize)
                        {
                            string old = _path + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(_path, old);
                        }
                    }
                    catch { }
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
