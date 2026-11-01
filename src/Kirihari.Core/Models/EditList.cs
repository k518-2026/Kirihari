using System.Collections.ObjectModel;
using Kirihari.Common;

namespace Kirihari.Models;

/// <summary>
/// 編集内容そのもの。区間（Segment）を並べた1本のタイムラインで、すべての操作はここを通る。
/// 時刻は断りがなければ「完成品の先頭からの秒」。UI には依存しない。
/// </summary>
public sealed class EditList : ObservableObject
{
    private readonly record struct Snap(MediaInfo Source, double Start, double End);

    private readonly Stack<List<Snap>> _undo = new();
    private readonly Stack<List<Snap>> _redo = new();

    /// <summary>これより短い切れ端は作らない（秒）。</summary>
    public const double MinPiece = 0.05;

    public ObservableCollection<Segment> Segments { get; } = new();

    public double Total => Segments.Sum(s => s.Duration);
    public int Count => Segments.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>区間の並びや長さが変わるたびに呼ばれる。</summary>
    public event Action Changed;

    // ---------- 検索 ----------

    /// <summary>完成品の時刻 t がどの区間の何秒目にあたるか。末尾（t = Total）は最後の区間の終わり。</summary>
    public bool TryLocate(double t, out Segment seg, out double offset)
    {
        seg = null;
        offset = 0;
        if (Segments.Count == 0) return false;
        foreach (var s in Segments)
        {
            if (t < s.OutEnd - 1e-9)
            {
                seg = s;
                offset = Math.Max(0, t - s.OutStart);
                return true;
            }
        }
        seg = Segments[^1];
        offset = seg.Duration;
        return true;
    }

    // ---------- 編集 ----------

    public void Add(IEnumerable<MediaInfo> clips)
    {
        var list = clips.ToList();
        if (list.Count == 0) return;
        Push();
        foreach (var m in list) Segments.Add(new Segment(m, 0, m.Duration));
        Commit();
    }

    /// <summary>t の位置で区間を2つに分ける。端に近すぎて分けられないときは false。</summary>
    public bool Split(double t)
    {
        if (!TryLocate(t, out var seg, out var off)) return false;
        if (off < MinPiece || seg.Duration - off < MinPiece) return false;
        Push();
        int i = Segments.IndexOf(seg);
        Segments.RemoveAt(i);
        Segments.Insert(i, new Segment(seg.Source, seg.Start, seg.Start + off));
        Segments.Insert(i + 1, new Segment(seg.Source, seg.Start + off, seg.End));
        Commit();
        return true;
    }

    public void DeleteSegment(Segment seg)
    {
        if (seg == null || !Segments.Contains(seg)) return;
        Push();
        Segments.Remove(seg);
        Commit();
    }

    /// <summary>[a, b] を取り除いて前後をつなぐ。複数の区間にまたがってもよい。</summary>
    public void RemoveRange(double a, double b)
    {
        if (b <= a) return;
        Slice(new[] { (0.0, a), (b, Total) });
    }

    /// <summary>[a, b] だけを残す。</summary>
    public void KeepRange(double a, double b)
    {
        if (b <= a) return;
        Slice(new[] { (a, b) });
    }

    public void Move(Segment seg, int delta)
    {
        int i = Segments.IndexOf(seg);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Segments.Count) return;
        Push();
        Segments.Move(i, j);
        Commit();
    }

    /// <summary>区間の元ファイル内での開始・終了を数字で直す（秒）。範囲外は元ファイルの長さに丸める。</summary>
    public bool SetRange(Segment seg, double start, double end)
    {
        int i = Segments.IndexOf(seg);
        if (i < 0) return false;
        start = Math.Clamp(start, 0, seg.Source.Duration);
        end = Math.Clamp(end, 0, seg.Source.Duration);
        if (end - start < MinPiece) return false;
        Push();
        Segments[i] = new Segment(seg.Source, start, end);
        Commit();
        return true;
    }

    public void Clear()
    {
        if (Segments.Count == 0) return;
        Push();
        Segments.Clear();
        Commit();
    }

    /// <summary>残したい時刻範囲（完成品の時刻）を順に並べ直して区間を作り替える。</summary>
    private void Slice(IEnumerable<(double a, double b)> keep)
    {
        var next = new List<Snap>();
        foreach (var (ka, kb) in keep)
        {
            foreach (var s in Segments)
            {
                double lo = Math.Max(ka, s.OutStart);
                double hi = Math.Min(kb, s.OutEnd);
                if (hi - lo < MinPiece) continue;
                next.Add(new Snap(s.Source, s.Start + (lo - s.OutStart), s.Start + (hi - s.OutStart)));
            }
        }
        Push();
        Apply(next);
        Commit();
    }

    // ---------- 元に戻す / やり直す ----------

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Apply(_undo.Pop());
        Commit();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Apply(_redo.Pop());
        Commit();
    }

    private List<Snap> Snapshot() => Segments.Select(s => new Snap(s.Source, s.Start, s.End)).ToList();

    private void Push()
    {
        _undo.Push(Snapshot());
        if (_undo.Count > 200)
        {
            // 古いものから捨てる（Stack は新しい順に列挙される）
            var newest = _undo.Take(200).Reverse().ToList();
            _undo.Clear();
            foreach (var k in newest) _undo.Push(k);
        }
        _redo.Clear();
    }

    private void Apply(List<Snap> snap)
    {
        Segments.Clear();
        foreach (var s in snap) Segments.Add(new Segment(s.Source, s.Start, s.End));
    }

    private void Commit()
    {
        Renumber();
        Raise(nameof(Total));
        Raise(nameof(Count));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
        Changed?.Invoke();
    }

    private void Renumber()
    {
        double t = 0;
        int n = 1;
        foreach (var s in Segments)
        {
            s.Number = n++;
            s.OutStart = t;
            t += s.Duration;
        }
    }
}
