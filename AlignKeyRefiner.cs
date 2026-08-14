using System;
using System.Collections.Generic;
using System.Linq;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using DrawingPoint = System.Drawing.Point;
using DrawingPointF = System.Drawing.PointF;
using DrawingRect = System.Drawing.Rectangle;
using DrawingRectF = System.Drawing.RectangleF;

namespace TMTestWpfApp
{
    /// <summary>Align Key 코너 방향 (원본 AlignKeyDir).</summary>
    public enum AlignKeyDir
    {
        LeftTop,
        LeftBottom,
        RightTop,
        RightBottom
    }

    /// <summary>
    /// AlignService.RefineAlignKeyCenter + FindEdges / FitLine 교점 이식.
    /// MemoryData 대신 Image&lt;Gray,byte&gt; ROI crop.
    /// </summary>
    public static class AlignKeyRefiner
    {
        private enum eEdgeSearchDir
        {
            ToLeft,
            ToRight,
            ToBottom,
            ToTop
        }

        private const int EdgeRefineInterval = 3;
        private const int EdgeRefineLevel = 60;

        /// <summary>템플릿 매칭 중심을 V/H 에지 교점으로 정밀화. 실패 시 입력 좌표 유지.</summary>
        public static void RefineAlignKeyCenter(
            Image<Gray, byte> source,
            AlignKeyDir dir,
            int templateWidth,
            int templateHeight,
            ref double dAlignKeyCenterX,
            ref double dAlignKeyCenterY,
            Action<string> logger = null,
            string historyFolder = null,
            bool saveImages = true,
            ProcessImageQueue shots = null)
        {
            if (source == null) return;
            if (!saveImages)
                historyFolder = null;

            double templateCenterX = dAlignKeyCenterX;
            double templateCenterY = dAlignKeyCenterY;

            var matchRect = CenterRect(
                (int)Math.Round(dAlignKeyCenterX),
                (int)Math.Round(dAlignKeyCenterY),
                templateWidth, templateHeight,
                source.Width, source.Height);

            if (matchRect.Width < 3 || matchRect.Height < 3)
            {
                logger?.Invoke($"[{dir}] Edge corner refine skipped (ROI too small) — keep template center");
                return;
            }

            using (Image<Gray, byte> matchCrop = source.Copy(matchRect))
            {
                GetAlignKeyEdgeSearchDirs(dir, out eEdgeSearchDir searchDirV, out eEdgeSearchDir searchDirH);

                var roi = new DrawingRectF(0, 0, matchCrop.Width, matchCrop.Height);
                if (!TryGetCornerIntersection(matchCrop, roi, searchDirV, searchDirH,
                    out DrawingPointF localCorner, out FitLine2D lineV, out FitLine2D lineH,
                    out List<DrawingPointF> edgesV, out List<DrawingPointF> edgesH))
                {
                    logger?.Invoke($"[{dir}] Edge corner refine failed — keep template center " +
                                   $"({templateCenterX:F2}, {templateCenterY:F2})");
                    return;
                }

                MatchHistory.SaveRefineFitLines(
                    matchCrop, lineV, lineH, localCorner, shots, historyFolder);

                dAlignKeyCenterX = matchRect.Left + localCorner.X;
                dAlignKeyCenterY = matchRect.Top + localCorner.Y;

                logger?.Invoke($"[{dir}] Edge corner refine: " +
                               $"template=({templateCenterX:F2}, {templateCenterY:F2}) → " +
                               $"corner=({dAlignKeyCenterX:F2}, {dAlignKeyCenterY:F2}) " +
                               $"dirs=({searchDirV}, {searchDirH})");
            }
        }

        /// <summary>FitLine 결과 (방향 벡터 + 직선 위 한 점). 시각화/교점용.</summary>
        public struct FitLine2D
        {
            public DrawingPointF Direction;
            public DrawingPointF PointOnLine;
        }

        /// <summary>원본 CRect(CPoint, w, h) — 중심 기준 사각형.</summary>
        public static DrawingRect CenterRect(int centerX, int centerY, int width, int height, int imgW, int imgH)
        {
            int left = centerX - width / 2;
            int top = centerY - height / 2;
            int right = centerX + width / 2;
            int bottom = centerY + height / 2;

            left = Math.Max(0, left);
            top = Math.Max(0, top);
            right = Math.Min(imgW, right);
            bottom = Math.Min(imgH, bottom);

            int w = Math.Max(0, right - left);
            int h = Math.Max(0, bottom - top);
            return new DrawingRect(left, top, w, h);
        }

