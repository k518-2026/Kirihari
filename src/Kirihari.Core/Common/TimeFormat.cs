using System.Globalization;
using System.Runtime.InteropServices;

namespace Kirihari.Common;

public static class TimeFormat
{
    /// <summary>h:mm:ss.fff 形式。decimals は小数の桁数（0〜3）。</summary>
    public static string Format(double sec, int decimals = 1)
    {
        if (double.IsNaN(sec) || sec < 0) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        int h = (int)t.TotalHours;
        string s = $"{h}:{t.Minutes:00}:{t.Seconds:00}";
        if (decimals <= 0) return s;
        int ms = t.Milliseconds;
        string frac = ms.ToString("000", CultureInfo.InvariantCulture).Substring(0, Math.Min(3, decimals));
        return s + "." + frac;
    }

    public static string Bytes(long b)
    {
        double v = b;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i >= 3 ? v.ToString("0.00", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture)) + " " + u[i];
    }

    /// <summary>ffmpeg に渡す秒数（ロケールに依存しない）。</summary>
    public static string Sec(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>00002.MTS が 00010.MTS より前に来るような、エクスプローラーと同じ並び順。</summary>
public sealed class NaturalComparer : IComparer<string>
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string x, string y) => StrCmpLogicalW(x ?? "", y ?? "");
}
