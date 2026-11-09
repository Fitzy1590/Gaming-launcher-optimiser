using System.IO.Compression;
using FitzLauncher.Core.Emulation;

namespace FitzLauncher.Core.Tests;

public class EmuReleaseTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "fitz-rel-" + Guid.NewGuid().ToString("N"));
    public EmuReleaseTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private const string Latest = """
    {"tag_name":"v2.9.167","name":"v2.9.167","draft":false,"prerelease":false,"published_at":"2026-11-01T10:00:00Z",
     "assets":[
       {"name":"pcsx2-v2.9.167-windows-x64-Qt.7z","browser_download_url":"https://github.com/o/r/releases/download/v/a.7z","size":50,"digest":"sha256:ABCDEF"},
       {"name":"pcsx2-v2.9.167-windows-arm64-Qt.7z","browser_download_url":"https://github.com/o/r/a2.7z","size":50},
       {"name":"pcsx2-v2.9.167-linux-appimage-x64-Qt.AppImage","browser_download_url":"https://github.com/o/r/a3","size":50},
       {"name":"pcsx2-v2.9.167-macos-Qt.tar.xz","browser_download_url":"https://github.com/o/r/a4","size":50},
       {"name":"pcsx2-v2.9.167-windows-x64-Qt-symbols.zip","browser_download_url":"https://github.com/o/r/a5.zip","size":50}]}
    """;

    [Fact]
    public void Parses_tag_date_assets_and_digest()
    {
        var r = GitHubReleaseParser.ParseLatest(Latest)!;
        Assert.Equal("v2.9.167", r.Tag);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), r.PublishedAt);
        Assert.Equal(5, r.Assets.Count);
        Assert.Equal("abcdef", r.Assets[0].Sha256);
        Assert.Null(r.Assets[1].Sha256);
        Assert.Null(GitHubReleaseParser.ParseLatest("{ not json"));
        Assert.Null(GitHubReleaseParser.ParseLatest("""{"message":"Not Found"}"""));
    }

    [Fact]
    public void List_skips_drafts_and_nightlies_unless_asked_and_sorts_newest_first()
    {
        const string json = """
        [{"tag_name":"a","published_at":"2026-01-01T00:00:00Z","assets":[]},
         {"tag_name":"b","published_at":"2026-03-01T00:00:00Z","prerelease":true,"assets":[]},
         {"tag_name":"c","published_at":"2026-02-01T00:00:00Z","draft":true,"assets":[]},
         {"tag_name":"d","published_at":"2026-02-15T00:00:00Z","assets":[]}]
        """;
        Assert.Equal(["d", "a"], GitHubReleaseParser.ParseList(json, false).Select(r => r.Tag));
        Assert.Equal(["b", "d", "a"], GitHubReleaseParser.ParseList(json, true).Select(r => r.Tag));
        Assert.Empty(GitHubReleaseParser.ParseList("[", true));
    }

    [Fact]
    public void Picks_the_windows_x64_portable_download_not_other_systems_or_symbols()
    {
        var r = GitHubReleaseParser.ParseLatest(Latest)!;
        Assert.Equal("pcsx2-v2.9.167-windows-x64-Qt.7z", AssetPicker.Pick(r.Assets, @"windows.*x64.*\.(7z|zip)$")!.Name);
        Assert.Equal("pcsx2-v2.9.167-windows-x64-Qt.7z", AssetPicker.Pick(r.Assets, null)!.Name);
        Assert.Null(AssetPicker.Pick([new EmuAsset("app-linux.tar.gz", "https://github.com/x", 1, null), new EmuAsset("setup.exe", "https://github.com/y", 1, null)], null));
        // zip is preferred to 7z when both fit
        Assert.EndsWith(".zip", AssetPicker.Pick([new EmuAsset("emu-win64.7z", "u", 1, null), new EmuAsset("emu-win64.zip", "u", 1, null)], null)!.Name);
    }

    [Fact]
    public void Newer_means_a_different_tag_published_later()
    {
        var latest = new EmuRelease("ff29cea", "x", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), false, []);
        Assert.True(EmuReleaseCompare.IsNewer(null, null, latest));
        Assert.False(EmuReleaseCompare.IsNewer("ff29cea", null, latest));
        Assert.True(EmuReleaseCompare.IsNewer("fe73920", new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), latest));
        Assert.False(EmuReleaseCompare.IsNewer("zz", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), latest)); // installed one is newer
        Assert.True(EmuReleaseCompare.IsNewer("zz", null, latest));
    }

    [Theory]
    [InlineData("https://github.com/a/b/releases/download/v1/x.zip", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://github.com/a/b/x.zip", false)]
    [InlineData("https://evil.example.com/x.zip", false)]
    [InlineData("https://github.com.evil.com/x.zip", false)]
    [InlineData("not a url", false)]
    public void Downloads_only_from_github_hosts_over_https(string url, bool ok) => Assert.Equal(ok, EmuReleaseCompare.IsAllowedHost(url));

    [Fact]
    public void Folder_names_are_safe()
    {
        Assert.Equal("build-abc_def", EmuReleaseCompare.FolderName("build-abc/def"));
        Assert.Equal("2127.0-rc2", EmuReleaseCompare.FolderName("2127.0-rc2"));
        Assert.Equal(60, EmuReleaseCompare.FolderName(new string('a', 100)).Length);
    }

    private string MakeZip(params (string name, string content)[] entries)
    {
        var path = Path.Combine(_tmp, Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return path;
    }

    [Fact]
    public void Extracts_and_drops_a_single_wrapping_folder_then_finds_the_program()
    {
        var zip = MakeZip(("emu-1.2/emu.exe", "x"), ("emu-1.2/lib/a.dll", "y"));
        var dest = Path.Combine(_tmp, "out");
        EmuArchive.ExtractAll(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "emu.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "lib", "a.dll")));
        Assert.Equal(Path.Combine(dest, "emu.exe"), EmuArchive.FindExe(dest, ["EMU.exe"]));
        Assert.Null(EmuArchive.FindExe(dest, ["other.exe"]));
    }

    [Fact]
    public void Keeps_the_folder_layout_when_there_is_no_single_wrapper()
    {
        var zip = MakeZip(("emu.exe", "x"), ("data/a.txt", "y"));
        var dest = Path.Combine(_tmp, "out2");
        EmuArchive.ExtractAll(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "emu.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "data", "a.txt")));
    }

    [Fact]
    public void A_download_with_a_path_that_climbs_out_is_refused_and_writes_nothing_outside()
    {
        var zip = MakeZip(("ok.txt", "x"), ("../evil.txt", "boom"));
        var dest = Path.Combine(_tmp, "safe");
        Assert.Throws<InvalidDataException>(() => EmuArchive.ExtractAll(zip, dest));
        Assert.False(File.Exists(Path.Combine(_tmp, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "ok.txt")));
    }
}

public class EmuLiveAdvisorTests
{
    private static TimeSpan T(int s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Says_nothing_during_warm_up_or_while_the_target_is_held()
    {
        var a = new EmuLiveAdvisor(60, true);
        Assert.Null(a.Evaluate(20, 10, 99, T(30)));
        Assert.Null(a.Evaluate(60, 55, 99, T(200)));
        Assert.Null(a.Evaluate(58, 50, 99, T(230)));
    }

    [Fact]
    public void Needs_the_dip_to_last_before_speaking_and_a_recovery_resets_it()
    {
        var a = new EmuLiveAdvisor(60, true);
        Assert.Null(a.Evaluate(40, 30, 99, T(100)));
        Assert.Null(a.Evaluate(40, 30, 99, T(110)));
        Assert.Null(a.Evaluate(60, 55, 99, T(115)));      // recovered: start again
        Assert.Null(a.Evaluate(40, 30, 99, T(120)));
        Assert.Null(a.Evaluate(40, 30, 99, T(135)));
        Assert.NotNull(a.Evaluate(40, 30, 99, T(141)));
    }

    [Fact]
    public void Tells_gpu_limit_from_cpu_limit_and_speaks_once_per_kind()
    {
        var a = new EmuLiveAdvisor(60, false);
        a.Evaluate(40, 30, 98, T(100));
        var gpu = a.Evaluate(40, 30, 98, T(125))!;
        Assert.Contains("graphics card", gpu.Short);
        Assert.Contains("Optimise", gpu.Long);
        Assert.Null(a.Evaluate(40, 30, 98, T(150)));       // same kind: not again
        var cpu = a.Evaluate(40, 30, 50, T(160))!;
        Assert.Contains("processor", cpu.Short);
        Assert.Contains("won't help", cpu.Long);
    }

    [Fact]
    public void Auto_tune_changes_the_wording()
    {
        var a = new EmuLiveAdvisor(30, true);
        a.Evaluate(20, 15, 95, T(100));
        Assert.Contains("Fitz will lower", a.Evaluate(20, 15, 95, T(125))!.Long);
    }
}

public class TitleRelationsTests
{
    [Theory]
    [InlineData("Game [0100ABCD01234000][v0].nsp", TitleFileKind.Base, "0100ABCD01234000")]
    [InlineData("Game [0100ABCD01234800][v131072].nsp", TitleFileKind.Update, "0100ABCD01234000")]
    [InlineData("Game DLC [0100ABCD01235001][v0].nsp", TitleFileKind.Dlc, "0100ABCD01234000")]
    [InlineData("Game DLC [0100abcd01235002].nsp", TitleFileKind.Dlc, "0100ABCD01234000")]
    public void Classifies_files_by_title_id(string name, TitleFileKind kind, string baseId)
    {
        var f = TitleRelations.Parse(name)!;
        Assert.Equal(kind, f.Kind);
        Assert.Equal(baseId, f.BaseId);
    }

    [Fact]
    public void Files_without_a_title_id_are_ignored() => Assert.Null(TitleRelations.Parse("Plain game.nsp"));

    [Fact]
    public void Summary_lists_updates_and_dlc_of_this_game_only()
    {
        var files = new[]
        {
            "A [0100AAAA00002800][v65536].nsp", "A [0100AAAA00002800][v131072].nsp", "A dlc [0100AAAA00003001].nsp", "A dlc2 [0100AAAA00003002].nsp",
            "B [0100BBBB00002800][v5].nsp", "B [0100BBBB00002000].nsp",
        };
        var s = TitleRelations.Summarise("A [0100AAAA00002000][v0].nsp", files);
        Assert.Equal(2, s.UpdateFiles);
        Assert.Equal(131072, s.NewestUpdate);
        Assert.Equal(2, s.DlcFiles);
        Assert.Contains("v131072", s.Text);
        Assert.Contains("No update", TitleRelations.Summarise("C [0100CCCC00002000].nsp", files).Text);
        Assert.Equal(0, TitleRelations.Summarise("not tagged.nsp", files).UpdateFiles);
    }
}
