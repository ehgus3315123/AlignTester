using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using DPoint = System.Drawing.Point;
using DRectangle = System.Drawing.Rectangle;

namespace TMTestWpfApp
{
    /// <summary>처리 과정 패널용 이미지를 모아, 파이프라인 종료 시 한 번에 저장한다.</summary>
    public sealed class ProcessImageQueue : IDisposable
    {
        private readonly List<(string FileName, Mat Mat)> _items = new List<(string, Mat)>();
        public bool Enabled { get; }

        public ProcessImageQueue(bool enabled)
        {
            Enabled = enabled;
        }

        public void Add(string fileName, Mat src)
        {
            if (!Enabled || src == null || src.IsEmpty || string.IsNullOrEmpty(fileName)) return;
            _items.Add((fileName, src.Clone()));
        }

        public void Add(string fileName, Image<Gray, byte> img)
        {
            if (img == null) return;
            Add(fileName, img.Mat);
        }

        public void Add(string fileName, Image<Bgr, byte> img)
        {
            if (img == null) return;
            Add(fileName, img.Mat);
        }

        public void Flush(string folder)
        {
            if (!Enabled || string.IsNullOrEmpty(folder) || _items.Count == 0) return;
            try { Directory.CreateDirectory(folder); }
            catch { return; }

            for (int i = 0; i < _items.Count; i++)
            {
                try { CvInvoke.Imwrite(Path.Combine(folder, _items[i].FileName), _items[i].Mat); }
                catch { /* ponytail: history write must not stop matching */ }
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].Mat?.Dispose();
            _items.Clear();
        }
    }

    /// <summary>
    /// AlignService.SaveScale025MatchResult / SaveSearchArea / summary.txt 와 같은
    /// 매칭 과정 아티팩트를 폴더에 남긴다.
    /// </summary>
    public static class MatchHistory
    {
        /// <summary>스케일된 검색 이미지 위에 템플릿 박스·십자·스코어를 그린다. Coarse 처리 과정용.</summary>
        public static void SaveMatchResult(
            Image<Gray, byte> searchScaled,
            int templateW,
            int templateH,
            double centerXInScaled,
            double centerYInScaled,
            double score,
            bool matched,
            double scale,
            ProcessImageQueue shots)
        {
            if (searchScaled == null || shots == null || !shots.Enabled) return;
            if (templateW < 1 || templateH < 1) return;

            try
            {
                string tag = FormatScaleTag(scale);
                string matchTag = matched ? "MATCH" : "NOMATCH";
                using (var vis = searchScaled.Convert<Bgr, byte>())
                {
                    int cx = (int)Math.Round(centerXInScaled);
                    int cy = (int)Math.Round(centerYInScaled);
                    int halfW = templateW / 2;
                    int halfH = templateH / 2;

                    var boxColor = new MCvScalar(0, 0, 220);
                    var crossColor = new MCvScalar(0, 200, 255);

                    CvInvoke.Rectangle(vis,
                        new DRectangle(cx - halfW, cy - halfH, templateW, templateH),
                        boxColor, 1, LineType.AntiAlias);

                    int arm = Math.Max(halfW, halfH) / 2 + 4;
                    CvInvoke.Line(vis, new DPoint(cx - arm, cy), new DPoint(cx + arm, cy), crossColor, 1, LineType.AntiAlias);
                    CvInvoke.Line(vis, new DPoint(cx, cy - arm), new DPoint(cx, cy + arm), crossColor, 1, LineType.AntiAlias);

                    CvInvoke.PutText(vis, $"{score:0.000} {matchTag}",
                        new DPoint(4, 16),
                        FontFace.HersheySimplex, 0.45, new MCvScalar(220, 220, 0), 1, LineType.AntiAlias);

                    shots.Add($"MatchResult_{tag}_{matchTag}.jpg", vis);
                }
            }
            catch
            {
                // ponytail: history write must not stop matching
            }
        }

