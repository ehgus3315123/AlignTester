using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.Win32;

namespace TMTestWpfApp
{
    public partial class MainWindow : Window
    {
        private Image<Gray, byte> sourceImage;
        private Image<Gray, byte> templateImage;

        private readonly TemplateMatcher matcher = new TemplateMatcher();
        private readonly MatchingPipeline matchingPipeline;

        private AlignSettings settings;

        private readonly object _logLock = new object();
        private readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private string _logDisplay = "";
        private LogWindow _logWindow;

        private string _lastHistoryFolder;
        private string _batchRootFolder;
        private double _resultFocusX;
        private double _resultFocusY;
        private bool _hasResultFocus;
        private List<ScaleMatchResult> _lastResults;

        private readonly List<string> _targetPaths = new List<string>();
        private readonly List<string> _templatePaths = new List<string>();
        private readonly ObservableCollection<BatchJobItem> _batchItems = new ObservableCollection<BatchJobItem>();
        private CancellationTokenSource _batchCts;
        private bool _batchRunning;
        private ProcessImageQueue _lastShots;

        private const string ImageFilter = "Image files (*.png;*.bmp;*.jpg;*.jpeg)|*.png;*.bmp;*.jpg;*.jpeg|All files (*.*)|*.*";
        private static readonly string OutputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TMTestWpfApp", "MatchResult");

    private sealed class ProcessStepItem
    {
        public string StepBadge { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public Brush BadgeBrush { get; set; }
        public string FilePath { get; set; }
        public string RightFilePath { get; set; }
        public string LeftLabel { get; set; } = "대상";
        public string RightLabel { get; set; } = "템플릿";
        public double FocusX { get; set; }
        public double FocusY { get; set; }
        public bool HasFocus { get; set; }
        // ponytail: SaveProcessImages=false일 때 파일 없이 UI 표시용 인메모리 소스
        public BitmapSource InMemorySource { get; set; }
        public BitmapSource InMemoryRightSource { get; set; }
        public Visibility PairPanelVisibility =>
            string.IsNullOrEmpty(RightFilePath) && InMemoryRightSource == null
                ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SubtitleVisibility =>
            string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

        private sealed class BatchJobItem : INotifyPropertyChanged
        {
            private string _status = "대기";
            private string _detail = "";
            private Brush _statusBrush = Brushes.Gray;

            public string TargetPath { get; set; }
            public string TemplatePath { get; set; }
            public string FileName { get; set; }
            public string HistoryFolder { get; set; }

            public string Status
            {
                get => _status;
                set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
            }

            public string Detail
            {
                get => _detail;
                set { _detail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail))); }
            }

