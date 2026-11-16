using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Emulators;

/// <summary>One BlueStacks Android instance (e.g. "Pie64"). AdbPort is null when the config does not list it.</summary>
public sealed record BlueStacksInstance(string Id, string Name, int? AdbPort);

/// <summary>An Android app found in an instance, ready to become a library card.</summary>
public sealed record AndroidApp(string Package, string Name);

/// <summary>
/// BlueStacks 5/10 parsing and launch arguments. Config key names (<c>bst.instance.&lt;id&gt;.display_name</c>,
/// <c>.adb_port</c>) and the <c>--cmd launchApp</c> switch are from memory: unverified until a device test (SPEC §15).
/// </summary>
public static partial class BlueStacks
{
    public const string PlayerExe = "HD-Player.exe";
    public const string AdbExe = "HD-Adb.exe";

    [GeneratedRegex(@"^bst\.instance\.([A-Za-z0-9_]+)\.(display_name|adb_port)=""?([^""\r\n]*)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex ConfKey();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex InstanceName();

    /// <summary>Instances listed in <c>bluestacks.conf</c>. Empty when the text has none.</summary>
    public static List<BlueStacksInstance> ParseInstances(string conf)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (Match m in ConfKey().Matches(conf))
        {
            var id = m.Groups[1].Value;
            if (!order.Contains(id, StringComparer.OrdinalIgnoreCase)) order.Add(id);
            if (m.Groups[2].Value == "display_name") names[id] = m.Groups[3].Value;
            else if (int.TryParse(m.Groups[3].Value, out var p) && p is > 0 and < 65536) ports[id] = p;
        }
        return [.. order.Select(id => new BlueStacksInstance(id, names.GetValueOrDefault(id) is { Length: > 0 } n ? n : id, ports.TryGetValue(id, out var port) ? port : null))];
    }

    public static bool IsValidPackage(string? package) => !string.IsNullOrEmpty(package) && package.Length <= 200 && PackageName().IsMatch(package);

    /// <summary>The command-line text that opens one app straight away. Throws on a name that is not a plain Android package, so nothing odd reaches a command line.</summary>
    public static string LaunchArguments(string instanceId, string package)
    {
        if (!InstanceName().IsMatch(instanceId)) throw new ArgumentException("Not a valid BlueStacks instance name.", nameof(instanceId));
        if (!IsValidPackage(package)) throw new ArgumentException("Not a valid Android package name.", nameof(package));
        return $"--instance {instanceId} --cmd launchApp --package {package}";
    }

    private static readonly string[] SystemPrefixes = ["com.android.", "com.google.", "com.bluestacks", "com.uncube.", "android."];

    /// <summary>Reads <c>pm list packages -3</c> output (one <c>package:name</c> per line) into app cards, leaving out BlueStacks' own apps.</summary>
    public static List<AndroidApp> ParsePackages(string adbOutput)
    {
        var apps = new List<AndroidApp>();
        foreach (var raw in adbOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            var pkg = line["package:".Length..].Trim();
            if (!IsValidPackage(pkg) || SystemPrefixes.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (apps.Any(a => a.Package == pkg)) continue;
            apps.Add(new AndroidApp(pkg, FriendlyName(pkg)));
        }
        return [.. apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jp.pokemon.pokemontcgp"] = "Pokémon TCG Pocket",
        ["com.pokemon.pokemontcgp"] = "Pokémon TCG Pocket",
    };

    /// <summary>A readable title from a package name ("com.example.cool_game" → "Cool Game"), with a few known titles spelled properly.</summary>
    public static string FriendlyName(string package)
    {
        if (KnownNames.TryGetValue(package, out var known)) return known;
        var last = package[(package.LastIndexOf('.') + 1)..].Replace('_', ' ');
        return last.Length == 0 ? package : char.ToUpperInvariant(last[0]) + last[1..];
    }
}
