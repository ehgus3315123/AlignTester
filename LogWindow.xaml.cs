using System.Collections.Generic;
using System.Windows;

namespace TMTestWpfApp
{
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
        }

        public void SetEntries(List<LogEntry> entries)
        {
            dgLog.ItemsSource = null;
            dgLog.ItemsSource = entries?.ConvertAll(e => new LogRow(e));
            if (dgLog.Items.Count > 0)
                dgLog.ScrollIntoView(dgLog.Items[dgLog.Items.Count - 1]);
        }
    }

    public class LogRow
    {
        public LogLevel Level { get; }
        public string TimeStr { get; }
        public string Message { get; }

        public LogRow(LogEntry e)
        {
            Level = e.Level;
            TimeStr = e.Time == System.DateTime.MinValue ? "" : e.Time.ToString("HH:mm:ss.fff");
            Message = e.Message;
        }
    }
}
