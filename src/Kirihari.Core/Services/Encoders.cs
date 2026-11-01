namespace Kirihari.Services;

public sealed class EncoderChoice
{
    public string Codec { get; init; }
    public string Label { get; init; }
    public bool IsGpu { get; init; }
    /// <summary>画質の数値（小さいほど高画質）と CPU 用の速さ設定から、ffmpeg の指定を作る。</summary>
    public Func<int, string, string[]> Build { get; init; }
}

/// <summary>再エンコードに使う H.264 エンコーダー。GPU が使えるかは実際にテストして決める。</summary>
public static class Encoders
{
    public static readonly EncoderChoice Cpu = new()
    {
        Codec = "libx264",
        Label = "CPU（x264）",
        IsGpu = false,
        Build = (q, preset) => new[] { "-preset", preset ?? "veryfast", "-crf", q.ToString(), "-profile:v", "high" },
    };

    private static readonly EncoderChoice[] GpuCandidates =
    {
        new() { Codec = "h264_nvenc", Label = "GPU（NVIDIA）", IsGpu = true,
                Build = (q, _) => new[] { "-preset", "p5", "-rc", "vbr", "-cq", q.ToString(), "-b:v", "0", "-profile:v", "high" } },
        new() { Codec = "h264_qsv", Label = "GPU（Intel）", IsGpu = true,
                Build = (q, _) => new[] { "-preset", "medium", "-global_quality", q.ToString(), "-profile:v", "high" } },
        new() { Codec = "h264_amf", Label = "GPU（AMD）", IsGpu = true,
                Build = (q, _) => new[] { "-quality", "quality", "-rc", "cqp", "-qp_i", q.ToString(), "-qp_p", (q + 2).ToString(), "-qp_b", (q + 4).ToString(), "-profile:v", "high" } },
    };

    /// <summary>使える GPU エンコーダーを探す。短い映像を実際に書き出せたものだけを採用する。なければ null。</summary>
    public static async Task<EncoderChoice> DetectGpuAsync()
    {
        foreach (var e in GpuCandidates)
        {
            try
            {
                var args = new List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                    "-i", "color=c=black:s=1280x720:r=30:d=1", "-frames:v", "10", "-pix_fmt", "yuv420p",
                    "-c:v", e.Codec,
                };
                args.AddRange(e.Build(20, null));
                args.AddRange(new[] { "-f", "null", "-" });
                var (code, _, _) = await ProcessUtil.RunAsync(Tools.Ffmpeg, args, default, 15000);
                if (code == 0) return e;
            }
            catch { /* 次の候補へ */ }
        }
        return null;
    }
}
