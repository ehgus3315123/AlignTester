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
                txtSubtitle.Text = "FindAlignKey Fine — 0.25× → 1.0×, ConvertToEdge 가중합산, Refine.";
                panelCoarse.Visibility = Visibility.Collapsed;
                panelFine.Visibility = Visibility.Visible;

                txtScales.Text = "1) 0.25×\r\n2) 1.0×\r\n3) (옵션) RefineAlignKeyCenter";
                txtPipelineHint.Text =
                    "[FineAlignSearchSize 창] → MatchTemplate(x0.25)\r\n" +
                    "  → CRect(center, template+8) → x1.0\r\n" +
                    "  → UseIntersectionPoint면 RefineAlignKeyCenter\r\n" +
                    "각 스케일에서 Gaussian→ConvertToEdge⊕Normal 가중 합산.";

                if (restore != null && restore.Mode == AlignMode.Fine)
                {
                    txtEdgeWeight.Text = restore.EdgeWeight.ToString("0.##", CultureInfo.InvariantCulture);
                    txtNormalWeight.Text = restore.NormalWeight.ToString("0.##", CultureInfo.InvariantCulture);
                    txtFineSearchSize.Text = restore.FineAlignSearchSize.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    txtEdgeWeight.Text = "5";
                    txtNormalWeight.Text = "5";
                    txtFineSearchSize.Text = "2000";
                }
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

            // RefineZoom: 0→index0, 2→index1, 4→index2
            int zoom = restore?.RefineZoomFactor ?? 0;
            cboRefineZoom.SelectedIndex = zoom == 4 ? 2 : zoom == 2 ? 1 : 0;

            txtEdgeLevel.Text = (restore?.EdgeRefineLevel ?? 60).ToString(CultureInfo.InvariantCulture);
            txtEdgeInterval.Text = (restore?.EdgeRefineInterval ?? 3).ToString(CultureInfo.InvariantCulture);
            txtEdgeSearchRatio.Text = (restore?.EdgeSearchRatio ?? 0.6667).ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void UseIntersection_Changed(object sender, RoutedEventArgs e)
        {
            if (panelRefineParams != null)
                panelRefineParams.IsEnabled = chkUseIntersection?.IsChecked == true;
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

            int[] zoomMap = { 0, 2, 4 };
            s.RefineZoomFactor = zoomMap[Math.Max(0, Math.Min(cboRefineZoom.SelectedIndex, 2))];

            if (!int.TryParse(txtEdgeLevel.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int el) || el < 0 || el > 255)
            {
                MessageBox.Show("EdgeLevel은 0〜255 정수여야 합니다.");
                return;
            }
            if (!int.TryParse(txtEdgeInterval.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ei) || ei < 1)
            {
                MessageBox.Show("EdgeInterval은 1 이상의 정수여야 합니다.");
                return;
            }
            if (!double.TryParse(txtEdgeSearchRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double esr) || esr <= 0 || esr > 1)
            {
                MessageBox.Show("EdgeSearchRatio는 0 초과 1 이하의 실수여야 합니다.");
                return;
            }
            s.EdgeRefineLevel = el;
            s.EdgeRefineInterval = ei;
            s.EdgeSearchRatio = esr;

            if (_mode == AlignMode.Fine)
            {
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
                if (ew + nw <= 0)
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
            }

            AlignSettings.Current = s;
            new MainWindow().Show();
            Close();
        }
    }
}
