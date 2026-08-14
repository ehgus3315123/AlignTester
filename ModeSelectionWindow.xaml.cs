using System.Windows;

namespace TMTestWpfApp
{
    public partial class ModeSelectionWindow : Window
    {
        public ModeSelectionWindow()
        {
            InitializeComponent();
        }

        private void Coarse_Click(object sender, RoutedEventArgs e)
        {
            OpenMain(AlignMode.Coarse);
        }

        private void Fine_Click(object sender, RoutedEventArgs e)
        {
            OpenMain(AlignMode.Fine);
        }

        private void OpenMain(AlignMode mode)
        {
            // 같은 모드면 기존 설정 유지, 모드가 바뀌면 기본값
            if (AlignSettings.Current == null || AlignSettings.Current.Mode != mode)
                AlignSettings.Current = AlignSettings.CreateDefault(mode);
            else
                AlignSettings.Current.Mode = mode;

            new MainWindow().Show();
            Close();
        }
    }
}
