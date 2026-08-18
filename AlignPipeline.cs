namespace TMTestWpfApp
{
    /// <summary>FindAlignKey 파이프라인 요약 (원본 AlignService 동작).</summary>
    public static class AlignPipeline
    {
        public static MatchingPipelineConfig CreateConfig(AlignSettings settings, TemplateMatchType matchType, double threshold)
        {
            if (settings == null) return null;
            return new MatchingPipelineConfig
            {
                Mode = settings.Mode,
                MatchType = matchType,
                ScoreThreshold = threshold,
                EdgeWeight = settings.EdgeWeight,
                NormalWeight = settings.NormalWeight,
                FineAlignSearchSize = settings.FineAlignSearchSize,
                KeyDir = settings.KeyDir,
                UseIntersectionPoint = settings.UseIntersectionPoint,
                RefineZoomFactor = settings.RefineZoomFactor,
                EdgeRefineInterval = settings.EdgeRefineInterval,
                EdgeRefineLevel = settings.EdgeRefineLevel,
                EdgeSearchRatio = settings.EdgeSearchRatio
            };
        }

        public static string DescribeFixedPipeline(AlignSettings s)
        {
            if (s == null) return "(no settings)";

            string match;
            if (s.Mode == AlignMode.Coarse)
            {
                match = "Normal MatchTemplate (raw)";
            }
            else
            {
                match = $"ConvertToEdge⊕Normal weighted ({s.EdgeWeight:0.##}:{s.NormalWeight:0.##})";
            }

            string scales = "0.25× → 1.0×";
            string refine = s.UseIntersectionPoint
                ? $"RefineAlignKeyCenter(Dir={s.KeyDir})"
                : "UseIntersectionPoint=false";
            string fine = s.Mode == AlignMode.Fine ? $" | SearchSize={s.FineAlignSearchSize}" : "";
            return $"{s.Mode} | {match} | Scales: {scales} | {refine}{fine}";
        }
    }
}