        /// <summary>ponytail: CenterRect / ConvertToEdge 한 줄 검증.</summary>
        public static void SelfCheck()
        {
            var r = CenterRect(100, 200, 50, 40, 1000, 1000);
            if (r.X != 75 || r.Y != 180 || r.Width != 50 || r.Height != 40)
                throw new InvalidOperationException($"CenterRect self-check failed: {r}");
            var clamped = CenterRect(10, 10, 100, 100, 50, 50);
            if (clamped.X != 0 || clamped.Y != 0 || clamped.Width != 50 || clamped.Height != 50)
                throw new InvalidOperationException($"CenterRect clamp self-check failed: {clamped}");

            using (var img = new Image<Gray, byte>(64, 48))
            {
                img.SetValue(128);
                using (var edge = TemplateMatcher.ConvertToEdge(img))
                {
                    if (edge == null || edge.Width != 64 || edge.Height != 48)
                        throw new InvalidOperationException("ConvertToEdge self-check failed");
                }

                double cx = 32, cy = 24;
                RefineAlignKeyCenter(img, AlignKeyDir.LeftTop, 16, 16, ref cx, ref cy);

                // ponytail: 수직×수평 파라메트릭 교점 (기울기식은 여기서 깨짐)
                var lineV = new FitLine2D
                {
                    Direction = new DrawingPointF(0, 1),
                    PointOnLine = new DrawingPointF(10, 0)
                };
                var lineH = new FitLine2D
                {
                    Direction = new DrawingPointF(1, 0),
                    PointOnLine = new DrawingPointF(0, 20)
                };
                var hit = IntersectFitLines(lineV, lineH);
                if (Math.Abs(hit.X - 10f) > 1e-3f || Math.Abs(hit.Y - 20f) > 1e-3f)
                    throw new InvalidOperationException($"IntersectFitLines self-check failed: {hit}");
            }
        }

        private static void GetAlignKeyEdgeSearchDirs(
            AlignKeyDir dir, out eEdgeSearchDir searchDirV, out eEdgeSearchDir searchDirH)
        {
            switch (dir)
            {
                case AlignKeyDir.LeftTop:
                    searchDirV = eEdgeSearchDir.ToRight;
                    searchDirH = eEdgeSearchDir.ToBottom;
                    break;
                case AlignKeyDir.LeftBottom:
                    searchDirV = eEdgeSearchDir.ToRight;
                    searchDirH = eEdgeSearchDir.ToTop;
                    break;
                case AlignKeyDir.RightTop:
                    searchDirV = eEdgeSearchDir.ToLeft;
                    searchDirH = eEdgeSearchDir.ToBottom;
                    break;
                case AlignKeyDir.RightBottom:
                default:
                    searchDirV = eEdgeSearchDir.ToLeft;
                    searchDirH = eEdgeSearchDir.ToTop;
                    break;
            }
        }

        private static bool TryGetCornerIntersection(
            Image<Gray, byte> image,
            DrawingRectF roi,
            eEdgeSearchDir searchDirV,
            eEdgeSearchDir searchDirH,
            out DrawingPointF intersection,
            out FitLine2D lineV,
            out FitLine2D lineH,
            out List<DrawingPointF> edgesV,
            out List<DrawingPointF> edgesH)
        {
            intersection = new DrawingPointF();
            lineV = default(FitLine2D);
            lineH = default(FitLine2D);
            edgesV = null;
            edgesH = null;
            if (image == null || roi.Width < 3 || roi.Height < 3)
                return false;

            edgesV = FindEdges(image, roi, searchDirV, false, EdgeRefineInterval, EdgeRefineLevel);
            edgesH = FindEdges(image, roi, searchDirH, false, EdgeRefineInterval, EdgeRefineLevel);

            if (edgesV.Count == 0 || edgesH.Count == 0)
                return false;

            intersection = GetEdgeIntersectionPoint(edgesV, edgesH, out lineV, out lineH);
            return !(float.IsNaN(intersection.X) || float.IsNaN(intersection.Y)
                || float.IsInfinity(intersection.X) || float.IsInfinity(intersection.Y));
        }

