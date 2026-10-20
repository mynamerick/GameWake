using System.Text;

namespace GameWake.Core.Infrastructure;

public static class AtomicFile
{
    /// <summary>Writes to a temporary file and swaps it in, so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
