using System.Text;

namespace FitzLauncher.Core.Scanning;

/// <summary>
/// Reads the DLL import table of a PE (exe/dll) without loading it. Only the headers and import
/// directory are read, so it is fast even for 200 MB executables. Used to hint at the graphics API.
/// </summary>
public static class PeImports
{
    public static IReadOnlyList<string> Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return []; // "MZ"
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 24) return [];
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return []; // "PE\0\0"

            br.ReadUInt16(); // machine
            var sectionCount = br.ReadUInt16();
            fs.Position += 12;
            var optionalHeaderSize = br.ReadUInt16();
            br.ReadUInt16(); // characteristics
            var optionalStart = fs.Position;
            var magic = br.ReadUInt16();
            var is64 = magic == 0x20B;
            // Data directory #1 (import table) offset inside the optional header.
            fs.Position = optionalStart + (is64 ? 112 : 96) + 8;
            var importRva = br.ReadUInt32();
            if (importRva == 0) return [];

            fs.Position = optionalStart + optionalHeaderSize;
            var sections = new List<(uint va, uint size, uint raw)>();
            for (var i = 0; i < sectionCount; i++)
            {
                fs.Position += 8; // name
                var virtualSize = br.ReadUInt32();
                var va = br.ReadUInt32();
                var rawSize = br.ReadUInt32();
                var rawPtr = br.ReadUInt32();
                fs.Position += 16;
                sections.Add((va, Math.Max(virtualSize, rawSize), rawPtr));
            }

            long RvaToOffset(uint rva)
            {
                foreach (var (va, size, raw) in sections)
                    if (rva >= va && rva < va + size) return raw + (rva - va);
                return -1;
            }

            var result = new List<string>();
            var descriptor = RvaToOffset(importRva);
            if (descriptor < 0) return [];
            for (var i = 0; i < 512; i++, descriptor += 20)
            {
                fs.Position = descriptor + 12;
                var nameRva = br.ReadUInt32();
                if (nameRva == 0) break;
                var nameOffset = RvaToOffset(nameRva);
                if (nameOffset < 0) continue;
                fs.Position = nameOffset;
                var sb = new StringBuilder();
                for (int b; (b = fs.ReadByte()) > 0 && sb.Length < 260;) sb.Append((char)b);
                result.Add(sb.ToString().ToLowerInvariant());
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return [];
        }
    }
}
