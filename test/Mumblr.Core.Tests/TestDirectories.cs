namespace Mumblr.Core.Tests;

/// <summary>Deletes a test directory that may hold read-only files - Windows refuses those otherwise.</summary>
public static class TestDirectories
{
    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(directory, recursive: true);
    }
}
