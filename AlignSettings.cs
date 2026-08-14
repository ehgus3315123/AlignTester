namespace TMTestWpfApp
{
    /// <summary>Coarse / Fine Align 모드.</summary>
    public enum AlignMode
    {
        Coarse,
        Fine
    }

    /// <summary>
    /// AlignService.FindAlignKey 설정 (원본 VEGA-D).
    /// Coarse: Normal MatchTemplate only, 0.25→1.0.
    /// Fine: optional TemplateMatchUseEdge(ConvertToEdge 가중합산), 0.25→1.0, RefineAlignKeyCenter.
    /// </summary>
    public class AlignSettings
    {
        public AlignMode Mode { get; set; }

        /// <summary>원본 TemplateMatchUseEdge. Fine에서만 의미 있음.</summary>
        public bool TemplateMatchUseEdge { get; set; }

        /// <summary>원본 TemplateMatchEdgeWeight (기본 5).</summary>
        public double EdgeWeight { get; set; } = 5.0;

        /// <summary>원본 TemplateMatchNormalWeight (기본 5).</summary>
        public double NormalWeight { get; set; } = 5.0;

        /// <summary>원본 FineAlignSearchSize (기본 2000).</summary>
        public int FineAlignSearchSize { get; set; } = 2000;

        /// <summary>Fine: x1.0 성공 후 x2.0 업스케일 매칭.</summary>
        public bool UseMatchScale2 { get; set; }

        /// <summary>RefineAlignKeyCenter 방향 (UseIntersectionPoint일 때).</summary>
        public AlignKeyDir KeyDir { get; set; } = AlignKeyDir.LeftTop;

        /// <summary>
        /// true면 RefineAlignKeyCenter로 V/H FitLine 교점을 최종 중심으로 사용.
        /// false면 템플릿 매칭 중심을 그대로 사용.
        /// </summary>
        public bool UseIntersectionPoint { get; set; } = true;

        /// <summary>처리 과정 이미지(SearchArea, MatchResult, Refine 등) 저장.</summary>
        public bool SaveProcessImages { get; set; } = true;

        public static AlignSettings Current { get; set; }

        public static AlignSettings CreateDefault(AlignMode mode)
        {
            var s = new AlignSettings
            {
                Mode = mode,
                UseIntersectionPoint = true,
                KeyDir = AlignKeyDir.LeftTop
            };
            if (mode == AlignMode.Fine)
            {
                s.TemplateMatchUseEdge = true;
                s.EdgeWeight = 5;
                s.NormalWeight = 5;
                s.FineAlignSearchSize = 2000;
            }
            return s;
        }
    }
}