        private static List<DrawingPointF> FindEdges(
            Image<Gray, byte> image,
            DrawingRectF rectROI,
            eEdgeSearchDir eDir,
            bool bMedianBlur,
            int nInterval,
            int nEdgeScore)
        {
            var tempList = new List<DrawingPointF>();
            Mat matInsp = null;
            Mat matAdjust = null;
            Mat matSubAdjust1 = null;
            Mat matSubAdjust2 = null;
            Matrix<float> sobel1 = null;
            Matrix<float> sobel2 = null;

            try
            {
                matInsp = image.Mat.Clone();

                var rtSubRect = new DrawingRect(
                    (int)rectROI.X, (int)rectROI.Y, (int)rectROI.Width, (int)rectROI.Height);
                rtSubRect.Intersect(new DrawingRect(0, 0, matInsp.Width, matInsp.Height));
                if (rtSubRect.Width < 3 || rtSubRect.Height < 3)
                    return tempList;

                // ROI header는 Size/연속성 이슈가 있어 Clone으로 독립 Mat 사용
                using (Mat matRoi = new Mat(matInsp, rtSubRect))
                    matAdjust = matRoi.Clone();

                matSubAdjust1 = new Mat(matAdjust.Rows, matAdjust.Cols, DepthType.Cv8U, 1);
                matSubAdjust2 = new Mat(matAdjust.Rows, matAdjust.Cols, DepthType.Cv8U, 1);

                var ptShifted = new DrawingPointF(rtSubRect.X, rtSubRect.Y);

                if (bMedianBlur)
                    CvInvoke.MedianBlur(matAdjust, matAdjust, 3);

                float[] pattern = new float[] { 1, 1, 1, 0, -1, -1, -1 };
                float[] patternNeg = new float[] { -1, -1, -1, 0, 1, 1, 1 };

                if (eDir == eEdgeSearchDir.ToLeft || eDir == eEdgeSearchDir.ToRight)
                {
                    sobel1 = CreateColumnKernel7x7(pattern);
                    sobel2 = CreateColumnKernel7x7(patternNeg);
                }
                else
                {
                    sobel1 = CreateRowKernel7x7(pattern);
                    sobel2 = CreateRowKernel7x7(patternNeg);
                }

                var anchor = new DrawingPoint(-1, -1);
                CvInvoke.Filter2D(matAdjust, matSubAdjust1, sobel1, anchor, 0, BorderType.Default);
                CvInvoke.Filter2D(matAdjust, matSubAdjust2, sobel2, anchor, 0, BorderType.Default);

                tempList = GetEdgePoint(matSubAdjust1, matSubAdjust2, eDir, ptShifted, nEdgeScore);
                return RemoveEdgeOutlier(tempList, eDir, nInterval);
            }
            catch
            {
                return tempList;
            }
            finally
            {
                sobel1?.Dispose();
                sobel2?.Dispose();
                matSubAdjust1?.Dispose();
                matSubAdjust2?.Dispose();
                matAdjust?.Dispose();
                matInsp?.Dispose();
            }
        }

        private static List<DrawingPointF> GetEdgePoint(
            Mat mat1, Mat mat2, eEdgeSearchDir eDir, DrawingPointF ptShifted, int nEdgeScore)
        {
            var result = new List<DrawingPointF>();
            byte[] image1Array = mat1.GetRawData();
            byte[] image2Array = mat2.GetRawData();

            int nWidth = mat1.Width;
            int nHeight = mat1.Height;
            int startX = 0, endX = nWidth;
            int startY = 0, endY = nHeight;

            double ratio = 0.6666666;
            int rangeX = (int)(nWidth * ratio);
            int rangeY = (int)(nHeight * ratio);

            switch (eDir)
            {
                case eEdgeSearchDir.ToRight: endX = Math.Min(rangeX, nWidth); break;
                case eEdgeSearchDir.ToLeft: startX = Math.Max(nWidth - rangeX, 0); break;
                case eEdgeSearchDir.ToTop: startY = Math.Max(nHeight - rangeY, 0); break;
                case eEdgeSearchDir.ToBottom: endY = Math.Min(rangeY, nHeight); break;
            }

            for (int y = startY; y < endY; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    int v1 = image1Array[(nWidth * y) + x];
                    int v2 = image2Array[(nWidth * y) + x];
                    if (nEdgeScore < v1 || nEdgeScore < v2)
                        result.Add(new DrawingPointF(x + ptShifted.X, y + ptShifted.Y));
                }
            }

