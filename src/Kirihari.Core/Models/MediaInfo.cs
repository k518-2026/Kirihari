using System.Globalization;

namespace Kirihari.Models;

/// <summary>ffprobe で調べた動画ファイル1本ぶんの情報。</summary>
public sealed class MediaInfo
{
    public string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public double Duration { get; init; }
    /// <summary>コンテナ先頭の時刻（MTS/TS は 0 でないことが多い）。</summary>
    public double StartTime { get; init; }
    public long Size { get; init; }

    public string VideoCodec { get; init; }
    public string Profile { get; init; }
    public int Level { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    /// <summary>"30000/1001" のような分数表記（ffmpeg にそのまま渡す）。</summary>
    public string FpsText { get; init; }
    public double Sar { get; init; } = 1.0;
    public string PixFmt { get; init; }
    public bool Interlaced { get; init; }

    public string AudioCodec { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    public int ColorIndex { get; set; }

    public bool HasAudio => !string.IsNullOrEmpty(AudioCodec);

    /// <summary>画素の縦横比を1:1に直したときの幅（偶数）。HDV の 1440x1080 などは 1920 になる。</summary>
    public int DisplayWidth => Math.Max(2, (int)Math.Round(Width * Sar / 2.0) * 2);

    public string Summary
    {
        get
        {
            string fps = Fps.ToString("0.##", CultureInfo.InvariantCulture);
            string v = $"{Width}×{Height} {fps}fps {VideoCodec}{(Interlaced ? " インターレース" : "")}";
            string a = HasAudio ? $"{AudioCodec} {Channels}ch" : "音声なし";
            return v + " / " + a;
        }
    }
}
