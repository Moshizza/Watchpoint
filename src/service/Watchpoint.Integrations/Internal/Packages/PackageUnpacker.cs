using System.IO.Compression;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>Limits of a package's content (spec IRT §4.3 step 1).</summary>
/// <param name="MaxTotalBytes">Maximum unpacked size.</param>
/// <param name="MaxFiles">Maximum number of entries.</param>
/// <param name="MaxCompressionRatio">Maximum unpacked size / archive size.</param>
internal sealed record UnpackLimits(long MaxTotalBytes = 500L * 1024 * 1024, int MaxFiles = 10_000, int MaxCompressionRatio = 100)
{
    public static UnpackLimits Default { get; } = new();
}

/// <summary>
/// Hardened unpacking of a <c>.wpkg</c> (zip) or copy of an unpacked package folder into an empty staging folder
/// (spec IRT §4.3 step 1). Nothing is ever written outside the staging folder; on refusal the folder is deleted.
/// </summary>
internal static class PackageUnpacker
{
    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSymlink = 0xA000;

    /// <summary>Unpacks <paramref name="archive"/> into <paramref name="targetDir"/> (created; must not exist).</summary>
    /// <exception cref="IOException">The archive cannot be opened (e.g. still being written by another process).</exception>
    public static Result Unpack(string archive, string targetDir, UnpackLimits limits)
    {
        // FileShare.Read: a writer still holding the file makes this throw, and the caller retries later.
        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Unpack(file, targetDir, limits);
    }

    /// <summary>Unpacks the zip in <paramref name="archive"/> into <paramref name="targetDir"/> (created; must not exist).</summary>
    public static Result Unpack(Stream archive, string targetDir, UnpackLimits limits)
    {
        var root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);
        try
        {
            var result = UnpackCore(archive, root, limits);
            if (!result.IsSuccess)
            {
                TryDelete(root);
            }

            return result;
        }
        catch (InvalidDataException)
        {
            TryDelete(root);
            return IntegrationErrors.UnsafeArchive("unreadable");
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    /// <summary>Copies the unpacked package in <paramref name="sourceDir"/> into <paramref name="targetDir"/> with the same limits.</summary>
    /// <exception cref="IOException">A file cannot be read (e.g. still being written).</exception>
    public static Result CopyFolder(string sourceDir, string targetDir, UnpackLimits limits)
    {
        var source = Path.GetFullPath(sourceDir);
        var root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);
        try
        {
            var result = CopyFolderCore(source, root, limits);
            if (!result.IsSuccess)
            {
                TryDelete(root);
            }

            return result;
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    /// <summary>Checks a relative entry path: no absolute path, drive, <c>..</c>, backslash, control character or stream separator.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path[0] == '/' || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal) || path.AsSpan().ContainsAnyInRange('\0', '\u001f'))
        {
            return false;
        }

        foreach (var segment in path.TrimEnd('/').Split('/'))
        {
            // "." and ".." escape or alias; empty segments ("a//b") and trailing dots/spaces are rewritten by Windows.
            if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ')
            {
                return false;
            }
        }

        return true;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Result UnpackCore(Stream archive, string root, UnpackLimits limits)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > limits.MaxFiles)
        {
            return IntegrationErrors.UnsafeArchive("tooManyFiles");
        }

        // First pass on the declared metadata: nothing is written when the archive is refused here.
        long declared = 0;
        long compressed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!IsSafeRelativePath(name) || !IsInside(root, name))
            {
                return IntegrationErrors.UnsafeArchive("path", name);
            }

            var type = (entry.ExternalAttributes >> 16) & UnixTypeMask;
            if (type == UnixSymlink)
            {
                return IntegrationErrors.UnsafeArchive("symlink", name);
            }

            if (type is not (0 or UnixRegularFile or UnixDirectory))
            {
                return IntegrationErrors.UnsafeArchive("path", name);
            }

            if (!seen.Add(name.TrimEnd('/')))
            {
                return IntegrationErrors.UnsafeArchive("duplicate", name);
            }

            declared += entry.Length;
            compressed += entry.CompressedLength;
            if (declared > limits.MaxTotalBytes)
            {
                return IntegrationErrors.UnsafeArchive("tooLarge");
            }
        }

        if (declared > Math.Max(compressed, 1) * limits.MaxCompressionRatio)
        {
            return IntegrationErrors.UnsafeArchive("compressionRatio");
        }

        // Second pass: extract, counting the real bytes (declared sizes are not trusted).
        long written = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > limits.MaxTotalBytes)
                {
                    return IntegrationErrors.UnsafeArchive("tooLarge");
                }

                if (written > Math.Max(compressed, 1) * limits.MaxCompressionRatio)
                {
                    return IntegrationErrors.UnsafeArchive("compressionRatio");
                }

                output.Write(buffer, 0, read);
            }
        }

        return Result.Success();
    }

    private static Result CopyFolderCore(string source, string root, UnpackLimits limits)
    {
        long total = 0;
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(source, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return IntegrationErrors.UnsafeArchive("symlink", relative);
                }

                if (!IsSafeRelativePath(relative) || !IsInside(root, relative))
                {
                    return IntegrationErrors.UnsafeArchive("path", relative);
                }

                if (++files > limits.MaxFiles)
                {
                    return IntegrationErrors.UnsafeArchive("tooManyFiles");
                }

                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(destination);
                    pending.Push(entry.FullName);
                    continue;
                }

                total += ((FileInfo)entry).Length;
                if (total > limits.MaxTotalBytes)
                {
                    return IntegrationErrors.UnsafeArchive("tooLarge");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }

        return Result.Success();
    }

    /// <summary>Zip-slip guard: the resolved path of <paramref name="relative"/> stays strictly inside <paramref name="root"/>.</summary>
    private static bool IsInside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) && full.Length > prefix.Length;
    }
}
