using System.Text;
using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Emulation;

/// <summary>An ID read from a game file's own header (nothing encrypted is touched, so no keys are involved).</summary>
public sealed record RomId(string TitleId, string? InternalName = null);

/// <summary>Minimal ISO 9660 reader: finds a file by path (2048-byte sectors) so a disc's SYSTEM.CNF / PARAM.SFO can be read.</summary>
public static class Iso9660
{
    public static byte[]? ReadFile(Stream iso, string path, int maxBytes = 65536)
    {
        try
        {
            var pvd = ReadAt(iso, 16 * 2048L, 2048);
            if (pvd is null || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;
            var dirLba = BitConverter.ToInt32(pvd, 156 + 2);
            var dirLen = BitConverter.ToInt32(pvd, 156 + 10);
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var entry = Find(iso, dirLba, dirLen, parts[i]);
                if (entry is null) return null;
                var (lba, len, isDir) = entry.Value;
                if (i == parts.Length - 1)
                {
                    if (isDir) return null;
                    return ReadAt(iso, lba * 2048L, Math.Min(len, maxBytes));
                }
                if (!isDir) return null;
                dirLba = lba; dirLen = len;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or OverflowException) { return null; }
    }

    private static (int lba, int len, bool dir)? Find(Stream iso, int lba, int len, string name)
    {
        var data = ReadAt(iso, lba * 2048L, Math.Min(len, 1 << 20));
        if (data is null) return null;
        var pos = 0;
        while (pos < data.Length)
        {
            var recLen = data[pos];
            if (recLen == 0) { pos = (pos / 2048 + 1) * 2048; continue; } // records never straddle sectors
            if (pos + recLen > data.Length || recLen < 34) break;
            var nameLen = data[pos + 32];
            if (pos + 33 + nameLen <= data.Length)
            {
                var n = Encoding.ASCII.GetString(data, pos + 33, nameLen);
                var semi = n.IndexOf(';');
                if (semi >= 0) n = n[..semi];
                if (n.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase))
                    return (BitConverter.ToInt32(data, pos + 2), BitConverter.ToInt32(data, pos + 10), (data[pos + 25] & 2) != 0);
            }
            pos += recLen;
        }
        return null;
    }

    private static byte[]? ReadAt(Stream s, long offset, int count)
    {
        if (offset < 0 || offset + 1 > s.Length) return null;
        s.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[count];
        var read = 0;
        while (read < count) { var n = s.Read(buf, read, count - read); if (n <= 0) break; read += n; }
        return read == count ? buf : buf[..read];
    }
}

public static partial class ParamSfo
{
    /// <summary>PlayStation PARAM.SFO key/value text entries (TITLE_ID, DISC_ID, TITLE…).</summary>
    public static Dictionary<string, string> Parse(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.Length < 20 || data[0] != 0 || data[1] != 'P' || data[2] != 'S' || data[3] != 'F') return result;
        var keyStart = BitConverter.ToInt32(data, 8);
        var dataStart = BitConverter.ToInt32(data, 12);
        var count = BitConverter.ToInt32(data, 16);
        for (var i = 0; i < count && 20 + i * 16 + 16 <= data.Length; i++)
        {
            var e = 20 + i * 16;
            var keyOff = BitConverter.ToUInt16(data, e);
            var fmt = BitConverter.ToUInt16(data, e + 2);
            var len = BitConverter.ToInt32(data, e + 4);
            var valOff = BitConverter.ToInt32(data, e + 12);
            if (fmt != 0x0204 && fmt != 0x0004) continue; // text only
            var ks = keyStart + keyOff;
            var vs = dataStart + valOff;
            if (ks >= data.Length || vs < 0 || vs + len > data.Length) continue;
            var kEnd = Array.IndexOf(data, (byte)0, ks);
            if (kEnd < 0) continue;
            result[Encoding.ASCII.GetString(data, ks, kEnd - ks)] = Encoding.UTF8.GetString(data, vs, len).TrimEnd('\0');
        }
        return result;
    }
}

