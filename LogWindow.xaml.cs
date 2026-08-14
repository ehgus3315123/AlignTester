using System.Windows;
using System.Windows.Documents;

namespace TMTestWpfApp
{
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
        }

        public void SetText(string text)
        {
            var p = new Paragraph(new Run(text ?? ""))
            {
                LineHeight = 30,
                Margin = new Thickness(0)
            };
            txtLog.Document.Blocks.Clear();
            txtLog.Document.Blocks.Add(p);
            // ponytail: PageWidth 고정으로 wrap 끔. 긴 줄은 가로 스크롤.
            txtLog.Document.PageWidth = 20000;
            txtLog.ScrollToEnd();
        }
    }
}
