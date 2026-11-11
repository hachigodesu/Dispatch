namespace Dispatch.Core.Platform;

public static class PathCase
{
    /// <summary>
    /// Returns the path with the casing stored on disk (Steam's registry value is all lower case,
    /// e.g. "c:\program files (x86)\steam"). Falls back to the input for parts that don't exist.
    /// </summary>
    public static string Canonical(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        string current = root.ToUpperInvariant();
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string? match = null;
            try
            {
                match = Directory.EnumerateFileSystemEntries(current, part).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the given casing for parts we can't list.
            }

            current = match ?? Path.Combine(current, part);
        }

        return current;
    }
}
