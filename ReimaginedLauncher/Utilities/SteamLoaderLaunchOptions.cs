using System;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Steam runs the executable recorded for the app, so D2RLoader is only reachable through
/// Steam when the app's launch options swap it in. Steam owns localconfig.vdf while it is
/// running, so this reads the setting rather than writing it.
/// </summary>
public static class SteamLoaderLaunchOptions
{
    private const string AppId = "2536520";
    private const string LoaderExecutable = "D2RLoader.exe";

    public const string RequiredLaunchOptions =
        "bash -c 'exec \"${@/D2R.exe/D2RLoader.exe}\"' -- %command%";

    public static string Instructions =>
        "Steam has to start D2RLoader for Online and Ladder launches. In Steam, right-click "
        + "Diablo II: Resurrected, choose Properties, and paste this into Launch Options:\n\n"
        + RequiredLaunchOptions;

    /// <summary>True when any Steam account on this machine has the loader swap configured.</summary>
    public static bool IsConfigured(string? installDirectory, out string hint)
    {
        hint = Instructions;

        var steamRoot = FindSteamRoot(installDirectory);
        if (steamRoot is null)
        {
            return false;
        }

        var userData = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userData))
        {
            return false;
        }

        foreach (var accountDirectory in Directory.EnumerateDirectories(userData))
        {
            var localConfig = Path.Combine(accountDirectory, "config", "localconfig.vdf");
            if (File.Exists(localConfig) && HasLoaderSwap(localConfig))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLoaderSwap(string localConfigPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(localConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable config is not proof the swap is missing; do not block the launch.
            return true;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsAppHeader(lines[index]))
            {
                continue;
            }

            // The app's block follows its header; LaunchOptions sits directly inside it.
            var depth = 0;
            for (var scan = index + 1; scan < lines.Length; scan++)
            {
                var line = lines[scan];

                if (line.Contains('{'))
                {
                    depth++;
                }

                if (depth > 0
                    && line.Contains("\"LaunchOptions\"", StringComparison.OrdinalIgnoreCase)
                    && line.Contains(LoaderExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (line.Contains('}'))
                {
                    depth--;
                    if (depth <= 0)
                    {
                        break;
                    }
                }
            }
        }

        return false;
    }

    // The same id also appears as a value in unrelated sections, so match a bare key line.
    private static bool IsAppHeader(string line)
        => line.Trim() == $"\"{AppId}\"";

    private static string? FindSteamRoot(string? installDirectory)
    {
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            var directory = new DirectoryInfo(installDirectory);
            while (directory is not null)
            {
                if (string.Equals(directory.Name, "steamapps", StringComparison.OrdinalIgnoreCase)
                    && directory.Parent is { } parent)
                {
                    return parent.FullName;
                }

                directory = directory.Parent;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
            {
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
            }
            .FirstOrDefault(Directory.Exists);
    }
}
