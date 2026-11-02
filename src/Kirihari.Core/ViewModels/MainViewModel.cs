using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Kirihari.Common;
using Kirihari.Models;
using Kirihari.Services;
using Microsoft.Win32;

namespace Kirihari.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mts", ".m2ts", ".m2t", ".mod", ".tod", ".mp4", ".m4v", ".mov", ".mpg", ".mpeg", ".vob",
        ".avi", ".mkv", ".wmv", ".ts", ".dv", ".3gp", ".mxf", ".flv", ".webm",
    };

    public const string OpenFilter =
        "動画ファイル|*.mts;*.m2ts;*.m2t;*.mod;*.tod;*.mp4;*.m4v;*.mov;*.mpg;*.mpeg;*.vob;*.avi;*.mkv;*.wmv;*.ts;*.dv;*.3gp;*.mxf;*.flv;*.webm|すべてのファイル|*.*";

    private double _position;
    private bool _fromPlayback;
    private Segment _current;
    private bool _isPlaying;
    private bool _muted;
    private double _markIn = double.NaN, _markOut = double.NaN;
    private string _status = "";
    private bool _ffmpegMissing;
    private bool _loading;

    private bool _preferCopy = true;
    private string _compatReason;
    private EncoderChoice _gpu;

    private bool _exportBusy;
    private double _exportPercent;
    private string _exportText = "";
    private string _resultText = "";
    private string _resultPath;
    private CancellationTokenSource _exportCts;

    public MainViewModel()
    {
        Project = new EditList();
        Project.Changed += OnProjectChanged;
        Options.PropertyChanged += (_, _) => RefreshExportInfo();

        TrimBeforeCommand = new RelayCommand(_ => TrimBefore(), _ => Project.Count > 0 && Position > EditList.MinPiece);
        TrimAfterCommand = new RelayCommand(_ => TrimAfter(), _ => Project.Count > 0 && Position < Project.Total - EditList.MinPiece);
        GoToTimeCommand = new RelayCommand(_ => GoToTime(), _ => Project.Count > 0);
        ApplySegmentRangeCommand = new RelayCommand(_ => ApplySegmentRange(), _ => _current != null);
        ResetSpeedCommand = new RelayCommand(_ => PlaybackSpeed = 1.0);

        AddFilesCommand = new RelayCommand(_ => PickFiles());
        MoveUpCommand = new RelayCommand(_ => Project.Move(_current, -1), _ => _current != null);
        MoveDownCommand = new RelayCommand(_ => Project.Move(_current, +1), _ => _current != null);
        DeleteSegmentCommand = new RelayCommand(_ => DeleteCurrent(), _ => _current != null);
        UndoCommand = new RelayCommand(_ => Project.Undo(), _ => Project.CanUndo);
        RedoCommand = new RelayCommand(_ => Project.Redo(), _ => Project.CanRedo);
        ClearAllCommand = new RelayCommand(_ => ClearAll(), _ => Project.Count > 0 && !ExportBusy);

        TogglePlayCommand = new RelayCommand(_ => PlayToggleRequested?.Invoke(), _ => Project.Count > 0);
        StepCommand = new RelayCommand(p => Step(ToInt(p)), _ => Project.Count > 0);
        JumpSecondsCommand = new RelayCommand(p => SeekUser(Position + ToInt(p)), _ => Project.Count > 0);
        GoStartCommand = new RelayCommand(_ => SeekUser(0), _ => Project.Count > 0);
        GoEndCommand = new RelayCommand(_ => SeekUser(Project.Total), _ => Project.Count > 0);
        PrevSegmentCommand = new RelayCommand(_ => JumpSegment(-1), _ => Project.Count > 0);
        NextSegmentCommand = new RelayCommand(_ => JumpSegment(+1), _ => Project.Count > 0);
        ZoomAllCommand = new RelayCommand(_ => ZoomAllRequested?.Invoke());

        SplitCommand = new RelayCommand(_ => Split(), _ => Project.Count > 0);
        SetInCommand = new RelayCommand(_ => SetIn(), _ => Project.Count > 0);
        SetOutCommand = new RelayCommand(_ => SetOut(), _ => Project.Count > 0);
        RemoveRangeCommand = new RelayCommand(_ => RemoveRange(), _ => HasRange);
        KeepRangeCommand = new RelayCommand(_ => KeepRange(), _ => HasRange);
        ClearMarksCommand = new RelayCommand(_ => ClearMarks(), _ => !double.IsNaN(_markIn) || !double.IsNaN(_markOut));

        ExportCommand = new RelayCommand(async _ => await ExportAsync(), _ => Project.Count > 0 && !ExportBusy && !FfmpegMissing);
        CancelExportCommand = new RelayCommand(_ => _exportCts?.Cancel(), _ => ExportBusy);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => _resultPath != null);
        PlayResultCommand = new RelayCommand(_ => PlayResult(), _ => _resultPath != null);
        ChooseFfmpegCommand = new RelayCommand(_ => ChooseFfmpeg());

        FfmpegMissing = !Tools.Locate();
        RefreshExportInfo();
        if (!FfmpegMissing) _ = DetectGpuAsync();
    }

    // ---------- 公開プロパティ ----------

    public EditList Project { get; }
    public ExportOptions Options { get; } = new();

    public ICommand TrimBeforeCommand { get; }
    public ICommand TrimAfterCommand { get; }
    public ICommand GoToTimeCommand { get; }
    public ICommand ApplySegmentRangeCommand { get; }
    public ICommand ResetSpeedCommand { get; }

    private double _speed = 1.0;
    /// <summary>プレビューの再生速度（書き出しには影響しない）。</summary>
    public double PlaybackSpeed
    {
        get => _speed;
        set { if (Set(ref _speed, Math.Clamp(value, 0.25, 2.0))) Raise(nameof(SpeedText)); }
    }
    public string SpeedText => $"{_speed:0.##}倍";

    // 時刻の直接入力（Pro）
    private string _timeInput = "";
    public string TimeInput { get => _timeInput; set => Set(ref _timeInput, value); }

    // 選んでいる区間の開始・終了を数字で直す（Pro）
    private string _segStartInput = "", _segEndInput = "";
    public string SegStartInput { get => _segStartInput; set => Set(ref _segStartInput, value); }
    public string SegEndInput { get => _segEndInput; set => Set(ref _segEndInput, value); }
    public string SegInfoText => _current == null ? "" :
        $"{_current.Source.FileName}\n{_current.Source.Summary}\n元ファイルの長さ {TimeFormat.Format(_current.Source.Duration, 3)}／{TimeFormat.Bytes(_current.Source.Size)}";

    private void GoToTime()
    {
        if (TryParseTime(TimeInput, out var t)) { Position = t; Status = ""; }
        else Status = "時刻は「1:23:45.678」「23:45」「125.5」のように入れてください。";
    }

    private void ApplySegmentRange()
    {
        if (_current == null) return;
        if (!TryParseTime(SegStartInput, out var s) || !TryParseTime(SegEndInput, out var e))
        {
            Status = "開始・終了は「1:23:45.678」「23:45」「125.5」のように入れてください。";
            return;
        }
        double at = _current.OutStart;
        if (Project.SetRange(_current, s, e)) { Position = at; Status = "区間の範囲を直しました。（元に戻す: Ctrl+Z）"; }
        else Status = "その範囲にはできません（終了が開始より後で、0.05秒以上あけてください）。";
    }

    /// <summary>"1:02:03.5" / "02:03" / "123.5" を秒にする。</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    private void TrimBefore()
    {
        double at = Position;
        Project.RemoveRange(0, at);
        Position = 0;
        Status = $"先頭から {TimeFormat.Format(at)} ぶんを削除しました。（元に戻す: Ctrl+Z）";
    }

    private void TrimAfter()
    {
        double at = Position;
        double len = Project.Total - at;
        Project.RemoveRange(at, Project.Total);
        Position = Project.Total;
        Status = $"ここから後ろ {TimeFormat.Format(len)} ぶんを削除しました。（元に戻す: Ctrl+Z）";
    }

    public event Action UserSeeked;
    public event Action PlayToggleRequested;
    public event Action ZoomAllRequested;

    public double Position
    {
        get => _position;
        set
        {
            value = Math.Clamp(value, 0, Project.Total);
            if (!Set(ref _position, value)) return;
            Raise(nameof(PositionText));
            UpdateCurrent();
            if (!_fromPlayback) UserSeeked?.Invoke();
        }
    }

    public string PositionText => TimeFormat.Format(Position, 3);
    public string TotalText => TimeFormat.Format(Project.Total, 1);

    public Segment SelectedSegment
    {
        get => _current;
        set
        {
            if (value == null || ReferenceEquals(value, _current)) return;
            if (!Project.Segments.Contains(value)) return;
            Position = value.OutStart;
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayText)); }
    }
    public string PlayText => IsPlaying ? "⏸ 一時停止" : "▶ 再生";

    public bool Muted { get => _muted; set => Set(ref _muted, value); }

    public double MarkIn { get => _markIn; private set { if (Set(ref _markIn, value)) MarksChanged(); } }
    public double MarkOut { get => _markOut; private set { if (Set(ref _markOut, value)) MarksChanged(); } }
    public bool HasRange => !double.IsNaN(_markIn) && !double.IsNaN(_markOut) && _markOut > _markIn;

    public string RangeText
    {
        get
        {
            bool i = !double.IsNaN(_markIn), o = !double.IsNaN(_markOut);
            if (!i && !o) return "不要な部分を消すには、消したい範囲の始まりで［開始点］、終わりで［終了点］を押して、［範囲を削除］。";
            if (i && !o) return $"開始点 {TimeFormat.Format(_markIn)} を決めました。次は消したい範囲の終わりで［終了点］を押してください。";
            if (!i) return $"終了点 {TimeFormat.Format(_markOut)} を決めました。次は範囲の始まりで［開始点］を押してください。";
            if (!HasRange) return "開始点より後ろに終了点を置いてください。";
            return $"選んだ範囲  {TimeFormat.Format(_markIn)} → {TimeFormat.Format(_markOut)}（{TimeFormat.Format(_markOut - _markIn)}）";
        }
    }

    public string Status { get => _status; set => Set(ref _status, value); }

    public bool FfmpegMissing
    {
        get => _ffmpegMissing;
        set { if (Set(ref _ffmpegMissing, value)) { Raise(nameof(FfmpegReady)); } }
    }
    public bool FfmpegReady => !FfmpegMissing;
    public bool IsEmpty => Project.Count == 0;
    public bool HasSegments => Project.Count > 0;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }

    // 書き出し設定
    public bool ModeCopy
    {
        get => _preferCopy;
        set { if (value && Set(ref _preferCopy, true)) { Raise(nameof(ModeEncode)); RefreshExportInfo(); } }
    }
    public bool ModeEncode
    {
        get => !_preferCopy;
        set { if (value && Set(ref _preferCopy, false)) { Raise(nameof(ModeCopy)); RefreshExportInfo(); } }
    }
    public bool CanCopy => _compatReason == null && Project.Count > 0;

    /// <summary>書き出しボタンの文字（Pro は形式を選べるので、それに合わせる）。</summary>
    public string ExportButtonText => Options.Extension.TrimStart('.').ToUpperInvariant() + "で書き出す…"
                                      + (Options.SplitFiles ? "（区間ごと）" : "");
    public string CompatText { get; private set; } = "";
    public string EstimateText { get; private set; } = "";
    public bool GpuAvailable => _gpu != null;
    public string GpuText => _gpu == null ? "GPU は見つかりませんでした（CPU で書き出します）" : $"見つかった GPU：{_gpu.Label}";

    private EncoderChoice PickEncoder()
        => Options.EncoderIndex == 2 || _gpu == null ? Encoders.Cpu : _gpu;

    public bool ExportBusy
    {
        get => _exportBusy;
        private set { if (Set(ref _exportBusy, value)) Raise(nameof(ExportIdle)); }
    }
    public bool ExportIdle => !ExportBusy;
    public double ExportPercent { get => _exportPercent; private set => Set(ref _exportPercent, value); }
    public string ExportText { get => _exportText; private set => Set(ref _exportText, value); }
    public string ResultText { get => _resultText; private set { if (Set(ref _resultText, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => !string.IsNullOrEmpty(_resultText);

    // コマンド
    public ICommand AddFilesCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DeleteSegmentCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand TogglePlayCommand { get; }
    public ICommand StepCommand { get; }
    public ICommand JumpSecondsCommand { get; }
    public ICommand GoStartCommand { get; }
    public ICommand GoEndCommand { get; }
    public ICommand PrevSegmentCommand { get; }
    public ICommand NextSegmentCommand { get; }
    public ICommand ZoomAllCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand SetInCommand { get; }
    public ICommand SetOutCommand { get; }
    public ICommand RemoveRangeCommand { get; }
    public ICommand KeepRangeCommand { get; }
    public ICommand ClearMarksCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelExportCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand PlayResultCommand { get; }
    public ICommand ChooseFfmpegCommand { get; }

    // ---------- 内部で使う操作（再生側から呼ぶものは public） ----------

    /// <summary>再生中に位置を進める（プレビューの再読み込みを起こさない）。</summary>
    public void SetPositionFromPlayback(double t)
    {
        _fromPlayback = true;
        try { Position = t; }
        finally { _fromPlayback = false; }
    }

    private void SeekUser(double t) => Position = t;

    private static int ToInt(object p) => p is int i ? i : int.Parse(p?.ToString() ?? "0");

    private void UpdateCurrent()
    {
        Project.TryLocate(_position, out var seg, out _);
        if (!ReferenceEquals(seg, _current))
        {
            _current = seg;
            _segStartInput = seg == null ? "" : TimeFormat.Format(seg.Start, 3);
            _segEndInput = seg == null ? "" : TimeFormat.Format(seg.End, 3);
            Raise(nameof(SelectedSegment));
            Raise(nameof(SegStartInput));
            Raise(nameof(SegEndInput));
            Raise(nameof(SegInfoText));
        }
    }

    private void Step(int dir)
    {
        double fps = _current?.Source.Fps ?? 30;
        if (fps <= 0) fps = 30;
        SeekUser(Position + dir / fps);
    }

    private void JumpSegment(int dir)
    {
        if (_current == null) return;
        int i = Project.Segments.IndexOf(_current);
        if (dir < 0)
        {
            if (Position - _current.OutStart > 0.3 || i == 0) SeekUser(_current.OutStart);
            else SeekUser(Project.Segments[i - 1].OutStart);
        }
        else if (i + 1 < Project.Segments.Count) SeekUser(Project.Segments[i + 1].OutStart);
        else SeekUser(Project.Total);
    }

    // ---------- 編集 ----------

    private void Split()
    {
        if (!Project.Split(Position)) Status = "ここでは分けられません（区間の端に近すぎます）。";
        else Status = $"{TimeFormat.Format(Position)} で分けました。";
    }

    private void SetIn()
    {
        MarkIn = Position;
        if (!double.IsNaN(_markOut) && _markOut <= _markIn) MarkOut = double.NaN;
    }

    private void SetOut()
    {
        MarkOut = Position;
        if (!double.IsNaN(_markIn) && _markIn >= _markOut) MarkIn = double.NaN;
    }

    private void RemoveRange()
    {
        if (!HasRange) return;
        double a = _markIn, b = _markOut;
        Project.RemoveRange(a, b);
        Position = a;
        Status = $"{TimeFormat.Format(b - a)} ぶんを削除しました。（元に戻す: Ctrl+Z）";
    }

    private void KeepRange()
    {
        if (!HasRange) return;
        double len = _markOut - _markIn;
        Project.KeepRange(_markIn, _markOut);
        Position = 0;
        Status = $"選んだ範囲（{TimeFormat.Format(len)}）だけを残しました。（元に戻す: Ctrl+Z）";
    }

    private void ClearMarks()
    {
        _markIn = _markOut = double.NaN;
        Raise(nameof(MarkIn));
        Raise(nameof(MarkOut));
        MarksChanged();
    }

    private void MarksChanged()
    {
        Raise(nameof(RangeText));
        Raise(nameof(HasRange));
    }

    private void DeleteCurrent()
    {
        var seg = _current;
        if (seg == null) return;
        double at = seg.OutStart;
        Project.DeleteSegment(seg);
        Position = at;
        Status = $"区間 {seg.Number} を削除しました。（元に戻す: Ctrl+Z）";
    }

    private void ClearAll()
    {
        if (MessageBox.Show("読み込んだ動画と編集内容をすべて消します。よろしいですか？\n（元に戻す: Ctrl+Z）", "Kirihari",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Project.Clear();
        Position = 0;
    }

    private void OnProjectChanged()
    {
        _markIn = _markOut = double.NaN;
        Raise(nameof(MarkIn));
        Raise(nameof(MarkOut));
        MarksChanged();

        double clamped = Math.Clamp(_position, 0, Project.Total);
        if (clamped != _position) { _position = clamped; Raise(nameof(Position)); Raise(nameof(PositionText)); }
        UpdateCurrent();

        Raise(nameof(TotalText));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasSegments));
        RefreshExportInfo();
        UserSeeked?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- 読み込み ----------

    private void PickFiles()
    {
        var dlg = new OpenFileDialog { Title = "動画を選ぶ（複数選べます）", Filter = OpenFilter, Multiselect = true };
        if (dlg.ShowDialog() == true) _ = AddFilesAsync(dlg.FileNames);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (!Tools.Ready) { Status = "ffmpeg が見つかりません。画面上の案内に従って設定してください。"; return; }
        if (IsLoading) return;

        var files = Expand(paths).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, new NaturalComparer()).ToList();
        if (files.Count == 0) { Status = "動画ファイルが見つかりませんでした。"; return; }

        IsLoading = true;
        var added = new List<MediaInfo>();
        var errors = new List<string>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                Status = $"読み込み中… {i + 1} / {files.Count}　{Path.GetFileName(files[i])}";
                try
                {
                    var m = await FFprobeService.ProbeAsync(files[i]);
                    m.ColorIndex = ColorFor(m.Path);
                    added.Add(m);
                }
                catch (Exception ex)
                {
                    errors.Add($"・{Path.GetFileName(files[i])}：{ex.Message}");
                }
            }
        }
        finally { IsLoading = false; }

        if (added.Count > 0)
        {
            bool wasEmpty = Project.Count == 0;
            Project.Add(added);
            if (wasEmpty) Position = 0;
            Status = $"{added.Count} 本を追加しました。合計 {TotalText}。";
        }
        else Status = "追加できる動画がありませんでした。";

        if (errors.Count > 0)
            MessageBox.Show("次のファイルは読み込めなかったので、飛ばしました。\n\n" + string.Join("\n", errors),
                "Kirihari", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private readonly Dictionary<string, int> _colors = new(StringComparer.OrdinalIgnoreCase);

    private int ColorFor(string path)
    {
        if (!_colors.TryGetValue(path, out var c)) _colors[path] = c = _colors.Count % Palette.Count;
        return c;
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> found;
                try { found = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(f => VideoExt.Contains(Path.GetExtension(f))).ToList(); }
                catch { continue; }
                foreach (var f in found) yield return f;
            }
            else if (File.Exists(p)) yield return p;
        }
    }

    // ---------- 書き出し ----------

    private ExportMode EffectiveMode => _preferCopy && CanCopy ? ExportMode.Copy : ExportMode.Encode;

    private void RefreshExportInfo()
    {
        var segs = Project.Segments.ToList();
        _compatReason = segs.Count == 0 ? "動画がありません"
            : Options.EncodeReason ?? ExportPlanner.CheckCopy(segs);
        Raise(nameof(CanCopy));
        Raise(nameof(ExportButtonText));

        if (segs.Count == 0)
        {
            CompatText = "";
            EstimateText = "";
        }
        else if (_preferCopy && _compatReason == null)
        {
            CompatText = "画質を落とさず、そのままつなぎます（とても速い）。切る位置は、前後のキーフレーム（約0.5〜2秒おき）に合います。";
            long est = ExportPlanner.EstimateCopySize(segs);
            EstimateText = $"出力サイズの目安：約 {TimeFormat.Bytes(est)}";
        }
        else if (_preferCopy)
        {
            CompatText = $"{_compatReason}、自動で「再エンコード」に切り替えて書き出します。";
            EstimateText = "";
        }
        else
        {
            CompatText = "1コマ単位で正確に切れます。形式がバラバラの動画もそろえてつなげます（時間がかかります）。";
            EstimateText = "";
        }
        Raise(nameof(CompatText));
        Raise(nameof(EstimateText));
    }

    private async Task DetectGpuAsync()
    {
        _gpu = await Encoders.DetectGpuAsync();
        Raise(nameof(GpuAvailable));
        Raise(nameof(GpuText));
    }

    private async Task ExportAsync()
    {
        if (ExportBusy || Project.Count == 0) return;
        var segs = Project.Segments.ToList();
        var first = segs[0].Source;

        string ext = Options.Extension;
        bool split = Options.SplitFiles && segs.Count > 1;
        var dlg = new SaveFileDialog
        {
            Title = split ? "書き出す場所と名前（区間ごとに _001・_002… を付けます）" : "書き出す場所と名前",
            Filter = Options.SaveFilter,
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = !split,
            InitialDirectory = Path.GetDirectoryName(first.Path),
            FileName = Path.GetFileNameWithoutExtension(first.FileName) + "_編集" + ext,
        };
        if (dlg.ShowDialog() != true) return;
        string outPath = dlg.FileName;

        // 区間ごとに書き出すときの名前（既にあれば (2) などを付けて、上書きしない）
        var targets = new List<(List<Segment> segs, string path)>();
        if (split)
        {
            string dir = Path.GetDirectoryName(outPath);
            string stem = Path.GetFileNameWithoutExtension(outPath);
            for (int i = 0; i < segs.Count; i++)
                targets.Add((new List<Segment> { segs[i] }, UniquePath(Path.Combine(dir, $"{stem}_{i + 1:000}{ext}"))));
        }
        else targets.Add((segs, outPath));

        if (targets.Any(t => segs.Any(s => string.Equals(Path.GetFullPath(s.Source.Path), Path.GetFullPath(t.path), StringComparison.OrdinalIgnoreCase))))
        {
            MessageBox.Show("元の動画と同じ名前には書き出せません。別の名前にしてください。", "Kirihari",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var mode = EffectiveMode;
        if (mode == ExportMode.Copy && !CheckDiskSpace(outPath, ExportPlanner.EstimateCopySize(segs))) return;

        ExportBusy = true;
        ResultText = "";
        _resultPath = null;
        ExportPercent = 0;
        ExportText = "準備しています…";
        _exportCts = new CancellationTokenSource();
        var enc = PickEncoder();
        ExportJob job = null;

        try
        {
            var done = new List<(ExportJob job, string path, int count)>();
            for (int t = 0; t < targets.Count; t++)
            {
                var (part, path) = targets[t];
                _splitLabel = targets.Count > 1 ? $"{t + 1}/{targets.Count} 本目　" : "";
                try
                {
                    job = await RunOnce(part, mode, enc, path);
                }
                catch (InvalidOperationException ex) when (mode == ExportMode.Encode && enc.IsGpu)
                {
                    // GPU での書き出しに失敗したら、CPU でやり直す（以降の区間も CPU で）
                    ExportText = "GPU で失敗したため、CPU でやり直しています…";
                    ExportPercent = 0;
                    Debug.WriteLine(ex.Message);
                    enc = Encoders.Cpu;
                    job = await RunOnce(part, mode, enc, path);
                }
                done.Add((job, path, part.Count));
            }
            _splitLabel = "";

            if (done.Count == 1) await ReportResult(done[0].job, done[0].path, done[0].count);
            else await ReportSplitResult(done.Select(d => (d.job, d.path)).ToList());
        }
        catch (OperationCanceledException)
        {
            Status = "書き出しを中止しました。";
            ExportText = "";
        }
        catch (Exception ex)
        {
            Status = "書き出しに失敗しました。";
            ExportText = "";
            MessageBox.Show("書き出しに失敗しました。\n\n" + ex.Message, "Kirihari", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportBusy = false;
            _exportCts.Dispose();
            _exportCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task<ExportJob> RunOnce(List<Segment> segs, ExportMode mode, EncoderChoice enc, string outPath)
    {
        var job = ExportPlanner.Build(segs, mode, enc, outPath, Options);
        var progress = new Progress<ExportProgress>(p =>
        {
            ExportPercent = p.Fraction * 100;
            string eta = "";
            if (p.Fraction > 0.02 && p.Fraction < 1)
            {
                double rest = p.ElapsedSeconds / p.Fraction * (1 - p.Fraction);
                eta = "　残り約 " + (rest >= 60 ? $"{(int)(rest / 60)}分{(int)(rest % 60):00}秒" : $"{(int)Math.Ceiling(rest)}秒");
            }
            string speed = p.Speed > 0 ? $"　{p.Speed:0.#}倍速" : "";
            ExportText = $"{_splitLabel}{p.Fraction * 100:0}%{speed}{eta}";
        });
        string encLabel = Options.Format switch
        {
            OutputFormat.Mp4H265 => "CPU（x265）",
            OutputFormat.Webm => "CPU（VP9）",
            OutputFormat.Gif => "GIF",
            OutputFormat.Mp3 or OutputFormat.Wav => "音声",
            _ => enc.Label,
        };
        ExportText = _splitLabel + (mode == ExportMode.Copy ? "つないでいます…" : $"再エンコードしています（{encLabel}）…");
        await ExportRunner.RunAsync(job, progress, _exportCts.Token);
        return job;
    }

    private async Task ReportResult(ExportJob job, string outPath, int segCount)
    {
        var fi = new FileInfo(outPath);
        if (!fi.Exists || fi.Length == 0) throw new InvalidOperationException("出力ファイルが見つかりません。");

        double actual = await FFprobeService.ProbeDurationAsync(outPath);
        double expected = job.TotalSeconds;
        string len = actual > 0 ? TimeFormat.Format(actual) : "（長さを確認できませんでした）";
        string text = $"書き出しが終わりました：{fi.Name}\nサイズ {TimeFormat.Bytes(fi.Length)}　／　長さ {len}（予定 {TimeFormat.Format(expected)}）";

        double tolerance = job.Mode == ExportMode.Copy ? 3.0 * segCount : 1.0;
        if (actual <= 0 || Math.Abs(actual - expected) > tolerance)
            text += "\n※ 予定と長さが大きくちがいます。念のため、再生して確かめてください。";

        _resultPath = outPath;
        ResultText = text;
        ExportPercent = 100;
        ExportText = "完了";
        Status = "書き出しが終わりました。";
    }

    private string _splitLabel = "";

    /// <summary>区間ごとに書き出したときの結果。各ファイルの長さも確かめる。</summary>
    private async Task ReportSplitResult(List<(ExportJob job, string path)> done)
    {
        long total = 0;
        int suspicious = 0;
        foreach (var (job, path) in done)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) throw new InvalidOperationException("出力ファイルが見つかりません: " + Path.GetFileName(path));
            total += fi.Length;
            double actual = await FFprobeService.ProbeDurationAsync(path);
            double tolerance = job.Mode == ExportMode.Copy ? 3.0 : 1.0;
            if (actual <= 0 || Math.Abs(actual - job.TotalSeconds) > tolerance) suspicious++;
        }

        string text = $"{done.Count} 本に分けて書き出しました：{Path.GetFileName(done[0].path)} 〜 {Path.GetFileName(done[^1].path)}\n合計サイズ {TimeFormat.Bytes(total)}";
        if (suspicious > 0) text += $"\n※ {suspicious} 本は予定と長さが大きくちがいます。念のため、再生して確かめてください。";

        _resultPath = done[0].path;
        ResultText = text;
        ExportPercent = 100;
        ExportText = "完了";
        Status = "書き出しが終わりました。";
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private bool CheckDiskSpace(string outPath, long need)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outPath)));
            if (drive.AvailableFreeSpace < need * 1.05)
            {
                return MessageBox.Show(
                    $"書き出し先の空き容量（{TimeFormat.Bytes(drive.AvailableFreeSpace)}）が、必要な目安（約 {TimeFormat.Bytes(need)}）より少ないようです。\n続けますか？",
                    "Kirihari", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            }
        }
        catch { /* ネットワークドライブなどは調べられない。そのまま進む */ }
        return true;
    }

    private void OpenFolder()
    {
        if (_resultPath == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_resultPath}\"") { UseShellExecute = true });
    }

    private void PlayResult()
    {
        if (_resultPath == null) return;
        Process.Start(new ProcessStartInfo(_resultPath) { UseShellExecute = true });
    }

    private void ChooseFfmpeg()
    {
        var dlg = new OpenFileDialog { Title = "ffmpeg.exe を選ぶ（ffprobe.exe が同じフォルダーにあること）", Filter = "ffmpeg.exe|ffmpeg.exe" };
        if (dlg.ShowDialog() != true) return;
        if (Tools.Choose(dlg.FileName))
        {
            FfmpegMissing = false;
            Status = "ffmpeg を設定しました。";
            _ = DetectGpuAsync();
            CommandManager.InvalidateRequerySuggested();
        }
        else
        {
            MessageBox.Show("そのフォルダーには ffmpeg.exe と ffprobe.exe の両方がありません。", "Kirihari",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>終了前に呼ぶ。書き出し中なら中止の確認をする。続行してよければ true。</summary>
    public bool ConfirmClose()
    {
        if (!ExportBusy) return true;
        if (MessageBox.Show("書き出しの途中です。中止して終了しますか？", "Kirihari",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        _exportCts?.Cancel();
        return true;
    }
}
