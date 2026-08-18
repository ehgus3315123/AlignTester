using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using DRectangle = System.Drawing.Rectangle;

namespace TMTestWpfApp
{
    /// <summary>한 스케일 단계의 매칭 결과.</summary>
    public struct ScaleMatchResult
    {
        public double Scale;
        public bool Executed;
        public bool IsFound;
        public double Score;
        public double Threshold;
        /// <summary>원본 좌표계 상 템플릿 중심 (해당 스케일 시점, refine 전).</summary>
        public System.Windows.Point CenterInOriginal;
        /// <summary>템플릿 매칭 중심 (refine과 무관, Step 이미지 줌용).</summary>
        public System.Windows.Point MatchCenterInOriginal;
        public DRectangle RoiUsed;
        public long ElapsedMs;
        public string Note;
        public double EdgeScoreRaw;
        public double NormalScoreRaw;
    }

    /// <summary>MatchingPipeline.Run 설정 — FindAlignKey / MatchAlignKeyOptions 대응.</summary>
    public class MatchingPipelineConfig
    {
        public AlignMode Mode { get; set; } = AlignMode.Coarse;
        public TemplateMatchType MatchType { get; set; } = TemplateMatchType.CCORR;
        public double ScoreThreshold { get; set; } = 0.7;
        public double EdgeWeight { get; set; } = 5.0;
        public double NormalWeight { get; set; } = 5.0;
        /// <summary>Fine 초기 탐색 창 크기 (원본 FineAlignSearchSize, 기본 2000).</summary>
        public int FineAlignSearchSize { get; set; } = 2000;
        /// <summary>RefineAlignKeyCenter 방향.</summary>
        public AlignKeyDir KeyDir { get; set; } = AlignKeyDir.LeftTop;
        /// <summary>true면 V/H FitLine 교점으로 중심 Refine.</summary>
        public bool UseIntersectionPoint { get; set; } = true;
        /// <summary>RefineAlignKeyCenter 줌 배율. 0=줌 없음, 2=2×, 4=4×.</summary>
        public int RefineZoomFactor { get; set; } = 0;
        /// <summary>에지 아웃라이어 제거 허용 범위 (픽셀).</summary>
        public int EdgeRefineInterval { get; set; } = 3;
        /// <summary>Sobel 응답 임계값.</summary>
        public int EdgeRefineLevel { get; set; } = 60;
        /// <summary>에지 탐색 범위 비율 (0.0〜1.0).</summary>
        public double EdgeSearchRatio { get; set; } = 0.6667;
    }

    /// <summary>
    /// AlignService.FindAlignKey 이식:
    /// MatchAlignKeyAtScale025 → Scale1 → RefineAlignKeyCenter.
    /// 한 스케일 실패 시 즉시 중단.
    /// </summary>
    public class MatchingPipeline
    {
        private readonly TemplateMatcher _matcher;

        public MatchingPipeline(TemplateMatcher matcher)
        {
            _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        }

