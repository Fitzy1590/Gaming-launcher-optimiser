using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Models;

/// <summary>A concrete, revertible action. Subclasses are serialised polymorphically.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(IniSetAction), "ini")]
[JsonDerivedType(typeof(KeyValueFileSetAction), "kv")]
[JsonDerivedType(typeof(RegistrySetAction), "reg")]
[JsonDerivedType(typeof(LaunchArgumentAction), "args")]
[JsonDerivedType(typeof(RtssFrameLimitAction), "rtss")]
[JsonDerivedType(typeof(ManualStepAction), "manual")]
public abstract class TweakAction
{
    /// <summary>One-line human description, e.g. "Engine.ini: t.MaxFPS = 117".</summary>
    public abstract string Describe();
}

/// <summary>Set a key inside an INI section (Unreal Engine.ini / GameUserSettings.ini and friends).</summary>
public sealed class IniSetAction : TweakAction
{
    public string FilePath { get; set; } = "";
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Create the file if it is missing (only used when the directory already exists).</summary>
    public bool CreateIfMissing { get; set; } = true;
    public override string Describe() => $"{Path.GetFileName(FilePath)} [{Section}] {Key}={Value}";
}

/// <summary>Set key=value in a flat file such as Unity's boot.config.</summary>
public sealed class KeyValueFileSetAction : TweakAction
{
    public string FilePath { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public override string Describe() => $"{Path.GetFileName(FilePath)}: {Key}={Value}";
}

public enum RegistryHive { CurrentUser, LocalMachine }
public enum RegistryValueType { String, DWord }

public sealed class RegistrySetAction : TweakAction
{
    public RegistryHive Hive { get; set; }
    public string KeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string Value { get; set; } = "";
    public RegistryValueType ValueType { get; set; }
    /// <summary>When true, the value is a ';'-separated list of Name=Value pairs (DirectX UserGpuPreferences)
    /// and only the given pairs are merged in, leaving other pairs alone.</summary>
    public bool MergeSemicolonPairs { get; set; }
    public override string Describe() =>
        $"{(Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{KeyPath} : {ValueName} = {Value}";
}

/// <summary>Append launch arguments (stored on the Game; the launcher passes them at start).</summary>
public sealed class LaunchArgumentAction : TweakAction
{
    public string Arguments { get; set; } = "";
    public override string Describe() => $"Launch option: {Arguments}";
}

/// <summary>Write an RTSS per-game profile that caps the frame rate (works for any graphics API).</summary>
public sealed class RtssFrameLimitAction : TweakAction
{
    public string RtssDirectory { get; set; } = "";
    public string ExeName { get; set; } = "";
    public int Limit { get; set; }
    public override string Describe() => $"RivaTuner frame limit for {ExeName}: {Limit} FPS";
}

/// <summary>Something the launcher can't do itself (BIOS, driver panel). Shown as a checklist item.</summary>
public sealed class ManualStepAction : TweakAction
{
    public string Instructions { get; set; } = "";
    public override string Describe() => Instructions;
}

/// <summary>Record of an action that was applied, with what it replaced, so it can be undone exactly.</summary>
public sealed class AppliedChange
{
    public string TweakId { get; set; } = "";
    public string Title { get; set; } = "";
    public TweakAction Action { get; set; } = null!;
    /// <summary>Previous value, or null if the key/value/file did not exist.</summary>
    public string? PreviousValue { get; set; }
    public bool PreviouslyExisted { get; set; }
    /// <summary>Full backup of the file before the first change, when the change edited a file.</summary>
    public string? BackupPath { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.Now;
}
