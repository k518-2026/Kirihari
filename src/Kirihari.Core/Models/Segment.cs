using System.Windows.Media;
using Kirihari.Common;

namespace Kirihari.Models;

/// <summary>書き出しに使う「元ファイルの Start〜End 秒」1区間。この区間を並べたものが完成品になる。</summary>
public sealed class Segment : ObservableObject
{
    private int _number;
    private double _outStart;

    public Segment(MediaInfo source, double start, double end)
    {
        Source = source;
        Start = start;
        End = end;
    }

    public MediaInfo Source { get; }
    public double Start { get; }
    public double End { get; }
    public double Duration => End - Start;

    /// <summary>一覧での番号（1から）。</summary>
    public int Number { get => _number; set { if (Set(ref _number, value)) Raise(nameof(Title)); } }

    /// <summary>完成品の中でこの区間が始まる時刻。</summary>
    public double OutStart { get => _outStart; set => Set(ref _outStart, value); }
    public double OutEnd => OutStart + Duration;

    public string Title => $"{Number}. {Source.FileName}";
    public string RangeText => $"{TimeFormat.Format(Start)} → {TimeFormat.Format(End)}　（{TimeFormat.Format(Duration)}）";
    public Brush ColorBrush => Palette.Brush(Source.ColorIndex);
}
