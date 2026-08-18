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
    /// Fine: ConvertToEdge⊕Normal 가중합산, 0.25→1.0, RefineAlignKeyCenter.
    /// </summary>
    public class AlignSettings
    {
        public AlignMode Mode { get; set; }

        /// <summary>원본 TemplateMatchEdgeWeight (기본 5).</summary>
        public double EdgeWeight { get; set; } = 5.0;

        /// <summary>원본 TemplateMatchNormalWeight (기본 5).</summary>
        public double NormalWeight { get; set; } = 5.0;

        /// <summary>원본 FineAlignSearchSize (기본 2000).</summary>
        public int FineAlignSearchSize { get; set; } = 2000;

        /// <summary>RefineAlignKeyCenter 방향 (UseIntersectionPoint일 때).</summary>
        public AlignKeyDir KeyDir { get; set; } = AlignKeyDir.LeftTop;

        /// <summary>
        /// true면 RefineAlignKeyCenter로 V/H FitLine 교점을 최종 중심으로 사용.
        /// false면 템플릿 매칭 중심을 그대로 사용.
        /// </summary>
        public bool UseIntersectionPoint { get; set; } = true;

        /// <summary>
        /// RefineAlignKeyCenter 줌 배율. 0=줌 없음, 2=2×, 4=4×.
        /// 중심 1/N crop → 원본 크기 resize로 에지 분해능 확보.
        /// </summary>
        public int RefineZoomFactor { get; set; } = 0;

        /// <summary>에지 아웃라이어 제거 허용 범위 (픽셀). RemoveEdgeOutlier nInterval.</summary>
        public int EdgeRefineInterval { get; set; } = 3;

        /// <summary>Sobel 응답 임계값. 이 값 이상인 픽셀만 에지로 채택.</summary>
        public int EdgeRefineLevel { get; set; } = 60;

        /// <summary>에지 탐색 범위 비율 (0.0〜1.0). ROI의 이 비율 범위 안에서만 에지 탐색.</summary>
        public double EdgeSearchRatio { get; set; } = 0.6667;

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
                s.EdgeWeight = 5;
                s.NormalWeight = 5;
                s.FineAlignSearchSize = 2000;
            }
            return s;
        }
    }
}
