using System.Globalization;
using Kirihari.Common;

namespace Kirihari.Models;

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

    /// <summary>画面サイズ・フレームレート・インターレース解除を変える指定があると、再エンコードなしでは書き出せない。</summary>
    public bool RequiresEncode => HeightIndex != 0 || FpsIndex != 0 || DeinterlaceIndex == 1;

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
