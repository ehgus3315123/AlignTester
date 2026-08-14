using System;
using System.Collections.Generic;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace TMTestWpfApp
{
    /// <summary>전처리 단계 종류. 시퀀스에는 각 종류가 정확히 한 번 등장한다.</summary>
    public enum PreprocessStepType
    {
        GaussianBlur,
        Morphology,
        SobelEdge
    }

    /// <summary>Morphology 연산 종류.</summary>
    public enum MorphologyOp
    {
        Open,
        Close,
        Erode,
        Dilate
    }

    /// <summary>
    /// 전처리 시퀀스의 한 단계. 종류 + 사용 여부 + 파라미터를 한 곳에 둠.
    /// 모든 파라미터를 한 클래스에 두는 이유: UI에서 위/아래 재정렬 시 동일 인스턴스를 그대로 옮기기 위함.
    /// </summary>
    public class PreprocessStep
    {
        public PreprocessStepType Type { get; }
        public bool Enabled { get; set; }

        // Gaussian
        public int GaussianKernel { get; set; } = 5;
        public double GaussianSigma { get; set; } = 1.0;

        // Morphology
        public int MorphKernel { get; set; } = 3;
        public int MorphIterations { get; set; } = 1;
        public MorphologyOp MorphOp { get; set; } = MorphologyOp.Open;

        // Sobel
        public int SobelKernel { get; set; } = 3;
        public double SobelScale { get; set; } = 3.0;

        public PreprocessStep(PreprocessStepType type, bool enabled = true)
        {
            Type = type;
            Enabled = enabled;
        }

        public string DescribeShort()
        {
            switch (Type)
            {
                case PreprocessStepType.GaussianBlur:
                    return $"Gaussian(K={NormalizeOdd(GaussianKernel)}, σ={GaussianSigma:0.##})";
                case PreprocessStepType.Morphology:
                    return $"Morph{MorphOp}(K={NormalizeOdd(MorphKernel)}, iter={Math.Max(1, MorphIterations)})";
                case PreprocessStepType.SobelEdge:
                    return $"Sobel(K={NormalizeOdd(SobelKernel)}, scale={SobelScale:0.##})";
                default:
                    return Type.ToString();
            }
        }

        internal static int NormalizeOdd(int k)
        {
            if (k < 3) k = 3;
            if ((k & 1) == 0) k++;
            return k;
        }
    }

    /// <summary>
    /// 정확히 3개의 PreprocessStep을 가지는 전처리 시퀀스.
    /// 순서는 리스트의 인덱스 순서대로 적용된다.
    /// </summary>
    public class PreprocessPipeline
    {
        public List<PreprocessStep> Steps { get; }

        public PreprocessPipeline(IEnumerable<PreprocessStep> steps)
        {
            Steps = new List<PreprocessStep>(steps);
        }

        /// <summary>입력 이미지를 시퀀스대로 처리한 새 이미지를 반환한다. 입력은 호출자가 dispose.</summary>
        public Image<Gray, byte> Apply(Image<Gray, byte> input, Action<string> logger = null)
        {
            if (input == null) return null;

            // 항상 새 이미지를 반환해 호출자가 입력을 안전히 보존하도록 한다.
            Image<Gray, byte> current = input.Copy();
            var trace = new StringBuilder("Preprocess: input");

            foreach (var step in Steps)
            {
                if (!step.Enabled)
                {
                    trace.Append($" -> [skip {step.Type}]");
                    continue;
                }

                Image<Gray, byte> next;
                try
                {
                    switch (step.Type)
                    {
                        case PreprocessStepType.GaussianBlur:
                            next = ApplyGaussian(current, step);
                            break;
                        case PreprocessStepType.Morphology:
                            next = ApplyMorphology(current, step);
                            break;
                        case PreprocessStepType.SobelEdge:
                            next = ApplySobel(current, step);
                            break;
                        default:
                            next = current.Copy();
                            break;
                    }
                }
                catch
                {
                    current.Dispose();
                    throw;
                }

                current.Dispose();
                current = next;
                trace.Append(" -> ").Append(step.DescribeShort());
            }

            logger?.Invoke(trace.ToString());
            return current;
        }

        private static Image<Gray, byte> ApplyGaussian(Image<Gray, byte> input, PreprocessStep s)
        {
            int k = PreprocessStep.NormalizeOdd(s.GaussianKernel);
            double sigma = s.GaussianSigma < 0 ? 0 : s.GaussianSigma;
            return input.SmoothGaussian(k, k, sigma, sigma);
        }

        private static Image<Gray, byte> ApplyMorphology(Image<Gray, byte> input, PreprocessStep s)
        {
            int k = PreprocessStep.NormalizeOdd(s.MorphKernel);
            int iters = Math.Max(1, s.MorphIterations);
            MorphOp op;
            switch (s.MorphOp)
            {
                case MorphologyOp.Close: op = Emgu.CV.CvEnum.MorphOp.Close; break;
                case MorphologyOp.Erode: op = Emgu.CV.CvEnum.MorphOp.Erode; break;
                case MorphologyOp.Dilate: op = Emgu.CV.CvEnum.MorphOp.Dilate; break;
                default: op = Emgu.CV.CvEnum.MorphOp.Open; break;
            }

            var output = new Image<Gray, byte>(input.Size);
            using (Mat kernel = CvInvoke.GetStructuringElement(ElementShape.Rectangle, new System.Drawing.Size(k, k), new System.Drawing.Point(-1, -1)))
            {
                CvInvoke.MorphologyEx(input, output, op, kernel, new System.Drawing.Point(-1, -1), iters, BorderType.Default, default(MCvScalar));
            }
            return output;
        }

        private static Image<Gray, byte> ApplySobel(Image<Gray, byte> input, PreprocessStep s)
        {
            int k = PreprocessStep.NormalizeOdd(s.SobelKernel);
            double scale = s.SobelScale <= 0 ? 1.0 : s.SobelScale;

            using (Image<Gray, float> gradX = input.Sobel(1, 0, k))
            using (Image<Gray, float> scaledX = gradX.Mul(scale))
            using (Image<Gray, byte> absX = scaledX.AbsDiff(new Gray(0)).Convert<Gray, byte>())
            using (Image<Gray, float> gradY = input.Sobel(0, 1, k))
            using (Image<Gray, float> scaledY = gradY.Mul(scale))
            using (Image<Gray, byte> absY = scaledY.AbsDiff(new Gray(0)).Convert<Gray, byte>())
            {
                return absX.AddWeighted(absY, 0.5, 0.5, 0);
            }
        }

        /// <summary>현재 시퀀스를 사람이 읽을 수 있는 한 줄 요약으로 반환.</summary>
        public string DescribeShort()
        {
            var sb = new StringBuilder();
            bool any = false;
            foreach (var s in Steps)
            {
                if (!s.Enabled) continue;
                if (any) sb.Append(" → ");
                sb.Append(s.DescribeShort());
                any = true;
            }
            return any ? sb.ToString() : "(no preprocess)";
        }
    }
}
