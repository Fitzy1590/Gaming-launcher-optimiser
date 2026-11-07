namespace FitzLauncher.Core.Tests;

/// <summary>Disposable temp directory with helpers to lay out fake game installs.</summary>
public sealed class TestDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fitz-tests", Guid.NewGuid().ToString("N"));

    public TestDir() => Directory.CreateDirectory(Root);

    public string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    public string File(string relative, string content = "", int sizeBytes = 0)
    {
        var path = Path.Combine(Root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (sizeBytes > 0) System.IO.File.WriteAllBytes(path, new byte[sizeBytes]);
        else System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Dir(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
