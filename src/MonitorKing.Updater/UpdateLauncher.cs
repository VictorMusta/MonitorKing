using System.Diagnostics;
using System.Globalization;

namespace MonitorKing.Updater;

/// <summary>
/// Au démarrage de l'installation d'origine : si une version plus récente, complète et vérifiée, attend dans
/// <c>.update\versions</c>, lui passer la main. Rien n'est remplacé, donc rien ne peut rester à moitié installé ;
/// une version qui ne démarre pas correctement est écartée, et la précédente reprend sa place.
/// </summary>
public static class UpdateLauncher
{
    /// <summary>Lancements tolérés avant qu'une version jamais confirmée soit écartée.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Temps laissé à la version lancée pour montrer qu'elle tient debout, avant de lui laisser la place.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    /// <summary>Versions installées plus récentes que celle qui tourne, de la plus récente à la plus ancienne.</summary>
    public static List<Version> Candidates(InstallLayout layout, Version running)
    {
        var current = InstallLayout.Normalize(running);
        var found = new List<Version>();
        if (!Directory.Exists(layout.VersionsDir)) return found;

        foreach (var directory in Directory.EnumerateDirectories(layout.VersionsDir))
        {
            // Jamais de retour en arrière : seules les versions strictement supérieures comptent.
            if (!InstallLayout.TryParseName(Path.GetFileName(directory), out var version) || version <= current) continue;
            if (!File.Exists(Path.Combine(directory, InstallLayout.AgentExe))) continue;
            if (File.Exists(layout.Marker(version, InstallLayout.Failed))) continue;
            found.Add(version);
        }

        found.Sort((a, b) => b.CompareTo(a));
        return found;
    }

    /// <param name="start">Lance la version et dit si elle tourne toujours après le délai de grâce (remplaçable dans les tests).</param>
    /// <returns>Vrai si une version plus récente a pris le relais : l'appelant doit se terminer sans rien faire d'autre.</returns>
    public static bool TryHandOver(InstallLayout layout, Version running, IReadOnlyList<string> args, Action<string> log, Func<ProcessStartInfo, bool>? start = null)
    {
        // Une version mise à jour ne relance rien : seule l'installation d'origine choisit.
        if (layout.RunningFromVersions is not null) return false;

        try
        {
            foreach (var version in Candidates(layout, running))
            {
                if (!File.Exists(layout.Marker(version, InstallLayout.Confirmed)) && !CountAttempt(layout, version, log)) continue;

                var info = new ProcessStartInfo(Path.Combine(layout.VersionDir(version), InstallLayout.AgentExe))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory,
                };
                foreach (var argument in args) info.ArgumentList.Add(argument);

                try
                {
                    if ((start ?? Start)(info)) return true;
                    log($"Version {InstallLayout.Name(version)} : arrêtée aussitôt lancée, la précédente reprend la main.");
                }
                catch (Exception e)
                {
                    log($"Version {InstallLayout.Name(version)} : lancement impossible ({e.Message}).");
                }
            }
        }
        catch (Exception e)
        {
            log($"Recherche d'une version plus récente impossible : {e.Message}");
        }

        return false;
    }

    /// <summary>Note un lancement de plus d'une version pas encore confirmée ; faux si elle doit être écartée.</summary>
    private static bool CountAttempt(InstallLayout layout, Version version, Action<string> log)
    {
        var path = layout.Marker(version, InstallLayout.Attempts);
        var attempts = File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
        try
        {
            if (attempts >= MaxAttempts)
            {
                File.WriteAllText(layout.Marker(version, InstallLayout.Failed), "");
                log($"Version {InstallLayout.Name(version)} écartée : elle n'a pas démarré correctement en {MaxAttempts} lancements.");
                return false;
            }

            File.WriteAllText(path, (attempts + 1).ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sans pouvoir compter les essais, on ne lance pas une version qui n'a jamais fait ses preuves.
            return false;
        }
    }

    private static bool Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        // Une version qui s'arrête aussitôt est cassée : mieux vaut garder la main que laisser le PC sans agent.
        return process is not null && !process.WaitForExit(Grace);
    }
}
