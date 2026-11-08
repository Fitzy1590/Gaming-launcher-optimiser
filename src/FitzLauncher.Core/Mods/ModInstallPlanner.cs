namespace FitzLauncher.Core.Mods;

public sealed record PlannedFile(string Entry, string Dest);

/// <summary>Where a mod archive's files will go, relative to the game root.</summary>
public sealed class ModInstallPlan
{
    public string TargetLabel { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public List<PlannedFile> Files { get; set; } = [];
    public List<string> Requires { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    /// <summary>False when the layout was a guess: the UI asks you to confirm the folder.</summary>
    public bool Confident { get; set; } = true;
    public List<(string Label, string Root)> Alternatives { get; set; } = [];
    /// <summary>Bethesda plugins (.esp/.esm/.esl) to activate in Plugins.txt.</summary>
    public List<string> Plugins { get; set; } = [];
}

/// <summary>
/// Works out where an archive's files belong for this game: Bethesda Data, Unreal ~mods, BepInEx plugins,
/// UE4SS mods, Cyberpunk archive/r6/CET folders, next to the exe for ASI/proxy DLLs, or the game root when the
/// archive already mirrors the game's folders. Thunderstore packages follow r2modman's layout; FOMOD installers
/// are resolved with their default/recommended options.
/// </summary>
public static class ModInstallPlanner
{
    private static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".pdf", ".url", ".html", ".htm", ".png", ".jpg", ".jpeg", ".webp", ".rtf", ".docx" };
    private static readonly HashSet<string> RootMarkers = new(StringComparer.OrdinalIgnoreCase) { "Data", "BepInEx", "MelonLoader", "bin", "r6", "archive", "red4ext", "engine", "mods" };
    private static readonly HashSet<string> BethesdaContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "scripts", "interface", "sound", "music", "skse", "f4se", "sfse", "nvse", "obse", "seq", "strings", "shadersfx", "materials", "lodsettings", "grass", "video", "calientetools", "netscriptframework", "source", "dialogueviews", "facegen",
    };
    private static readonly HashSet<string> PluginExt = new(StringComparer.OrdinalIgnoreCase) { ".esp", ".esm", ".esl" };
    private static readonly HashSet<string> PakExt = new(StringComparer.OrdinalIgnoreCase) { ".pak", ".utoc", ".ucas", ".sig" };

    public static ModInstallPlan Plan(IReadOnlyList<string> archiveEntries, GameModLayout layout, string modName,
        Func<string, bool> existsInGame, string? fomodXml = null, string? overrideRoot = null, IReadOnlyDictionary<string, HashSet<string>>? fomodChoices = null)
    {
        var entries = archiveEntries.Select(Normalize).Where(e => e.Length > 0 && !e.EndsWith('/')).Distinct().ToList();
        var plan = new ModInstallPlan();
        AddAlternatives(plan, layout);

        // ---- FOMOD installer
        var fomodPath = entries.FirstOrDefault(e => e.EndsWith("fomod/ModuleConfig.xml", StringComparison.OrdinalIgnoreCase));
        if (fomodXml is not null && fomodPath is not null && overrideRoot is null)
        {
            var baseDir = fomodPath[..^"fomod/ModuleConfig.xml".Length];
            var result = FomodInstaller.Resolve(fomodXml, entries.Where(e => e.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)).Select(e => e[baseDir.Length..]).ToList(), fomodChoices);
            var root = layout.DataDir ?? "";
            plan.TargetRoot = root;
            plan.TargetLabel = layout.DataDir is not null ? "Data folder (FOMOD)" : "Game folder (FOMOD)";
            plan.Files = result.Files.Select(f => new PlannedFile(baseDir + f.Source, Join(root, f.Dest))).ToList();
            plan.Notes.Add(result.Summary);
            return Finish(plan, layout);
        }

