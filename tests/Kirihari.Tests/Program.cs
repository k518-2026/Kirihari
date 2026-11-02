using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Kirihari.Models;
using Kirihari.Services;

// 画面なしの検査。編集内容の計算と、実際の ffmpeg での書き出しを確かめる。
// 使い方: dotnet run --project tests\Kirihari.Tests   （終了コード 0 なら全部合格）

// 画面の描画確認: dotnet run --project tests\Kirihari.Tests -- shot lite|pro <動画のフォルダー> <出力PNG>
if (args.Length >= 4 && args[0] == "shot")
{
    Tools.Locate();
    return Kirihari.Tests.ShotRunner.Run(args[1], args[2], args[3]);
}

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
}

MediaInfo Fake(string name, double dur, int w = 1920, int h = 1080, double fps = 29.97, string vc = "h264", string ac = "ac3") => new()
{
    Path = @"X:\" + name, Duration = dur, Size = (long)(dur * 1_000_000), VideoCodec = vc, Profile = "High", Level = 40,
    Width = w, Height = h, Fps = fps, FpsText = "30000/1001", PixFmt = "yuv420p", AudioCodec = ac, SampleRate = 48000, Channels = 2,
};

// ---------- 編集内容 ----------
{
    var a = Fake("a.mts", 100);
    var b = Fake("b.mts", 50);
    var e = new EditList();
    e.Add(new[] { a, b });
    Check("追加: 区間が2つで合計150秒", e.Count == 2 && Math.Abs(e.Total - 150) < 1e-9);
    Check("追加: 2つ目の開始は100秒", Math.Abs(e.Segments[1].OutStart - 100) < 1e-9);

    Check("分割: 60秒で分けられる", e.Split(60) && e.Count == 3);
    Check("分割: 前半は0〜60、後半は60〜100",
        e.Segments[0].Start == 0 && Math.Abs(e.Segments[0].End - 60) < 1e-9 &&
        Math.Abs(e.Segments[1].Start - 60) < 1e-9 && Math.Abs(e.Segments[1].End - 100) < 1e-9);
    Check("分割: 合計は変わらない", Math.Abs(e.Total - 150) < 1e-9);
    Check("分割: 端に近すぎると拒否", !e.Split(0.01) && !e.Split(60));

    e.Undo();
    Check("元に戻す: 2区間に戻る", e.Count == 2 && e.CanRedo);
    e.Redo();
    Check("やり直す: 3区間", e.Count == 3);
    e.Undo();

    // 範囲削除: 90〜120 秒 (a の 90-100 と b の 0-20 を削る)
    e.RemoveRange(90, 120);
    Check("範囲削除: 合計は120秒", Math.Abs(e.Total - 120) < 1e-9, e.Total.ToString(CultureInfo.InvariantCulture));
    Check("範囲削除: a は0〜90、b は20〜50",
        e.Count == 2 && Math.Abs(e.Segments[0].End - 90) < 1e-9 && Math.Abs(e.Segments[1].Start - 20) < 1e-9 &&
        Math.Abs(e.Segments[1].End - 50) < 1e-9);
    Check("範囲削除: 2つ目の開始は90秒", Math.Abs(e.Segments[1].OutStart - 90) < 1e-9);

    e.KeepRange(10, 100);
    Check("範囲だけ残す: 合計90秒", Math.Abs(e.Total - 90) < 1e-9, e.Total.ToString(CultureInfo.InvariantCulture));
    Check("範囲だけ残す: 先頭は a の10秒から", Math.Abs(e.Segments[0].Start - 10) < 1e-9);

    var seg = e.Segments[0];
    e.Move(seg, 1);
    Check("並べ替え: 先頭が後ろへ", ReferenceEquals(e.Segments[1], seg) && e.Segments[0].OutStart == 0);

    e.DeleteSegment(e.Segments[0]);
    Check("区間の削除", e.Count == 1);
    e.Clear();
    Check("全部消す", e.Count == 0 && e.Total == 0);

    // 探し方
    var e2 = new EditList();
    e2.Add(new[] { a, b });
    e2.TryLocate(100, out var s100, out var o100);
    Check("探す: ちょうど100秒は b の先頭", s100.Source == b && o100 == 0);
    e2.TryLocate(150, out var send, out var oend);
    Check("探す: 末尾は最後の区間の終わり", send.Source == b && Math.Abs(oend - 50) < 1e-9);
}

