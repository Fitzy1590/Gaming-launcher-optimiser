using System.Xml.Linq;

namespace FitzLauncher.Core.Mods;

public sealed record FomodFile(string Source, string Dest);
public sealed record FomodResult(List<FomodFile> Files, string Summary, List<string> Picked);

/// <summary>
/// Resolves a FOMOD ModuleConfig.xml without asking: required files, then for each option group the
/// author's Required/Recommended choices (or the first usable option where one must be picked), plus
/// conditional installs whose flags those choices set. The summary lists what was picked.
/// </summary>
public static class FomodInstaller
{
    public static FomodResult Resolve(string xml, IReadOnlyList<string> entriesRelToFomodRoot)
    {
        // No DTDs or external entities from downloaded files.
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
        var doc = XDocument.Load(reader);
        XElement? E(XElement? p, string name) => p?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        IEnumerable<XElement> Es(XElement? p, string name) => p?.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? [];
        string? A(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        var root = doc.Root!;
        var files = new List<(XElement el, int priority)>();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<string>();

        foreach (var f in E(root, "requiredInstallFiles")?.Elements() ?? []) files.Add((f, Priority(f)));

        foreach (var step in Es(E(root, "installSteps"), "installStep"))
        {
            if (!Visible(E(step, "visible"))) continue;
            foreach (var group in Es(E(step, "optionalFileGroups"), "group"))
            {
                var type = A(group, "type") ?? "SelectAny";
                var plugins = Es(E(group, "plugins"), "plugin").Select(p => (el: p, name: A(p, "name") ?? "?", kind: PluginType(p))).ToList();
                var usable = plugins.Where(p => p.kind != "NotUsable").ToList();
                var chosen = type switch
                {
                    "SelectAll" => plugins,
                    "SelectExactlyOne" => [usable.FirstOrDefault(p => p.kind == "Required") is { el: not null } r ? r
                                           : usable.FirstOrDefault(p => p.kind == "Recommended") is { el: not null } rec ? rec
                                           : usable.FirstOrDefault()],
                    "SelectAtMostOne" => usable.Where(p => p.kind is "Required" or "Recommended").Take(1).ToList(),
                    "SelectAtLeastOne" => usable.Where(p => p.kind is "Required" or "Recommended").DefaultIfEmpty(usable.FirstOrDefault()).ToList(),
                    _ => usable.Where(p => p.kind is "Required" or "Recommended").ToList(),
                };
                foreach (var c in chosen.Where(c => c.el is not null))
                {
                    picked.Add(c.name);
                    foreach (var f in E(c.el, "files")?.Elements() ?? []) files.Add((f, Priority(f)));
                    foreach (var flag in Es(E(c.el, "conditionFlags"), "flag")) flags[A(flag, "name") ?? ""] = flag.Value;
                }
            }
        }

        foreach (var pattern in Es(E(E(root, "conditionalFileInstalls"), "patterns"), "pattern"))
            if (Matches(E(pattern, "dependencies")))
                foreach (var f in E(pattern, "files")?.Elements() ?? []) files.Add((f, Priority(f)));

        // Expand files/folders; later (higher priority) entries win the same destination.
        var result = new Dictionary<string, FomodFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (el, _) in files.OrderBy(f => f.priority))
        {
            var src = ModInstallPlanner.Normalize(A(el, "source") ?? "").TrimEnd('/');
            var dst = ModInstallPlanner.Normalize(A(el, "destination") ?? "").TrimEnd('/');
            if (src.Length == 0) continue;
            if (el.Name.LocalName.Equals("folder", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in entriesRelToFomodRoot.Where(e => e.StartsWith(src + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    var rest = e[(src.Length + 1)..];
                    var dest = dst.Length == 0 ? rest : $"{dst}/{rest}";
                    result[dest] = new FomodFile(e, dest);
                }
            }
            else
            {
                var entry = entriesRelToFomodRoot.FirstOrDefault(e => e.Equals(src, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var dest = A(el, "destination") is null ? Path.GetFileName(src) : dst.Length == 0 ? Path.GetFileName(src) : dst;
                result[dest] = new FomodFile(entry, dest);
            }
        }

        var name = E(root, "moduleName")?.Value?.Trim();
        var summary = picked.Count == 0
            ? $"FOMOD installer{(name is { Length: > 0 } ? $" ({name})" : "")}: required files only."
            : $"FOMOD installer{(name is { Length: > 0 } ? $" ({name})" : "")}: picked {string.Join(", ", picked.Distinct())}. Reinstall from the mod's own folder if you want different options.";
        return new FomodResult([.. result.Values], summary, picked);

        bool Visible(XElement? vis) => vis is null || Matches(vis);

        bool Matches(XElement? deps)
        {
            if (deps is null) return true;
            var op = A(deps, "operator") ?? "And";
            var results = deps.Elements().Select(d => d.Name.LocalName.ToLowerInvariant() switch
            {
                "flagdependency" => flags.TryGetValue(A(d, "flag") ?? "", out var v) && v == (A(d, "value") ?? ""),
                "dependencies" => Matches(d),
                _ => true, // file/game/fomm version checks: assume satisfied
            }).ToList();
            return results.Count == 0 || (op.Equals("Or", StringComparison.OrdinalIgnoreCase) ? results.Any(r => r) : results.All(r => r));
        }

        string PluginType(XElement p)
        {
            var desc = E(p, "typeDescriptor");
            var simple = E(desc, "type");
            if (simple is not null) return A(simple, "name") ?? "Optional";
            var dep = E(desc, "dependencyType");
            if (dep is null) return "Optional";
            foreach (var pat in Es(E(dep, "patterns"), "pattern"))
                if (Matches(E(pat, "dependencies"))) return A(E(pat, "type")!, "name") ?? "Optional";
            return A(E(dep, "defaultType")!, "name") ?? "Optional";
        }

        int Priority(XElement f) => int.TryParse(A(f, "priority"), out var p) ? p : 0;
    }
}
