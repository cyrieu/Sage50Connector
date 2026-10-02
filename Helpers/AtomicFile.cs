using System;
using System.IO;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Writes a file by replacing it, so a crash mid-write cannot leave a
    /// half-written config or connection list behind.
    /// </summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A file path is required.", "path");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, contents ?? string.Empty);
            try
            {
                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { /* the replace already succeeded */ }
                }
            }
        }
    }
}