// ---------- 再エンコードなしで使えるかの判定 ----------
{
    var a = Fake("a.mts", 10);
    var same = Fake("b.mts", 10);
    var segs = new List<Segment> { new(a, 0, 10), new(same, 0, 10) };
    Check("判定: 同じ形式ならつなげる", ExportPlanner.CheckCopy(segs) == null);

    var diffSize = new List<Segment> { new(a, 0, 10), new(Fake("c.mp4", 5, 1280, 720), 0, 5) };
    Check("判定: 画面サイズが違うと不可", ExportPlanner.CheckCopy(diffSize) != null, ExportPlanner.CheckCopy(diffSize) ?? "");

    var diffFps = new List<Segment> { new(a, 0, 10), new(Fake("d.mp4", 5, fps: 25), 0, 5) };
    Check("判定: フレームレートが違うと不可", ExportPlanner.CheckCopy(diffFps) != null);

    var mpeg2 = new List<Segment> { new(Fake("m.mod", 10, vc: "mpeg2video"), 0, 10) };
    Check("判定: MPEG-2 は不可", ExportPlanner.CheckCopy(mpeg2) != null);
}

// ---------- Pro の設定・時刻入力・数字での区間調整 ----------
{
    Check("時刻入力: 1:02:03.5", Kirihari.ViewModels.MainViewModel.TryParseTime("1:02:03.5", out var t1) && Math.Abs(t1 - 3723.5) < 1e-9);
    Check("時刻入力: 23:45", Kirihari.ViewModels.MainViewModel.TryParseTime("23:45", out var t2) && Math.Abs(t2 - 1425) < 1e-9);
    Check("時刻入力: 125.5（秒）", Kirihari.ViewModels.MainViewModel.TryParseTime("125.5", out var t3) && Math.Abs(t3 - 125.5) < 1e-9);
    Check("時刻入力: 不正な文字は拒否", !Kirihari.ViewModels.MainViewModel.TryParseTime("abc", out _) && !Kirihari.ViewModels.MainViewModel.TryParseTime("", out _));

    var a = Fake("a.mts", 100);
    var e = new EditList();
    e.Add(new[] { a });
    Check("区間の数字調整: 10〜40秒にする", e.SetRange(e.Segments[0], 10, 40) && Math.Abs(e.Total - 30) < 1e-9);
    Check("区間の数字調整: 元の長さを超える終了は丸める", e.SetRange(e.Segments[0], 0, 500) && Math.Abs(e.Total - 100) < 1e-9);
    Check("区間の数字調整: 終了が開始以前なら拒否", !e.SetRange(e.Segments[0], 50, 50));

    var o = new ExportOptions();
    Check("設定: 既定では再エンコード不要", !o.RequiresEncode);
    o.HeightIndex = 4;
    Check("設定: 画面の高さを変えると再エンコード必須", o.RequiresEncode && o.TargetHeight == 720);
    o.HeightIndex = 0; o.FpsIndex = 1;
    Check("設定: フレームレートを変えても再エンコード必須", o.RequiresEncode && o.TargetFps == "24");
    o.FpsIndex = 0; o.DeinterlaceIndex = 1;
    Check("設定: インターレース常に解除でも必須", o.RequiresEncode && o.Deinterlace == true);
    o.DeinterlaceIndex = 2;
    Check("設定: 解除しないは必須にしない", !o.RequiresEncode && o.Deinterlace == false);
}

