namespace FitzLauncher.Core.Scanning;

/// <summary>User folders where games keep their config. Injected so scanning is testable.</summary>
public sealed record ScanContext(string LocalAppData, string Documents, string SavedGames, string RoamingAppData)
{
    public static ScanContext FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new ScanContext(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Saved Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }
}