        /// <summary>
        /// 파이프라인 실행. shots를 out으로 반환하므로 호출자가 Dispose 책임짐.
        /// saveImages=false면 shots에 수집은 하되 historyFolder에 파일을 쓰지 않는다.
        /// </summary>
        public List<ScaleMatchResult> Run(
            Image<Gray, byte> targetRaw,
            Image<Gray, byte> templateRaw,
            MatchingPipelineConfig config,
            Action<string> logger,
            string historyFolder,
            bool saveImages,
            out ProcessImageQueue shots)
        {
            shots = new ProcessImageQueue(true);
            var results = new List<ScaleMatchResult>();
            if (targetRaw == null || templateRaw == null || config == null) return results;

            try
            {
                int tw = templateRaw.Width;
                int th = templateRaw.Height;
                int srcW = targetRaw.Width;
                int srcH = targetRaw.Height;

                DRectangle searchArea = GetInitialSearchArea(config, srcW, srcH);
                logger?.Invoke($"[{config.Mode}] Initial searchArea=({searchArea.X},{searchArea.Y},{searchArea.Width},{searchArea.Height})");

                var r025 = MatchAtScale025(targetRaw, templateRaw, searchArea, config, logger, shots);
                results.Add(r025);
                if (!r025.Executed || !r025.IsFound)
                {
                    logger?.Invoke($"[{config.Mode}] Cannot get Align Key Position (x0.25) — abort");
                    return results;
                }

                searchArea = AlignKeyRefiner.CenterRect(
                    (int)Math.Round(r025.CenterInOriginal.X),
                    (int)Math.Round(r025.CenterInOriginal.Y),
                    tw + 8, th + 8, srcW, srcH);

                if (searchArea.Width < 1 || searchArea.Height < 1)
                {
                    results.Add(FailResult(1.0, config, "search area out of bounds"));
                    return results;
                }

                var r1 = MatchAtScale1(targetRaw, templateRaw, searchArea, config, logger, shots);
                results.Add(r1);
                if (!r1.Executed || !r1.IsFound)
                {
                    logger?.Invoke($"[{config.Mode}] Cannot get Align Key Position (x1.0) — abort");
                    return results;
                }

                double dAlignKeyCenterX = r1.CenterInOriginal.X;
                double dAlignKeyCenterY = r1.CenterInOriginal.Y;

                if (config.UseIntersectionPoint)
                {
                    AlignKeyRefiner.RefineAlignKeyCenter(
                        targetRaw, config.KeyDir, tw, th,
                        ref dAlignKeyCenterX, ref dAlignKeyCenterY, logger, null, saveImages, shots,
                        config.RefineZoomFactor, config.EdgeRefineInterval, config.EdgeRefineLevel,
                        config.EdgeSearchRatio);
                }

                if (results.Count > 0)
                {
                    int last = results.Count - 1;
                    var final = results[last];
                    final.CenterInOriginal = new System.Windows.Point(dAlignKeyCenterX, dAlignKeyCenterY);
                    final.Note = (final.Note ?? "") + (config.UseIntersectionPoint ? " [refined]" : "");
                    results[last] = final;
                }

                logger?.Invoke($"[{config.Mode}] FindAlignKey done Center=({dAlignKeyCenterX:F2},{dAlignKeyCenterY:F2})");
                return results;
            }
            finally
            {
                shots.Flush(saveImages ? historyFolder : null);
            }
        }

        /// <summary>Coarse: 전체 / Fine: FineAlignSearchSize 창 (이미지 중심).</summary>
        private static DRectangle GetInitialSearchArea(MatchingPipelineConfig config, int srcW, int srcH)
        {
            if (config.Mode == AlignMode.Fine && config.FineAlignSearchSize > 0)
            {
                int size = config.FineAlignSearchSize;
                return AlignKeyRefiner.CenterRect(srcW / 2, srcH / 2, size, size, srcW, srcH);
            }
            return new DRectangle(0, 0, srcW, srcH);
        }

