using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameWake.Providers.Common;

public sealed record LogReadResult(IReadOnlyList<string> Lines, bool Restarted, bool FileMissing);

/// <summary>
/// Reads only the lines appended to a log since the previous call.
/// <para>
/// Rotation is detected by file identity (volume serial + file index), not by name or creation
/// time: Windows file-system tunnelling can give a re-created file the old creation time. When a
/// log is rotated by renaming, the unread tail of the old file is read from <c>rotatedPath</c>
/// first so no lines are lost. A trailing line without a newline is left for the next call.
/// </para>
/// </summary>
public sealed class IncrementalLogReader
{
    private const int MaxUnterminatedLineBytes = 1024 * 1024;

    private readonly string? _rotatedPath;
    private readonly long _maxInitialBytes;
    private FileIdentity? _identity;
    private long _position;
    private Encoding? _encoding;

    public IncrementalLogReader(string path, string? rotatedPath = null, long maxInitialBytes = 8 * 1024 * 1024)
    {
        Path = path;
        _rotatedPath = rotatedPath;
        _maxInitialBytes = maxInitialBytes;
    }

    public string Path { get; }

    public long Position => _position;

    public bool HasStarted => _identity is not null;

    public void Reset()
    {
        _identity = null;
        _position = 0;
        _encoding = null;
    }

    /// <summary>Starts reading from the current end of the file, so only lines written later are returned.</summary>
    public void SkipToEnd()
    {
        Reset();
        try
        {
            using var stream = SharedFile.OpenRead(Path, 4096);
            _identity = FileIdentity.TryGet(stream.SafeFileHandle) ?? new FileIdentity(0, 0);
            _encoding = DetectEncoding(stream);
            _position = stream.Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    public LogReadResult ReadNewLines()
    {
        var lines = new List<string>();
        var restarted = false;
        FileStream stream;
        try
        {
            stream = SharedFile.OpenRead(Path, 64 * 1024);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var wasStarted = _identity is not null;
            Reset();
            return new LogReadResult(lines, wasStarted, FileMissing: true);
        }

        using (stream)
        {
            var identity = FileIdentity.TryGet(stream.SafeFileHandle);
            var length = stream.Length;

            if (_identity is { } previous)
            {
                var replaced = identity is not null && identity != previous;
                if (replaced || length < _position)
                {
                    restarted = true;
                    if (replaced)
                    {
                        ReadRemainderOfRotatedFile(previous, _position, lines);
                    }

                    _position = 0;
                    _encoding = null;
                }
            }

            var skipPartialFirstLine = false;
            if (_identity is null || length - _position > _maxInitialBytes)
            {
                var start = Math.Max(0, length - _maxInitialBytes);
                skipPartialFirstLine = start > 0 && start != _position;
                restarted |= _identity is not null;
                _position = start;
            }

            _identity = identity ?? new FileIdentity(0, 0);
            _encoding ??= DetectEncoding(stream);
            _position = ReadLines(stream, _position, _encoding, skipPartialFirstLine, lines);
        }

        return new LogReadResult(lines, restarted, FileMissing: false);
    }

    private void ReadRemainderOfRotatedFile(FileIdentity previous, long position, List<string> lines)
    {
        if (_rotatedPath is null || _encoding is null)
        {
            return;
        }

        try
        {
            using var rotated = SharedFile.OpenRead(_rotatedPath, 64 * 1024);
            if (FileIdentity.TryGet(rotated.SafeFileHandle) == previous && rotated.Length > position)
            {
                ReadLines(rotated, position, _encoding, skipPartialFirstLine: false, lines, includeUnterminated: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Encoding DetectEncoding(FileStream stream)
    {
        Span<byte> bom = stackalloc byte[3];
        stream.Position = 0;
        var read = stream.Read(bom);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        return new UTF8Encoding(false);
    }

    private static long ReadLines(
        FileStream stream,
        long position,
        Encoding encoding,
        bool skipPartialFirstLine,
        List<string> lines,
        bool includeUnterminated = false)
    {
        var available = stream.Length - position;
        if (available <= 0)
        {
            return position;
        }

        var unicode = encoding is UnicodeEncoding;
        if (unicode && position % 2 != 0)
        {
            position++;
            available--;
        }

        var buffer = new byte[available];
        stream.Position = position;
        stream.ReadExactly(buffer);

        var end = LastNewlineEnd(buffer, unicode);
        if (end == 0)
        {
            if (!includeUnterminated && buffer.Length < MaxUnterminatedLineBytes)
            {
                return position;
            }

            end = buffer.Length;
        }
        else if (includeUnterminated)
        {
            end = buffer.Length;
        }

        var text = encoding.GetString(buffer, 0, end);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var parts = text.Split('\n');
        var start = skipPartialFirstLine ? 1 : 0;
        for (var i = start; i < parts.Length; i++)
        {
            var line = parts[i].TrimEnd('\r');
            if (i == parts.Length - 1 && line.Length == 0)
            {
                break;
            }

            lines.Add(line);
        }

        return position + end;
    }

    private static int LastNewlineEnd(byte[] buffer, bool unicode)
    {
        if (unicode)
        {
            for (var i = buffer.Length - 2; i >= 0; i -= 1)
            {
                if (i % 2 == 0 && buffer[i] == 0x0A && buffer[i + 1] == 0x00)
                {
                    return i + 2;
                }
            }

            return 0;
        }

        var index = Array.LastIndexOf(buffer, (byte)0x0A);
        return index < 0 ? 0 : index + 1;
    }

    private readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex)
    {
        public static FileIdentity? TryGet(SafeFileHandle handle)
        {
            if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
            {
                return null;
            }

            return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }
}
