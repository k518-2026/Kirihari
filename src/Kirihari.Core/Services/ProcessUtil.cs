using System.Diagnostics;
using System.Text;

namespace Kirihari.Services;

public static class ProcessUtil
{
    public static ProcessStartInfo Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>最後まで走らせて、標準出力（バイト列）と標準エラーを受け取る。</summary>
    public static async Task<(int Code, byte[] Stdout, string Stderr)> RunAsync(
        string exe, IEnumerable<string> args, CancellationToken ct = default, int timeoutMs = 0)
    {
        using var p = new Process { StartInfo = Start(exe, args) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) cts.CancelAfter(timeoutMs);

        p.Start();
        using var reg = cts.Token.Register(() => TryKill(p));

        var ms = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var errTask = p.StandardError.ReadToEndAsync();

        await p.WaitForExitAsync(CancellationToken.None);
        await outTask;
        string err = await errTask;

        cts.Token.ThrowIfCancellationRequested();
        return (p.ExitCode, ms.ToArray(), err);
    }

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }
}
