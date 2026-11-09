using System.Text.Json.Nodes;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Mods;

/// <summary>How the mod browser orders results.</summary>
public enum ModRanking { Relevance, MostDownloaded, MostEndorsed, PopularThisMonth, RecentlyUpdated, Newest }

/// <summary>
/// Builds Nexus Mods GraphQL v2 <c>mods(filter, sort)</c> arguments for a ranking (schema from the official
/// Nexus Mods App). "Popular this month" = created in the last 30 days, by downloads; "Recently updated" =
/// updated in the last 14 days, by downloads, so it shows what people are actually picking up.
/// </summary>
public static class NexusQuery
{
    public static string Label(ModRanking r) => r switch
    {
        ModRanking.MostDownloaded => "Most downloaded",
        ModRanking.MostEndorsed => "Most endorsed",
        ModRanking.PopularThisMonth => "Popular this month",
        ModRanking.RecentlyUpdated => "Recently updated",
        ModRanking.Newest => "Newest",
        _ => "Best match",
    };

    public static (JsonObject Filter, JsonArray Sort) Build(string domain, string? query, ModRanking ranking, DateTimeOffset now, bool includeAdult = false)
    {
        static JsonArray Val(string value, string op) => [new JsonObject { ["value"] = value, ["op"] = op }];
        var filter = new JsonObject { ["gameDomainName"] = Val(domain, "EQUALS") };
        if (!includeAdult) filter["adultContent"] = new JsonArray(new JsonObject { ["value"] = false, ["op"] = "EQUALS" });
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if (hasQuery) filter["nameStemmed"] = Val(query!.Trim(), "MATCHES");

        string Unix(TimeSpan ago) => (now - ago).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ranking == ModRanking.PopularThisMonth) filter["createdAt"] = Val(Unix(TimeSpan.FromDays(30)), "GTE");
        if (ranking == ModRanking.RecentlyUpdated) filter["updatedAt"] = Val(Unix(TimeSpan.FromDays(14)), "GTE");

        var key = ranking switch
        {
            ModRanking.MostDownloaded or ModRanking.PopularThisMonth or ModRanking.RecentlyUpdated => "downloads",
            ModRanking.MostEndorsed => "endorsements",
            ModRanking.Newest => "createdAt",
            _ => hasQuery ? "relevance" : "endorsements",
        };
        return (filter, [new JsonObject { [key] = new JsonObject { ["direction"] = "DESC" } }]);
    }
}

/// <summary>What one mod needs, as Nexus lists it.</summary>
public sealed record ModRequirementInfo(long ModId, string Name, IReadOnlyList<RequiredMod> Mods, IReadOnlyList<string> Dlc);

/// <summary>A requirement: another Nexus mod for this game, or something external (a site link).</summary>
public sealed record RequiredMod(long? ModId, string Name, string? Url, bool External, string? Notes);

