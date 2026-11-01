using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kirihari.Models;
using Kirihari.Services;
using Kirihari.ViewModels;

namespace Kirihari.Views;

/// <summary>
/// プレビュー画面。Windows が再生できる形式は MediaElement で再生し、
/// できない形式（一部のカムコーダー形式など）は ffmpeg で1コマずつ取り出して表示する。
/// 区間の境目では次の区間へ自動でつなぎ、完成品を頭から通して見られる。
/// </summary>
public sealed class PreviewController
{
    private readonly MediaElement _me;
    private readonly Image _img;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private string _path;
    private bool _opened;
    private double _pendingPos;
    private bool _wantPlay;
    private bool _playing;
    private Segment _playSeg;
    private CancellationTokenSource _grabCts;

    public PreviewController(MediaElement me, Image img, MainViewModel vm)
    {
        _me = me;
        _img = img;
        _vm = vm;

        _me.LoadedBehavior = MediaState.Manual;
        _me.UnloadedBehavior = MediaState.Manual;
        _me.ScrubbingEnabled = true;
        _me.MediaOpened += OnOpened;
        _me.MediaFailed += OnFailed;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick();

        _vm.UserSeeked += OnUserSeeked;
        _vm.PlayToggleRequested += Toggle;
        _vm.Project.Changed += Pause;
        _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Muted)) _me.IsMuted = _vm.Muted;
        else if (e.PropertyName == nameof(MainViewModel.PlaybackSpeed)) _me.SpeedRatio = _vm.PlaybackSpeed;
    }

    // ---------- 再生・停止 ----------

    private void Toggle()
    {
        if (_playing) Pause(); else Play();
    }

    private void Play()
    {
        var project = _vm.Project;
        if (project.Count == 0) return;
        if (_vm.Position >= project.Total - 0.05) _vm.Position = 0;
        if (!project.TryLocate(_vm.Position, out var seg, out var off)) return;

        if (_failed.Contains(seg.Source.Path))
        {
            _vm.Status = "この動画はこのパソコンではプレビュー再生できません（コマの確認と書き出しはできます）。";
            return;
        }

        _playing = true;
        _vm.IsPlaying = true;
        _playSeg = seg;
        double src = seg.Start + off;
        ShowVideo();
        if (_path == seg.Source.Path && _opened)
        {
            _me.Position = TimeSpan.FromSeconds(src);
            _me.SpeedRatio = _vm.PlaybackSpeed;
            _me.Play();
        }
        else Load(seg.Source.Path, src, true);
        _timer.Start();
    }

    public void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _timer.Stop();
        try { _me.Pause(); } catch { }
        _vm.IsPlaying = false;
    }

    private void Tick()
    {
        if (!_playing || !_opened || _playSeg == null) return;
        double pos = _me.Position.TotalSeconds;
        var seg = _playSeg;

        if (pos >= seg.End - 0.02)
        {
            var list = _vm.Project.Segments;
            int idx = list.IndexOf(seg) + 1;
            if (idx <= 0 || idx >= list.Count)
            {
                Pause();
                _vm.SetPositionFromPlayback(_vm.Project.Total);
                return;
            }
            var next = list[idx];
            _playSeg = next;
            if (next.Source.Path == seg.Source.Path)
            {
                if (Math.Abs(next.Start - pos) > 0.15) _me.Position = TimeSpan.FromSeconds(next.Start);
            }
            else Load(next.Source.Path, next.Start, true);
            _vm.SetPositionFromPlayback(next.OutStart);
            return;
        }
        _vm.SetPositionFromPlayback(seg.OutStart + Math.Max(0, pos - seg.Start));
    }

    // ---------- 位置を動かしたとき ----------

    private void OnUserSeeked()
    {
        var project = _vm.Project;
        if (!project.TryLocate(_vm.Position, out var seg, out var off))
        {
            Pause();
            _img.Source = null;
            _me.Visibility = Visibility.Hidden;
            return;
        }
        double src = seg.Start + off;

        if (_playing)
        {
            _playSeg = seg;
            if (_path == seg.Source.Path && _opened) _me.Position = TimeSpan.FromSeconds(src);
            else Load(seg.Source.Path, src, true);
            return;
        }
        ShowFrame(seg.Source, src);
    }

    private void ShowFrame(MediaInfo m, double srcPos)
    {
        if (_failed.Contains(m.Path)) { ShowGrab(m, srcPos); return; }
        ShowVideo();
        if (_path == m.Path && _opened) _me.Position = TimeSpan.FromSeconds(srcPos);
        else Load(m.Path, srcPos, false);
    }

    private void Load(string path, double srcPos, bool play)
    {
        _path = path;
        _opened = false;
        _pendingPos = srcPos;
        _wantPlay = play;
        _me.Source = new Uri(path);
    }

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        _me.IsMuted = _vm.Muted;
        _me.SpeedRatio = _vm.PlaybackSpeed;
        _me.Position = TimeSpan.FromSeconds(_pendingPos);
        if (_wantPlay) _me.Play();
        else { _me.Play(); _me.Pause(); } // 一度動かして、止まった状態でも最初の1コマを出す
    }

    private void OnFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_path != null) _failed.Add(_path);
        _opened = false;
        if (_playing) Pause();
        _vm.Status = "この動画はプレビュー再生に対応していません。止まった画面の確認と書き出しはできます。";
        if (_vm.Project.TryLocate(_vm.Position, out var seg, out var off)) ShowGrab(seg.Source, seg.Start + off);
    }

    // ---------- ffmpeg でコマを取り出す表示 ----------

    private void ShowVideo()
    {
        _img.Visibility = Visibility.Collapsed;
        _me.Visibility = Visibility.Visible;
    }

    private async void ShowGrab(MediaInfo m, double srcPos)
    {
        _grabCts?.Cancel();
        var cts = _grabCts = new CancellationTokenSource();
        _me.Visibility = Visibility.Hidden;
        _img.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(60, cts.Token); // つまみを動かしている間は最後の位置だけ取り出す
            var bmp = await FrameGrabber.GrabAsync(m.Path, srcPos, cts.Token);
            if (!cts.IsCancellationRequested && bmp != null) _img.Source = bmp;
        }
        catch (OperationCanceledException) { }
        catch { }
    }
}
