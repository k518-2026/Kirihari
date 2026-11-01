using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kirihari.Services;
using Kirihari.ViewModels;

namespace Kirihari.Tests;

/// <summary>Lite / Pro の画面を実際に作り、動画を読み込んで切った状態を PNG に描く（目視確認用）。</summary>
public static class ShotRunner
{
    public static int Run(string which, string mediaDir, string outPng)
    {
        int result = 1;
        var t = new Thread(() => result = RunSta(which, mediaDir, outPng));
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static int RunSta(string which, string mediaDir, string outPng)
    {
        Window win;
        if (which == "lite")
        {
            var app = new Lite.App();
            app.InitializeComponent();
            win = new Lite.MainWindow();
        }
        else
        {
            var app = new Pro.App();
            app.InitializeComponent();
            win = new Pro.MainWindow();
        }
        win.Show();

        var vm = (MainViewModel)win.DataContext;
        var errors = new List<string>();
        var files = new[] { "00001.MTS", "00002.MTS", "small.mp4" }.Select(f => Path.Combine(mediaDir, f)).Where(File.Exists).ToArray();

        var frame = new DispatcherFrame();
        _ = Task.Run(async () => { await Task.Delay(0); }); // keep thread pool warm
        win.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await vm.AddFilesAsync(files);
                vm.Position = 6;
                vm.SplitCommand.Execute(null);
                vm.Position = 12;
                vm.SetInCommand.Execute(null);
                vm.Position = 16;
                vm.SetOutCommand.Execute(null);
                vm.Position = 22;
                vm.Status = "画面確認用の状態です。";
                await Task.Delay(800);
            }
            catch (Exception ex) { errors.Add(ex.ToString()); }
            frame.Continue = false;
        });
        Dispatcher.PushFrame(frame);

        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        int w = (int)win.ActualWidth, h = (int)win.ActualHeight;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var back = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = (Brush)win.FindResource("BgBrush") };
        back.Measure(new Size(w, h)); back.Arrange(new Rect(0, 0, w, h));
        rtb.Render(back);
        rtb.Render((Visual)win.Content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPng)) enc.Save(fs);

        Console.WriteLine($"{which}: {w}x{h} -> {outPng}  区間 {vm.Project.Count}  合計 {vm.TotalText}  GPU={vm.GpuText}");
        foreach (var e in errors) Console.WriteLine("ERR " + e);
        win.Close();
        return errors.Count == 0 ? 0 : 1;
    }
}
