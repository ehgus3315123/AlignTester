using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace TMTestWpfApp
{
    public partial class AlignSettingsWindow : Window
    {
        private readonly AlignMode _mode;

        public AlignSettingsWindow(AlignMode mode, AlignSettings restore = null)
        {
            InitializeComponent();
            _mode = mode;

            if (mode == AlignMode.Coarse)
            {
                txtTitle.Text = "Coarse Align 설정";
                txtSubtitle.Text = "FindAlignKey Coarse — 0.25× → 1.0×, Normal MatchTemplate, RefineAlignKeyCenter.";
                panelCoarse.Visibility = Visibility.Visible;
                panelFine.Visibility = Visibility.Collapsed;

                txtScales.Text = "1) 0.25× 다운스케일 매칭\r\n2) 1.0× 원본 매칭\r\n3) (옵션) RefineAlignKeyCenter";
                txtPipelineHint.Text =
                    "[SearchArea] → MatchTemplate(x0.25) → fail면 중단\r\n" +
                    "  → CRect(center, template+8) → MatchTemplate(x1.0) → fail면 중단\r\n" +
                    "  → UseIntersectionPoint면 RefineAlignKeyCenter (V/H FitLine 교점)";
            }
            else
            {
                txtTitle.Text = "Fine Align 설정";
                txtSubtitle.Text = "FindAlignKey Fine — 0.25× → 1.0× (옵션 2.0×), optional ConvertToEdge 가중합산, Refine.";
                panelCoarse.Visibility = Visibility.Collapsed;
                panelFine.Visibility = Visibility.Visible;

                txtScales.Text = "1) 0.25×\r\n2) 1.0×\r\n3) (옵션) 2.0×\r\n4) (옵션) RefineAlignKeyCenter";
                txtPipelineHint.Text =
                    "[FineAlignSearchSize 창] → MatchTemplate(x0.25)\r\n" +
                    "  → CRect(center, template+8) → x1.0\r\n" +
                    "  → (옵션) CRect(center, template+8) → x2.0\r\n" +
                    "  → UseIntersectionPoint면 RefineAlignKeyCenter\r\n" +
                    "UseEdge 시 각 스케일에서 Gaussian→ConvertToEdge⊕Normal 가중 합산.";

                if (restore != null && restore.Mode == AlignMode.Fine)
                {
                    chkUseEdge.IsChecked = restore.TemplateMatchUseEdge;
                    txtEdgeWeight.Text = restore.EdgeWeight.ToString("0.##", CultureInfo.InvariantCulture);
                    txtNormalWeight.Text = restore.NormalWeight.ToString("0.##", CultureInfo.InvariantCulture);
                    txtFineSearchSize.Text = restore.FineAlignSearchSize.ToString(CultureInfo.InvariantCulture);
                    chkUseMatchScale2.IsChecked = restore.UseMatchScale2;
                }
                else
                {
                    chkUseEdge.IsChecked = true;
                    txtEdgeWeight.Text = "5";
                    txtNormalWeight.Text = "5";
                    txtFineSearchSize.Text = "2000";
                }
                UseEdge_Changed(null, null);
            }

            chkUseIntersection.IsChecked = restore?.UseIntersectionPoint ?? true;
            chkSaveImages.IsChecked = restore?.SaveProcessImages ?? true;
            UseIntersection_Changed(null, null);

            AlignKeyDir dir = restore?.KeyDir ?? AlignKeyDir.LeftTop;
            for (int i = 0; i < cboKeyDir.Items.Count; i++)
            {
                if ((cboKeyDir.Items[i] as ComboBoxItem)?.Content as string == dir.ToString())
                {
                    cboKeyDir.SelectedIndex = i;
                    break;
                }
            }
        }

        private void UseEdge_Changed(object sender, RoutedEventArgs e)
        {
            if (panelEdgeWeights != null)
                panelEdgeWeights.IsEnabled = chkUseEdge?.IsChecked == true;
        }

        private void UseIntersection_Changed(object sender, RoutedEventArgs e)
        {
            if (panelKeyDir != null)
                panelKeyDir.IsEnabled = chkUseIntersection?.IsChecked == true;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            new ModeSelectionWindow().Show();
            Close();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            var s = new AlignSettings { Mode = _mode };

            s.UseIntersectionPoint = chkUseIntersection.IsChecked == true;
            s.SaveProcessImages = chkSaveImages.IsChecked == true;

            var dirItem = cboKeyDir.SelectedItem as ComboBoxItem;
            if (!Enum.TryParse((dirItem?.Content as string) ?? "LeftTop", out AlignKeyDir keyDir))
                keyDir = AlignKeyDir.LeftTop;
            s.KeyDir = keyDir;

            if (_mode == AlignMode.Fine)
            {
                s.TemplateMatchUseEdge = chkUseEdge.IsChecked == true;

                if (!double.TryParse(txtEdgeWeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ew) || ew < 0)
                {
                    MessageBox.Show("Edge Weight는 0 이상의 실수여야 합니다.");
                    return;
                }
                if (!double.TryParse(txtNormalWeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double nw) || nw < 0)
                {
                    MessageBox.Show("Normal Weight는 0 이상의 실수여야 합니다.");
                    return;
                }
                if (s.TemplateMatchUseEdge && ew + nw <= 0)
                {
                    MessageBox.Show("Edge/Normal Weight 합이 0보다 커야 합니다.");
                    return;
                }
                if (!int.TryParse(txtFineSearchSize.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fss) || fss < 1)
                {
                    MessageBox.Show("FineAlignSearchSize는 1 이상의 정수여야 합니다.");
                    return;
                }
                s.EdgeWeight = ew;
                s.NormalWeight = nw;
                s.FineAlignSearchSize = fss;
                s.UseMatchScale2 = chkUseMatchScale2.IsChecked == true;
            }

            AlignSettings.Current = s;
            new MainWindow().Show();
            Close();
        }
    }
}
