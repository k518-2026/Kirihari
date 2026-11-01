using System.Globalization;
using System.Text.Json;
using Kirihari.Models;

namespace Kirihari.Services;

public static class FFprobeService
{
    /// <summary>動画ファイルの長さ・形式を調べる。映像がないファイルや壊れたファイルは例外にする。</summary>
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await ProcessUtil.RunAsync(Tools.Ffprobe,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, ct, 30000);
        if (code != 0 || stdout.Length == 0)
            throw new InvalidOperationException("読み込めませんでした。" + FirstLine(stderr));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        JsonElement video = default, audio = default;
        bool hasV = false, hasA = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = Str(s, "codec_type");
                if (type == "video" && !hasV && !IsCoverArt(s)) { video = s; hasV = true; }
                else if (type == "audio" && !hasA) { audio = s; hasA = true; }
            }
        }
        if (!hasV) throw new InvalidOperationException("映像が入っていないファイルです。");

        root.TryGetProperty("format", out var fmt);
        double duration = Num(fmt, "duration");
        if (duration <= 0) duration = Num(video, "duration");
        if (duration <= 0) throw new InvalidOperationException("動画の長さがわかりません（壊れている可能性があります）。");

        string fpsText = Str(video, "avg_frame_rate");
        double fps = ParseRatio(fpsText);
        if (fps <= 0) { fpsText = Str(video, "r_frame_rate"); fps = ParseRatio(fpsText); }
        if (fps <= 0) { fpsText = "30"; fps = 30; }

        string order = Str(video, "field_order");
        bool interlaced = order is "tt" or "bb" or "tb" or "bt";

        return new MediaInfo
        {
            Path = path,
            Duration = duration,
            StartTime = Math.Max(0, Num(fmt, "start_time")),
            Size = (long)Num(fmt, "size"),
            VideoCodec = Str(video, "codec_name") ?? "?",
            Profile = Str(video, "profile") ?? "",
            Level = (int)Num(video, "level"),
            Width = (int)Num(video, "width"),
            Height = (int)Num(video, "height"),
            Fps = fps,
            FpsText = fpsText,
            Sar = ParseSar(Str(video, "sample_aspect_ratio")),
            PixFmt = Str(video, "pix_fmt") ?? "",
            Interlaced = interlaced,
            AudioCodec = hasA ? Str(audio, "codec_name") : null,
            SampleRate = hasA ? (int)Num(audio, "sample_rate") : 0,
            Channels = hasA ? (int)Num(audio, "channels") : 0,
        };
    }

    /// <summary>出力ファイルの長さだけ手早く調べる（書き出し後の確認用）。</summary>
    public static async Task<double> ProbeDurationAsync(string path, CancellationToken ct = default)
    {
        var (code, stdout, _) = await ProcessUtil.RunAsync(Tools.Ffprobe,
            new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path }, ct, 30000);
        if (code != 0) return -1;
        string s = System.Text.Encoding.UTF8.GetString(stdout).Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : -1;
    }

    private static bool IsCoverArt(JsonElement s)
        => s.TryGetProperty("disposition", out var d) && Num(d, "attached_pic") == 1;

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    private static double ParseRatio(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var p = s.Split('/');
        if (p.Length == 2 &&
            double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static double ParseSar(string s)
    {
        if (string.IsNullOrEmpty(s)) return 1;
        var p = s.Split(':');
        if (p.Length == 2 &&
            double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0)
            return n / d;
        return 1;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }
}
