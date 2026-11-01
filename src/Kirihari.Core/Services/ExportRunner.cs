using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Kirihari.Services;

public readonly record struct ExportProgress(double Fraction, double Speed, double ElapsedSeconds);

public static class ExportRunner
{
    /// <summary>
    /// 書き出しを実行する。途中は「○○.mp4.part」に書き、最後まで成功したときだけ本来の名前にする。
    /// 失敗・中止のときは .part と一時ファイルを消す。
    /// </summary>
    public static async Task RunAsync(ExportJob job, IProgress<ExportProgress> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var errTail = new StringBuilder();
        try
        {
            using var p = new Process { StartInfo = ProcessUtil.Start(Tools.Ffmpeg, job.Args) };
            p.Start();
            using var reg = ct.Register(() => ProcessUtil.TryKill(p));

            var errTask = Task.Run(async () =>
            {
                string line;
                while ((line = await p.StandardError.ReadLineAsync()) != null)
                {
                    errTail.AppendLine(line);
                    if (errTail.Length > 6000) errTail.Remove(0, errTail.Length - 4000);
                }
            });

            double speed = 0;
            string l;
            while ((l = await p.StandardOutput.ReadLineAsync()) != null)
            {
                int eq = l.IndexOf('=');
                if (eq < 0) continue;
                string key = l.Substring(0, eq), val = l.Substring(eq + 1).Trim();
                if (key == "speed")
                {
                    double.TryParse(val.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                }
                else if (key == "out_time_us" || key == "out_time_ms")
                {
                    if (long.TryParse(val, out var us) && us >= 0 && job.TotalSeconds > 0)
                    {
                        double frac = Math.Min(1.0, us / 1_000_000.0 / job.TotalSeconds);
                        progress?.Report(new ExportProgress(frac, speed, sw.Elapsed.TotalSeconds));
                    }
                }
            }
            await p.WaitForExitAsync(CancellationToken.None);
            await errTask;

            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg が失敗しました（コード {p.ExitCode}）。\n{Tail(errTail.ToString())}");

            var fi = new FileInfo(job.PartPath);
            if (!fi.Exists || fi.Length == 0)
                throw new InvalidOperationException("出力ファイルができていません。\n" + Tail(errTail.ToString()));

            File.Move(job.PartPath, job.FinalPath, overwrite: true);
            progress?.Report(new ExportProgress(1, speed, sw.Elapsed.TotalSeconds));
        }
        catch
        {
            TryDelete(job.PartPath);
            throw;
        }
        finally
        {
            foreach (var t in job.TempFiles) TryDelete(t);
        }
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd()).ToArray();
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 8)));
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}
