using System.Text;
using Kirihari.Common;
using Kirihari.Models;

namespace Kirihari.Services;

public enum ExportMode { Copy, Encode }

/// <summary>ffmpeg に渡す引数と、終わったあとに消す一時ファイル。</summary>
public sealed class ExportJob
{
    public List<string> Args { get; } = new();
    public List<string> TempFiles { get; } = new();
    public double TotalSeconds { get; set; }
    public string PartPath { get; set; }
    public string FinalPath { get; set; }
    public ExportMode Mode { get; set; }
}

public static class ExportPlanner
{
    private static readonly string[] CopyableVideo = { "h264", "hevc", "av1" };
    private static readonly string[] CopyableAudio = { "aac", "mp3" };

    /// <summary>
    /// 再エンコードなしでつなげるか調べる。つなげるなら null、だめなら理由を返す。
    /// 形式（コーデック・サイズ・フレームレートなど）が1つでも違うと、そのままではつなげられない。
    /// </summary>
    public static string CheckCopy(IReadOnlyList<Segment> segs)
    {
        if (segs.Count == 0) return "動画がありません";
        var f = segs[0].Source;
        if (!CopyableVideo.Contains(f.VideoCodec))
            return $"映像の形式（{f.VideoCodec}）はそのままMP4に入れられないため";

        foreach (var s in segs.Select(x => x.Source).Distinct())
        {
            string n = $"「{s.FileName}」は";
            if (s.VideoCodec != f.VideoCodec) return n + $"映像の形式が違う（{s.VideoCodec}）ため";
            if (s.Width != f.Width || s.Height != f.Height) return n + $"画面サイズが違う（{s.Width}×{s.Height}）ため";
            if (Math.Abs(s.Fps - f.Fps) > 0.01) return n + $"フレームレートが違う（{s.Fps:0.##}fps）ため";
            if (Math.Abs(s.Sar - f.Sar) > 0.001) return n + "画素の縦横比が違うため";
            if (s.PixFmt != f.PixFmt) return n + "色の形式が違うため";
            if (s.Profile != f.Profile || s.Level != f.Level) return n + "映像の細かい設定（プロファイル）が違うため";
            if (s.HasAudio != f.HasAudio) return n + "音声の有無が違うため";
            if (s.HasAudio && (s.AudioCodec != f.AudioCodec || s.SampleRate != f.SampleRate || s.Channels != f.Channels))
                return n + "音声の形式が違うため";
        }
        return null;
    }

    public static ExportJob Build(IReadOnlyList<Segment> segs, ExportMode mode, EncoderChoice enc, string finalPath,
        ExportOptions opts = null)
    {
        opts ??= new ExportOptions();
        if (segs.Count == 0) throw new InvalidOperationException("動画がありません。");
        var job = new ExportJob
        {
            Mode = mode,
            FinalPath = finalPath,
            PartPath = finalPath + ".part",
            TotalSeconds = segs.Sum(s => s.Duration),
        };
        job.Args.AddRange(new[] { "-hide_banner", "-y", "-nostats", "-loglevel", "warning", "-progress", "pipe:1" });
        if (mode == ExportMode.Copy) BuildCopy(job, segs);
        else BuildEncode(job, segs, enc ?? Encoders.Cpu, opts);
        return job;
    }

    // ---------- 再エンコードなし ----------

