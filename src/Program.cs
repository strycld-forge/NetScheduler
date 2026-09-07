using System;
using System.Threading;
using System.Windows.Forms;

namespace NetScheduler
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool selftest = args != null &&
                (Array.IndexOf(args, "--selftest") >= 0 || Array.IndexOf(args, "-selftest") >= 0);
            if (selftest)
            {
                SelfTest.Run(); // 自测不占用单实例锁，便于与正在运行的托盘程序共存
                return;
            }
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "NetScheduler_SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 全局异常兜底：记录日志并继续运行，避免任何未预期异常杀掉托盘进程
                // （否则一旦崩溃，被禁用的有线网卡就没人负责恢复了）
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Logger.Error("UI线程异常(已忽略继续运行): " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Logger.Error("未处理异常: " + e.ExceptionObject);
                };

                Application.Run(new TrayContext());
            }
        }
    }
}