public static partial class RomHeaders
{
    [GeneratedRegex(@"BOOT2?\s*=\s*cdrom0:\\?(?<a>[A-Z]{4})_(?<b>\d{3})\.(?<c>\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Boot();

    /// <summary>"cdrom0:\SLUS_200.62;1" → "SLUS-20062" (the form PCSX2 and its patch files use).</summary>
    public static string? Ps2Serial(string systemCnf)
    {
        var m = Boot().Match(systemCnf);
        return m.Success ? $"{m.Groups["a"].Value.ToUpperInvariant()}-{m.Groups["b"].Value}{m.Groups["c"].Value}" : null;
    }

    public static RomId? Identify(EmuPlatform platform, string path)
    {
        try
        {
            if (platform == EmuPlatform.PlayStation3 && Directory.Exists(path))
            {
                var sfo = Path.Combine(path, "PS3_GAME", "PARAM.SFO");
                if (!File.Exists(sfo)) return null;
                var p = ParamSfo.Parse(File.ReadAllBytes(sfo));
                return p.TryGetValue("TITLE_ID", out var id) ? new RomId(id, p.GetValueOrDefault("TITLE")) : null;
            }
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return Identify(platform, fs, Path.GetExtension(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static RomId? Identify(EmuPlatform platform, Stream s, string extension)
    {
        var ext = extension.ToLowerInvariant();
        switch (platform)
        {
            case EmuPlatform.PlayStation2 when ext == ".iso":
                return Iso9660.ReadFile(s, "SYSTEM.CNF") is { } cnf && Ps2Serial(Encoding.ASCII.GetString(cnf)) is { } serial ? new RomId(serial) : null;
            case EmuPlatform.PSP when ext == ".iso":
                if (Iso9660.ReadFile(s, "PSP_GAME/PARAM.SFO") is not { } sfo) return null;
                var p = ParamSfo.Parse(sfo);
                return p.TryGetValue("DISC_ID", out var disc) ? new RomId(disc, p.GetValueOrDefault("TITLE")) : null;
            case EmuPlatform.GameCube or EmuPlatform.Wii when ext is ".iso" or ".gcm":
            {
                var b = new byte[6];
                s.Seek(0, SeekOrigin.Begin);
                if (s.Read(b, 0, 6) != 6 || b.Any(x => x < 0x20 || x > 0x7E)) return null;
                return new RomId(Encoding.ASCII.GetString(b));
            }
            case EmuPlatform.N64: return N64(s);
            case EmuPlatform.Nintendo3DS when ext is ".3ds" or ".cci": return Ncsd(s);
            default: return null;
        }
    }

    private static RomId? N64(Stream s)
    {
        var h = new byte[0x40];
        s.Seek(0, SeekOrigin.Begin);
        if (s.Read(h, 0, h.Length) != h.Length) return null;
        if (h[0] == 0x37 && h[1] == 0x80) for (var i = 0; i + 1 < h.Length; i += 2) (h[i], h[i + 1]) = (h[i + 1], h[i]);                    // .v64: byte-swapped
        else if (h[0] == 0x40 && h[1] == 0x12) for (var i = 0; i + 3 < h.Length; i += 4) { Array.Reverse(h, i, 4); }                          // .n64: word-swapped
        else if (!(h[0] == 0x80 && h[1] == 0x37)) return null;                                                                                 // not an N64 header
        var code = Encoding.ASCII.GetString(h, 0x3B, 4).TrimEnd('\0');
        var name = Encoding.ASCII.GetString(h, 0x20, 20).TrimEnd('\0', ' ');
        return code.Length == 4 ? new RomId(code, name) : null;
    }

    /// <summary>3DS cartridge dump: the program ID (the title ID mods and textures are filed under) from the first partition's header.</summary>
    private static RomId? Ncsd(Stream s)
    {
        var head = new byte[0x200];
        s.Seek(0, SeekOrigin.Begin);
        if (s.Read(head, 0, head.Length) != head.Length || Encoding.ASCII.GetString(head, 0x100, 4) != "NCSD") return null;
        var offset = BitConverter.ToUInt32(head, 0x120) * 0x200L;
        var ncch = new byte[0x200];
        s.Seek(offset, SeekOrigin.Begin);
        if (s.Read(ncch, 0, ncch.Length) != ncch.Length || Encoding.ASCII.GetString(ncch, 0x100, 4) != "NCCH") return null;
        var id = BitConverter.ToUInt64(ncch, 0x118);
        var product = Encoding.ASCII.GetString(ncch, 0x150, 16).TrimEnd('\0', ' ');
        return new RomId(id.ToString("X16"), product);
    }
}
