using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReimaginedLauncher.Utilities;

public sealed record ProtonLaunch(
    string Executable,
    string Arguments,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Runs a Windows executable inside Diablo II: Resurrected's own Proton prefix.
/// D2RLoader requires the Steam environment variables Proton sets.
/// </summary>
public static class SteamProtonService
{
    private const string AppId = "2536520";

    /// <summary>True when the profile is a Steam install whose Proton prefix resolves.</summary>
    public static bool IsSupported(InstallationProfile profile, out string? reason)
        => TryResolve(profile, "probe.exe", string.Empty, out _, out reason);

    public static bool TryResolve(
        InstallationProfile profile,
        string targetExecutable,
        string targetArguments,
        out ProtonLaunch? launch,
        out string? reason)
    {
        launch = null;

        if (profile.Type != InstallationType.Steam)
        {
            reason = "D2RLoader needs the Steam installation type on this platform.";
            return false;
        }

        var steamApps = FindSteamAppsDirectory(profile.InstallDirectory);
        if (steamApps is null)
        {
            reason = "Could not find the steamapps folder above the install directory.";
            return false;
        }

        var steamRoot = Path.GetDirectoryName(steamApps);
        if (string.IsNullOrEmpty(steamRoot))
        {
            reason = "Could not resolve the Steam root folder.";
            return false;
        }

        var compatData = Path.Combine(steamApps, "compatdata", AppId);
        if (!Directory.Exists(Path.Combine(compatData, "pfx")))
        {
            reason = "No Proton prefix yet. Start Diablo II: Resurrected from Steam once, then try again.";
            return false;
        }

        var protonDirectory = FindProtonDirectory(compatData, steamApps);
        if (protonDirectory is null)
        {
            reason = "Could not find the Proton version used by Diablo II: Resurrected. Enable Steam Play for it.";
            return false;
        }

        var proton = Path.Combine(protonDirectory, "proton");
        if (!File.Exists(proton))
        {
            reason = $"Proton is missing its launcher script at {proton}.";
            return false;
        }

        var environment = new Dictionary<string, string>
        {
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot,
            ["STEAM_COMPAT_DATA_PATH"] = compatData,
            ["SteamAppId"] = AppId,
            ["SteamGameId"] = AppId
        };

        var target = $"\"{proton}\" waitforexitandrun \"{targetExecutable}\"";
        if (!string.IsNullOrWhiteSpace(targetArguments))
        {
            target += $" {targetArguments}";
        }

        // Proton builds that pin a runtime only work inside its container.
        var runtimeEntryPoint = FindRuntimeEntryPoint(protonDirectory, steamApps);
        if (runtimeEntryPoint is not null)
        {
            launch = new ProtonLaunch(runtimeEntryPoint, $"--verb=waitforexitandrun -- {target}", environment);
        }
        else
        {
            launch = new ProtonLaunch(
                proton,
                $"waitforexitandrun \"{targetExecutable}\""
                + (string.IsNullOrWhiteSpace(targetArguments) ? string.Empty : $" {targetArguments}"),
                environment);
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Kills wine services left on the prefix by an earlier session.
    /// No-ops while the game is still running.
    /// </summary>
    public static void ClearStaleSession(ProtonLaunch launch, string procRoot = "/proc")
    {
        if (!launch.Environment.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compatData)) return;

        var processes = FindPrefixProcesses(compatData, procRoot);
        if (processes.Count == 0 || processes.Any(process => process.IsGame)) return;

        foreach (var process in processes)
        {
            try
            {
                using var handle = Process.GetProcessById(process.Id);
                handle.Kill(entireProcessTree: false);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already gone */ }
            catch (NotSupportedException) { }
            catch (SystemException) { /* permissions, races */ }
        }
    }

    internal readonly record struct PrefixProcess(int Id, bool IsGame);

    /// <summary>Processes bound to this prefix, flagging the game itself.</summary>
    internal static IReadOnlyList<PrefixProcess> FindPrefixProcesses(string compatData, string procRoot = "/proc")
    {
        var found = new List<PrefixProcess>();
        if (!Directory.Exists(procRoot)) return found;

        var marker = $"STEAM_COMPAT_DATA_PATH={compatData}";

        foreach (var directory in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;

            var environment = ReadNulSeparated(Path.Combine(directory, "environ"));
            if (!environment.Any(entry => string.Equals(entry, marker, StringComparison.Ordinal))) continue;

            var commandLine = string.Join(" ", ReadNulSeparated(Path.Combine(directory, "cmdline")));
            var isGame = commandLine.Contains("D2R.exe", StringComparison.OrdinalIgnoreCase)
                         || commandLine.Contains("D2RLoader.exe", StringComparison.OrdinalIgnoreCase);

            found.Add(new PrefixProcess(pid, isGame));
        }

        return found;
    }

    private static string[] ReadNulSeparated(string path)
    {
        try
        {
            return File.ReadAllText(path).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string? FindSteamAppsDirectory(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        var current = new DirectoryInfo(normalized);
        while (current is not null)
        {
            if (string.Equals(current.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>config_info path lines point inside the Proton build's "files" folder.</summary>
    private static string? FindProtonDirectory(string compatData, string steamApps)
    {
        var configInfo = Path.Combine(compatData, "config_info");
        if (File.Exists(configInfo))
        {
            foreach (var line in ReadLinesSafe(configInfo))
            {
                var marker = line.IndexOf("/files/", StringComparison.Ordinal);
                if (marker <= 0) continue;

                var candidate = line[..marker];
                if (File.Exists(Path.Combine(candidate, "proton")))
                {
                    return candidate;
                }
            }
        }

        var common = Path.Combine(steamApps, "common");
        if (!Directory.Exists(common)) return null;

        return Directory.EnumerateDirectories(common, "Proton*")
            .Where(directory => File.Exists(Path.Combine(directory, "proton")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? FindRuntimeEntryPoint(string protonDirectory, string steamApps)
    {
        var manifest = Path.Combine(protonDirectory, "toolmanifest.vdf");
        if (!File.Exists(manifest)) return null;

        string text;
        try { text = File.ReadAllText(manifest); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var required = Regex.Match(text, "\"require_tool_appid\"\\s*\"(\\d+)\"");
        if (!required.Success) return null;

        var installDirectory = ReadAppManifestInstallDirectory(steamApps, required.Groups[1].Value);
        if (installDirectory is not null)
        {
            var entryPoint = Path.Combine(steamApps, "common", installDirectory, "_v2-entry-point");
            if (File.Exists(entryPoint)) return entryPoint;
        }

        // The runtime may live in another library.
        var common = Path.Combine(steamApps, "common");
        if (!Directory.Exists(common)) return null;

        return Directory.EnumerateDirectories(common, "SteamLinuxRuntime*")
            .Select(directory => Path.Combine(directory, "_v2-entry-point"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? ReadAppManifestInstallDirectory(string steamApps, string appId)
    {
        var manifest = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
        if (!File.Exists(manifest)) return null;

        try
        {
            var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> ReadLinesSafe(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
