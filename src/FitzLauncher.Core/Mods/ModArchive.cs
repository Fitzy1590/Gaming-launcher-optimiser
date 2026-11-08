using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace FitzLauncher.Core.Mods;

/// <summary>Reads zip/7z/rar mod archives (or a plain folder) and extracts planned files into a staging folder.</summary>
public static class ModArchive
{
    public static bool IsArchive(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz";

    public static List<string> ListEntries(string path)
    {
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(path, f).Replace('\\', '/')).ToList();
        if (!IsArchive(path)) return [Path.GetFileName(path)]; // a single loose file (e.g. a .pak or .asi)
        using var archive = ArchiveFactory.OpenArchive(path);
        return archive.Entries.Where(e => !e.IsDirectory && e.Key is not null).Select(e => ModInstallPlanner.Normalize(e.Key!)).ToList();
    }

    /// <summary>Text of one entry (e.g. fomod/ModuleConfig.xml), with BOM/UTF-16 detection.</summary>
    public static string? ReadText(string path, string entry)
    {
        if (Directory.Exists(path))
        {
            var f = Path.Combine(path, entry);
            return File.Exists(f) ? File.ReadAllText(f) : null;
        }
        if (!IsArchive(path)) return null;
        using var archive = ArchiveFactory.OpenArchive(path);
        var e = archive.Entries.FirstOrDefault(x => x.Key is not null && ModInstallPlanner.Normalize(x.Key).Equals(entry, StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        using var s = e.OpenEntryStream();
        using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Extracts each planned file to {stagingDir}/{dest}. Returns the dests written.</summary>
    public static List<string> Extract(string path, IReadOnlyList<PlannedFile> plan, string stagingDir, IProgress<double>? progress = null)
    {
        var byEntry = plan.Where(p => p.Entry.Length > 0).GroupBy(p => p.Entry, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.Dest).ToList(), StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        var root = Path.GetFullPath(stagingDir);

        string Target(string dest)
        {
            if (!ModInstallPlanner.IsSafeRelative(dest)) throw new InvalidDataException($"Unsafe path in archive: {dest}");
            var full = Path.GetFullPath(Path.Combine(root, dest.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe path in archive: {dest}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return full;
        }

        if (Directory.Exists(path))
        {
            foreach (var (entry, dests) in byEntry)
                foreach (var d in dests) { File.Copy(Path.Combine(path, entry), Target(d), true); written.Add(d); }
        }
        else if (!IsArchive(path))
        {
            foreach (var d in byEntry.Values.SelectMany(x => x)) { File.Copy(path, Target(d), true); written.Add(d); }
        }
        else
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var total = Math.Max(1, byEntry.Count);
            var done = 0;
            void Write(string key, Action<Stream> copy)
            {
                if (!byEntry.TryGetValue(ModInstallPlanner.Normalize(key), out var dests)) return;
                var first = Target(dests[0]);
                using (var fs = File.Create(first)) copy(fs);
                written.Add(dests[0]);
                foreach (var d in dests.Skip(1)) { File.Copy(first, Target(d), true); written.Add(d); }
                progress?.Report(++done / (double)total);
            }

            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                // Sequential reader: decompresses solid 7z/rar once instead of per file.
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                    if (!reader.Entry.IsDirectory && reader.Entry.Key is { } key) Write(key, reader.WriteEntryTo);
            }
            else
            {
                foreach (var e in archive.Entries.Where(e => !e.IsDirectory && e.Key is not null))
                    Write(e.Key!, fs => { using var s = e.OpenEntryStream(); s.CopyTo(fs); });
            }
        }

        // Planned marker files with no source (e.g. UE4SS enabled.txt).
        foreach (var p in plan.Where(p => p.Entry.Length == 0))
        {
            var t = Target(p.Dest);
            if (!File.Exists(t)) File.WriteAllText(t, "");
            written.Add(p.Dest);
        }
        return written;
    }
}
