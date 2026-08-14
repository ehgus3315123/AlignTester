using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace TMTestWpfApp
{
    public partial class App : Application
    {
        private static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TMTestWpfApp", "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                WriteCrash("DispatcherUnhandledException", args.Exception);
                MessageBox.Show("예기치 않은 오류:\n" + args.Exception.Message, "TMTestWpfApp");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                WriteCrash("UnhandledException", args.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                WriteCrash("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };
        }

        private static void WriteCrash(string kind, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath));
                var sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + kind + " ====");
                sb.AppendLine(ex == null ? "(null)" : ex.ToString());
                sb.AppendLine();
                File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }
    }
}
