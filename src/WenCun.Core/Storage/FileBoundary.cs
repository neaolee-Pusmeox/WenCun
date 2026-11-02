namespace WenCun.Core.Storage;

internal static class FileBoundary
{
    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string RequireWithin(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsWithin(Normalize(root), full)) throw new IOException("文件路径超出允许的目录。");
        RequireNoLinks(full);
        return full;
    }

    internal static void RequireNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不能通过符号链接或目录联接操作文库文件。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static IEnumerable<string> Files(string directory)
    {
        RequireNoLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RequireNoLinks(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in Files(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    internal static bool TryDeleteDirectory(string root, string directory)
    {
        try
        {
            RequireWithin(root, directory);
            if (!Directory.Exists(directory)) return false;
            // Check the complete tree before recursion can encounter a junction.
            foreach (var _ in Files(directory)) { }
            Directory.Delete(directory, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string TemporarySibling(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    internal static void Publish(string temporary, string path)
    {
        RequireNoLinks(path);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    internal static string Write(string path, Action<string> write)
    {
        RequireNoLinks(path);
        var temporary = TemporarySibling(path);
        try
        {
            write(temporary);
            Publish(temporary, path);
            return path;
        }
        finally { File.Delete(temporary); }
    }
}
