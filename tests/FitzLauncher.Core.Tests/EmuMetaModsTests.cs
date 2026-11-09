using System.Text;
using FitzLauncher.Core.Emulation;

namespace FitzLauncher.Core.Tests;

public class RomHeaderTests
{
    /// <summary>A tiny ISO 9660 image holding the given root files (and optionally one sub folder with files).</summary>
    private static MemoryStream Iso(Dictionary<string, byte[]> rootFiles, string? dirName = null, Dictionary<string, byte[]>? dirFiles = null)
    {
        const int S = 2048;
        var img = new byte[S * 64];
        var next = 20;
        var content = new List<(string name, int lba, int len, bool dir)>();
        foreach (var (n, d) in rootFiles) { Array.Copy(d, 0, img, next * S, d.Length); content.Add((n + ";1", next, d.Length, false)); next += (d.Length + S - 1) / S; }
        int dirLba = 0;
        if (dirName is not null)
        {
            dirLba = next++;
            var inner = new List<(string, int, int, bool)>();
            foreach (var (n, d) in dirFiles!) { Array.Copy(d, 0, img, next * S, d.Length); inner.Add((n + ";1", next, d.Length, false)); next += (d.Length + S - 1) / S; }
            WriteDir(img, dirLba, inner);
            content.Add((dirName, dirLba, S, true));
        }
        WriteDir(img, 18, content);
        var pvd = 16 * S;
        img[pvd] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(img, pvd + 1);
        BitConverter.GetBytes(18).CopyTo(img, pvd + 156 + 2);
        BitConverter.GetBytes(S).CopyTo(img, pvd + 156 + 10);
        return new MemoryStream(img);
    }

    private static void WriteDir(byte[] img, int lba, List<(string name, int lba, int len, bool dir)> entries)
    {
        var pos = lba * 2048;
        foreach (var (name, l, len, dir) in entries)
        {
            var nb = Encoding.ASCII.GetBytes(name);
            var recLen = 33 + nb.Length + (nb.Length % 2 == 0 ? 1 : 0);
            img[pos] = (byte)recLen;
            BitConverter.GetBytes(l).CopyTo(img, pos + 2);
            BitConverter.GetBytes(len).CopyTo(img, pos + 10);
            img[pos + 25] = (byte)(dir ? 2 : 0);
            img[pos + 32] = (byte)nb.Length;
            nb.CopyTo(img, pos + 33);
            pos += recLen;
        }
    }

    private static byte[] Sfo(params (string key, string value)[] entries)
    {
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var table = new List<(int k, int dlen, int doff)>();
        foreach (var (k, v) in entries)
        {
            table.Add(((int)keys.Length, v.Length + 1, (int)data.Length));
            keys.Write(Encoding.ASCII.GetBytes(k + "\0"));
            data.Write(Encoding.UTF8.GetBytes(v + "\0"));
        }
        var keyStart = 20 + entries.Length * 16;
        var dataStart = keyStart + (int)keys.Length;
        var o = new MemoryStream();
        o.Write([0, (byte)'P', (byte)'S', (byte)'F']);
        o.Write(BitConverter.GetBytes(0x101));
        o.Write(BitConverter.GetBytes(keyStart));
        o.Write(BitConverter.GetBytes(dataStart));
        o.Write(BitConverter.GetBytes(entries.Length));
        foreach (var (k, dlen, doff) in table)
        {
            o.Write(BitConverter.GetBytes((ushort)k));
            o.Write(BitConverter.GetBytes((ushort)0x0204));
            o.Write(BitConverter.GetBytes(dlen));
            o.Write(BitConverter.GetBytes(dlen));
            o.Write(BitConverter.GetBytes(doff));
        }
        o.Write(keys.ToArray());
        o.Write(data.ToArray());
        return o.ToArray();
    }

    [Theory]
    [InlineData("BOOT2 = cdrom0:\\SLUS_200.62;1\r\nVER = 1.00", "SLUS-20062")]
    [InlineData("BOOT2=cdrom0:SCES_500.01;1", "SCES-50001")]
    [InlineData("nothing here", null)]
    public void Ps2_serial_from_system_cnf(string cnf, string? expected) => Assert.Equal(expected, RomHeaders.Ps2Serial(cnf));

