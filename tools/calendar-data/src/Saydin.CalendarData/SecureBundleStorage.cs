namespace Saydin.CalendarData;

internal static class SecureBundleStorage
{
    public static string EnsurePrivateDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        EnsureAbsoluteNoReparse(Path.GetDirectoryName(full)! , "staging_path_unsafe");
        Directory.CreateDirectory(full);
        EnsureAbsoluteNoReparse(full, "staging_path_unsafe");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return full;
    }

    public static void EnsureRegularFileNoFollow(string root, string path, string errorCode)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CalendarDataException(errorCode, path);
        EnsureNoReparseComponents(fullRoot, fullPath, errorCode);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new CalendarDataException(errorCode, path);
    }

    public static void WriteNewPrivateFile(string root, string relativePath, ReadOnlySpan<byte> content)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, target);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CalendarDataException("output_path_escape", relativePath);

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        EnsureNoReparseComponents(fullRoot, parent, "staging_path_unsafe");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static FileStream OpenExclusiveLock(string stagingRoot)
    {
        var path = Path.Combine(stagingRoot, ".acquisition.lock");
        if (File.Exists(path))
            EnsureRegularFileNoFollow(stagingRoot, path, "acquisition_lock_unsafe");
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return stream;
    }

    private static void EnsureNoReparseComponents(string root, string target, string errorCode)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullTarget = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(fullRoot, fullTarget);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CalendarDataException(errorCode, target);

        Check(fullRoot, errorCode);
        var current = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) Check(current, errorCode);
        }
    }

    private static void Check(string path, string errorCode)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new CalendarDataException(errorCode, path);
    }

    private static void EnsureAbsoluteNoReparse(string path, string errorCode)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new CalendarDataException(errorCode, path);
        var current = root;
        foreach (var part in full[root.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) Check(current, errorCode);
        }
    }
}
