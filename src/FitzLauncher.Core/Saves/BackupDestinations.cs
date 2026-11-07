using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Saves;

/// <summary>
/// Resolves backup destinations to folders that are reachable right now.
/// USB drives are identified by a small marker file at the drive root, so a different drive
/// letter next time doesn't matter. Google Drive uses the Google Drive for desktop folder
/// (e.g. G:\My Drive), so no Google sign-in or API key is needed in the launcher.
/// </summary>
public static class BackupDestinations
{
    public const string UsbMarker = ".fitzlauncher-backup-drive";

    public static List<string> Resolve(IEnumerable<BackupDestination> destinations, IEnumerable<string>? driveRoots = null)
    {
        var roots = (driveRoots ?? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName)).ToList();
        var result = new List<string>();
        foreach (var d in destinations.Where(d => d.Enabled))
        {
            switch (d.Kind)
            {
                case BackupDestinationKind.Usb:
                {
                    var root = roots.FirstOrDefault(r => d.DriveId is { } id && MarkerId(r) == id);
                    if (root is null) continue; // not plugged in: catches up next time
                    // For USB, Path is relative to the drive root, so any drive letter works.
                    result.Add(d.Path is "" or "." ? root : Path.Combine(root, d.Path));
                    break;
                }
                default:
                    if (Directory.Exists(d.Path)) result.Add(d.Path);
                    break;
            }
        }
        return result;
    }

    /// <summary>Marks a USB drive so it can be recognised later. Returns the destination to store.</summary>
    public static BackupDestination AddUsb(string folderOnDrive, string label, string? driveRoot = null)
    {
        var root = driveRoot ?? Path.GetPathRoot(folderOnDrive)!;
        // One marker per drive (reused if the drive is already known), so several folders can share a drive.
        var driveId = MarkerId(root) ?? Guid.NewGuid();
        var marker = Path.Combine(root, UsbMarker);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, driveId.ToString());
            try { File.SetAttributes(marker, File.GetAttributes(marker) | FileAttributes.Hidden); } catch (IOException) { }
        }
        return new BackupDestination
        {
            Kind = BackupDestinationKind.Usb, DriveId = driveId, Label = label,
            Path = Path.GetRelativePath(root, folderOnDrive),
        };
    }

    private static Guid? MarkerId(string root)
    {
        try
        {
            var marker = Path.Combine(root, UsbMarker);
            return File.Exists(marker) && Guid.TryParse(File.ReadAllText(marker).Trim(), out var id) ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Finds the Google Drive for desktop folder ("My Drive"), if installed.</summary>
    public static string? FindGoogleDrive(IEnumerable<string>? driveRoots = null, string? userProfile = null)
    {
        var roots = driveRoots ?? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);
        foreach (var r in roots)
        {
            var my = Path.Combine(r, "My Drive");
            if (Directory.Exists(my)) return my;
        }
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { Path.Combine(home, "My Drive"), Path.Combine(home, "Google Drive", "My Drive"), Path.Combine(home, "Google Drive") })
            if (Directory.Exists(candidate)) return candidate;
        return null;
    }
}
