using System.Text;

namespace GameWake.Providers.Common;

/// <summary>
/// Opens launcher files read-only with full sharing so GameWake never blocks a launcher
/// from writing, renaming or deleting its own files.
/// </summary>
public static class SharedFile
{
    public const FileShare LauncherFriendlyShare = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream OpenRead(string path, int bufferSize = 4096) =>
        new(path, FileMode.Open, FileAccess.Read, LauncherFriendlyShare, bufferSize, FileOptions.SequentialScan);

    public static string ReadAllText(string path, long maxBytes = 4 * 1024 * 1024)
    {
        using var stream = OpenRead(path);
        if (stream.Length > maxBytes)
        {
            throw new IOException($"{Path.GetFileName(path)} is larger than {maxBytes} bytes");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
