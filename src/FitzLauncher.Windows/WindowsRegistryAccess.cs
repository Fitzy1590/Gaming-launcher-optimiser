using FitzLauncher.Core.Optimization;
using Microsoft.Win32;
using CoreHive = FitzLauncher.Core.Models.RegistryHive;
using CoreType = FitzLauncher.Core.Models.RegistryValueType;

namespace FitzLauncher.Windows;

public sealed class WindowsRegistryAccess : IRegistryAccess
{
    private static RegistryKey Root(CoreHive hive) => hive == CoreHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

    public string? GetString(CoreHive hive, string key, string name)
    {
        using var k = Root(hive).OpenSubKey(key);
        return k?.GetValue(name)?.ToString();
    }

    public void Set(CoreHive hive, string key, string name, string value, CoreType type)
    {
        using var k = Root(hive).CreateSubKey(key, writable: true);
        if (type == CoreType.DWord) k.SetValue(name, int.Parse(value), RegistryValueKind.DWord);
        else k.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(CoreHive hive, string key, string name)
    {
        using var k = Root(hive).OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    internal static object? Read(RegistryKey root, string key, string name)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            return k?.GetValue(name);
        }
        catch (System.Security.SecurityException) { return null; }
    }
}