    private static void BuildCopy(ExportJob job, IReadOnlyList<Segment> segs)
    {
        var first = segs[0].Source;
        string listPath = job.FinalPath + ".list.txt";

        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var s in segs)
        {
            sb.Append("file '").Append(s.Source.Path.Replace("'", "'\\''")).Append("'\n");
            // TS 系は先頭の時刻が 0 でないので、その分をずらして指定する
            if (s.Start > 0.001) sb.Append("inpoint ").Append(TimeFormat.Sec(s.Start + s.Source.StartTime)).Append('\n');
            if (s.End < s.Source.Duration - 0.001) sb.Append("outpoint ").Append(TimeFormat.Sec(s.End + s.Source.StartTime)).Append('\n');
        }
        File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));
        job.TempFiles.Add(listPath);

        job.Args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0:v:0" });
        if (first.HasAudio) job.Args.AddRange(new[] { "-map", "0:a:0" });

        job.Args.AddRange(new[] { "-c:v", "copy" });
        if (first.VideoCodec == "hevc") job.Args.AddRange(new[] { "-tag:v", "hvc1" });

        if (first.HasAudio)
        {
            if (CopyableAudio.Contains(first.AudioCodec)) job.Args.AddRange(new[] { "-c:a", "copy" });
            else job.Args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" }); // AC-3 など。音声だけ作り直す（軽い）
        }
        job.Args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", job.PartPath });
    }

    // ---------- 再エンコード ----------

    private static void BuildEncode(ExportJob job, IReadOnlyList<Segment> segs, EncoderChoice enc, ExportOptions opts)
    {
        var first = segs[0].Source;
        int w = first.DisplayWidth;
        int h = first.Height / 2 * 2;
        if (opts.TargetHeight > 0)
        {
            double aspect = (double)first.DisplayWidth / first.Height;
            h = opts.TargetHeight;
            w = Math.Max(2, (int)Math.Round(h * aspect / 2.0) * 2);
        }
        string fps = opts.TargetFps ?? first.FpsText;

        // 区間の長さを「コマ数ちょうど」にそろえる。そろえないと区間ごとに端数のコマが出て、
        // 切った数だけ映像と音声がじわじわずれる。
        double rate = opts.TargetFps != null ? ExportOptions.ParseRate(opts.TargetFps) : first.Fps;
        var frames = segs.Select(s => Math.Max(1, (int)Math.Round(s.Duration * rate, MidpointRounding.AwayFromZero))).ToArray();
        job.TotalSeconds = frames.Sum() / rate;

        // 区間ごとに別の入力として開き、必要な位置から読む（長い動画の後半でも頭からデコードしない）
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Start > 0.001) job.Args.AddRange(new[] { "-ss", TimeFormat.Sec(s.Start) });
            // 少し長めに読み、映像は trim でコマ数ちょうどに切る
            job.Args.AddRange(new[] { "-t", TimeFormat.Sec(frames[i] / rate + 0.5), "-i", s.Source.Path });
        }

        var g = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var m = s.Source;
            bool doDeint = opts.Deinterlace ?? m.Interlaced;
            string deint = doDeint ? "yadif=mode=send_frame:parity=auto:deint=all," : "";
            g.Append($"[{i}:v:0]{deint}scale=trunc(iw*sar/2)*2:ih,setsar=1,")
             .Append($"scale={w}:{h}:force_original_aspect_ratio=decrease,")
             .Append($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setpts=PTS-STARTPTS,fps={fps},")
             .Append($"trim=end_frame={frames[i]},setpts=PTS-STARTPTS,format=yuv420p[v{i}];\n");

            string d = TimeFormat.Sec(frames[i] / rate);
            if (m.HasAudio)
                g.Append($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=end={d},asetpts=PTS-STARTPTS[a{i}];\n");
            else
                g.Append($"anullsrc=r=48000:cl=stereo,atrim=end={d},asetpts=PTS-STARTPTS[a{i}];\n");
        }
        for (int i = 0; i < segs.Count; i++) g.Append($"[v{i}][a{i}]");
        g.Append($"concat=n={segs.Count}:v=1:a=1[v][a]");

        string graph = g.ToString();
        if (graph.Length > 12000)
        {
            // コマンドが長くなりすぎるのでファイルで渡す
            string scriptPath = job.FinalPath + ".graph.txt";
            File.WriteAllText(scriptPath, graph, new UTF8Encoding(false));
            job.TempFiles.Add(scriptPath);
            job.Args.AddRange(new[] { "-/filter_complex", scriptPath });
        }
        else
        {
            job.Args.AddRange(new[] { "-filter_complex", graph });
        }

        job.Args.AddRange(new[] { "-map", "[v]", "-map", "[a]", "-c:v", enc.Codec });
        job.Args.AddRange(enc.Build(opts.Quality, opts.Preset));
        job.Args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", opts.AudioBitrateK + "k", "-ar", "48000", "-ac", "2",
                                  "-movflags", "+faststart", "-f", "mp4", job.PartPath });
    }

    /// <summary>再エンコードなしのときの出力サイズの目安（バイト）。</summary>
    public static long EstimateCopySize(IReadOnlyList<Segment> segs)
        => (long)segs.Sum(s => s.Source.Duration > 0 ? s.Duration / s.Source.Duration * s.Source.Size : 0);
}
