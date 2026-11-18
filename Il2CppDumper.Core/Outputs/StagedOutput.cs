using System;
using System.IO;
using System.Text.Json;

namespace Il2CppDumper
{
    internal static class StagedOutput
    {
        internal static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
        {
            Write(path, stream => JsonSerializer.Serialize(stream, value, options));
        }

        internal static void Write(string path, Action<Stream> write)
        {
            var target = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(target);
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    write(stream);
                    stream.Dispose();
                    File.Move(temporary, target, overwrite: true);
                }
                catch
                {
                    // Preserve the original generation/publication failure if cleanup also fails.
                    try { stream.Dispose(); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    throw;
                }
            }
        }
    }
}
