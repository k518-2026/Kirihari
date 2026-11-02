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
        if (mode == ExportMode.Copy) BuildCopy(job, segs, opts);
        else BuildEncode(job, segs, enc ?? Encoders.Cpu, opts);
        return job;
    }

    /// <summary>形式ごとの ffmpeg の出力形式名（.part に書くので明示する）。</summary>
    private static string Muxer(OutputFormat f) => f switch
    {
        OutputFormat.Mkv => "matroska",
        OutputFormat.Mov => "mov",
        OutputFormat.Webm => "webm",
        OutputFormat.Gif => "gif",
        OutputFormat.Mp3 => "mp3",
        OutputFormat.Wav => "wav",
        _ => "mp4",
    };

    private static void AddMuxer(ExportJob job, OutputFormat f)
    {
        if (f is OutputFormat.Mp4H264 or OutputFormat.Mp4H265 or OutputFormat.Mov)
            job.Args.AddRange(new[] { "-movflags", "+faststart" });
        job.Args.AddRange(new[] { "-f", Muxer(f), job.PartPath });
    }

    /// <summary>速さ（atempo は 0.5〜2.0 が安全なので段重ね）と音量の音声フィルター。何もなければ空。</summary>
    public static string AudioPostFilter(ExportOptions o)
    {
        var parts = new List<string>();
        double remain = o.Speed;
        if (Math.Abs(remain - 1.0) > 1e-9)
        {
            while (remain > 2.0) { parts.Add("atempo=2.0"); remain /= 2.0; }
            while (remain < 0.5) { parts.Add("atempo=0.5"); remain /= 0.5; }
            if (Math.Abs(remain - 1.0) > 1e-9) parts.Add("atempo=" + TimeFormat.Sec(remain));
        }
        if (!o.Mute && Math.Abs(o.Volume - 1.0) > 1e-9) parts.Add("volume=" + TimeFormat.Sec(o.Volume));
        return string.Join(",", parts);
    }

    // ---------- 再エンコードなし ----------

    private static void BuildCopy(ExportJob job, IReadOnlyList<Segment> segs, ExportOptions opts)
    {
        var first = segs[0].Source;
        bool withAudio = first.HasAudio && !opts.Mute;
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
        if (withAudio) job.Args.AddRange(new[] { "-map", "0:a:0" });

        job.Args.AddRange(new[] { "-c:v", "copy" });
        if (first.VideoCodec == "hevc" && opts.Format != OutputFormat.Mkv) job.Args.AddRange(new[] { "-tag:v", "hvc1" });

        if (withAudio)
        {
            if (CopyableAudio.Contains(first.AudioCodec)) job.Args.AddRange(new[] { "-c:a", "copy" });
            else job.Args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" }); // AC-3 など。音声だけ作り直す（軽い）
        }
        else job.Args.Add("-an");
        // 再エンコードなしで入れられる箱は MP4・MKV・MOV（それ以外は EncodeReason で再エンコードに回る）
        AddMuxer(job, opts.Format is OutputFormat.Mkv or OutputFormat.Mov ? opts.Format : OutputFormat.Mp4H264);
    }

    // ---------- 再エンコード ----------

    private static void BuildEncode(ExportJob job, IReadOnlyList<Segment> segs, EncoderChoice enc, ExportOptions opts)
    {
        var first = segs[0].Source;
        var format = opts.Format;
        bool isGif = format == OutputFormat.Gif;
        bool wantVideo = !opts.IsAudioOnly;
        bool wantAudio = !isGif && (opts.IsAudioOnly || !opts.Mute);
        if (opts.IsAudioOnly && segs.All(s => !s.Source.HasAudio))
            throw new InvalidOperationException("音声の入っていない動画なので、音声だけの形式（MP3・WAV）では書き出せません。");

        int w = first.DisplayWidth;
        int h = first.Height / 2 * 2;
        int targetHeight = opts.TargetHeight;
        if (isGif && targetHeight == 0 && first.Height > 480) targetHeight = 480;   // GIF は大きいと何百 MB にもなる
        if (targetHeight > 0)
        {
            double aspect = (double)first.DisplayWidth / first.Height;
            h = targetHeight;
            w = Math.Max(2, (int)Math.Round(h * aspect / 2.0) * 2);
        }
        string targetFps = opts.TargetFps ?? (isGif ? "15" : null);
        string fps = targetFps ?? first.FpsText;

        // 区間の長さを「コマ数ちょうど」にそろえる。そろえないと区間ごとに端数のコマが出て、
        // 切った数だけ映像と音声がじわじわずれる。
        double rate = targetFps != null ? ExportOptions.ParseRate(targetFps) : first.Fps;
        var frames = segs.Select(s => Math.Max(1, (int)Math.Round(s.Duration * rate, MidpointRounding.AwayFromZero))).ToArray();
        double speed = opts.Speed;
        job.TotalSeconds = frames.Sum() / rate / speed;

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
            if (wantVideo)
            {
                bool doDeint = opts.Deinterlace ?? m.Interlaced;
                string deint = doDeint ? "yadif=mode=send_frame:parity=auto:deint=all," : "";
                g.Append($"[{i}:v:0]{deint}scale=trunc(iw*sar/2)*2:ih,setsar=1,")
                 .Append($"scale={w}:{h}:force_original_aspect_ratio=decrease,")
                 .Append($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setpts=PTS-STARTPTS,fps={fps},")
                 .Append($"trim=end_frame={frames[i]},setpts=PTS-STARTPTS,format=yuv420p[v{i}];\n");
            }

            if (wantAudio)
            {
                string d = TimeFormat.Sec(frames[i] / rate);
                if (m.HasAudio)
                    g.Append($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=end={d},asetpts=PTS-STARTPTS[a{i}];\n");
                else
                    g.Append($"anullsrc=r=48000:cl=stereo,atrim=end={d},asetpts=PTS-STARTPTS[a{i}];\n");
            }
        }
        for (int i = 0; i < segs.Count; i++)
        {
            if (wantVideo) g.Append($"[v{i}]");
            if (wantAudio) g.Append($"[a{i}]");
        }
        g.Append($"concat=n={segs.Count}:v={(wantVideo ? 1 : 0)}:a={(wantAudio ? 1 : 0)}");
        if (wantVideo) g.Append("[vc]");
        if (wantAudio) g.Append("[ac]");

        // つないだ後に、速さ・GIF のパレット・音量をかける（旧 SwiftCut と同じ考え方）
        if (wantVideo)
        {
            var post = new List<string>();
            if (Math.Abs(speed - 1.0) > 1e-9) post.Add("setpts=PTS/" + TimeFormat.Sec(speed));
            if (isGif)
            {
                if (Math.Abs(speed - 1.0) > 1e-9) post.Add("fps=" + fps);   // 速さを変えてもコマ数を増やさない
                post.Add("split[gs0][gs1];[gs0]palettegen=stats_mode=diff[gp];[gs1][gp]paletteuse=dither=bayer:bayer_scale=5");
            }
            g.Append(";\n[vc]").Append(post.Count == 0 ? "null" : string.Join(",", post)).Append("[v]");
        }
        if (wantAudio)
        {
            string af = AudioPostFilter(opts);
            g.Append(";\n[ac]").Append(af.Length == 0 ? "anull" : af).Append("[a]");
        }

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

        // 映像
        if (wantVideo)
        {
            job.Args.AddRange(new[] { "-map", "[v]" });
            switch (format)
            {
                case OutputFormat.Gif:
                    break;   // パレットは上のフィルターで作った
                case OutputFormat.Mp4H265:
                    // H.265 は CPU（x265）で作る。x264 と同じ見た目になるよう、画質の数値は 4 大きくする
                    job.Args.AddRange(new[] { "-c:v", "libx265", "-preset", opts.Preset, "-crf", (opts.Quality + 4).ToString(),
                                              "-tag:v", "hvc1", "-pix_fmt", "yuv420p" });
                    break;
                case OutputFormat.Webm:
                    // VP9 の CRF は 0〜63。x264 の 18 がおよそ 31 にあたる
                    job.Args.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", (opts.Quality + 13).ToString(), "-b:v", "0",
                                              "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p" });
                    break;
                default:   // MP4 / MKV / MOV（H.264。GPU があれば GPU）
                    job.Args.AddRange(new[] { "-c:v", enc.Codec });
                    job.Args.AddRange(enc.Build(opts.Quality, opts.Preset));
                    job.Args.AddRange(new[] { "-pix_fmt", "yuv420p" });
                    break;
            }
        }
        else job.Args.Add("-vn");

        // 音声
        if (wantAudio)
        {
            job.Args.AddRange(new[] { "-map", "[a]" });
            switch (format)
            {
                case OutputFormat.Wav: job.Args.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
                case OutputFormat.Mp3: job.Args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", opts.AudioBitrateK + "k" }); break;
                case OutputFormat.Webm: job.Args.AddRange(new[] { "-c:a", "libopus", "-b:a", opts.AudioBitrateK + "k" }); break;
                default: job.Args.AddRange(new[] { "-c:a", "aac", "-b:a", opts.AudioBitrateK + "k" }); break;
            }
            job.Args.AddRange(new[] { "-ar", "48000", "-ac", "2" });
        }
        else job.Args.Add("-an");

        AddMuxer(job, format);
    }

    /// <summary>再エンコードなしのときの出力サイズの目安（バイト）。</summary>
    public static long EstimateCopySize(IReadOnlyList<Segment> segs)
        => (long)segs.Sum(s => s.Source.Duration > 0 ? s.Duration / s.Source.Duration * s.Source.Size : 0);
}
