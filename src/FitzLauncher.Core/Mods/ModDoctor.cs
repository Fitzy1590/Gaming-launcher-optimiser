using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Mods;

public enum IssueSeverity { Info, Warning, Error }

public sealed record ModIssue(IssueSeverity Severity, string Title, string Detail, Guid? ModId = null, string? Fix = null);

/// <summary>Everything the checks need about the game, gathered by the app.</summary>
public sealed class ModCheckContext
{
    public required Game Game { get; init; }
    public required GameModLayout Layout { get; init; }
    public required string GameRoot { get; init; }
    public required Func<InstalledMod, string, string> StagedPath { get; init; }
    public string? GameVersion { get; init; }
    public bool GameIs64Bit { get; init; } = true;
    /// <summary>Game-relative files the launcher's own tweaks replaced (DXVK, ReShade toggle, DLL updates).</summary>
    public IReadOnlyCollection<string> TweakFiles { get; init; } = [];
    public Func<string, bool> ExistsInGame => rel => File.Exists(Path.Combine(GameRoot, rel)) || Directory.Exists(Path.Combine(GameRoot, rel));
}

/// <summary>
/// Compatibility checker: loaders a mod needs, script extender vs game version, game updated since install,
/// 32/64-bit DLLs, Bethesda missing masters, Thunderstore dependencies, proxy-DLL clashes (ReShade/DXVK/ASI),
/// IoStore pak mods, and file overlaps between mods.
/// </summary>
public static partial class ModCompatibility
{
    public static List<ModIssue> Check(ModCheckContext c)
    {
        var issues = new List<ModIssue>();
        var mods = c.Game.ModSetup.Mods;
        var enabled = mods.Where(m => m.Enabled).ToList();
        var layout = c.Layout;

        if (layout.HasAntiCheat && (mods.Count > 0 || c.Game.Trainer is not null))
            issues.Add(new(IssueSeverity.Warning, "Anti-cheat found", "This game ships with EasyAntiCheat or BattlEye. Mods and trainers are fine offline, but online play can get the account banned.", Fix: "Play offline (or with anti-cheat disabled, if the game supports it) when using mods or a trainer."));

        // ---- loaders: installed on disk, or provided by an enabled mod
        var provided = new HashSet<string>(layout.Loaders, StringComparer.OrdinalIgnoreCase);
        foreach (var m in enabled) provided.UnionWith(ProvidedLoaders(m));
        foreach (var m in enabled)
            foreach (var need in m.Requires.Where(r => !provided.Contains(r)))
                issues.Add(new(IssueSeverity.Error, $"{m.Name} needs {need}", $"{need} isn't installed, so this mod won't load.", m.Id, $"Install {need} first (search for it in Find mods). {LoaderHint(need)}"));

        // ---- script extender built for a different game version
        if (layout.ScriptExtenderRuntime is { } rt && c.GameVersion is { } gv && !SameVersion(rt, gv))
            issues.Add(new(IssueSeverity.Error, $"{layout.ScriptExtenderName} doesn't match the game version",
                $"{layout.ScriptExtenderName} here is built for {rt}, but the game is {gv}. Its plugins won't load and the game may crash at start-up.",
                Fix: $"Download the {layout.ScriptExtenderName} build for game version {gv} (repacks are often an older version: match the repack, not the latest)."));

        // ---- game updated since mods with code were installed
        if (c.GameVersion is { } ver)
            foreach (var m in enabled.Where(m => m.GameVersionAtInstall is { } at && !SameVersion(at, ver) && HasCode(m)))
                issues.Add(new(IssueSeverity.Warning, $"Game updated since {m.Name} was installed", $"Installed on {m.GameVersionAtInstall}, the game is now {ver}. Mods with DLLs often break on game updates.", m.Id, "Check the mod page for an update."));

        // ---- architecture of DLL/ASI mods
        foreach (var m in enabled)
        {
            foreach (var rel in m.Files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)).Take(60))
            {
                var bits = NativeBits(c.StagedPath(m, rel));
                if (bits is null || bits == (c.GameIs64Bit ? 64 : 32)) continue;
                issues.Add(new(IssueSeverity.Error, $"{m.Name}: wrong DLL type", $"{Path.GetFileName(rel)} is {bits}-bit but the game is {(c.GameIs64Bit ? 64 : 32)}-bit, so it can't load.", m.Id, "Download the version of the mod for this edition of the game (e.g. Skyrim SE/AE vs LE)."));
                break;
            }
        }

        // ---- Bethesda: missing masters
        if (layout.DataDir is { } data)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in enabled) foreach (var f in m.Files) if (IsPluginIn(f, data)) available.Add(Path.GetFileName(f));
            foreach (var m in enabled)
            {
                foreach (var plugin in m.Files.Where(f => IsPluginIn(f, data)))
                {
                    var missing = PluginMasters.Read(c.StagedPath(m, plugin))
                        .Where(ms => !available.Contains(ms) && !c.ExistsInGame($"{data}/{ms}")).ToList();
                    if (missing.Count > 0)
                        issues.Add(new(IssueSeverity.Error, $"{Path.GetFileName(plugin)} is missing masters", $"It needs {string.Join(", ", missing)}, which isn't installed. The game will crash on loading a save or at the main menu.", m.Id, "Install the required mods listed on the mod page, or disable this one."));
                }
            }
        }

        // ---- dependencies listed by Thunderstore / mod.io
        var installedIds = mods.Where(m => m.SourceId is not null).ToDictionary(m => DependencyKey(m.SourceId!), m => m, StringComparer.OrdinalIgnoreCase);
        foreach (var m in enabled)
            foreach (var dep in m.Dependencies)
            {
                var key = DependencyKey(dep);
                if (key.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase) && provided.Contains("BepInEx")) continue;
                if (installedIds.TryGetValue(key, out var d) && d.Enabled) continue;
                issues.Add(new(IssueSeverity.Error, $"{m.Name} needs {key}", installedIds.ContainsKey(key) ? $"{key} is installed but turned off." : $"{key} isn't installed.", m.Id, installedIds.ContainsKey(key) ? $"Turn {key} on." : $"Install {key} (Install dependencies does this for Thunderstore mods)."));
            }

        // ---- proxy DLLs: only one ReShade/ENB/DXVK/ASI loader can own each name
        foreach (var dll in GameModLayout.ProxyDlls)
        {
            var rel = layout.ExeDir.Length == 0 ? dll : $"{layout.ExeDir}/{dll}";
            var owners = enabled.Where(m => m.Files.Contains(rel, StringComparer.OrdinalIgnoreCase)).Select(m => m.Name).ToList();
            if (c.TweakFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)) owners.Add("a launcher tweak (DXVK or DLL update)");
            if (owners.Count > 1)
                issues.Add(new(IssueSeverity.Warning, $"{dll} clash", $"{string.Join(" and ", owners)} all use {dll}; only the last one installed works, so the others silently do nothing (or crash).", Fix: "Keep one, or use a chain-loading option (e.g. ReShade's proxy name setting, or the ASI loader's other DLL names)."));
        }

        // ---- Unreal IoStore
        if (layout.UsesIoStore && layout.PaksModsDir is { } paks)
            foreach (var m in enabled.Where(m => m.Files.Any(f => f.StartsWith(paks, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                                             && !m.Files.Any(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))))
                issues.Add(new(IssueSeverity.Warning, $"{m.Name} may not load", "This game uses IoStore (.utoc/.ucas) and the mod only has a .pak. Many mods for these games need all three files.", m.Id, "Check the mod is made for this game version."));

        // ---- missing staged files
        foreach (var m in enabled.Where(m => m.Files.Count > 0 && !File.Exists(c.StagedPath(m, m.Files[0]))))
            issues.Add(new(IssueSeverity.Warning, $"{m.Name}: files missing", "The launcher's copy of this mod is gone, so it can't be turned off cleanly.", m.Id, "Reinstall it."));

        // ---- overlaps between mods (normal, but worth knowing which one wins)
        foreach (var g in ModDeployer.Conflicts(enabled).GroupBy(x => (x.Winner, Loser: x.Overridden[0])))
            issues.Add(new(IssueSeverity.Info, $"{g.Key.Winner.Name} overrides {g.Key.Loser.Name}", $"{g.Count()} file(s) are in both; {g.Key.Winner.Name} wins because it's later in the list.", g.Key.Loser.Id, "Move a mod up or down to change which one wins."));

        return [.. issues.OrderByDescending(i => i.Severity)];
    }

    public static IEnumerable<string> ProvidedLoaders(InstalledMod m)
    {
        foreach (var f in m.Files)
        {
            var n = Path.GetFileName(f).ToLowerInvariant();
            if (n.StartsWith("skse64_loader") || n.StartsWith("sksevr_loader") || n.StartsWith("skse_loader")) yield return "SKSE";
            else if (n.StartsWith("f4se_loader")) yield return "F4SE";
            else if (n.StartsWith("sfse_loader")) yield return "SFSE";
            else if (n.StartsWith("nvse_loader")) yield return "NVSE";
            else if (n is "ue4ss.dll") yield return "UE4SS";
            else if (n is "cyber_engine_tweaks.asi") yield return "Cyber Engine Tweaks";
            else if (n is "red4ext.dll") yield return "RED4ext";
            else if (n is "scc.exe") yield return "redscript";
            else if (n is "scripthookv.dll") yield return "Script Hook V";
            else if (f.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)) yield return "BepInEx";
            else if (f.StartsWith("MelonLoader/", StringComparison.OrdinalIgnoreCase)) yield return "MelonLoader";
        }
        if (m.Name.Contains("ASI Loader", StringComparison.OrdinalIgnoreCase) || m.Files.Any(f => Path.GetFileName(f).Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) && m.Name.Contains("ASI", StringComparison.OrdinalIgnoreCase)))
            yield return "ASI Loader";
    }

    private static string LoaderHint(string loader) => loader switch
    {
        "SKSE" or "F4SE" or "SFSE" or "NVSE" or "OBSE" => "Script extenders come from their own site (e.g. skse.silverlock.org) and must match your game version exactly; start the game through the extender's loader exe (set it as the game's exe in Edit).",
        "BepInEx" => "On Thunderstore it's 'BepInExPack' for this game.",
        "UE4SS" => "Get it from the UE4SS GitHub releases or the game's Nexus page.",
        "ASI Loader" => "Ultimate ASI Loader (ThirteenAG) goes next to the game exe as dinput8.dll or version.dll.",
        _ => "",
    };

    private static bool HasCode(InstalledMod m) => m.Requires.Count > 0 || m.Files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));

    private static bool IsPluginIn(string rel, string dataDir) =>
        (rel.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
        && string.Equals(Path.GetDirectoryName(rel)?.Replace('\\', '/'), dataDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Owner-Name-1.2.3" → "Owner-Name".</summary>
    public static string DependencyKey(string id) => VersionSuffix().Replace(id, "");

    [GeneratedRegex(@"-\d+\.\d+\.\d+$")]
    private static partial Regex VersionSuffix();

    /// <summary>Compares the first three numeric parts ("1.6.1170" vs "1.6.1170.0").</summary>
    public static bool SameVersion(string a, string b)
    {
        static string Key(string v) => string.Join('.', Regex.Matches(v, @"\d+").Select(m => int.Parse(m.Value)).Take(3));
        return Key(a) == Key(b);
    }

    /// <summary>32/64 for native code; null for managed AnyCPU (.NET plugins) or unreadable files.</summary>
    public static int? NativeBits(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            if (h.CorHeader is not null && (h.CorHeader.Flags & System.Reflection.PortableExecutable.CorFlags.ILOnly) != 0 && h.CoffHeader.Machine == Machine.I386) return null;
            return h.CoffHeader.Machine switch { Machine.Amd64 or Machine.Arm64 => 64, Machine.I386 => 32, _ => null };
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
}

// ============================================================================ troubleshooter

public sealed record LogFile(string Path, string Text, DateTime ModifiedUtc);

public sealed class TroubleReport
{
    public List<ModIssue> Findings { get; } = [];
    /// <summary>Mods most likely involved, best guess first.</summary>
    public List<Guid> Suspects { get; } = [];
    public string Summary { get; set; } = "";
}

/// <summary>
/// Crash and error diagnosis: what the exit code means, which mod owns the module that crashed, and what
/// the loader and crash-logger logs say (SKSE, Crash Logger/Trainwreck, BepInEx, UE4SS, Unity, CET).
/// </summary>
public static partial class ModTroubleshooter
{
    private static readonly HashSet<string> SystemModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll.dll", "kernel32.dll", "kernelbase.dll", "ucrtbase.dll", "msvcrt.dll", "vcruntime140.dll", "msvcp140.dll", "user32.dll", "win32u.dll", "gdi32.dll",
        "d3d11.dll", "d3d12.dll", "dxgi.dll", "combase.dll", "ole32.dll", "amdxx64.dll", "amdxc64.dll", "nvwgf2umx.dll", "atidxx64.dll", "nvgpucomp64.dll",
    };

    public static TroubleReport Analyze(Game game, GameModLayout layout, IReadOnlyList<LogFile> logs, IReadOnlyList<ModIssue>? compatIssues = null)
    {
        var r = new TroubleReport();
        var mods = game.ModSetup.Mods;
        var score = new Dictionary<Guid, int>();
        void Suspect(InstalledMod m, int points) => score[m.Id] = score.GetValueOrDefault(m.Id) + points;

        // ---- the last crash
        if (game.LastCrash is { } crash)
        {
            var module = crash.FaultingModule ?? "";
            if (ExitCodeHex().Match(module) is { Success: true } hex)
            {
                var code = Convert.ToUInt32(hex.Groups[1].Value, 16);
                r.Findings.Add(new(IssueSeverity.Error, $"Last crash: {ExitCodeName(code)}", ExitCodeAdvice(code)));
            }
            else if (module.Length > 0)
            {
                var owner = OwnerOf(mods, module);
                if (owner is not null)
                {
                    Suspect(owner, 10);
                    r.Findings.Add(new(IssueSeverity.Error, $"Crashed in {module}, from {owner.Name}", $"The game crashed inside a file that {owner.Name} installed.", owner.Id, $"Turn {owner.Name} off, or check its page for an update or a version matching your game."));
                }
                else if (GameModLayout.ProxyDlls.Contains(module, StringComparer.OrdinalIgnoreCase))
                    r.Findings.Add(new(IssueSeverity.Error, $"Crashed in {module}", "That's a wrapper DLL used by ReShade, ENB, DXVK or an ASI loader. Usually an outdated wrapper or two wrappers fighting over the same name."));
                else if (module.Equals(Path.GetFileName(game.ExePath), StringComparison.OrdinalIgnoreCase) || module.Equals(Path.GetFileName(game.LastScan?.RenderExePath ?? ""), StringComparison.OrdinalIgnoreCase))
                    r.Findings.Add(new(IssueSeverity.Warning, "Crashed inside the game itself", "Mods can still cause this through their data (missing masters, broken meshes or scripts) or an outdated loader. See the log findings below, or use Find the bad mod."));
                else
                    r.Findings.Add(new(IssueSeverity.Warning, $"Crashed in {module}", "Not a file installed by a mod. If it's a graphics driver file, update the driver; otherwise use Find the bad mod."));
            }
        }

        // ---- logs
        foreach (var log in logs)
        {
            var name = Path.GetFileName(log.Path);
            var lines = log.Text.Split('\n');
            if (name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase) || log.Text.Contains("PROBABLE CALL STACK", StringComparison.Ordinal))
            {
                var dlls = DllInText().Matches(log.Text).Select(m => m.Value).Where(d => !SystemModules.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var plugins = PluginInText().Matches(log.Text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var hits = dlls.Concat(plugins).Select(f => (f, owner: OwnerOf(mods, f))).Where(x => x.owner is not null).ToList();
                foreach (var (f, owner) in hits.Take(5)) Suspect(owner!, 6);
                r.Findings.Add(new(IssueSeverity.Error, $"Crash log {name}",
                    hits.Count > 0 ? $"Mentions files from: {string.Join(", ", hits.Select(h => $"{h.owner!.Name} ({h.f})").Distinct().Take(5))}." : $"Modules in the call stack: {string.Join(", ", dlls.Take(6))}.",
                    hits.FirstOrDefault().owner?.Id, "The first mod named in the call stack is the usual culprit."));
            }
            else if (name.StartsWith("skse", StringComparison.OrdinalIgnoreCase) || name.StartsWith("f4se", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sfse", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines.Where(l => l.Contains("disabled", StringComparison.OrdinalIgnoreCase) || l.Contains("couldn't load", StringComparison.OrdinalIgnoreCase) || l.Contains("incompatible", StringComparison.OrdinalIgnoreCase)).Take(10))
                {
                    var dll = DllInText().Match(line).Value;
                    var owner = dll.Length > 0 ? OwnerOf(mods, dll) : null;
                    if (owner is not null) Suspect(owner, 5);
                    r.Findings.Add(new(IssueSeverity.Error, $"{(dll.Length > 0 ? dll : "A plugin")} didn't load", line.Trim(), owner?.Id, "It's built for a different game or script extender version. Get the version matching your game."));
                }
            }
            else if (name.Equals("LogOutput.log", StringComparison.OrdinalIgnoreCase) || name.Equals("Latest.log", StringComparison.OrdinalIgnoreCase))
            {
                var bySource = lines.Select(l => BepInExError().Match(l)).Where(m => m.Success).GroupBy(m => m.Groups[1].Value.Trim()).ToList();
                foreach (var g in bySource.OrderByDescending(g => g.Count()).Take(6))
                {
                    var owner = OwnerByName(mods, g.Key);
                    if (owner is not null) Suspect(owner, 3);
                    r.Findings.Add(new(IssueSeverity.Warning, $"{g.Key}: {g.Count()} error(s) in {name}", g.First().Value.Trim(), owner?.Id, owner is null ? null : $"Update or turn off {owner.Name}."));
                }
            }
            else
            {
                // UE4SS, CET, Unity Player.log, anything else: count errors, attribute by mod folder names
                var errors = lines.Where(l => ErrorWord().IsMatch(l)).ToList();
                if (errors.Count == 0) continue;
                InstalledMod? owner = null;
                foreach (var e in errors)
                    if (ModFolderInText().Match(e) is { Success: true } mf && OwnerByName(mods, mf.Groups[1].Value) is { } o) { owner = o; break; }
                if (owner is not null) Suspect(owner, 3);
                r.Findings.Add(new(IssueSeverity.Warning, $"{errors.Count} error line(s) in {name}", errors[0].Trim().Length > 300 ? errors[0].Trim()[..300] + "…" : errors[0].Trim(), owner?.Id));
            }
        }

        // ---- compatibility errors are crash suspects too
        foreach (var i in compatIssues?.Where(i => i.Severity == IssueSeverity.Error) ?? [])
        {
            r.Findings.Add(i);
            if (i.ModId is { } id && mods.FirstOrDefault(m => m.Id == id) is { } m) Suspect(m, 4);
        }

        // ---- recent changes
        var recent = mods.Where(m => m.Enabled && (game.LastGoodSession is null || m.InstalledAt > game.LastGoodSession)).ToList();
        foreach (var m in recent) Suspect(m, 1);
        if (game.LastCrash is not null && recent.Count > 0 && game.LastGoodSession is not null)
            r.Findings.Add(new(IssueSeverity.Info, "Added since the last clean session", string.Join(", ", recent.Select(m => m.Name)), recent[0].Id, "If the crashes started after these, turn them off first."));

        r.Suspects.AddRange(score.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
        var top = r.Suspects.Select(id => mods.FirstOrDefault(m => m.Id == id)).FirstOrDefault(m => m is not null);
        r.Summary = top is not null && score[top.Id] >= 5 ? $"Most likely cause: {top.Name}."
            : r.Findings.Count == 0 ? "No problems found in the logs."
            : mods.Count(m => m.Enabled) >= 4 ? "No single mod stands out. Use Find the bad mod to narrow it down in a few sessions."
            : "See the findings below.";
        return r;
    }

    public static InstalledMod? OwnerOf(IEnumerable<InstalledMod> mods, string fileName) =>
        mods.Where(m => m.Files.Any(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))).OrderByDescending(m => m.Enabled).ThenByDescending(m => m.Priority).FirstOrDefault();

    /// <summary>Matches a log source name ("MoreCompany", "Mods/BetterHUD") to a mod by name, folder or DLL name.</summary>
    public static InstalledMod? OwnerByName(IEnumerable<InstalledMod> mods, string name)
    {
        var key = Squash(name);
        if (key.Length < 3) return null;
        return mods.FirstOrDefault(m => Squash(m.Name) == key)
            ?? mods.FirstOrDefault(m => m.Files.Any(f => Squash(Path.GetFileNameWithoutExtension(f)) == key || f.Split('/').Any(seg => Squash(seg) == key)))
            ?? mods.FirstOrDefault(m => Squash(m.Name).Contains(key) || (Squash(m.Name).Length >= 5 && key.Contains(Squash(m.Name))));
    }

    private static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string ExitCodeName(uint code) => code switch
    {
        0xC0000005 => "access violation (0xC0000005)",
        0xC0000135 => "a DLL is missing (0xC0000135)",
        0xC0000142 => "a DLL failed to start (0xC0000142)",
        0xC0000409 => "fail-fast / stack buffer overrun (0xC0000409)",
        0xC000001D => "illegal CPU instruction (0xC000001D)",
        0xC0000374 => "heap corruption (0xC0000374)",
        0xC00000FD => "stack overflow (0xC00000FD)",
        0xE0434352 => ".NET exception (0xE0434352)",
        0x80000003 => "breakpoint (0x80000003)",
        _ => $"error 0x{code:X8}",
    };

    public static string ExitCodeAdvice(uint code) => code switch
    {
        0xC0000005 => "The game read or wrote memory it shouldn't. The usual causes are a mod or loader built for another game version, or two mods editing the same thing. Check the findings below.",
        0xC0000135 => "A file the game or a mod needs isn't there: usually a Visual C++ runtime (use Install runtimes on the game page) or a mod's required library.",
        0xC0000142 => "A DLL couldn't initialise. Common with a missing runtime, or a mod DLL for a different game version.",
        0xC0000409 => "The game stopped itself on a fatal check. Often an outdated plugin or loader; sometimes DRM/anti-tamper reacting to a mod or trainer.",
        0xC000001D => "Code used CPU instructions this CPU doesn't have: a mod or repack component built for another CPU.",
        0xC0000374 => "Memory got corrupted. Most often a mod DLL or an outdated loader.",
        0xC00000FD => "Something recursed forever: often a script mod.",
        0xE0434352 => "A .NET mod (BepInEx/MelonLoader) threw an error it didn't handle. Check LogOutput.log findings below.",
        0x80000003 => "The game hit a debug breakpoint: often anti-debug/DRM reacting to a trainer or injector.",
        _ => "Use the log findings below, or Find the bad mod.",
    };

    [GeneratedRegex(@"exit code 0x([0-9A-Fa-f]{8})")] private static partial Regex ExitCodeHex();
    [GeneratedRegex(@"[\w\-. ]+?\.(?:dll|asi)\b", RegexOptions.IgnoreCase)] private static partial Regex DllInText();
    [GeneratedRegex(@"[\w\-. ']+?\.(?:esp|esm|esl)\b", RegexOptions.IgnoreCase)] private static partial Regex PluginInText();
    [GeneratedRegex(@"\[(?:Error|Fatal)\s*:\s*([^\]]+)\]", RegexOptions.IgnoreCase)] private static partial Regex BepInExError();
    [GeneratedRegex(@"\b(error|exception|fatal|failed)\b", RegexOptions.IgnoreCase)] private static partial Regex ErrorWord();
    [GeneratedRegex(@"[Mm]ods[\\/]+([^\\/\s:]+)")] private static partial Regex ModFolderInText();
}

/// <summary>Finds recent loader and crash logs for a game.</summary>
public static class ModLogLocator
{
    public static List<LogFile> Find(string gameRoot, GameModLayout layout, DateTime sinceUtc, string documentsDir, string localLowDir, int maxBytes = 200_000)
    {
        var paths = new List<string>();
        void Add(string p) { if (File.Exists(p)) paths.Add(p); }
        void AddDir(string dir, string pattern = "*.log")
        {
            try { if (Directory.Exists(dir)) paths.AddRange(Directory.EnumerateFiles(dir, pattern)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        Add(Path.Combine(gameRoot, "BepInEx", "LogOutput.log"));
        Add(Path.Combine(gameRoot, "MelonLoader", "Latest.log"));
        if (layout.Ue4ssModsDir is { } ue) Add(Path.Combine(gameRoot, Path.GetDirectoryName(ue.Replace('/', Path.DirectorySeparatorChar))!, "UE4SS.log"));
        AddDir(Path.Combine(gameRoot, "bin", "x64", "plugins", "cyber_engine_tweaks"));
        AddDir(Path.Combine(gameRoot, "red4ext", "logs"));
        AddDir(Path.Combine(gameRoot, "r6", "logs"));
        AddDir(Path.Combine(gameRoot, layout.ExeDir.Replace('/', Path.DirectorySeparatorChar)));
        if (layout.PluginsFolderName is { } docs)
            foreach (var se in new[] { "SKSE", "F4SE", "SFSE", "NVSE", "OBSE" })
            {
                AddDir(Path.Combine(documentsDir, "My Games", docs, se));
                AddDir(Path.Combine(documentsDir, "My Games", docs, se, "Plugins"));
                AddDir(Path.Combine(documentsDir, "My Games", docs, se, "Crashlogs"));
            }
        // Unity Player.log: %USERPROFILE%\AppData\LocalLow\{company}\{product}
        try
        {
            var exeDir = Path.Combine(gameRoot, layout.ExeDir.Replace('/', Path.DirectorySeparatorChar));
            foreach (var dataDir in Directory.Exists(exeDir) ? Directory.EnumerateDirectories(exeDir, "*_Data") : [])
            {
                var info = Path.Combine(dataDir, "app.info");
                if (!File.Exists(info)) continue;
                var lines = File.ReadAllLines(info);
                if (lines.Length >= 2) { Add(Path.Combine(localLowDir, lines[0].Trim(), lines[1].Trim(), "Player.log")); Add(Path.Combine(localLowDir, lines[0].Trim(), lines[1].Trim(), "Player-prev.log")); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var result = new List<LogFile>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fi = new FileInfo(p);
                if (fi.LastWriteTimeUtc < sinceUtc) continue;
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > maxBytes) fs.Position = fs.Length - maxBytes; // the end of a log is what matters
                using var sr = new StreamReader(fs, Encoding.UTF8);
                result.Add(new LogFile(p, sr.ReadToEnd(), fi.LastWriteTimeUtc));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return [.. result.OrderByDescending(l => l.ModifiedUtc).Take(20)];
    }
}

// ============================================================================ find the bad mod

public sealed record BisectStep(bool Done, Guid? Culprit, HashSet<Guid> EnableSet, string Message);

/// <summary>
/// Binary search for the mod causing a crash: known-good mods stay on, half the suspects are tested each
/// session. With N mods it takes about log2(N) sessions. Assumes one culprit (mods that need each other
/// can make results confusing; the message says so).
/// </summary>
public static class ModBisect
{
    public static BisectStep Start(ModSetup setup)
    {
        var enabled = setup.Mods.Where(m => m.Enabled).Select(m => m.Id).ToList();
        setup.Bisect = new BisectState { Suspects = enabled, OriginallyEnabled = enabled, Round = 0 };
        return Next(setup, $"Testing {enabled.Count} mods. ");
    }

    public static BisectStep Report(ModSetup setup, bool crashed)
    {
        var b = setup.Bisect ?? throw new InvalidOperationException("No search in progress.");
        b.Suspects = crashed ? b.Testing : b.Suspects.Except(b.Testing).ToList();
        return Next(setup, crashed ? "It crashed with that half, so the problem is in it. " : "No crash, so that half is fine. ");
    }

    private static BisectStep Next(ModSetup setup, string prefix)
    {
        var b = setup.Bisect!;
        b.Round++;
        if (b.Suspects.Count == 0)
        {
            var restore = b.OriginallyEnabled.ToHashSet();
            setup.Bisect = null;
            return new(true, null, restore, prefix + "No single mod reproduces the crash: it may need two mods together, or not be mod-related. Your mods are back as they were.");
        }
        if (b.Suspects.Count == 1)
        {
            var culprit = b.Suspects[0];
            var restore = b.OriginallyEnabled.Where(id => id != culprit).ToHashSet();
            setup.Bisect = null;
            var name = setup.Mods.FirstOrDefault(m => m.Id == culprit)?.Name ?? "a mod";
            return new(true, culprit, restore, prefix + $"Found it: {name}. It's been turned off and everything else is back on.");
        }
        b.Testing = b.Suspects.Take((b.Suspects.Count + 1) / 2).ToList();
        var good = b.OriginallyEnabled.Except(b.Suspects);
        var set = good.Concat(b.Testing).ToHashSet();
        return new(false, null, set, prefix + $"Round {b.Round}: {b.Testing.Count} of {b.Suspects.Count} suspects are on. Play until it would normally crash, then report what happened.");
    }

    public static BisectStep Cancel(ModSetup setup)
    {
        var restore = setup.Bisect?.OriginallyEnabled.ToHashSet() ?? setup.Mods.Where(m => m.Enabled).Select(m => m.Id).ToHashSet();
        setup.Bisect = null;
        return new(true, null, restore, "Search stopped. Your mods are back as they were.");
    }
}

// ============================================================================ error report

public static class ModErrorReport
{
    /// <summary>A plain-text report to paste on a mod page, forum or Reddit.</summary>
    public static string Build(Game game, GameModLayout layout, string? gameVersion, string hardware, IReadOnlyList<ModIssue> compat, TroubleReport? trouble, IReadOnlyList<LogFile> logs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {game.Name} – mod report");
        sb.AppendLine($"Game: {Path.GetFileName(game.ExePath)} {gameVersion ?? "(version unknown)"} · {layout.Framework}");
        sb.AppendLine($"Loaders: {(layout.Loaders.Count == 0 ? "none" : string.Join(", ", layout.Loaders))}{(layout.ScriptExtenderRuntime is { } rt ? $" ({layout.ScriptExtenderName} for {rt})" : "")}");
        sb.AppendLine($"PC: {hardware}");
        if (game.LastCrash is { } c) sb.AppendLine($"Last crash: {c.When.ToLocalTime():yyyy-MM-dd HH:mm} in {c.FaultingModule ?? "unknown"}");
        sb.AppendLine();
        sb.AppendLine("### Mods (load order, last wins)");
        foreach (var m in game.ModSetup.Mods.OrderBy(m => m.Priority))
            sb.AppendLine($"- [{(m.Enabled ? "x" : " ")}] {m.Name} {m.Version} ({m.SourceLabel}{(m.SourceId is { } id ? $" {id}" : "")})");
        if (trouble is not null)
        {
            sb.AppendLine();
            sb.AppendLine("### Diagnosis");
            sb.AppendLine(trouble.Summary);
            foreach (var f in trouble.Findings) sb.AppendLine($"- {f.Severity}: {f.Title}. {f.Detail}");
        }
        if (compat.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Compatibility check");
            foreach (var i in compat) sb.AppendLine($"- {i.Severity}: {i.Title}. {i.Detail}");
        }
        foreach (var log in logs.Take(4))
        {
            var tail = string.Join('\n', log.Text.Split('\n').TakeLast(40));
            sb.AppendLine();
            sb.AppendLine($"### {Path.GetFileName(log.Path)} (last 40 lines)");
            sb.AppendLine("```");
            sb.AppendLine(Redact(tail.TrimEnd()));
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    /// <summary>Hides the Windows user name in paths before sharing.</summary>
    public static string Redact(string text) => Regex.Replace(text, @"(?i)([A-Z]:[\\/]+Users[\\/]+)[^\\/]+", "$1<user>");
}