// ---------- 実際に ffmpeg で書き出す ----------
if (!Tools.Locate())
{
    Console.WriteLine("SKIP  ffmpeg が見つからないので書き出しの検査は省略");
}
else
{
    string work = Path.Combine(AppContext.BaseDirectory, "work");
    if (Directory.Exists(work)) Directory.Delete(work, true);
    Directory.CreateDirectory(work);
    Console.WriteLine("作業フォルダー: " + work);

    async Task<int> Ff(params string[] args)
    {
        var (code, _, err) = await ProcessUtil.RunAsync(Tools.Ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args));
        if (code != 0) Console.WriteLine("  ffmpeg: " + err.Trim());
        return code;
    }

    // カムコーダー風: MPEG-TS、H.264 + AC-3、キーフレームは0.5秒ごと
    string[] vEnc = { "-c:v", "libx264", "-g", "15", "-keyint_min", "15", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-preset", "ultrafast" };
    string a1 = Path.Combine(work, "00001.MTS"), a2 = Path.Combine(work, "00002.MTS"), c1 = Path.Combine(work, "small.mp4");
    await Ff(new[] { "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=20", "-f", "lavfi", "-i", "sine=f=440:d=20", "-shortest" }
        .Concat(vEnc).Concat(new[] { "-c:a", "ac3", "-f", "mpegts", a1 }).ToArray());
    await Ff(new[] { "-f", "lavfi", "-i", "mandelbrot=s=1280x720:r=30", "-f", "lavfi", "-i", "sine=f=880:d=12", "-t", "12" }
        .Concat(vEnc).Concat(new[] { "-c:a", "ac3", "-f", "mpegts", a2 }).ToArray());
    await Ff("-f", "lavfi", "-i", "smptebars=s=640x360:r=24:d=8", "-f", "lavfi", "-i", "sine=f=220:d=8", "-shortest",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast", "-c:a", "aac", "-f", "mp4", c1);
    // 音声なし
    string noaudio = Path.Combine(work, "silent.mp4");
    await Ff("-f", "lavfi", "-i", "testsrc=s=1280x720:r=30:d=5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast", "-f", "mp4", noaudio);

    var mA1 = await FFprobeService.ProbeAsync(a1);
    var mA2 = await FFprobeService.ProbeAsync(a2);
    var mC = await FFprobeService.ProbeAsync(c1);
    var mN = await FFprobeService.ProbeAsync(noaudio);
    Check("読み込み: MTS の長さ 20秒前後", Math.Abs(mA1.Duration - 20) < 0.5, $"{mA1.Duration:0.00}s start={mA1.StartTime:0.000} {mA1.Summary}");
    Check("読み込み: 形式の取得", mA1.Width == 1280 && mA1.Height == 720 && mA1.VideoCodec == "h264" && mA1.AudioCodec == "ac3");
    Check("読み込み: 音声なしを判別", !mN.HasAudio);

    async Task<(bool ok, string msg)> Export(string name, ExportMode mode, params Segment[] segs)
    {
        string outp = Path.Combine(work, name);
        var job = ExportPlanner.Build(segs, mode, Encoders.Cpu, outp);
        try
        {
            await ExportRunner.RunAsync(job, null, default);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    async Task<string> FrameMd5(string file, double t)
    {
        var (code, stdout, err) = await ProcessUtil.RunAsync(Tools.Ffmpeg,
            new[] { "-v", "error", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-f", "image2pipe", "-c:v", "bmp", "-" });
        return code == 0 ? Convert.ToHexString(MD5.HashData(stdout)) : "ERR " + err;
    }

    // 1) 再エンコードなし: A の 2〜10 秒 + B 全体
    {
        var segs = new[] { new Segment(mA1, 2, 10), new Segment(mA2, 0, mA2.Duration) };
        Check("判定: 同じカメラ想定の2本はつなげる", ExportPlanner.CheckCopy(segs) == null, ExportPlanner.CheckCopy(segs) ?? "");
        var (ok, msg) = await Export("copy.mp4", ExportMode.Copy, segs);
        Check("再エンコードなし: 書き出せた", ok, msg);
        if (ok)
        {
            string o = Path.Combine(work, "copy.mp4");
            double d = await FFprobeService.ProbeDurationAsync(o);
            double expect = 8 + mA2.Duration;
            Check("再エンコードなし: 長さが予定どおり（±1秒）", Math.Abs(d - expect) < 1.0, $"実際 {d:0.00}s / 予定 {expect:0.00}s");
            var mo = await FFprobeService.ProbeAsync(o);
            Check("再エンコードなし: 映像は H.264 のまま、音声は AAC", mo.VideoCodec == "h264" && mo.AudioCodec == "aac", mo.Summary);
            string m0 = await FrameMd5(o, 0), mSrc = await FrameMd5(a1, 2.0);
            Check("再エンコードなし: 先頭のコマが元の2秒地点と一致（時刻のずれなし）", m0 == mSrc, $"{m0[..8]} vs {mSrc[..8]}");
            Check("後始末: .part と一時ファイルが残らない",
                !File.Exists(o + ".part") && !File.Exists(o + ".list.txt"));
        }
    }

    // 2) 再エンコード: A の 2〜10 秒 + 小さい mp4 の 1〜5 秒（サイズも fps も違う）
    {
        var segs = new[] { new Segment(mA1, 2, 10), new Segment(mC, 1, 5) };
        string why = ExportPlanner.CheckCopy(segs);
        Check("判定: 形式が違う2本は再エンコード扱い", why != null, why ?? "");
        var (ok, msg) = await Export("encode.mp4", ExportMode.Encode, segs);
        Check("再エンコード: 書き出せた", ok, msg);
        if (ok)
        {
            string o = Path.Combine(work, "encode.mp4");
            double d = await FFprobeService.ProbeDurationAsync(o);
            Check("再エンコード: 長さが予定どおり（±0.3秒）", Math.Abs(d - 12) < 0.3, $"実際 {d:0.00}s / 予定 12.00s");
            var mo = await FFprobeService.ProbeAsync(o);
            Check("再エンコード: 画面サイズは先頭に合わせて 1280×720", mo.Width == 1280 && mo.Height == 720, mo.Summary);
            Check("再エンコード: 音声あり(AAC)", mo.AudioCodec == "aac");
        }
    }

    // 2b) Pro の設定: 480p・25fps・音声128k・画質26
    {
        var opts = new ExportOptions { HeightIndex = 5, FpsIndex = 2, AudioIndex = 0, Quality = 26 };
        var segs = new[] { new Segment(mA1, 0, 6) };
        string outp = Path.Combine(work, "pro-options.mp4");
        var job = ExportPlanner.Build(segs, ExportMode.Encode, Encoders.Cpu, outp, opts);
        bool ok = true; string msg = "";
        try { await ExportRunner.RunAsync(job, null, default); } catch (Exception ex) { ok = false; msg = ex.Message; }
        Check("Pro設定: 書き出せた", ok, msg);
        if (ok)
        {
            var mo = await FFprobeService.ProbeAsync(outp);
            Check("Pro設定: 854×480・25fps", mo.Height == 480 && Math.Abs(mo.Fps - 25) < 0.01 && mo.Width == 854, mo.Summary);
            double d = await FFprobeService.ProbeDurationAsync(outp);
            Check("Pro設定: 長さ 6秒", Math.Abs(d - 6) < 0.2, $"{d:0.00}s");
            Check("Pro設定: 画質26は18よりファイルが小さい", new FileInfo(outp).Length < new FileInfo(Path.Combine(work, "encode.mp4")).Length);
        }
    }

    // 3) 音声のない動画が混ざっても書き出せる
    {
        var segs = new[] { new Segment(mA1, 0, 3), new Segment(mN, 0, 3) };
        var (ok, msg) = await Export("mixed-audio.mp4", ExportMode.Encode, segs);
        Check("音声なしが混ざる: 書き出せた", ok, msg);
        if (ok)
        {
            double d = await FFprobeService.ProbeDurationAsync(Path.Combine(work, "mixed-audio.mp4"));
            Check("音声なしが混ざる: 長さ 6秒", Math.Abs(d - 6) < 0.3, $"{d:0.00}s");
        }
    }

    // 4) 中止したら .part が残らない
    {
        string outp = Path.Combine(work, "cancel.mp4");
        var segs = new[] { new Segment(mA1, 0, 20), new Segment(mA2, 0, 12) };
        var job = ExportPlanner.Build(segs, ExportMode.Encode, Encoders.Cpu, outp);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        bool cancelled = false;
        try { await ExportRunner.RunAsync(job, null, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception) { cancelled = true; }
        Check("中止: 完成ファイルも .part も残らない", cancelled && !File.Exists(outp) && !File.Exists(outp + ".part"));
    }

    // 5) 区間がとても多いとき（コマンドが長くなる）
    {
        var segs = new List<Segment>();
        // 0.25秒 = 7.5コマ。端数が出る長さをわざと80個つなぎ、ずれが溜まらないことを見る
        for (int i = 0; i < 80; i++) segs.Add(new Segment(mA1, i * 0.2, i * 0.2 + 0.25));
        var (ok, msg) = await Export("many.mp4", ExportMode.Encode, segs.ToArray());
        Check("区間80個: 書き出せた", ok, msg);
        if (ok)
        {
            string o = Path.Combine(work, "many.mp4");
            var (code, so, _) = await ProcessUtil.RunAsync(Tools.Ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-count_frames",
                "-show_entries", "stream=nb_read_frames:format=duration", "-of", "default=nw=1", o });
            string txt = System.Text.Encoding.UTF8.GetString(so);
            var (c2, sa, _) = await ProcessUtil.RunAsync(Tools.Ffprobe, new[] { "-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "csv=p=0", o });
            string ta = System.Text.Encoding.UTF8.GetString(sa);
            double dv = 0, da = 0;
            foreach (var line in ta.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Trim().Split(',');
                if (p.Length < 2) continue;
                double v = double.Parse(p[1], CultureInfo.InvariantCulture);
                if (p[0] == "video") dv = v; else if (p[0] == "audio") da = v;
            }
            // 8コマ×80 = 640コマが理想。つなぎ目で1コマ欠けることはあるが、80個つないでも1〜2コマ以内に収まる
            int nf = int.Parse(txt.Split('\n').First(x => x.StartsWith("nb_read_frames=")).Trim().Substring(15));
            Check("区間80個: 映像は640コマ（±2）", Math.Abs(nf - 640) <= 2, $"{nf}コマ");
            Check("区間80個: 映像と音声の長さが0.1秒以内で一致", Math.Abs(dv - da) < 0.1, $"映像 {dv:0.000}s / 音声 {da:0.000}s");
        }
    }

    // 7) 旧 SwiftCut から移した出力形式・速さ・音量（2026-11-02）
    {
        async Task<string> Streams(string file)
        {
            var (_, so, _) = await ProcessUtil.RunAsync(Tools.Ffprobe, new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", file });
            return string.Join(" ", System.Text.Encoding.UTF8.GetString(so).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        }
        async Task<(bool ok, string msg, string path)> Run(string name, ExportMode mode, ExportOptions o, params Segment[] segs)
        {
            string outp = Path.Combine(work, name);
            try
            {
                var job = ExportPlanner.Build(segs, mode, Encoders.Cpu, outp, o);
                await ExportRunner.RunAsync(job, null, default);
                return (File.Exists(outp), "", outp);
            }
            catch (Exception ex) { return (false, ex.Message, outp); }
        }

        Check("形式: 既定は MP4・再エンコード不要（Lite の動作は変わらない）", new ExportOptions().Format == OutputFormat.Mp4H264 && !new ExportOptions().RequiresEncode);
        Check("形式: MKV・MOV は再エンコードなしで可", !new ExportOptions { FormatIndex = 2 }.RequiresEncode && !new ExportOptions { FormatIndex = 3 }.RequiresEncode);
        Check("形式: H.265・WebM・GIF・MP3・WAV は再エンコード", new[] { 1, 4, 5, 6, 7 }.All(i => new ExportOptions { FormatIndex = i }.RequiresEncode));
        Check("形式: 速さ・音量を変えると再エンコード、音声なしはそのままで可",
            new ExportOptions { SpeedIndex = 5 }.RequiresEncode && new ExportOptions { VolumeIndex = 3 }.RequiresEncode && !new ExportOptions { VolumeIndex = 5 }.RequiresEncode);
        string af4 = ExportPlanner.AudioPostFilter(new ExportOptions { SpeedIndex = 6 });
        Check("速さ: 4倍は atempo を2段に分ける", af4.Split(',').Length == 2 && af4.Split(',').All(x => x.StartsWith("atempo=2")), af4);

        var two = new[] { new Segment(mA1, 2, 6), new Segment(mA2, 0, 4) };

        var r = await Run("copy.mkv", ExportMode.Copy, new ExportOptions { FormatIndex = 2 }, two);
        Check("MKV（再エンコードなし）: 書き出せた", r.ok, r.msg);
        if (r.ok) Check("MKV: 映像 h264 のまま・matroska", (await Streams(r.path)).Contains("h264,video") && (await FFprobeService.ProbeAsync(r.path)).Summary != null, await Streams(r.path));

        r = await Run("mute-copy.mp4", ExportMode.Copy, new ExportOptions { VolumeIndex = 5 }, two);
        Check("音声なし（再エンコードなし）: 音声トラックが無い", r.ok && !(await Streams(r.path)).Contains("audio"), r.ok ? await Streams(r.path) : r.msg);

        r = await Run("h265.mp4", ExportMode.Encode, new ExportOptions { FormatIndex = 1, PresetIndex = 0 }, two);
        Check("H.265: 書き出せた・hevc + aac", r.ok && (await Streams(r.path)).Contains("hevc,video") && (await Streams(r.path)).Contains("aac,audio"), r.ok ? await Streams(r.path) : r.msg);

        r = await Run("vp9.webm", ExportMode.Encode, new ExportOptions { FormatIndex = 4, HeightIndex = 5 }, two);
        Check("WebM: 書き出せた・vp9 + opus", r.ok && (await Streams(r.path)).Contains("vp9,video") && (await Streams(r.path)).Contains("opus,audio"), r.ok ? await Streams(r.path) : r.msg);

        r = await Run("anim.gif", ExportMode.Encode, new ExportOptions { FormatIndex = 5 }, new Segment(mA1, 0, 3));
        if (r.ok)
        {
            var mg = await FFprobeService.ProbeAsync(r.path);
            Check("GIF: 書き出せた・480p に縮小・15fps・音声なし", mg.VideoCodec == "gif" && mg.Height == 480 && mg.Fps >= 14 && mg.Fps <= 17 /* GIF のコマ間隔は 1/100 秒単位なので 15fps は 16.67 と読める */ && !mg.HasAudio, mg.Summary);
        }
        else Check("GIF: 書き出せた", false, r.msg);

        r = await Run("audio.mp3", ExportMode.Encode, new ExportOptions { FormatIndex = 6 }, two);
        if (r.ok)
        {
            string st = await Streams(r.path);
            double d = await FFprobeService.ProbeDurationAsync(r.path);
            Check("MP3: 音声だけ・長さ 8秒", st == "mp3,audio" && Math.Abs(d - 8) < 0.3, $"{st} {d:0.00}s");
        }
        else Check("MP3: 書き出せた", false, r.msg);

        r = await Run("audio.wav", ExportMode.Encode, new ExportOptions { FormatIndex = 7 }, new Segment(mA1, 0, 4));
        Check("WAV: pcm_s16le", r.ok && (await Streams(r.path)) == "pcm_s16le,audio", r.ok ? await Streams(r.path) : r.msg);

        r = await Run("noaudio.mp3", ExportMode.Encode, new ExportOptions { FormatIndex = 6 }, new Segment(mN, 0, 3));
        Check("MP3: 音声のない動画は分かる言葉で断る", !r.ok && r.msg.Contains("音声の入っていない"), r.msg);

        r = await Run("speed2x.mp4", ExportMode.Encode, new ExportOptions { SpeedIndex = 5 }, two);
        if (r.ok)
        {
            double d = await FFprobeService.ProbeDurationAsync(r.path);
            Check("速さ2倍: 8秒の素材が4秒に", Math.Abs(d - 4) < 0.3, $"{d:0.00}s");
        }
        else Check("速さ2倍: 書き出せた", false, r.msg);

        r = await Run("half.mp4", ExportMode.Encode, new ExportOptions { SpeedIndex = 1, VolumeIndex = 1 }, new Segment(mA1, 0, 3));
        if (r.ok)
        {
            double d = await FFprobeService.ProbeDurationAsync(r.path);
            Check("速さ0.5倍＋音量50%: 3秒が6秒に", Math.Abs(d - 6) < 0.3, $"{d:0.00}s");
        }
        else Check("速さ0.5倍＋音量50%: 書き出せた", false, r.msg);

        r = await Run("gif-fast.gif", ExportMode.Encode, new ExportOptions { FormatIndex = 5, SpeedIndex = 5 }, new Segment(mA1, 0, 4));
        if (r.ok)
        {
            var mg = await FFprobeService.ProbeAsync(r.path);
            double d = await FFprobeService.ProbeDurationAsync(r.path);
            Check("GIF＋速さ2倍: 2秒・15fps のまま", Math.Abs(d - 2) < 0.3 && mg.Fps >= 14 && mg.Fps <= 17 /* GIF のコマ間隔は 1/100 秒単位なので 15fps は 16.67 と読める */, $"{d:0.00}s {mg.Summary}");
        }
        else Check("GIF＋速さ2倍: 書き出せた", false, r.msg);
    }

    // 6) GPU
    var gpu = await Encoders.DetectGpuAsync();
    Console.WriteLine("INFO  GPU エンコーダー: " + (gpu?.Label ?? "なし"));
    if (gpu != null)
    {
        string outp = Path.Combine(work, "gpu.mp4");
        var job = ExportPlanner.Build(new[] { new Segment(mA1, 0, 5) }, ExportMode.Encode, gpu, outp);
        try { await ExportRunner.RunAsync(job, null, default); Check("GPU: 書き出せた", File.Exists(outp)); }
        catch (Exception ex) { Check("GPU: 書き出せた", false, ex.Message); }
    }
}

Console.WriteLine($"\n合格 {pass} / 不合格 {fail}");
return fail == 0 ? 0 : 1;