/// <summary>The full set of things to install for a mod, requirements first.</summary>
public sealed class RequirementPlan
{
    /// <summary>Nexus mods to install, dependencies before the mods that need them (the requested mod last).</summary>
    public List<(long ModId, string Name)> Install { get; } = [];
    public List<(long ModId, string Name)> AlreadyInstalled { get; } = [];
    public List<RequiredMod> External { get; } = [];
    public List<string> Dlc { get; } = [];
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Walks a mod's Nexus requirements recursively (requirements of requirements), skipping what's installed,
/// guarding against cycles and very deep chains, and orders the installs so dependencies come first.
/// </summary>
public static class RequirementResolver
{
    public static async Task<RequirementPlan> ResolveAsync(long rootModId, string rootName, Func<long, Task<ModRequirementInfo?>> fetch,
        Func<long, bool> isInstalled, int maxDepth = 4, int maxMods = 25)
    {
        var plan = new RequirementPlan();
        var visited = new HashSet<long>();
        var onPath = new HashSet<long>();
        var externals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task Visit(long id, string name, int depth)
        {
            if (visited.Contains(id)) return;
            if (onPath.Contains(id)) { plan.Notes.Add($"{name} is part of a requirement loop; it's installed once."); return; }
            if (depth > maxDepth) { plan.Notes.Add($"{name}'s requirements go deeper than {maxDepth} levels; check its page."); return; }
            if (visited.Count >= maxMods) { plan.Notes.Add("Long requirement chain: the rest are listed on the mod pages."); return; }
            onPath.Add(id);
            var info = await fetch(id).ConfigureAwait(false);
            if (info is not null)
            {
                foreach (var d in info.Dlc.Where(d => !plan.Dlc.Contains(d))) plan.Dlc.Add(d);
                foreach (var r in info.Mods)
                {
                    if (r.External || r.ModId is null)
                    {
                        if (externals.Add(r.Url ?? r.Name)) plan.External.Add(r);
                        continue;
                    }
                    if (isInstalled(r.ModId.Value))
                    {
                        if (!plan.AlreadyInstalled.Any(x => x.ModId == r.ModId)) plan.AlreadyInstalled.Add((r.ModId.Value, r.Name));
                        continue;
                    }
                    await Visit(r.ModId.Value, r.Name, depth + 1).ConfigureAwait(false);
                }
            }
            onPath.Remove(id);
            visited.Add(id);
            plan.Install.Add((id, info?.Name ?? name)); // post-order: requirements first
        }

        await Visit(rootModId, rootName, 0).ConfigureAwait(false);
        return plan;
    }
}

public enum CompatVerdict { Good, Check, Problem }

/// <summary>A mod's compatibility with this game install, before downloading.</summary>
public sealed record CompatReport(CompatVerdict Verdict, IReadOnlyList<string> Reasons)
{
    public string Summary => Verdict switch
    {
        CompatVerdict.Good => "Looks compatible",
        CompatVerdict.Check => "Check before installing",
        _ => "Likely problem",
    };
}

/// <summary>
/// Pre-install compatibility: anti-cheat, loaders the mod family needs, requirements, DLC, and whether the game
/// was updated after the mod's latest file (mods with code often break on game updates).
/// </summary>
public static class ModPreInstallCheck
{
    public static CompatReport Assess(GameModLayout layout, RequirementPlan? plan, DateTimeOffset? modUpdated, DateTimeOffset? gameUpdated,
        IEnumerable<string>? loadersNeeded = null, bool deprecated = false)
    {
        var reasons = new List<string>();
        var verdict = CompatVerdict.Good;
        void Bump(CompatVerdict v, string why) { reasons.Add(why); if (v > verdict) verdict = v; }

        if (deprecated) Bump(CompatVerdict.Problem, "Marked deprecated by its author.");
        if (layout.HasAntiCheat) Bump(CompatVerdict.Check, "This game has anti-cheat: use mods offline only.");
        foreach (var need in loadersNeeded ?? [])
            if (!layout.Loaders.Contains(need, StringComparer.OrdinalIgnoreCase))
                Bump(CompatVerdict.Check, $"Needs {need}, which isn't installed yet.");
        if (plan is not null)
        {
            var extra = plan.Install.Count - 1;
            if (extra > 0) reasons.Add($"Also installs {extra} required mod(s): {string.Join(", ", plan.Install.Take(plan.Install.Count - 1).Select(x => x.Name))}.");
            foreach (var e in plan.External) Bump(CompatVerdict.Check, $"Needs {e.Name} from outside Nexus{(e.Url is { } u ? $" ({u})" : "")}.");
            foreach (var d in plan.Dlc) Bump(CompatVerdict.Check, $"Needs the {d} DLC.");
        }
        // Big updates that broke most older mods: Witcher 3 next-gen (4.0) and Cyberpunk 2.0.
        if (modUpdated is { } m0 && gameUpdated is { } g0 && BigUpdate(layout.Framework) is { } big && g0 >= big.Date && m0 < big.Date)
            Bump(CompatVerdict.Check, $"Last updated before {big.Name} ({big.Date:MMM yyyy}); your game is newer, so check the mod page says it works with {big.Name}.");
        else if (modUpdated is { } mu && gameUpdated is { } gu && gu > mu.AddDays(2))
            Bump(CompatVerdict.Check, $"The game was updated ({gu:d MMM yyyy}) after this mod's last update ({mu:d MMM yyyy}); if it uses a script extender or DLLs, check the comments for an update.");
        if (reasons.Count == 0) reasons.Add("No requirements or known conflicts with this game install.");
        return new CompatReport(verdict, reasons);
    }

    private static (string Name, DateTimeOffset Date)? BigUpdate(ModFramework f) => f switch
    {
        ModFramework.Witcher3 => ("the next-gen update 4.0", new DateTimeOffset(2022, 12, 14, 0, 0, 0, TimeSpan.Zero)),
        ModFramework.Cyberpunk => ("update 2.0", new DateTimeOffset(2023, 9, 21, 0, 0, 0, TimeSpan.Zero)),
        _ => null,
    };
}