            public Brush StatusBrush
            {
                get => _statusBrush;
                set { _statusBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush))); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static readonly Brush StepBadgeBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x5C, 0x7A));
        private static readonly Brush BatchWaitBrush = Brushes.Gray;
        private static readonly Brush BatchRunBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x5C, 0x7A));
        private static readonly Brush BatchOkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly Brush BatchFailBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        private static readonly Brush BatchStopBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x6C, 0x00));

        static MainWindow()
        {
            StepBadgeBrush.Freeze();
            BatchRunBrush.Freeze();
            BatchOkBrush.Freeze();
            BatchFailBrush.Freeze();
            BatchStopBrush.Freeze();
        }

        public MainWindow()
        {
            InitializeComponent();
            matchingPipeline = new MatchingPipeline(matcher);
            matcher.Logger = AppendLog;
            AlignKeyRefiner.SelfCheck();
            lstBatchTargets.ItemsSource = _batchItems;

            settings = AlignSettings.Current;
            if (settings == null)
            {
                // 안전 가드: 직접 MainWindow를 열면 모드 선택 화면으로 돌려보냄
                Loaded += (_, __) =>
                {
                    new ModeSelectionWindow().Show();
                    Close();
                };
                return;
            }

            LoadSettingsToPanel();
            RefreshSettingsBanner();
        }

        private void LoadSettingsToPanel()
        {
            bool fine = settings.Mode == AlignMode.Fine;
            panelFineSettings.Visibility = fine ? Visibility.Visible : Visibility.Collapsed;
            panelCoarseSettings.Visibility = fine ? Visibility.Collapsed : Visibility.Visible;

            txtSettingsMode.Text = fine ? "Fine Align" : "Coarse Align";
            txtSettingsSubtitle.Text = fine
                ? "0.25× → 1.0× · Edge · Refine"
                : "0.25× → 1.0× · Normal Match · Refine";
            txtPipelineHint.Text = fine
                ? "SearchSize 창 → x0.25 → ROI(template+8) → x1.0 → (옵션) FitLine 교점 Refine\nEdge⊕Normal 가중 합산"
                : "전체 탐색 → x0.25 → ROI(template+8) → x1.0 → (옵션) FitLine 교점 Refine";

            chkUseIntersection.IsChecked = settings.UseIntersectionPoint;
            cboRefineZoom.SelectedIndex = settings.RefineZoomFactor == 2 ? 1 : settings.RefineZoomFactor >= 4 ? 2 : 0;
            txtEdgeLevel.Text = settings.EdgeRefineLevel.ToString(CultureInfo.InvariantCulture);
            txtEdgeInterval.Text = settings.EdgeRefineInterval.ToString(CultureInfo.InvariantCulture);
            txtEdgeSearchRatio.Text = settings.EdgeSearchRatio.ToString("0.####", CultureInfo.InvariantCulture);
            chkSaveImages.IsChecked = settings.SaveProcessImages;
            UseIntersection_Changed(null, null);

            for (int i = 0; i < cboKeyDir.Items.Count; i++)
            {
                if ((cboKeyDir.Items[i] as ComboBoxItem)?.Content as string == settings.KeyDir.ToString())
                {
                    cboKeyDir.SelectedIndex = i;
                    break;
                }
            }

            if (fine)
            {
                txtEdgeWeight.Text = settings.EdgeWeight.ToString("0.##", CultureInfo.InvariantCulture);
                txtNormalWeight.Text = settings.NormalWeight.ToString("0.##", CultureInfo.InvariantCulture);
                txtFineSearchSize.Text = settings.FineAlignSearchSize.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void RefreshSettingsBanner()
        {
            if (settings == null) return;
            txtModeBanner.Text = AlignPipeline.DescribeFixedPipeline(settings);
            AlignSettings.Current = settings;
        }

        /// <summary>사이드바 값을 settings에 반영. 실패 시 false.</summary>
        private bool TryApplySettingsFromPanel(out string error)
        {
            error = null;
            if (settings == null) return false;

            settings.UseIntersectionPoint = chkUseIntersection.IsChecked == true;
            settings.RefineZoomFactor = cboRefineZoom.SelectedIndex == 1 ? 2 : cboRefineZoom.SelectedIndex == 2 ? 4 : 0;
            settings.SaveProcessImages = chkSaveImages.IsChecked == true;

            var dirItem = cboKeyDir.SelectedItem as ComboBoxItem;
            if (!Enum.TryParse((dirItem?.Content as string) ?? "LeftTop", out AlignKeyDir keyDir))
                keyDir = AlignKeyDir.LeftTop;
            settings.KeyDir = keyDir;

            if (!int.TryParse(txtEdgeLevel.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int el) || el < 0 || el > 255)
            {
                error = "EdgeLevel은 0〜255 정수여야 합니다.";
                return false;
            }
            if (!int.TryParse(txtEdgeInterval.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ei) || ei < 1)
            {
                error = "EdgeInterval은 1 이상의 정수여야 합니다.";
                return false;
            }
            if (!double.TryParse(txtEdgeSearchRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double esr) || esr <= 0 || esr > 1)
            {
                error = "EdgeSearchRatio는 0 초과 1 이하의 실수여야 합니다.";
                return false;
            }
            settings.EdgeRefineLevel = el;
            settings.EdgeRefineInterval = ei;
            settings.EdgeSearchRatio = esr;

            if (settings.Mode == AlignMode.Fine)
            {
                if (!double.TryParse(txtEdgeWeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ew) || ew < 0)
                {
                    error = "Edge Weight는 0 이상의 실수여야 합니다.";
                    return false;
                }
                if (!double.TryParse(txtNormalWeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double nw) || nw < 0)
                {
                    error = "Normal Weight는 0 이상의 실수여야 합니다.";
                    return false;
                }
                if (ew + nw <= 0)
                {
                    error = "Edge/Normal Weight 합이 0보다 커야 합니다.";
                    return false;
                }
                if (!int.TryParse(txtFineSearchSize.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fss) || fss < 1)
                {
                    error = "FineAlignSearchSize는 1 이상의 정수여야 합니다.";
                    return false;
                }
                settings.EdgeWeight = ew;
                settings.NormalWeight = nw;
                settings.FineAlignSearchSize = fss;
            }

            RefreshSettingsBanner();
            return true;
        }

        private void UseIntersection_Changed(object sender, RoutedEventArgs e)
        {
            bool on = chkUseIntersection?.IsChecked == true;
            if (panelKeyDir != null) panelKeyDir.IsEnabled = on;
        }

        private void txtEdgeLevel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtEdgeLevel.Text, out int v)) v = 60;
            txtEdgeLevel.Text = Math.Max(0, Math.Min(255, v)).ToString();
        }

        private void txtEdgeInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtEdgeInterval.Text, out int v)) v = 3;
            txtEdgeInterval.Text = Math.Max(1, Math.Min(100, v)).ToString();
        }

        private void txtEdgeSearchRatio_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtEdgeSearchRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) v = 0.6667;
            txtEdgeSearchRatio.Text = Math.Max(0.01, Math.Min(1.0, v)).ToString("0.####", CultureInfo.InvariantCulture);
        }

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject([In] IntPtr hObject);

        private BitmapSource ToBitmapSource(Mat mat)
        {
            if (mat == null || mat.IsEmpty) return null;
            using (var bitmap = mat.ToBitmap())
            {
                IntPtr h = bitmap.GetHbitmap();
                try
                {
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                finally { DeleteObject(h); }
            }
        }

        private static Image<Gray, byte> LoadImageAsGray(string path)
        {
            using (var mat = CvInvoke.Imread(path, ImreadModes.Color))
            {
                if (mat == null || mat.IsEmpty) return null;
                if (mat.NumberOfChannels == 3)
                {
                    using (Mat gray = new Mat())
                    {
                        CvInvoke.CvtColor(mat, gray, ColorConversion.Bgr2Gray);
                        return gray.ToImage<Gray, byte>();
                    }
                }
                return mat.ToImage<Gray, byte>();
            }
        }

        // ---- Navigation ----

        private void BackToSettings_Click(object sender, RoutedEventArgs e)
        {
            TryApplySettingsFromPanel(out _);
            new ModeSelectionWindow().Show();
            Close();
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            string folder = !string.IsNullOrEmpty(_batchRootFolder) && Directory.Exists(_batchRootFolder)
                ? _batchRootFolder
                : _lastHistoryFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return;
            Process.Start("explorer.exe", folder);
        }

        private string _stepForcePath;

        private void ProcessStep_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstProcessSteps.SelectedItem is ProcessStepItem item)
                ShowProcessStep(item);
            _stepForcePath = null;
        }

        private void ProcessStepPart_Click(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            string path = fe?.Tag as string;
            var item = fe?.DataContext as ProcessStepItem;
            if (item == null || string.IsNullOrEmpty(path)) return;
            e.Handled = true;
            _stepForcePath = path;
            if (ReferenceEquals(lstProcessSteps.SelectedItem, item))
            {
                ShowProcessStep(item);
                _stepForcePath = null;
            }
            else
                lstProcessSteps.SelectedItem = item;
        }

        private void ShowProcessStep(ProcessStepItem item)
        {
            if (item == null) return;

            // 인메모리 소스 우선 (SaveProcessImages=false일 때)
            bool forceRight = !string.IsNullOrEmpty(_stepForcePath)
                && _stepForcePath == item.RightFilePath;
            BitmapSource memSrc = forceRight ? item.InMemoryRightSource
                                              : (item.InMemorySource ?? item.InMemoryRightSource);
            if (memSrc != null && string.IsNullOrEmpty(_stepForcePath))
            {
                bool useFocus = item.HasFocus;
                if (useFocus)
                    ResultImageView.SetSourceWithFocus(memSrc, item.FocusX, item.FocusY);
                else
                    ResultImageView.SetSourceWithFocus(memSrc, memSrc.PixelWidth * 0.5, memSrc.PixelHeight * 0.5);
                return;
            }

            string path = !string.IsNullOrEmpty(_stepForcePath) ? _stepForcePath : item.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                using (var mat = CvInvoke.Imread(path, ImreadModes.Color))
                {
                    if (mat == null || mat.IsEmpty) return;
                    var bmp = ToBitmapSource(mat);
                    bool useFocus = item.HasFocus && path == item.FilePath;
                    if (useFocus)
                        ResultImageView.SetSourceWithFocus(bmp, item.FocusX, item.FocusY);
                    else if (_hasResultFocus
                        && sourceImage != null
                        && mat.Width == sourceImage.Width
                        && mat.Height == sourceImage.Height)
                        ResultImageView.SetSourceWithFocus(bmp, _resultFocusX, _resultFocusY);
                    else
                        ResultImageView.SetSourceWithFocus(bmp, mat.Width * 0.5, mat.Height * 0.5);
                }
            }
            catch
            {
                // ignore preview failure
            }
        }

        // ---- Image loading ----

        private void LoadSource_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = ImageFilter,
                Multiselect = true,
                Title = "대상 이미지 선택 (여러 장 가능)"
            };
            if (ofd.ShowDialog() != true) return;

            SetPathList(_targetPaths, ofd.FileNames);
            if (_targetPaths.Count == 0)
            {
                MessageBox.Show("이미지 로드 실패");
                return;
            }

            if (!TryShowTarget(0))
            {
                MessageBox.Show("이미지 로드 실패");
                return;
            }

            RebuildBatchJobs();
            ResultImageView.ImageControl.Source = null;
            lstProcessSteps.ItemsSource = null;
            UpdateActionButtons();
            txtStatus.Text = DescribeLoadStatus();
            SetLogText(string.Empty);
        }

        private void LoadTemplate_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = ImageFilter,
                Multiselect = true,
                Title = "템플릿 이미지 선택 (여러 장 가능)"
            };
            if (ofd.ShowDialog() != true) return;

            SetPathList(_templatePaths, ofd.FileNames);
            if (_templatePaths.Count == 0)
            {
                MessageBox.Show("이미지 로드 실패");
                return;
            }

            if (!TryShowTemplate(0))
            {
                MessageBox.Show("이미지 로드 실패");
                return;
            }

            RebuildBatchJobs();
            ResultImageView.ImageControl.Source = null;
            lstProcessSteps.ItemsSource = null;
            UpdateActionButtons();
            txtStatus.Text = DescribeLoadStatus();
            SetLogText(string.Empty);
        }

        private static void SetPathList(List<string> dest, IEnumerable<string> paths)
        {
            dest.Clear();
            foreach (string path in paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                dest.Add(path);
            }
        }

        private void RebuildBatchJobs()
        {
            _batchItems.Clear();
            _batchRootFolder = null;

            // 대상 또는 템플릿이 아직 없으면 큐 비움
            if (_targetPaths.Count == 0 || _templatePaths.Count == 0)
            {
                panelBatch.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (string target in _targetPaths)
            {
                foreach (string template in _templatePaths)
                {
                    string tName = Path.GetFileName(target);
                    string mName = Path.GetFileName(template);
                    _batchItems.Add(new BatchJobItem
                    {
                        TargetPath = target,
                        TemplatePath = template,
                        FileName = _templatePaths.Count > 1 || _targetPaths.Count > 1
                            ? $"{tName}  ×  {mName}"
                            : tName,
                        Status = "대기",
                        StatusBrush = BatchWaitBrush,
                        Detail = $"{tName} ← {mName}"
                    });
                }
            }

            bool showBatch = _batchItems.Count > 1;
            panelBatch.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
            txtBatchHeader.Text = showBatch
                ? $"배치 작업 ({_batchItems.Count})  ·  대상 {_targetPaths.Count} × 템플릿 {_templatePaths.Count}"
                : "배치 작업";
            if (_batchItems.Count > 0)
                lstBatchTargets.SelectedIndex = 0;
        }

        private string DescribeLoadStatus()
        {
            string t = _targetPaths.Count > 0
                ? (_targetPaths.Count == 1
                    ? $"대상 1장 ({Path.GetFileName(_targetPaths[0])})"
                    : $"대상 {_targetPaths.Count}장")
                : "대상 없음";
            string m = _templatePaths.Count > 0
                ? (_templatePaths.Count == 1
                    ? $"템플릿 1장 ({Path.GetFileName(_templatePaths[0])})"
                    : $"템플릿 {_templatePaths.Count}장")
                : "템플릿 없음";
            int jobs = _targetPaths.Count * _templatePaths.Count;
            string job = jobs > 1 ? $" → 실행 시 {jobs}회 순차 매칭" : "";
            return $"{t}, {m}{job}";
        }

        private bool TryShowTarget(int index)
        {
            if (index < 0 || index >= _targetPaths.Count) return false;
            var img = LoadImageAsGray(_targetPaths[index]);
            if (img == null) return false;

            sourceImage?.Dispose();
            sourceImage = img;
            SourceImageView.Title = _targetPaths.Count > 1
                ? $"대상 ({index + 1}/{_targetPaths.Count})"
                : "대상";
            SourceImageView.ImageControl.Source = ToBitmapSource(sourceImage.Mat);
            return true;
        }

        private bool TryShowTemplate(int index)
        {
            if (index < 0 || index >= _templatePaths.Count) return false;
            var img = LoadImageAsGray(_templatePaths[index]);
            if (img == null) return false;

            templateImage?.Dispose();
            templateImage = img;
            TemplateImageView.Title = _templatePaths.Count > 1
                ? $"템플릿 ({index + 1}/{_templatePaths.Count})"
                : "템플릿";
            TemplateImageView.ImageControl.Source = ToBitmapSource(templateImage.Mat);
            return true;
        }

        private bool TryShowJob(BatchJobItem job)
        {
            if (job == null) return false;
            int ti = _targetPaths.FindIndex(p => string.Equals(p, job.TargetPath, StringComparison.OrdinalIgnoreCase));
            int mi = _templatePaths.FindIndex(p => string.Equals(p, job.TemplatePath, StringComparison.OrdinalIgnoreCase));
            if (ti < 0 || mi < 0) return false;
            return TryShowTarget(ti) && TryShowTemplate(mi);
        }

        private void BatchJob_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_batchRunning) return;
            if (!(lstBatchTargets.SelectedItem is BatchJobItem item)) return;
            if (!TryShowJob(item)) return;

            if (!string.IsNullOrEmpty(item.HistoryFolder) && Directory.Exists(item.HistoryFolder))
            {
                _lastHistoryFolder = item.HistoryFolder;
                LoadProcessSteps(item.HistoryFolder);
                btnOpenHistory.IsEnabled = true;
            }
        }

        private void UpdateActionButtons()
        {
            bool both = sourceImage != null && templateImage != null && settings != null;
            btnMatch.IsEnabled = !_batchRunning && both;
            btnPreviewPreprocess.IsEnabled = !_batchRunning && both;
            btnTestIntersection.IsEnabled = !_batchRunning && sourceImage != null;
            btnCropSource.IsEnabled = !_batchRunning && sourceImage != null;
            btnStopBatch.IsEnabled = _batchRunning;
        }

        // ---- Source Crop ----

        private bool _cropMode;

        private void CropSource_Click(object sender, RoutedEventArgs e)
        {
            if (sourceImage == null)
            {
                return;
            }

            _cropMode = !_cropMode;
            SourceImageView.CropMode = _cropMode;
            btnCropSource.Content = _cropMode ? "Crop 취소" : "대상 Crop";

            if (_cropMode)
            {
                SourceImageView.CropSelected += OnCropSelected;
                txtStatus.Text = "대상 이미지에서 마우스 드래그로 Crop 영역을 선택하세요.";
            }
            else
            {
                SourceImageView.CropSelected -= OnCropSelected;
                txtStatus.Text = "Crop 모드 해제.";
            }
        }

        private void OnCropSelected(object sender, Int32Rect rect)
        {
            if (sourceImage == null)
            {
                return;
            }

            var cropRect = new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
            var cropped = sourceImage.Copy(cropRect);

            sourceImage.Dispose();
            sourceImage = cropped;

            SourceImageView.ImageControl.Source = ToBitmapSource(sourceImage.Mat);
            ResultImageView.ImageControl.Source = null;

            // Crop 모드 해제
            _cropMode = false;
            SourceImageView.CropMode = false;
            SourceImageView.CropSelected -= OnCropSelected;
            btnCropSource.Content = "대상 Crop";

            UpdateActionButtons();
            txtStatus.Text = $"Crop 완료: {sourceImage.Width} x {sourceImage.Height}";
            SetLogText(string.Empty);
        }

        // ---- Param reading ----

        private bool TryReadCommonParams(out TemplateMatchType matchType, out double threshold, out string error)
        {
            matchType = TemplateMatchType.CCORR;
            threshold = 0.7;
            error = null;
            var typeItem = cboMatchType.SelectedItem as ComboBoxItem;
            string typeText = (typeItem?.Content as string) ?? "CCORR";
            if (!Enum.TryParse(typeText, out matchType))
            { error = $"알 수 없는 Type: {typeText}"; return false; }
            if (!double.TryParse(txtThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold))
            { error = "Threshold는 실수여야 합니다 (예: 0.7)"; return false; }
            return true;
        }

        // ---- Logging ----

        private static string Stamp(string msg) =>
            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";

        private void AppendLog(string msg) => AppendLog(msg, LogLevel.Info);

        private void AppendLog(string msg, LogLevel level)
        {
            lock (_logLock) _logEntries.Add(new LogEntry(DateTime.Now, level, msg));
        }

        private string FlushLog()
        {
            lock (_logLock)
            {
                var entries = _logEntries.ToList();
                _logEntries.Clear();
                return string.Join(Environment.NewLine, entries.Select(e => e.ToStampedLine()));
            }
        }

        private void SetLogText(string text)
        {
            _logDisplay = text ?? "";
            _logWindow?.SetEntries(LogEntry.ParseLines(_logDisplay));
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow == null)
            {
                _logWindow = new LogWindow { Owner = this };
                _logWindow.Closed += (_, __) => _logWindow = null;
                _logWindow.SetEntries(LogEntry.ParseLines(_logDisplay));
                _logWindow.Show();
            }
            else
            {
                if (_logWindow.WindowState == WindowState.Minimized)
                    _logWindow.WindowState = WindowState.Normal;
                _logWindow.Activate();
            }
        }

        // ---- Match ----

        private void StopBatch_Click(object sender, RoutedEventArgs e)
        {
            _batchCts?.Cancel();
            txtStatus.Text = "중단 요청… 현재 작업 완료 후 멈춥니다.";
        }

        private async void Match_Click(object sender, RoutedEventArgs e)
        {
            if (settings == null) return;
            if (sourceImage == null || templateImage == null) return;
            if (!TryApplySettingsFromPanel(out var settingsErr))
            { txtStatus.Text = "설정 오류: " + settingsErr; return; }
            if (!TryReadCommonParams(out var matchType, out var threshold, out var commonErr))
            { txtStatus.Text = "공통 파라미터 오류: " + commonErr; return; }

            // 경로 없이 crop만 한 경우 등: 단일 실행
            if (_batchItems.Count <= 1)
            {
                string srcPath = _targetPaths.Count > 0 ? _targetPaths[0] : null;
                string tmplPath = _templatePaths.Count > 0 ? _templatePaths[0] : null;
                await RunSingleMatchAsync(matchType, threshold, srcPath, tmplPath);
                return;
            }

            await RunBatchMatchAsync(matchType, threshold);
        }

        private async Task RunSingleMatchAsync(
            TemplateMatchType matchType, double threshold, string sourcePath, string templatePath)
        {
            if (sourceImage == null || templateImage == null) return;

            FlushLog();
            _batchRootFolder = null;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            txtStatus.Text = $"매칭 중... ({settings.Mode})";
            SetLogText(string.Empty);
            lstProcessSteps.ItemsSource = null;
            _batchRunning = true;
            SetUiEnabled(false);

            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string srcName = !string.IsNullOrEmpty(sourcePath)
                ? Path.GetFileNameWithoutExtension(sourcePath) : "source";
            string tmplName = !string.IsNullOrEmpty(templatePath)
                ? Path.GetFileNameWithoutExtension(templatePath) : "template";
            string historyFolder = Path.Combine(OutputRoot,
                $"{settings.Mode}_{timeStamp}_{SanitizeFolderName(srcName)}__{SanitizeFolderName(tmplName)}");
            Directory.CreateDirectory(historyFolder);
            _lastHistoryFolder = historyFolder;

            var config = AlignPipeline.CreateConfig(settings, matchType, threshold);

            List<ScaleMatchResult> results = null;
            Exception caught = null;
            ProcessImageQueue shots = null;

            try
            {
                // UI가 표시 중인 Image와 OpenCV 백그라운드 작업이 같은 Mat을 쓰면
                // Release에서 AccessViolation으로 프로세스가 죽을 수 있음 → 클론 사용
                using (var srcClone = sourceImage.Clone())
                using (var tmplClone = templateImage.Clone())
                {
                    ProcessImageQueue bgShots = null;
                    await Task.Run(() =>
                    {
                        results = matchingPipeline.Run(srcClone, tmplClone, config, AppendLog, historyFolder, settings.SaveProcessImages, out bgShots);
                    });
                    shots = bgShots;
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            _lastShots?.Dispose();
            _lastShots = shots;

            progressBar.Visibility = Visibility.Collapsed;
            progressBar.IsIndeterminate = false;
            _batchRunning = false;
            SetUiEnabled(true);
            btnOpenHistory.IsEnabled = Directory.Exists(historyFolder);

            string logText = FlushLog();

            if (caught != null)
            {
                txtStatus.Text = "매칭 오류: " + caught.Message;
                SetLogText(logText);
                MatchHistory.WriteSummary(historyFolder, settings, config, results, logText);
                return;
            }

            if (_batchItems.Count == 1)
            {
                _batchItems[0].Status = "완료";
                _batchItems[0].StatusBrush = BatchOkBrush;
                _batchItems[0].HistoryFolder = historyFolder;
                _batchItems[0].Detail = SummarizeFound(results);
            }

            RenderResults(results, threshold, config, historyFolder, logText, shots);
        }

        private async Task RunBatchMatchAsync(TemplateMatchType matchType, double threshold)
        {
            if (_batchItems.Count == 0) return;

            FlushLog();
            _batchCts?.Dispose();
            _batchCts = new CancellationTokenSource();
            var ct = _batchCts.Token;

            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _batchRootFolder = Path.Combine(OutputRoot, $"{settings.Mode}_Batch_{timeStamp}");
            Directory.CreateDirectory(_batchRootFolder);
            _lastHistoryFolder = _batchRootFolder;

            foreach (var item in _batchItems)
            {
                item.Status = "대기";
                item.StatusBrush = BatchWaitBrush;
                item.Detail = $"{Path.GetFileName(item.TargetPath)} ← {Path.GetFileName(item.TemplatePath)}";
                item.HistoryFolder = null;
            }

            var config = AlignPipeline.CreateConfig(settings, matchType, threshold);
            var batchLog = new StringBuilder();
            batchLog.AppendLine(Stamp($"Batch start jobs={_batchItems.Count} targets={_targetPaths.Count} templates={_templatePaths.Count}"));
            batchLog.AppendLine(Stamp(AlignPipeline.DescribeFixedPipeline(settings)));

            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = false;
            progressBar.Minimum = 0;
            progressBar.Maximum = _batchItems.Count;
            progressBar.Value = 0;
            _batchRunning = true;
            SetUiEnabled(false);
            panelBatch.Visibility = Visibility.Visible;

            int done = 0;
            int ok = 0;
            int fail = 0;
            bool stopped = false;

            for (int i = 0; i < _batchItems.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    MarkRemainingStopped(i);
                    break;
                }

                var item = _batchItems[i];
                lstBatchTargets.SelectedIndex = i;
                lstBatchTargets.ScrollIntoView(item);
                item.Status = "실행중";
                item.StatusBrush = BatchRunBrush;
                item.Detail = "매칭 중…";
                txtStatus.Text = $"배치 매칭 {i + 1}/{_batchItems.Count} — {item.FileName}";
                SetLogText(batchLog.ToString());

                if (!TryShowJob(item))
                {
                    item.Status = "실패";
                    item.StatusBrush = BatchFailBrush;
                    item.Detail = "이미지 로드 실패";
                    fail++;
                    done++;
                    progressBar.Value = done;
                    batchLog.AppendLine(Stamp($"[{i + 1}] FAIL load {item.FileName}"));
                    continue;
                }

                string stemT = SanitizeFolderName(Path.GetFileNameWithoutExtension(item.TargetPath));
                string stemM = SanitizeFolderName(Path.GetFileNameWithoutExtension(item.TemplatePath));
                string historyFolder = Path.Combine(_batchRootFolder, $"{i + 1:000}_{stemT}__{stemM}");
                Directory.CreateDirectory(historyFolder);
                item.HistoryFolder = historyFolder;
                _lastHistoryFolder = historyFolder;

                FlushLog();
                List<ScaleMatchResult> results = null;
                Exception caught = null;
                ProcessImageQueue jobShots = null;

                try
                {
                    using (var srcClone = sourceImage.Clone())
                    using (var tmplClone = templateImage.Clone())
                    {
                        ProcessImageQueue bgShots = null;
                        await Task.Run(() =>
                        {
                            results = matchingPipeline.Run(srcClone, tmplClone, config, AppendLog, historyFolder, settings.SaveProcessImages, out bgShots);
                        });
                        jobShots = bgShots;
                    }
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                _lastShots?.Dispose();
                _lastShots = jobShots;

                string logText = FlushLog();

                if (ct.IsCancellationRequested)
                {
                    if (results != null)
                    {
                        RenderResults(results, threshold, config, historyFolder, logText, jobShots);
                        item.Status = "완료";
                        item.StatusBrush = BatchOkBrush;
                        item.Detail = SummarizeFound(results);
                        ok++;
                        batchLog.AppendLine(Stamp($"[{i + 1}] OK {item.FileName} {item.Detail}"));
                    }
                    else
                    {
                        item.Status = "중단";
                        item.StatusBrush = BatchStopBrush;
                        item.Detail = caught?.Message ?? "중단됨";
                        batchLog.AppendLine(Stamp($"[{i + 1}] STOP {item.FileName}"));
                    }
                    done++;
                    progressBar.Value = done;
                    MarkRemainingStopped(i + 1);
                    stopped = true;
                    break;
                }

                if (caught != null || results == null)
                {
                    item.Status = "실패";
                    item.StatusBrush = BatchFailBrush;
                    item.Detail = caught?.Message ?? "결과 없음";
                    fail++;
                    MatchHistory.WriteSummary(historyFolder, settings, config, results, logText);
                    batchLog.AppendLine(Stamp($"[{i + 1}] FAIL {item.FileName} {item.Detail}"));
                }
                else
                {
                    RenderResults(results, threshold, config, historyFolder, logText, jobShots);
                    item.Status = "완료";
                    item.StatusBrush = BatchOkBrush;
                    item.Detail = SummarizeFound(results);
                    ok++;
                    batchLog.AppendLine(Stamp($"[{i + 1}] OK {item.FileName} {item.Detail}"));
                }

                done++;
                progressBar.Value = done;
                SetLogText(batchLog.ToString());
            }

            try
            {
                File.WriteAllText(
                    Path.Combine(_batchRootFolder, "batch_summary.txt"),
                    batchLog.ToString(),
                    Encoding.UTF8);
            }
            catch { /* ponytail: summary non-fatal */ }

            progressBar.Visibility = Visibility.Collapsed;
            _batchRunning = false;
            SetUiEnabled(true);
            btnOpenHistory.IsEnabled = Directory.Exists(_batchRootFolder);

            string verb = stopped ? "중단" : "완료";
            txtStatus.Text = $"배치 {verb}: {ok} 성공 / {fail} 실패 / {_batchItems.Count} 전체. 저장: {_batchRootFolder}";
            SetLogText(batchLog.ToString());
        }

        private void MarkRemainingStopped(int fromIndex)
        {
            for (int j = fromIndex; j < _batchItems.Count; j++)
            {
                if (_batchItems[j].Status == "대기" || _batchItems[j].Status == "실행중")
                {
                    _batchItems[j].Status = "중단";
                    _batchItems[j].StatusBrush = BatchStopBrush;
                    _batchItems[j].Detail = "실행 전 중단";
                }
            }
        }

        private static string SummarizeFound(IList<ScaleMatchResult> results)
        {
            if (results == null || results.Count == 0) return "no result";
            int found = results.Count(r => r.Executed && r.IsFound);
            int exec = results.Count(r => r.Executed);
            var last = results.LastOrDefault(r => r.Executed);
            if (exec == 0) return "not executed";
            return $"Found {found}/{exec} Center=({last.CenterInOriginal.X:F1},{last.CenterInOriginal.Y:F1})";
        }

        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "item";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        // ---- IntersectionPoint only (no MatchAlignKey) ----

        private async void TestIntersectionPoint_Click(object sender, RoutedEventArgs e)
        {
            if (sourceImage == null || settings == null) return;

            var dirItem = cboKeyDir.SelectedItem as ComboBoxItem;
            if (!Enum.TryParse((dirItem?.Content as string) ?? "LeftTop", out AlignKeyDir keyDir))
                keyDir = AlignKeyDir.LeftTop;
            settings.KeyDir = keyDir;
            settings.SaveProcessImages = chkSaveImages.IsChecked == true;

            int tw = templateImage != null ? templateImage.Width : sourceImage.Width;
            int th = templateImage != null ? templateImage.Height : sourceImage.Height;
            double seedX = sourceImage.Width / 2.0;
            double seedY = sourceImage.Height / 2.0;
            double cx = seedX, cy = seedY;

            FlushLog();
            _batchRootFolder = null;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            txtStatus.Text = "IntersectionPoint 계산 중...";
            SetLogText(string.Empty);
            lstProcessSteps.ItemsSource = null;
            _batchRunning = true;
            SetUiEnabled(false);

            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string srcName = _targetPaths.Count > 0
                ? Path.GetFileNameWithoutExtension(_targetPaths[0]) : "source";
            string historyFolder = Path.Combine(OutputRoot,
                $"IntersectionPoint_{timeStamp}_{SanitizeFolderName(srcName)}");
            Directory.CreateDirectory(historyFolder);
            _lastHistoryFolder = historyFolder;

            Exception caught = null;
            ProcessImageQueue ipShots = null;
            try
            {
                using (var srcClone = sourceImage.Clone())
                {
                    ProcessImageQueue bgShots = new ProcessImageQueue(true);
                    await Task.Run(() =>
                    {
                        double x = seedX, y = seedY;
                        AlignKeyRefiner.RefineAlignKeyCenter(
                            srcClone, keyDir, tw, th, ref x, ref y, AppendLog,
                            settings.SaveProcessImages ? historyFolder : null,
                            settings.SaveProcessImages, bgShots,
                            zoomFactor: settings.RefineZoomFactor);
                        cx = x;
                        cy = y;
                    });
                    if (settings.SaveProcessImages)
                        bgShots.Flush(historyFolder);
                    ipShots = bgShots;
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            _lastShots?.Dispose();
            _lastShots = ipShots;

            progressBar.Visibility = Visibility.Collapsed;
            progressBar.IsIndeterminate = false;
            _batchRunning = false;
            SetUiEnabled(true);
            btnOpenHistory.IsEnabled = settings.SaveProcessImages && Directory.Exists(historyFolder);

            string logText = FlushLog();
            bool refined = Math.Abs(cx - seedX) > 1e-6 || Math.Abs(cy - seedY) > 1e-6;
            string summary = Stamp(
                $"[IntersectionPoint] Dir={keyDir} ROI={tw}x{th} seed=({seedX:F2},{seedY:F2}) → " +
                $"corner=({cx:F2},{cy:F2})" + (refined ? "" : " (unchanged — refine failed or skipped)"));
            string combined = string.IsNullOrEmpty(logText) ? summary : logText + Environment.NewLine + summary;

            if (settings.SaveProcessImages)
                File.WriteAllText(Path.Combine(historyFolder, "summary.txt"), combined);

            if (!settings.SaveProcessImages && ipShots != null)
                LoadProcessStepsFromQueue(ipShots);
            else
                LoadProcessSteps(historyFolder);

            if (caught != null)
            {
                txtStatus.Text = "IntersectionPoint 오류: " + caught.Message;
                SetLogText(combined);
                return;
            }

            txtStatus.Text = refined
                ? $"IntersectionPoint 완료 ({cx:F2}, {cy:F2})"
                    + (settings.SaveProcessImages ? $". 저장: {historyFolder}" : "")
                : "IntersectionPoint 실패 — 템플릿 중심 유지. 로그/결과 폴더를 확인하세요.";
            SetLogText(combined);
        }

        // ---- ConvertToEdge preview ----

        private async void PreviewPreprocess_Click(object sender, RoutedEventArgs e)
        {
            if (sourceImage == null || templateImage == null || settings == null) return;

            FlushLog();
            progressBar.Visibility = Visibility.Visible;
            txtStatus.Text = "ConvertToEdge 미리보기 생성 중...";
            SetUiEnabled(false);

            var src = sourceImage;
            var tmpl = templateImage;

            Image<Gray, byte> preSrc = null;
            Image<Gray, byte> preTmpl = null;
            Exception caught = null;

            try
            {
                Image<Gray, byte> preSrcLocal = null;
                Image<Gray, byte> preTmplLocal = null;
                await Task.Run(() =>
                {
                    using (var srcClone = src.Clone())
                    using (var tmplClone = tmpl.Clone())
                    {
                        preSrcLocal = TemplateMatcher.ConvertToEdge(srcClone);
                        preTmplLocal = TemplateMatcher.ConvertToEdge(tmplClone);
                    }
                });
                preSrc = preSrcLocal;
                preTmpl = preTmplLocal;
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            progressBar.Visibility = Visibility.Collapsed;
            SetUiEnabled(true);

            string logText = FlushLog();

            if (caught != null)
            {
                preSrc?.Dispose();
                preTmpl?.Dispose();
                txtStatus.Text = "ConvertToEdge 미리보기 오류: " + caught.Message;
                SetLogText(logText);
                return;
            }

            SourceImageView.ImageControl.Source = ToBitmapSource(preSrc.Mat);
            TemplateImageView.ImageControl.Source = ToBitmapSource(preTmpl.Mat);
            preSrc.Dispose();
            preTmpl.Dispose();

            txtStatus.Text = "ConvertToEdge 미리보기 (이미지를 다시 로드하면 원본 복귀)";
            SetLogText(logText);
        }

        // ---- Result rendering / saving ----

        private void RenderResults(List<ScaleMatchResult> results, double threshold,
            MatchingPipelineConfig config, string historyFolder, string processLog,
            ProcessImageQueue shots = null)
        {
            _lastResults = results;

            var sb = new StringBuilder();
            sb.AppendLine(Stamp("[Result] " + AlignPipeline.DescribeFixedPipeline(settings)));
            if (config.Mode == AlignMode.Fine)
            {
                sb.AppendLine(Stamp("        Edge/Normal weight: " +
                    config.EdgeWeight.ToString("0.##", CultureInfo.InvariantCulture) + " : " +
                    config.NormalWeight.ToString("0.##", CultureInfo.InvariantCulture)));
            }

            int executed = 0;
            int found = 0;

            foreach (var r in results)
            {
                if (!r.Executed)
                {
                    sb.AppendLine(Stamp($"  [x{r.Scale}] Skipped — {r.Note}"));
                    continue;
                }
                executed++;
                if (r.IsFound)
                    found++;

                string note = string.IsNullOrEmpty(r.Note) ? "" : " " + r.Note;
                if (r.EdgeScoreRaw != 0)
                {
                    sb.AppendLine(Stamp($"  [x{r.Scale}] {(r.IsFound ? "Found" : "NotFound")} Combined={r.Score:F6}/{threshold:F4} (Edge={r.EdgeScoreRaw:F6}, Normal={r.NormalScoreRaw:F6}) Time={r.ElapsedMs}ms Center=({r.CenterInOriginal.X:F1},{r.CenterInOriginal.Y:F1}) ROI=({r.RoiUsed.X},{r.RoiUsed.Y},{r.RoiUsed.Width},{r.RoiUsed.Height}){note}"));
                }
                else
                {
                    sb.AppendLine(Stamp($"  [x{r.Scale}] {(r.IsFound ? "Found" : "NotFound")} Score={r.Score:F6}/{threshold:F4} Time={r.ElapsedMs}ms Center=({r.CenterInOriginal.X:F1},{r.CenterInOriginal.Y:F1}) ROI=({r.RoiUsed.X},{r.RoiUsed.Y},{r.RoiUsed.Width},{r.RoiUsed.Height}){note}"));
                }
            }

            if (settings != null && settings.SaveProcessImages)
                SaveConvertToEdgePreview(historyFolder);

            // 줌 포커스: 마지막 실행 스케일 중심 (원본 좌표 — full-frame Step용)
            _hasResultFocus = false;
            for (int i = results.Count - 1; i >= 0; i--)
            {
                if (!results[i].Executed) continue;
                _resultFocusX = results[i].CenterInOriginal.X;
                _resultFocusY = results[i].CenterInOriginal.Y;
                _hasResultFocus = true;
                break;
            }

            string combinedLog = string.IsNullOrEmpty(processLog)
                ? sb.ToString().TrimEnd()
                : processLog + Environment.NewLine + sb.ToString().TrimEnd();

            MatchHistory.WriteSummary(historyFolder, settings, config, results, combinedLog);

            if (settings != null && !settings.SaveProcessImages && shots != null)
                LoadProcessStepsFromQueue(shots);
            else
                LoadProcessSteps(historyFolder);

            txtStatus.Text = $"[{settings.Mode}] 완료: 실행 {executed}/{results.Count}, Found {found}/{executed}"
                + (settings.SaveProcessImages ? $". 저장: {historyFolder}" : "");
            SetLogText(combinedLog);
        }

        private void SaveConvertToEdgePreview(string subDir)
        {
            if (settings?.Mode != AlignMode.Fine) return;
            if (!settings.SaveProcessImages) return;

            Image<Gray, byte> ps = null;
            Image<Gray, byte> pt = null;
            try
            {
                ps = TemplateMatcher.ConvertToEdge(sourceImage);
                pt = TemplateMatcher.ConvertToEdge(templateImage);
                SaveMatAsBmp(ps.Mat, Path.Combine(subDir, "prep_edge_source.bmp"));
                SaveMatAsBmp(pt.Mat, Path.Combine(subDir, "prep_edge_template.bmp"));
            }
            finally
            {
                ps?.Dispose();
                pt?.Dispose();
            }
        }

        private void LoadProcessSteps(string folder)
        {
            var items = new List<ProcessStepItem>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                txtProcessHeader.Text = "처리 과정";
                lstProcessSteps.ItemsSource = items;
                return;
            }

            bool hasFineSteps = File.Exists(Path.Combine(folder, "Step2_Match025.jpg"))
                || File.Exists(Path.Combine(folder, "Step2_Match025.bmp"))
                || File.Exists(Path.Combine(folder, "Step1_Edge_Target.bmp"));
            bool hasCoarseSearch = File.Exists(Path.Combine(folder, "SearchArea_x0.25.bmp"));
            bool hasRefineOnly = !hasFineSteps && !hasCoarseSearch
                && (File.Exists(Path.Combine(folder, "Step4_RefineLines.bmp"))
                    || File.Exists(Path.Combine(folder, "Step4_RefineLines.jpg")));

            if (hasRefineOnly)
            {
                txtProcessHeader.Text = "IntersectionPoint";
                AddRefineLinesStep(items, folder, "IP");
                lstProcessSteps.ItemsSource = items;
                if (items.Count > 0)
                    lstProcessSteps.SelectedIndex = 0;
                return;
            }

            if (hasFineSteps)
            {
                txtProcessHeader.Text = "Fine Align 과정";
                AddSearchAreaPair(items, folder, "1", "×0.25",
                    new[] { "Step1_SearchArea" }, new[] { "Step1_Template" });
                AddStepIfExists(items, folder, "Step1_Edge_Target.bmp", "1", "Edge 변환", "대상 이미지");
                AddStepIfExists(items, folder, "Step1_Edge_Template.bmp", "1", "Edge 변환", "템플릿 이미지");
                AddStepIfExists(items, folder, "Step2_Match025.jpg", "2", "0.25× 매칭", "검색 영역 + Bounding Box");
                AddStepIfExists(items, folder, "Step2_Match025.bmp", "2", "0.25× 매칭", "검색 영역 + Bounding Box");
                AddSearchAreaPair(items, folder, "3", "×1.0",
                    new[] { "Step3_SearchArea", "Step3_Pre1_0" }, new[] { "Step3_Template" });
                if (!AddStepIfExists(items, folder, "Step3_Match1_0.bmp", "3", "1.0× 매칭", "매칭 영역 + 빨간 중심점",
                        focus: FocusInSearchImage(1.0)))
                {
                    AddStepIfExists(items, folder, "Step3_Match1_0.jpg", "3", "1.0× 매칭", "매칭 영역 + 빨간 중심점",
                        focus: FocusInSearchImage(1.0));
                }
                AddRefineLinesStep(items, folder, "4");
            }
            else
            {
                txtProcessHeader.Text = settings?.Mode == AlignMode.Coarse ? "Coarse Align 과정" : "처리 과정";
                if (!AddSearchAreaPair(items, folder, "1", "×0.25",
                        new[] { "Step1_SearchArea" }, new[] { "Step1_Template" }))
                    AddStepIfExists(items, folder, "SearchArea_x0.25.bmp", "1", "검색 영역", "×0.25 ROI");
                AddLegacyMatch(items, folder, "x0.25", "2", "0.25× 매칭");
                AddSearchAreaPair(items, folder, "3", "×1.0",
                    new[] { "Step3_SearchArea", "Step3_Pre1_0" }, new[] { "Step3_Template" });
                AddLegacyMatch(items, folder, "x1.0", "4", "1.0× 매칭");
                AddRefineLinesStep(items, folder, "R");
            }

            lstProcessSteps.ItemsSource = items;
            if (items.Count > 0)
                lstProcessSteps.SelectedIndex = items.Count - 1;
        }

        /// <summary>스케일 매칭 이미지 좌표계의 중심 (Step3/4 줌 포커스).</summary>
        private (double x, double y)? FocusInSearchImage(double scale)
        {
            if (_lastResults == null) return null;
            for (int i = 0; i < _lastResults.Count; i++)
            {
                var r = _lastResults[i];
                if (!r.Executed || Math.Abs(r.Scale - scale) > 1e-9) continue;
                double fx = (r.MatchCenterInOriginal.X - r.RoiUsed.X) * scale;
                double fy = (r.MatchCenterInOriginal.Y - r.RoiUsed.Y) * scale;
                return (fx, fy);
            }
            return null;
        }

        private bool AddSearchAreaPair(
            List<ProcessStepItem> items, string folder, string badge, string scaleLabel,
            string[] searchStems, string[] templateStems)
        {
            bool any = false;
            any |= AddSearchAreaPairRow(items, folder, badge, "검색 영역", scaleLabel + " 원본",
                searchStems, templateStems, "_Normal.bmp");
            any |= AddSearchAreaPairRow(items, folder, badge, "검색 영역", scaleLabel + " Edge",
                searchStems, templateStems, "_Edge.bmp");
            return any;
        }

        private bool AddSearchAreaPairRow(
            List<ProcessStepItem> items, string folder, string badge, string title, string subtitle,
            string[] searchStems, string[] templateStems, string suffix)
        {
            string search = FirstExisting(folder, searchStems, suffix);
            string template = FirstExisting(folder, templateStems, suffix);
            if (search == null && template == null) return false;

            items.Add(new ProcessStepItem
            {
                StepBadge = badge,
                Title = title,
                Subtitle = subtitle,
                BadgeBrush = StepBadgeBrush,
                FilePath = search ?? template,
                RightFilePath = search != null ? template : null
            });
            return true;
        }

        private static string FirstExisting(string folder, string[] stems, string suffix)
        {
            if (stems == null) return null;
            for (int i = 0; i < stems.Length; i++)
            {
                string path = Path.Combine(folder, stems[i] + suffix);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private bool AddStepIfExists(
            List<ProcessStepItem> items, string folder, string fileName,
            string badge, string title, string subtitle,
            Brush badgeBrush = null,
            (double x, double y)? focus = null,
            string rightFileName = null)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return false;
            string rightPath = rightFileName != null ? Path.Combine(folder, rightFileName) : null;
            if (rightPath != null && !File.Exists(rightPath)) rightPath = null;
            var item = new ProcessStepItem
            {
                StepBadge = badge,
                Title = title,
                Subtitle = subtitle,
                BadgeBrush = badgeBrush ?? StepBadgeBrush,
                FilePath = path,
                RightFilePath = rightPath
            };
            if (focus.HasValue)
            {
                item.FocusX = focus.Value.x;
                item.FocusY = focus.Value.y;
                item.HasFocus = true;
            }
            items.Add(item);
            return true;
        }

        private void AddRefineLinesStep(List<ProcessStepItem> items, string folder, string badge)
        {
            AddStepIfExists(items, folder, "Step4_Refine_ROI.bmp", badge, "Refine ROI", "매칭 영역 Crop",
                rightFileName: "Step4_RefineInput.bmp");
            AddStepIfExists(items, folder, "Step4_Refine_Edges.bmp", badge, "Refine Edges", "V(파랑) / H(초록) 에지 포인트");
            AddStepIfExists(items, folder, "Step4_Refine_FitLines.bmp", badge, "Refine FitLines", "V/H FitLine + 교점(빨강)");

            string[] candidates = { "Step4_RefineLines.bmp", "Step4_RefineLines.jpg", "Step5_RefineLines.jpg" };
            foreach (var f in candidates)
            {
                if (AddStepIfExists(items, folder, f, badge, "Refine Result", "교점 중심 50×50",
                        rightFileName: "Step4_RefineInput.bmp"))
                {
                    var last = items[items.Count - 1];
                    last.LeftLabel = "Lines";
                    last.RightLabel = "Input";
                    return;
                }
            }
        }

        private void AddLegacyMatch(List<ProcessStepItem> items, string folder, string scaleTag, string badge, string title)
        {
            string match = Directory.GetFiles(folder, $"MatchResult_{scaleTag}_*.jpg").FirstOrDefault();
            if (match == null) return;
            string tag = Path.GetFileNameWithoutExtension(match);
            string verdict = tag.EndsWith("_MATCH", StringComparison.OrdinalIgnoreCase) ? "MATCH" : "NOMATCH";
            items.Add(new ProcessStepItem
            {
                StepBadge = badge,
                Title = title,
                Subtitle = verdict,
                BadgeBrush = StepBadgeBrush,
                FilePath = match
            });
        }

        /// <summary>파일 저장 없이 ProcessImageQueue의 인메모리 Mat으로 처리 과정 패널을 채운다.</summary>
        private void LoadProcessStepsFromQueue(ProcessImageQueue shots)
        {
            var items = new List<ProcessStepItem>();
            if (shots == null)
            {
                lstProcessSteps.ItemsSource = items;
                return;
            }

            bool hasFineSteps = shots.TryGet("Step2_Match025.bmp") != null;
            bool hasCoarseSearch = shots.TryGet("SearchArea_x0.25.bmp") != null;
            bool hasRefineOnly = !hasFineSteps && !hasCoarseSearch
                && shots.TryGet("Step4_RefineLines.bmp") != null;

            if (hasRefineOnly)
            {
                txtProcessHeader.Text = "IntersectionPoint";
                AddQueueRefineLinesStep(items, shots, "IP");
            }
            else if (hasFineSteps)
            {
                txtProcessHeader.Text = "Fine Align 과정";
                AddQueueSearchAreaPair(items, shots, "1", "×0.25", new[] { "Step1_SearchArea" }, new[] { "Step1_Template" });
                AddQueueItemIfExists(items, shots, "Step1_Edge_Target.bmp", "1", "Edge 변환", "대상 이미지");
                AddQueueItemIfExists(items, shots, "Step1_Edge_Template.bmp", "1", "Edge 변환", "템플릿 이미지");
                AddQueueItemIfExists(items, shots, "Step2_Match025.bmp", "2", "0.25× 매칭", "검색 영역 + Bounding Box");
                AddQueueSearchAreaPair(items, shots, "3", "×1.0", new[] { "Step3_SearchArea", "Step3_Pre1_0" }, new[] { "Step3_Template" });
                AddQueueItemIfExists(items, shots, "Step3_Match1_0.bmp", "3", "1.0× 매칭", "매칭 영역 + 빨간 중심점",
                    focus: FocusInSearchImage(1.0));
                AddQueueRefineLinesStep(items, shots, "4");
            }
            else
            {
                txtProcessHeader.Text = settings?.Mode == AlignMode.Coarse ? "Coarse Align 과정" : "처리 과정";
                AddQueueSearchAreaPair(items, shots, "1", "×0.25", new[] { "Step1_SearchArea" }, new[] { "Step1_Template" });
                AddQueueSearchAreaPair(items, shots, "3", "×1.0", new[] { "Step3_SearchArea", "Step3_Pre1_0" }, new[] { "Step3_Template" });
                AddQueueRefineLinesStep(items, shots, "R");
            }

            lstProcessSteps.ItemsSource = items;
            if (items.Count > 0)
                lstProcessSteps.SelectedIndex = items.Count - 1;
        }

        private void AddQueueSearchAreaPair(
            List<ProcessStepItem> items, ProcessImageQueue shots,
            string badge, string scaleLabel, string[] searchStems, string[] templateStems)
        {
            AddQueueSearchAreaPairRow(items, shots, badge, "검색 영역", scaleLabel + " 원본", searchStems, templateStems, "_Normal.bmp");
            AddQueueSearchAreaPairRow(items, shots, badge, "검색 영역", scaleLabel + " Edge", searchStems, templateStems, "_Edge.bmp");
        }

        private void AddQueueSearchAreaPairRow(
            List<ProcessStepItem> items, ProcessImageQueue shots,
            string badge, string title, string subtitle,
            string[] searchStems, string[] templateStems, string suffix)
        {
            BitmapSource search = FirstExistingQueue(shots, searchStems, suffix);
            BitmapSource template = FirstExistingQueue(shots, templateStems, suffix);
            if (search == null && template == null) return;
            items.Add(new ProcessStepItem
            {
                StepBadge = badge, Title = title, Subtitle = subtitle, BadgeBrush = StepBadgeBrush,
                InMemorySource = search ?? template,
                InMemoryRightSource = search != null ? template : null
            });
        }

        private BitmapSource FirstExistingQueue(ProcessImageQueue shots, string[] stems, string suffix)
        {
            if (stems == null) return null;
            for (int i = 0; i < stems.Length; i++)
            {
                var mat = shots.TryGet(stems[i] + suffix);
                if (mat != null) return ToBitmapSource(mat);
            }
            return null;
        }

        private bool AddQueueItemIfExists(
            List<ProcessStepItem> items, ProcessImageQueue shots,
            string fileName, string badge, string title, string subtitle,
            (double x, double y)? focus = null,
            string rightFileName = null)
        {
            var mat = shots.TryGet(fileName);
            if (mat == null) return false;
            var rightMat = rightFileName != null ? shots.TryGet(rightFileName) : null;
            var item = new ProcessStepItem
            {
                StepBadge = badge, Title = title, Subtitle = subtitle, BadgeBrush = StepBadgeBrush,
                InMemorySource = ToBitmapSource(mat),
                InMemoryRightSource = rightMat != null ? ToBitmapSource(rightMat) : null
            };
            if (focus.HasValue) { item.FocusX = focus.Value.x; item.FocusY = focus.Value.y; item.HasFocus = true; }
            items.Add(item);
            return true;
        }

        private void AddQueueRefineLinesStep(List<ProcessStepItem> items, ProcessImageQueue shots, string badge)
        {
            AddQueueItemIfExists(items, shots, "Step4_Refine_ROI.bmp", badge, "Refine ROI", "매칭 영역 Crop",
                rightFileName: "Step4_RefineInput.bmp");
            AddQueueItemIfExists(items, shots, "Step4_Refine_Edges.bmp", badge, "Refine Edges", "V(파랑) / H(초록) 에지 포인트");
            AddQueueItemIfExists(items, shots, "Step4_Refine_FitLines.bmp", badge, "Refine FitLines", "V/H FitLine + 교점(빨강)");

            if (!AddQueueItemIfExists(items, shots, "Step4_RefineLines.bmp", badge,
                    "Refine Result", "교점 중심 50×50", rightFileName: "Step4_RefineInput.bmp"))
                return;
            var last = items[items.Count - 1];
            last.LeftLabel = "Lines";
            last.RightLabel = "Input";
        }

        private void SetUiEnabled(bool enabled)
        {
            bool imagesReady = sourceImage != null && templateImage != null && settings != null;
            btnMatch.IsEnabled = enabled && imagesReady && !_batchRunning;
            btnPreviewPreprocess.IsEnabled = enabled && imagesReady && !_batchRunning;
            btnTestIntersection.IsEnabled = enabled && sourceImage != null && !_batchRunning;
            btnLoadSource.IsEnabled = enabled && !_batchRunning;
            btnLoadTemplate.IsEnabled = enabled && !_batchRunning;
            btnBackToSettings.IsEnabled = enabled && !_batchRunning;
            btnCropSource.IsEnabled = enabled && !_batchRunning && sourceImage != null;
            cboMatchType.IsEnabled = enabled && !_batchRunning;
            txtThreshold.IsEnabled = enabled && !_batchRunning;
            txtEdgeWeight.IsEnabled = enabled && !_batchRunning;
            txtNormalWeight.IsEnabled = enabled && !_batchRunning;
            txtFineSearchSize.IsEnabled = enabled && !_batchRunning;
            chkUseIntersection.IsEnabled = enabled && !_batchRunning;
            panelKeyDir.IsEnabled = enabled && !_batchRunning && chkUseIntersection?.IsChecked == true;
            chkSaveImages.IsEnabled = enabled && !_batchRunning;
            btnStopBatch.IsEnabled = _batchRunning;
            bool histOk = (!string.IsNullOrEmpty(_lastHistoryFolder) && Directory.Exists(_lastHistoryFolder))
                || (!string.IsNullOrEmpty(_batchRootFolder) && Directory.Exists(_batchRootFolder));
            btnOpenHistory.IsEnabled = enabled && histOk;
        }

        private static void SaveMatAsBmp(Mat mat, string filePath)
        {
            if (mat == null || mat.IsEmpty) return;
            CvInvoke.Imwrite(filePath, mat);
        }
    }

    public enum LogLevel { Info, Error }

    public class LogEntry
    {
        public DateTime Time { get; }
        public LogLevel Level { get; }
        public string Message { get; }

        public LogEntry(DateTime time, LogLevel level, string message)
        {
            Time = time;
            Level = level;
            Message = message;
        }

        public string ToStampedLine() =>
            $"[{Time:HH:mm:ss.fff}] {Message}";

        private static readonly string[] _errorKeywords =
            { "fail", "error", "abort", "failed", "exception", "FAIL", "STOP" };

        /// <summary>Stamp 포맷([HH:mm:ss.fff] msg) 텍스트를 LogEntry 목록으로 파싱.</summary>
        public static List<LogEntry> ParseLines(string text)
        {
            var result = new List<LogEntry>();
            if (string.IsNullOrEmpty(text)) return result;
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                DateTime ts = DateTime.MinValue;
                string msg = line;
                if (line.Length > 15 && line[0] == '[' && line[13] == ']')
                {
                    if (DateTime.TryParseExact(line.Substring(1, 12), "HH:mm:ss.fff",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out ts))
                        msg = line.Length > 15 ? line.Substring(15) : "";
                }
                var level = _errorKeywords.Any(k => msg.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    ? LogLevel.Error : LogLevel.Info;
                result.Add(new LogEntry(ts, level, msg));
            }
            return result;
        }
    }
}
