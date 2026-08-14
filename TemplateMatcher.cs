using System;
using System.Diagnostics;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace TMTestWpfApp
{
    /// <summary>매칭 방식 (Sqdiff/Ccorr/Ccoeff Normed 모드 사용)</summary>
    public enum TemplateMatchType
    {
        SQDIFF,
        CCORR,
        CCOEFF
    }

    /// <summary>
    /// Root_VEGA_D.TemplateMatcher 이식.
    /// TemplateMatch → FindTemplate → PerformTemplateMatching / ConvertToEdge / CenterLocation.
    /// </summary>
    public class TemplateMatcher
    {
        public Action<string> Logger { get; set; }

        public struct SingleMatchResult
        {
            public bool IsFound;
            public double Score;
            /// <summary>검색 이미지 좌표계의 템플릿 중심 (원본 CenterLocation 결과).</summary>
            public System.Windows.Point CenterInSearch;
            /// <summary>매칭 맵상 좌상단 (진단용).</summary>
            public System.Drawing.Point RawLocation;
            public long ElapsedMs;
            public TemplateMatchType Type;
            public double EdgeScoreRaw;
            public double NormalScoreRaw;
        }

        /// <summary>원본 TemplateMatch 와 동일. useEdge면 ConvertToEdge + 가중 합산.</summary>
        public bool TemplateMatch(
            out double score,
            Image<Gray, byte> imgTargetArea,
            Image<Gray, byte> imgTemplate,
            double scoreThreshold,
            out System.Windows.Point ptResult,
            bool useEdge = false,
            double edgeWeight = 5,
            double normalWeight = 5)
        {
            var r = FindTemplate(imgTargetArea, imgTemplate, scoreThreshold, useEdge, edgeWeight, normalWeight);
            score = r.Score;
            ptResult = r.CenterInSearch;
            return r.IsFound;
        }

        public SingleMatchResult FindTemplate(
            Image<Gray, byte> targetArea,
            Image<Gray, byte> template,
            double scoreThreshold,
            bool useCombinedEdgeMatching = false,
            double edgeWeight = 5,
            double normalWeight = 5,
            TemplateMatchType matchType = TemplateMatchType.CCORR)
        {
            var sw = Stopwatch.StartNew();
            var result = new SingleMatchResult { Type = matchType };

            if (targetArea == null || template == null
                || targetArea.Width < template.Width || targetArea.Height < template.Height)
            {
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                Logger?.Invoke("FindTemplate: invalid inputs");
                return result;
            }

            TemplateMatchingType matchingType = ToCv(matchType);
            using (var matchingResult = PerformTemplateMatching(
                targetArea, template, matchingType, useCombinedEdgeMatching, edgeWeight, normalWeight,
                out result.EdgeScoreRaw, out result.NormalScoreRaw))
            {
                System.Drawing.Point matchLocation;
                double matchScore;
                bool isFound;
                if (IsMaxBased(matchingType))
                    matchLocation = FindMaxMatchLocation(matchingResult, scoreThreshold, out matchScore, out isFound);
                else
                    matchLocation = FindMinMatchLocation(matchingResult, scoreThreshold, out matchScore, out isFound);

                result.Score = matchScore;
                result.RawLocation = matchLocation;
                result.IsFound = isFound;

                if (isFound)
                {
                    var centered = CenterLocation(matchLocation, targetArea.Width, targetArea.Height,
                        matchingResult.Width, matchingResult.Height);
                    result.CenterInSearch = centered;
                }
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;

            string mode = useCombinedEdgeMatching
                ? $"Combined(Edge:{edgeWeight}, Normal:{normalWeight})"
                : "Normal";
            Logger?.Invoke($"Template Match | Result: {(result.IsFound ? "Found" : "NotFound")} | Mode: {mode} | Score={result.Score:F6} | Time={result.ElapsedMs}ms");
            return result;
        }

        private Image<Gray, float> PerformTemplateMatching(
            Image<Gray, byte> targetArea,
            Image<Gray, byte> template,
            TemplateMatchingType matchingType,
            bool useCombinedEdgeMatching,
            double edgeWeight,
            double normalWeight,
            out double edgeScoreRaw,
            out double normalScoreRaw)
        {
            edgeScoreRaw = 0;
            normalScoreRaw = 0;

            if (useCombinedEdgeMatching)
            {
                double total = edgeWeight + normalWeight;
                double we = total > 0 ? edgeWeight / total : 0.5;
                double wn = total > 0 ? normalWeight / total : 0.5;

                using (var edgeTarget = ConvertToEdge(targetArea))
                using (var edgeTemplate = ConvertToEdge(template))
                using (var edgeResult = edgeTarget.MatchTemplate(edgeTemplate, matchingType))
                using (var normalResult = targetArea.MatchTemplate(template, matchingType))
                using (var edgeWeighted = edgeResult.Mul(we))
                using (var normalWeighted = normalResult.Mul(wn))
                {
                    if (IsMaxBased(matchingType))
                    {
                        edgeResult.MinMax(out _, out double[] eMax, out _, out _);
                        normalResult.MinMax(out _, out double[] nMax, out _, out _);
                        edgeScoreRaw = eMax[0];
                        normalScoreRaw = nMax[0];
                    }
                    else
                    {
                        edgeResult.MinMax(out double[] eMin, out _, out _, out _);
                        normalResult.MinMax(out double[] nMin, out _, out _, out _);
                        edgeScoreRaw = eMin[0];
                        normalScoreRaw = nMin[0];
                    }

                    Logger?.Invoke($"Template Matching | Edge Score: {edgeScoreRaw:0.######} | Normal Score: {normalScoreRaw:0.######}");

                    // Mul().Add() 체인은 임시 이미지 dispose 타이밍이 위험 — 명시적 Add
                    var combined = new Image<Gray, float>(edgeWeighted.Size);
                    CvInvoke.Add(edgeWeighted, normalWeighted, combined);
                    return combined;
                }
            }

            var result = targetArea.MatchTemplate(template, matchingType);
            result.MinMax(out _, out double[] maxValues, out _, out _);
            normalScoreRaw = maxValues[0];
            Logger?.Invoke($"Template Matching Normal Score: {normalScoreRaw:0.######}");
            return result;
        }

        /// <summary>Gaussian → 7x7 box-gradient 4방향 에지. 기본 k=5, σ=1 (원본 ConvertToEdge).</summary>
        public static Image<Gray, byte> ConvertToEdge(
            Image<Gray, byte> inputImage,
            int gaussianKernelSize = 5,
            double gaussianSigma = 1.0)
        {
            if (inputImage == null) return null;

            int k = gaussianKernelSize < 1 ? 1 : (gaussianKernelSize % 2 == 0 ? gaussianKernelSize + 1 : gaussianKernelSize);
            double sigma = gaussianSigma < 0 ? 0 : gaussianSigma;

            using (var blurred = inputImage.SmoothGaussian(k, k, sigma, sigma))
                return ConvertToEdgeCore(blurred);
        }

        private static Image<Gray, byte> ConvertToEdgeCore(Image<Gray, byte> inputImage)
        {
            var anchor = new System.Drawing.Point(-1, -1);
            var pattern = new float[] { 1, 1, 1, 0, -1, -1, -1 };
            var patternNeg = new float[] { -1, -1, -1, 0, 1, 1, 1 };

            using (var srcFloat = new Mat())
            using (var kernelA = CreateColumnKernel7x7(pattern))
            using (var kernelB = CreateColumnKernel7x7(patternNeg))
            using (var kernelC = CreateRowKernel7x7(pattern))
            using (var kernelD = CreateRowKernel7x7(patternNeg))
            {
                inputImage.Mat.ConvertTo(srcFloat, DepthType.Cv32F);
                var sz = srcFloat.Size;

                using (var a = new Mat(sz, DepthType.Cv32F, 1))
                using (var b = new Mat(sz, DepthType.Cv32F, 1))
                using (var c = new Mat(sz, DepthType.Cv32F, 1))
                using (var d = new Mat(sz, DepthType.Cv32F, 1))
                using (var aAbs = new Mat(sz, DepthType.Cv8U, 1))
                using (var bAbs = new Mat(sz, DepthType.Cv8U, 1))
                using (var cAbs = new Mat(sz, DepthType.Cv8U, 1))
                using (var dAbs = new Mat(sz, DepthType.Cv8U, 1))
                using (var H = new Mat(sz, DepthType.Cv8U, 1))
                using (var V = new Mat(sz, DepthType.Cv8U, 1))
                using (var result = new Mat(sz, DepthType.Cv8U, 1))
                {
                    CvInvoke.Filter2D(srcFloat, a, kernelA, anchor);
                    CvInvoke.Filter2D(srcFloat, b, kernelB, anchor);
                    CvInvoke.Filter2D(srcFloat, c, kernelC, anchor);
                    CvInvoke.Filter2D(srcFloat, d, kernelD, anchor);

                    CvInvoke.ConvertScaleAbs(a, aAbs, 1, 0);
                    CvInvoke.ConvertScaleAbs(b, bAbs, 1, 0);
                    CvInvoke.ConvertScaleAbs(c, cAbs, 1, 0);
                    CvInvoke.ConvertScaleAbs(d, dAbs, 1, 0);

                    CvInvoke.Max(aAbs, bAbs, H);
                    CvInvoke.Max(cAbs, dAbs, V);

                    CvInvoke.Threshold(H, H, 20, 255, ThresholdType.ToZero);
                    CvInvoke.Threshold(V, V, 20, 255, ThresholdType.ToZero);

                    CvInvoke.BitwiseOr(H, V, result);

                    // ponytail: ToImage may share buffer — clone so using-dispose is safe
                    using (Mat owned = result.Clone())
                        return owned.ToImage<Gray, byte>();
                }
            }
        }

        private static System.Windows.Point CenterLocation(
            System.Drawing.Point location, int targetWidth, int targetHeight, int resultWidth, int resultHeight)
        {
            double widthDiff = targetWidth - resultWidth;
            double heightDiff = targetHeight - resultHeight;
            return new System.Windows.Point(
                location.X + widthDiff * 0.5,
                location.Y + heightDiff * 0.5);
        }

        private static System.Drawing.Point FindMaxMatchLocation(
            Image<Gray, float> matchResult, double scoreThreshold, out double maxScore, out bool isFound)
        {
            matchResult.MinMax(out _, out double[] maxValues, out _, out System.Drawing.Point[] maxLocations);
            maxScore = maxValues[0];
            isFound = maxScore >= scoreThreshold;
            return maxLocations[0];
        }

        private static System.Drawing.Point FindMinMatchLocation(
            Image<Gray, float> matchResult, double scoreThreshold, out double minScore, out bool isFound)
        {
            matchResult.MinMax(out double[] minValues, out _, out System.Drawing.Point[] minLocations, out _);
            minScore = minValues[0];
            isFound = minScore <= scoreThreshold;
            return minLocations[0];
        }

        private static TemplateMatchingType ToCv(TemplateMatchType type)
        {
            switch (type)
            {
                case TemplateMatchType.SQDIFF: return TemplateMatchingType.SqdiffNormed;
                case TemplateMatchType.CCORR: return TemplateMatchingType.CcorrNormed;
                case TemplateMatchType.CCOEFF: return TemplateMatchingType.CcoeffNormed;
                default: return TemplateMatchingType.CcorrNormed;
            }
        }

        private static bool IsMaxBased(TemplateMatchingType cvType)
        {
            return cvType != TemplateMatchingType.Sqdiff
                && cvType != TemplateMatchingType.SqdiffNormed;
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
