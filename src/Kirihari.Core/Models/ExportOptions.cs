using System.Globalization;
using Kirihari.Common;

namespace Kirihari.Models;

/// <summary>出力の形式。</summary>
public enum OutputFormat { Mp4H264, Mp4H265, Mkv, Mov, Webm, Gif, Mp3, Wav }

/// <summary>
/// 再エンコードで書き出すときの細かい設定。Lite は既定のまま使い、Pro は画面から変える。
/// </summary>
public sealed class ExportOptions : ObservableObject
{
    public static readonly int[] Heights = { 0, 2160, 1440, 1080, 720, 480 };
    public static readonly string[] HeightLabels = { "元のまま", "2160p（4K）", "1440p", "1080p（フルHD）", "720p（HD）", "480p" };

    public static readonly string[] Fpses = { null, "24", "25", "30", "60", "24000/1001", "30000/1001", "60000/1001" };
    public static readonly string[] FpsLabels = { "元のまま", "24", "25", "30", "60", "23.976", "29.97", "59.94" };

    public static readonly string[] DeinterlaceLabels = { "自動（インターレースなら解除）", "常に解除する", "解除しない" };

    public static readonly int[] AudioKbps = { 128, 192, 256, 320 };
    public static readonly string[] AudioLabels = { "128 kbps", "192 kbps（標準）", "256 kbps", "320 kbps" };

    public static readonly string[] EncoderLabels = { "自動（使えるなら GPU）", "GPU を使う", "CPU（x264）を使う" };

    public static readonly string[] Presets = { "ultrafast", "veryfast", "faster", "medium", "slow" };
    public static readonly string[] PresetLabels = { "最速（ファイル大）", "速い（標準）", "やや速い", "ふつう", "遅い（ファイル小）" };

    // ---- 旧 SwiftCut から移した設定（2026-11-02）。既定値は従来の Kirihari と同じ（MP4・等速・音量そのまま・1本にまとめる） ----

    public static readonly OutputFormat[] Formats =
    {
        OutputFormat.Mp4H264, OutputFormat.Mp4H265, OutputFormat.Mkv, OutputFormat.Mov,
        OutputFormat.Webm, OutputFormat.Gif, OutputFormat.Mp3, OutputFormat.Wav,
    };
    public static readonly string[] FormatLabels =
    {
        "MP4（H.264・標準）", "MP4（H.265・小さめ）", "MKV（H.264）", "MOV（H.264）",
        "WebM（VP9 + Opus）", "GIF アニメ（最大 480p・15fps）", "MP3（音声だけ）", "WAV（音声だけ）",
    };

    public static readonly double[] Speeds = { 1.0, 0.5, 0.75, 1.25, 1.5, 2.0, 4.0 };
    public static readonly string[] SpeedLabels = { "等速（そのまま）", "0.5 倍（ゆっくり）", "0.75 倍", "1.25 倍", "1.5 倍", "2 倍", "4 倍" };

    /// <summary>音量の倍率。0 は音声を入れない。</summary>
    public static readonly double[] Volumes = { 1.0, 0.5, 0.75, 1.5, 2.0, 0.0 };
    public static readonly string[] VolumeLabels = { "そのまま", "50%", "75%", "150%", "200%", "音声を入れない" };

    private int _formatIndex, _speedIndex, _volumeIndex;
    private bool _splitFiles;

    public int FormatIndex { get => _formatIndex; set { if (Set(ref _formatIndex, value)) { Raise(nameof(RequiresEncode)); Raise(nameof(Format)); Raise(nameof(Extension)); } } }
    public int SpeedIndex { get => _speedIndex; set { if (Set(ref _speedIndex, value)) Raise(nameof(RequiresEncode)); } }
    public int VolumeIndex { get => _volumeIndex; set { if (Set(ref _volumeIndex, value)) Raise(nameof(RequiresEncode)); } }

    /// <summary>区間ごとに別のファイルとして書き出す（旧 SwiftCut の「分割書き出し」）。</summary>
    public bool SplitFiles { get => _splitFiles; set => Set(ref _splitFiles, value); }

    public OutputFormat Format => Formats[Math.Clamp(FormatIndex, 0, Formats.Length - 1)];
    public double Speed => Speeds[Math.Clamp(SpeedIndex, 0, Speeds.Length - 1)];
    public double Volume => Volumes[Math.Clamp(VolumeIndex, 0, Volumes.Length - 1)];
    public bool Mute => Volume <= 0;
    public bool IsAudioOnly => Format is OutputFormat.Mp3 or OutputFormat.Wav;

