#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Il2CppDumper
{
    internal sealed class ImageDeclarationExport
    {
        internal const string DirectoryName = "dump-by-image";
        internal const string IndexFileName = "index.json";
        internal const string IndexArtifact = DirectoryName + "/" + IndexFileName;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly string directory;
        private readonly List<ImageEntry> entries = new();

        internal ImageDeclarationExport(string outputDir) => directory = Path.Combine(outputDir, DirectoryName);

        internal ImageEntry AddImage(int id, string name, int typeStart, uint typeCount)
        {
            var entry = new ImageEntry(id, name, typeStart, typeCount);
            entries.Add(entry);
            return entry;
        }

        internal StagedFile OpenImage(ImageEntry image)
        {
            // The invariant ID remains unique even if names sanitize to the same value.
            var slug = new string(image.ImageName.Take(64).Select(c =>
                c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'
                    ? c : '_').ToArray()).Trim('.');
            var fileName = image.ImageId.ToString("D6", CultureInfo.InvariantCulture) +
                "-" + (slug.Length == 0 ? "image" : slug) + ".cs";
            return Open(fileName);
        }

        internal string WriteIndex(double metadataVersion)
        {
            using var index = Open(IndexFileName);
            index.Writer.Write(JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                MetadataVersion = metadataVersion,
                Images = entries
            }, JsonOptions));
            return index.Publish();
        }

        private StagedFile Open(string name)
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Declaration output directory '{DirectoryName}' is a symlink or junction.");
            }
            return new StagedFile(directory, name);
        }

        internal sealed class ImageEntry
        {
            internal ImageEntry(int imageId, string imageName, int typeStart, uint sourceTypeCount)
            {
                ImageId = imageId;
                ImageName = imageName;
                TypeStart = typeStart;
                SourceTypeCount = sourceTypeCount;
            }

            public int ImageId { get; }
            public string ImageName { get; }
            public int TypeStart { get; }
            public uint SourceTypeCount { get; }
            public int DeclarationCount { get; internal set; }
            public string? Path { get; internal set; }
            public string? Error { get; internal set; }
            public string Status => Error != null ? "failed" : SourceTypeCount == 0 ? "empty" : "written";
        }

        internal sealed class StagedFile : IDisposable
        {
            private readonly string target;
            private readonly string temporary;
            private bool closed;

            internal StagedFile(string directory, string name)
            {
                target = Path.Combine(directory, name);
                temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
                Writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false));
            }

            internal StreamWriter Writer { get; }

            internal string Publish()
            {
                Close();
                File.Move(temporary, target, overwrite: true);
                return target;
            }

            private void Close()
            {
                if (!closed)
                {
                    closed = true;
                    Writer.Dispose();
                }
            }

            public void Dispose()
            {
                try { Close(); }
                catch (IOException) { /* Failed images are discarded. Publish reports flush failures. */ }
                catch (UnauthorizedAccessException) { }
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Both outputs receive the same formatter calls without buffering a declaration or an image.
        internal sealed class TeeWriter : TextWriter
        {
            private TextWriter? first;
            private TextWriter? second;
            private readonly Action<Exception> firstFailure;
            private readonly Action<Exception> secondFailure;

            internal TeeWriter(TextWriter first, TextWriter second, Action<Exception> firstFailure, Action<Exception> secondFailure)
            {
                this.first = first;
                this.second = second;
                this.firstFailure = firstFailure;
                this.secondFailure = secondFailure;
            }

            public override Encoding Encoding => (first ?? second)?.Encoding ?? Encoding.UTF8;

            public override void Write(string? value)
            {
                WriteTo(ref first, firstFailure, value);
                WriteTo(ref second, secondFailure, value);
            }

            private static void WriteTo(ref TextWriter? output, Action<Exception> onFailure, string? value)
            {
                try
                {
                    output?.Write(value);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    output = null;
                    onFailure(e);
                }
            }
        }
    }
}
