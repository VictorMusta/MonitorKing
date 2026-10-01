using System.Globalization;

namespace MonitorKing.Agent.Reports;

/// <summary>Formats français pour les rapports (mêmes conventions que le dashboard).</summary>
internal static class Fmt
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Value(double v, string unit) => unit switch
    {
        "%" => $"{v.ToString("0", Fr)} %",
        "o/s" => Rate(v),
        "Go" => $"{v.ToString("0.0", Fr)} Go",
        "Mo" => Mb(v),
        "ms" => $"{v.ToString(v < 10 ? "0.0" : "0", Fr)} ms",
        "°C" => $"{v.ToString("0", Fr)} °C",
        "/s" => $"{v.ToString("0", Fr)}/s",
        "" => v.ToString("0", Fr),
        _ => $"{v.ToString(Math.Abs(v) >= 100 ? "0" : "0.#", Fr)} {unit}",
    };

    public static string Pct(double v) => Value(v, "%");

    public static string Rate(double bps) => bps switch
    {
        >= 1024 * 1024 => $"{(bps / 1024 / 1024).ToString("0.0", Fr)} Mo/s",
        >= 1024 => $"{(bps / 1024).ToString("0", Fr)} Ko/s",
        _ => $"{bps.ToString("0", Fr)} o/s",
    };

    public static string Mb(double mb) => mb >= 1024 ? $"{(mb / 1024).ToString("0.0", Fr)} Go" : $"{mb.ToString("0", Fr)} Mo";

    public static string Ratio(double r) => r >= 10 ? $"×{r.ToString("0", Fr)}" : $"×{r.ToString("0.0", Fr)}";

    public static DateTime Local(long ts) => DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().DateTime;

    public static string Time(long ts) => Local(ts).ToString("HH:mm:ss", Fr);

    public static string When(long ts) => Local(ts).ToString("ddd d MMM yyyy, HH:mm:ss", Fr);

    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, Math.Round(seconds));
        return s switch
        {
            < 60 => $"{s} s",
            < 3600 => $"{s / 60} min {s % 60:00} s",
            < 86400 => $"{s / 3600} h {s % 3600 / 60:00}",
            _ => $"{s / 86400} j {s % 86400 / 3600} h",
        };
    }

    /// <summary>Texte sûr dans une cellule de tableau Markdown.</summary>
    public static string Cell(string? text) =>
        (text ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
}
