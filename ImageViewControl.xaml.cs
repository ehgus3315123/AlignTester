using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TMTestWpfApp
{
    public partial class ImageViewControl : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(ImageViewControl),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty CropModeProperty =
            DependencyProperty.Register(nameof(CropMode), typeof(bool), typeof(ImageViewControl),
                new PropertyMetadata(false, OnCropModeChanged));

        public static readonly DependencyProperty PixelZoomEnabledProperty =
            DependencyProperty.Register(nameof(PixelZoomEnabled), typeof(bool), typeof(ImageViewControl),
                new PropertyMetadata(false, OnPixelZoomEnabledChanged));

        public static readonly DependencyProperty LockZoomProperty =
            DependencyProperty.Register(nameof(LockZoom), typeof(bool), typeof(ImageViewControl),
                new PropertyMetadata(false));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public bool CropMode
        {
            get => (bool)GetValue(CropModeProperty);
            set => SetValue(CropModeProperty, value);
        }

        /// <summary>true면 휠/버튼으로 이미지 픽셀 단위 확대·축소 (결과 뷰용).</summary>
        public bool PixelZoomEnabled
        {
            get => (bool)GetValue(PixelZoomEnabledProperty);
            set => SetValue(PixelZoomEnabledProperty, value);
        }

        /// <summary>true면 새 이미지 로드 시 현재 Zoom 배율 유지.</summary>
        public bool LockZoom
        {
            get => (bool)GetValue(LockZoomProperty);
            set => SetValue(LockZoomProperty, value);
        }

        public event MouseEventHandler ImageMouseMove;
        public event MouseEventHandler ImageMouseLeave;

        /// <summary>이미지 픽셀 좌표계 기준 선택 영역 확정 시 발생.</summary>
        public event EventHandler<Int32Rect> CropSelected;

        /// <summary>현재 표시용 Image. PixelZoom 켜면 ZoomImage.</summary>
        public Image ImageControl => PixelZoomEnabled ? ZoomImage : ImageDisplay;

        private bool _isDragging;
        private Point _dragStart;

        // --- pixel zoom ---
        private double _zoom = 1.0;          // 화면픽셀 / 이미지픽셀 (1.0 = 1:1)
        private double _fitZoom = 1.0;
        private double _focusX;             // 이미지 픽셀 좌표 (줌 중심)
        private double _focusY;
        private bool _hasFocus;
        private bool _panning;
        private Point _panStart;
        private double _panOriginH;
        private double _panOriginV;
        private bool _suppressSourceHook;
        private DependencyPropertyDescriptor _zoomSourceDesc;

        // 1이미지픽셀 = 1화면픽셀 단위로 맞춤. 최대 64화면픽셀/1이미지픽셀
        private const double MinZoomFloor = 0.01;
        private const double MaxZoom = 64.0;

        static ImageViewControl()
        {
            // ponytail: MapSelectionToPixels 회귀 — 이미지 좌표에 여백 없이 맞춘 선택
            var r = MapSelectionToPixels(10, 20, 50, 40, 200, 100, 400, 200, 400, 200);
            if (r == null || r.Value.X != 20 || r.Value.Y != 40 || r.Value.Width != 100 || r.Value.Height != 80)
                throw new InvalidOperationException("MapSelectionToPixels self-check failed");
        }

        public ImageViewControl()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                if (PixelZoomEnabled)
                    ApplyPixelZoomChrome(true);
            };
            ZoomScroll.SizeChanged += (_, __) =>
            {
                if (!PixelZoomEnabled || ZoomImage.Source == null) return;
                // 뷰포트만 바뀌면 fit 재계산 후 현재 배율이 fit보다 작으면 끌어올림
                RecalcFitZoom();
                if (_zoom < _fitZoom)
                {
                    _zoom = _fitZoom;
                    ApplyZoom(keepFocus: true);
                }
            };
        }

        private static void OnCropModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (ImageViewControl)d;
            if (!(bool)e.NewValue)
                ctrl.CancelSelection();
            ctrl.ImageGrid.Cursor = (bool)e.NewValue ? Cursors.Cross : Cursors.Arrow;
        }

        private static void OnPixelZoomEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ImageViewControl)d).ApplyPixelZoomChrome((bool)e.NewValue);
        }

        private void ApplyPixelZoomChrome(bool enabled)
        {
            ZoomToolbar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            ZoomScroll.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            ImageDisplay.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

            if (enabled)
            {
                if (_zoomSourceDesc == null)
                {
                    _zoomSourceDesc = DependencyPropertyDescriptor.FromProperty(
                        Image.SourceProperty, typeof(Image));
                    _zoomSourceDesc.AddValueChanged(ZoomImage, OnZoomImageSourceChanged);
                }

                // 기존에 ImageDisplay에만 넣어둔 소스 동기화
                if (ZoomImage.Source == null && ImageDisplay.Source != null)
                {
                    _suppressSourceHook = true;
                    ZoomImage.Source = ImageDisplay.Source;
                    _suppressSourceHook = false;
                }

                if (ZoomImage.Source != null)
                    ResetZoomToFit();
            }
            else if (_zoomSourceDesc != null)
            {
                _zoomSourceDesc.RemoveValueChanged(ZoomImage, OnZoomImageSourceChanged);
                _zoomSourceDesc = null;
            }
        }

        private void OnZoomImageSourceChanged(object sender, EventArgs e)
        {
            if (_suppressSourceHook || !PixelZoomEnabled) return;
            if (!_hasFocus && ZoomImage.Source is BitmapSource bmp)
            {
                _focusX = bmp.PixelWidth * 0.5;
                _focusY = bmp.PixelHeight * 0.5;
                _hasFocus = true;
            }
            ApplyZoomOnLoad();
        }

        /// <summary>줌 중심을 이미지 픽셀 좌표로 고정. 이후 확대/축소는 이 점 기준.</summary>
        public void SetZoomFocus(double imageX, double imageY)
        {
            _focusX = imageX;
            _focusY = imageY;
            _hasFocus = true;
            if (PixelZoomEnabled && ZoomImage.Source != null)
                ApplyZoom(keepFocus: true);
        }

        /// <summary>소스 설정 + 줌 중심. MainWindow 결과 표시용.</summary>
        public void SetSourceWithFocus(ImageSource source, double focusX, double focusY)
        {
            _focusX = focusX;
            _focusY = focusY;
            _hasFocus = true;
            if (PixelZoomEnabled)
            {
                _suppressSourceHook = true;
                ZoomImage.Source = source;
                _suppressSourceHook = false;
                ApplyZoomOnLoad();
            }
            else
            {
                ImageDisplay.Source = source;
            }
        }

        /// <summary>
        /// 새 이미지 로드 시 Zoom 적용. LockZoom이면 현재 배율 유지(범위 재클램프),
        /// 아니면 Fit으로 초기화.
        /// </summary>
        private void ApplyZoomOnLoad()
        {
            if (LockZoom && ZoomImage.Source is BitmapSource)
            {
                // 새 이미지 크기 기준으로 fitZoom 재계산 후 배율 클램프만 적용
                _zoom = ClampZoom(_zoom);
                ApplyZoom(keepFocus: true);
            }
            else
            {
                ResetZoomToFit();
            }
        }

        public void CancelSelection()
        {
            _isDragging = false;
            SelectionRect.Visibility = Visibility.Collapsed;
        }

        // ---- crop (대상 뷰) ----

        private void ImageGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!CropMode || ImageDisplay.Source == null || PixelZoomEnabled)
                return;

            _isDragging = true;
            _dragStart = e.GetPosition(SelectionCanvas);
            Canvas.SetLeft(SelectionRect, _dragStart.X);
            Canvas.SetTop(SelectionRect, _dragStart.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            SelectionRect.Visibility = Visibility.Visible;
            ImageGrid.CaptureMouse();
        }

        private void ImageGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            Point current = e.GetPosition(SelectionCanvas);
            double x = Math.Min(_dragStart.X, current.X);
            double y = Math.Min(_dragStart.Y, current.Y);
            double w = Math.Abs(current.X - _dragStart.X);
            double h = Math.Abs(current.Y - _dragStart.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;
        }

        private void ImageGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;

            _isDragging = false;
            ImageGrid.ReleaseMouseCapture();

            if (SelectionRect.Width < 3 || SelectionRect.Height < 3)
            {
                SelectionRect.Visibility = Visibility.Collapsed;
                return;
            }

            Int32Rect? pixelRect = ScreenRectToPixelRect();
            if (pixelRect.HasValue)
                CropSelected?.Invoke(this, pixelRect.Value);
        }

        private Int32Rect? ScreenRectToPixelRect()
        {
            var source = ImageDisplay.Source as BitmapSource;
            if (source == null) return null;

            double renderW = ImageDisplay.RenderSize.Width;
            double renderH = ImageDisplay.RenderSize.Height;
            if (renderW < 1 || renderH < 1) return null;

            // Canvas fills the cell; Image ArrangeOverride returns the fitted bitmap size
            // so the Image is centered in leftover space. Map overlay → Image first.
            double left = Canvas.GetLeft(SelectionRect);
            double top = Canvas.GetTop(SelectionRect);
            Point imgPt = SelectionCanvas.TranslatePoint(new Point(left, top), ImageDisplay);

            return MapSelectionToPixels(
                imgPt.X, imgPt.Y, SelectionRect.Width, SelectionRect.Height,
                renderW, renderH, source.PixelWidth, source.PixelHeight,
                source.Width, source.Height);
        }

        /// <summary>
        /// ImageDisplay 좌표의 선택 사각형 → 비트맵 픽셀. Uniform letterbox 포함.
        /// </summary>
        internal static Int32Rect? MapSelectionToPixels(
            double selX, double selY, double selW, double selH,
            double renderW, double renderH,
            int pixelW, int pixelH,
            double srcDipW, double srcDipH)
        {
            if (renderW < 1 || renderH < 1 || pixelW < 1 || pixelH < 1) return null;
            if (srcDipW < 1 || srcDipH < 1)
            {
                srcDipW = pixelW;
                srcDipH = pixelH;
            }

            double fit = Math.Min(renderW / srcDipW, renderH / srcDipH);
            double displayedW = srcDipW * fit;
            double displayedH = srcDipH * fit;
            double offsetX = (renderW - displayedW) * 0.5;
            double offsetY = (renderH - displayedH) * 0.5;
            if (displayedW < 1 || displayedH < 1) return null;

            int px = (int)Math.Round((selX - offsetX) / displayedW * pixelW);
            int py = (int)Math.Round((selY - offsetY) / displayedH * pixelH);
            int pw = (int)Math.Round(selW / displayedW * pixelW);
            int ph = (int)Math.Round(selH / displayedH * pixelH);

            px = Math.Max(0, Math.Min(px, pixelW - 1));
            py = Math.Max(0, Math.Min(py, pixelH - 1));
            pw = Math.Min(pw, pixelW - px);
            ph = Math.Min(ph, pixelH - py);

            if (pw < 1 || ph < 1) return null;
            return new Int32Rect(px, py, pw, ph);
        }

        private void ImageDisplay_MouseMove(object sender, MouseEventArgs e)
        {
            ImageMouseMove?.Invoke(sender, e);
        }

        private void ImageDisplay_MouseLeave(object sender, MouseEventArgs e)
        {
            ImageMouseLeave?.Invoke(sender, e);
        }

        // ---- pixel zoom ----

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);
        private void ZoomFit_Click(object sender, RoutedEventArgs e) => ResetZoomToFit();

        private void ZoomScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!PixelZoomEnabled || ZoomImage.Source == null) return;
            StepZoom(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        }

        private void ZoomScroll_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ResetZoomToFit();
            e.Handled = true;
        }

        private void ZoomScroll_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!PixelZoomEnabled || CropMode) return;
            _panning = true;
            _panStart = e.GetPosition(ZoomScroll);
            _panOriginH = ZoomScroll.HorizontalOffset;
            _panOriginV = ZoomScroll.VerticalOffset;
            ZoomScroll.CaptureMouse();
            ZoomScroll.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void ZoomScroll_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning) return;
            Point now = e.GetPosition(ZoomScroll);
            ZoomScroll.ScrollToHorizontalOffset(_panOriginH - (now.X - _panStart.X));
            ZoomScroll.ScrollToVerticalOffset(_panOriginV - (now.Y - _panStart.Y));
        }

        private void ZoomScroll_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_panning) return;
            _panning = false;
            ZoomScroll.ReleaseMouseCapture();
            ZoomScroll.Cursor = Cursors.Arrow;
        }

        /// <summary>
        /// 휠/버튼 1스텝. 1:1 이상에서는 화면상 이미지 픽셀이 1px씩 늘/줄도록
        /// (zoom이 정수일 때 ±1, 그 외는 ±10% 후 정수로 스냅 가능 구간 진입).
        /// </summary>
        private void StepZoom(int direction)
        {
            if (ZoomImage.Source == null) return;
            RecalcFitZoom();

            double next;
            if (_zoom >= 1.0 - 1e-9)
            {
                // 1px 단위: 1이미지픽셀당 화면픽셀 수 ±1
                double snapped = Math.Round(_zoom);
                if (Math.Abs(_zoom - snapped) > 1e-6)
                    next = direction > 0 ? Math.Ceiling(_zoom) : Math.Floor(_zoom);
                else
                    next = snapped + direction;
                if (next < 1.0) next = Math.Max(_fitZoom, _zoom * 0.9);
            }
            else
            {
                next = direction > 0 ? _zoom * 1.25 : _zoom / 1.25;
                if (direction > 0 && next >= 1.0) next = 1.0; // 1:1에 안착
            }

            next = ClampZoom(next);
            if (Math.Abs(next - _zoom) < 1e-9) return;
            _zoom = next;
            ApplyZoom(keepFocus: true);
        }

        private double ClampZoom(double z)
        {
            RecalcFitZoom();
            return Math.Max(_fitZoom, Math.Min(MaxZoom, Math.Max(MinZoomFloor, z)));
        }

        public void ResetZoomToFit()
        {
            if (!PixelZoomEnabled || !(ZoomImage.Source is BitmapSource)) return;
            RecalcFitZoom();
            _zoom = _fitZoom;
            ApplyZoom(keepFocus: true);
        }

        private void RecalcFitZoom()
        {
            if (!(ZoomImage.Source is BitmapSource bmp)) return;
            double vw = ZoomScroll.ViewportWidth;
            double vh = ZoomScroll.ViewportHeight;
            if (vw < 2 || vh < 2)
            {
                vw = ZoomScroll.ActualWidth;
                vh = ZoomScroll.ActualHeight;
            }
            if (vw < 2 || vh < 2 || bmp.PixelWidth < 1 || bmp.PixelHeight < 1)
            {
                _fitZoom = 1.0;
                return;
            }

            _fitZoom = Math.Min(vw / bmp.PixelWidth, vh / bmp.PixelHeight);
            if (_fitZoom <= 0) _fitZoom = MinZoomFloor;
        }

        private void ApplyZoom(bool keepFocus)
        {
            if (!(ZoomImage.Source is BitmapSource bmp)) return;

            double w = bmp.PixelWidth * _zoom;
            double h = bmp.PixelHeight * _zoom;
            ZoomImage.Width = w;
            ZoomImage.Height = h;

            RenderOptions.SetBitmapScalingMode(ZoomImage,
                _zoom >= 1.0 - 1e-9 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Fant);

            UpdateZoomLabel();

            if (!keepFocus || !_hasFocus) return;

            // 레이아웃 반영 후 중심 스크롤
            ZoomScroll.Dispatcher.BeginInvoke(new Action(() =>
            {
                double vx = ZoomScroll.ViewportWidth;
                double vy = ZoomScroll.ViewportHeight;
                double targetX = _focusX * _zoom - vx * 0.5;
                double targetY = _focusY * _zoom - vy * 0.5;
                ZoomScroll.ScrollToHorizontalOffset(Math.Max(0, targetX));
                ZoomScroll.ScrollToVerticalOffset(Math.Max(0, targetY));
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void UpdateZoomLabel()
        {
            if (Math.Abs(_zoom - _fitZoom) < 1e-6)
                txtZoomLevel.Text = "Fit";
            else if (_zoom >= 1.0 - 1e-9)
                txtZoomLevel.Text = $"{_zoom:0.#}× · 1px";
            else
                txtZoomLevel.Text = $"{_zoom * 100:0.#}%";
        }
    }
}
