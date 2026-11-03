using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MathNet.Numerics.LinearAlgebra;

// 1. 定义参数映射类
public class SVGPParams
{
    public float[][][] inducing_points { get; set; }        //诱导点
    public float[][] variational_mean { get; set; }         //变分均值
    public float[][][] variational_chol_covar { get; set; }  //变分协方差的Cholesky分解
    public float[][] lmc_coeffs { get; set; }              //线性混合系数
    public float[][] lengthscale { get; set; }//核函数长度尺度
    public float[] outputscale { get; set; }//核函数输出尺度
    public float[] noise_variance { get; set; }//噪声方差
    public float mean_constant { get; set; }//均值函数常数项
}

// 2. SVGPredictor 读取 JSON 并初始化
public class SVGPredictor
{
    private readonly int L, M, D, T;
    private readonly Matrix<float>[] Kinv, A;
    private readonly Vector<float>[] whiteMu;
    private readonly float meanConst;
    private readonly float[][][] Z;
    private readonly float[][] lengthscale;
    private readonly float[] outputscale, noiseVar;
    private readonly float[][] lmc;

    private static string SanitizeJson(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        string s = raw;
        // 删除单行注释 //...
        s = Regex.Replace(s, @"(?m)//.*$", "");
        // 删除块注释 /* ... */
        s = Regex.Replace(s, @"(?s)/\*.*?\*/", "");
        // 移除对象/数组末尾多余逗号: ,}
        s = Regex.Replace(s, @",\s*([}\]])", "$1");
        // 处理非标准数值
        s = Regex.Replace(s, @"\bInfinity\b", "1e12");
        s = Regex.Replace(s, @"\bNaN\b", "0");
        return s;
    }