        private ScaleMatchResult MatchAtScale025(
            Image<Gray, byte> target, Image<Gray, byte> template,
            DRectangle searchArea, MatchingPipelineConfig config,
            Action<string> logger, ProcessImageQueue shots)
        {
            var sw = Stopwatch.StartNew();
            var result = NewResult(0.25, config, searchArea);

            using (var crop = SafeCopy(target, searchArea))
            {
                if (crop == null)
                {
                    result.Note = "crop failed";
                    sw.Stop();
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    return result;
                }

                using (var cropDiv4 = crop.Resize(0.25, Inter.Linear))
                using (var tmplDiv4 = template.Resize(0.25, Inter.Linear))
                {
                    MatchHistory.SaveSearchArea025(cropDiv4, tmplDiv4, shots);

                    logger?.Invoke($"[{config.Mode}] AlignKey (x0.25) template matching...");
                    bool fine = config.Mode == AlignMode.Fine;
                    var mr = _matcher.FindTemplate(cropDiv4, tmplDiv4, config.ScoreThreshold,
                        fine, config.EdgeWeight, config.NormalWeight, config.MatchType);

                    result.Executed = true;
                    result.IsFound = mr.IsFound;
                    result.Score = mr.Score;
                    result.EdgeScoreRaw = mr.EdgeScoreRaw;
                    result.NormalScoreRaw = mr.NormalScoreRaw;

                    // div4 center → original absolute
                    double absX = searchArea.X + mr.CenterInSearch.X * 4.0;
                    double absY = searchArea.Y + mr.CenterInSearch.Y * 4.0;
                    result.CenterInOriginal = new System.Windows.Point(absX, absY);
                    result.MatchCenterInOriginal = result.CenterInOriginal;

                    if (mr.IsFound)
                    {
                        if (config.Mode == AlignMode.Fine)
                        {
                            MatchHistory.SaveFineStepMatchBox(cropDiv4, tmplDiv4.Width, tmplDiv4.Height,
                                mr.CenterInSearch.X, mr.CenterInSearch.Y,
                                "Step2_Match025", shots);
                        }
                        else
                        {
                            MatchHistory.SaveMatchResult(cropDiv4, tmplDiv4.Width, tmplDiv4.Height,
                                mr.CenterInSearch.X, mr.CenterInSearch.Y,
                                mr.Score, true, 0.25, shots);
                        }
                    }
                }
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            LogScale(logger, result);
            return result;
        }

        private ScaleMatchResult MatchAtScale1(
            Image<Gray, byte> target, Image<Gray, byte> template,
            DRectangle searchArea, MatchingPipelineConfig config,
            Action<string> logger, ProcessImageQueue shots)
        {
            var sw = Stopwatch.StartNew();
            var result = NewResult(1.0, config, searchArea);

            using (var crop = SafeCopy(target, searchArea))
            {
                if (crop == null)
                {
                    result.Note = "crop failed";
                    sw.Stop();
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    return result;
                }

                MatchHistory.SavePreMatch1_0(crop, template, shots);

                logger?.Invoke($"[{config.Mode}] AlignKey (x1.0) template matching...");
                bool fine = config.Mode == AlignMode.Fine;
                var mr = _matcher.FindTemplate(crop, template, config.ScoreThreshold,
                    fine, config.EdgeWeight, config.NormalWeight, config.MatchType);

                result.Executed = true;
                result.IsFound = mr.IsFound;
                result.Score = mr.Score;
                result.EdgeScoreRaw = mr.EdgeScoreRaw;
                result.NormalScoreRaw = mr.NormalScoreRaw;
                result.CenterInOriginal = new System.Windows.Point(
                    searchArea.X + mr.CenterInSearch.X,
                    searchArea.Y + mr.CenterInSearch.Y);
                result.MatchCenterInOriginal = result.CenterInOriginal;

                if (mr.IsFound)
                {
                    if (config.Mode == AlignMode.Fine)
                    {
                        MatchHistory.SaveFineStepMatchBox(crop, template.Width, template.Height,
                            mr.CenterInSearch.X, mr.CenterInSearch.Y,
                            "Step3_Match1_0", shots, drawCenterDot: true);
                    }
                    else
                    {
                        MatchHistory.SaveMatchResult(crop, template.Width, template.Height,
                            mr.CenterInSearch.X, mr.CenterInSearch.Y,
                            mr.Score, true, 1.0, shots);
                    }
                }
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            LogScale(logger, result);
            return result;
        }

        private static ScaleMatchResult NewResult(double scale, MatchingPipelineConfig config, DRectangle roi)
        {
            return new ScaleMatchResult
            {
                Scale = scale,
                Threshold = config.ScoreThreshold,
                RoiUsed = roi
            };
        }

        private static ScaleMatchResult FailResult(double scale, MatchingPipelineConfig config, string note)
        {
            return new ScaleMatchResult
            {
                Scale = scale,
                Executed = false,
                IsFound = false,
                Threshold = config.ScoreThreshold,
                Note = note
            };
        }

        private static Image<Gray, byte> SafeCopy(Image<Gray, byte> src, DRectangle roi)
        {
            if (src == null || roi.Width < 1 || roi.Height < 1) return null;
            if (roi.X < 0 || roi.Y < 0 || roi.Right > src.Width || roi.Bottom > src.Height) return null;
            try { return src.Copy(roi); }
            catch { return null; }
        }

        private static void LogScale(Action<string> logger, ScaleMatchResult r)
        {
            if (logger == null) return;
            if (r.EdgeScoreRaw != 0)
                logger($"[x{r.Scale}] {(r.IsFound ? "Found" : "NotFound")} Combined={r.Score:F6} (Edge={r.EdgeScoreRaw:F6}, Normal={r.NormalScoreRaw:F6}) Time={r.ElapsedMs}ms Center=({r.CenterInOriginal.X:F1},{r.CenterInOriginal.Y:F1}) ROI=({r.RoiUsed.X},{r.RoiUsed.Y},{r.RoiUsed.Width},{r.RoiUsed.Height})");
            else
                logger($"[x{r.Scale}] {(r.IsFound ? "Found" : "NotFound")} Score={r.Score:F6} Time={r.ElapsedMs}ms Center=({r.CenterInOriginal.X:F1},{r.CenterInOriginal.Y:F1}) ROI=({r.RoiUsed.X},{r.RoiUsed.Y},{r.RoiUsed.Width},{r.RoiUsed.Height})");
        }
    }
}
