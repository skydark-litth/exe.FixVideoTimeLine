using System;
using System.Windows.Forms;

namespace FixVideoTimeLine
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    Log.Open();
                    Log.Err("未处理异常：" + e.ExceptionObject);
                    Log.Close();
                }
                catch { }
            };

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, AppConfig.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                Log.Close();
            }
        }
    }
}