    [Fact]
    public void Reads_the_ps2_serial_from_a_disc_image()
    {
        var iso = Iso(new() { ["SYSTEM.CNF"] = Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLUS_212.34;1\r\n") });
        Assert.Equal("SLUS-21234", RomHeaders.Identify(EmuPlatform.PlayStation2, iso, ".iso")!.TitleId);
        Assert.Null(RomHeaders.Identify(EmuPlatform.PlayStation2, Iso(new() { ["OTHER.TXT"] = [1] }), ".iso"));
        Assert.Null(RomHeaders.Identify(EmuPlatform.PlayStation2, new MemoryStream(new byte[100000]), ".iso"));
    }

    [Fact]
    public void Reads_the_psp_disc_id_from_a_folder_inside_the_image()
    {
        var iso = Iso([], "PSP_GAME", new() { ["PARAM.SFO"] = Sfo(("DISC_ID", "ULUS10041"), ("TITLE", "Cool Game")) });
        // the sub folder holds PARAM.SFO directly, so path PSP_GAME/PARAM.SFO resolves through it
        var id = RomHeaders.Identify(EmuPlatform.PSP, iso, ".iso")!;
        Assert.Equal("ULUS10041", id.TitleId);
        Assert.Equal("Cool Game", id.InternalName);
    }

    [Fact]
    public void Param_sfo_reads_text_entries_and_survives_junk()
    {
        var p = ParamSfo.Parse(Sfo(("TITLE_ID", "BLUS30001"), ("TITLE", "Demo")));
        Assert.Equal("BLUS30001", p["TITLE_ID"]);
        Assert.Empty(ParamSfo.Parse([1, 2, 3]));
        Assert.Empty(ParamSfo.Parse(new byte[40]));
    }

    [Fact]
    public void Reads_the_gamecube_wii_id_and_rejects_non_text()
    {
        Assert.Equal("GALE01", RomHeaders.Identify(EmuPlatform.GameCube, new MemoryStream(Encoding.ASCII.GetBytes("GALE01\0\0\0\0")), ".iso")!.TitleId);
        Assert.Null(RomHeaders.Identify(EmuPlatform.GameCube, new MemoryStream(new byte[16]), ".iso"));
    }

    private static byte[] N64Header(string code, string name)
    {
        var h = new byte[0x40];
        h[0] = 0x80; h[1] = 0x37; h[2] = 0x12; h[3] = 0x40;
        Encoding.ASCII.GetBytes(name.PadRight(20)).CopyTo(h, 0x20);
        Encoding.ASCII.GetBytes(code).CopyTo(h, 0x3B);
        return h;
    }

    [Fact]
    public void Reads_n64_headers_in_every_byte_order()
    {
        var z64 = N64Header("NSME", "SUPER MARIO 64");
        var v64 = (byte[])z64.Clone();
        for (var i = 0; i + 1 < v64.Length; i += 2) (v64[i], v64[i + 1]) = (v64[i + 1], v64[i]);
        var n64 = (byte[])z64.Clone();
        for (var i = 0; i + 3 < n64.Length; i += 4) Array.Reverse(n64, i, 4);
        foreach (var data in new[] { z64, v64, n64 })
        {
            var id = RomHeaders.Identify(EmuPlatform.N64, new MemoryStream(data), ".z64")!;
            Assert.Equal("NSME", id.TitleId);
            Assert.Equal("SUPER MARIO 64", id.InternalName);
        }
        Assert.Null(RomHeaders.Identify(EmuPlatform.N64, new MemoryStream(new byte[0x40]), ".z64"));
    }

    [Fact]
    public void Reads_the_3ds_program_id_and_product_code()
    {
        var img = new byte[0x4000 + 0x200];
        Encoding.ASCII.GetBytes("NCSD").CopyTo(img, 0x100);
        BitConverter.GetBytes(0x20u).CopyTo(img, 0x120);                  // first partition at 0x20 * 0x200 = 0x4000
        Encoding.ASCII.GetBytes("NCCH").CopyTo(img, 0x4000 + 0x100);
        BitConverter.GetBytes(0x0004000000123400UL).CopyTo(img, 0x4000 + 0x118);
        Encoding.ASCII.GetBytes("CTR-P-ABCD").CopyTo(img, 0x4000 + 0x150);
        var id = RomHeaders.Identify(EmuPlatform.Nintendo3DS, new MemoryStream(img), ".3ds")!;
        Assert.Equal("0004000000123400", id.TitleId);
        Assert.Equal("CTR-P-ABCD", id.InternalName);
    }
}

public class EmuModStoreTests : IDisposable
{
    private readonly string _t = Path.Combine(Path.GetTempPath(), "fitz-mod-" + Guid.NewGuid().ToString("N"));
    public EmuModStoreTests() => Directory.CreateDirectory(_t);
    public void Dispose() { try { Directory.Delete(_t, true); } catch (IOException) { } }

    private string Src(params (string rel, string text)[] files)
    {
        var dir = Path.Combine(_t, "src-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files) { var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        return dir;
    }

    [Theory]
    [InlineData("azahar", EmuModKind.Mod, "0004000000123400", @"load\mods\0004000000123400")]
    [InlineData("azahar", EmuModKind.Textures, "0004000000123400", @"load\textures\0004000000123400")]
    [InlineData("pcsx2", EmuModKind.Textures, "SLUS-21234", @"textures\SLUS-21234\replacements")]
    [InlineData("dolphin", EmuModKind.Textures, "GALE01", @"Load\Textures\GALE01")]
    [InlineData("ppsspp", EmuModKind.Textures, "ULUS10041", @"PSP\TEXTURES\ULUS10041")]
    [InlineData("rpcs3", EmuModKind.Patch, null, "patches")]
    public void Mods_go_to_the_folder_each_emulator_reads(string emu, EmuModKind kind, string? id, string expected) =>
        Assert.Equal(expected, ModDestinations.For(emu, kind, id, "My Mod", out _));

    [Fact]
    public void Missing_id_or_unsupported_kind_explains_itself()
    {
        Assert.Null(ModDestinations.For("pcsx2", EmuModKind.Textures, null, "m", out var why));
        Assert.Contains("ID", why);
        Assert.Null(ModDestinations.For("rpcs3", EmuModKind.Textures, null, "m", out var why2));
        Assert.Contains("aren't supported", why2);
        Assert.Contains(EmuModKind.Patch, ModDestinations.KindsFor("pcsx2"));
    }

    [Fact]
    public void Enable_copies_files_and_disable_removes_exactly_those()
    {
        var lib = Path.Combine(_t, "lib");
        EmuModStore.Import(Src(("a.txt", "A"), ("sub/b.txt", "B")), lib);
        var dest = Path.Combine(_t, "emu", "load");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "mine");
        var mod = new EmuModEntry { Name = "m", LibraryDir = lib };

        Assert.True(EmuModStore.Enable(mod, dest).Ok);
        Assert.True(mod.Enabled);
        Assert.Equal("B", File.ReadAllText(Path.Combine(dest, "sub", "b.txt")));
        Assert.Equal(2, mod.Written.Count);

        Assert.True(EmuModStore.Disable(mod).Ok);
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "sub")));
        Assert.True(File.Exists(Path.Combine(dest, "keep.txt")));
        Assert.False(mod.Enabled);
    }

    [Fact]
    public void Existing_files_are_never_overwritten_and_changed_files_are_kept_on_disable()
    {
        var lib = Path.Combine(_t, "lib2");
        EmuModStore.Import(Src(("a.txt", "A")), lib);
        var dest = Path.Combine(_t, "emu2");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "someone else's");
        var mod = new EmuModEntry { LibraryDir = lib };
        var r = EmuModStore.Enable(mod, dest);
        Assert.False(r.Ok);
        Assert.Equal(["a.txt"], r.Conflicts);
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(dest, "a.txt")));

        File.Delete(Path.Combine(dest, "a.txt"));
        Assert.True(EmuModStore.Enable(mod, dest).Ok);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "edited after");
        var off = EmuModStore.Disable(mod);
        Assert.Single(off.Conflicts);
        Assert.Equal("edited after", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }
}