            return result;
        }

        private static List<DrawingPointF> RemoveEdgeOutlier(
            List<DrawingPointF> list, eEdgeSearchDir eDir, int range)
        {
            try
            {
                var projectionList = new List<float>();
                var removedList = new List<DrawingPointF>();
                bool isVertical = eDir == eEdgeSearchDir.ToLeft || eDir == eEdgeSearchDir.ToRight;

                for (int i = 0; i < list.Count; i++)
                    projectionList.Add(isVertical ? list[i].X : list[i].Y);

                EdgePointRange(list, eDir, out double min, out double max);

                int nMaxCount = 0;
                int nMaxIndex = 0;
                for (int i = (int)min; i < max; i++)
                {
                    int inRangeNum = projectionList.Count(n => (n < i + range) && (n > i - range));
                    if (inRangeNum > nMaxCount)
                    {
                        nMaxCount = inRangeNum;
                        nMaxIndex = i;
                    }
                }

                for (int i = 0; i < list.Count; i++)
                {
                    float coord = isVertical ? list[i].X : list[i].Y;
                    if (coord < nMaxIndex + range && coord > nMaxIndex - range)
                        removedList.Add(list[i]);
                }

                return removedList;
            }
            catch
            {
                return list;
            }
        }

        private static void EdgePointRange(
            List<DrawingPointF> list, eEdgeSearchDir eDir, out double min, out double max)
        {
            try
            {
                bool isVertical = eDir == eEdgeSearchDir.ToLeft || eDir == eEdgeSearchDir.ToRight;
                var projectionList = new List<float>();
                for (int i = 0; i < list.Count; i++)
                    projectionList.Add(isVertical ? list[i].X : list[i].Y);
                min = projectionList.Min();
                max = projectionList.Max();
            }
            catch
            {
                min = 0;
                max = 1500;
            }
        }

        private static DrawingPointF GetEdgeIntersectionPoint(
            List<DrawingPointF> edgePointsVertical,
            List<DrawingPointF> edgePointsHorizontal,
            out FitLine2D lineV,
            out FitLine2D lineH)
        {
            DrawingPointF directionV, pointOnLineV, directionH, pointOnLineH;
            CvInvoke.FitLine(edgePointsVertical.ToArray(), out directionV, out pointOnLineV, DistType.Welsch, 0, 0.001, 0.001);
            CvInvoke.FitLine(edgePointsHorizontal.ToArray(), out directionH, out pointOnLineH, DistType.Welsch, 0, 0.001, 0.001);

            lineV = new FitLine2D { Direction = directionV, PointOnLine = pointOnLineV };
            lineH = new FitLine2D { Direction = directionH, PointOnLine = pointOnLineH };

            // 기울기(y=ax+b)는 수직선에서 발산 — 파라메트릭 교점 사용
            return IntersectFitLines(lineV, lineH);
        }

        /// <summary>직선 P+tD 와 Q+sE 교점. 평행이면 NaN.</summary>
        private static DrawingPointF IntersectFitLines(FitLine2D a, FitLine2D b)
        {
            double px = a.PointOnLine.X, py = a.PointOnLine.Y;
            double dx = a.Direction.X, dy = a.Direction.Y;
            double qx = b.PointOnLine.X, qy = b.PointOnLine.Y;
            double ex = b.Direction.X, ey = b.Direction.Y;

            double cross = dx * ey - dy * ex;
            if (Math.Abs(cross) < 1e-12)
                return new DrawingPointF(float.NaN, float.NaN);

            double t = ((qx - px) * ey - (qy - py) * ex) / cross;
            return new DrawingPointF((float)(px + t * dx), (float)(py + t * dy));
        }

        private static Matrix<float> CreateColumnKernel7x7(float[] pattern)
        {
            var m = new Matrix<float>(7, 7);
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++)
                    m[y, x] = pattern[x];
            return m;
        }

        private static Matrix<float> CreateRowKernel7x7(float[] pattern)
        {
            var m = new Matrix<float>(7, 7);
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++)
                    m[y, x] = pattern[y];
            return m;
        }
    }
}