    public SVGPredictor(string jsonPath)
    {
        var raw = File.ReadAllText(jsonPath);
        var json = SanitizeJson(raw);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        SVGPParams p = null;
        try
        {
            p = JsonSerializer.Deserialize<SVGPParams>(json, opts);//映射到 SVGPParams 对象
        }
        catch (JsonException)
        {
            // 若标准映射失败（可能因尾逗号/注释/键名不匹配），走 GPyTorch 兼容解析
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                p = BuildParamsFromGpytorch(doc.RootElement);
            }
        }
        if (p == null || p.inducing_points == null)
        {
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                p = BuildParamsFromGpytorch(doc.RootElement);
            }
        }

        Z = p.inducing_points;   L = Z.Length;
        M = Z[0].Length; D = Z[0][0].Length;
        lengthscale = p.lengthscale; outputscale = p.outputscale;
        noiseVar = p.noise_variance; lmc = p.lmc_coeffs; T = lmc.Length;
        meanConst = p.mean_constant;

        Kinv = new Matrix<float>[L];
        A = new Matrix<float>[L];
        whiteMu = new Vector<float>[L];

        // 构造 Kzz, L, 反白化 m, 变分协方差 A
        for (int l = 0; l < L; l++)
        {
            // Kzz
            var K = Matrix<float>.Build.Dense(M, M, (i, j) => {
                float s = 0; 
                for (int d = 0; d < D; d++)
                {
                    var diff = (Z[l][i][d] - Z[l][j][d]) / lengthscale[l][d];
                    s += diff * diff;
                }
                return outputscale[l] * (float)Math.Exp(-0.5 * s);
            });
            for (int i = 0; i < M; i++) K[i, i] += 1e-5f;  // jitter

            // Cholesky
            var chol = K.Cholesky();
            var Lmat = chol.Factor;

            // Kinv
            var I = Matrix<float>.Build.DenseIdentity(M);
            Kinv[l] = chol.Solve(I);

            // unwhiten m_u = L * m_w
            var m_w = Vector<float>.Build.DenseOfArray(p.variational_mean[l]);
            var m_u = Lmat.Multiply(m_w);

            // whiteMu = Kinv * m_u
            whiteMu[l] = Kinv[l].Multiply(m_u);

            // S_u = L S_w L^T
            var Wc = Matrix<float>.Build.DenseOfRowArrays(p.variational_chol_covar[l]);
            var S_w = Wc.Multiply(Wc.Transpose());
            var S_u = Lmat.Multiply(S_w).Multiply(Lmat.Transpose());

            // A = Kinv * S_u * Kinv
            A[l] = Kinv[l].Multiply(S_u).Multiply(Kinv[l]);
        }
    }

    // 3. 预测接口
    public (float[][] mean, float[][] var) Predict(float[][] x)
    {
        int N = x.Length;
        var mean = new float[N][];
        var var = new float[N][];

        for (int i = 0; i < N; i++)
        {
            mean[i] = new float[T]; var[i] = new float[T];
            // each latent
            for (int t = 0; t < T; t++)
            {
                float m = 0, v = 0;
                for (int l = 0; l < L; l++)
                {
                    // k_xZ row
                    var krow = Vector<float>.Build.Dense(M, j => {
                        float s = 0; 
                        for (int d = 0; d < D; d++)
                        {
                            var diff = (x[i][d] - Z[l][j][d]) / lengthscale[l][d];
                            s += diff * diff;
                        }
                        return outputscale[l] * (float)Math.Exp(-0.5 * s);
                    });
                    // mean
                    m += lmc[t][l] * (meanConst + krow.DotProduct(whiteMu[l]));
                    // variance
                    var prior = outputscale[l];
                    var proj = krow.DotProduct(Kinv[l].Multiply(krow));
                    var varw = krow.DotProduct(A[l].Multiply(krow));
                    v += lmc[t][l] * lmc[t][l] * (prior - proj + varw);
                }
                mean[i][t] = m;
                var[i][t] = v + noiseVar[t];
            }
        }
        return (mean, var);
    }
    private static SVGPParams BuildParamsFromGpytorch(JsonElement root)
    {
        float Softplus(float x)
        {
            // 数值稳定的 softplus
            if (x > 20f) return x; // exp(x) 很大时 softplus(x) ~= x
            if (x < -20f) return 0f; // 非常小趋近 0
            return (float)Math.Log(1.0 + Math.Exp(x));
        }

        SVGPParams p = new SVGPParams();

        // inducing_points: L x M x D
        if (!root.TryGetProperty("variational_strategy.base_variational_strategy.inducing_points", out var zEl))
            throw new InvalidDataException("JSON 缺少 inducing_points 字段");
        int L = zEl.GetArrayLength();
        p.inducing_points = new float[L][][];
        for (int l = 0; l < L; l++)
        {
            var zL = zEl[l];
            int M = zL.GetArrayLength();
            p.inducing_points[l] = new float[M][];
            for (int i = 0; i < M; i++)
            {
                var zi = zL[i];
                int D = zi.GetArrayLength();
                p.inducing_points[l][i] = new float[D];
                for (int d = 0; d < D; d++)
                    p.inducing_points[l][i][d] = (float)zi[d].GetDouble();
            }
        }

        // variational_mean: L x M
        if (!root.TryGetProperty("variational_strategy.base_variational_strategy._variational_distribution.variational_mean", out var vmEl))
            throw new InvalidDataException("JSON 缺少 variational_mean 字段");
        p.variational_mean = new float[L][];
        for (int l = 0; l < L; l++)
        {
            var vml = vmEl[l];
            int M = vml.GetArrayLength();
            p.variational_mean[l] = new float[M];
            for (int i = 0; i < M; i++) p.variational_mean[l][i] = (float)vml[i].GetDouble();
        }

        // variational_chol_covar: L x M x M
        if (!root.TryGetProperty("variational_strategy.base_variational_strategy._variational_distribution.chol_variational_covar", out var vcEl))
            throw new InvalidDataException("JSON 缺少 chol_variational_covar 字段");
        p.variational_chol_covar = new float[L][][];
        for (int l = 0; l < L; l++)
        {
            var vcl = vcEl[l];
            int M = vcl.GetArrayLength();
            p.variational_chol_covar[l] = new float[M][];
            for (int r = 0; r < M; r++)
            {
                var row = vcl[r];
                int C = row.GetArrayLength();
                p.variational_chol_covar[l][r] = new float[C];
                for (int c = 0; c < C; c++) p.variational_chol_covar[l][r][c] = (float)row[c].GetDouble();
            }
        }

        // lmc_coeffs: T x L
        if (!root.TryGetProperty("variational_strategy.lmc_coefficients", out var lmcEl))
            throw new InvalidDataException("JSON 缺少 lmc_coefficients 字段");
        int T = lmcEl.GetArrayLength();
        p.lmc_coeffs = new float[T][];
        for (int t = 0; t < T; t++)
        {
            var row = lmcEl[t];
            int LL = row.GetArrayLength();
            p.lmc_coeffs[t] = new float[LL];
            for (int j = 0; j < LL; j++) p.lmc_coeffs[t][j] = (float)row[j].GetDouble();
        }

        // lengthscale: from raw_lengthscale (L x 1 x D) => L x D with softplus
        if (!root.TryGetProperty("covar_module.base_kernel.raw_lengthscale", out var rawLenEl))
            throw new InvalidDataException("JSON 缺少 raw_lengthscale 字段");
        p.lengthscale = new float[L][];
        for (int l = 0; l < L; l++)
        {
            var l0 = rawLenEl[l];
            // 有的导出是 [ [D] ]，取第一层
            var arr = l0[0];
            int D = arr.GetArrayLength();
            p.lengthscale[l] = new float[D];
            for (int d = 0; d < D; d++) p.lengthscale[l][d] = Softplus((float)arr[d].GetDouble());
        }

        // outputscale: from raw_outputscale (L) with softplus
        if (!root.TryGetProperty("covar_module.raw_outputscale", out var rawOutEl))
            throw new InvalidDataException("JSON 缺少 raw_outputscale 字段");
        p.outputscale = new float[L];
        for (int l = 0; l < L; l++) p.outputscale[l] = Softplus((float)rawOutEl[l].GetDouble());

        // mean_constant: mean_module.raw_constant 可能是长度 T 的数组，这里取均值近似
        float meanConst = 0f;
        if (root.TryGetProperty("mean_module.raw_constant", out var meanEl))
        {
            int n = meanEl.GetArrayLength();
            double s = 0;
            for (int i = 0; i < n; i++) s += meanEl[i].GetDouble();
            meanConst = (float)(s / Math.Max(1, n));
        }
        p.mean_constant = meanConst;

        // noise_variance: JSON 无此字段，按输出维度 T 设一个小噪声
        p.noise_variance = new float[T];
        for (int i = 0; i < T; i++) p.noise_variance[i] = 1e-4f;

        return p;
    }
}

