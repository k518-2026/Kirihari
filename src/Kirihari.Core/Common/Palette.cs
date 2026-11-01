using System.Windows.Media;

namespace Kirihari.Common;

/// <summary>元ファイルごとに色を割り当てる（タイムラインと一覧で共通）。</summary>
public static class Palette
{
    private static readonly Color[] Colors =
    {
        Color.FromRgb(0x4C, 0x8D, 0xFF), Color.FromRgb(0xE8, 0xA3, 0x3D),
        Color.FromRgb(0x4C, 0xC2, 0xA0), Color.FromRgb(0xC2, 0x6D, 0xE0),
        Color.FromRgb(0xE5, 0x64, 0x6B), Color.FromRgb(0x7F, 0xBF, 0x4A),
        Color.FromRgb(0x3F, 0xB8, 0xD9), Color.FromRgb(0xD9, 0x8A, 0x5C),
    };

    private static readonly SolidColorBrush[] Brushes = Colors.Select(c =>
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }).ToArray();

    public static int Count => Colors.Length;
    public static Color ColorAt(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
    public static Brush Brush(int index) => Brushes[((index % Colors.Length) + Colors.Length) % Colors.Length];
}
