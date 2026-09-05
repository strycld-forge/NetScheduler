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
                Application.Run(new TrayContext());
            }
        }
    }
}
