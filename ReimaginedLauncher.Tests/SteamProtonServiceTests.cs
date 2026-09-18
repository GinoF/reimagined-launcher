using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamProtonServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-proton-tests-{Guid.NewGuid():N}");

    private string SteamApps => Path.Combine(_root, "steamapps");
    private string GameDirectory => Path.Combine(SteamApps, "common", "Diablo II Resurrected");
    private string ProtonDirectory => Path.Combine(SteamApps, "common", "Proton - Experimental");
    private string LoaderPath => Path.Combine(GameDirectory, "D2RLoader.exe");

    private InstallationProfile SteamProfile => new()
    {
        Type = InstallationType.Steam,
        InstallDirectory = GameDirectory
    };

    /// <summary>Lays out the parts of a Steam library the resolver reads.</summary>
    private void CreateLibrary(bool withRuntime = true, bool withPrefix = true, bool withConfigInfo = true)
    {
        Directory.CreateDirectory(GameDirectory);
        File.WriteAllText(Path.Combine(GameDirectory, "D2R.exe"), string.Empty);
        File.WriteAllText(LoaderPath, string.Empty);

        Directory.CreateDirectory(Path.Combine(SteamApps, "compatdata", "2536520"));
        if (withPrefix)
        {
            Directory.CreateDirectory(Path.Combine(SteamApps, "compatdata", "2536520", "pfx"));
        }

        Directory.CreateDirectory(ProtonDirectory);
        File.WriteAllText(Path.Combine(ProtonDirectory, "proton"), string.Empty);

        if (withConfigInfo)
        {
            File.WriteAllLines(Path.Combine(SteamApps, "compatdata", "2536520", "config_info"),
            [
                "11.0-100",
                Path.Combine(ProtonDirectory, "files", "share", "fonts") + Path.DirectorySeparatorChar
            ]);
        }

        if (withRuntime)
        {
            File.WriteAllText(Path.Combine(ProtonDirectory, "toolmanifest.vdf"),
                "\"manifest\"\n{\n  \"version\" \"2\"\n  \"require_tool_appid\" \"4183110\"\n}\n");
            File.WriteAllText(Path.Combine(SteamApps, "appmanifest_4183110.acf"),
                "\"AppState\"\n{\n  \"appid\" \"4183110\"\n  \"installdir\" \"SteamLinuxRuntime_4\"\n}\n");

            var runtime = Path.Combine(SteamApps, "common", "SteamLinuxRuntime_4");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "_v2-entry-point"), string.Empty);
        }
    }

    [Fact]
    public void ResolvesThroughTheRuntimeEntryPointTheProtonBuildRequires()
    {
        CreateLibrary();

        Assert.True(SteamProtonService.TryResolve(
            SteamProfile, LoaderPath, "-mod ReimaginedLadder -txt", out var launch, out var reason));
        Assert.Null(reason);
        Assert.NotNull(launch);

        Assert.Equal(
            Path.Combine(SteamApps, "common", "SteamLinuxRuntime_4", "_v2-entry-point"),
            launch!.Executable);
        Assert.StartsWith("--verb=waitforexitandrun -- ", launch.Arguments);
        Assert.Contains(Path.Combine(ProtonDirectory, "proton"), launch.Arguments);
        Assert.Contains("waitforexitandrun", launch.Arguments);
        Assert.Contains(LoaderPath, launch.Arguments);
        Assert.EndsWith("-mod ReimaginedLadder -txt", launch.Arguments);
    }

    [Fact]
    public void SuppliesThePrefixEnvironmentTheLoaderNeedsToSeeSteam()
    {
        CreateLibrary();

        Assert.True(SteamProtonService.TryResolve(SteamProfile, LoaderPath, string.Empty, out var launch, out _));

        Assert.Equal(_root, launch!.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
        Assert.Equal(Path.Combine(SteamApps, "compatdata", "2536520"), launch.Environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal("2536520", launch.Environment["SteamAppId"]);
        Assert.Equal("2536520", launch.Environment["SteamGameId"]);
    }

    [Fact]
    public void RunsProtonDirectlyWhenItPinsNoRuntime()
    {
        CreateLibrary(withRuntime: false);

        Assert.True(SteamProtonService.TryResolve(SteamProfile, LoaderPath, string.Empty, out var launch, out _));

        Assert.Equal(Path.Combine(ProtonDirectory, "proton"), launch!.Executable);
        Assert.StartsWith("waitforexitandrun ", launch.Arguments);
    }

    [Fact]
    public void FallsBackToTheInstalledProtonWhenConfigInfoIsMissing()
    {
        CreateLibrary(withConfigInfo: false);

        Assert.True(SteamProtonService.TryResolve(SteamProfile, LoaderPath, string.Empty, out var launch, out _));
        Assert.Contains(Path.Combine(ProtonDirectory, "proton"), launch!.Arguments);
    }

    [Fact]
    public void ExplainsThatTheGameHasNeverRunUnderProton()
    {
        CreateLibrary(withPrefix: false);

        Assert.False(SteamProtonService.TryResolve(SteamProfile, LoaderPath, string.Empty, out var launch, out var reason));
        Assert.Null(launch);
        Assert.Contains("No Proton prefix", reason);
    }

    [Fact]
    public void RejectsInstallDirectoriesOutsideASteamLibrary()
    {
        var outside = Path.Combine(_root, "Games", "Diablo II Resurrected");
        Directory.CreateDirectory(outside);

        var profile = new InstallationProfile { Type = InstallationType.Steam, InstallDirectory = outside };

        Assert.False(SteamProtonService.TryResolve(profile, "D2RLoader.exe", string.Empty, out _, out var reason));
        Assert.Contains("steamapps", reason);
    }

    [Fact]
    public void RejectsNonSteamProfiles()
    {
        CreateLibrary();

        var profile = new InstallationProfile { Type = InstallationType.BattleNet, InstallDirectory = GameDirectory };

        Assert.False(SteamProtonService.IsSupported(profile, out var reason));
        Assert.Contains("Steam installation type", reason);
    }

    private string WriteFakeProcess(string procRoot, int pid, string compatData, string commandLine)
    {
        var directory = Path.Combine(procRoot, pid.ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "environ"),
            string.Join('\0', ["LANG=C", $"STEAM_COMPAT_DATA_PATH={compatData}", "USER=test"]) + '\0');
        File.WriteAllText(Path.Combine(directory, "cmdline"), commandLine.Replace(' ', '\0') + '\0');
        return directory;
    }

    [Fact]
    public void FindsOnlyProcessesBoundToThisPrefix()
    {
        var procRoot = Path.Combine(_root, "proc");
        WriteFakeProcess(procRoot, 101, "/steam/compatdata/2536520", @"C:\windows\system32\rpcss.exe");
        WriteFakeProcess(procRoot, 102, "/steam/compatdata/9999999", @"C:\windows\system32\rpcss.exe");
        Directory.CreateDirectory(Path.Combine(procRoot, "self"));

        var found = SteamProtonService.FindPrefixProcesses("/steam/compatdata/2536520", procRoot);

        Assert.Equal([101], found.Select(process => process.Id));
    }

    [Fact]
    public void FlagsTheGameSoALiveSessionIsNeverSweptAway()
    {
        var procRoot = Path.Combine(_root, "proc");
        WriteFakeProcess(procRoot, 201, "/steam/compatdata/2536520", @"C:\windows\system32\services.exe");
        WriteFakeProcess(procRoot, 202, "/steam/compatdata/2536520", @"Z:\games\D2RLoader.exe -txt -mod Reimagined");

        var found = SteamProtonService.FindPrefixProcesses("/steam/compatdata/2536520", procRoot);

        Assert.False(found.Single(process => process.Id == 201).IsGame);
        Assert.True(found.Single(process => process.Id == 202).IsGame);
    }

    [Fact]
    public void LeavesEverythingAloneWhileTheGameIsStillRunning()
    {
        var procRoot = Path.Combine(_root, "proc");
        WriteFakeProcess(procRoot, 301, "/steam/compatdata/2536520", @"C:\windows\system32\rpcss.exe");
        WriteFakeProcess(procRoot, 302, "/steam/compatdata/2536520", @"Z:\games\D2R.exe");

        var launch = new ProtonLaunch("/entry", "args", new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = "/steam/compatdata/2536520"
        });

        // Would throw if it tried to kill these fabricated pids.
        SteamProtonService.ClearStaleSession(launch, procRoot);

        Assert.Equal(2, SteamProtonService.FindPrefixProcesses("/steam/compatdata/2536520", procRoot).Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