//// 4. 使用示例
//class Program
//{
//    static void Main()
//    {
//        var pred = new SVGPredictor("svgp_params.json");
//        float[][] testX = new float[][] {
//            new[]{0.1f,0.2f,0.3f},
//            new[]{0.6f,0.5f,0.4f},
//            new[] {0.12f, 0.34f, 1.21f},
//            new[]{0.58f, 0.34f, 0.91f},
//            new[]{1.21f, 0.00f, 1.00f},
//            new[]{0.00f, 0.79f, 0.62f}
//        };
//        //var (m, v) = pred.Predict(testX);
//        //for (int i = 0; i < m.Length; i++)
//        //{
//        //    Console.WriteLine($"Input {i}: mean={m[i][0]:F4}, var={v[i][0]:F4}");
//        //}
//        var (m, v) = pred.Predict(testX);
//        string savePath = "predictions.txt";
//        using (StreamWriter sw = new StreamWriter(savePath))
//        {
//            for (int i = 0; i < m.Length; i++)
//            {
//                // 构建输出字符串
//                string line = $"Input {i}: mean=[{string.Join(", ", m[i])}] var=[{string.Join(", ", v[i])}]";

//                // 写入文件
//                sw.WriteLine(line);

//                // 同时输出到控制台（可选）
//                Console.WriteLine(line);
//            }
//        }

//        Console.WriteLine($"Predictions saved to: {Path.GetFullPath(savePath)}");

//    }
//}
