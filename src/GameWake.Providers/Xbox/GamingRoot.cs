using System.Buffers.Binary;
using System.Text;

namespace GameWake.Providers.Xbox;

/// <summary>
/// The Xbox app writes <c>&lt;drive&gt;:\.GamingRoot</c> to mark its install folders. Observed format:
/// ASCII <c>RGBX</c>, a little-endian uint32 count, then that many null-terminated UTF-16LE
/// folder paths relative to the drive root (e.g. <c>XboxGames</c>).
/// </summary>
public static class GamingRoot
{
    private const int MaxFolders = 64;

    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> data, string driveRoot)
    {
        if (data.Length < 8 || data[0] != (byte)'R' || data[1] != (byte)'G' || data[2] != (byte)'B' || data[3] != (byte)'X')
        {
            throw new FormatException("missing RGBX header");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        if (count > MaxFolders)
        {
            throw new FormatException($"unexpected folder count {count}");
        }

        var folders = new List<string>((int)count);
        var offset = 8;
        for (var i = 0; i < count; i++)
        {
            var end = offset;
            while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            {
                end += 2;
            }

            if (end + 1 >= data.Length)
            {
                throw new FormatException("unterminated folder name");
            }

            var name = Encoding.Unicode.GetString(data[offset..end]);
            offset = end + 2;
            if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            folders.Add(Path.IsPathRooted(name) && !name.StartsWith('\\') ? name : Path.Combine(driveRoot, name.TrimStart('\\')));
        }

        return folders;
    }

    /// <summary>Finds Xbox install folders on fixed drives from their <c>.GamingRoot</c> files, plus the default <c>XboxGames</c> folders.</summary>
    public static IReadOnlyList<string> FindInstallFolders(ICollection<string>? problems = null)
    {
        var folders = new List<string>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems?.Add($"Could not list drives: {ex.Message}");
            return folders;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (File.Exists(marker))
                {
                    try
                    {
                        using var stream = Common.SharedFile.OpenRead(marker);
                        if (stream.Length <= 64 * 1024)
                        {
                            var buffer = new byte[stream.Length];
                            stream.ReadExactly(buffer);
                            folders.AddRange(Parse(buffer, drive.RootDirectory.FullName));
                        }
                    }
                    catch (FormatException ex)
                    {
                        problems?.Add($"{marker} has an unexpected format: {ex.Message}");
                    }
                }

                var fallback = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (Directory.Exists(fallback))
                {
                    folders.Add(fallback);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems?.Add($"Could not check drive {drive.Name}: {ex.Message}");
            }
        }

        return folders
            .Select(Path.TrimEndingDirectorySeparator)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
