using Sidetrack.Core.Capture;
using Sidetrack.Core.Steam;

namespace Sidetrack.Tests;

public class SteamGamesTests
{
    /// <summary>A Steam folder with a second library on another "drive" holding the game.</summary>
    private static (string Steam, string Library) FakeSteam(TempDir dir)
    {
        var steam = Path.Combine(dir.Path, "Steam");
        var library = Path.Combine(dir.Path, "Games");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "Wardogs"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace("\\", "\\\\")}}" }
                "1" { "path" "{{library.Replace("\\", "\\\\")}}" "apps" { "1867240" "123" } }
                "2" { "path" "Z:\\\\gone" }
            }
            """);
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1867240.acf"), """
            "AppState"
            {
                "appid"		"1867240"
                "name"		"WARDOGS"
                "installdir"		"Wardogs"
            }
            """);
        return (steam, library);
    }

    [Fact]
    public void Library_folders_include_steam_and_every_existing_library()
    {
        using var dir = new TempDir();
        var (steam, library) = FakeSteam(dir);
        Assert.Equal([steam, library], SteamGames.LibraryFolders(steam));
    }

    [Fact]
    public void Game_is_found_in_any_library_with_its_install_folder()
    {
        using var dir = new TempDir();
        var (steam, library) = FakeSteam(dir);

        var game = SteamGames.Describe(steam, 1867240);

        Assert.Equal("WARDOGS", game.Name);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "Wardogs"), game.InstallDirectory);
    }

    [Fact]
    public void Unknown_app_has_no_install_folder_rather_than_a_guess()
    {
        using var dir = new TempDir();
        var (steam, _) = FakeSteam(dir);

        var game = SteamGames.Describe(steam, 42);

        Assert.Null(game.InstallDirectory);
        Assert.Equal("App 42", game.Name);
    }

    [Fact]
    public void Process_is_found_by_the_folder_its_exe_runs_from()
    {
        // The test host runs from the test output folder, like a game from its install folder.
        var found = ProcessTree.FindRootUnder(AppContext.BaseDirectory);

        Assert.NotNull(found);
        Assert.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), ProcessTree.ImagePath(found.Value.Pid), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_process_runs_from_an_empty_folder()
    {
        using var dir = new TempDir();
        Assert.Null(ProcessTree.FindRootUnder(dir.Path));
    }
}