        /// <summary>실행 요약 summary.txt 를 기록한다.</summary>
        public static void WriteSummary(
            string folder,
            AlignSettings settings,
            MatchingPipelineConfig config,
            System.Collections.Generic.IList<ScaleMatchResult> results,
            string processLog)
        {
            if (string.IsNullOrEmpty(folder)) return;

            try
            {
                Directory.CreateDirectory(folder);
                var sb = new StringBuilder();
                sb.AppendLine($"Mode={settings?.Mode}");
                sb.AppendLine($"Pipeline={AlignPipeline.DescribeFixedPipeline(settings)}");
                sb.AppendLine($"MatchType={config?.MatchType}");
                sb.AppendLine($"Threshold={config?.ScoreThreshold.ToString("0.####", CultureInfo.InvariantCulture)}");
                if (config != null && config.UseEdgeCombined)
                {
                    sb.AppendLine($"EdgeWeight={config.EdgeWeight.ToString("0.##", CultureInfo.InvariantCulture)}");
                    sb.AppendLine($"NormalWeight={config.NormalWeight.ToString("0.##", CultureInfo.InvariantCulture)}");
                }
                sb.AppendLine();
                sb.AppendLine("ScaleResults:");

                if (results != null)
                {
                    foreach (var r in results)
                    {
                        if (!r.Executed)
                        {
                            sb.AppendLine($"  [x{r.Scale}] SKIPPED — {r.Note}");
                            continue;
                        }

                        string tag = r.IsFound ? "MATCH" : "NOMATCH";
                        if (r.UsedEdgeCombined)
                        {
                            sb.AppendLine($"  [x{r.Scale}] {tag} Combined={r.Score:F6} Edge={r.EdgeScoreRaw:F6} Normal={r.NormalScoreRaw:F6} Center=({r.CenterInOriginal.X:F1},{r.CenterInOriginal.Y:F1}) ROI=({r.RoiUsed.X},{r.RoiUsed.Y},{r.RoiUsed.Width},{r.RoiUsed.Height}) {r.ElapsedMs}ms");
                        }
                        else
                        {
                            sb.AppendLine($"  [x{r.Scale}] {tag} Score={r.Score:F6} Center=({r.CenterInOriginal.X:F1},{r.CenterInOriginal.Y:F1}) ROI=({r.RoiUsed.X},{r.RoiUsed.Y},{r.RoiUsed.Width},{r.RoiUsed.Height}) {r.ElapsedMs}ms");
                        }
                    }
                }

                File.WriteAllText(Path.Combine(folder, "summary.txt"), sb.ToString(), Encoding.UTF8);

                if (!string.IsNullOrEmpty(processLog))
                    File.WriteAllText(Path.Combine(folder, "process.log"), processLog, Encoding.UTF8);
            }
            catch
            {
                // ponytail: history write must not stop matching
            }
        }

        public static string FormatScaleTag(double scale)
        {
            if (Math.Abs(scale - 0.25) < 1e-9) return "x0.25";
            if (Math.Abs(scale - 1.0) < 1e-9) return "x1.0";
            if (Math.Abs(scale - 2.0) < 1e-9) return "x2.0";
            return "x" + scale.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static void SaveSearchArea025(Image<Gray, byte> searchScaled, Image<Gray, byte> templateScaled, ProcessImageQueue shots)
        {
            SaveNormalAndEdge(searchScaled, shots, "Step1_SearchArea");
            SaveNormalAndEdge(templateScaled, shots, "Step1_Template");
        }

        public static void SavePreMatch1_0(Image<Gray, byte> searchCrop, Image<Gray, byte> template, ProcessImageQueue shots)
        {
            SaveNormalAndEdge(searchCrop, shots, "Step3_SearchArea");
            SaveNormalAndEdge(template, shots, "Step3_Template");
        }

        public static void SavePreMatch2_0(Image<Gray, byte> searchScaled, Image<Gray, byte> templateScaled, ProcessImageQueue shots)
        {
            SaveNormalAndEdge(searchScaled, shots, "Step3_SearchArea2_0");
            SaveNormalAndEdge(templateScaled, shots, "Step3_Template2_0");
        }

        private static void SaveNormalAndEdge(Image<Gray, byte> img, ProcessImageQueue shots, string stem)
        {
            if (img == null || shots == null || !shots.Enabled || string.IsNullOrEmpty(stem)) return;
            try
            {
                shots.Add(stem + "_Normal.bmp", img);
                using (var edge = TemplateMatcher.ConvertToEdge(img))
                {
                    if (edge != null)
                        shots.Add(stem + "_Edge.bmp", edge);
                }
            }
            catch
            {
                // ponytail: history write must not stop matching
            }
        }

        /// <summary>
        /// Fine Align Step2~4: 매칭 이미지 위에 Bounding Box(+옵션 빨간 중심점)를 그려 저장.
        /// fileStem 예: Step2_Match025 / Step3_Match1_0
        /// </summary>
        public static void SaveFineStepMatchBox(
            Image<Gray, byte> searchImage,
            int templateW,
            int templateH,
            double centerX,
            double centerY,
            string fileStem,
            ProcessImageQueue shots,
            bool drawCenterDot = false)
        {
            if (searchImage == null || shots == null || !shots.Enabled || string.IsNullOrEmpty(fileStem)) return;
            if (templateW < 1 || templateH < 1) return;

            try
            {
                using (var vis = searchImage.Convert<Bgr, byte>())
                {
                    int cx = (int)Math.Round(centerX);
                    int cy = (int)Math.Round(centerY);
                    int halfW = templateW / 2;
                    int halfH = templateH / 2;

                    CvInvoke.Rectangle(vis,
                        new DRectangle(cx - halfW, cy - halfH, templateW, templateH),
                        new MCvScalar(0, 0, 220), 1, LineType.EightConnected);

                    if (drawCenterDot)
                        DrawCenterPixel(vis, cx, cy, new MCvScalar(0, 0, 255));

                    shots.Add(fileStem + ".bmp", vis);
                }
            }
            catch
            {
                // ponytail: history write must not stop matching
            }
        }

        /// <summary>
        /// RefineAlignKeyCenter: V/H FitLine + 교점을 그린 뒤, 교점 중심 50×50만 저장.
        /// 파일: Step4_RefineLines.bmp
        /// </summary>
        public static void SaveRefineFitLines(
            Image<Gray, byte> matchCrop,
            AlignKeyRefiner.FitLine2D lineV,
            AlignKeyRefiner.FitLine2D lineH,
            System.Drawing.PointF intersection,
            ProcessImageQueue shots,
            string folder = null)
        {
            if (matchCrop == null) return;
            bool queue = shots != null && shots.Enabled;
            if (!queue && string.IsNullOrEmpty(folder)) return;

            try
            {
                using (var vis = matchCrop.Convert<Bgr, byte>())
                {
                    var lineColorV = new MCvScalar(0, 220, 255);
                    var lineColorH = new MCvScalar(255, 80, 255);
                    var cornerColor = new MCvScalar(0, 0, 255);

                    DrawFitLine(vis, lineV, lineColorV);
                    DrawFitLine(vis, lineH, lineColorH);

                    int cx = (int)Math.Round(intersection.X);
                    int cy = (int)Math.Round(intersection.Y);
                    DrawCenterPixel(vis, cx, cy, cornerColor);

                    var zoomRect = AlignKeyRefiner.CenterRect(cx, cy, 50, 50, vis.Width, vis.Height);
                    if (zoomRect.Width > 0 && zoomRect.Height > 0)
                    {
                        using (var zoom = vis.Copy(zoomRect))
                        {
                            if (queue) shots.Add("Step4_RefineLines.bmp", zoom);
                            else
                            {
                                Directory.CreateDirectory(folder);
                                zoom.Save(Path.Combine(folder, "Step4_RefineLines.bmp"));
                            }
                        }
                    }
                    else if (queue)
                        shots.Add("Step4_RefineLines.bmp", vis);
                    else
                    {
                        Directory.CreateDirectory(folder);
                        vis.Save(Path.Combine(folder, "Step4_RefineLines.bmp"));
                    }
                }
            }
            catch
            {
                // ponytail: history write must not stop matching
            }
        }

        /// <summary>정확히 1이미지픽셀만 칠한다 (Rectangle 1x1은 OpenCV에서 2x2로 나오는 경우 있음).</summary>
        private static void DrawCenterPixel(Image<Bgr, byte> vis, int x, int y, MCvScalar color)
        {
            if (vis == null || x < 0 || y < 0 || x >= vis.Width || y >= vis.Height) return;
            vis.Data[y, x, 0] = (byte)color.V0;
            vis.Data[y, x, 1] = (byte)color.V1;
            vis.Data[y, x, 2] = (byte)color.V2;
        }

        private static void DrawFitLine(Image<Bgr, byte> vis, AlignKeyRefiner.FitLine2D line, MCvScalar color)
        {
            if (vis == null) return;
            float dx = line.Direction.X;
            float dy = line.Direction.Y;
            if (Math.Abs(dx) < 1e-8 && Math.Abs(dy) < 1e-8) return;

            const float span = 100000f;
            var p1 = new DPoint(
                (int)Math.Round(line.PointOnLine.X - dx * span),
                (int)Math.Round(line.PointOnLine.Y - dy * span));
            var p2 = new DPoint(
                (int)Math.Round(line.PointOnLine.X + dx * span),
                (int)Math.Round(line.PointOnLine.Y + dy * span));

            var bounds = new DRectangle(0, 0, vis.Width, vis.Height);
            if (!CvInvoke.ClipLine(bounds, ref p1, ref p2)) return;
            // AntiAlias면 선이 두껍게 퍼져 교점이 어긋나 보임 — 1px 선
            CvInvoke.Line(vis, p1, p2, color, 1, LineType.EightConnected);
        }
    }
}