    public string Extension => Format switch
    {
        OutputFormat.Mkv => ".mkv",
        OutputFormat.Mov => ".mov",
        OutputFormat.Webm => ".webm",
        OutputFormat.Gif => ".gif",
        OutputFormat.Mp3 => ".mp3",
        OutputFormat.Wav => ".wav",
        _ => ".mp4",
    };

    public string SaveFilter => Format switch
    {
        OutputFormat.Mkv => "MKV 動画 (*.mkv)|*.mkv",
        OutputFormat.Mov => "MOV 動画 (*.mov)|*.mov",
        OutputFormat.Webm => "WebM 動画 (*.webm)|*.webm",
        OutputFormat.Gif => "GIF アニメ (*.gif)|*.gif",
        OutputFormat.Mp3 => "MP3 音声 (*.mp3)|*.mp3",
        OutputFormat.Wav => "WAV 音声 (*.wav)|*.wav",
        _ => "MP4 動画 (*.mp4)|*.mp4",
    };

    /// <summary>再エンコードなしでは書き出せない理由。書き出せるなら null。</summary>
    public string EncodeReason =>
        HeightIndex != 0 || FpsIndex != 0 || DeinterlaceIndex == 1 ? "画面サイズ・フレームレート・インターレースを変える設定のため"
        : Format is OutputFormat.Mp4H265 ? "H.265 に作り直す設定のため"
        : Format is OutputFormat.Webm or OutputFormat.Gif or OutputFormat.Mp3 or OutputFormat.Wav ? $"出力形式（{FormatLabels[FormatIndex]}）に作り直す必要があるため"
        : Math.Abs(Speed - 1.0) > 1e-9 ? "速さを変える設定のため"
        : !Mute && Math.Abs(Volume - 1.0) > 1e-9 ? "音量を変える設定のため"
        : null;

    private int _quality = 18;
    private int _heightIndex, _fpsIndex, _deintIndex, _audioIndex = 1, _encoderIndex, _presetIndex = 1;

    /// <summary>画質の数値（CRF 相当。小さいほど高画質・ファイル大）。</summary>
    public int Quality { get => _quality; set { if (Set(ref _quality, Math.Clamp(value, 10, 35))) Raise(nameof(QualityText)); } }
    public string QualityText => Quality switch
    {
        <= 15 => $"{Quality}（最高画質・ファイル大）",
        <= 19 => $"{Quality}（高画質・標準）",
        <= 23 => $"{Quality}（バランス）",
        _ => $"{Quality}（軽量・画質は控えめ）",
    };

    public int HeightIndex { get => _heightIndex; set { if (Set(ref _heightIndex, value)) Raise(nameof(RequiresEncode)); } }
    public int FpsIndex { get => _fpsIndex; set { if (Set(ref _fpsIndex, value)) Raise(nameof(RequiresEncode)); } }
    public int DeinterlaceIndex { get => _deintIndex; set { if (Set(ref _deintIndex, value)) Raise(nameof(RequiresEncode)); } }
    public int AudioIndex { get => _audioIndex; set => Set(ref _audioIndex, value); }
    public int EncoderIndex { get => _encoderIndex; set => Set(ref _encoderIndex, value); }
    public int PresetIndex { get => _presetIndex; set => Set(ref _presetIndex, value); }

    public int TargetHeight => Heights[Math.Clamp(HeightIndex, 0, Heights.Length - 1)];
    public string TargetFps => Fpses[Math.Clamp(FpsIndex, 0, Fpses.Length - 1)];
    public int AudioBitrateK => AudioKbps[Math.Clamp(AudioIndex, 0, AudioKbps.Length - 1)];
    public string Preset => Presets[Math.Clamp(PresetIndex, 0, Presets.Length - 1)];

    /// <summary>"auto" / true / false の3択。</summary>
    public bool? Deinterlace => DeinterlaceIndex switch { 1 => true, 2 => false, _ => null };

    /// <summary>画面サイズ・フレームレート・インターレース解除・出力形式・速さ・音量の指定によっては、再エンコードなしでは書き出せない。</summary>
    public bool RequiresEncode => EncodeReason != null;

    public static double ParseRate(string fps)
    {
        if (string.IsNullOrEmpty(fps)) return 0;
        var p = fps.Split('/');
        if (p.Length == 2 &&
            double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return n / d;
        return double.TryParse(fps, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
