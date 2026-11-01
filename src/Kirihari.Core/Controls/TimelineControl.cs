using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Kirihari.Common;
using Kirihari.Models;

namespace Kirihari.Controls;

/// <summary>
/// 完成品の全体を1本の帯で見せるタイムライン。元ファイルごとに色が違い、クリック／ドラッグで再生位置を動かす。
/// Ctrl+ホイールで拡大、ホイールで左右に動かす。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
        nameof(Project), typeof(EditList), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnProjectChanged));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender |
                                            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public static readonly DependencyProperty MarkInProperty = DependencyProperty.Register(
        nameof(MarkIn), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkOutProperty = DependencyProperty.Register(
        nameof(MarkOut), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public EditList Project { get => (EditList)GetValue(ProjectProperty); set => SetValue(ProjectProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double MarkIn { get => (double)GetValue(MarkInProperty); set => SetValue(MarkInProperty, value); }
    public double MarkOut { get => (double)GetValue(MarkOutProperty); set => SetValue(MarkOutProperty, value); }

    private const double RulerHeight = 20;
    private const double BarTop = 26;
    private const double BarHeight = 38;

    private double _viewStart;
    private double _viewSpan; // 0 のときは全体表示
    private bool _dragging;

    private static readonly Typeface Face = new("Yu Gothic UI");
    private static readonly double[] TickSteps =
        { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.IBeam;
    }

    /// <summary>全体が見える倍率に戻す。</summary>
    public void ZoomAll()
    {
        _viewSpan = 0;
        _viewStart = 0;
        InvalidateVisual();
    }

    private double Total => Project?.Total ?? 0;
    private double Span => _viewSpan > 0 && _viewSpan < Total ? _viewSpan : Math.Max(Total, 0.001);
    private double Start => Span >= Total ? 0 : Math.Clamp(_viewStart, 0, Total - Span);

    private double XOf(double t) => (t - Start) / Span * ActualWidth;
    private double TimeOf(double x) => Start + x / Math.Max(1, ActualWidth) * Span;

    private static void OnProjectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TimelineControl)d;
        if (e.OldValue is EditList o) o.Changed -= c.OnProjectEdited;
        if (e.NewValue is EditList n) n.Changed += c.OnProjectEdited;
    }

    private void OnProjectEdited()
    {
        if (_viewSpan >= Total) _viewSpan = 0;
        InvalidateVisual();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TimelineControl)d;
        c.FollowPlayhead();
    }

    /// <summary>拡大中に再生位置が画面の外へ出たら、見える範囲をずらす。</summary>
    private void FollowPlayhead()
    {
        if (_viewSpan <= 0 || _viewSpan >= Total) return;
        double p = Position;
        if (p < _viewStart || p > _viewStart + _viewSpan)
            _viewStart = Math.Clamp(p - _viewSpan * 0.1, 0, Total - _viewSpan);
    }

    // ---------- 描画 ----------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var bg = (Brush)Application.Current.Resources["BgDeepBrush"];
        var border = (Brush)Application.Current.Resources["BorderBrush2"];
        var dim = (Brush)Application.Current.Resources["TextDimBrush"];
        dc.DrawRectangle(bg, new Pen(border, 1), new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));

        double total = Total;
        if (Project == null || total <= 0)
        {
            Text(dc, "動画を入れると、ここに全体の長さが帯で表示されます", 12, 28, dim, 12);
            return;
        }

        DrawRuler(dc, w, dim);

        // 区間の帯
        foreach (var s in Project.Segments)
        {
            double x0 = XOf(s.OutStart), x1 = XOf(s.OutEnd);
            if (x1 < 0 || x0 > w) continue;
            double l = Math.Max(0, x0), r = Math.Min(w, x1);
            var color = Palette.ColorAt(s.Source.ColorIndex);
            var fill = new SolidColorBrush(Color.FromArgb(0xD0, color.R, color.G, color.B));
            fill.Freeze();
            var rect = new Rect(l, BarTop, Math.Max(1, r - l - 1), BarHeight);
            dc.DrawRectangle(fill, null, rect);
            if (r - l > 24)
            {
                string label = s.Number.ToString(CultureInfo.InvariantCulture);
                if (r - l > 90) label += "  " + s.Source.FileName;
                Text(dc, label, l + 5, BarTop + 10, Brushes.White, 12, r - l - 8);
            }
        }

        // 範囲の印
        bool hasIn = !double.IsNaN(MarkIn), hasOut = !double.IsNaN(MarkOut);
        if (hasIn && hasOut && MarkOut > MarkIn)
        {
            double a = Math.Clamp(XOf(MarkIn), 0, w), b = Math.Clamp(XOf(MarkOut), 0, w);
            var band = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xD0, 0x40));
            band.Freeze();
            dc.DrawRectangle(band, null, new Rect(a, RulerHeight, Math.Max(1, b - a), h - RulerHeight - 2));
        }
        var markPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x40)), 2);
        if (hasIn) DrawFlag(dc, XOf(MarkIn), markPen, true);
        if (hasOut) DrawFlag(dc, XOf(MarkOut), markPen, false);

        // 再生位置
        double px = XOf(Position);
        if (px >= -2 && px <= w + 2)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
            dc.DrawLine(new Pen(red, 2), new Point(px, 0), new Point(px, h));
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(px - 6, 0), true, true);
                g.LineTo(new Point(px + 6, 0), true, false);
                g.LineTo(new Point(px, 10), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(red, null, tri);
        }
    }

    private static void DrawFlag(DrawingContext dc, double x, Pen pen, bool isIn)
    {
        dc.DrawLine(pen, new Point(x, RulerHeight), new Point(x, RulerHeight + BarHeight + 12));
        double d = isIn ? 7 : -7;
        dc.DrawLine(pen, new Point(x, RulerHeight), new Point(x + d, RulerHeight));
    }

    private void DrawRuler(DrawingContext dc, double w, Brush dim)
    {
        double span = Span, start = Start;
        double step = TickSteps[^1];
        foreach (var s in TickSteps)
        {
            if (s / span * w >= 80) { step = s; break; }
        }
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x5E, 0x6C)), 1);
        double first = Math.Ceiling(start / step) * step;
        for (double t = first; t <= start + span + 1e-9; t += step)
        {
            double x = Math.Round(XOf(t)) + 0.5;
            dc.DrawLine(pen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            Text(dc, TimeFormat.Format(t, step < 1 ? 1 : 0), x + 3, 2, dim, 11);
        }
    }

    private void Text(DrawingContext dc, string s, double x, double y, Brush brush, double size, double maxWidth = double.MaxValue)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth < double.MaxValue) ft.MaxTextWidth = Math.Max(1, maxWidth);
        dc.DrawText(ft, new Point(x, y));
    }

    // ---------- 操作 ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Total <= 0) return;
        _dragging = true;
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void SeekTo(double x)
    {
        Position = Math.Clamp(TimeOf(x), 0, Total);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double total = Total;
        if (total <= 0) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double mouseX = e.GetPosition(this).X;
            double tAt = TimeOf(mouseX);
            double newSpan = Span * (e.Delta > 0 ? 0.7 : 1 / 0.7);
            newSpan = Math.Clamp(newSpan, Math.Min(total, 2.0), total);
            double frac = mouseX / Math.Max(1, ActualWidth);
            if (newSpan >= total) { _viewSpan = 0; _viewStart = 0; }
            else
            {
                _viewSpan = newSpan;
                _viewStart = Math.Clamp(tAt - frac * newSpan, 0, total - newSpan);
            }
        }
        else if (_viewSpan > 0 && _viewSpan < total)
        {
            _viewStart = Math.Clamp(_viewStart - Math.Sign(e.Delta) * _viewSpan * 0.15, 0, total - _viewSpan);
        }
        InvalidateVisual();
        e.Handled = true;
    }
}