        // ---- Thunderstore package (manifest.json + icon.png)
        var manifest = entries.FirstOrDefault(e => e.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) && entries.Contains(e[..^"manifest.json".Length] + "icon.png", StringComparer.OrdinalIgnoreCase));
        if (manifest is not null && overrideRoot is null)
            return Finish(Thunderstore(entries, manifest[..^"manifest.json".Length], modName, layout), layout);

        // ---- drop root-level readmes/screenshots when there's real content
        var content = entries.Where(e => e.Contains('/') || !DocExt.Contains(Path.GetExtension(e))).ToList();
        if (content.Count > 0) entries = content;

        // ---- strip wrapper folders ("MyMod v1.2/Data/…")
        string? wrapperName = null;
        var prefix = "";
        while (true)
        {
            var rel = entries.Select(e => e[prefix.Length..]).ToList();
            var tops = rel.Select(e => e.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tops.Count != 1 || rel.Any(e => !e.Contains('/'))) break;
            var top = tops[0];
            if (IsRootMarker(top, existsInGame) || (layout.DataDir is not null && BethesdaContent.Contains(top))) break;
            if (layout.Framework == ModFramework.Unity && top is "plugins" or "patchers" or "config") break;
            if (rel.Any(e => e.Equals(top + "/Scripts/main.lua", StringComparison.OrdinalIgnoreCase) || e.Equals(top + "/dlls/main.dll", StringComparison.OrdinalIgnoreCase)) && layout.Ue4ssModsDir is not null)
            {
                wrapperName = top; // a UE4SS mod: its folder name is the mod name
                prefix += top + "/";
                break;
            }
            wrapperName = top;
            prefix += top + "/";
        }
        var files = entries.Select(e => (entry: e, rel: e[prefix.Length..])).ToList();
        var topFolders = files.Where(f => f.rel.Contains('/')).Select(f => f.rel.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var looseExt = files.Where(f => !f.rel.Contains('/')).Select(f => Path.GetExtension(f.rel).ToLowerInvariant()).ToHashSet();
        var safeName = SafeName(wrapperName ?? modName);

        void Place(string root, string label, Func<string, string>? map = null, bool confident = true)
        {
            plan.TargetRoot = root;
            plan.TargetLabel = label;
            plan.Confident = confident;
            plan.Files = files.Select(f => new PlannedFile(f.entry, Join(root, map is null ? f.rel : map(f.rel)))).ToList();
        }

        if (overrideRoot is not null)
            Place(overrideRoot, plan.Alternatives.FirstOrDefault(a => a.Root == overrideRoot).Label ?? overrideRoot);
        else if (topFolders.Any(t => IsRootMarker(t, existsInGame)) && !(layout.DataDir is not null && topFolders.All(BethesdaContent.Contains)))
            Place("", "Game folder");
        else if (layout.DataDir is not null && (looseExt.Overlaps(PluginExt) || looseExt.Contains(".bsa") || looseExt.Contains(".ba2") || topFolders.Any(BethesdaContent.Contains)))
            Place(layout.DataDir, "Data folder");
        else if (layout.PaksModsDir is not null && files.All(f => PakExt.Contains(Path.GetExtension(f.rel))))
            Place(layout.PaksModsDir, "Paks/~mods", rel => Path.GetFileName(rel));
        else if (layout.Ue4ssModsDir is not null && files.Any(f => f.rel.Equals("Scripts/main.lua", StringComparison.OrdinalIgnoreCase) || f.rel.Equals("dlls/main.dll", StringComparison.OrdinalIgnoreCase)))
        {
            Place($"{layout.Ue4ssModsDir}/{safeName}", "UE4SS Mods");
            plan.Notes.Add($"UE4SS mod: add \"{safeName} : 1\" to Mods/mods.txt if it doesn't load (newer UE4SS loads enabled.txt automatically).");
            plan.Files.Add(new PlannedFile("", Join(plan.TargetRoot, "enabled.txt"))); // empty marker file
        }
        else if (layout.Framework == ModFramework.Cyberpunk && looseExt.Overlaps([".archive", ".xl"]) && topFolders.Count == 0)
            Place("archive/pc/mod", "archive/pc/mod");
        else if (layout.Framework == ModFramework.Cyberpunk && looseExt.Contains(".reds"))
            Place($"r6/scripts/{safeName}", "r6/scripts");
        else if (layout.Framework == ModFramework.Cyberpunk && files.Any(f => f.rel.Equals("init.lua", StringComparison.OrdinalIgnoreCase)))
            Place($"bin/x64/plugins/cyber_engine_tweaks/mods/{safeName}", "Cyber Engine Tweaks mods");
        else if (layout.Loaders.Contains("BepInEx") && (topFolders.Any(t => t is "plugins" or "patchers" or "config") || (topFolders.Count == 0 && looseExt.Contains(".dll"))))
            Place("BepInEx", "BepInEx", rel => rel.Contains('/') && rel.Split('/')[0] is "plugins" or "patchers" or "config" ? rel : $"plugins/{safeName}/{rel}");
        else if (layout.Loaders.Contains("MelonLoader") && topFolders.Count == 0 && looseExt.Contains(".dll"))
            Place("Mods", "MelonLoader Mods");
        else if (topFolders.Count == 0 && (looseExt.Contains(".asi") || looseExt.Contains(".dll") || looseExt.Contains(".ini")))
            Place(layout.ExeDir, "Next to the game exe");
        else
        {
            Place("", "Game folder", confident: false);
            plan.Notes.Add("Couldn't tell where this mod goes from its files. Check the mod page's install instructions and pick the folder.");
        }
        return Finish(plan, layout);
    }

    private static ModInstallPlan Thunderstore(List<string> entries, string baseDir, string package, GameModLayout layout)
    {
        var plan = new ModInstallPlan { TargetLabel = "BepInEx (Thunderstore layout)", TargetRoot = "BepInEx" };
        var pkg = SafeName(package);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "LICENSE.md" };
        var isPack = entries.Any(e => e[baseDir.Length..].StartsWith("BepInExPack", StringComparison.OrdinalIgnoreCase));
        foreach (var e in entries.Where(e => e.StartsWith(baseDir, StringComparison.Ordinal)))
        {
            var rel = e[baseDir.Length..];
            if (skip.Contains(rel)) continue;
            var first = rel.Split('/')[0];
            string dest;
            if (isPack && first.StartsWith("BepInExPack", StringComparison.OrdinalIgnoreCase))
            {
                if (!rel.Contains('/')) continue;
                dest = rel[(first.Length + 1)..];
            }
            else if (first.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) || (layout.Loaders.Contains("MelonLoader") && first is "Mods" or "UserLibs" or "Plugins" or "UserData"))
                dest = rel;
            else if (first.Equals("plugins", StringComparison.OrdinalIgnoreCase) && rel.Contains('/')) dest = $"BepInEx/plugins/{pkg}/{rel[8..]}";
            else if (first.Equals("patchers", StringComparison.OrdinalIgnoreCase) && rel.Contains('/')) dest = $"BepInEx/patchers/{pkg}/{rel[9..]}";
            else if (first.Equals("config", StringComparison.OrdinalIgnoreCase) && rel.Contains('/')) dest = $"BepInEx/config/{rel[7..]}";
            else if (first.Equals("core", StringComparison.OrdinalIgnoreCase) && rel.Contains('/')) dest = $"BepInEx/core/{rel[5..]}";
            else dest = $"BepInEx/plugins/{pkg}/{rel}";
            plan.Files.Add(new PlannedFile(e, dest));
        }
        if (isPack) { plan.TargetLabel = "Game folder (BepInEx loader)"; plan.TargetRoot = ""; plan.Notes.Add("Installs the BepInEx mod loader itself."); }
        AddAlternatives(plan, layout);
        return plan;
    }

    private static ModInstallPlan Finish(ModInstallPlan plan, GameModLayout layout)
    {
        // Never write outside the game folder ("../" or absolute paths in a hostile archive).
        var unsafeCount = plan.Files.RemoveAll(f => !IsSafeRelative(f.Dest));
        if (unsafeCount > 0) plan.Notes.Add($"Skipped {unsafeCount} file(s) with paths outside the game folder.");
        var dests = plan.Files.Select(f => f.Dest).ToList();
        plan.Requires = DetectRequires(dests, layout).ToList();
        if (layout.DataDir is { } data)
            plan.Plugins = dests.Where(d => PluginExt.Contains(Path.GetExtension(d)) && string.Equals(Path.GetDirectoryName(d)?.Replace('\\', '/'), data, StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.GetFileName(d)).ToList();
        return plan;
    }

    /// <summary>Mod loaders a set of installed paths depends on.</summary>
    public static IEnumerable<string> DetectRequires(IEnumerable<string> dests, GameModLayout layout)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dests)
        {
            var l = d.ToLowerInvariant();
            if (l.Contains("/skse/plugins/") || l.StartsWith("skse/plugins/")) set.Add("SKSE");
            else if (l.Contains("/f4se/plugins/")) set.Add("F4SE");
            else if (l.Contains("/sfse/plugins/")) set.Add("SFSE");
            else if (l.Contains("/nvse/plugins/")) set.Add("NVSE");
            else if (l.Contains("/obse/plugins/")) set.Add("OBSE");
            if ((l.StartsWith("bepinex/plugins/") || l.StartsWith("bepinex/patchers/")) && !l.StartsWith("bepinex/core/")) set.Add("BepInEx");
            if (l.EndsWith(".asi")) set.Add("ASI Loader");
            if (layout.Ue4ssModsDir is { } ue && l.StartsWith(ue.ToLowerInvariant() + "/")) set.Add("UE4SS");
            if (l.StartsWith("r6/scripts/") && l.EndsWith(".reds")) set.Add("redscript");
            if (l.StartsWith("bin/x64/plugins/cyber_engine_tweaks/mods/")) set.Add("Cyber Engine Tweaks");
            if (l.StartsWith("red4ext/plugins/")) set.Add("RED4ext");
            if (l.StartsWith("mods/") && l.EndsWith(".dll") && layout.Loaders.Contains("MelonLoader")) set.Add("MelonLoader");
        }
        // A mod that IS the loader doesn't require itself.
        foreach (var d in dests)
        {
            var name = Path.GetFileName(d).ToLowerInvariant();
            if (name.EndsWith("_loader.exe") && name.StartsWith("skse")) set.Remove("SKSE");
            if (name.StartsWith("f4se_loader")) set.Remove("F4SE");
            if (d.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)) set.Remove("BepInEx");
            if (name == "ue4ss.dll") set.Remove("UE4SS");
            if (name == "cyber_engine_tweaks.asi") { set.Remove("Cyber Engine Tweaks"); set.Remove("ASI Loader"); }
        }
        return set;
    }

    private static void AddAlternatives(ModInstallPlan plan, GameModLayout layout)
    {
        plan.Alternatives = [("Game folder", "")];
        if (layout.ExeDir.Length > 0) plan.Alternatives.Add(("Next to the game exe", layout.ExeDir));
        if (layout.DataDir is { } d) plan.Alternatives.Add(("Data folder", d));
        if (layout.PaksModsDir is { } p) plan.Alternatives.Add(("Paks/~mods", p));
        if (layout.Ue4ssModsDir is { } u) plan.Alternatives.Add(("UE4SS Mods", u));
        if (layout.Loaders.Contains("BepInEx")) plan.Alternatives.Add(("BepInEx/plugins", "BepInEx/plugins"));
    }

    private static bool IsRootMarker(string top, Func<string, bool> existsInGame) => RootMarkers.Contains(top) || existsInGame(top);

    public static bool IsSafeRelative(string rel) =>
        rel.Length > 0 && !Path.IsPathRooted(rel) && !rel.Contains(':') && !rel.Split('/', '\\').Any(seg => seg is ".." or ".");

    public static string Normalize(string entry) => entry.Replace('\\', '/').TrimStart('/');

    private static string Join(string root, string rel) => root.Length == 0 ? rel : $"{root.TrimEnd('/')}/{rel}";

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "mod" : s;
    }
}
