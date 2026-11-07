using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;

namespace FitzLauncher.Core.Tests;

public class GameNameCleanerTests
{
    [Theory]
    [InlineData("Elden.Ring.v1.10-[FitGirl Repack]", "Elden Ring")]
    [InlineData("Cyberpunk 2077 [DODI Repack]", "Cyberpunk 2077")]
    [InlineData("Hogwarts_Legacy_Deluxe_Edition_v1.0.3", "Hogwarts Legacy")]
    [InlineData("Stray-Win64-Shipping", "Stray")]
    [InlineData("Black Myth Wukong (Build 12345)", "Black Myth Wukong")]
    [InlineData("DOOMEternalx64vk", "DOOMEternalx64vk")]
    public void Cleans_repack_names(string raw, string expected) =>
        Assert.Equal(expected, GameNameCleaner.Clean(raw));
}

public class ExeCandidateFinderTests
{
    [Fact]
    public void Picks_the_game_over_installers_and_redists()
    {
        using var d = new TestDir();
        var root = d.Dir("Stray");
        d.File(@"Stray\unins000.exe", sizeBytes: 2_000_000);
        d.File(@"Stray\_CommonRedist\vcredist_x64.exe", sizeBytes: 20_000_000);
        d.File(@"Stray\Engine\Binaries\Win64\CrashReportClient.exe", sizeBytes: 20_000_000);
        var boot = d.File(@"Stray\Stray.exe", sizeBytes: 300_000);
        d.File(@"Stray\Hk_project\Binaries\Win64\Stray-Win64-Shipping.exe", sizeBytes: 3_000_000);

        var candidates = ExeCandidateFinder.FindCandidates(root);
        Assert.Equal(boot, candidates[0]);
        Assert.DoesNotContain(candidates, c => c.Contains("unins") || c.Contains("vcredist") || c.Contains("CrashReport"));
    }
}

public class GameScannerTests
{
    [Fact]
    public void Detects_unreal_project_and_user_config_folder()
    {
        using var d = new TestDir();
        var root = d.Dir("Games/Stray");
        d.File("Games/Stray/Hk_project/Content/Paks/Hk_project-WindowsNoEditor.pak", "x");
        var exe = d.File("Games/Stray/Hk_project/Binaries/Win64/Hk_project-Win64-Shipping.exe", "MZ");
        d.File("Games/Stray/Hk_project/Binaries/Win64/nvngx_dlss.dll", "x");
        var cfg = d.Dir("Local/Hk_project/Saved/Config/WindowsNoEditor");
        File.WriteAllText(Path.Combine(cfg, "GameUserSettings.ini"),
            "[ScalabilityGroups]\nsg.ShadowQuality=4\nsg.PostProcessQuality=3\n[/Script/Engine.GameUserSettings]\nFrameRateLimit=0.000000\n");

        var ctx = new ScanContext(d.Path_("Local"), d.Path_("Docs"), d.Path_("Saved"), d.Path_("Roaming"));
        var game = new Game { Name = "Stray", ExePath = exe };
        Assert.Equal(root, GameScanner.ResolveInstallRoot(game));

        var scan = new GameScanner(ctx).Scan(game);
        Assert.Equal(GameEngine.Unreal4, scan.Engine); // WindowsNoEditor ⇒ UE4
        Assert.Equal("Hk_project", scan.UnrealProjectName);
        Assert.Contains(cfg, scan.ConfigDirectories);
        Assert.Contains(Upscaler.Dlss, scan.Upscalers);
        Assert.Equal("4", scan.CurrentSettings["GUS:sg.ShadowQuality"]);
        Assert.Equal("0.000000", scan.CurrentSettings["GUS:FrameRateLimit"]);
    }

    [Fact]
    public void Detects_unity_and_boot_config()
    {
        using var d = new TestDir();
        d.File("Hollow/UnityPlayer.dll", "x");
        var boot = d.File("Hollow/Hollow_Data/boot.config", "gfx-enable-gfx-jobs=0\n");
        var exe = d.File("Hollow/Hollow.exe", "MZ");

        var scan = new GameScanner(new ScanContext(d.Root, d.Root, d.Root, d.Root)).Scan(new Game { Name = "Hollow", ExePath = exe });
        Assert.Equal(GameEngine.Unity, scan.Engine);
        Assert.Contains(boot, scan.ConfigFiles);
        Assert.Equal("0", scan.CurrentSettings["boot:gfx-enable-gfx-jobs"]);
    }

    [Fact]
    public void Detects_reshade_injector()
    {
        using var d = new TestDir();
        var exe = d.File("G/game.exe", "MZ");
        d.File("G/dxgi.dll", "x");
        d.File("G/ReShade.ini", "");
        var scan = new GameScanner(new ScanContext(d.Root, d.Root, d.Root, d.Root)).Scan(new Game { Name = "G", ExePath = exe });
        Assert.Contains("ReShade", scan.Injectors);
    }

    [Fact]
    public void Pe_reader_returns_empty_for_non_pe_files()
    {
        using var d = new TestDir();
        Assert.Empty(PeImports.Read(d.File("x.exe", "not a pe")));
    }
}

public class LibraryFolderScannerTests
{
    [Fact]
    public void Lists_each_subfolder_with_status_and_never_adds_by_itself()
    {
        using var d = new TestDir();
        var root = d.Dir("Games");
        d.File("Games/Hollow Knight/hollow_knight.exe", sizeBytes: 1000);
        d.File("Games/Hollow Knight/UnityPlayer.dll", "x");
        var known = d.File("Games/Stray/Stray.exe", sizeBytes: 1000);
        d.File("Games/Elden Ring [FitGirl Repack]/setup.exe", sizeBytes: 1000);
        d.File("Games/Elden Ring [FitGirl Repack]/fg-01.bin", "x");
        d.File("Games/Elden Ring [FitGirl Repack]/fg-02.bin", "x");
        d.Dir("Games/Empty Folder");

        var found = LibraryFolderScanner.Scan(root, [known], []);
        Assert.Equal(4, found.Count);

        var hk = found.Single(f => f.Name == "Hollow Knight");
        Assert.Equal(FoundGameStatus.New, hk.Status);
        Assert.True(hk.SuggestAdd);
        Assert.EndsWith("hollow_knight.exe", hk.Exe);

        Assert.Equal(FoundGameStatus.AlreadyInLibrary, found.Single(f => f.Name == "Stray").Status);

        var er = found.Single(f => f.Name == "Elden Ring");
        Assert.Equal(FoundGameStatus.NotInstalled, er.Status);
        Assert.False(er.SuggestAdd);

        Assert.Equal(FoundGameStatus.NoExe, found.Single(f => f.Name == "Empty Folder").Status);
    }

    [Fact]
    public void A_folder_that_is_itself_a_game_is_one_result()
    {
        using var d = new TestDir();
        var game = d.Dir("Cyberpunk 2077");
        d.File("Cyberpunk 2077/launcher.exe", sizeBytes: 100);
        d.File("Cyberpunk 2077/bin/x64/Cyberpunk2077.exe", sizeBytes: 5_000_000);
        var found = LibraryFolderScanner.Scan(game, [], []);
        Assert.Single(found);
        Assert.True(found[0].Candidates.Count >= 2);
    }

    [Fact]
    public void Installed_game_with_leftover_setup_is_not_mistaken_for_installer()
    {
        using var d = new TestDir();
        d.File("G/setup.exe", "x");
        d.File("G/data.bin", "x");
        d.File("G/Game.exe", sizeBytes: 2_000_000);
        Assert.False(LibraryFolderScanner.IsUninstalledRepack(d.Path_("G")));
    }
}