public class IgdbTests
{
    private const string Json = """
    [{"name":"Okami HD","summary":"Remaster."},
     {"name":"Okami","summary":"A wolf goddess.","first_release_date":1161820800,"total_rating":87.6,
      "genres":[{"name":"Adventure"},{"name":"Hack and slash"}],
      "involved_companies":[{"developer":false,"company":{"name":"Capcom"}},{"developer":true,"company":{"name":"Clover Studio"}}]}]
    """;

    [Fact]
    public void Parses_text_fields_and_picks_the_exact_title()
    {
        var c = Igdb.Parse(Json);
        Assert.Equal(2, c.Count);
        var m = Igdb.BestMatch(c, "Okami")!;
        Assert.Equal("A wolf goddess.", m.Summary);
        Assert.Equal("26 Oct 2006", m.Released);
        Assert.Equal(88, m.Rating);
        Assert.Equal(["Adventure", "Hack and slash"], m.Genres);
        Assert.Equal("Clover Studio", m.Developer);
    }

    [Fact]
    public void Unrelated_titles_give_no_match_and_close_ones_do()
    {
        var c = Igdb.Parse(Json);
        Assert.Null(Igdb.BestMatch(c, "Totally Different Game"));
        Assert.Equal("Remaster.", Igdb.BestMatch([c[0]], "okami hd (usa)")!.Summary);
        Assert.Empty(Igdb.Parse("not json"));
        Assert.Empty(Igdb.Parse("""{"message":"x"}"""));
    }

    [Fact]
    public void Search_body_quotes_titles_safely_and_filters_by_platform()
    {
        var b = Igdb.SearchBody("Say \"hi\"", 8);
        Assert.Contains("search \"Say  hi\";", b);
        Assert.Contains("where platforms = (8);", b);
        Assert.DoesNotContain("where", Igdb.SearchBody("x", null));
    }
}
