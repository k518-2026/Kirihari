using System.Windows.Media.Imaging;

namespace Kirihari.Services;

/// <summary>Windows が再生できない形式でも、ffmpeg で1コマだけ取り出して画面に出す。</summary>
public static class FrameGrabber
{
    public static async Task<BitmapSource> GrabAsync(string path, double seconds, CancellationToken ct)
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-ss", Common.TimeFormat.Sec(Math.Max(0, seconds)),
            "-i", path, "-frames:v", "1", "-vf", "scale='min(960,iw)':-2", "-c:v", "bmp", "-f", "image2pipe", "-",
        };
        var (code, stdout, _) = await ProcessUtil.RunAsync(Tools.Ffmpeg, args, ct, 15000);
        if (code != 0 || stdout.Length < 64) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(stdout);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
