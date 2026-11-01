namespace Kirihari.Services;

/// <summary>ffmpeg.exe / ffprobe.exe の場所を探して覚える。</summary>
public static class Tools
{
    public static string Ffmpeg { get; private set; }
    public static string Ffprobe { get; private set; }
    public static bool Ready => Ffmpeg != null && Ffprobe != null;

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kirihari", "ffmpeg-folder.txt");

    /// <summary>実行ファイルの隣 → 保存済みの場所 → PATH → winget の置き場、の順に探す。</summary>
    public static bool Locate()
    {
        foreach (var dir in CandidateFolders())
        {
            if (TryFolder(dir)) return true;
        }
        return false;
    }

    /// <summary>利用者が選んだ ffmpeg.exe を採用して、次回以降も使う。</summary>
    public static bool Choose(string ffmpegExePath)
    {
        string dir = Path.GetDirectoryName(ffmpegExePath);
        if (dir == null || !TryFolder(dir)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, dir);
        }
        catch { /* 保存できなくても今回は使える */ }
        return true;
    }

    private static bool TryFolder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            string ff = Path.Combine(dir, "ffmpeg.exe");
            string fp = Path.Combine(dir, "ffprobe.exe");
            if (!File.Exists(ff) || !File.Exists(fp)) return false;
            Ffmpeg = ff;
            Ffprobe = fp;
            return true;
        }
        catch { return false; }
    }

    private static IEnumerable<string> CandidateFolders()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        yield return exeDir;
        yield return Path.Combine(exeDir, "tools");
        yield return Path.Combine(exeDir, "ffmpeg");

        string saved = null;
        try { if (File.Exists(SettingsFile)) saved = File.ReadAllText(SettingsFile).Trim(); } catch { }
        if (!string.IsNullOrEmpty(saved)) yield return saved;

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return p.Trim().Trim('"');

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links");
    }
}
